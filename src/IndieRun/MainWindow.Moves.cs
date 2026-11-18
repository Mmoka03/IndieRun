using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace IndieRun;

public partial class MainWindow
{
    readonly Dictionary<string,(BackupJob Backup,MoveJob Move,MoveLocationEvidence Evidence)> moveLocationIssues=[];
    readonly HashSet<string> recentMoveIds=[];
    readonly Dictionary<string,(string Source,string Destination)> observedMoveLocations=[];
    void RememberObservedLocations(BackupJob backup,MoveJob move,Package parent)
    {
        var root=move.Status=="Completed"?move.Destination:move.Source;var previous=move.Status=="Completed"?move.Source:move.Destination;
        foreach(var game in packages.Where(p=>GameRegistration.IsAdded(p)&&(p.Id==parent.Id||!p.Source.Equals(root,StringComparison.OrdinalIgnoreCase)&&GameScanner.Within(p.Source,root))))
        {
            var relative=Path.GetRelativePath(root,game.Source);
            if(!backup.Package.Includes(Path.Combine(backup.SourceRoot,relative)))continue;
            observedMoveLocations[game.Id]=(relative=="."?previous:Path.Combine(previous,relative),game.Source);
        }
    }
    async Task SyncMovedPackagesAsync()
    {
        var wasRunning=running;var wasScanning=scanning;var sourceEnabled=SourceButton.IsEnabled;var rescanEnabled=RescanButton.IsEnabled;
        running=scanning=true;SourceButton.IsEnabled=RescanButton.IsEnabled=false;
        var known=packages.ToDictionary(p=>p.Id,p=>(p.Source,p.CompositionMoveState));
        try
        {
            var service=BackupService();var recover=!backupBusy;
            var inspected=await Task.Run(()=>
            {
                var moves=AllMoves(recover);if(moves.Any(m=>m.Status=="RecordError"))throw new IOException("이동 기록을 확인하지 못했습니다. 현재 게임 위치는 유지합니다.");
                var seen=new HashSet<string>();var results=new List<(BackupJob Backup,MoveJob Move,MoveLocationEvidence Evidence)>();
                foreach(var move in moves)
                {
                    var backup=MoveService(move).Load(move.BackupId);if(!seen.Add(backup.Package.Id))continue;
                    if(!known.TryGetValue(backup.Package.Id,out var current)||current.CompositionMoveState?.StartsWith("Reconnected:",StringComparison.Ordinal)==true||move.Status is not ("Completed" or "RolledBack"))continue;
                    if(!current.Source.Equals(move.Source,StringComparison.OrdinalIgnoreCase)&&!current.Source.Equals(move.Destination,StringComparison.OrdinalIgnoreCase))continue;
                    var evidence=MoveEvidence.Inspect(backup,move);
                    // Even an already-applied move can still need its library path saved
                    // after a crash. ApplyObservedMove records that verified transition.
                    results.Add((backup,move,evidence));
                }
                return results;
            });
            foreach(var item in inspected)ApplyObservedMove(item.Backup,item.Move,null,item.Evidence);
        }
        catch(Exception ex) when(LibraryFault(ex)){Say("이동 기록 확인 필요 · 현재 연결은 유지합니다.");Notice.ToolTip=ex.Message;}
        finally{running=wasRunning;scanning=wasScanning;SourceButton.IsEnabled=sourceEnabled;RescanButton.IsEnabled=rescanEnabled;}
    }
    bool ApplyMoveToWorkspace(BackupJob backup,MoveJob move)=>ApplyObservedMove(backup,move,null);
    static ScanSnapshot ObserveMoveFiles(MoveJob move,CancellationToken token)
    {
        var root=move.Status=="Completed"?move.Destination:move.Source;
        var actual=GameScanner.Scan(root,cancellation:token);if(actual.UnreadablePaths.Count>0||actual.Issues.Count>0)throw new IOException("이동 결과 폴더를 모두 확인하지 못했습니다.");actual.PrepareIndex(token);return actual;
    }
    bool ApplyObservedMove(BackupJob backup,MoveJob move,ScanSnapshot? observed,MoveLocationEvidence? inspectedEvidence=null)
    {
        var p=packages.Find(p=>p.Id==backup.Package.Id);if(p==null)return false;
        if(!p.Source.Equals(move.Source,StringComparison.OrdinalIgnoreCase)&&!p.Source.Equals(move.Destination,StringComparison.OrdinalIgnoreCase))return false;
        var evidence=inspectedEvidence??MoveEvidence.Inspect(backup,move);
        if(!evidence.MatchesRecord)
        {
            moveLocationIssues[p.Id]=(backup,move,evidence);p.Done=false;p.Planned=false;p.ReviewState="unverified";p.ReviewReason=evidence.Message;
            resultPages.Remove("organize");CandidateReviewSection.IsExpanded=true;
            if(operationStatus?.Name=="이동"&&operationStatus.Items.Any(i=>i.Id==p.Id&&i.State=="Completed"))operationStatus.Mark(p.Id,"LocationCheck",evidence.Message);return false;
        }
        moveLocationIssues.Remove(p.Id);
        var done=move.Status=="Completed";var root=done?move.Destination:move.Source;var changedLocation=!p.Source.Equals(root,StringComparison.OrdinalIgnoreCase);
        if(!changedLocation&&p.CompositionMoveState==move.Id+move.Status&&observed==null){RememberObservedLocations(backup,move,p);return true;}
        recentMoveIds.Add(p.Id);resultPages.Remove("organize");
        if(done&&changedLocation&&!searchLocations.Any(l=>GameScanner.Within(root,l.Path)))SearchLocationRules.Add(searchLocations,Path.GetDirectoryName(root)!);
        var executableRoot=Path.GetRelativePath(backup.SourceRoot,backup.Package.DetectedRoot);
        p.ScopeHistory.Clear();p.Source=root;p.SearchRoot=done?Path.GetDirectoryName(root)!:backup.Package.SearchRoot;
        p.DetectedRoot=executableRoot=="."?root:Path.Combine(root,executableRoot);
        p.Folder=Path.GetFileName(root);p.Name=done?p.Folder:backup.Package.Name;
        if(p.CompositionMoveState!=move.Id+move.Status)
        {
        p.CompositionMoveState=move.Id+move.Status;
        var restored=Clone(backup.Package);restored.RestoreAutomaticExclusions();
        p.OmittedFolders=done?[]:[..restored.OmittedFolders];p.CandidateOmissions.Clear();
        p.Contents=done?Clone(backup.Package.Contents.Where(c=>c.Role=="related").ToList()):Clone(backup.Package.Contents);
        }
        var oldRoot=done?move.Source:move.Destination;
        foreach(var child in packages.Where(q=>q.Id!=p.Id&&!q.Source.Equals(oldRoot,StringComparison.OrdinalIgnoreCase)&&GameScanner.Within(q.Source,oldRoot)&&backup.Package.Includes(Path.Combine(backup.SourceRoot,Path.GetRelativePath(oldRoot,q.Source)))).ToList())
        {
            var previous=child.Source;var next=Path.Combine(root,Path.GetRelativePath(oldRoot,previous));var detected=Path.GetRelativePath(previous,child.DetectedRoot);
            child.Source=next;child.DetectedRoot=Path.Combine(next,detected);child.SearchRoot=GameScanner.Within(next,p.SearchRoot)?p.SearchRoot:root;child.ScopeHistory.Clear();child.CompositionMoveState="Reconnected:"+Guid.NewGuid().ToString("N");child.RestoreAutomaticExclusions();
            foreach(var c in checkpoints.Where(c=>c.PackageId==child.Id))c.Source=next;
            if(observed!=null){checkpoints.RemoveAll(c=>c.PackageId==child.Id);checkpoints.Add(WorkspaceReconciler.Capture(child,observed));}
            foreach(var owner in packages)foreach(var link in owner.RelatedFolders.Where(r=>r.Id==child.Id))link.Source=next;
        }
        foreach(var owner in packages)foreach(var related in owner.RelatedFolders.Where(r=>r.Id==p.Id)){related.Source=root;related.Name=Path.GetFileName(root);}
        p.PortableMetadata=done||backup.Package.PortableMetadata;p.Backup=p.Backup||!move.WithoutBackup;p.Done=done;p.Planned=!done;p.ReviewState="";p.ReviewReason="";
        if(!done&&CompositionPlan.AnchorProblem(backup.Package) is {} anchorIssue){p.ReviewState="changed";p.ReviewReason=anchorIssue;}
        if(observed==null)
        {
            // Journal reconciliation cannot manufacture a current filesystem snapshot.
            snapshot=null;foreach(var game in packages.Where(g=>g.ReviewState.Length==0)){game.ReviewState="cached";game.ReviewReason="위치는 확인했습니다. 폴더 작업 전에 내용을 다시 확인하세요.";}
        }
        else if(snapshot!=null)
        {
            snapshot.InvalidateIndex();snapshot.Files.RemoveAll(f=>backup.Package.Includes(f.Path)||GameScanner.Within(f.Path,move.Destination)||GameScanner.Within(f.Path,root));
            snapshot.Directories.RemoveAll(d=>backup.Package.Includes(d)&&!backup.Package.OmittedFolders.Any(r=>GameScanner.Within(Path.Combine(move.Source,r),d))||GameScanner.Within(d,move.Destination)||GameScanner.Within(d,root));
            snapshot.Files.AddRange(observed.Files);snapshot.Directories.AddRange(observed.Directories);
            if(!snapshot.SearchRoots.Any(r=>GameScanner.Within(root,r)))snapshot.SearchRoots.Add(root);
        }
        else {snapshot=observed;snapshot.IsPartial=true;snapshot.SearchRoots.Add(root);}
        checkpoints.RemoveAll(c=>c.PackageId==p.Id);
        checkpoints.Add(observed!=null?WorkspaceReconciler.Capture(p,observed):new FolderCheckpoint{PackageId=p.Id,Source=root,Files=backup.Files.Select(f=>new FileStamp(f.RelativePath,f.Length,f.ModifiedTicks)).ToList(),Directories=backup.Directories.ToList()});
        RememberObservedLocations(backup,move,p);
        history.Clear();QueueWorkspaceSave();return true;
    }
    static string MoveStatus(string status)=>status switch {"Completed"=>"파일 이동 완료","RolledBack"=>"원래 위치로 되돌림","Interrupted"=>"중단됨 · 작업 선택 필요","RecoveryRequired"=>"복구 필요","Running"=>"이동 처리 중","RecordError"=>"기록 확인 필요",_=>status};
    async Task RunMoveJob(MoveJob move,bool undo)
    {
        await RunFileOperation(undo?(move.WithoutBackup?"원래 위치로 이동":"이동 복구"):"이동 재개",async(engine,progress,token)=>
        {
            var result=undo?await engine.UndoMoveAsync(move.BackupId,move.Id,progress,token):await engine.ResumeMoveAsync(move.BackupId,move.Id,progress,token);
            var observed=ObserveMoveFiles(result,token);
            await Dispatcher.InvokeAsync(()=>{if(!ApplyObservedMove(engine.Load(move.BackupId),result,observed))throw new IOException("이동 기록과 실제 위치가 일치하지 않습니다.");if(!FlushWorkspace())operationStatus?.Mark("single","SavePending","파일 처리는 끝났지만 설정을 저장하지 못했습니다.");});
        },null,Path.GetFileName(move.Destination),MoveService(move),move.WithoutBackup);
    }
    Task StartMoveBatchAsync(List<Package> list,string? targetRoot=null)=>ExecuteMoveListAsync(new MoveSelection(list,packages).Items(targetRoot??destination),targetRoot);
    Task ExecuteMoveListAsync(List<Package> list,string? targetRoot=null)=>ExecuteMoveListWithModeAsync(list,targetRoot,false);
    bool movePreparing;
    async Task ExecuteMoveListWithModeAsync(List<Package> list,string? targetRoot,bool withoutBackup)
    {
        if(backupBusy||scanning||movePreparing||list.Count==0)return;
        if(!intakeInProgress&&!await VerifyCurrentSelectionAsync(list,false))return;
        movePreparing=true;var priorRunning=running;running=true;RenderPlan();
        try
        {
        if(list.Any(p=>p.ReviewState.Length>0)){Info("원본 확인","변경되었거나 찾을 수 없는 패키지를 먼저 확인해 주세요.");return;}
        var service=withoutBackup?DirectMoveService():BackupService();
        try
        {
            if(await Task.Run(()=>AllMoves(true).Any(m=>m.Status is "Interrupted" or "RecoveryRequired" or "Running" or "RecordError")))throw new IOException("작업 탭에서 이전 이동을 먼저 복구하거나 완료해 주세요.");
            foreach(var content in list.Where(p=>p.IsRelatedContent))if(LibraryStore.ReadIdentity(content.Source) is {} marker&&marker.GameId!=content.Id)throw new IOException("관련 자료의 식별 정보가 다릅니다. 자료 위치를 먼저 확인하세요.");
            foreach(var item in list){RequireUserGameFolder(item.Source);RequireUserGameFolder(CompositionPlan.Target(item,targetRoot??destination));}
            foreach(var item in list)if(packages.Any(other=>GameRegistration.IsAdded(other)&&FolderPolicy.Overlap(item,other,item.Source)&&!(IsAdded(item)&&!other.Source.Equals(item.Source,StringComparison.OrdinalIgnoreCase)&&item.Includes(other.Source))))throw new IOException("다른 등록 항목이 이동 범위에 포함되어 있습니다. 게임 구성을 먼저 확인하세요.");
            foreach(var item in list){var anchor=CompositionPlan.AnchorProblem(item,PlayFor(item));if(anchor!=null)throw new IOException(anchor);}
            if(withoutBackup&&list.Any(p=>GameScanner.Within(CompositionPlan.Target(p,targetRoot??destination),BackupService().Root)||GameScanner.Within(BackupService().Root,CompositionPlan.Target(p,targetRoot??destination))))throw new IOException("게임 도착 위치와 백업 폴더는 서로 분리해야 합니다.");
            CompositionPlan.Preflight(service,list,targetRoot??destination);
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException){ReportMoveProblem("이동 전 확인","이동 준비 중 문제가 발생했습니다.",ex.Message);return;}
        if(!FlushWorkspace()){ShowWorkspaceSaveIssue();return;}
        foreach(var p in list)
        {
            if(p.IsRelatedContent&&snapshot!=null&&!snapshot.Directories.Contains(p.Source,StringComparer.OrdinalIgnoreCase))
            {
                try{var scan=await Task.Run(()=>GameScanner.Scan(p.Source));if(scan.Issues.Count>0)throw new IOException("자료 폴더를 모두 읽지 못했습니다.");snapshot.InvalidateIndex();snapshot.Files.AddRange(scan.Files);snapshot.Directories.AddRange(scan.Directories);}catch(Exception ex)when(LibraryFault(ex)){ReportMoveProblem("자료 위치 확인","자료 위치와 접근 권한을 확인하세요.",ex.Message);return;}
            }
            var canonical=packages.Find(q=>GameRegistration.SameEntry(q,p));
            var registered=canonical??p;if(!RegisterGame(registered))return;p.Id=registered.Id;p.Registered=registered.Registered;p.LocalRegistration=registered.LocalRegistration;var live=packages.Find(q=>q.Id==p.Id);if(live!=null){live.Registered=p.Registered;live.LocalRegistration=p.LocalRegistration;}else if(p.IsRelatedContent){packages.Add(Clone(p));if(snapshot!=null)checkpoints.Add(WorkspaceReconciler.Capture(p,snapshot));}}
        if(!FlushWorkspace()){ShowWorkspaceSaveIssue();return;}
        if(playerRoot!=destination)LoadPlayer();
        try{foreach(var p in list)PortableStore.Export(Path.GetDirectoryName(CompositionPlan.Target(p,targetRoot??destination))!,p.Name,p,PlayFor(p),catalog,destination);}
        catch(Exception ex) when(LibraryFault(ex)){ReportMoveProblem("게임 정보 보관","별칭·태그·실행 설정을 함께 보관하지 못해 이동을 시작하지 않았습니다. 도착 위치의 정보 파일과 권한을 확인하세요.",ex.Message);return;}
        var captured=JsonSerializer.Deserialize<List<Package>>(JsonSerializer.Serialize(list))!;
        var acceptedChecks=Clone(checkpoints);foreach(var p in list)if(!acceptedChecks.Any(c=>c.PackageId==p.Id)&&verifiedTransferChecks.TryGetValue(p.Id,out var acceptedCheck))acceptedChecks.Add(Clone(acceptedCheck));
        var tags=JsonSerializer.Deserialize<List<TagDefinition>>(JsonSerializer.Serialize(catalog.Tags))!;
        var target=targetRoot??destination;var batchId=Guid.NewGuid().ToString("N");
        await RunFileOperation("이동",async(engine,progress,token)=>
        {
            var completed=new List<MoveJob>();
            for(int i=0;i<captured.Count;i++)
            {
                var p=captured[i];
                await BeginOperationItem(p,withoutBackup?"원본 확인 중":"백업 준비 중");
                try
                {
                    token.ThrowIfCancellationRequested();
                    var accepted=acceptedChecks.Find(c=>c.PackageId==p.Id);if(accepted==null)throw new IOException("이동 전 확인 기록이 없습니다. 다시 검색한 뒤 이동하세요.");
                    var beforeCapture=GameScanner.Scan(p.Source,cancellation:token);var probe=Clone(p);WorkspaceReconciler.Check(probe,accepted,beforeCapture);if(probe.ReviewState.Length>0)throw new IOException("확인 후 폴더 내용이 달라졌거나 읽을 수 없습니다. 다시 검색한 뒤 이동하세요.");
                    var backup=withoutBackup?await engine.PrepareDirectMoveAsync(p,tags,progress,token):await engine.CreateAsync(p,tags,progress,token,reason:"move");
                    await BeginOperationItem(p,"이동 준비 중");
                    var moved=await engine.MoveAsync(backup.Id,CompositionPlan.Target(p,target),batchId,progress,token);completed.Add(moved);
                    var observed=ObserveMoveFiles(moved,token);
                    var saved=await Dispatcher.InvokeAsync(()=>{if(!ApplyObservedMove(backup,moved,observed))throw new IOException("실제 이동 위치를 확인하지 못해 완료로 처리하지 않았습니다.");var persisted=FlushWorkspace();RenderBackupJobs();return persisted;});
                    if(!saved){await MarkOperationItem(p.Id,"SavePending","파일은 이동했지만 설정을 저장하지 못했습니다. 저장 문제를 해결한 뒤 다시 확인하세요.");await Dispatcher.InvokeAsync(()=>stopPending=true);return;}
                    await MarkOperationItem(p.Id,"Completed");
                }
                catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
                {
                    await MarkOperationItem(p.Id,ex is OperationCanceledException?"Interrupted":"Failed",ex.Message);
                    var pending=engine.ListMoves(false).Any(m=>m.Status is "RecoveryRequired" or "Running" or "Interrupted");
                    var choice=await Dispatcher.InvokeAsync(()=>backupCloseRequested?"stop":ChooseMoveFailure(ex.Message,completed.Count,i+1<captured.Count&&!pending&&ex is not OperationCanceledException,withoutBackup));
                    if(choice=="undo")
                    {
                        foreach(var previous in completed.AsEnumerable().Reverse())
                        {
                            var prior=engine.Load(previous.BackupId).Package;
                            await BeginOperationItem(prior,"원래 위치로 되돌리기 중");
                            MoveJob undone;
                            try{undone=await engine.UndoMoveAsync(previous.BackupId,previous.Id,progress,CancellationToken.None);}
                            catch(Exception recovery) when(recovery is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
                            {await MarkOperationItem(prior.Id,"Failed",recovery.Message);throw;}
                            var observed=ObserveMoveFiles(undone,CancellationToken.None);
                            var saved=await Dispatcher.InvokeAsync(()=>{if(!ApplyObservedMove(engine.Load(previous.BackupId),undone,observed))throw new IOException("복구 기록과 실제 위치가 일치하지 않습니다.");return FlushWorkspace();});
                            await MarkOperationItem(prior.Id,saved?"RolledBack":"SavePending",saved?null:"파일은 복구했지만 설정을 저장하지 못했습니다.");
                        }
                    }
                    if(choice=="continue")continue;
                    await Dispatcher.InvokeAsync(()=>stopPending=true);
                    throw new IOException(pending?"이동이 중단됐습니다. 작업 탭에서 복구가 필요한 패키지를 확인해 주세요.":withoutBackup?"이동을 중단했습니다. 완료한 이동은 선택에 따라 처리했습니다. 별도 백업은 없습니다.":"이동을 중단했습니다. 완료된 작업과 백업은 선택에 따라 보관했습니다.",ex);
                }
            }
        },captured,null,service,withoutBackup);
        }
        finally{movePreparing=false;running=priorRunning;RenderPlan();}
    }
    string MoveFailureChoice(string error,int completed,bool canContinue)=>ChooseMoveFailure(error,completed,canContinue,false);
    string ChooseMoveFailure(string error,int completed,bool canContinue,bool withoutBackup)
    {
        var choices=new List<DialogChoice>();
        if(canContinue)choices.Add(new("continue","실패한 패키지는 건너뛰고 계속"));
        choices.Add(new("stop",completed>0?"이동 중단 · 완료한 이동 유지":"이동 중단",true));
        if(completed>0)choices.Add(new("undo","이동 중단 · 완료한 이동도 되돌리기"));
        return Choose("이동을 완료하지 못했습니다",$"완료한 패키지 {completed}개 · "+(withoutBackup?"별도 백업 없음":"백업은 유지됩니다."),choices.ToArray(),"stop",error,DialogTone.Warning,stacked:true);
    }
}


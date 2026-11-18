using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace IndieRun;

public partial class MainWindow
{
    void SyncMovedPackages()
    {
        var service=BackupService();
        try
        {
            var seen=new HashSet<string>();
            foreach(var move in service.ListMoves(!backupBusy).Where(m=>m.Status is "Completed" or "RolledBack"))
            {
                var backup=service.Load(move.BackupId);if(!seen.Add(backup.Package.Id))continue;
                ApplyMoveToWorkspace(backup,move);
            }
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException){Say("이동 기록 확인 필요");Notice.ToolTip=ex.Message;}
    }
    void ApplyMoveToWorkspace(BackupJob backup,MoveJob move)
    {
        var p=packages.Find(p=>p.Id==backup.Package.Id);if(p==null)return;
        if(!p.Source.Equals(move.Source,StringComparison.OrdinalIgnoreCase)&&!p.Source.Equals(move.Destination,StringComparison.OrdinalIgnoreCase))return;
        var done=move.Status=="Completed";var root=done?move.Destination:move.Source;var changedLocation=!p.Source.Equals(root,StringComparison.OrdinalIgnoreCase);
        if(!changedLocation&&p.CompositionMoveState==move.Id+move.Status)return;
        if(done&&changedLocation&&!searchLocations.Any(l=>GameScanner.Within(root,l.Path)))SearchLocationRules.Add(searchLocations,Path.GetDirectoryName(root)!);
        var executableRoot=Path.GetRelativePath(backup.SourceRoot,backup.Package.DetectedRoot);
        p.ScopeHistory.Clear();p.Source=root;p.SearchRoot=done?Path.GetDirectoryName(root)!:backup.Package.SearchRoot;
        p.DetectedRoot=executableRoot=="."?root:Path.Combine(root,executableRoot);
        p.Folder=Path.GetFileName(root);p.Name=done?p.Folder:backup.Package.Name;
        if(p.CompositionMoveState!=move.Id+move.Status)
        {
        p.CompositionMoveState=move.Id+move.Status;
        p.OmittedFolders=done?[]:[..backup.Package.OmittedFolders];
        p.Contents=done?Clone(backup.Package.Contents.Where(c=>c.Role=="related").ToList()):Clone(backup.Package.Contents);
        }
        foreach(var owner in packages)foreach(var related in owner.RelatedFolders.Where(r=>r.Id==p.Id)){related.Source=root;related.Name=Path.GetFileName(root);}
        p.PortableMetadata=done||backup.Package.PortableMetadata;p.Backup=true;p.Done=done;p.Planned=!done;p.ReviewState="";p.ReviewReason="";
        if(snapshot!=null)
        {
            snapshot.InvalidateIndex();snapshot.Files.RemoveAll(f=>backup.Package.Includes(f.Path)||GameScanner.Within(f.Path,move.Destination));
            snapshot.Directories.RemoveAll(d=>backup.Package.Includes(d)&&!backup.Package.OmittedFolders.Any(r=>GameScanner.Within(Path.Combine(move.Source,r),d))||GameScanner.Within(d,move.Destination));
            snapshot.Directories.Add(root);snapshot.Directories.AddRange(backup.Directories.Select(d=>Path.Combine(root,d)));
            snapshot.Files.AddRange(backup.Files.Select(f=>new ScannedFile(Path.Combine(root,f.RelativePath),f.Length,new DateTime(f.ModifiedTicks,DateTimeKind.Utc))));
        }
        checkpoints.RemoveAll(c=>c.PackageId==p.Id);
        checkpoints.Add(new FolderCheckpoint {PackageId=p.Id,Source=root,Files=backup.Files.Select(f=>new FileStamp(f.RelativePath,f.Length,f.ModifiedTicks)).ToList(),Directories=backup.Directories.ToList()});
        history.Clear();QueueWorkspaceSave();
    }
    int RenderMoveJobs(BackupEngine service,List<BackupJob> backups,Panel target,bool attention)
    {
        List<MoveJob> moves;
        try{moves=service.ListMoves(!backupBusy);}catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException){target.Children.Add(WorkText("이동 기록을 읽지 못했습니다.",13,"Warning.Text"));target.Children.Add(WorkDetails(ex.Message));return 0;}
        moves=moves.Where(m=>(m.Status is not ("Completed" or "RolledBack"))==attention).ToList();
        if(moves.Count==0)return 0;
        target.Children.Add(new TextBlock {Text=attention?"확인할 이동":"이동 기록",FontSize=19,Margin=new Thickness(0,0,0,14)});
        foreach(var move in moves)
        {
            var saved=backups.Find(j=>j.Id==move.BackupId)?.Package;var game=saved==null?null:packages.Find(p=>p.Id==saved.Id)??saved;
            var row=new StackPanel();row.Children.Add(WorkText(game==null?Path.GetFileName(move.Destination):PlayerRules.Title(game),17,"Text.Primary"));
            row.Children.Add(WorkText(MoveStatus(move.Status),13,move.Status is "Completed" or "RolledBack"?"Success.Text":"Warning.Text"));
            if(move.Status is "Interrupted" or "RecoveryRequired")row.Children.Add(WorkText("재개하거나 원래 위치로 복구하세요. 백업은 유지됩니다.",12));
            row.Children.Add(WorkDetails("원본: "+move.Source+"\n도착: "+move.Destination+(move.Error==null?"":"\n\n"+move.Error)));
            var buttons=new WrapPanel {Margin=new Thickness(0,12,0,0)};
            if(move.Status is "Interrupted" or "RecoveryRequired")
            {
                if(move.Action=="Move")buttons.Children.Add(Btn("이동 재개",async()=>await RunMoveJob(move,false)));
                buttons.Children.Add(Btn("원래 위치로 복구",async()=>await RunMoveJob(move,true)));
            }
            if(move.Status=="Completed")
            {
                buttons.Children.Add(Btn("이동 폴더",()=>OpenSourceFolder(move.Destination)));
                if(backups.Find(j=>j.Id==move.BackupId) is {} savedBackup&&!BackupEngine.Removed(savedBackup))buttons.Children.Add(Btn("이동 되돌리기",async()=>
                {
                    if(!Confirm("이동 되돌리기","게임을 종료한 뒤 진행하세요. 패키지를 원래 위치로 되돌립니다.","원래 위치로 이동",move.Source))return;
                    await RunMoveJob(move,true);
                }));
            }
            foreach(Button button in buttons.Children){button.IsEnabled=!backupBusy;button.Margin=new Thickness(0,0,8,6);}
            row.Children.Add(buttons);target.Children.Add(WorkCard(row));
        }
        return moves.Count;
    }
    static string MoveStatus(string status)=>status switch {"Completed"=>"이동 완료","RolledBack"=>"원래 위치로 복구 완료","Interrupted"=>"중단됨 · 작업 선택 필요","RecoveryRequired"=>"복구 필요","Running"=>"이동 처리 중","RecordError"=>"기록 확인 필요",_=>status};
    async Task RunMoveJob(MoveJob move,bool undo)
    {
        await RunBackupOperation(undo?"이동 복구":"이동 재개",async(engine,progress,token)=>
        {
            var result=undo?await engine.UndoMoveAsync(move.BackupId,move.Id,progress,token):await engine.ResumeMoveAsync(move.BackupId,move.Id,progress,token);
            await Dispatcher.InvokeAsync(()=>ApplyMoveToWorkspace(engine.Load(move.BackupId),result));
        },subject:Path.GetFileName(move.Destination));
    }
    Task StartMoveBatchAsync(List<Package> list,string? targetRoot=null)=>ExecuteMoveListAsync(new MoveSelection(list,packages).Items(targetRoot??destination),targetRoot);
    async Task ExecuteMoveListAsync(List<Package> list,string? targetRoot=null)
    {
        if(backupBusy||scanning||list.Count==0)return;
        if(list.Any(p=>p.ReviewState.Length>0)){Info("원본 확인","변경되었거나 찾을 수 없는 패키지를 먼저 확인해 주세요.");return;}
        var service=BackupService();
        try
        {
            if(service.ListMoves().Any(m=>m.Status is "Interrupted" or "RecoveryRequired" or "Running" or "RecordError"))throw new IOException("작업 탭에서 이전 이동을 먼저 복구하거나 완료해 주세요.");
            foreach(var content in list.Where(p=>p.IsRelatedContent))if(LibraryStore.ReadIdentity(content.Source) is {} marker&&marker.GameId!=content.Id)throw new IOException("관련 자료의 식별 정보가 다릅니다. 자료 위치를 먼저 확인하세요.");
            CompositionPlan.Preflight(service,list,targetRoot??destination);
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException){ErrorInfo("이동 전 확인","이동 준비 중 문제가 발생했습니다.",ex.Message);return;}
        if(!FlushWorkspace()){Info("계획 저장","정리 계획을 먼저 저장해 주세요.");return;}
        foreach(var p in list)
        {
            if(p.IsRelatedContent&&snapshot!=null&&!snapshot.Directories.Contains(p.Source,StringComparer.OrdinalIgnoreCase))
            {
                try{var scan=await Task.Run(()=>GameScanner.Scan(p.Source));if(scan.Issues.Count>0)throw new IOException("자료 폴더를 모두 읽지 못했습니다.");snapshot.InvalidateIndex();snapshot.Files.AddRange(scan.Files);snapshot.Directories.AddRange(scan.Directories);}catch(Exception ex)when(LibraryFault(ex)){ErrorInfo("자료 위치 확인","자료 위치와 접근 권한을 확인하세요.",ex.Message);return;}
            }
            if(!RegisterGame(p))return;var live=packages.Find(q=>q.Id==p.Id);if(live!=null){live.Registered=p.Registered;live.LocalRegistration=p.LocalRegistration;}else if(p.IsRelatedContent){packages.Add(Clone(p));if(snapshot!=null)checkpoints.Add(WorkspaceReconciler.Capture(p,snapshot));}}
        if(!FlushWorkspace())return;
        if(playerRoot!=destination)LoadPlayer();
        try{foreach(var p in list)PortableStore.Export(Path.GetDirectoryName(CompositionPlan.Target(p,targetRoot??destination))!,p.Name,p,PlayFor(p),catalog,destination);}
        catch(Exception ex) when(LibraryFault(ex)){ErrorInfo("게임 정보 보관","별칭·태그·실행 설정을 함께 보관하지 못해 이동을 시작하지 않았습니다. 도착 위치의 정보 파일과 권한을 확인하세요.",ex.Message);return;}
        var captured=JsonSerializer.Deserialize<List<Package>>(JsonSerializer.Serialize(list))!;
        var tags=JsonSerializer.Deserialize<List<TagDefinition>>(JsonSerializer.Serialize(catalog.Tags))!;
        var target=targetRoot??destination;var batchId=Guid.NewGuid().ToString("N");
        await RunBackupOperation("이동",async(engine,progress,token)=>
        {
            var completed=new List<MoveJob>();
            for(int i=0;i<captured.Count;i++)
            {
                var p=captured[i];
                await BeginOperationItem(p,"백업 준비 중");
                try
                {
                    token.ThrowIfCancellationRequested();
                    var backup=await engine.CreateAsync(p,tags,progress,token,reason:"move");
                    await BeginOperationItem(p,"이동 준비 중");
                    var moved=await engine.MoveAsync(backup.Id,CompositionPlan.Target(p,target),batchId,progress,token);completed.Add(moved);
                    await MarkOperationItem(p.Id,"Completed");
                    await Dispatcher.InvokeAsync(()=>{ApplyMoveToWorkspace(backup,moved);FlushWorkspace();RenderBackupJobs();});
                }
                catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
                {
                    await MarkOperationItem(p.Id,ex is OperationCanceledException?"Interrupted":"Failed",ex.Message);
                    var pending=engine.ListMoves(false).Any(m=>m.Status is "RecoveryRequired" or "Running" or "Interrupted");
                    var choice=await Dispatcher.InvokeAsync(()=>backupCloseRequested?"stop":MoveFailureChoice(ex.Message,completed.Count,i+1<captured.Count&&!pending&&ex is not OperationCanceledException));
                    if(choice=="undo")
                    {
                        foreach(var previous in completed.AsEnumerable().Reverse())
                        {
                            var prior=engine.Load(previous.BackupId).Package;
                            await BeginOperationItem(prior,"원래 위치로 복구 중");
                            MoveJob undone;
                            try{undone=await engine.UndoMoveAsync(previous.BackupId,previous.Id,progress,CancellationToken.None);}
                            catch(Exception recovery) when(recovery is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
                            {await MarkOperationItem(prior.Id,"Failed",recovery.Message);throw;}
                            await MarkOperationItem(prior.Id,"RolledBack");
                            await Dispatcher.InvokeAsync(()=>{ApplyMoveToWorkspace(engine.Load(previous.BackupId),undone);FlushWorkspace();});
                        }
                    }
                    if(choice=="continue")continue;
                    await Dispatcher.InvokeAsync(()=>stopPending=true);
                    throw new IOException(pending?"이동이 중단됐습니다. 작업 탭에서 복구가 필요한 패키지를 확인해 주세요.":"이동을 중단했습니다. 완료된 작업과 백업은 선택에 따라 보관했습니다.",ex);
                }
            }
        },captured);
    }
    string MoveFailureChoice(string error,int completed,bool canContinue)
    {
        var choices=new List<DialogChoice>();
        if(canContinue)choices.Add(new("continue","실패한 패키지는 건너뛰고 계속"));
        choices.Add(new("stop",completed>0?"이동 중단 · 완료한 이동 유지":"이동 중단",true));
        if(completed>0)choices.Add(new("undo","이동 중단 · 완료한 이동도 되돌리기"));
        return Choose("이동을 완료하지 못했습니다",$"완료한 패키지 {completed}개 · 백업은 유지됩니다.",choices.ToArray(),"stop",error,DialogTone.Warning,stacked:true);
    }
}


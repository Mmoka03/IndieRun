using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace IndieRun;

public partial class MainWindow
{
    readonly WorkspaceStore workspaceStore=new(Path.Combine(AppContext.BaseDirectory,"data","workspace.json"));
    List<FolderCheckpoint> checkpoints=[];
    DispatcherTimer saveTimer=null!;
    FileStream? workspaceLock;
    bool sessionReady;
    bool workspaceBlocked;
    bool closingWithoutSave;
    bool aliasEditing,physicalEditing;
    string? lastSavedSignature;
    bool HasSelection => selected!=null&&!selected.Excluded&&packages.Contains(selected);

    void InitializeWorkspace()
    {
        saveTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(450)};
        saveTimer.Tick+=(_,_)=>{saveTimer.Stop();FlushWorkspace();};
        Closing+=WorkspaceClosing;
        Closing+=BackupClosing;
        Closed+=(_,_)=>{saveTimer.Stop();workspaceLock?.Dispose();};
    }
    async Task StartWorkspaceAsync()
    {
        try{workspaceLock=workspaceStore.AcquireLock();}
        catch(IOException){Info("이미 실행 중","같은 보관 데이터의 프로그램이 이미 실행 중입니다.");closingWithoutSave=true;Close();return;}
        catch(UnauthorizedAccessException){workspaceBlocked=true;SaveStatus.Content="저장 권한 확인";}
        try{if(BatchRenames().HasPending){await Task.Run(()=>BatchRenames().ResolveActive());workspaceBlocked=true;}else Renames().Recover();ScopeChanges().Recover();if(Edits().Recover())LoadTags();}catch(Exception ex) when(LibraryFault(ex)){workspaceBlocked=true;ErrorInfo("편집 저장 복구","이전 편집 내용을 복구하지 못했습니다.",ex.Message);}
        var loaded=await Task.Run(()=>workspaceStore.Load());
        if(loaded.Document!=null)
        {
            lastSavedSignature=loaded.Recovered?null:await Task.Run(()=>StateSignature(loaded.Document));
            ApplyWorkspace(loaded.Document);ResolveTagBindings(loaded.Document);await RefreshHistoryAsync();await SyncMovedPackagesAsync();
            try{libraryDocument=await Task.Run(()=>Library().Load());}catch(Exception ex) when(LibraryFault(ex)){libraryError=ex.Message;}
            foreach(var p in packages.Where(p=>!p.Excluded&&p.ReviewState.Length==0)){p.ReviewState="cached";p.ReviewReason="저장된 목록입니다. 폴더 작업 전에 다시 검색하세요.";}
            snapshot=null;sessionReady=true;Render();saveTimer.Stop();Say("저장된 목록을 열었습니다. 탐색 위치를 바꾸거나 다시 검색할 수 있습니다.");
            await RecoverBackupsAsync();
            if(loaded.Recovered)Say("직전 정상 저장본에서 계획을 복원했습니다");
            return;
        }
        if(loaded.Error!=null){workspaceBlocked=true;SaveStatus.Content="저장 파일 확인 필요";SaveStatus.ToolTip=loaded.Error;}
        await ScanDefaultAsync();
        await RecoverBackupsAsync();
        if(loaded.Error!=null)Say("저장 파일을 읽지 못했습니다 · 원본은 보존했습니다");
    }
    WorkspaceDocument CaptureWorkspace()
    {
        foreach(var p in packages)
        {
            p.GenreIds=catalog.Tags.Where(t=>t.Kind=="genre"&&p.Genres.Contains(t.Name)).Select(t=>t.Id).ToList();
            p.CategoryIds=catalog.Tags.Where(t=>t.Kind=="category"&&p.Categories.Contains(t.Name)).Select(t=>t.Id).ToList();
        }
        return new WorkspaceDocument {IdentityNoticeAccepted=identityNoticeAccepted,SavedUtc=DateTime.UtcNow,SearchRoot=source,SearchLocations=Clone(searchLocations),Destination=destination,BackupRoot=backupStorageRoot,SelectedId=HasSelection?selected.Id:"",Filter=Search.Text,Packages=packages,Checkpoints=checkpoints,Tags=catalog.Tags};
    }
    void ApplyWorkspace(WorkspaceDocument doc)
    {
        FolderPolicy.RestoreAutomaticExclusions(doc.Packages);source=doc.SearchRoot;searchLocations=doc.SearchLocations??[new SearchLocation {Path=doc.SearchRoot}];destination=doc.Destination;packages=doc.Packages;checkpoints=doc.Checkpoints;
        identityNoticeAccepted=doc.IdentityNoticeAccepted;foreach(var p in packages)p.Registered??=true;
        if(!string.IsNullOrWhiteSpace(doc.BackupRoot))backupStorageRoot=doc.BackupRoot;
        selected=packages.Find(p=>p.Id==doc.SelectedId&&!p.Excluded)??packages.FirstOrDefault(p=>!p.Excluded)??new Package();
        rendering=true;Search.Text=doc.Filter;rendering=false;
        DestinationLabel.Text=destination;UpdateSearchLocationLabel();
        history.Clear();
    }
    void ResolveTagBindings(WorkspaceDocument doc)
    {
        var tagsFile=Path.Combine(AppContext.BaseDirectory,"data","tags.json");
        if(tagLoadError.Length>0||!File.Exists(tagsFile))
        {
            catalog=new TagCatalog {Tags=doc.Tags};
            if(tagLoadError.Length==0)try{tagStore.Save(catalog);}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){tagLoadError=ex.Message;}
        }
        foreach(var p in packages)
        {
            p.Genres=catalog.Tags.Where(t=>t.Kind=="genre"&&p.GenreIds.Contains(t.Id)).Select(t=>t.Name).ToList();
            p.Categories=catalog.Tags.Where(t=>t.Kind=="category"&&p.CategoryIds.Contains(t.Id)).Select(t=>t.Name).ToList();
        }
    }
    void QueueWorkspaceSave()
    {
        if(File.Exists(Edits().Pending)||(ScopeChanges().HasPending||RenamePending)){SaveStatus.Content="편집 저장 복구 필요";return;}
        if(!sessionReady||scanning||workspaceBlocked||workspaceLock==null)return;
        SaveStatus.Content="저장 중…";saveTimer.Stop();saveTimer.Start();
    }
    bool FlushWorkspace()
    {
        if(File.Exists(Edits().Pending)||(ScopeChanges().HasPending||RenamePending)){SaveStatus.Content="편집 저장 복구 필요";return false;}
        if(!sessionReady)return true;
        if(workspaceBlocked||workspaceLock==null)return false;
        try
        {
            var document=CaptureWorkspace();var signature=StateSignature(document);
            if(signature!=lastSavedSignature){workspaceStore.Save(document);lastSavedSignature=signature;}
            workspaceSaveIssue=null;saveIssueDetail="";SaveStatus.Content="저장됨 ✓";SaveStatus.ToolTip=$"{DateTime.Now:HH:mm:ss} · {workspaceStore.FilePath}";SaveLibraryMetadata();SyncPortableMetadata();return true;
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {SaveIssue(WorkspaceSaveIssue.From(ex),ex.Message);return false;}
    }
    static string StateSignature(WorkspaceDocument document)
    {
        var stamp=document.SavedUtc;document.SavedUtc=default;
        var signature=JsonSerializer.Serialize(document);document.SavedUtc=stamp;return signature;
    }
    void WorkspaceClosing(object? sender,CancelEventArgs e)
    {
        if(!LeaveScopeEdit()){e.Cancel=true;return;}
        saveTimer.Stop();scanCancellation?.Cancel();
        if(!closingWithoutSave&&!FlushWorkspace()&&Choose("계획 저장 실패","최근 변경 내용을 저장하지 못했습니다. 종료하면 저장되지 않은 내용이 사라집니다.",[new("stay","돌아가기",true),new("exit","저장하지 않고 종료")],"stay",SaveStatus.ToolTip?.ToString(),DialogTone.Warning)!="exit")e.Cancel=true;
    }
    async Task<ScanSnapshot> ReadWorkspaceFolders(string root,IEnumerable<Package> previous,CancellationToken token)
    {
        var merged=new ScanSnapshot {Root=root,IsPartial=intakeScanRoot!=null};var budget=new ScanBudget();
        var progress=new Progress<int>(count=>{if(scanning)Say($"폴더 확인 중 · 항목 {count:N0}개 · 중지할 수 있습니다.");});
        var workingPaths=await Task.Run(()=>AllMoveWorkingPaths().ToArray());
        var exclusions=ProtectedGameRoots().Concat(previous.Where(p=>p.Excluded&&!previous.Any(q=>q.Id!=p.Id&&q.Registered!=false&&GameScanner.Within(q.Source,p.Source))).Select(p=>p.Source)).Concat(workingPaths).ToArray();
        var enabled=searchLocations.Where(l=>l.Enabled).Select(l=>l.Path).ToList();
        var disabled=searchLocations.Where(l=>!l.Enabled&&(intakeScanRoot==null||!GameScanner.Within(intakeScanRoot,l.Path))).Select(l=>l.Path).ToList();
        var roots=intakeScanRoot!=null?new List<string>{intakeScanRoot}:SearchLocationRules.MinimalRoots(enabled.Concat(Directory.Exists(destination)?[destination]:Array.Empty<string>()));
        if(intakeScanRoot!=null&&snapshot!=null){merged.Files.AddRange(snapshot.Files.Where(f=>!GameScanner.Within(f.Path,intakeScanRoot)));merged.Directories.AddRange(snapshot.Directories.Where(d=>!GameScanner.Within(d,intakeScanRoot)));merged.SearchRoots.AddRange(snapshot.SearchRoots.Where(r=>!GameScanner.Within(r,intakeScanRoot)));merged.UnreadablePaths.AddRange(snapshot.UnreadablePaths.Where(p=>!GameScanner.Within(p,intakeScanRoot)));}
        if(intakeScanRoot==null){var recovered=await Task.Run(()=>LocalIdentityProbe.Find(roots,previous,exclusions,token),token);foreach(var pair in recovered)merged.RecoveredRoots[pair.Key]=pair.Value;roots=SearchLocationRules.MinimalRoots(roots.Select(r=>recovered.GetValueOrDefault(r,r)));}
        foreach(var asset in previous.Where(p=>p.IsRelatedContent&&!Directory.Exists(p.Source)))foreach(var pair in merged.RecoveredRoots){var candidate=PathProof.RebaseTree(pair.Key,pair.Value,asset.Source);if(candidate!=null&&Directory.Exists(candidate)&&!exclusions.Any(e=>GameScanner.Within(candidate,e)))roots.Add(candidate);}
        roots=SearchLocationRules.MinimalRoots(roots);merged.SearchRoots.AddRange(roots);merged.InactivePaths.AddRange(disabled);
        foreach(var p in previous.Where(p=>!p.Excluded&&!merged.SearchRoots.Any(r=>GameScanner.Within(p.Source,r))))
        {merged.InactivePaths.Add(p.Source);merged.UnreadablePaths.Add(p.Source);}
        exclusions=exclusions.Concat(disabled).ToArray();
        foreach(var scanRoot in roots)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if(!Directory.Exists(scanRoot)){merged.OfflineRoots.Add(scanRoot);merged.UnreadablePaths.Add(scanRoot);merged.Issues.Add("연결 안 됨: "+scanRoot);continue;}
                var part=await Task.Run(()=>GameScanner.Scan(scanRoot,exclusions,token,progress,budget),token);
                merged.Directories.AddRange(part.Directories);merged.Files.AddRange(part.Files);merged.Issues.AddRange(part.Issues);merged.UnreadablePaths.AddRange(part.UnreadablePaths);
            }
            catch(ScanLimitException){throw;}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
            {merged.UnreadablePaths.Add(scanRoot);merged.Issues.Add($"검색 불가: {scanRoot} ({ex.Message})");}
        }
        var dirs=merged.Directories.Distinct(StringComparer.OrdinalIgnoreCase).ToList();merged.Directories.Clear();merged.Directories.AddRange(dirs);
        var files=merged.Files.DistinctBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).ToList();merged.Files.Clear();merged.Files.AddRange(files);return merged;
    }
    async Task RefreshWorkspaceAsync(string root,bool replace,bool restoring)
    {
        if(scanning||running||!LeaveScopeEdit())return;
        saveTimer.Stop();scanning=true;SourceButton.IsEnabled=false;TagsNav.IsEnabled=false;RescanButton.Content="검색 중지";SaveStatus.Content="폴더 확인 중…";
        scanCancellation=new CancellationTokenSource();var token=scanCancellation.Token;
        Render();Say(restoring?"이전 작업을 불러오고 있어요…":"폴더를 확인하고 있어요…");
        try
        {
            var previous=replace?new List<Package>():packages;var knownIds=new HashSet<string>(previous.Select(p=>p.Id));
            var result=await ReadWorkspaceFolders(root,previous,token);token.ThrowIfCancellationRequested();
            
            // A failed new search must not discard a valid existing plan.
            if(replace&&searchLocations.Any(l=>l.Enabled)&&!result.SearchRoots.Any(r=>result.Directories.Contains(r,StringComparer.OrdinalIgnoreCase)))throw new IOException("새 검색 위치를 읽지 못했습니다.");
            Say("게임 후보를 정리하고 있습니다… 중지할 수 있습니다.");
            var oldChecks=Clone(checkpoints);var oldPackages=Clone(packages);
            if(playerRoot!=destination)LoadPlayer();if(playerError.Length>0)throw new IOException("실행 설정을 읽지 못했습니다. 기존 연결을 유지합니다.");
            var nextPlay=Clone(playerDocument);var protectedIds=nextPlay.Games.Where(LaunchPathRepair.HasSettings).Select(g=>g.Id).ToHashSet();
            var moveLocations=observedMoveLocations.ToDictionary(pair=>pair.Key,pair=>pair.Value);
            var prepared=await Task.Run(()=>ScanPreparation.Build(result,replace?[]:Clone(packages),replace?[]:Clone(checkpoints),Clone(catalog.Tags),destination,token,protectedIds,moveLocations),token);
            await Task.Run(()=>LaunchPathRepair.Apply(prepared.Packages,oldChecks,nextPlay,result,oldPackages),token);
            token.ThrowIfCancellationRequested();
            if(replace)
            {
                if(sessionReady&&!workspaceBlocked){if(!FlushWorkspace())throw new IOException("기존 계획을 저장하지 못했습니다.");workspaceStore.Archive();}
                packages=[];checkpoints=[];selected=new Package();source=Path.GetFullPath(root);Search.Text="";
            }
            var relocated=prepared.Packages.Where(p=>oldPackages.Any(q=>q.Id==p.Id&&!q.Source.Equals(p.Source,StringComparison.OrdinalIgnoreCase))&&prepared.Checkpoints.Any(c=>c.PackageId==p.Id)).Select(p=>p.Id).ToList();
            if(!replace&&(relocated.Count>0||System.Text.Json.JsonSerializer.Serialize(nextPlay)!=System.Text.Json.JsonSerializer.Serialize(playerDocument)))
            {
                if(workspaceBlocked||workspaceLock==null||tagLoadError.Length>0)throw new IOException("저장 상태를 확인한 뒤 다시 검색하세요.");
                var plan=Clone(CaptureWorkspace());plan.Packages=prepared.Packages;plan.Checkpoints=prepared.Checkpoints;plan.Tags=prepared.Tags;
                foreach(var loc in plan.SearchLocations??[])if(result.RecoveredRoots.TryGetValue(loc.Path,out var recovered))loc.Path=recovered;
                try{await Task.Run(()=>Edits().Commit(new(){Plan=plan,Tags=new(){Tags=prepared.Tags},Play=nextPlay,EditedIds=relocated,ReconnectedIds=relocated}),token);}
                catch{if(File.Exists(Edits().Pending))workspaceBlocked=true;throw;}
            }
            foreach(var loc in searchLocations)if(result.RecoveredRoots.TryGetValue(loc.Path,out var recovered))loc.Path=recovered;
            playerDocument=nextPlay;var selectedId=selected.Id;
            snapshot=result;packages=prepared.Packages;checkpoints=prepared.Checkpoints;selected=packages.Find(p=>p.Id==selectedId)??new Package();libraryDocument=prepared.Library;identityInventory=prepared.Inventory;catalog.Tags=prepared.Tags;libraryError="";libraryChanges.Clear();
            foreach(var p in packages.Where(p=>result.ContainsDirectory(p.Source)&&!result.UnreadablePaths.Any(path=>p.Includes(path)||GameScanner.Within(p.Source,path))))launchFailures.Remove(p.Id);
            ImportPortableGames(knownIds,result);
            ContentLinks.Normalize(packages);
            foreach(var p in packages)IdentityReconciler.Flag(p,identityInventory,libraryDocument);
            if(!HasSelection)selected=packages.FirstOrDefault(p=>!p.Excluded)??new Package();
            history.Clear();sessionReady=true;
            DestinationLabel.Text=destination;UpdateSearchLocationLabel();
            var changed=packages.Count(p=>!p.Excluded&&!p.IsRelatedContent&&p.ReviewState.Length>0);
            Say((restoring?"이전 작업을 불러왔습니다":$"후보 {packages.Count(p=>!p.Excluded&&!p.IsRelatedContent)}개")+(changed>0?$" · 폴더 확인 {changed}개":""));Notice.ToolTip=string.Join('\n',result.Issues.Take(20));
        }
        catch(OperationCanceledException){Say("검색을 중지했습니다. 기존 목록은 유지됩니다.");}
        catch(ScanLimitException){Say("메모리 여유가 부족해 중지했습니다. ‘탐색 위치’를 나눠서 검색해 주세요.");}
        catch(Exception ex) when(LibraryFault(ex)){Say("폴더를 확인하지 못했습니다 · 기존 계획은 유지됩니다");Notice.ToolTip=ex.Message;}
        finally
        {
            scanning=false;SourceButton.IsEnabled=true;TagsNav.IsEnabled=true;RescanButton.Content="다시 검색";scanCancellation.Dispose();scanCancellation=null;
            Render();if(page=="play"&&playerCards!=null){LoadPlayer();RenderPlayer();}if(workspaceBlocked)SaveStatus.Content="저장 파일 확인 필요";else if(sessionReady&&snapshot!=null)FlushWorkspace();else SaveStatus.Content="자동 저장";
        }
    }
    void RefreshReviewDisplay()
    {
        ReviewBanner.Visibility=HasSelection&&selected.ReviewState.Length>0?Visibility.Visible:Visibility.Collapsed;
        ReviewMessage.Text=selected.ReviewReason;
        ReviewChangesButton.Content=selected.ReviewState=="scope"?"범위 확인":selected.ReviewState=="launch"?"실행 설정":"변경 확인";
        ReviewChangesButton.Visibility=selected.ReviewState is "changed" or "scope" or "launch"?Visibility.Visible:Visibility.Collapsed;
        LocationReviewButton.Visibility=selected.ReviewState is "paused" or "offline"?Visibility.Visible:Visibility.Collapsed;
        ReconnectButton.Content=moveLocationIssues.ContainsKey(selected.Id)?"이동 위치 확인":"게임 폴더 다시 찾기";
        ReconnectButton.Visibility=selected.ReviewState is "missing" or "unverified" or "relocation"?Visibility.Visible:Visibility.Collapsed;
        var hidden=packages.Count(p=>p.Excluded);ExcludedButton.Content=$"제외 {hidden}";ExcludedButton.Visibility=hidden>0?Visibility.Visible:Visibility.Collapsed;
    }
    void AcceptCurrentCheckpoint(Package p)
    {
        if(snapshot==null||p.ReviewState is "missing" or "unverified")return;
        SaveUndo();checkpoints.RemoveAll(c=>c.PackageId==p.Id);checkpoints.Add(WorkspaceReconciler.Capture(p,snapshot));
        var bytes=checkpoints.Last().Files.Sum(f=>f.Length);p.Size=FormatBytes(bytes);p.Gb=bytes/(1024d*1024*1024);
        WorkspaceReconciler.Check(p,checkpoints.Last(),snapshot);Render();Say("현재 폴더 구성을 확인했습니다");
    }
    void ReviewChanges_Click(object sender,RoutedEventArgs e)
    {
        if(!HasSelection)return;var p=selected;
        if(p.ReviewState=="scope"){ConfirmStoredScope(p);return;}
        if(p.ReviewState=="launch"){EditGames([p],launchOnly:true,advancedLaunch:true);Render();return;}
        if(p.AddedExecutables.Count>0){ReviewAddedExecutables(p);return;}
        if(Choose("폴더 변경 확인",p.ReviewReason,[new("later","나중에"),new("accept","현재 상태로 확인",true)],"later",string.Join('\n',p.ReviewDetails.Take(300)),DialogTone.Warning,subject:PlayerRules.Title(p))=="accept")AcceptCurrentCheckpoint(p);
    }
    async void Reconnect_Click(object sender,RoutedEventArgs e)
    {if(HasSelection){if(moveLocationIssues.ContainsKey(selected.Id))ShowMoveLocationIssue(selected);else await ReconnectGameAsync(selected);}}
    void Excluded_Click(object sender,RoutedEventArgs e)
    {
        var win=Modal("제외한 후보",720,500);var list=new StackPanel {Margin=new Thickness(22)};
        foreach(var p in packages.Where(p=>p.Excluded))
        {
            var row=new DockPanel {Margin=new Thickness(0,0,0,14)};var back=Btn("다시 표시",async()=>{SaveUndo();p.Excluded=false;win.Close();await RefreshWorkspaceAsync(source,false,false);});DockPanel.SetDock(back,Dock.Right);row.Children.Add(back);row.Children.Add(Text(p.Source,12));list.Children.Add(row);
        }
        win.Content=new ScrollViewer {Content=list};win.ShowDialog();
    }
    void SaveStatus_Click(object sender,RoutedEventArgs e)
    {
        if(workspaceSaveIssue!=null||workspaceBlocked){ShowWorkspaceSaveIssue();return;}
        if(HasMetadataSaveIssue){ShowMetadataSaveIssue();return;}
        var menu=new ContextMenu();var save=new MenuItem {Header="지금 저장"};save.Click+=(_,_)=>{if(!FlushWorkspace())ShowWorkspaceSaveIssue();else if(HasMetadataSaveIssue)ShowMetadataSaveIssue();};menu.Items.Add(save);
        var reset=new MenuItem {Header="새 계획 시작"};reset.Click+=async (_,_)=>
        {
            if(!Confirm("새 계획","현재 계획을 보관하고 새로 시작합니다. 게임 파일과 태그는 유지됩니다.","새 계획 시작"))return;
            workspaceBlocked=false;await RefreshWorkspaceAsync(source,true,false);
        };menu.Items.Add(reset);menu.PlacementTarget=SaveStatus;menu.IsOpen=true;
    }
    void Alias_TextChanged(object sender,TextChangedEventArgs e)
    {
        if(rendering||!HasSelection||!sessionReady)return;
        if(!aliasEditing){SaveUndo();aliasEditing=true;}selected.Alias=Alias.Text;SelectedTitle.Text=string.IsNullOrWhiteSpace(Alias.Text)?selected.Folder:Alias.Text;RenderCandidates();RenderPlan();QueueWorkspaceSave();
    }
    void Physical_TextChanged(object sender,TextChangedEventArgs e)
    {
        if(rendering||!HasSelection||!sessionReady)return;
        if(!physicalEditing){SaveUndo();physicalEditing=true;}selected.Name=PhysicalName.Text;NameWarning.Text=ValidateName(selected.Name);RenderPlan();QueueWorkspaceSave();
    }
}

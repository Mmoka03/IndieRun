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
        try{ScopeChanges().Recover();if(Edits().Recover())LoadTags();}catch(Exception ex) when(LibraryFault(ex)){workspaceBlocked=true;ErrorInfo("편집 저장 복구","이전 편집 내용을 복구하지 못했습니다.",ex.Message);}
        var loaded=workspaceStore.Load();
        if(loaded.Document!=null)
        {
            lastSavedSignature=loaded.Recovered?null:StateSignature(loaded.Document);
            ApplyWorkspace(loaded.Document);ResolveTagBindings(loaded.Document);SyncMovedPackages();
            await RefreshWorkspaceAsync(source,false,true);
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
        source=doc.SearchRoot;searchLocations=doc.SearchLocations??[new SearchLocation {Path=doc.SearchRoot}];destination=doc.Destination;packages=doc.Packages;checkpoints=doc.Checkpoints;
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
        if(File.Exists(Edits().Pending)||ScopeChanges().HasPending){SaveStatus.Content="편집 저장 복구 필요";return;}
        if(!sessionReady||scanning||workspaceBlocked||workspaceLock==null)return;
        SaveStatus.Content="저장 중…";saveTimer.Stop();saveTimer.Start();
    }
    bool FlushWorkspace()
    {
        if(File.Exists(Edits().Pending)||ScopeChanges().HasPending){SaveStatus.Content="편집 저장 복구 필요";return false;}
        if(!sessionReady)return true;
        if(workspaceBlocked||workspaceLock==null)return false;
        try
        {
            var document=CaptureWorkspace();var signature=StateSignature(document);
            if(signature!=lastSavedSignature){workspaceStore.Save(document);lastSavedSignature=signature;}
            SaveStatus.Content="저장됨 ✓";SaveStatus.ToolTip=$"{DateTime.Now:HH:mm:ss} · {workspaceStore.FilePath}";SaveLibraryMetadata();SyncPortableMetadata();return true;
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {SaveStatus.Content="저장 실패 · 재시도";SaveStatus.ToolTip=ex.Message;return false;}
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
        var merged=new ScanSnapshot {Root=root};
        var exclusions=new[]{AppContext.BaseDirectory,Path.Combine(projectRoot,"backups"),backupStorageRoot,Path.Combine(projectRoot,"src")}.Concat(previous.Where(p=>p.Excluded).Select(p=>p.Source)).Concat(BackupService().MoveWorkingPaths()).ToArray();
        var enabled=searchLocations.Where(l=>l.Enabled).Select(l=>l.Path).ToList();
        var disabled=searchLocations.Where(l=>!l.Enabled).Select(l=>l.Path).ToList();
        var roots=SearchLocationRules.MinimalRoots(enabled.Concat(Directory.Exists(destination)?[destination]:Array.Empty<string>()));
        merged.SearchRoots.AddRange(roots);merged.InactivePaths.AddRange(disabled);
        foreach(var p in previous.Where(p=>!p.Excluded&&!roots.Any(r=>GameScanner.Within(p.Source,r))))
        {merged.InactivePaths.Add(p.Source);merged.UnreadablePaths.Add(p.Source);}
        exclusions=exclusions.Concat(disabled).ToArray();
        foreach(var scanRoot in roots)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if(!Directory.Exists(scanRoot)){merged.OfflineRoots.Add(scanRoot);merged.UnreadablePaths.Add(scanRoot);merged.Issues.Add("연결 안 됨: "+scanRoot);continue;}
                var part=await Task.Run(()=>GameScanner.Scan(scanRoot,exclusions,token),token);
                merged.Directories.AddRange(part.Directories);merged.Files.AddRange(part.Files);merged.Issues.AddRange(part.Issues);merged.UnreadablePaths.AddRange(part.UnreadablePaths);
            }
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
            if(replace)
            {
                if(sessionReady&&!workspaceBlocked){if(!FlushWorkspace())throw new IOException("기존 계획을 저장하지 못했습니다.");workspaceStore.Archive();}
                packages=[];checkpoints=[];selected=new Package();source=Path.GetFullPath(root);Search.Text="";
            }
            snapshot=result;InspectLibrary(result,packages);
            foreach(var p in packages)WorkspaceReconciler.Check(p,checkpoints.Find(c=>c.PackageId==p.Id),result);
            var additions=WorkspaceReconciler.Discover(result,packages);
            foreach(var p in additions)
            {
                IdentityReconciler.ExpandCandidate(p,identityInventory,destination);
                if(packages.Any(q=>GameScanner.Within(p.DetectedRoot,q.Source)))continue;
                p.Size=FormatBytes(result.Files.Where(f=>GameScanner.Within(f.Path,p.Source)).Sum(f=>f.Length));p.Color=packageColors[packages.Count%packageColors.Length];
                packages.Add(p);checkpoints.Add(WorkspaceReconciler.Capture(p,result));WorkspaceReconciler.Check(p,checkpoints.Last(),result);
            }
            ImportPortableGames(knownIds,result);
            foreach(var owner in packages)foreach(var r in owner.RelatedFolders){var linked=packages.Find(p=>p.Id==r.Id);if(linked!=null)r.Source=linked.Source;}
            foreach(var p in packages)IdentityReconciler.Flag(p,identityInventory,libraryDocument);
            if(!HasSelection)selected=packages.FirstOrDefault(p=>!p.Excluded)??new Package();
            history.Clear();sessionReady=true;
            DestinationLabel.Text=destination;UpdateSearchLocationLabel();
            var changed=packages.Count(p=>!p.Excluded&&p.ReviewState.Length>0);
            Say(restoring?$"이전 작업 복원 · 확인 필요 {changed}개":$"후보 {packages.Count(p=>!p.Excluded)}개 · 확인 필요 {changed}개");Notice.ToolTip=string.Join('\n',result.Issues.Take(20));
        }
        catch(OperationCanceledException){Say("검색을 중지했습니다");}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException){Say("폴더를 확인하지 못했습니다 · 기존 계획은 유지됩니다");Notice.ToolTip=ex.Message;}
        finally
        {
            scanning=false;SourceButton.IsEnabled=true;TagsNav.IsEnabled=true;RescanButton.Content="다시 검색";scanCancellation.Dispose();scanCancellation=null;
            Render();if(page=="play"&&playerCards!=null){LoadPlayer();RenderPlayer();}if(workspaceBlocked)SaveStatus.Content="저장 파일 확인 필요";else if(sessionReady)FlushWorkspace();else SaveStatus.Content="자동 저장";
        }
    }
    void RefreshReviewDisplay()
    {
        ReviewBanner.Visibility=HasSelection&&selected.ReviewState.Length>0?Visibility.Visible:Visibility.Collapsed;
        ReviewMessage.Text=selected.ReviewReason;
        ReviewChangesButton.Visibility=selected.ReviewState=="changed"?Visibility.Visible:Visibility.Collapsed;
        LocationReviewButton.Visibility=selected.ReviewState is "paused" or "offline"?Visibility.Visible:Visibility.Collapsed;
        ReconnectButton.Visibility=selected.ReviewState is "missing" or "unverified"?Visibility.Visible:Visibility.Collapsed;
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
        if(Choose("폴더 변경 확인",p.ReviewReason,[new("later","나중에"),new("accept","현재 상태로 확인",true)],"later",string.Join('\n',p.ReviewDetails.Take(300)),DialogTone.Warning,subject:PlayerRules.Title(p))=="accept")AcceptCurrentCheckpoint(p);
    }
    async void Reconnect_Click(object sender,RoutedEventArgs e)
    {if(HasSelection)await ReconnectGameAsync(selected);}
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
        var menu=new ContextMenu();var save=new MenuItem {Header="지금 저장"};save.Click+=(_,_)=>{if(!FlushWorkspace())ErrorInfo("저장 확인","변경 내용을 저장하지 못했습니다.",SaveStatus.ToolTip?.ToString()??"저장 파일을 확인해 주세요.");};menu.Items.Add(save);
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

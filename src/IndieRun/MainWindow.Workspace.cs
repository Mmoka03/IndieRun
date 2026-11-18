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
        try{if(Edits().Recover())LoadTags();}catch(Exception ex) when(LibraryFault(ex)){workspaceBlocked=true;Info("편집 저장 복구",ex.Message);}
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
        return new WorkspaceDocument {SavedUtc=DateTime.UtcNow,SearchRoot=source,Destination=destination,BackupRoot=backupStorageRoot,SelectedId=HasSelection?selected.Id:"",Filter=Search.Text,Packages=packages,Checkpoints=checkpoints,Tags=catalog.Tags};
    }
    void ApplyWorkspace(WorkspaceDocument doc)
    {
        source=doc.SearchRoot;destination=doc.Destination;packages=doc.Packages;checkpoints=doc.Checkpoints;
        if(!string.IsNullOrWhiteSpace(doc.BackupRoot))backupStorageRoot=doc.BackupRoot;
        selected=packages.Find(p=>p.Id==doc.SelectedId&&!p.Excluded)??packages.FirstOrDefault(p=>!p.Excluded)??new Package();
        rendering=true;Search.Text=doc.Filter;rendering=false;
        DestinationLabel.Text=destination;ScanSourceLabel.Text=Path.GetFileName(source.TrimEnd('\\'));ScanSourceLabel.ToolTip=source;
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
        if(File.Exists(Edits().Pending)){SaveStatus.Content="편집 저장 복구 필요";return;}
        if(!sessionReady||scanning||workspaceBlocked||workspaceLock==null)return;
        SaveStatus.Content="저장 중…";saveTimer.Stop();saveTimer.Start();
    }
    bool FlushWorkspace()
    {
        if(File.Exists(Edits().Pending)){SaveStatus.Content="편집 저장 복구 필요";return false;}
        if(!sessionReady)return true;
        if(workspaceBlocked||workspaceLock==null)return false;
        try
        {
            var document=CaptureWorkspace();var signature=StateSignature(document);
            if(signature!=lastSavedSignature){workspaceStore.Save(document);lastSavedSignature=signature;}
            SaveStatus.Content="저장됨 ✓";SaveStatus.ToolTip=$"{DateTime.Now:HH:mm:ss} · {workspaceStore.FilePath}";SaveLibraryMetadata();return true;
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
        saveTimer.Stop();scanCancellation?.Cancel();
        if(!closingWithoutSave&&!FlushWorkspace()&&MessageBox.Show(this,"계획을 저장하지 못했습니다. 그래도 종료할까요?\n실제 게임 파일에는 영향이 없습니다.","계획 저장 확인",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)e.Cancel=true;
    }
    async Task<ScanSnapshot> ReadWorkspaceFolders(string root,IEnumerable<Package> previous,CancellationToken token)
    {
        var merged=new ScanSnapshot {Root=root};
        var exclusions=new[]{AppContext.BaseDirectory,Path.Combine(projectRoot,"backups"),backupStorageRoot,Path.Combine(projectRoot,"src")}.Concat(previous.Where(p=>p.Excluded).Select(p=>p.Source)).Concat(BackupService().MoveWorkingPaths()).ToArray();
        var roots=new[]{root}.Concat(Directory.Exists(destination)?new[]{destination}:Array.Empty<string>()).Concat(previous.Where(p=>!p.Excluded&&!GameScanner.Within(p.Source,root)).Select(p=>p.Source)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach(var scanRoot in roots)
        {
            token.ThrowIfCancellationRequested();
            try
            {
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
        if(scanning||running)return;
        saveTimer.Stop();scanning=true;SourceButton.IsEnabled=false;TagsNav.IsEnabled=false;RescanButton.Content="검색 중지";SaveStatus.Content="폴더 확인 중…";
        scanCancellation=new CancellationTokenSource();var token=scanCancellation.Token;
        Render();Say(restoring?"이전 작업을 불러오고 있어요…":"폴더를 확인하고 있어요…");
        try
        {
            var previous=replace?new List<Package>():packages;
            var result=await ReadWorkspaceFolders(root,previous,token);token.ThrowIfCancellationRequested();
            if(!restoring&&!sessionReady&&packages.Count==0&&!result.Directories.Contains(root,StringComparer.OrdinalIgnoreCase))throw new IOException("검색 위치를 읽지 못했습니다.");
            // A failed new search must not discard a valid existing plan.
            if(replace&&!result.Directories.Contains(root,StringComparer.OrdinalIgnoreCase))throw new IOException("새 검색 위치를 읽지 못했습니다.");
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
            foreach(var p in packages)IdentityReconciler.Flag(p,identityInventory,libraryDocument);
            if(!HasSelection)selected=packages.FirstOrDefault(p=>!p.Excluded)??new Package();
            history.Clear();sessionReady=true;
            DestinationLabel.Text=destination;ScanSourceLabel.Text=Path.GetFileName(source.TrimEnd('\\'));ScanSourceLabel.ToolTip=source;
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
        if(!HasSelection)return;var p=selected;var win=Modal("변경 확인",760,560);var dock=new DockPanel {Margin=new Thickness(24)};
        var title=Text(p.ReviewReason,20);DockPanel.SetDock(title,Dock.Top);dock.Children.Add(title);
        var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,18,0,0)};DockPanel.SetDock(actions,Dock.Bottom);
        actions.Children.Add(Btn("나중에",()=>win.Close()));actions.Children.Add(Btn("현재 상태로 확인",()=>{AcceptCurrentCheckpoint(p);win.Close();},true));dock.Children.Add(actions);
        dock.Children.Add(new TextBox {Text=string.Join('\n',p.ReviewDetails.Take(300)),IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,16,0,0)});win.Content=dock;win.ShowDialog();
    }
    async void Reconnect_Click(object sender,RoutedEventArgs e)
    {
        if(!HasSelection||scanning)return;var p=selected;
        var picker=new OpenFolderDialog {Title="이 게임의 현재 폴더 선택"};if(picker.ShowDialog(this)!=true)return;
        ScanSnapshot part;
        try{part=await Task.Run(()=>GameScanner.Scan(picker.FolderName));}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){Info("폴더 확인",ex.Message);return;}
        if(part.UnreadablePaths.Count>0||!GameScanner.CandidateGroups(part).Any()){Info("폴더 확인","읽을 수 있는 게임 실행 후보가 있는 폴더를 선택해 주세요.");return;}
        var duplicates=packages.Where(q=>q!=p&&(GameScanner.Within(q.Source,picker.FolderName)||GameScanner.Within(picker.FolderName,q.Source))).ToList();
        if(duplicates.Count>0&&MessageBox.Show(this,"이 위치에 별도로 발견한 후보가 있습니다.\n선택한 게임의 기존 정보를 유지하고 하나로 연결할까요?","후보 연결",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        SaveUndo();foreach(var q in duplicates){packages.Remove(q);checkpoints.RemoveAll(c=>c.PackageId==q.Id);}
        p.Source=picker.FolderName;p.DetectedRoot=GameScanner.CandidateGroups(part).First().Key;p.SearchRoot=GameScanner.Within(p.Source,source)?source:p.Source;p.Folder=Path.GetFileName(p.Source);
        var bytes=part.Files.Sum(f=>f.Length);p.Size=FormatBytes(bytes);p.Gb=bytes/(1024d*1024*1024);
        snapshot??=new ScanSnapshot {Root=source};snapshot.Files.RemoveAll(f=>GameScanner.Within(f.Path,p.Source));snapshot.Directories.RemoveAll(d=>GameScanner.Within(d,p.Source));snapshot.Files.AddRange(part.Files);snapshot.Directories.AddRange(part.Directories);
        snapshot.UnreadablePaths.RemoveAll(path=>GameScanner.Within(path,p.Source));
        checkpoints.RemoveAll(c=>c.PackageId==p.Id);checkpoints.Add(WorkspaceReconciler.Capture(p,part));WorkspaceReconciler.Check(p,checkpoints.Last(),snapshot);Render();Say("현재 폴더에 다시 연결했습니다 · 파일 변경 없음");
    }
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
        var menu=new ContextMenu();var save=new MenuItem {Header="지금 저장"};save.Click+=(_,_)=>{if(!FlushWorkspace())Info("저장 확인",SaveStatus.ToolTip?.ToString()??"저장 파일을 확인해 주세요.");};menu.Items.Add(save);
        var reset=new MenuItem {Header="새 계획 시작"};reset.Click+=async (_,_)=>
        {
            if(MessageBox.Show(this,"현재 편집 계획을 비우고 새로 시작할까요?\n게임 파일과 만든 태그는 유지됩니다.","새 계획",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
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

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IndieRun;

public partial class MainWindow
{
    ScanSnapshot? snapshot;
    CancellationTokenSource? scanCancellation;
    bool scanning;
    string projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
    static readonly string[] packageColors=["#527BDC","#548B75","#9471BF","#C18554","#547F93"];

    Task ScanDefaultAsync()
    {
        source=projectRoot;destination=Path.Combine(projectRoot,"library");DestinationLabel.Text=destination;
        sessionReady=true;UpdateSearchLocationLabel();Say("탐색 위치를 추가해 게임을 찾아보세요");FlushWorkspace();return Task.CompletedTask;
    }
    async Task ScanAsync(string root,bool askDiscard)
    {if(scanning||running)return;await RefreshWorkspaceAsync(source,false,false);}
    static string FormatBytes(long bytes) => bytes>=1024L*1024*1024?$"{bytes/(1024d*1024*1024):0.##} GB":bytes>=1024*1024?$"{bytes/(1024d*1024):0.#} MB":bytes>=1024?$"{bytes/1024d:0.#} KB":$"{bytes} B";
    List<ScannedFile> Executables(Package p) => snapshot?.Files.Where(f=>p.Includes(f.Path)&&Path.GetExtension(f.Path).Equals(".exe",StringComparison.OrdinalIgnoreCase)).ToList()??[];
    bool CanIncludeParent()
    {
        if(!selected.IsReal||snapshot==null||IsAdded(selected)||selected.ReviewState.Length>0)return false;
        var parent=Directory.GetParent(selected.Source)?.FullName;
        return parent!=null&&GameScanner.Within(parent,selected.SearchRoot)&&!parent.Equals(selected.SearchRoot,StringComparison.OrdinalIgnoreCase)
            &&!packages.Any(p=>p!=selected&&(GameScanner.Within(p.Source,parent)||GameScanner.Within(parent,p.Source)));
    }
    void IncludeParent_Click(object sender,RoutedEventArgs e)
    {
        if(scanning||backupBusy||running)return;
        if(scopeDraft!=null){var parent=Directory.GetParent(scopeDraft.Source)?.FullName;if(parent!=null)DraftScope(parent);return;}
        if(CanIncludeParent())SetScope(Directory.GetParent(selected.Source)!.FullName);
        else ShowOrganizeWarning("다른 게임이 함께 포함되거나 범위를 변경할 수 없습니다.");
    }
    void ResetScope_Click(object sender,RoutedEventArgs e)
    {
        if(scanning||backupBusy||running)return;
        if(scopeDraft!=null){var prior=scopeDraftHistory.LastOrDefault();if(prior!=null)DraftScope(prior,false);return;}
        var previous=selected.ScopeHistory.LastOrDefault();if(previous!=null)SetScope(previous,false);
    }
    void SetScope(string path,bool remember=true)
    {
        path=Path.GetFullPath(path);
        if(snapshot==null||!selected.IsReal||IsAdded(selected)||scanning||backupBusy||running)return;
        if(selected.Contents.Count>0||selected.RelatedFolders.Count>0){ShowOrganizeWarning("구성 수정에서 폴더 지정을 먼저 해제하세요.");return;}
        if(File.Exists(Path.Combine(selected.Source,LibraryStore.MarkerName))){ShowOrganizeWarning("식별 파일이 있는 폴더는 범위를 바꿀 수 없습니다.");return;}
        if(selected.ReviewState.Length>0){ShowOrganizeWarning("현재 폴더 상태를 먼저 확인하세요.");return;}
        if(!GameScanner.Within(path,selected.SearchRoot)||path.Equals(selected.SearchRoot,StringComparison.OrdinalIgnoreCase)||!GameScanner.Within(selected.DetectedRoot,path)||!snapshot.Directories.Contains(path,StringComparer.OrdinalIgnoreCase))
        {ShowOrganizeWarning("실행 후보를 포함하는 폴더를 선택하세요. 탐색 위치 전체는 포함하지 않습니다.");return;}
        if(packages.Any(p=>p!=selected&&(GameScanner.Within(p.Source,path)||GameScanner.Within(path,p.Source))))
        {ShowOrganizeWarning("다른 게임이 함께 포함됩니다. 범위는 변경하지 않았습니다.");return;}
        if(selected.Source.Equals(path,StringComparison.OrdinalIgnoreCase))return;
        SaveUndo();if(remember)selected.ScopeHistory.Add(selected.Source);else selected.ScopeHistory.RemoveAt(selected.ScopeHistory.Count-1);
        var oldFolder=selected.Folder;selected.Source=path;selected.Folder=Path.GetFileName(path);
        if(selected.Name==oldFolder)selected.Name=selected.Folder;
        var bytes=snapshot.Files.Where(f=>GameScanner.Within(f.Path,path)).Sum(f=>f.Length);selected.Size=FormatBytes(bytes);selected.Gb=bytes/(1024d*1024*1024);
        checkpoints.RemoveAll(c=>c.PackageId==selected.Id);checkpoints.Add(WorkspaceReconciler.Capture(selected,snapshot));
        WorkspaceReconciler.Check(selected,checkpoints.Last(),snapshot);Render();Say(remember?"상위 폴더 전체를 포함했습니다.":"이전 범위로 돌아왔습니다.");
    }
    void ExcludeCandidate(Package p)
    {
        SaveUndo();p.Excluded=true;p.Planned=false;if(p==selected)selected=packages.FirstOrDefault(q=>!q.Excluded)??new Package();Render();Say("이 계획에서 제외했습니다");
    }
    void RenderRealTree()
    {
        if(snapshot==null)return;
        var tree=new TreeView {BorderThickness=new Thickness(0),Background=Brushes.Transparent,FontSize=12,MaxHeight=240,HorizontalContentAlignment=HorizontalAlignment.Stretch};
        ScrollViewer.SetVerticalScrollBarVisibility(tree,ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(tree,ScrollBarVisibility.Disabled);
        var root=FolderNode(selected.Source,true);root.IsExpanded=true;tree.Items.Add(root);
        TreeRows.Children.Add(tree);
        IncludeParentButton.ToolTip=Directory.GetParent(selected.Source)?.FullName;
    }
    TreeViewItem FolderNode(string path,bool root=false)
    {
        var node=new TreeViewItem {Header=NodeHeader(Path.GetFileName(path),root?"함께 이동":"폴더",path),HorizontalContentAlignment=HorizontalAlignment.Stretch,Padding=new Thickness(0,4,0,4)};
        var menu=new ContextMenu();var scope=new MenuItem {Header="이 폴더를 이동 범위로"};scope.Click+=(_,_)=>SetScope(path);menu.Items.Add(scope);node.ContextMenu=menu;
        node.Items.Add(new TreeViewItem {Header="…"});bool populated=false;
        void Populate()
        {
            if(populated||snapshot==null)return;populated=true;node.Items.Clear();
            var dirs=snapshot.Directories.Where(d=>string.Equals(Path.GetDirectoryName(d),path,StringComparison.OrdinalIgnoreCase)).OrderBy(d=>d,StringComparer.OrdinalIgnoreCase).ToList();
            var files=snapshot.Files.Where(f=>string.Equals(Path.GetDirectoryName(f.Path),path,StringComparison.OrdinalIgnoreCase)).OrderBy(f=>Path.GetExtension(f.Path).Equals(".exe",StringComparison.OrdinalIgnoreCase)?0:1).ThenBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).ToList();
            foreach(var dir in dirs.Take(150))node.Items.Add(FolderNode(dir));
            foreach(var file in files.Take(Math.Max(0,200-dirs.Count)))
            {
                var ext=Path.GetExtension(file.Path).ToLowerInvariant();
                var role=ext==".exe"?(GameScanner.Auxiliary(file.Path)?"보조 추정":"실행 후보"):ReadOnlyPreview.IsText(file.Path)?"문서":ReadOnlyPreview.IsImage(file.Path)?"이미지":"파일";
                node.Items.Add(new TreeViewItem {Header=NodeHeader(Path.GetFileName(file.Path),role,file.Path),Padding=new Thickness(0,3,0,3)});
            }
            if(dirs.Count+files.Count>200)node.Items.Add(new TreeViewItem {Header=Text("일부 항목만 표시 · 원본 폴더에서 전체 확인",11,"#8796AA")});
        }
        node.Expanded+=(_,e)=>{if(e.OriginalSource==node)Populate();};if(root)Populate();return node;
    }
    Grid NodeHeader(string name,string role,string fullPath)
    {
        var grid=new Grid {MaxWidth=Math.Max(300,PackageLayer.ActualWidth-75),ToolTip=fullPath};
        grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        var label=new TextBlock {Text=name,FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)};grid.Children.Add(label);
        var badge=Badge(role);Grid.SetColumn(badge,1);grid.Children.Add(badge);return grid;
    }
    void OpenSourceFolder(string path)
    {
        try
        {
            if(!Directory.Exists(path)){Info("폴더 확인","폴더 위치가 바뀌었습니다. 다시 검색해 주세요.");return;}
            var start=new ProcessStartInfo("explorer.exe"){UseShellExecute=true};start.ArgumentList.Add(path);Process.Start(start);
        }
        catch(Exception ex) when(ex is IOException or System.ComponentModel.Win32Exception){ErrorInfo("폴더 확인","폴더를 열지 못했습니다.",ex.Message);}
    }
    long PackageBytes(Package p)=>snapshot?.Files.Where(f=>p.Includes(f.Path)).Sum(f=>f.Length)??(long)Math.Ceiling(p.Gb*1024*1024*1024);
    void ShowReadOnlyPlan(List<Package> list,string? targetRoot=null)
    {
        try{list=CompositionPlan.Moving(CompositionPlan.Expand(list,packages),targetRoot??destination);}catch(Exception ex) when(LibraryFault(ex)){ErrorInfo("구성 확인","게임 구성을 다시 확인하세요.",ex.Message);return;}
        var moveTarget=targetRoot??destination;
        var win=Modal("이동 계획 확인",840,720);var dock=new DockPanel {Margin=new Thickness(28)};
        var header=new StackPanel();header.Children.Add(WorkText($"이동 대상 {list.Count}개 · 약 {FormatBytes(list.Sum(PackageBytes))}",21,"Text.Primary"));
        var paths=new StackPanel {Margin=new Thickness(0,12,0,12)};
        paths.Children.Add(WorkText("도착 위치",11,"Text.Muted"));var moveTargetPath=WorkText(moveTarget,12);moveTargetPath.TextTrimming=TextTrimming.CharacterEllipsis;moveTargetPath.TextWrapping=TextWrapping.NoWrap;moveTargetPath.ToolTip=moveTarget;var moveTargetRow=new DockPanel();var moveTargetChange=Btn("변경",()=>{});DockPanel.SetDock(moveTargetChange,Dock.Right);moveTargetRow.Children.Add(moveTargetChange);moveTargetRow.Children.Add(moveTargetPath);paths.Children.Add(moveTargetRow);
        var backupRow=new DockPanel {Margin=new Thickness(0,10,0,0)};var change=Btn("변경",()=>{});DockPanel.SetDock(change,Dock.Right);backupRow.Children.Add(change);
        var backupInfo=new StackPanel();backupInfo.Children.Add(WorkText("백업 위치",11,"Text.Muted"));var backupPath=WorkText(backupStorageRoot,12);backupPath.TextTrimming=TextTrimming.CharacterEllipsis;backupPath.TextWrapping=TextWrapping.NoWrap;backupPath.ToolTip=backupStorageRoot;backupInfo.Children.Add(backupPath);backupRow.Children.Add(backupInfo);paths.Children.Add(backupRow);header.Children.Add(paths);
        var estimate=WorkText("",12);var storageDetails=WorkDetails("","드라이브별 여유 공간");header.Children.Add(estimate);header.Children.Add(storageDetails);
        void UpdateStorage()
        {
            var total=list.Sum(PackageBytes);
            var copy=list.Where(p=>p.OmittedFolders.Count>0||!string.Equals(Path.GetPathRoot(p.Source),Path.GetPathRoot(moveTarget),StringComparison.OrdinalIgnoreCase)).Sum(PackageBytes);
            estimate.Text="추가 공간 예상 · 백업 "+FormatBytes(total)+(copy>0?" + 이동 복사 "+FormatBytes(copy):"");
            var volumes=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);void Add(string path,long bytes){var root=Path.GetPathRoot(Path.GetFullPath(path))!;volumes[root]=volumes.GetValueOrDefault(root)+bytes;}
            Add(backupStorageRoot,total);if(copy>0)Add(moveTarget,copy);
            var lines=new List<string>();foreach(var (root,bytes) in volumes){try{var free=new DriveInfo(root).AvailableFreeSpace;lines.Add(root+" · 필요 약 "+FormatBytes(bytes)+" / 사용 가능 "+FormatBytes(free)+(free<bytes?" · 공간 부족":""));}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException){lines.Add(root+" · 여유 공간을 확인하지 못했습니다.");}}
            storageDetails.Content=new ScrollViewer {Content=WorkText(string.Join("\n",lines),12),MaxHeight=90};
        }
        moveTargetChange.Click+=(_,_)=>{var picker=new Microsoft.Win32.OpenFolderDialog {Title="보관함으로 옮길 위치 선택",InitialDirectory=moveTarget};if(picker.ShowDialog(win)==true){win.Close();ShowReadOnlyPlan(list,picker.FolderName);}};
        change.Click+=(_,_)=>{ChooseBackupRoot();backupPath.Text=backupStorageRoot;backupPath.ToolTip=backupStorageRoot;UpdateStorage();};UpdateStorage();
        DockPanel.SetDock(header,Dock.Top);dock.Children.Add(header);
        var footer=new StackPanel {Margin=new Thickness(0,18,0,0)};DockPanel.SetDock(footer,Dock.Bottom);
        var warning=WorkText("게임을 종료한 뒤 진행하세요. 백업은 정상 작동 확인 후 직접 삭제하세요.",12,"Warning.Text");footer.Children.Add(warning);
        if(list.Any(p=>p.Contents.Any(c=>c.Role=="exclude")))footer.Children.Add(WorkText("제외한 폴더는 현재 위치에 남습니다.",12,"Text.Secondary"));
        footer.Children.Add(WorkText("별칭·태그·실행 설정과 이미지를 도착 위치에 함께 보관합니다.",12,"Text.Secondary"));
        footer.Children.Add(WorkText("이동할 파일을 백업합니다. 일부 폴더를 남기는 이동은 같은 드라이브에서도 복사 공간이 필요합니다.",12,"Text.Muted"));
        var buttons=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
        buttons.Children.Add(Btn("계속 편집",()=>win.Close()));
        buttons.Children.Add(Btn("백업만 실행",async()=>{win.Close();await StartBackupBatchAsync(list);}));
        var move=Btn("백업 후 이동",async()=>
        {
            var problems=list.Select(p=>BackupEngine.MoveNameError(p.Name)).FirstOrDefault(s=>s!=null);
            if(problems!=null){warning.Text=problems;return;}
            var paths=list.Select(p=>CompositionPlan.Target(p,moveTarget)).ToList();
            if(paths.Any(path=>path.Any(ch=>ch>127))&&!Confirm("경로 문자 확인","경로에 한글·일본어 등이 포함되어 있어 일부 게임이 실행되지 않을 수 있습니다.","이 경로로 이동",string.Join('\n',paths),win))return;
            win.Close();await StartMoveBatchAsync(list,moveTarget);
        },true);
        move.IsEnabled=!backupBusy&&!list.Any(p=>p.ReviewState.Length>0);buttons.Children.Add(move);
        footer.Children.Add(buttons);dock.Children.Add(footer);
        var rows=new StackPanel();foreach(var p in list)
        {
            var content=new StackPanel();content.Children.Add(Text(string.IsNullOrWhiteSpace(p.Alias)?p.Folder:p.Alias,16));
            if(p.Contents.Count>0||p.RelatedFolders.Count>0)content.Children.Add(WorkText(CompositionPlan.Summary(p),12,"Text.Secondary"));
            if(p.ContentOwnerId.Length>0)content.Children.Add(WorkText("관련 콘텐츠 · 본편과 연결",12,"Composition.Related.Text"));
            var movePath=WorkText("",12);var route=new Expander {Header="이동 경로",Content=movePath,Margin=new Thickness(0,10,0,0)};route.SetResourceReference(ForegroundProperty,"Text.Muted");content.Children.Add(route);
            var name=new TextBox {Text=p.Name,Margin=new Thickness(0,8,0,4),ToolTip="이동 후 폴더명 · 내부 폴더명은 유지됩니다"};
            var structure=MoveStructure(p);
            var nameStatus=WorkText("",12,"Warning.Text");
            void CheckName(){structure.Header=p.Name+"/";var problem=BackupEngine.MoveNameError(p.Name);if(problem!=null){movePath.Text="원본: "+p.Source+"\n도착 위치: "+moveTarget;nameStatus.Text=problem;return;}var path=CompositionPlan.Target(p,moveTarget);movePath.Text="원본: "+p.Source+"\n도착: "+path;nameStatus.Text=File.Exists(path)||Directory.Exists(path)?"같은 이름이 있습니다 · 다른 이름을 입력하세요":ValidateName(p.Name);}
            name.TextChanged+=(_,_)=>{p.Name=name.Text;CheckName();QueueWorkspaceSave();};CheckName();content.Children.Add(WorkText("이동 후 폴더명",11,"Text.Muted"));content.Children.Add(name);content.Children.Add(nameStatus);content.Children.Add(structure);
            if(p.ReviewState.Length>0)content.Children.Add(Text("확인 필요: "+p.ReviewReason,12,"#A77B40"));
            rows.Children.Add(new Border {Child=content,Padding=new Thickness(14),Background=ThemeBrush("Surface.Secondary"),CornerRadius=new CornerRadius(7),Margin=new Thickness(0,0,0,12)});
        }
        dock.Children.Add(new ScrollViewer {Content=rows});win.Content=dock;win.ShowDialog();Render();
    }
}

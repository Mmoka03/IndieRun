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
    List<ScannedFile> Executables(Package p)
    {
        if(snapshot==null)return [];
        return snapshot.FilesUnder(p.Source).Where(f=>Path.GetExtension(f.Path).Equals(".exe",StringComparison.OrdinalIgnoreCase)&&p.Includes(f.Path)).ToList();
    }
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
    void ShowReadOnlyPlan(List<Package> list,string? targetRoot=null)=>OpenMovePlan(list,targetRoot??destination);
}

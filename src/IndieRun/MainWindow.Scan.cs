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

    async Task ScanDefaultAsync()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        for(int i=0;directory!=null&&i<7;i++,directory=directory.Parent)
        {
            var root=Path.Combine(directory.FullName,"scan-input");
            if(!Directory.Exists(root))continue;
            projectRoot=directory.FullName;destination=Path.Combine(projectRoot,"library");DestinationLabel.Text=destination;
            await ScanAsync(root,false);return;
        }
        Say("폴더를 선택해 게임 후보를 찾아보세요");
    }
    async Task ScanAsync(string root,bool askDiscard)
    {
        if(scanning||running)return;
        var same=Path.GetFullPath(root).Equals(Path.GetFullPath(source),StringComparison.OrdinalIgnoreCase);
        if(!same && askDiscard && packages.Count>0 && MessageBox.Show(this,"다른 검색 위치로 새 계획을 시작할까요?\n현재 계획의 직전 저장본은 보관합니다.","검색 위치 변경",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        await RefreshWorkspaceAsync(root,!same,false);
    }
    static string FormatBytes(long bytes) => bytes>=1024L*1024*1024?$"{bytes/(1024d*1024*1024):0.##} GB":bytes>=1024*1024?$"{bytes/(1024d*1024):0.#} MB":$"{bytes/1024d:0.#} KB";
    List<ScannedFile> Executables(Package p) => snapshot?.Files.Where(f=>GameScanner.Within(f.Path,p.Source)&&Path.GetExtension(f.Path).Equals(".exe",StringComparison.OrdinalIgnoreCase)).ToList()??[];
    bool CanIncludeParent()
    {
        if(!selected.IsReal||snapshot==null||selected.ReviewState.Length>0)return false;
        var parent=Directory.GetParent(selected.Source)?.FullName;
        return parent!=null&&GameScanner.Within(parent,selected.SearchRoot)&&!parent.Equals(selected.SearchRoot,StringComparison.OrdinalIgnoreCase)
            &&!packages.Any(p=>p!=selected&&GameScanner.Within(p.Source,parent));
    }
    void IncludeParent_Click(object sender,RoutedEventArgs e)
    {if(CanIncludeParent())SetScope(Directory.GetParent(selected.Source)!.FullName);}
    void ResetScope_Click(object sender,RoutedEventArgs e) => SetScope(selected.DetectedRoot);
    void SetScope(string path)
    {
        if(snapshot==null||!selected.IsReal)return;
        if(selected.ReviewState.Length>0){Info("변경 확인","현재 폴더 상태를 먼저 확인해 주세요.");return;}
        if(!GameScanner.Within(path,selected.SearchRoot)||path.Equals(selected.SearchRoot,StringComparison.OrdinalIgnoreCase)||!GameScanner.Within(selected.DetectedRoot,path))
        {Info("범위 확인","실행 후보를 포함하는 폴더를 선택해 주세요. 검색 시작 폴더 전체는 이동 범위로 지정하지 않습니다.");return;}
        if(packages.Any(p=>p!=selected&&(GameScanner.Within(p.Source,path)||GameScanner.Within(path,p.Source))))
        {Info("범위 확인","다른 후보와 범위가 겹칩니다. 여러 후보의 패키지 병합은 다음 단계에서 지원합니다.");return;}
        if(selected.Source.Equals(path,StringComparison.OrdinalIgnoreCase))return;
        SaveUndo();var oldFolder=selected.Folder;selected.Source=path;selected.Folder=Path.GetFileName(path);
        if(selected.Name==oldFolder)selected.Name=selected.Folder;
        var bytes=snapshot.Files.Where(f=>GameScanner.Within(f.Path,path)).Sum(f=>f.Length);
        selected.Size=FormatBytes(bytes);selected.Gb=bytes/(1024d*1024*1024);
        checkpoints.RemoveAll(c=>c.PackageId==selected.Id);checkpoints.Add(WorkspaceReconciler.Capture(selected,snapshot));
        WorkspaceReconciler.Check(selected,checkpoints.Last(),snapshot);Render();Say("이동 범위를 변경했습니다 · 파일 변경 없음");
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
        catch(Exception ex) when(ex is IOException or System.ComponentModel.Win32Exception){Info("폴더 확인",ex.Message);}
    }
    void ShowReadOnlyPlan(List<Package> list)
    {
        var win=Modal("이동 계획 확인",840,720);var dock=new DockPanel {Margin=new Thickness(28)};
        var header=new StackPanel();header.Children.Add(Text("이동 계획 확인",24));
        header.Children.Add(new TextBlock {Text=$"패키지 {list.Count}개 · 추가 백업 공간 약 {list.Sum(p=>p.Gb):0.##} GB",Margin=new Thickness(0,10,0,10)});
        header.Children.Add(Text("백업 위치",12));var backupPath=Text(backupStorageRoot,12,"#7D8FA9");header.Children.Add(backupPath);
        header.Children.Add(Btn("백업 위치 변경",()=>{ChooseBackupRoot();backupPath.Text=backupStorageRoot;}));
        DockPanel.SetDock(header,Dock.Top);dock.Children.Add(header);
        var footer=new StackPanel {Margin=new Thickness(0,18,0,0)};DockPanel.SetDock(footer,Dock.Bottom);
        var warning=Text("게임 종료 후 진행하세요. 검증된 백업은 직접 삭제할 때까지 보관합니다.",12,"#8B795C");footer.Children.Add(warning);
        footer.Children.Add(Text("다른 드라이브는 복사 공간이 더 필요합니다. 작업 중 폴더를 직접 변경하지 마세요.",12,"#8B795C"));
        var buttons=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
        buttons.Children.Add(Btn("계속 편집",()=>win.Close()));
        buttons.Children.Add(Btn("백업만 실행",async()=>{win.Close();await StartBackupBatchAsync(list);}));
        var move=Btn("백업 후 이동",async()=>
        {
            var problems=list.Select(p=>BackupEngine.MoveNameError(p.Name)).FirstOrDefault(s=>s!=null);
            if(problems!=null){warning.Text=problems;return;}
            var paths=list.Select(p=>Path.Combine(destination,p.Name)).ToList();
            if(paths.Any(path=>path.Any(ch=>ch>127))&&MessageBox.Show(win,"이동할 경로에 한글·일본어 등 비ASCII 문자가 포함되어 있습니다. 일부 게임이 실행되지 않을 수 있습니다.\n이 경로로 이동할까요?","경로 문자 확인",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
            win.Close();await StartMoveBatchAsync(list);
        },true);
        move.IsEnabled=!backupBusy&&!list.Any(p=>p.ReviewState.Length>0);buttons.Children.Add(move);
        footer.Children.Add(buttons);dock.Children.Add(footer);
        var rows=new StackPanel();foreach(var p in list)
        {
            var content=new StackPanel();content.Children.Add(Text(string.IsNullOrWhiteSpace(p.Alias)?p.Folder:p.Alias,16));
            content.Children.Add(Text("원본: "+p.Source,12,"#7E8FA8"));content.Children.Add(Text("목적지: "+destination,12,"#7E8FA8"));
            var name=new TextBox {Text=p.Name,Margin=new Thickness(0,8,0,4),ToolTip="이동 후 폴더명 · 내부 폴더명은 유지됩니다"};
            var nameStatus=Text("",12,"#A77B40");
            void CheckName(){var problem=BackupEngine.MoveNameError(p.Name);if(problem!=null){nameStatus.Text=problem;return;}var path=Path.Combine(destination,p.Name);nameStatus.Text=File.Exists(path)||Directory.Exists(path)?"같은 이름이 있습니다 · 다른 이름을 입력하세요":ValidateName(p.Name);}
            name.TextChanged+=(_,_)=>{p.Name=name.Text;CheckName();QueueWorkspaceSave();};CheckName();content.Children.Add(name);content.Children.Add(nameStatus);
            if(p.ReviewState.Length>0)content.Children.Add(Text("확인 필요: "+p.ReviewReason,12,"#A77B40"));
            rows.Children.Add(new Border {Child=content,Padding=new Thickness(14),Background=B("#F4F7FC"),CornerRadius=new CornerRadius(7),Margin=new Thickness(0,0,0,12)});
        }
        dock.Children.Add(new ScrollViewer {Content=rows});win.Content=dock;win.ShowDialog();Render();
    }
}
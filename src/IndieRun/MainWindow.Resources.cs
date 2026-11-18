using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
namespace IndieRun;
public partial class MainWindow
{
    bool HasResources(Package p)=>p.Contents.Any(c=>c.Role=="related")||p.RelatedFolders.Count>0;
    Window BuildResources(Package original)
    {
        var win=Modal("관련 자료",960,700);win.MinWidth=820;win.MinHeight=580;var root=new DockPanel{Margin=new Thickness(26)};
        var title=WorkText(PlayerRules.Title(original),21,"Text.Primary");title.TextWrapping=TextWrapping.NoWrap;title.TextTrimming=TextTrimming.CharacterEllipsis;title.Margin=new Thickness(0,0,0,18);DockPanel.SetDock(title,Dock.Top);root.Children.Add(title);
        var footer=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,16,0,0)};DockPanel.SetDock(footer,Dock.Bottom);var close=Btn("닫기",()=>win.Close(),true);close.IsCancel=true;footer.Children.Add(close);root.Children.Add(footer);
        var columns=new Grid();columns.ColumnDefinitions.Add(new(){Width=new GridLength(230)});columns.ColumnDefinitions.Add(new(){Width=new GridLength(20)});columns.ColumnDefinitions.Add(new());root.Children.Add(columns);
        var list=new ListBox{Name="ResourceList",BorderThickness=new Thickness(0),Background=ThemeBrush("Surface.Primary"),HorizontalContentAlignment=HorizontalAlignment.Stretch};list.Resources.Add(typeof(ListBoxItem),FindResource("ContentListItem"));ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);columns.Children.Add(list);
        var right=new DockPanel();Grid.SetColumn(right,2);columns.Children.Add(right);var heading=new StackPanel();DockPanel.SetDock(heading,Dock.Top);right.Children.Add(heading);var resourceTitle=WorkText("자료",17,"Text.Primary");heading.Children.Add(resourceTitle);
        var files=new ComboBox{Name="ResourceFiles",DisplayMemberPath="Label",Margin=new Thickness(0,12,0,12),MinHeight=36};heading.Children.Add(files);
        var lower=new StackPanel();DockPanel.SetDock(lower,Dock.Bottom);right.Children.Add(lower);var status=WorkText("",12,"Text.Muted");status.Name="ResourceStatus";lower.Children.Add(status);
        var buttons=new WrapPanel{Margin=new Thickness(0,12,0,6)};var native=Btn("기본 앱으로 열기",()=>{});var folder=Btn("폴더 열기",()=>{});var reconnect=Btn("위치 다시 선택",()=>{});buttons.Children.Add(native);buttons.Children.Add(folder);buttons.Children.Add(reconnect);lower.Children.Add(buttons);
        var manage=new Expander{Header="자료 관리",Margin=new Thickness(0,8,0,0)};var fields=new StackPanel{Margin=new Thickness(0,10,0,0)};manage.Content=fields;lower.Children.Add(manage);
        var aliasRow=new DockPanel();var saveAlias=Btn("저장",()=>{});saveAlias.Margin=new Thickness(8,0,0,0);DockPanel.SetDock(saveAlias,Dock.Right);aliasRow.Children.Add(saveAlias);var alias=new TextBox{MaxLength=120,ToolTip="표시 이름 · 실제 폴더명은 유지됩니다."};aliasRow.Children.Add(alias);fields.Children.Add(WorkText("표시 이름",11,"Text.Muted"));fields.Children.Add(aliasRow);
        var management=new WrapPanel{Margin=new Thickness(0,10,0,0)};var rename=Btn("폴더명 변경",()=>{});var detach=Btn("연결 해제",()=>{});management.Children.Add(rename);management.Children.Add(detach);fields.Children.Add(management);var pathDetails=WorkDetails("","실제 위치");fields.Children.Add(pathDetails);
        var preview=new ContentControl{Name="ResourcePreview",Margin=new Thickness(0,0,0,14)};right.Children.Add(preview);ResourceEntry? entry=null;int revision=0;bool closed=false,restoringSelection=false;string savedAlias="";
        bool AliasDirty()=>entry!=null&&alias.Text!=savedAlias;
        bool LeaveAlias(){if(!AliasDirty())return true;if(Choose("표시 이름 수정","저장하지 않은 표시 이름이 있습니다.",[new("discard","저장 안 함"),new("edit","계속 편집",true)],"edit",tone:DialogTone.Warning,owner:win)!="discard")return false;alias.Text=savedAlias;return true;}
        alias.TextChanged+=(_,_)=>saveAlias.IsEnabled=AliasDirty();
        void Reload(string? id=null)
        {
            list.Items.Clear();var game=packages.Find(p=>p.Id==original.Id)??original;foreach(var e in ResourceRules.Entries(game,packages))
            {var row=new StackPanel();var label=OrganizeLabel(e.Title,14);label.ToolTip=e.Title;row.Children.Add(label);var type=OrganizeLabel(Directory.Exists(e.Path)?e.Kind:"위치 확인 필요",11,Directory.Exists(e.Path)?"Text.Muted":"Warning.Text");type.Margin=new Thickness(0,5,0,0);row.Children.Add(type);var item=new ListBoxItem{Content=row,Tag=e,Padding=new Thickness(12),Margin=new Thickness(0,0,0,6)};list.Items.Add(item);}
            var selected=list.Items.Cast<ListBoxItem>().FirstOrDefault(i=>((ResourceEntry)i.Tag).Id==id)??list.Items.Cast<ListBoxItem>().FirstOrDefault();if(selected!=null)list.SelectedItem=selected;else{entry=null;preview.Content=WorkText("연결된 자료가 없습니다.",13,"Text.Muted");heading.IsEnabled=lower.IsEnabled=false;}
        }
        async Task ShowFile()
        {
            var version=++revision;if(entry==null||files.SelectedItem is not ResourceFile f)return;var path=f.Path;native.IsEnabled=ResourceRules.CanOpen(path);status.Text="읽는 중…";
            try
            {
                if(!GameScanner.Within(path,entry.Path))throw new IOException("자료 범위 밖의 파일입니다.");BackupPaths.NoLinks(path);
                if(ReadOnlyPreview.IsText(path)){var text=await Task.Run(()=>ReadOnlyPreview.ReadText(path));if(closed||version!=revision)return;preview.Content=new TextBox{Text=text.Text,IsReadOnly=true,AcceptsReturn=true,VerticalContentAlignment=VerticalAlignment.Top,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(14)};status.Text=text.Encoding+" · 읽기 전용"+(text.Truncated?" · 앞부분 256 KB":"");}
                else if(ReadOnlyPreview.IsImage(path))
                {
                    var bitmap=await Task.Run(()=>{using var stream=File.OpenRead(path);if(stream.Length>50L*1024*1024)throw new IOException("50 MB 이하의 이미지만 미리 볼 수 있습니다.");var img=new BitmapImage();img.BeginInit();img.CacheOption=BitmapCacheOption.OnLoad;img.DecodePixelWidth=1200;img.StreamSource=stream;img.EndInit();img.Freeze();return img;});if(closed||version!=revision)return;preview.Content=new Image{Source=bitmap,Stretch=Stretch.Uniform};status.Text="이미지 · 읽기 전용";
                }
                else{preview.Content=WorkText(ResourceRules.CanOpen(path)?"기본 앱으로 열어 확인할 수 있습니다.":"자동으로 열지 않는 파일 형식입니다. 폴더에서 확인하세요.",13,"Text.Muted");status.Text="파일 변경 없음";}
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or NotSupportedException or FormatException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException){if(closed||version!=revision)return;preview.Content=WorkText("미리 보지 못했습니다. 파일 위치와 접근 권한을 확인하세요.",13,"Warning.Text");status.Text="폴더에서 확인할 수 있습니다.";status.ToolTip=ex.Message;}
        }
        list.SelectionChanged+=async (_,_)=>
        {
            if(restoringSelection||list.SelectedItem is not ListBoxItem item)return;
            if(AliasDirty()&&!LeaveAlias()){restoringSelection=true;list.SelectedItem=list.Items.Cast<ListBoxItem>().FirstOrDefault(i=>((ResourceEntry)i.Tag).Id==entry!.Id);restoringSelection=false;return;}
            entry=(ResourceEntry)item.Tag;var selectedEntry=entry;var version=++revision;resourceTitle.Text=entry.Title;savedAlias=entry.Title;alias.Text=entry.Title;saveAlias.IsEnabled=false;pathDetails.Content=WorkText(entry.Path,12);files.ItemsSource=null;preview.Content=WorkText("자료를 확인하고 있습니다…",13,"Text.Muted");status.Text="";native.IsEnabled=false;var exists=Directory.Exists(entry.Path);folder.IsEnabled=rename.IsEnabled=exists;reconnect.Visibility=!exists&&entry.External?Visibility.Visible:Visibility.Collapsed;
            if(!exists){preview.Content=WorkText("자료 위치를 찾지 못했습니다. 연결은 유지됩니다.",13,"Warning.Text");return;}
            try{var scan=await Task.Run(()=>GameScanner.Scan(selectedEntry.Path));if(closed||version!=revision)return;var available=scan.Files.Where(f=>Path.GetFileName(f.Path)!=LibraryStore.MarkerName).OrderBy(f=>ReadOnlyPreview.IsText(f.Path)?0:ReadOnlyPreview.IsImage(f.Path)?1:2).ThenBy(f=>f.Path).Take(500).Select(f=>new ResourceFile(f.Path,Path.GetRelativePath(selectedEntry.Path,f.Path),ReadOnlyPreview.IsText(f.Path)?"문서":ReadOnlyPreview.IsImage(f.Path)?"이미지":Path.GetExtension(f.Path).TrimStart('.').ToUpperInvariant())).ToList();files.ItemsSource=available;if(available.Count>0)files.SelectedIndex=0;else{preview.Content=WorkText("표시할 파일이 없습니다.",13,"Text.Muted");status.Text=scan.Issues.Count>0?"일부 위치의 접근 권한을 확인하세요.":"빈 폴더";}}
            catch(Exception ex)when(LibraryFault(ex)){preview.Content=WorkText("폴더 위치와 접근 권한을 확인하세요.",13,"Warning.Text");status.ToolTip=ex.Message;}
        };
        files.SelectionChanged+=async (_,_)=>await ShowFile();folder.Click+=(_,_)=>{if(entry!=null)OpenSourceFolder(entry.Path);};
        native.Click+=(_,_)=>{if(entry==null||files.SelectedItem is not ResourceFile f||!ResourceRules.CanOpen(f.Path))return;try{BackupPaths.NoLinks(f.Path);if(!GameScanner.Within(f.Path,entry.Path))throw new IOException("자료 범위를 확인하세요.");Process.Start(new ProcessStartInfo(f.Path){UseShellExecute=true});}catch(Exception ex)when(ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException){ErrorInfo("자료 열기","파일과 기본 앱 연결을 확인하세요.",ex.Message);}};
        saveAlias.Click+=(_,_)=>{if(entry==null)return;try{var next=ResourceRules.Alias(packages,original.Id,entry,alias.Text);if(CommitPlayerEdit(next,CopyCatalog(),Clone(playerDocument),next.Select(p=>p.Id))){savedAlias=alias.Text;Reload(entry.Id);}}catch(Exception ex)when(LibraryFault(ex)){status.Text=ex.Message;}};
        detach.Click+=(_,_)=>{if(entry==null||!LeaveAlias()||!Confirm("자료 연결 해제","연결만 해제합니다. 실제 파일과 다른 게임의 연결은 유지됩니다.","연결 해제",owner:win))return;var next=ResourceRules.Detach(packages,original.Id,entry);if(CommitPlayerEdit(next,CopyCatalog(),Clone(playerDocument),next.Select(p=>p.Id)))Reload();};
        rename.Click+=(_,_)=>{if(entry!=null&&LeaveAlias())RenameFolder(packages.Find(p=>p.Id==original.Id)??original,entry.Path,win);};
        reconnect.Click+=async (_,_)=>
        {
            if(entry==null||!entry.External||!LeaveAlias())return;var picker=new OpenFolderDialog{Title="자료의 현재 폴더 선택"};if(picker.ShowDialog(win)!=true)return;var chosen=entry;try{var scan=await Task.Run(()=>GameScanner.Scan(picker.FolderName));if(scan.Issues.Count>0)throw new IOException("자료를 모두 확인하지 못했습니다.");var next=ResourceRules.Relink(packages,entry.Id,picker.FolderName);var plan=Clone(CaptureWorkspace());plan.Packages=next;var asset=next.Single(p=>p.Id==chosen.Id);plan.Checkpoints.RemoveAll(c=>c.PackageId==asset.Id);plan.Checkpoints.Add(WorkspaceReconciler.Capture(asset,scan));SearchLocationRules.Add(plan.SearchLocations!,asset.Source);var merged=snapshot!;merged.Files.RemoveAll(f=>GameScanner.Within(f.Path,asset.Source));merged.Files.AddRange(scan.Files);merged.Directories.AddRange(scan.Directories.Where(d=>!merged.Directories.Contains(d,StringComparer.OrdinalIgnoreCase)));if(CommitPlayerEdit(next,CopyCatalog(),Clone(playerDocument),next.Where(p=>p.Id==asset.Id||p.RelatedFolders.Any(r=>r.Id==asset.Id)).Select(p=>p.Id),prepared:plan,reconnectedScan:merged))Reload(chosen.Id);}catch(Exception ex)when(LibraryFault(ex)){ErrorInfo("자료 위치 확인","원래 자료의 폴더인지 확인한 뒤 다시 선택하세요.",ex.Message);}
        };
        win.Closing+=(_,e)=>{if(!LeaveAlias())e.Cancel=true;};win.Closed+=(_,_)=>{closed=true;revision++;};win.Content=root;Reload();return win;
    }
    sealed record ResourceFile(string Path,string Name,string Kind){public string Label=>Name+" · "+Kind;}
    void ShowResources(Package p)=>BuildResources(p).ShowDialog();
    void RemoveGame(Package p)
    {
        if(!Confirm("목록에서 제거","게임 등록과 자료 연결만 해제합니다. 실제 파일과 다른 게임의 자료 연결은 유지됩니다.","목록에서 제거",destructive:true))return;
        if(!FlushWorkspace())return;var next=ResourceRules.RemoveGame(packages,p.Id);var plan=Clone(CaptureWorkspace());plan.Packages=next;
        try{Edits().Commit(new(){Plan=plan,Tags=CopyCatalog(),Play=Clone(playerDocument),EditedIds=next.Select(p=>p.Id).ToList(),RemovedIds=[p.Id]});ApplyWorkspace(workspaceStore.Load().Document!);libraryDocument=Library().Load();lastSavedSignature=null;Render();if(page=="play")RenderPlayer();Say("목록에서 제거했습니다 · 실제 파일은 유지했습니다.");}catch(Exception ex)when(LibraryFault(ex)){workspaceBlocked=File.Exists(Edits().Pending);ErrorInfo("목록 변경 확인","편집 기록을 보관했습니다. 저장을 복구한 뒤 다시 확인하세요.",ex.Message);}
    }
}

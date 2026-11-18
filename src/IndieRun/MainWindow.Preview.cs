using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
namespace IndieRun;
public partial class MainWindow
{
    void ShowRealPreview(){if(snapshot!=null&&selected.IsReal)BuildPreview(selected,snapshot).ShowDialog();}
    Window BuildPreview(Package package,ScanSnapshot scan)
    {
        var files=PreviewFiles.Candidates(package,scan);var recommended=PreviewFiles.Recommended(package,files);bool showAll=false;var win=Modal("내용 보기",1040,750);win.MinWidth=820;win.MinHeight=580;
        var dock=new DockPanel{Margin=new Thickness(26)};var heading=new StackPanel();heading.Children.Add(WorkText(PlayerRules.Title(package),22,"Text.Primary"));heading.Children.Add(CompactPath(package.Source));DockPanel.SetDock(heading,Dock.Top);dock.Children.Add(heading);
        var footer=new DockPanel{LastChildFill=false,Margin=new Thickness(0,16,0,0)};DockPanel.SetDock(footer,Dock.Bottom);var close=Btn("닫기",()=>win.Close());close.IsCancel=true;DockPanel.SetDock(close,Dock.Right);footer.Children.Add(close);var launch=Btn("게임 실행",async()=>await PreviewGameAsync(package,win),true);footer.Children.Add(launch);dock.Children.Add(footer);
        var grid=new Grid{Margin=new Thickness(0,16,0,0)};grid.ColumnDefinitions.Add(new(){Width=new GridLength(270)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(20)});grid.ColumnDefinitions.Add(new());dock.Children.Add(grid);
        var sidebar=new DockPanel();grid.Children.Add(sidebar);var filters=new StackPanel();DockPanel.SetDock(filters,Dock.Top);sidebar.Children.Add(filters);
        var search=new TextBox{Margin=new Thickness(0,0,0,10)};AutomationProperties.SetName(search,"내용 파일 검색");filters.Children.Add(WithHint(search,"파일 이름 검색"));
        var folders=new ComboBox{Margin=new Thickness(0,0,0,10)};folders.Items.Add("전체 폴더");foreach(var dir in files.Select(f=>Path.GetDirectoryName(f.Path)!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d=>d).Take(250))folders.Items.Add(new ComboBoxItem{Content=RelativePreviewPath(package,dir),Tag=dir,ToolTip=dir});folders.SelectedIndex=0;AutomationProperties.SetName(folders,"내용 폴더 선택");filters.Children.Add(folders);
        var exe=Btn("실행 후보 보기",()=>{});exe.Margin=new Thickness(0,0,0,10);var all=Btn("전체 문서·이미지 보기",()=>{});all.Margin=new Thickness(0,0,0,10);filters.Children.Add(all);filters.Children.Add(new Expander{Header="더 보기",Content=exe,Margin=new Thickness(0,0,0,10)});var count=WorkText("",11,"Text.Muted");count.Margin=new Thickness(0,0,0,8);filters.Children.Add(count);
        var list=new ListBox{BorderThickness=new Thickness(0),Padding=new Thickness(0)};list.SetResourceReference(BackgroundProperty,"Surface.Primary");AutomationProperties.SetName(list,"내용 파일 목록");ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);sidebar.Children.Add(list);
        var view=new DockPanel();Grid.SetColumn(view,2);grid.Children.Add(view);var bar=new DockPanel{Margin=new Thickness(0,0,0,12)};DockPanel.SetDock(bar,Dock.Top);var filename=WorkText("",15,"Text.Primary");filename.FontWeight=FontWeights.SemiBold;
        var encoding=new ComboBox{Width=126,ItemsSource=new[]{"자동","UTF-8","일본어 CP932","한국어 CP949","UTF-16 LE"},SelectedIndex=0,Margin=new Thickness(12,0,0,0),Visibility=Visibility.Collapsed};AutomationProperties.SetName(encoding,"문서 인코딩");DockPanel.SetDock(encoding,Dock.Right);bar.Children.Add(encoding);filename.TextTrimming=TextTrimming.CharacterEllipsis;filename.TextWrapping=TextWrapping.NoWrap;filename.VerticalAlignment=VerticalAlignment.Center;bar.Children.Add(filename);view.Children.Add(bar);
        var bottom=new StackPanel{Margin=new Thickness(0,12,0,0)};DockPanel.SetDock(bottom,Dock.Bottom);var status=WorkText("",11,"Text.Muted");bottom.Children.Add(status);var actions=new WrapPanel{Margin=new Thickness(0,10,0,0)};
        var open=Btn("기본 앱으로 열기",()=>{});var folder=Btn("폴더 열기",()=>{});var use=Btn("카드 이미지로 사용",()=>{});actions.Children.Add(open);actions.Children.Add(folder);actions.Children.Add(use);bottom.Children.Add(actions);view.Children.Add(bottom);var body=new ContentControl();view.Children.Add(body);win.Content=dock;
        string? current=null;bool closed=false;int revision=0;bool applying=false;
        void Buttons(){open.IsEnabled=current!=null&&ResourceRules.CanOpen(current)&&File.Exists(current);folder.IsEnabled=Directory.Exists(current==null?package.Source:Path.GetDirectoryName(current));use.Visibility=current!=null&&ReadOnlyPreview.IsImage(current)?Visibility.Visible:Visibility.Collapsed;use.IsEnabled=!applying&&current!=null&&File.Exists(current);}
        async Task ShowFile(string path)
        {
            var version=++revision;current=path;filename.Text=Path.GetFileName(path);filename.ToolTip=RelativePreviewPath(package,path);status.Text="읽는 중…";body.Content=null;Buttons();
            try
            {
                if(!PreviewFiles.InScope(package,path))throw new IOException("현재 범위 밖의 파일입니다.");BackupPaths.NoLinks(path);
                if(ReadOnlyPreview.IsText(path))
                {
                    encoding.Visibility=Visibility.Visible;var mode=encoding.SelectedItem as string??"자동";var text=await Task.Run(()=>ReadOnlyPreview.ReadText(path,mode));if(closed||version!=revision)return;
                    var box=new TextBox{Text=text.Text,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalContentAlignment=VerticalAlignment.Top,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(14),FontSize=13};box.SetResourceReference(BackgroundProperty,"Surface.Secondary");box.SetResourceReference(ForegroundProperty,"Text.Primary");body.Content=box;status.Text=text.Encoding+" · 읽기 전용"+(text.Truncated?" · 앞부분 256 KB 표시":"");
                }
                else
                {
                    encoding.Visibility=Visibility.Collapsed;var bitmap=await Task.Run(()=>PreviewFiles.Image(path,1200));if(closed||version!=revision)return;var frame=new Border{Child=new Image{Source=bitmap,Stretch=Stretch.Uniform}};frame.SetResourceReference(BackgroundProperty,"Surface.Secondary");body.Content=frame;status.Text="이미지 · 읽기 전용";
                }
            }
            catch(Exception ex)when(PreviewFault(ex)){if(closed||version!=revision)return;body.Content=WorkText("미리 볼 수 없습니다. 폴더에서 파일을 확인하세요.",13,"Warning.Text");status.Text="파일 변경 없음";status.ToolTip=ex.Message;}
        }
        async Task Thumb(Image image,string path){try{var bitmap=await Task.Run(()=>PreviewFiles.Image(path,128));if(!closed)image.Source=bitmap;}catch(Exception ex)when(PreviewFault(ex)){image.ToolTip="미리보기 없음";}}
        void Fill()
        {
            var previous=current;list.Items.Clear();var dir=(folders.SelectedItem as ComboBoxItem)?.Tag as string;var effectiveAll=showAll||dir!=null||search.Text.Length>0;all.Content=effectiveAll?"추천 파일 보기":"전체 문서·이미지 보기";var matching=(effectiveAll?files:recommended).Where(f=>(dir==null||string.Equals(Path.GetDirectoryName(f.Path),dir,StringComparison.OrdinalIgnoreCase))&&Path.GetFileName(f.Path).Contains(search.Text,StringComparison.OrdinalIgnoreCase)).ToList();count.Text=(effectiveAll?"파일 ":"추천 ")+matching.Count+"개"+(files.Count==500?" · 최대 500개 표시":"");
            foreach(var file in matching)
            {
                var row=new Grid();row.ColumnDefinitions.Add(new(){Width=new GridLength(58)});row.ColumnDefinitions.Add(new());var thumb=new Border{Width=48,Height=42,CornerRadius=new CornerRadius(4),Margin=new Thickness(0,0,10,0)};thumb.SetResourceReference(BackgroundProperty,"Surface.Secondary");
                if(ReadOnlyPreview.IsImage(file.Path)){var image=new Image{Stretch=Stretch.UniformToFill};thumb.Child=image;image.Loaded+=async(_,_)=>{if(image.Source==null)await Thumb(image,file.Path);};}else{var icon=WorkText("문서",11,"Text.Muted");icon.VerticalAlignment=VerticalAlignment.Center;icon.HorizontalAlignment=HorizontalAlignment.Center;thumb.Child=icon;}row.Children.Add(thumb);
                var name=new StackPanel{VerticalAlignment=VerticalAlignment.Center};var text=WorkText(Path.GetFileName(file.Path),12,"Text.Primary");text.TextTrimming=TextTrimming.CharacterEllipsis;text.TextWrapping=TextWrapping.NoWrap;name.Children.Add(text);var path=WorkText(!effectiveAll?PreviewFiles.Recommendation(package,file.Path)??"":RelativePreviewPath(package,Path.GetDirectoryName(file.Path)!),10,"Text.Muted");path.TextWrapping=TextWrapping.NoWrap;path.TextTrimming=TextTrimming.CharacterEllipsis;name.Children.Add(path);Grid.SetColumn(name,1);row.Children.Add(name);list.Items.Add(new ListBoxItem{Content=row,Tag=file.Path,Padding=new Thickness(10),Margin=new Thickness(0,0,0,8),ToolTip=RelativePreviewPath(package,file.Path),Style=(Style)FindResource("ContentListItem")});
            }
            var item=list.Items.OfType<ListBoxItem>().FirstOrDefault(i=>i.Tag as string==previous)??list.Items.OfType<ListBoxItem>().FirstOrDefault();if(item!=null)list.SelectedItem=item;
            else{revision++;current=null;encoding.Visibility=Visibility.Collapsed;filename.Text="확인할 파일 없음";body.Content=WorkText(!effectiveAll?"추천할 이미지나 문서가 없습니다. 전체 목록을 보거나 게임을 실행해 확인하세요.":files.Count==0?"미리 볼 이미지나 문서가 없습니다. 폴더를 열거나 게임을 실행해 확인하세요.":"검색 조건을 바꿔보세요.",13,"Text.Muted");status.Text="";Buttons();}
        }
        list.SelectionChanged+=async(_,_)=>{if(list.SelectedItem is ListBoxItem item&&item.Tag is string path)await ShowFile(path);};search.TextChanged+=(_,_)=>Fill();folders.SelectionChanged+=(_,_)=>Fill();encoding.SelectionChanged+=async(_,_)=>{if(current!=null&&ReadOnlyPreview.IsText(current))await ShowFile(current);};
        all.Click+=(_,_)=>{if(showAll||folders.SelectedIndex>0||search.Text.Length>0){showAll=false;search.Text="";folders.SelectedIndex=0;}else showAll=true;Fill();};
        exe.Click+=(_,_)=>{revision++;current=null;list.SelectedItem=null;encoding.Visibility=Visibility.Collapsed;filename.Text="실행 후보";var rows=new StackPanel();foreach(var file in Executables(package)){rows.Children.Add(WorkText(Path.GetRelativePath(package.Source,file.Path),14,"Text.Primary"));var label=WorkText(GameScanner.Auxiliary(file.Path)?"보조 프로그램으로 추정":"게임 실행 후보",12,"Text.Muted");label.Margin=new Thickness(0,4,0,18);rows.Children.Add(label);}if(rows.Children.Count==0)rows.Children.Add(WorkText("실행 후보가 없습니다.",13,"Text.Muted"));body.Content=new ScrollViewer{Content=rows};status.Text="자동으로 실행하지 않습니다.";Buttons();};
        open.Click+=(_,_)=>{if(current==null||!ResourceRules.CanOpen(current))return;try{BackupPaths.NoLinks(current);Process.Start(new ProcessStartInfo(current){UseShellExecute=true});}catch(Exception ex)when(PreviewFault(ex)||ex is System.ComponentModel.Win32Exception){ErrorInfo("파일 열기","파일을 열지 못했습니다.",ex.Message);}};
        folder.Click+=(_,_)=>OpenSourceFolder(current==null?package.Source:Path.GetDirectoryName(current)!);
        use.Click+=async(_,_)=>
        {
            if(current==null||applying)return;var image=current;applying=true;Buttons();
            try
            {
                if(!PreviewFiles.InScope(package,image))throw new IOException("현재 범위 밖의 이미지입니다.");if(!FlushWorkspace())return;var stored=await Task.Run(()=>PreviewFiles.CopyArtwork(image,destination));var draft=Clone(playerDocument);var game=draft.Games.Find(g=>g.Id==package.Id);if(game==null){game=new(){Id=package.Id};draft.Games.Add(game);}game.Image=stored;game.CropX=game.CropY=.5;
                if(CommitPlayerEdit(Clone(packages),CopyCatalog(),draft,[package.Id])){artworkCache.Clear();status.Text="카드 이미지로 저장했습니다.";if(page=="play")RenderPlayerItems();}
            }
            catch(Exception ex)when(PreviewFault(ex)){ErrorInfo("이미지 저장","카드 이미지를 저장하지 못했습니다.",ex.Message);}finally{applying=false;Buttons();}
        };
        win.Closing+=(_,e)=>{if(applying)e.Cancel=true;};win.Closed+=(_,_)=>{closed=true;revision++;};Fill();return win;
    }
    static bool PreviewFault(Exception ex)=>LibraryFault(ex)||ex is NotSupportedException or FormatException or InvalidOperationException or System.Runtime.InteropServices.COMException;
    static string RelativePreviewPath(Package p,string path)
    {var r=p.RelatedFolders.FirstOrDefault(r=>GameScanner.Within(path,r.Source));var relative=Path.GetRelativePath(r?.Source??p.Source,path);return relative=="."?(r==null?p.Folder:ContentLinks.Title(r)):(r==null?relative:ContentLinks.Title(r)+"/"+relative);}
}


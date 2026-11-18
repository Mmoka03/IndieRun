using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IndieRun;

public partial class MainWindow
{
    void ShowRealPreview()
    {
        if(snapshot==null||!selected.IsReal)return;
        var package=selected;
        var files=snapshot.Files.Where(f=>(package.Includes(f.Path)||package.RelatedFolders.Any(r=>GameScanner.Within(f.Path,r.Source)))&&(ReadOnlyPreview.IsText(f.Path)||ReadOnlyPreview.IsImage(f.Path)))
            .OrderBy(f=>ReadOnlyPreview.IsText(f.Path)?0:1)
            .ThenBy(f=>Path.GetFileName(f.Path).Contains("readme",StringComparison.OrdinalIgnoreCase)?0:1)
            .ThenBy(f=>Path.GetRelativePath(package.Source,f.Path).Count(c=>c=='\\'))
            .ThenBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).Take(500).ToList();
        var win=Modal("내용 확인 · 읽기 전용",1000,730);win.MinWidth=800;
        var dock=new DockPanel {Margin=new Thickness(24)};
        var heading=new StackPanel();heading.Children.Add(Text(string.IsNullOrWhiteSpace(package.Alias)?package.Folder:package.Alias,22));
        heading.Children.Add(new TextBlock {Text=package.Source,FontSize=12,Foreground=B("#8796AD"),TextTrimming=TextTrimming.CharacterEllipsis,ToolTip=package.Source,Margin=new Thickness(0,7,0,17)});
        DockPanel.SetDock(heading,Dock.Top);dock.Children.Add(heading);
        var footer=new DockPanel {LastChildFill=false,Margin=new Thickness(0,18,0,0)};DockPanel.SetDock(footer,Dock.Bottom);
        var close=Btn("작업대로 돌아가기",()=>win.Close(),true);close.IsCancel=true;DockPanel.SetDock(close,Dock.Right);footer.Children.Add(close);
        var openFolder=Btn("원본 폴더 열기",()=>OpenSourceFolder(package.Source));footer.Children.Add(openFolder);dock.Children.Add(footer);
        var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(250)});grid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(20)});grid.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar=new DockPanel();
        var execButton=new Button {Content=$"실행 파일 {Executables(package).Count}개 보기"};DockPanel.SetDock(execButton,Dock.Top);execButton.Margin=new Thickness(0,0,0,12);sidebar.Children.Add(execButton);
        var list=new ListBox {BorderBrush=B("#E2E8F2"),BorderThickness=new Thickness(1),Background=B("#FAFBFE"),Padding=new Thickness(4)};AutomationProperties.SetName(list,"미리보기 파일");
        foreach(var file in files)
        {
            var related=package.RelatedFolders.FirstOrDefault(r=>GameScanner.Within(file.Path,r.Source));var name=related==null?Path.GetRelativePath(package.Source,file.Path):related.Name+"/"+Path.GetRelativePath(related.Source,file.Path);
            list.Items.Add(new ListBoxItem {Content=new TextBlock {Text=name,TextTrimming=TextTrimming.CharacterEllipsis,ToolTip=name,FontSize=12},Tag=file.Path,Padding=new Thickness(8,8,8,8)});
        }
        sidebar.Children.Add(list);grid.Children.Add(sidebar);
        var view=new DockPanel();Grid.SetColumn(view,2);grid.Children.Add(view);
        var toolbar=new DockPanel {Margin=new Thickness(0,0,0,12)};DockPanel.SetDock(toolbar,Dock.Top);
        var encoding=new ComboBox {MinWidth=130,Padding=new Thickness(7,5,7,5),Margin=new Thickness(8,0,0,0)};AutomationProperties.SetName(encoding,"문서 인코딩");
        foreach(var mode in new[]{"자동","UTF-8","일본어 CP932","한국어 CP949","UTF-16 LE"})encoding.Items.Add(mode);
        encoding.SelectedIndex=0;DockPanel.SetDock(encoding,Dock.Right);toolbar.Children.Add(encoding);
        var filename=new TextBlock {FontWeight=FontWeights.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};toolbar.Children.Add(filename);view.Children.Add(toolbar);
        var status=Text("",11,"#8594AA");status.Margin=new Thickness(0,10,0,0);DockPanel.SetDock(status,Dock.Bottom);view.Children.Add(status);
        var body=new ContentControl();view.Children.Add(body);dock.Children.Add(grid);win.Content=dock;
        string? currentFile=null;int revision=0;bool closed=false;
        void ShowExecutables()
        {
            revision++;currentFile=null;filename.Text="실행 파일 후보";encoding.Visibility=Visibility.Collapsed;
            var rows=new StackPanel();foreach(var exe in Executables(package))
            {
                var item=new StackPanel {Margin=new Thickness(0,0,0,18)};
                item.Children.Add(Text(Path.GetRelativePath(package.Source,exe.Path),15));
                item.Children.Add(Text(GameScanner.Auxiliary(exe.Path)?"보조 프로그램으로 추정":"실행 후보 · 사용자 확인 필요",12,"#7F91AB"));rows.Children.Add(item);
            }
            body.Content=new ScrollViewer {Content=rows};status.Text="실행 파일은 자동으로 실행하지 않습니다.";
        }
        async Task ShowFileAsync(string path)
        {
            var version=++revision;currentFile=path;filename.Text=Path.GetFileName(path);filename.ToolTip=path;status.Text="읽는 중…";
            try
            {
                if(!GameScanner.Within(path,package.Source))throw new IOException("현재 범위 밖의 파일입니다.");
                if(ReadOnlyPreview.IsText(path))
                {
                    encoding.Visibility=Visibility.Visible;var mode=encoding.SelectedItem as string??"자동";
                    var preview=await Task.Run(()=>ReadOnlyPreview.ReadText(path,mode));
                    if(closed||version!=revision)return;
                    body.Content=new TextBox {Text=preview.Text,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=B("#F8FAFD"),BorderBrush=B("#EDF0F5"),Padding=new Thickness(14),FontFamily=new FontFamily("Malgun Gothic"),FontSize=13};
                    status.Text=$"{preview.Encoding} · 읽기 전용"+(preview.Truncated?" · 앞부분 256 KB 표시":"");
                }
                else if(ReadOnlyPreview.IsImage(path))
                {
                    encoding.Visibility=Visibility.Collapsed;
                    var image=await Task.Run(()=>
                    {
                        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                        if(stream.Length>50*1024*1024)throw new IOException("50 MB를 넘는 이미지는 원본 폴더에서 확인해 주세요.");
                        var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=1200;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();return bitmap;
                    });
                    if(closed||version!=revision)return;
                    body.Content=new Border {Background=B("#F5F7FB"),Child=new Image {Source=image,Stretch=Stretch.Uniform}};status.Text="이미지 미리보기 · 읽기 전용";
                }
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException or InvalidOperationException or System.Text.DecoderFallbackException or System.Runtime.InteropServices.COMException)
            {if(closed||version!=revision)return;body.Content=Text("이 파일을 미리 볼 수 없습니다. 원본 폴더에서 확인해 주세요.",13,"#9B805A");status.Text="파일 변경 없음";status.ToolTip=ex.Message;}
        }
        execButton.Click+=(_,_)=>ShowExecutables();
        list.SelectionChanged+=async (_,_)=>{if(list.SelectedItem is ListBoxItem item&&item.Tag is string path)await ShowFileAsync(path);};
        encoding.SelectionChanged+=async (_,_)=>{if(currentFile!=null&&ReadOnlyPreview.IsText(currentFile))await ShowFileAsync(currentFile);};
        win.Closed+=(_,_)=>{closed=true;revision++;};
        if(list.Items.Count>0)list.SelectedIndex=0;else ShowExecutables();
        win.ShowDialog();
    }
}

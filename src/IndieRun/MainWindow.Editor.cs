using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;

namespace IndieRun;

public partial class MainWindow
{
    void OrganizeEdit_Click(object sender,RoutedEventArgs e)
    {if(HasSelection&&EditGames([selected]))Render();}
    sealed record ExeOption(string Path)
    {public override string ToString()=>(GameScanner.Auxiliary(Path)?"보조 추정 · ":"")+Path;}
    bool EditGames(List<Package> targets,bool firstLaunch=false)
    {
        if(targets.Count==0||backupBusy||scanning)return false;
        if(playerRoot!=destination)LoadPlayer();
        if(File.Exists(Edits().Pending)){Info("편집 저장","실행 화면에서 이전 편집 저장을 먼저 복구하세요.");return false;}
        saveTimer.Stop();if(!FlushWorkspace())return false;
        foreach(var targetGame in targets)PlayFor(targetGame);
        var drafts=Clone(targets);var draftTags=CopyCatalog();var draftPlay=Clone(playerDocument);bool single=targets.Count==1,saved=false;
        var additions=new HashSet<string>();var removals=new HashSet<string>();string? newImage=null;
        var win=Modal(single?"게임 정보 편집":$"게임 {targets.Count}개 태그 편집",single?920:840,790);
        var dock=new DockPanel {Margin=new Thickness(28)};
        var heading=new StackPanel {Margin=new Thickness(0,0,0,18)};heading.Children.Add(Text(single?PlayerRules.Title(targets[0]):$"게임 {targets.Count}개 편집",23));
        heading.Children.Add(Text(single?"변경 사항은 저장할 때 적용됩니다":"추가·제거할 태그만 변경하고 나머지는 유지합니다",12,"#8898AD"));DockPanel.SetDock(heading,Dock.Top);dock.Children.Add(heading);
        var bottom=new StackPanel {Margin=new Thickness(0,16,0,0)};var summary=Text("",12,"#647FA8");bottom.Children.Add(summary);
        var error=Text("",12,"#AB7651");bottom.Children.Add(error);var buttons=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,10,0,0)};
        var cancel=Btn("취소",()=>win.Close());buttons.Children.Add(cancel);
        DockPanel.SetDock(bottom,Dock.Bottom);bottom.Children.Add(buttons);dock.Children.Add(bottom);
        var columns=new Grid();columns.ColumnDefinitions.Add(new ColumnDefinition());if(single)columns.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(278)});
        var form=new StackPanel {Margin=new Thickness(0,0,single?24:0,0)};columns.Children.Add(form);
        var p=drafts[0];var game=draftPlay.Games.Single(g=>g.Id==p.Id);TextBox? alias=null;Border? preview=null;
        void UpdatePreview()
        {
            if(preview!=null)
            {
                var canvas=new Grid {Width=240,Height=166,Background=Artwork(game,240,166,newImage)??B("#283C60")};canvas.Children.Add(new Border {Background=B("#071222"),Opacity=.58});
                canvas.Children.Add(new TextBlock {Text=string.IsNullOrWhiteSpace(p.Alias)?p.Folder:p.Alias,FontSize=17,FontWeight=FontWeights.SemiBold,Foreground=Brushes.White,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(14)});preview.Child=canvas;
            }
            if(single)summary.Text="별칭 · 분류 · 이미지 · 실행 설정을 함께 저장합니다";
            else summary.Text=$"추가: {string.Join(", ",draftTags.Tags.Where(t=>additions.Contains(t.Id)).Select(t=>t.Name))} · 제거: {string.Join(", ",draftTags.Tags.Where(t=>removals.Contains(t.Id)).Select(t=>t.Name))} → 게임 {targets.Count}개에 적용";
        }
        if(single)
        {
            form.Children.Add(Text("별칭",13));alias=new TextBox {Text=p.Alias,Margin=new Thickness(0,7,0,16)};AutomationProperties.SetName(alias,"편집 별칭");alias.TextChanged+=(_,_)=>{p.Alias=alias.Text;UpdatePreview();};form.Children.Add(alias);
            var right=new StackPanel();Grid.SetColumn(right,1);columns.Children.Add(right);right.Children.Add(Text("카드 미리보기",13));preview=new Border {Margin=new Thickness(0,8,0,12),HorizontalAlignment=HorizontalAlignment.Left,CornerRadius=new CornerRadius(9),ClipToBounds=true};right.Children.Add(preview);
            right.Children.Add(Btn("이미지 선택 · 교체",()=>
            {
                var picker=new OpenFileDialog {Title="카드 배경 이미지 선택",Filter="이미지|*.png;*.jpg;*.jpeg;*.bmp;*.gif"};if(picker.ShowDialog(win)!=true)return;
                if(Artwork(game,240,166,picker.FileName)==null){error.Text="이미지를 읽지 못했습니다. PNG/JPG/BMP/GIF, 50MB 이하를 선택하세요.";return;}
                newImage=picker.FileName;error.Text="";UpdatePreview();
            }));
            right.Children.Add(Btn("이미지 제거",()=>{newImage=null;game.Image="";UpdatePreview();}));
            foreach(var horizontal in new[]{true,false})
            {
                right.Children.Add(new TextBlock {Text=horizontal?"가로 위치":"세로 위치",FontSize=12,Foreground=B("#8091AA"),Margin=new Thickness(0,16,0,6)});
                var slider=new Slider {Minimum=0,Maximum=1,Value=horizontal?game.CropX:game.CropY};AutomationProperties.SetName(slider,horizontal?"이미지 가로 위치":"이미지 세로 위치");
                slider.ValueChanged+=(_,_)=>{if(horizontal)game.CropX=slider.Value;else game.CropY=slider.Value;UpdatePreview();};right.Children.Add(slider);
            }
        }
        var search=new TextBox {ToolTip="편집할 태그 검색",Margin=new Thickness(0,0,0,10)};AutomationProperties.SetName(search,"편집 태그 검색");form.Children.Add(WithHint(search,"태그 검색"));
        var choices=new StackPanel();form.Children.Add(choices);
        void RenderChoices()
        {
            choices.Children.Clear();
            foreach(var operation in single?new[]{"선택"}:new[]{"추가할 태그","제거할 태그"})
            {
                if(!single)choices.Children.Add(new TextBlock {Text=operation,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,8,0,8)});
                foreach(var kind in new[]{"genre","category"})
                {
                    choices.Children.Add(new TextBlock {Text=KindLabel(kind),FontSize=12,Foreground=B("#8594AA"),Margin=new Thickness(0,5,0,6)});var strip=new StackPanel {Orientation=Orientation.Horizontal};
                    foreach(var tag in draftTags.Tags.Where(t=>t.Kind==kind&&t.Name.Contains(search.Text,StringComparison.OrdinalIgnoreCase)))
                    {
                        var values=kind=="genre"?p.Genres:p.Categories;var selectedTag=single?values.Contains(tag.Name):(operation=="추가할 태그"?additions:removals).Contains(tag.Id);
                        var chip=new ToggleButton {Content=tag.Name,IsChecked=selectedTag,Style=(Style)FindResource("TagChip")};AutomationProperties.SetName(chip,operation+" "+tag.Name);
                        chip.Click+=(_,_)=>
                        {
                            if(single){if(chip.IsChecked==true&&!values.Contains(tag.Name))values.Add(tag.Name);else if(chip.IsChecked!=true)values.Remove(tag.Name);}
                            else {var set=operation=="추가할 태그"?additions:removals;var other=operation=="추가할 태그"?removals:additions;if(chip.IsChecked==true){set.Add(tag.Id);other.Remove(tag.Id);}else set.Remove(tag.Id);RenderChoices();}
                            UpdatePreview();
                        };strip.Children.Add(chip);
                    }
                    choices.Children.Add(new ScrollViewer {Content=strip,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled});
                }
            }
        }
        search.TextChanged+=(_,_)=>RenderChoices();RenderChoices();
        var creator=new DockPanel {Margin=new Thickness(0,16,0,16)};var kindBox=new ComboBox {Width=100,ItemsSource=new[]{"장르","카테고리"},SelectedIndex=0};DockPanel.SetDock(kindBox,Dock.Left);creator.Children.Add(kindBox);
        var tagName=new TextBox {MaxLength=40,ToolTip="새 태그 이름"};AutomationProperties.SetName(tagName,"편집 새 태그 이름");
        var add=Btn("＋ 태그 추가",()=>
        {
            var name=tagName.Text.Trim();var kind=kindBox.SelectedIndex==0?"genre":"category";
            if(name.Length==0||name.Any(char.IsControl)){error.Text="태그 이름을 확인하세요.";return;}
            if(draftTags.Tags.Any(t=>t.Kind==kind&&t.Name.Equals(name,StringComparison.OrdinalIgnoreCase))){error.Text="이미 있는 태그입니다.";return;}
            var tag=new TagDefinition {Name=name,Kind=kind};draftTags.Tags.Add(tag);
            if(single)(kind=="genre"?p.Genres:p.Categories).Add(name);else additions.Add(tag.Id);
            error.Text="";tagName.Text="";RenderChoices();UpdatePreview();
        });DockPanel.SetDock(add,Dock.Right);creator.Children.Add(add);creator.Children.Add(WithHint(tagName,"새 태그 이름"));form.Children.Add(creator);
        if(single)
        {
            var launch=new StackPanel();launch.Children.Add(Text("기본 실행 파일",13));
            var options=Executables(targets[0]).Select(f=>Path.GetRelativePath(targets[0].Source,f.Path)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(GameScanner.Auxiliary).ThenBy(n=>n).Select(n=>new ExeOption(n)).ToList();
            if(game.Executable.Length>0&&!options.Any(o=>o.Path==game.Executable))options.Insert(0,new ExeOption(game.Executable));
            var exe=new ComboBox {ItemsSource=options,Margin=new Thickness(0,7,0,10),MinHeight=34};exe.SelectedItem=options.Find(o=>o.Path==game.Executable)??(firstLaunch?options.FirstOrDefault():null);if(firstLaunch&&exe.SelectedItem is ExeOption auto)game.Executable=auto.Path;
            AutomationProperties.SetName(exe,"기본 실행 파일");exe.SelectionChanged+=(_,_)=>{if(exe.SelectedItem is ExeOption option)game.Executable=option.Path;};launch.Children.Add(exe);
            launch.Children.Add(Btn("직접 선택",()=>{var picker=new OpenFileDialog {Title="이 게임 폴더 안의 실행 파일 선택",Filter="실행 파일|*.exe",InitialDirectory=targets[0].Source};if(picker.ShowDialog(win)!=true)return;var relative=Path.GetRelativePath(targets[0].Source,picker.FileName);if(!BackupPaths.Relative(relative)){error.Text="게임 폴더 안의 실행 파일을 선택하세요.";return;}game.Executable=relative;var option=new ExeOption(relative);options.Add(option);exe.ItemsSource=null;exe.ItemsSource=options;exe.SelectedItem=option;}));
            var advanced=new StackPanel();advanced.Children.Add(Text("실행 인자",12));var args=new TextBox {Text=game.Arguments,Margin=new Thickness(0,6,0,12)};args.TextChanged+=(_,_)=>game.Arguments=args.Text;advanced.Children.Add(args);
            advanced.Children.Add(Text("작업 폴더 · 비우면 실행 파일이 있는 폴더",12));var working=new TextBox {Text=game.WorkingDirectory,ToolTip="게임 폴더 기준 상대 경로 · . 은 게임 폴더",Margin=new Thickness(0,6,0,0)};working.TextChanged+=(_,_)=>game.WorkingDirectory=working.Text;advanced.Children.Add(working);
            launch.Children.Add(new Expander {Header="고급 설정",Content=advanced,Margin=new Thickness(0,14,0,0)});form.Children.Add(new Expander {Header="실행 설정",Content=launch,IsExpanded=firstLaunch,Margin=new Thickness(0,10,0,0)});
        }
        dock.Children.Add(new ScrollViewer {Content=columns});win.Content=dock;UpdatePreview();
        string Signature()=>JsonSerializer.Serialize(new{drafts,draftTags,draftPlay,newImage,additions=additions.Order(),removals=removals.Order()});var initial=Signature();
        var save=Btn(firstLaunch?"저장 후 실행":single?"저장":$"{targets.Count}개에 적용",()=>
        {
            try
            {
                if(firstLaunch){if(game.Executable.Length==0)throw new IOException("실행 파일을 지정하세요.");PlayerStore.StartInfo(targets[0],game);}
                if(newImage!=null)
                {
                    var folder=Path.Combine(destination,".indierun-art");BackupPaths.NoLinks(folder);Directory.CreateDirectory(folder);
                    var name=Guid.NewGuid().ToString("N")+Path.GetExtension(newImage).ToLowerInvariant();var output=Path.Combine(folder,name);BackupPaths.NoLinks(newImage);
                    using(var input=new FileStream(newImage,FileMode.Open,FileAccess.Read,FileShare.Read))using(var target=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None)){if(input.Length>50L*1024*1024)throw new IOException("이미지는 50MB 이하로 선택하세요.");input.CopyTo(target);target.Flush(true);}
                    game.Image=".indierun-art/"+name;
                }
                if(!single)foreach(var draft in drafts)PlayerRules.ApplyTags(draft,draftTags.Tags,additions,removals);
                PlayerStore.Validate(draftPlay);var next=Clone(packages);foreach(var draft in drafts){var index=next.FindIndex(q=>q.Id==draft.Id);if(index>=0)next[index]=draft;}
                if(!CommitPlayerEdit(next,draftTags,draftPlay,targets.Select(t=>t.Id)))return;
                saved=true;win.Close();
            }
            catch(Exception ex) when(LibraryFault(ex)){error.Text=ex.Message;}
        },true);buttons.Children.Add(save);
        win.Closing+=(_,e)=>{if(!saved&&Signature()!=initial&&!DiscardChanges(win))e.Cancel=true;};
        win.ShowDialog();return saved;
    }
    bool DiscardChanges(Window owner)
    {
        bool discard=false;var win=Modal("변경 사항",440,230);win.MinWidth=440;win.MinHeight=230;win.Owner=owner;
        var stack=new StackPanel {Margin=new Thickness(25)};stack.Children.Add(Text("저장하지 않은 변경 사항이 있습니다",17));
        var row=new WrapPanel {Margin=new Thickness(0,24,0,0),HorizontalAlignment=HorizontalAlignment.Right};row.Children.Add(Btn("계속 편집",()=>win.Close()));row.Children.Add(Btn("변경 버리기",()=>{discard=true;win.Close();}));stack.Children.Add(row);win.Content=stack;win.ShowDialog();return discard;
    }
}

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
    bool EditGames(List<Package> targets,bool firstLaunch=false,bool launchOnly=false,bool advancedLaunch=false,bool batchTags=false)
    {
        if(targets.Count==0||backupBusy||scanning)return false;
        if(playerRoot!=destination)LoadPlayer();
        if(File.Exists(Edits().Pending)){Info("편집 저장","실행 화면에서 이전 편집 저장을 먼저 복구하세요.");return false;}
        saveTimer.Stop();if(!FlushWorkspace())return false;
        foreach(var targetGame in targets)PlayFor(targetGame);
        var drafts=Clone(targets);var draftTags=CopyCatalog();var draftPlay=Clone(playerDocument);bool single=targets.Count==1&&!batchTags,saved=false;
        var additions=new HashSet<string>();var removals=new HashSet<string>();string? newImage=null;
        var win=Modal(single?"게임 정보 편집":$"게임 {targets.Count}개 태그 편집",single?980:1040,790);win.MinWidth=single?900:940;win.MinHeight=660;
        var dock=new DockPanel {Margin=new Thickness(28)};
        var heading=new StackPanel {Margin=new Thickness(0,0,0,18)};heading.Children.Add(Text(single?PlayerRules.Title(targets[0]):$"게임 {targets.Count}개 · 태그 편집",23));
        if(!single)heading.Children.Add(WorkText("태그 보유 수 · 현재 → 적용 후",12,"Text.Muted"));DockPanel.SetDock(heading,Dock.Top);dock.Children.Add(heading);
        var bottom=new StackPanel {Margin=new Thickness(0,16,0,0)};var summary=Text("",12,"#647FA8");if(!single)bottom.Children.Add(summary);
        var error=Text("",12,"#AB7651");bottom.Children.Add(error);var buttons=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,10,0,0)};
        var cancel=Btn("취소",()=>win.Close());cancel.IsCancel=true;buttons.Children.Add(cancel);
        DockPanel.SetDock(bottom,Dock.Bottom);bottom.Children.Add(buttons);dock.Children.Add(bottom);
        var columns=new Grid();columns.ColumnDefinitions.Add(new ColumnDefinition());columns.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(single?276:256)});
        var left=new DockPanel {Margin=new Thickness(0,0,26,0)};columns.Children.Add(left);
        var nav=new WrapPanel {Margin=new Thickness(0,0,0,18)};DockPanel.SetDock(nav,Dock.Top);left.Children.Add(nav);
        var pages=new Grid();left.Children.Add(pages);var form=new StackPanel();var launch=new StackPanel();
        var infoScroll=new ScrollViewer {Content=form,Style=(Style)FindResource("EditorScroll")};var launchScroll=new ScrollViewer {Content=launch,Style=(Style)FindResource("EditorScroll"),Visibility=Visibility.Collapsed};pages.Children.Add(infoScroll);pages.Children.Add(launchScroll);
        if(single)
        {
            var infoTab=new Button {Content="게임 정보",Padding=new Thickness(14,8,14,8),Margin=new Thickness(0,0,8,0)};var launchTab=new Button {Content="실행 설정",Padding=new Thickness(14,8,14,8)};
            void Switch(bool showLaunch){infoScroll.Visibility=showLaunch?Visibility.Collapsed:Visibility.Visible;launchScroll.Visibility=showLaunch?Visibility.Visible:Visibility.Collapsed;foreach(var item in new[]{infoTab,launchTab}){var active=(item==launchTab)==showLaunch;item.Background=B(active?"#EAF0FE":"#FFFFFF");item.Foreground=B(active?"#4167C7":"#8593A8");item.BorderBrush=B(active?"#D9E4FB":"#E6EBF3");}}
            infoTab.Click+=(_,_)=>Switch(false);launchTab.Click+=(_,_)=>Switch(true);nav.Children.Add(infoTab);nav.Children.Add(launchTab);Switch(firstLaunch||launchOnly);
        }
        else nav.Visibility=Visibility.Collapsed;
        var p=drafts[0];var game=draftPlay.Games.Single(g=>g.Id==p.Id);TextBox? alias=null;Border? preview=null;StackPanel? batchPreview=null;Button? applyButton=null;TextBox search=null!;StackPanel choices=null!;
        void UpdatePreview()
        {
            if(preview!=null)
            {
                var canvas=new Grid {Width=240,Height=166,Background=Artwork(game,240,166,newImage)??ThemeBrush("Card.Default"),Clip=new RectangleGeometry(new Rect(0,0,240,166),9,9)};canvas.Children.Add(new Border {Background=ThemeBrush("Card.Shade")});
                canvas.Children.Add(new TextBlock {Text=string.IsNullOrWhiteSpace(p.Alias)?p.Folder:p.Alias,FontSize=16,FontWeight=FontWeights.SemiBold,Foreground=ThemeBrush("Card.Title"),TextWrapping=TextWrapping.Wrap,MaxHeight=48,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(16,16,42,0)});canvas.Children.Add(new TextBlock {Text="⋯",FontSize=22,Foreground=ThemeBrush("Card.Title"),HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,7,14,0)});canvas.Children.Add(new TextBlock {Text="▶  실행",FontSize=12,Foreground=ThemeBrush("Card.Text"),VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(16)});preview.Child=canvas;
            }
            if(single)summary.Text="별칭 · 분류 · 이미지 · 실행 설정을 함께 저장합니다";
            else
            {
                summary.Text=additions.Count+removals.Count==0?"변경할 태그를 선택하세요.":$"게임 {targets.Count}개에 적용 · 추가 태그 {additions.Count}개 · 제거 태그 {removals.Count}개";
                if(applyButton!=null)applyButton.IsEnabled=additions.Count+removals.Count>0;
                if(batchPreview!=null)
                {
                    batchPreview.Children.Clear();batchPreview.Children.Add(WorkText("변경 내용",17,"Text.Primary"));
                    foreach(var addMode in new[]{true,false})
                    {
                        var set=addMode?additions:removals;var section=new StackPanel();section.Children.Add(WorkText(addMode?"＋ 추가할 태그":"− 제거할 태그",13,addMode?"Success.Text":"Danger.Text"));
                        if(set.Count==0)section.Children.Add(WorkText("없음",12,"Text.Muted"));
                        foreach(var tag in draftTags.Tags.Where(t=>set.Contains(t.Id)))
                        {
                            var button=Btn("",()=>{set.Remove(tag.Id);RenderChoices();UpdatePreview();});button.Content=new TextBlock {Text=tag.Name+$" · {TagBatchRules.Affected(targets,tag,addMode)}개  ×",TextWrapping=TextWrapping.Wrap};button.Margin=new Thickness(0,7,0,0);button.ToolTip="이 태그의 변경 취소";AutomationProperties.SetName(button,tag.Name+" 변경 취소");section.Children.Add(button);
                        }
                        var card=WorkCard(section,addMode?"Success.Surface":"Danger.Surface");card.Margin=new Thickness(0,12,0,0);batchPreview.Children.Add(card);
                    }
                }
            }
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
            var removeImage=Btn("이미지 제거",()=>{newImage=null;game.Image="";UpdatePreview();});removeImage.Margin=new Thickness(0,8,8,0);removeImage.BorderThickness=new Thickness(0);removeImage.Background=Brushes.Transparent;removeImage.Foreground=B("#8393A9");right.Children.Add(removeImage);
            foreach(var horizontal in new[]{true,false})
            {
                right.Children.Add(new TextBlock {Text=horizontal?"가로 위치":"세로 위치",FontSize=12,Foreground=B("#8091AA"),Margin=new Thickness(0,16,0,6)});
                var slider=new Slider {Minimum=0,Maximum=1,Value=horizontal?game.CropX:game.CropY};AutomationProperties.SetName(slider,horizontal?"이미지 가로 위치":"이미지 세로 위치");
                slider.ValueChanged+=(_,_)=>{if(horizontal)game.CropX=slider.Value;else game.CropY=slider.Value;UpdatePreview();};right.Children.Add(slider);
            }
        }
        if(!single){batchPreview=new StackPanel();var review=new ScrollViewer {Content=batchPreview};Grid.SetColumn(review,1);columns.Children.Add(review);}
        search=new TextBox {ToolTip="편집할 태그 검색",Width=210,HorizontalAlignment=HorizontalAlignment.Left};AutomationProperties.SetName(search,"편집 태그 검색");var searchHint=WithHint(search,"태그 이름 검색");searchHint.HorizontalAlignment=HorizontalAlignment.Left;searchHint.Visibility=Visibility.Collapsed;
        var tagHeader=new DockPanel {Margin=new Thickness(0,3,0,10)};var findTags=Btn("›  태그 찾기",()=>{var open=searchHint.Visibility!=Visibility.Visible;searchHint.Visibility=open?Visibility.Visible:Visibility.Collapsed;if(!open)search.Text="";});findTags.Foreground=B("#7E8FA9");findTags.BorderThickness=new Thickness(0);findTags.Background=Brushes.Transparent;findTags.Padding=new Thickness(0,4,0,4);findTags.Margin=new Thickness(0);DockPanel.SetDock(findTags,Dock.Right);tagHeader.Children.Add(findTags);var tagTitle=Text("태그",13);tagTitle.VerticalAlignment=VerticalAlignment.Center;tagHeader.Children.Add(tagTitle);form.Children.Add(tagHeader);form.Children.Add(searchHint);
        choices=new StackPanel();form.Children.Add(choices);
        void RenderChoices()
        {
            var offset=infoScroll.VerticalOffset;choices.Children.Clear();
            if(!single)
            {
                foreach(var kind in new[]{"genre","category"})
                {
                    var title=WorkText(KindLabel(kind),12,"Text.Muted");title.Margin=new Thickness(0,8,0,8);choices.Children.Add(title);
                    foreach(var tag in draftTags.Tags.Where(t=>t.Kind==kind&&t.Name.Contains(search.Text,StringComparison.OrdinalIgnoreCase)))
                    {
                        var present=TagBatchRules.Present(targets,tag);var mode=additions.Contains(tag.Id)?"add":removals.Contains(tag.Id)?"remove":"keep";
                        var row=new Grid {Margin=new Thickness(0,0,0,8)};row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(78)});row.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
                        var name=WorkText(tag.Name,13,"Text.Primary");name.TextWrapping=TextWrapping.NoWrap;name.TextTrimming=TextTrimming.CharacterEllipsis;name.ToolTip=tag.Name;name.VerticalAlignment=VerticalAlignment.Center;name.Margin=new Thickness(0,0,8,0);row.Children.Add(name);
                        var amount=WorkText(mode=="keep"?$"{present}/{targets.Count}개":$"{present} → {(mode=="add"?targets.Count:0)}개",12,"Text.Muted");amount.VerticalAlignment=VerticalAlignment.Center;amount.ToolTip="태그를 가진 게임 수 · 현재 → 적용 후";Grid.SetColumn(amount,1);row.Children.Add(amount);
                        var actions=new StackPanel {Orientation=Orientation.Horizontal};Grid.SetColumn(actions,2);
                        foreach(var action in new[]{("keep","유지"),("add","추가"),("remove","제거")})
                        {
                            var button=Btn(action.Item2,()=>{additions.Remove(tag.Id);removals.Remove(tag.Id);if(action.Item1=="add")additions.Add(tag.Id);else if(action.Item1=="remove")removals.Add(tag.Id);RenderChoices();UpdatePreview();});
                            button.Padding=new Thickness(10,7,10,7);button.Margin=new Thickness(4,0,0,0);button.IsEnabled=action.Item1!="add"||present<targets.Count; if(action.Item1=="remove"&&present==0)button.IsEnabled=false;
                            AutomationProperties.SetName(button,tag.Name+" "+action.Item2);
                            if(mode==action.Item1){var role=mode=="add"?"Success":mode=="remove"?"Danger":"Accent";button.SetResourceReference(BackgroundProperty,role+".Surface");button.SetResourceReference(ForegroundProperty,role+".Text");button.SetResourceReference(BorderBrushProperty,role+".Text");}
                            actions.Children.Add(button);
                        }
                        row.Children.Add(actions);choices.Children.Add(row);
                    }
                }
                infoScroll.ScrollToVerticalOffset(offset);return;
            }
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
        var creator=new DockPanel {Margin=new Thickness(0,16,0,16)};var kindBox=new ComboBox {Width=106,Margin=new Thickness(0,0,8,0),ItemsSource=new[]{"장르","카테고리"},SelectedIndex=0};AutomationProperties.SetName(kindBox,"새 태그 종류");DockPanel.SetDock(kindBox,Dock.Left);creator.Children.Add(kindBox);
        var tagName=new TextBox {MaxLength=40,ToolTip="새 태그 이름"};AutomationProperties.SetName(tagName,"편집 새 태그 이름");
        var add=Btn("＋ 태그 추가",()=>
        {
            var name=tagName.Text.Trim();var kind=kindBox.SelectedIndex==0?"genre":"category";
            if(name.Length==0||name.Any(char.IsControl)){error.Text="태그 이름을 확인하세요.";return;}
            if(draftTags.Tags.Any(t=>t.Kind==kind&&t.Name.Equals(name,StringComparison.OrdinalIgnoreCase))){error.Text="이미 있는 태그입니다.";return;}
            var tag=new TagDefinition {Name=name,Kind=kind};draftTags.Tags.Add(tag);
            if(single)(kind=="genre"?p.Genres:p.Categories).Add(name);else additions.Add(tag.Id);
            error.Text="";tagName.Text="";RenderChoices();UpdatePreview();
        });add.Margin=new Thickness(8,0,0,0);add.Padding=new Thickness(10,8,10,8);DockPanel.SetDock(add,Dock.Right);creator.Children.Add(add);creator.Children.Add(WithHint(tagName,"새 태그 이름"));form.Children.Add(creator);
        if(single)
        {
            launch.Children.Add(Text("기본 실행 파일",13));
            var options=Executables(targets[0]).Select(f=>Path.GetRelativePath(targets[0].Source,f.Path)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(GameScanner.Auxiliary).ThenBy(n=>n).Select(n=>new ExeOption(n)).ToList();
            if(game.Executable.Length>0&&!options.Any(o=>o.Path==game.Executable))options.Insert(0,new ExeOption(game.Executable));
            var exe=new ComboBox {ItemsSource=options,Margin=new Thickness(0,9,0,10),MinHeight=36};exe.SelectedItem=options.Find(o=>o.Path==game.Executable);
            AutomationProperties.SetName(exe,"기본 실행 파일");exe.SelectionChanged+=(_,_)=>{if(exe.SelectedItem is ExeOption option)game.Executable=option.Path;};launch.Children.Add(exe);
            launch.Children.Add(Btn("직접 선택",()=>{var picker=new OpenFileDialog {Title="이 게임 폴더 안의 실행 파일 선택",Filter="실행 파일|*.exe",InitialDirectory=targets[0].Source};if(picker.ShowDialog(win)!=true)return;var relative=Path.GetRelativePath(targets[0].Source,picker.FileName);if(!BackupPaths.Relative(relative)){error.Text="게임 폴더 안의 실행 파일을 선택하세요.";return;}game.Executable=relative;var option=new ExeOption(relative);options.Add(option);exe.ItemsSource=null;exe.ItemsSource=options;exe.SelectedItem=option;}));
            var advanced=new StackPanel();advanced.Children.Add(Text("실행 인자",12));var args=new TextBox {Text=game.Arguments,Margin=new Thickness(0,6,0,12)};args.TextChanged+=(_,_)=>game.Arguments=args.Text;advanced.Children.Add(args);
            advanced.Children.Add(Text("작업 폴더 · 비우면 실행 파일이 있는 폴더",12));var working=new TextBox {Text=game.WorkingDirectory,ToolTip="게임 폴더 기준 상대 경로 · . 은 게임 폴더",Margin=new Thickness(0,6,0,0)};working.TextChanged+=(_,_)=>game.WorkingDirectory=working.Text;advanced.Children.Add(working);
            advanced.Margin=new Thickness(0,12,0,0);launch.Children.Add(new Expander {Header="고급 설정",Content=advanced,IsExpanded=advancedLaunch,Margin=new Thickness(0,22,0,0),Foreground=B("#788CA8")});
        }
        dock.Children.Add(columns);win.Content=dock;UpdatePreview();
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
        },true);buttons.Children.Add(save);applyButton=save;UpdatePreview();
        win.Closing+=(_,e)=>{if(!saved&&Signature()!=initial&&!DiscardChanges(win))e.Cancel=true;};
        win.ShowDialog();return saved;
    }
    bool DiscardChanges(Window owner)
        =>Choose("편집을 종료할까요?","저장하지 않은 내용은 적용되지 않습니다.",[new("discard","저장 안 함"),new("edit","계속 편집",true)],"edit",tone:DialogTone.Warning,owner:owner)=="discard";
}


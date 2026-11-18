using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace IndieRun;

public partial class MainWindow
{
    TagCatalog catalog = TagCatalog.Initial();
    readonly TagCatalogStore tagStore = new(Path.Combine(AppContext.BaseDirectory,"data","tags.json"));
    string tagLoadError = "";
    string page = "organize";

    void LoadTags()
    {
        try { catalog=tagStore.Load(); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // A damaged catalog is never silently overwritten by initial values.
            tagLoadError=ex.Message;
            catalog=new TagCatalog();
        }
    }

    bool StoreCatalog(TagCatalog next, TextBlock error)
    {
        if(tagLoadError.Length>0) { error.Text="태그 파일을 확인해 주세요."; error.ToolTip=tagLoadError; return false; }
        try { tagStore.Save(next);catalog=next;return true; }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        { error.Text="저장하지 못했습니다. 위치와 권한을 확인해 주세요.";error.ToolTip=ex.Message;return false; }
    }
    TagCatalog CopyCatalog() => JsonSerializer.Deserialize<TagCatalog>(JsonSerializer.Serialize(catalog))!;
    static string KindLabel(string kind) => kind=="genre"?"장르":"카테고리";
    string? CheckTagName(string kind,string value,string? except=null)
    {
        if(string.IsNullOrWhiteSpace(value))return "이름을 입력해 주세요.";
        if(value.Length>40)return "40자 이내로 입력해 주세요.";
        if(value.Any(char.IsControl))return "줄바꿈이나 제어 문자는 사용할 수 없습니다.";
        return catalog.Tags.Any(t=>t.Kind==kind && t.Id!=except && t.Name.Equals(value,StringComparison.OrdinalIgnoreCase))?"이미 있는 태그입니다.":null;
    }
    void FillTags(WrapPanel panel,string kind,List<string> values)
    {
        panel.Children.Clear();
        foreach(var tag in catalog.Tags.Where(t=>t.Kind==kind))
        {
            var label=new TextBlock {Text=tag.Name,MaxWidth=180,TextTrimming=TextTrimming.CharacterEllipsis};
            var chip=new ToggleButton {Content=label,Style=(Style)FindResource("TagChip"),IsChecked=values.Contains(tag.Name),ToolTip=tag.Name};
            AutomationProperties.SetName(chip,$"{KindLabel(kind)}: {tag.Name}");
            chip.Click+=(_,_)=>{SaveUndo();if(chip.IsChecked==true){if(!values.Contains(tag.Name))values.Add(tag.Name);}else values.Remove(tag.Name);QueueWorkspaceSave();Say("");};
            panel.Children.Add(chip);
        }
        var add=Btn(catalog.Tags.Any(t=>t.Kind==kind)?"＋ 추가":$"＋ {KindLabel(kind)} 만들기",()=>ShowTagCreator(kind,kind=="genre"?GenreCreator:CategoryCreator,true));
        add.Margin=new Thickness(0,0,7,7);add.Padding=new Thickness(10,6,10,6);add.Foreground=B("#6D84AB");add.BorderBrush=B("#D9E3F2");
        panel.Children.Add(add);
    }

    void ShowTagCreator(string kind,StackPanel host,bool apply)
    {
        host.Children.Clear();
        var row=new Grid {Margin=new Thickness(0,4,0,6)};
        row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        var input=new TextBox {MaxLength=40,MinWidth=110};AutomationProperties.SetName(input,$"새 {KindLabel(kind)} 이름");row.Children.Add(input);
        var error=Text("",12,"#AD6A39");
        void Commit()
        {
            var value=input.Text.Trim();var validation=CheckTagName(kind,value);
            if(validation!=null){error.Text=validation;return;}
            var next=CopyCatalog();next.Tags.Add(new TagDefinition {Kind=kind,Name=value});
            if(!StoreCatalog(next,error))return;
            if(apply){SaveUndo();(kind=="genre"?selected.Genres:selected.Categories).Add(value);}
            Render();if(page=="tags")RenderTagManager();Say("태그를 저장했습니다");
        }
        var save=Btn("추가",Commit,true);save.Margin=new Thickness(8,0,0,0);Grid.SetColumn(save,1);row.Children.Add(save);
        var close=Btn("취소",()=>host.Children.Clear());close.Margin=new Thickness(5,0,0,0);Grid.SetColumn(close,2);row.Children.Add(close);
        input.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){e.Handled=true;Commit();}else if(e.Key==Key.Escape){e.Handled=true;host.Children.Clear();}};
        host.Children.Add(row);host.Children.Add(error);input.Focus();
    }

    void SetPage(string target)
    {
        page=target;
        Workbench.Visibility=target=="organize"?Visibility.Visible:Visibility.Collapsed;
        JobsPanel.Visibility=target=="jobs"?Visibility.Visible:Visibility.Collapsed;
        TagsPanel.Visibility=target=="tags"?Visibility.Visible:Visibility.Collapsed;
        FooterActions.Visibility=target=="organize"?Visibility.Visible:Visibility.Collapsed;
        foreach(var pair in new[]{(OrganizeNav,"organize"),(TagsNav,"tags"),(JobsNav,"jobs")})
        {pair.Item1.Background=target==pair.Item2?B("#344D7C"):Brushes.Transparent;pair.Item1.Foreground=target==pair.Item2?Brushes.White:B("#B8C5DC");}
        PageTitle.Text=target=="tags"?"태그 관리":target=="jobs"?"이동 · 백업":"게임 정리";
        PageSubtitle.Text=target=="tags"?"이름 수정과 삭제는 여기에서":target=="jobs"?"진행 결과와 남겨둔 백업":"선택하고, 확인하고, 보관함에 담기";
        if(target=="tags") {PlanCount.Text="정리 일시 중지";PlanHint.Text="현재 계획은 유지됩니다";Say("");}
        else if(target=="jobs") {PlanCount.Text="백업 · 복원 기록";PlanHint.Text="백업은 직접 삭제 전까지 보관";Say("");}
        else RenderPlan();
    }
    void Tags_Click(object sender,RoutedEventArgs e)
    {
        if(running)return;
        SetPage("tags");RenderTagManager();
    }
    void RenderTagManager()
    {
        QueueWorkspaceSave();
        TagsContent.Children.Clear();
        var header=new DockPanel {Margin=new Thickness(0,0,0,28)};
        var back=Btn("정리로 돌아가기 →",()=>Organize_Click(this,new RoutedEventArgs()),true);DockPanel.SetDock(back,Dock.Right);header.Children.Add(back);
        var status=new StackPanel();status.Children.Add(Text("Ⅱ  정리 일시 중지",17));status.Children.Add(new TextBlock {Text="계획은 그대로 유지됩니다",Foreground=B("#8C98AA"),FontSize=12,Margin=new Thickness(0,6,0,0)});header.Children.Add(status);TagsContent.Children.Add(header);
        if(tagLoadError.Length>0){var error=Text("태그 파일을 읽지 못했습니다. 원본 파일을 확인해 주세요.",13,"#AD6A39");error.ToolTip=tagLoadError;TagsContent.Children.Add(error);}
        var columns=new Grid();columns.ColumnDefinitions.Add(new ColumnDefinition());columns.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(40)});columns.ColumnDefinitions.Add(new ColumnDefinition());
        foreach(var kind in new[]{"genre","category"})
        {
            var section=new StackPanel();Grid.SetColumn(section,kind=="genre"?0:2);columns.Children.Add(section);
            var heading=new DockPanel {Margin=new Thickness(0,0,0,18)};
            var creator=new StackPanel();var create=Btn("＋ 추가",()=>ShowTagCreator(kind,creator,false));create.Padding=new Thickness(10,6,10,6);DockPanel.SetDock(create,Dock.Right);heading.Children.Add(create);
            heading.Children.Add(Text($"{KindLabel(kind)}  {catalog.Tags.Count(t=>t.Kind==kind)}",17));section.Children.Add(heading);section.Children.Add(creator);
            foreach(var tag in catalog.Tags.Where(t=>t.Kind==kind).ToList())
            {
                var item=new StackPanel();var row=new Grid {Margin=new Thickness(0,12,0,12)};
                row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
                var tagLabel=new TextBlock {Text=tag.Name,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=230,ToolTip=tag.Name};
                row.Children.Add(new Border {Background=B("#F0F4FA"),BorderBrush=B("#E0E7F1"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(5),Padding=new Thickness(11,6,11,6),HorizontalAlignment=HorizontalAlignment.Left,Child=tagLabel});
                var editor=new StackPanel();
                var rename=Btn("수정",()=>ShowTagRename(tag,editor));rename.Padding=new Thickness(10,6,10,6);Grid.SetColumn(rename,1);row.Children.Add(rename);
                var delete=Btn("삭제",()=>ShowTagDelete(tag,editor));delete.Padding=new Thickness(10,6,10,6);delete.Foreground=B("#9A7D7D");Grid.SetColumn(delete,2);row.Children.Add(delete);
                item.Children.Add(row);item.Children.Add(editor);section.Children.Add(item);section.Children.Add(new Border {Height=1,Background=B("#EDF0F6")});
            }
            if(!catalog.Tags.Any(t=>t.Kind==kind))section.Children.Add(new TextBlock {Text="아직 만든 태그가 없습니다",Foreground=B("#9AA5B5"),Margin=new Thickness(0,22,0,0)});
        }
        TagsContent.Children.Add(columns);
    }
    void ShowTagRename(TagDefinition tag,StackPanel host)
    {
        host.Children.Clear();var row=new DockPanel {Margin=new Thickness(0,0,0,12)};var input=new TextBox {Text=tag.Name,MaxLength=40};AutomationProperties.SetName(input,"태그 이름 수정");
        var error=Text("",12,"#AD6A39");
        void Commit()
        {
            var value=input.Text.Trim();var validation=CheckTagName(tag.Kind,value,tag.Id);if(validation!=null){error.Text=validation;return;}
            var next=CopyCatalog();next.Tags.Single(t=>t.Id==tag.Id).Name=value;if(!StoreCatalog(next,error))return;
            foreach(var p in packages)
            {var list=tag.Kind=="genre"?p.Genres:p.Categories;for(int i=0;i<list.Count;i++)if(list[i]==tag.Name)list[i]=value;}
            history.Clear();RenderTagManager();Say("태그 이름을 변경했습니다");
        }
        var cancel=Btn("취소",()=>host.Children.Clear());DockPanel.SetDock(cancel,Dock.Right);row.Children.Add(cancel);
        var save=Btn("저장",Commit,true);DockPanel.SetDock(save,Dock.Right);row.Children.Add(save);row.Children.Add(input);
        host.Children.Add(row);host.Children.Add(error);input.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){e.Handled=true;Commit();}else if(e.Key==Key.Escape)host.Children.Clear();};input.Focus();input.SelectAll();
    }
    void ShowTagDelete(TagDefinition tag,StackPanel host)
    {
        host.Children.Clear();
        var affected=packages.Count(p=>(tag.Kind=="genre"?p.Genres:p.Categories).Contains(tag.Name));
        host.Children.Add(Text(affected>0?$"현재 게임 {affected}개에서 이 분류를 제거합니다.":"이 태그를 삭제할까요?",12,"#9B7753"));
        var error=Text("",12,"#AD6A39");var actions=new WrapPanel {Margin=new Thickness(0,10,0,12)};
        actions.Children.Add(Btn("삭제",()=>
        {
            var next=CopyCatalog();next.Tags.RemoveAll(t=>t.Id==tag.Id);if(!StoreCatalog(next,error))return;
            foreach(var p in packages)(tag.Kind=="genre"?p.Genres:p.Categories).Remove(tag.Name);
            history.Clear();RenderTagManager();Say("태그를 삭제했습니다");
        }));
        actions.Children.Add(Btn("취소",()=>host.Children.Clear()));host.Children.Add(actions);host.Children.Add(error);
    }
    void Guide_Click(object sender,RoutedEventArgs e)
    {
        var win=Modal("사용 가이드",640,500);var layout=new DockPanel {Margin=new Thickness(28)};
        var close=Btn("알겠어요",()=>win.Close(),true);close.IsCancel=true;close.HorizontalAlignment=HorizontalAlignment.Right;DockPanel.SetDock(close,Dock.Bottom);layout.Children.Add(close);
        var content=new StackPanel();content.Children.Add(Text(page=="tags"?"태그 관리":page=="jobs"?"백업 · 복원":"게임 정리, 세 단계",23));
        var steps=page=="tags"?new[]{("추가","만든 태그는 다음 실행에도 남습니다."),("수정 · 삭제","현재 게임의 분류에도 반영됩니다."),("돌아가기","정리 계획을 유지한 채 이어서 작업합니다.")}:page=="jobs"?new[]{("백업","정리 계획 확인에서 백업만 실행하거나 백업 후 이동을 선택하세요."),("중단 · 재개","중단된 기록에서 재개할 수 있습니다. 원본이 바뀌면 새 백업이 필요합니다."),("복원 · 보관","새 위치에 복원하고 게임을 직접 확인하세요. 백업은 자동 삭제하지 않습니다.")}:new[]{("1  선택","왼쪽 게임을 선택하고 ‘내용 보기’로 확인하세요."),("2  확인","색 영역은 이동 범위, 배지는 각 항목의 역할입니다. 분류는 선택 사항이에요."),("3  담기","카드를 오른쪽으로 끌거나 ‘보관함에 담기’를 누르세요.")};
        foreach(var step in steps){content.Children.Add(new TextBlock {Text=step.Item1,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,24,0,7)});content.Children.Add(Text(step.Item2,13,"#7B8CA4"));}
        if(page!="tags")content.Children.Add(new TextBlock {Text="태그 이름 수정·삭제는 왼쪽 ‘태그’ 탭에서 합니다.",Margin=new Thickness(0,22,0,0),Foreground=B("#8797AF"),TextWrapping=TextWrapping.Wrap});
        layout.Children.Add(content);win.Content=layout;win.ShowDialog();
    }
}

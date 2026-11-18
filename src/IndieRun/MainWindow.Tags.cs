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
        try { catalog=tagStore.Load();tagLoadError=""; }
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
        if(target!="organize"&&!LeaveScopeEdit())return;
        if(page!=target)Say("");
        page=target;
        PageFooter.Visibility=target=="organize"?Visibility.Visible:Visibility.Collapsed;FooterRow.Height=new GridLength(target=="organize"?84:0);
        PlayerPanel.Visibility=target=="play"?Visibility.Visible:Visibility.Collapsed;
        LibraryPanel.Visibility=target=="library"?Visibility.Visible:Visibility.Collapsed;
        Workbench.Visibility=target=="organize"?Visibility.Visible:Visibility.Collapsed;
        JobsPanel.Visibility=target=="jobs"?Visibility.Visible:Visibility.Collapsed;
        TagsPanel.Visibility=target=="tags"?Visibility.Visible:Visibility.Collapsed;
        FooterActions.Visibility=target=="organize"?Visibility.Visible:Visibility.Collapsed;ScopeUndoButton.Visibility=target=="organize"&&history.Count>0?Visibility.Visible:Visibility.Collapsed;
        foreach(var pair in new[]{(OrganizeNav,"organize"),(TagsNav,"tags"),(JobsNav,"jobs"),(LibraryNav,"library"),(PlayerNav,"play")})
        {pair.Item1.Background=target==pair.Item2?B("#344D7C"):Brushes.Transparent;pair.Item1.Foreground=target==pair.Item2?Brushes.White:B("#B8C5DC");}
        PageTitle.Text=target=="play"?"게임 실행":target=="library"?"보관함":target=="tags"?"태그 관리":target=="jobs"?"작업 기록":"게임 정리";
        PageSubtitle.Text="";PageSubtitle.Visibility=Visibility.Collapsed;
        if(target=="tags") {PlanCount.Text="정리 일시 중지";PlanHint.Text="현재 계획은 유지됩니다";Say("");}
        else if(target=="jobs") {PlanCount.Text="백업 · 복원 기록";PlanHint.Text="백업은 직접 삭제 전까지 보관";Say("");}
        else if(target=="library"){PlanCount.Text="보관함 메타데이터";PlanHint.Text="고유 ID로 연결 · JSON과 CSV로 보관";}
        else if(target=="play"){PlanCount.Text="내 게임";PlanHint.Text="실행 파일은 처음 한 번 직접 지정합니다";}
        else {RenderPlan();Say("가져올 게임을 선택하세요.");}
    }
    void Tags_Click(object sender,RoutedEventArgs e)
    {
        if(running)return;
        tagReturnPage=page is "play" or "library"?page:"organize";SetPage("tags");RenderTagManager();
    }
    void RenderTagManager() => RenderModernTags();
    void Guide_Click(object sender,RoutedEventArgs e)
    {
        var win=Modal("사용 가이드",640,500);var layout=new DockPanel {Margin=new Thickness(28)};
        var close=Btn("알겠어요",()=>win.Close(),true);close.IsCancel=true;close.HorizontalAlignment=HorizontalAlignment.Right;DockPanel.SetDock(close,Dock.Bottom);layout.Children.Add(close);
        var content=new StackPanel();content.Children.Add(Text(page=="tags"?"태그 관리":page=="jobs"?"백업 · 복원":page=="play"?"내 게임 찾기 · 실행":"게임 가져오기와 정리",23));
        var steps=page=="play"?new[]{("찾기","별칭으로 검색하고 장르·카테고리를 함께 선택해 좁힙니다."),("편집","게임의 ⋯ 메뉴에서 별칭·태그·이미지를 편집합니다. 다중 선택에서는 태그를 한 번에 바꿉니다."),("실행","처음 한 번 실행 파일을 확인합니다. 상세 정보는 ⋯ 메뉴에서 필요할 때 엽니다.")}:page=="tags"?new[]{("추가","만든 태그는 다음 실행에도 남습니다."),("수정 · 삭제","현재 게임의 분류에도 반영됩니다."),("돌아가기","정리 계획을 유지한 채 이어서 작업합니다.")}:page=="jobs"?new[]{("백업","게임의 관리 메뉴에서 백업을 만듭니다. 이동할 때도 백업 여부를 선택할 수 있습니다."),("중단 · 재개","중단된 기록에서 재개할 수 있습니다. 원본이 바뀌면 새 백업이 필요합니다."),("복원 · 보관","새 위치에 복원하고 게임을 직접 확인하세요. 백업은 자동 삭제하지 않습니다.")}:new[]{("선택","후보를 누르면 폴더 구조를 확인합니다. 선택 버튼으로 가져올 게임을 고릅니다."),("추가","‘게임 가져오기’에서 이 위치에서 사용할지, 보관함으로 옮길지 선택합니다."),("이동","옮기기를 선택하면 도착 위치와 백업 여부를 확인합니다. 기본은 백업 후 이동입니다.")};
        foreach(var step in steps){content.Children.Add(new TextBlock {Text=step.Item1,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,24,0,7)});content.Children.Add(Text(step.Item2,13,"#7B8CA4"));}
        if(page!="tags")content.Children.Add(new TextBlock {Text="태그 이름 수정·삭제는 왼쪽 ‘태그’ 탭에서 합니다.",Margin=new Thickness(0,22,0,0),Foreground=B("#8797AF"),TextWrapping=TextWrapping.Wrap});
        layout.Children.Add(content);win.Content=layout;win.ShowDialog();
    }
}


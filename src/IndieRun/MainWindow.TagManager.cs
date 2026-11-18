using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace IndieRun;

public partial class MainWindow
{
    string managerKind="genre",managerQuery="",tagReturnPage="play";
    List<(string Id,string Title)> TagUsers(TagDefinition tag)
    {
        var doc=HasLibraryFile?Library().Load():libraryDocument;
        return PlayerRules.TagUsage(packages,doc,[tag])[tag.Id];
    }
    void RenderModernTags()
    {
        if(playerRoot!=destination)LoadPlayer();TagsContent.Children.Clear();
        var toolbar=new DockPanel {Margin=new Thickness(0,0,0,18)};
        var back=Btn("← 돌아가기",()=>{SetPage(tagReturnPage);if(tagReturnPage=="play")RenderPlayer();else if(tagReturnPage=="library")RenderLibrary();else Render();});back.Margin=new Thickness(12,0,0,0);DockPanel.SetDock(back,Dock.Right);toolbar.Children.Add(back);
        var add=Btn("＋ 새 태그",()=>EditTag(null),true);add.Margin=new Thickness(12,0,0,0);DockPanel.SetDock(add,Dock.Right);toolbar.Children.Add(add);
        foreach(var kind in new[]{"genre","category"}){var b=Btn($"{KindLabel(kind)}  {catalog.Tags.Count(t=>t.Kind==kind)}",()=>{managerKind=kind;RenderModernTags();},managerKind==kind);DockPanel.SetDock(b,Dock.Left);toolbar.Children.Add(b);}
        var query=new TextBox {Text=managerQuery,ToolTip="태그 이름 검색",MinWidth=180};AutomationProperties.SetName(query,"관리 태그 검색");toolbar.Children.Add(WithHint(query,"태그 이름 검색"));TagsContent.Children.Add(toolbar);
        var rows=new StackPanel();TagsContent.Children.Add(rows);
        Dictionary<string,List<(string Id,string Title)>> usage=[];string usageError="";
        if(tagLoadError.Length==0)try{usage=PlayerRules.TagUsage(packages,HasLibraryFile?Library().Load():libraryDocument,catalog.Tags);}catch(Exception ex)when(LibraryFault(ex)){usageError=ex.Message;}
        void RenderRows()
        {
            rows.Children.Clear();
            if(tagLoadError.Length>0){rows.Children.Add(Text("태그 파일 확인 필요 · "+tagLoadError,13,"#A37B50"));return;}
            foreach(var tag in catalog.Tags.Where(t=>t.Kind==managerKind&&t.Name.Contains(managerQuery,StringComparison.OrdinalIgnoreCase)).OrderBy(t=>t.Name))
            {
                var grid=new Grid {Margin=new Thickness(0,0,0,8)};grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(170)});grid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(48)});
                grid.Children.Add(new Border {Background=B("#EAF0FB"),CornerRadius=new CornerRadius(5),Padding=new Thickness(12,7,12,7),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center,Child=new TextBlock {Text=tag.Name,FontWeight=FontWeights.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=380,ToolTip=tag.Name}});
                if(usageError.Length>0){rows.Children.Add(WorkText("태그 사용 현황을 읽지 못했습니다.",13,"Warning.Text"));var retry=Btn("다시 확인",RenderModernTags);retry.HorizontalAlignment=HorizontalAlignment.Left;retry.Margin=new Thickness(0,10,0,10);rows.Children.Add(retry);rows.Children.Add(WorkDetails(usageError));break;}
                var users=usage.GetValueOrDefault(tag.Id)??[];
                var count=Btn($"게임 {users.Count}개 ↗",()=>
                {var win=Modal(tag.Name+" · 사용 게임",650,500);var list=new StackPanel {Margin=new Thickness(24)};list.Children.Add(Text($"게임 {users.Count}개",20));foreach(var user in users)list.Children.Add(new TextBlock {Text=user.Title,Margin=new Thickness(0,12,0,0)});win.Content=new ScrollViewer {Content=list};win.ShowDialog();});count.BorderThickness=new Thickness(0);count.Background=System.Windows.Media.Brushes.Transparent;Grid.SetColumn(count,1);grid.Children.Add(count);
                var more=Btn("⋯",()=>{});Grid.SetColumn(more,2);grid.Children.Add(more);
                more.Click+=(_,_)=>{var menu=new ContextMenu();var rename=new MenuItem {Header="이름 변경"};rename.Click+=(_,_)=>EditTag(tag);var delete=new MenuItem {Header="태그 삭제"};delete.Click+=(_,_)=>DeleteTag(tag);menu.Items.Add(rename);menu.Items.Add(delete);menu.PlacementTarget=more;menu.IsOpen=true;};
                rows.Children.Add(new Border {Child=grid,Padding=new Thickness(16,12,12,8),Background=B("#F7F9FD"),CornerRadius=new CornerRadius(8),Margin=new Thickness(0,0,0,8)});
            }
            if(rows.Children.Count==0)rows.Children.Add(Text("표시할 태그가 없습니다",14,"#8997AA"));
        }
        query.TextChanged+=(_,_)=>{managerQuery=query.Text;RenderRows();};RenderRows();
    }
    void EditTag(TagDefinition? existing)
    {
        var win=Modal(existing==null?"태그 추가":"태그 이름 변경",540,290);win.MinWidth=540;win.MinHeight=290;
        var stack=new StackPanel {Margin=new Thickness(26)};stack.Children.Add(Text(existing==null?"새 "+KindLabel(managerKind):"태그 이름 변경",21));
        var input=new TextBox {Text=existing?.Name??"",MaxLength=40,Margin=new Thickness(0,16,0,8)};AutomationProperties.SetName(input,"관리 태그 이름");stack.Children.Add(input);
        var error=Text("",12,"#A37B50");stack.Children.Add(error);var row=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,18,0,0)};row.Children.Add(Btn("취소",()=>win.Close()));
        row.Children.Add(Btn("저장",()=>
        {
            var name=input.Text.Trim();var kind=existing?.Kind??managerKind;var invalid=CheckTagName(kind,name,existing?.Id);if(invalid!=null){error.Text=invalid;return;}
            var next=CopyCatalog();var games=Clone(packages);var changes=new List<TagChange>();
            if(existing==null)next.Tags.Add(new TagDefinition {Name=name,Kind=kind});
            else
            {
                next.Tags.Single(t=>t.Id==existing.Id).Name=name;
                PlayerRules.ChangeTag(games,new LibraryDocument(),existing,name);
                changes.Add(new TagChange {Id=existing.Id,Kind=kind,OldName=existing.Name,NewName=name});
            }
            if(CommitPlayerEdit(games,next,Clone(playerDocument),[],changes)){win.Close();RenderModernTags();}
        },true));stack.Children.Add(row);win.Content=stack;win.ShowDialog();
    }
    void DeleteTag(TagDefinition tag)
    {
        try
        {
            var users=TagUsers(tag);
            if(!Confirm("태그를 삭제할까요?",$"‘{tag.Name}’ 태그가 게임 {users.Count}개에서 제거됩니다.\n찾을 수 없거나 목록에서 제외한 게임도 포함합니다.","태그 삭제",destructive:true))return;
            var next=CopyCatalog();next.Tags.RemoveAll(t=>t.Id==tag.Id);var games=Clone(packages);PlayerRules.ChangeTag(games,new LibraryDocument(),tag,null);
            if(CommitPlayerEdit(games,next,Clone(playerDocument),[],[new TagChange {Id=tag.Id,Kind=tag.Kind,OldName=tag.Name}])){playerView.TagIds.Remove(tag.Id);SaveView();RenderModernTags();}
        }
        catch(Exception ex) when(LibraryFault(ex)){ErrorInfo("태그 삭제 확인","태그를 삭제하지 못했습니다.",ex.Message);}
    }
}

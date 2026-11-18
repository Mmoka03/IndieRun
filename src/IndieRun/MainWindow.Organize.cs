using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ShapePath=System.Windows.Shapes.Path;

namespace IndieRun;
public partial class MainWindow
{
    readonly HashSet<string> organizeSelection=[];
    readonly Dictionary<string,string> registrationErrors=[];
    bool identityNoticeAccepted;
    bool IsAdded(Package p)=>GameRegistration.IsAdded(p)||libraryDocument.Games.Any(g=>g.Id==p.Id);
    bool NeedsCandidateReview(Package p)=>p.ReviewState.Length>0||registrationErrors.ContainsKey(p.Id)||!IsAdded(p)&&!Executables(p).Any(f=>!ExecutableCandidates.IsAuxiliary(f.Path));
    bool CanPick(Package p)=>p.IsReal&&!p.Excluded&&p.ContentOwnerId.Length==0&&p.ReviewState.Length==0&&(IsAdded(p)||Executables(p).Any(f=>!ExecutableCandidates.IsAuxiliary(f.Path)));
    static TextBlock OrganizeLabel(string text,double size=13,string color="Text.Primary")=>new(){Text=text,FontSize=size,Foreground=ThemeBrush(color),VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};
    static Border OrganizeChip(string text,string role="Accent")=>new(){Child=OrganizeLabel(text,11,role+".Text"),Background=ThemeBrush(role+".Surface"),CornerRadius=new CornerRadius(4),Padding=new Thickness(8,4,8,4),VerticalAlignment=VerticalAlignment.Center};
    void RenderOrganize()
    {
        RenderCandidates();
        SelectedTitle.Text=HasSelection?PlayerRules.Title(selected):"검색할 폴더를 선택하세요";SelectedTitle.ToolTip=SelectedTitle.Text;
        OriginalPath.Text=selected.Source;OriginalPath.ToolTip=selected.Source;PackageLabel.Text="폴더 구조";
        PackageLayer.SetResourceReference(Border.BackgroundProperty,"Surface.Primary");PackageLayer.BorderThickness=new Thickness(0);TreeRows.Children.Clear();
        if(HasSelection&&selected.IsReal&&snapshot!=null)RenderScopeContext();
        else TreeRows.Children.Add(WorkText(scanning?"폴더를 확인하고 있습니다…":"‘탐색 위치’에서 게임이 있는 폴더를 추가하세요.",13,"Text.Muted"));
        Alias.Text=selected.Alias;PhysicalName.Text=selected.Name;
        PreviewButton.IsEnabled=HasSelection&&!scanning&&!backupBusy;OrganizeInfoButton.Visibility=HasSelection&&IsAdded(selected)?Visibility.Visible:Visibility.Collapsed;
        var parent=HasSelection?Directory.GetParent(selected.Source)?.FullName:null;
        ScopeActions.Visibility=HasSelection&&!IsAdded(selected)&&selected.ReviewState.Length==0?Visibility.Visible:Visibility.Collapsed;
        IncludeParentButton.Visibility=parent!=null&&GameScanner.Within(parent,selected.SearchRoot)&&!parent.Equals(selected.SearchRoot,StringComparison.OrdinalIgnoreCase)?Visibility.Visible:Visibility.Collapsed;
        IncludeParentButton.IsEnabled=!scanning&&!backupBusy;IncludeParentButton.ToolTip=parent;
        ResetScopeButton.Visibility=selected.ScopeHistory.Count>0?Visibility.Visible:Visibility.Collapsed;
        OrganizeWarning.Visibility=Visibility.Collapsed;AddReviewedButton.Visibility=Visibility.Collapsed;
        if(HasSelection&&!IsAdded(selected)&&selected.ReviewState.Length==0&&!Executables(selected).Any(f=>!ExecutableCandidates.IsAuxiliary(f.Path))){ShowOrganizeWarning("일반 실행 후보가 없습니다. 내용을 확인한 뒤 직접 추가할 수 있습니다.");AddReviewedButton.Visibility=Visibility.Visible;}
        if(HasSelection&&registrationErrors.TryGetValue(selected.Id,out var error)){ShowOrganizeWarning("추가하지 못했습니다. 위치와 권한을 확인한 뒤 다시 시도하세요.");OrganizeWarning.ToolTip=error;}
        MoveSelectedButton.Visibility=HasSelection&&IsAdded(selected)&&scopeDraft==null?Visibility.Visible:Visibility.Collapsed;
        MoveSelectedButton.IsEnabled=!backupBusy&&!scanning&&selected.ReviewState.Length==0;
        OrganizeInfoButton.IsEnabled=!backupBusy&&!scanning;
        OrganizeHint.Text=HasSelection&&selected.LocalRegistration?"식별 파일 없이 등록됨 · 위치를 바꾸면 직접 연결하세요.":"게임을 추가해도 폴더는 이동하지 않습니다.";
        OrganizeHint.Visibility=HasSelection&&selected.LocalRegistration?Visibility.Visible:Visibility.Collapsed;
        SelectedState.Text=!HasSelection?"":IsAdded(selected)?"추가됨":"추가할 범위";
        SelectedState.SetResourceReference(ForegroundProperty,HasSelection&&IsAdded(selected)?"Success.Text":"Accent.Text");
        RetryPortableButton.Visibility=HasSelection&&portableErrors.ContainsKey(selected.Id)?Visibility.Visible:Visibility.Collapsed;
        if(RetryPortableButton.Visibility==Visibility.Visible){ShowOrganizeWarning("게임 정보 파일을 확인하지 못했습니다. 기존 게임과 설정은 유지됩니다.");OrganizeWarning.ToolTip=portableErrors[selected.Id];}
        RenderScopeEditActions();RenderPlan();
    }
    void ShowOrganizeWarning(string message){OrganizeWarningText.Text=message;OrganizeWarning.Visibility=Visibility.Visible;}
    void RenderOrganizeCandidates()
    {
        var offset=CandidateScroll.VerticalOffset;CandidateList.Children.Clear();CandidateReviewList.Children.Clear();
        organizeSelection.RemoveWhere(id=>!packages.Any(p=>p.Id==id&&!p.Excluded));
        var query=Search.Text.Trim();var matching=packages.Where(p=>!p.Excluded&&p.ContentOwnerId.Length==0&&(p.Alias+p.Folder).Contains(query,StringComparison.OrdinalIgnoreCase)).ToList();CandidateCount.Text=$"{matching.Count}개";
        foreach(var p in matching)
        {
            var row=new Grid {Margin=new Thickness(0,0,0,8)};row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(48)});
            var body=new StackPanel();var header=new DockPanel {Height=26};
            var status=organizeSelection.Contains(p.Id)?"선택됨":IsAdded(p)?"추가됨":NeedsCandidateReview(p)?"확인 필요":"";
            var chip=OrganizeChip(status,organizeSelection.Contains(p.Id)?"Accent":IsAdded(p)?"Success":NeedsCandidateReview(p)?"Warning":"Accent");chip.Visibility=status.Length>0?Visibility.Visible:Visibility.Collapsed;DockPanel.SetDock(chip,Dock.Right);header.Children.Add(chip);
            var title=OrganizeLabel(PlayerRules.Title(p),14);title.FontWeight=FontWeights.SemiBold;title.Margin=new Thickness(0,0,8,0);header.Children.Add(title);body.Children.Add(header);
            body.Children.Add(new TextBlock {Text=p.Folder,Foreground=ThemeBrush("Text.Muted"),FontSize=11,TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(0,7,0,0)});
            body.Children.Add(new TextBlock {Text=p.Size,Foreground=ThemeBrush("Text.Muted"),FontSize=11,Margin=new Thickness(0,5,0,0)});
            var inspect=Btn("",()=>{if(!LeaveScopeEdit())return;selected=p;Render();});inspect.Content=body;inspect.HorizontalContentAlignment=HorizontalAlignment.Stretch;inspect.Margin=new Thickness(0);inspect.Padding=new Thickness(13);inspect.Background=ThemeBrush(organizeSelection.Contains(p.Id)?"Accent.Surface":"Surface.Primary");inspect.BorderBrush=ThemeBrush(p==selected?"Border.Focus":"Border.Subtle");inspect.ToolTip="폴더 구조 보기\n"+p.Source;AutomationProperties.SetName(inspect,PlayerRules.Title(p)+" 폴더 확인");
            var menu=new ContextMenu();var composition=new MenuItem {Header="구성 수정"};composition.IsEnabled=!scanning&&!running&&!backupBusy&&p.ReviewState.Length==0;composition.Click+=(_,_)=>EditComposition(p);menu.Items.Add(composition);menu.Items.Add(new Separator());var exclude=new MenuItem {Header="이 계획에서 제외"};exclude.Click+=(_,_)=>ExcludeCandidate(p);menu.Items.Add(exclude);
            if(!IsAdded(p)&&p.ReviewState.Length==0&&!CanPick(p))
            {var explicitAdd=new MenuItem {Header="내용 확인 후 추가"};explicitAdd.Click+=(_,_)=>{if(Confirm("실행 후보 확인","일반 실행 후보가 없습니다. 내용을 확인한 게임만 추가하세요.","이 게임 추가",p.Source)){organizeSelection.Add(p.Id);AddGames_Click(this,new RoutedEventArgs());}};menu.Items.Add(explicitAdd);}
            inspect.ContextMenu=menu;row.Children.Add(inspect);
            if(CanPick(p)||!IsAdded(p)&&registrationErrors.ContainsKey(p.Id))
            {
                var picked=organizeSelection.Contains(p.Id);var marker=new Grid {Width=26,Height=26};marker.Children.Add(new System.Windows.Shapes.Ellipse {Stroke=ThemeBrush(picked?"Accent.Fill":"Border.Hover"),Fill=ThemeBrush(picked?"Accent.Fill":"Surface.Primary"),StrokeThickness=1});
                marker.Children.Add(new ShapePath {Data=Geometry.Parse(picked?"M 0,5 L 4,9 L 12,0":"M 6,0 L 6,12 M 0,6 L 12,6"),Stroke=ThemeBrush(picked?"Accent.OnFill":"Text.Secondary"),StrokeThickness=1.5,Width=12,Height=12,Stretch=Stretch.Uniform,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center});
                var pick=Btn("",()=>{if(!organizeSelection.Add(p.Id))organizeSelection.Remove(p.Id);RenderCandidates();RenderPlan();});pick.Content=marker;pick.Padding=new Thickness(0);pick.Margin=new Thickness(6,0,0,0);pick.BorderThickness=new Thickness(0);pick.Background=Brushes.Transparent;pick.ToolTip=picked?"선택 해제":IsAdded(p)?"이동할 게임 선택":"추가할 게임 선택";AutomationProperties.SetName(pick,PlayerRules.Title(p)+(picked?" 선택 해제":" 선택"));Grid.SetColumn(pick,1);row.Children.Add(pick);
            }
            (NeedsCandidateReview(p)?CandidateReviewList:CandidateList).Children.Add(row);
        }
        CandidateReviewSection.Header=$"확인 필요 · {CandidateReviewList.Children.Count}개";CandidateReviewSection.Visibility=CandidateReviewList.Children.Count>0?Visibility.Visible:Visibility.Collapsed;
        if(matching.Count==0)CandidateList.Children.Add(WorkText("일치하는 후보가 없습니다.",12,"Text.Muted"));
        CandidateScroll.ScrollToVerticalOffset(offset);
    }
    void RenderOrganizeFooter()
    {
        if(scopeDraft!=null)
        {
            PlanCount.Text="범위 수정 중";PlanHint.Text="게임 파일은 이동하지 않습니다.";PlanHint.Visibility=Visibility.Visible;
            AddGamesButton.Visibility=MovePickedButton.Visibility=ClearPickedButton.Visibility=ReviewButton.Visibility=ScopeUndoButton.Visibility=Visibility.Collapsed;return;
        }
        var chosen=packages.Where(p=>organizeSelection.Contains(p.Id)&&!p.Excluded).ToList();var add=chosen.Count(p=>!IsAdded(p));var move=chosen.Count(p=>IsAdded(p));
        PlanCount.Text=chosen.Count>0?$"{chosen.Count}개 선택":"추가하거나 옮길 게임을 선택하세요";PlanHint.Visibility=chosen.Count>0?Visibility.Visible:Visibility.Collapsed;PlanHint.Text=chosen.Count==0?"":$"추가 {add}개 · 이동 {move}개";
        var ready=!scanning&&!backupBusy&&!running&&!workspaceBlocked&&playerError.Length==0&&!File.Exists(Edits().Pending)&&!ScopeChanges().HasPending;
        AddGamesButton.Content=add>0?$"게임 {add}개 추가":"선택한 게임 추가";AddGamesButton.Visibility=move>0&&add==0?Visibility.Collapsed:Visibility.Visible;AddGamesButton.IsEnabled=add>0&&ready&&scopeDraft==null;
        MovePickedButton.Content=$"게임 {move}개 옮기기";MovePickedButton.Visibility=move>0?Visibility.Visible:Visibility.Collapsed;MovePickedButton.IsEnabled=move>0&&ready&&scopeDraft==null&&chosen.Where(IsAdded).All(p=>p.ReviewState.Length==0);
        ClearPickedButton.Visibility=chosen.Count>0?Visibility.Visible:Visibility.Collapsed;
        var planned=packages.Count(p=>!p.Excluded&&p.Planned&&!p.Done);ReviewButton.Visibility=planned>0?Visibility.Visible:Visibility.Collapsed;ReviewButton.Content=$"이동 계획 {planned}개 확인";ReviewButton.IsEnabled=ready&&scopeDraft==null;
        ScopeUndoButton.Visibility=page=="organize"&&history.Count>0&&scopeDraft==null?Visibility.Visible:Visibility.Collapsed;
    }
    void PickAll_Click(object sender,RoutedEventArgs e)
    {if(running||scanning||backupBusy||scopeDraft!=null)return;var query=Search.Text.Trim();foreach(var p in packages.Where(p=>CanPick(p)&&(p.Alias+p.Folder).Contains(query,StringComparison.OrdinalIgnoreCase)))organizeSelection.Add(p.Id);RenderCandidates();RenderPlan();}
    void ClearPicked_Click(object sender,RoutedEventArgs e){organizeSelection.Clear();RenderCandidates();RenderPlan();}
    void MovePicked_Click(object sender,RoutedEventArgs e)
    {var list=packages.Where(p=>organizeSelection.Contains(p.Id)&&IsAdded(p)&&!p.Excluded).ToList();if(list.Count>0&&!running&&!scanning&&!backupBusy&&scopeDraft==null&&list.All(p=>p.ReviewState.Length==0))ShowReadOnlyPlan(list);}
    void AddReviewed_Click(object sender,RoutedEventArgs e)
    {
        if(!HasSelection||IsAdded(selected)||selected.ReviewState.Length>0||backupBusy||scanning||running)return;
        if(!Confirm("실행 후보 확인","일반 실행 후보가 없습니다. 내용을 확인한 게임만 추가하세요.","이 게임 추가",selected.Source))return;
        organizeSelection.Add(selected.Id);AddGames_Click(sender,e);
    }
    async void AddGames_Click(object sender,RoutedEventArgs e)
    {
        if(scanning||backupBusy||running||workspaceBlocked||scopeDraft!=null||organizeSelection.Count==0)return;
        if(!identityNoticeAccepted)
        {
            if(!Confirm("게임 추가","폴더 위치는 유지하고, 게임을 구별하는 작은 식별 파일을 게임 폴더에 저장합니다.","추가", "식별 파일을 쓸 권한이 없는 폴더는 프로그램 내부에만 등록합니다. 이 경우 폴더 위치를 바꾸면 직접 다시 연결해야 합니다."))return;
            identityNoticeAccepted=true;
        }
        await AddPickedGamesAsync();
    }
    async Task AddPickedGamesAsync(bool showErrors=true)
    {
        if(!identityNoticeAccepted||snapshot==null||workspaceBlocked||workspaceLock==null||playerError.Length>0||File.Exists(Edits().Pending))return;
        var chosen=packages.Where(p=>organizeSelection.Contains(p.Id)&&!p.Excluded&&!IsAdded(p)).ToList();
        if(chosen.Count==0)return;
        if(!FlushWorkspace()){Say("추가 전에 현재 작업을 저장해 주세요.");return;}
        running=true;Workbench.IsEnabled=false;FooterActions.IsEnabled=false;RescanButton.IsEnabled=false;
        var navs=new[]{PlayerNav,OrganizeNav,TagsNav,JobsNav,LibraryNav};foreach(var nav in navs)nav.IsEnabled=false;
        int added=0,local=0;var failures=new List<string>();
        try
        {
            foreach(var p in chosen)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);registrationErrors.Remove(p.Id);
                if(RegisterGame(p,true)){added++;if(p.LocalRegistration)local++;organizeSelection.Remove(p.Id);}
                else failures.Add(PlayerRules.Title(p)+": "+registrationErrors.GetValueOrDefault(p.Id,"폴더 위치와 상태를 확인하세요."));
            }
            if(!FlushWorkspace()&&showErrors){ErrorInfo("추가 상태 저장","최근 추가 상태를 저장하지 못했습니다. 프로그램을 닫기 전에 저장을 다시 시도하세요.",SaveStatus.ToolTip?.ToString()??"");}
            if(playerRoot!=destination)LoadPlayer();
            foreach(var p in packages.Where(IsAdded))PlayFor(p);
            try{new PlayerStore(destination).Save(playerDocument);}
            catch(Exception ex) when(LibraryFault(ex)){if(showErrors)ErrorInfo("실행 설정 저장","게임 추가는 유지했습니다. 실행 설정 저장을 다시 시도하세요.",ex.Message);}
            Render();Say($"게임 {added}개 추가 · 폴더 위치 유지"+(failures.Count>0?$" · 확인 필요 {failures.Count}개":"")+(local>0?$" · 식별 파일 없이 등록 {local}개":""));
            if(failures.Count>0&&showErrors)ErrorInfo("일부 게임 추가 확인","추가한 게임은 유지했습니다. 실패한 항목은 위치와 권한을 확인한 뒤 다시 시도하세요.",string.Join('\n',failures));
        }
        finally{running=false;Workbench.IsEnabled=true;FooterActions.IsEnabled=true;RescanButton.IsEnabled=true;foreach(var nav in navs)nav.IsEnabled=true;RenderPlan();}
    }
    void MoveSelected_Click(object sender,RoutedEventArgs e)
    {if(HasSelection&&IsAdded(selected)&&scopeDraft==null&&!scanning&&!backupBusy&&!running&&selected.ReviewState.Length==0)ShowReadOnlyPlan([selected]);}
    void Organize_KeyDown(object sender,KeyEventArgs e)
    {if(page=="organize"&&OwnedWindows.Count==0&&e.Key==Key.Z&&Keyboard.Modifiers==ModifierKeys.Control&&Keyboard.FocusedElement is not TextBoxBase){e.Handled=true;if(scopeDraft!=null){var previous=scopeDraftHistory.LastOrDefault();if(previous!=null)DraftScope(previous,false);}else Undo_Click(sender,e);}}

    void RenderScopeContext()
    {
        if(snapshot==null)return;var context=Directory.GetParent(ScopeView.Source)?.FullName;
        if(context==null||context.Equals(ScopeView.SearchRoot,StringComparison.OrdinalIgnoreCase)||!GameScanner.Within(context,ScopeView.SearchRoot))context=ScopeView.Source;
        TreeRows.Children.Add(ScopeFolder(context,0));
        if(selected.RelatedFolders.Count>0)
        {
            var links=new StackPanel();foreach(var r in selected.RelatedFolders){var label=WorkText(r.Name,12,"Composition.Related.Text");label.ToolTip=r.Source;label.Margin=new Thickness(0,8,0,0);links.Children.Add(label);}
            TreeRows.Children.Add(new Expander{Header=$"연결된 자료 · {selected.RelatedFolders.Count}개",Content=links,Margin=new Thickness(0,14,0,0)});
        }
    }
    Expander MoveStructure(Package p)
    {
        var children=new StackPanel {Margin=new Thickness(18,8,0,0)};
        if(snapshot!=null)
        {
            var dirs=snapshot.Directories.Where(d=>p.Includes(d)&&string.Equals(Path.GetDirectoryName(d),p.Source,StringComparison.OrdinalIgnoreCase)).OrderBy(d=>d).Select(d=>Path.GetFileName(d)+"/");
            var files=snapshot.Files.Where(f=>string.Equals(Path.GetDirectoryName(f.Path),p.Source,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(f.Path)!=LibraryStore.MarkerName).OrderBy(f=>f.Path).Select(f=>Path.GetFileName(f.Path));
            var names=dirs.Concat(files).ToList();foreach(var name in names.Take(12)){var label=OrganizeLabel(name,12,"Text.Secondary");label.Margin=new Thickness(0,0,0,6);children.Children.Add(label);}
            if(names.Count>12)children.Children.Add(WorkText($"그 외 {names.Count-12}개 항목",11,"Text.Muted"));
        }
        var result=new Expander {Header=p.Name+"/",Content=children,IsExpanded=true,Margin=new Thickness(0,12,0,0)};result.SetResourceReference(ForegroundProperty,"Text.Secondary");return result;
    }
    Border ScopeFolder(string path,int depth)
    {
        var isScope=path.Equals(ScopeView.Source,StringComparison.OrdinalIgnoreCase);var included=ScopeView.Includes(path);var body=new StackPanel();var header=new Grid();header.ColumnDefinitions.Add(new ColumnDefinition());header.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        var name=OrganizeLabel(Path.GetFileName(path)+"/",13,included?"Text.Primary":"Text.Muted");name.FontWeight=FontWeights.SemiBold;name.Margin=new Thickness(0,0,12,0);header.Children.Add(name);
        var assigned=GameScanner.Within(path,ScopeView.Source)?CompositionPlan.Role(ScopeView,path):"outside";
        var chip=OrganizeChip(assigned is "related" or "game" or "exclude"?CompositionPlan.Label(assigned):isScope&&ScopeView.OmittedFolders.Count>0?"본편 범위":isScope?"전체 포함":included?"포함":"범위 밖",assigned is "related" or "game"?CompositionPlan.Color(assigned):included?"Accent":"Neutral");Grid.SetColumn(chip,1);header.Children.Add(chip);
        var expand=new Expander {Header=header,Content=body,HorizontalContentAlignment=HorizontalAlignment.Stretch};expand.SetResourceReference(ForegroundProperty,"Text.Secondary");bool loaded=false;
        void Populate()
        {
            if(loaded||snapshot==null)return;loaded=true;
            var dirs=snapshot.Directories.Concat(ScopeView.Contents.Select(c=>Path.Combine(ScopeView.Source,c.Path)).Where(Directory.Exists)).Where(d=>string.Equals(Path.GetDirectoryName(d),path,StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d=>d,StringComparer.OrdinalIgnoreCase).ToList();
            var files=snapshot.Files.Where(f=>string.Equals(Path.GetDirectoryName(f.Path),path,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(f.Path)!=LibraryStore.MarkerName).OrderBy(f=>Path.GetExtension(f.Path)==".exe"?0:1).ThenBy(f=>f.Path).ToList();
            foreach(var dir in dirs.Take(120))body.Children.Add(ScopeFolder(dir,depth+1));
            foreach(var f in files.Take(Math.Max(0,180-dirs.Count)))
            {
                var row=new Grid {Margin=new Thickness(14,10,0,0)};row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});var text=OrganizeLabel(Path.GetFileName(f.Path),12,included?"Text.Secondary":"Text.Muted");text.ToolTip=f.Path;text.Margin=new Thickness(0,0,12,0);row.Children.Add(text);
                var role=Path.GetExtension(f.Path).Equals(".exe",StringComparison.OrdinalIgnoreCase)?ExecutableCandidates.IsAuxiliary(f.Path)?"보조 추정":"실행 후보":ReadOnlyPreview.IsText(f.Path)?"문서":ReadOnlyPreview.IsImage(f.Path)?"이미지":"";var type=OrganizeLabel(role,11,"Text.Muted");Grid.SetColumn(type,1);row.Children.Add(type);body.Children.Add(row);
            }
            if(dirs.Count+files.Count>180)body.Children.Add(WorkText("일부 항목만 표시합니다. 폴더 전체가 포함됩니다.",11,"Text.Muted"));
        }
        expand.Expanded+=(_,_)=>Populate();expand.IsExpanded=GameScanner.Within(ScopeView.Source,path);if(expand.IsExpanded)Populate();
        return new Border {Child=expand,Padding=new Thickness(14,8,14,12),Margin=new Thickness(depth==0?0:10,depth==0?0:10,0,0),CornerRadius=new CornerRadius(8),Background=ThemeBrush(isScope?"Scope.Surface":"Surface.Primary"),BorderBrush=ThemeBrush(isScope?"Border.Focus":"Border.Subtle"),BorderThickness=new Thickness(isScope?1.5:1)};
    }
}

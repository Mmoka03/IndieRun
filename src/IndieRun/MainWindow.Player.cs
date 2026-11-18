using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IndieRun;

public partial class MainWindow
{
    PlayerDocument playerDocument=new();
    PlayerView playerView=new();
    string playerRoot="",playerError="",tagFilterText="";
    bool multiSelect,tagFinderOpen;
    readonly HashSet<string> playerSelection=[];
    TextBox playerSearch=null!,filterTagSearch=null!;
    StackPanel playerFilters=null!,selectionBar=null!;
    Expander playerTagExpander=null!;
    string FilterHeader()=>"태그 필터"+(playerView.TagIds.Count>0?$" · {playerView.TagIds.Count}개 적용 중":"");
    WrapPanel playerCards=null!;
    ScrollViewer playerScroll=null!;
    TextBlock playerCount=null!;
    readonly Dictionary<string,BitmapSource> artworkCache=new();
    readonly Dictionary<string,LaunchState> launchFailures=new();
    string DataRoot=>Path.Combine(AppContext.BaseDirectory,"data");
    EditTransaction Edits()=>new(DataRoot);
    static T Clone<T>(T value)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    static Grid WithHint(TextBox box,string hint)
    {
        var grid=new Grid();grid.Children.Add(box);var label=new TextBlock {Text=hint,Foreground=ThemeBrush("Text.Hint"),Margin=new Thickness(12,0,8,0),VerticalAlignment=VerticalAlignment.Center,IsHitTestVisible=false,Visibility=box.Text.Length==0?Visibility.Visible:Visibility.Collapsed};grid.Children.Add(label);
        box.TextChanged+=(_,_)=>label.Visibility=box.Text.Length==0?Visibility.Visible:Visibility.Collapsed;return grid;
    }
    void LoadPlayer()
    {
        try{playerDocument=new PlayerStore(destination).Load();playerRoot=destination;playerError="";}
        catch(Exception ex) when(LibraryFault(ex)){playerDocument=new();playerRoot=destination;playerError=ex.Message;}
        foreach(var p in packages.Where(p=>IsAdded(p)&&BackupPaths.Id(p.Id)))if(!playerDocument.Games.Any(g=>g.Id==p.Id))playerDocument.Games.Add(new GamePlay {Id=p.Id});
    }
    void InitializePlayer()
    {
        var path=Path.Combine(DataRoot,"player-view.json");
        try{if(File.Exists(path))playerView=BackupJson.Load<PlayerView>(path,ValidateView);}catch(Exception ex) when(LibraryFault(ex)){Say("보기 설정을 확인해 주세요");Notice.ToolTip=ex.Message;}
        LoadPlayer();SetPage("play");RenderPlayer();
    }
    static void ValidateView(PlayerView v)
    {if(v.Query==null||v.TagIds==null||v.Layout is not ("cards" or "list")||v.Sort is not ("alias" or "recent" or "added")||v.Group is not ("all" or "favorites" or "recent" or "hidden"))throw new InvalidDataException("보기 설정을 확인하세요.");}
    void SaveView()
    {try{BackupJson.Save(Path.Combine(DataRoot,"player-view.json"),playerView,ValidateView);}catch(Exception ex) when(LibraryFault(ex)){Say("보기 설정 저장 실패");Notice.ToolTip=ex.Message;}}
    GamePlay PlayFor(Package p)
    {
        var game=playerDocument.Games.Find(g=>g.Id==p.Id);if(game!=null)return game;
        game=new GamePlay {Id=p.Id};playerDocument.Games.Add(game);return game;
    }
    async void Player_Click(object sender,RoutedEventArgs e)
    {if(backupBusy||scanning)return;await RefreshWorkspaceAsync(source,false,false);LoadPlayer();SetPage("play");RenderPlayer();}
    bool CommitPlayerEdit(List<Package> next,TagCatalog tags,PlayerDocument play,IEnumerable<string> edited,List<TagChange>? changes=null,WorkspaceDocument? prepared=null,ScanSnapshot? reconnectedScan=null)
    {
        if(workspaceBlocked||workspaceLock==null||tagLoadError.Length>0||playerError.Length>0){Info("저장 확인","데이터 읽기 오류를 먼저 해결해 주세요.");return false;}
        try
        {
            saveTimer.Stop();
            var plan=prepared??Clone(CaptureWorkspace());plan.Packages=next;plan.Tags=tags.Tags;
            foreach(var p in plan.Packages){p.GenreIds=tags.Tags.Where(t=>t.Kind=="genre"&&p.Genres.Contains(t.Name)).Select(t=>t.Id).ToList();p.CategoryIds=tags.Tags.Where(t=>t.Kind=="category"&&p.Categories.Contains(t.Name)).Select(t=>t.Id).ToList();}
            var ids=edited.ToList();var chosen=selected.Id;Edits().Commit(new EditBundle {Plan=plan,Tags=tags,Play=play,EditedIds=ids,TagChanges=changes??[],ReconnectedIds=prepared!=null?ids:[]});
            packages=next;catalog=tags;playerDocument=play;lastSavedSignature=null;
            checkpoints=plan.Checkpoints;if(plan.SearchLocations!=null){searchLocations=plan.SearchLocations;UpdateSearchLocationLabel();}foreach(var id in ids)launchFailures.Remove(id);
            if(reconnectedScan!=null){snapshot=reconnectedScan;InspectLibrary(snapshot,packages);}
            selected=packages.Find(p=>p.Id==chosen)??packages.FirstOrDefault()??new();history.Clear();
            if(snapshot!=null)foreach(var p in packages){WorkspaceReconciler.Check(p,checkpoints.Find(c=>c.PackageId==p.Id),snapshot);IdentityReconciler.Flag(p,identityInventory,libraryDocument);}
            FlushWorkspace();Say("저장했습니다");return true;
        }
        catch(Exception ex) when(LibraryFault(ex)){Say("편집 저장 확인 필요 · 기록을 보관했습니다");ErrorInfo("저장 확인","변경 내용을 저장하지 못했습니다.",ex.Message);return false;}
    }
    void RenderPlayer()
    {
        if(playerRoot!=destination)LoadPlayer();
        PlayerContent.Children.Clear();
        var controls=new StackPanel();DockPanel.SetDock(controls,Dock.Top);PlayerContent.Children.Add(controls);
        var top=new DockPanel {Margin=new Thickness(0,0,0,18)};
        var options=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right};
        options.Children.Add(Btn("탐색 위치",async()=>await ManageSearchLocationsAsync()));
        options.Children.Add(Btn(playerView.Layout=="cards"?"목록형 보기":"카드형 보기",()=>{playerView.Layout=playerView.Layout=="cards"?"list":"cards";SaveView();RenderPlayer();}));
        options.Children.Add(Btn(multiSelect?"선택 마침":"다중 선택",()=>{multiSelect=!multiSelect;playerSelection.Clear();RenderPlayer();}));DockPanel.SetDock(options,Dock.Right);top.Children.Add(options);controls.Children.Add(top);
        var searchRow=new DockPanel {Margin=new Thickness(0,0,0,14)};
        var original=new CheckBox {Content="원본 이름 포함",IsChecked=playerView.IncludeOriginal,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(14,0,0,0)};
        DockPanel.SetDock(original,Dock.Right);searchRow.Children.Add(original);
        var sorting=new ComboBox {Width=140,Margin=new Thickness(12,0,0,0),VerticalContentAlignment=VerticalAlignment.Center,ItemsSource=new[]{"별칭순","최근 실행순","추가순"},SelectedIndex=playerView.Sort=="recent"?1:playerView.Sort=="added"?2:0};AutomationProperties.SetName(sorting,"게임 정렬 순서");DockPanel.SetDock(sorting,Dock.Right);searchRow.Children.Add(sorting);
        playerSearch=new TextBox {Text=playerView.Query,ToolTip="별칭 검색 · 별칭이 없으면 원본 이름 검색"};AutomationProperties.SetName(playerSearch,"게임 별칭 검색");searchRow.Children.Add(WithHint(playerSearch,"별칭으로 검색"));controls.Children.Add(searchRow);
        original.Click+=(_,_)=>{playerView.IncludeOriginal=original.IsChecked==true;ResetPlayerSelection();};
        sorting.SelectionChanged+=(_,_)=>{playerView.Sort=sorting.SelectedIndex==1?"recent":sorting.SelectedIndex==2?"added":"alias";ResetPlayerSelection();};
        playerSearch.TextChanged+=(_,_)=>{playerView.Query=playerSearch.Text;ResetPlayerSelection();};
        var groups=new WrapPanel {VerticalAlignment=VerticalAlignment.Center};
        foreach(var group in new[]{("all","전체"),("favorites","즐겨찾기"),("recent","최근 실행"),("hidden","숨김")})
        {var b=Btn(group.Item2,()=>{playerView.Group=group.Item1;ResetPlayerSelection();RenderPlayer();},playerView.Group==group.Item1);groups.Children.Add(b);}top.Children.Add(groups);
        var tagRow=new DockPanel {Margin=new Thickness(0,0,0,8)};
        var reset=Btn("필터 초기화",()=>{playerView.TagIds.Clear();playerView.Query="";playerView.Group="all";playerSelection.Clear();SaveView();RenderPlayer();});reset.VerticalAlignment=VerticalAlignment.Top;reset.Background=Brushes.Transparent;reset.BorderThickness=new Thickness(0);reset.SetResourceReference(ForegroundProperty,"Text.Muted");DockPanel.SetDock(reset,Dock.Right);tagRow.Children.Add(reset);
        var filterBody=new StackPanel {Margin=new Thickness(0,10,0,4)};
        filterTagSearch=new TextBox {Text=tagFilterText,Width=230,HorizontalAlignment=HorizontalAlignment.Left,ToolTip="장르·카테고리 태그 검색"};AutomationProperties.SetName(filterTagSearch,"필터 태그 검색");
        var tagHint=WithHint(filterTagSearch,"태그 이름 검색");tagHint.HorizontalAlignment=HorizontalAlignment.Left;tagHint.Margin=new Thickness(0,0,0,12);filterBody.Children.Add(tagHint);
        playerFilters=new StackPanel();filterBody.Children.Add(playerFilters);
        playerTagExpander=new Expander {Header=FilterHeader(),IsExpanded=tagFinderOpen,Content=filterBody};playerTagExpander.SetResourceReference(ForegroundProperty,"Text.Secondary");
        AutomationProperties.SetName(playerTagExpander,"태그 필터");playerTagExpander.Expanded+=(_,_)=>tagFinderOpen=true;playerTagExpander.Collapsed+=(_,_)=>tagFinderOpen=false;
        tagRow.Children.Add(playerTagExpander);controls.Children.Add(tagRow);RenderPlayerFilters();
        filterTagSearch.TextChanged+=(_,_)=>{tagFilterText=filterTagSearch.Text;RenderPlayerFilters();};
        selectionBar=new StackPanel {Margin=new Thickness(0,4,0,10)};controls.Children.Add(selectionBar);
        playerCount=Text("",12,"#8090A8");controls.Children.Add(playerCount);
        if(playerError.Length>0)controls.Children.Add(Text("실행 설정 확인 필요 · "+playerError,12,"#AA7848"));
        if(File.Exists(Edits().Pending)||ScopeChanges().HasPending)controls.Children.Add(Btn("중단된 편집 저장 복구",async()=>
        {
            try{Edits().Recover();workspaceBlocked=false;LoadTags();var saved=workspaceStore.Load().Document;if(saved!=null)ApplyWorkspace(saved);await RefreshWorkspaceAsync(source,false,false);LoadPlayer();RenderPlayer();}
            catch(Exception ex) when(LibraryFault(ex)){ErrorInfo("편집 복구","편집 내용을 복구하지 못했습니다.",ex.Message);}
        }));
        playerCards=new WrapPanel {Margin=new Thickness(0,14,0,0)};
        playerScroll=new ScrollViewer {Content=playerCards,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};PlayerContent.Children.Add(playerScroll);RenderPlayerItems();
    }
    void ResetPlayerSelection(){playerSelection.Clear();SaveView();RenderPlayerFilters();RenderPlayerItems();}
    void RenderPlayerFilters()
    {
        playerFilters.Children.Clear();
        playerTagExpander.Header=FilterHeader();
        foreach(var kind in new[]{"genre","category"})
        {
            var row=new DockPanel {Margin=new Thickness(0,0,0,6)};var title=Text(KindLabel(kind),12,"#8594A9");title.Width=70;title.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(title,Dock.Left);row.Children.Add(title);
            var strip=new StackPanel {Orientation=Orientation.Horizontal};
            foreach(var tag in catalog.Tags.Where(t=>t.Kind==kind&&t.Name.Contains(tagFilterText,StringComparison.OrdinalIgnoreCase)))
            {
                var chip=new ToggleButton {Content=tag.Name,Style=(Style)FindResource("TagChip"),IsChecked=playerView.TagIds.Contains(tag.Id)};AutomationProperties.SetName(chip,"필터 "+tag.Name);
                chip.Click+=(_,_)=>{if(chip.IsChecked==true)playerView.TagIds.Add(tag.Id);else playerView.TagIds.Remove(tag.Id);ResetPlayerSelection();};strip.Children.Add(chip);
            }
            row.Children.Add(new ScrollViewer {Content=strip,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled});playerFilters.Children.Add(row);
        }
        var active=new WrapPanel();
        foreach(var id in playerView.TagIds.ToList())
        {var tag=catalog.Tags.Find(t=>t.Id==id);active.Children.Add(Btn((tag?.Name??"없는 태그")+" ×",()=>{playerView.TagIds.Remove(id);ResetPlayerSelection();}));}
        playerFilters.Children.Add(active);
    }
    IEnumerable<Package> VisibleGames()
    {
        var result=packages.Where(p=>p.ContentOwnerId.Length==0&&IsAdded(p)&&PlayerRules.Matches(p,PlayFor(p),playerView,catalog.Tags));
        return playerView.Sort=="recent"?result.OrderByDescending(p=>PlayFor(p).LastPlayedUtc).ThenBy(PlayerRules.Title):playerView.Sort=="added"?result.OrderByDescending(p=>PlayFor(p).AddedUtc).ThenBy(PlayerRules.Title):result.OrderBy(PlayerRules.Title,StringComparer.CurrentCultureIgnoreCase);
    }
    void RenderPlayerItems()
    {
        if(playerCards==null)return;var offset=playerScroll.VerticalOffset;var visible=VisibleGames().ToList();
        playerCards.Children.Clear();playerCards.ItemWidth=playerView.Layout=="cards"?252:double.NaN;selectionBar.Children.Clear();
        if(multiSelect)
        {
            var bar=new Grid {Margin=new Thickness(0,8,0,8)};bar.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});bar.ColumnDefinitions.Add(new ColumnDefinition());
            var count=WorkText($"{playerSelection.Count}개 선택",15,"Text.Primary");count.FontWeight=FontWeights.SemiBold;count.VerticalAlignment=VerticalAlignment.Center;count.Margin=new Thickness(0,0,20,0);bar.Children.Add(count);
            var actions=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right};Grid.SetColumn(actions,1);
            actions.Children.Add(Btn("결과 전체 선택",()=>{foreach(var p in visible)playerSelection.Add(p.Id);RenderPlayerItems();}));
            var clear=Btn("선택 해제",()=>{playerSelection.Clear();RenderPlayerItems();});clear.IsEnabled=playerSelection.Count>0;actions.Children.Add(clear);
            var hide=Btn(playerView.Group=="hidden"?"숨김 해제":"숨기기",()=>SetGamesHidden(playerSelection,playerView.Group!="hidden"));hide.IsEnabled=playerSelection.Count>0;actions.Children.Add(hide);
            var edit=Btn("태그 편집",()=>{if(EditGames(packages.Where(p=>playerSelection.Contains(p.Id)).ToList(),batchTags:true))RenderPlayer();},true);edit.IsEnabled=playerSelection.Count>0;edit.Margin=new Thickness(0);actions.Children.Add(edit);
            bar.Children.Add(actions);selectionBar.Children.Add(WorkCard(bar,"Accent.Surface"));
        }
        playerCount.Text=$"게임 {visible.Count}개"+(playerView.TagIds.Count>0?" · 선택 태그 모두 충족":"");
        foreach(var p in visible)playerCards.Children.Add(GameCard(p,playerView.Layout=="list"));
        if(visible.Count==0)
        {
            var empty=new StackPanel();empty.Children.Add(WorkText(searchLocations.Count==0&&packages.Count==0?"탐색 위치를 추가해 게임을 찾아보세요.":playerView.Group=="hidden"?"숨긴 게임이 없습니다.":"조건에 맞는 게임이 없습니다.",14,"Text.Muted"));
            if(searchLocations.Count==0&&packages.Count==0)empty.Children.Add(Btn("탐색 위치",async()=>await ManageSearchLocationsAsync()));
            playerCards.Children.Add(new Border {Padding=new Thickness(24),Child=empty});
        }
        playerScroll.ScrollToVerticalOffset(offset);
    }
    Border GameCard(Package p,bool list)
    {
        var settings=PlayFor(p);var state=PlayerStatus(p);var card=new Grid {Height=list?88:166};
        if(!list)card.Clip=new RectangleGeometry(new Rect(0,0,240,166),9,9);
        var art=list?null:Artwork(settings,240,166);card.Background=list?ThemeBrush("Surface.Secondary"):art??ThemeBrush("Card.Default");
        if(!list)card.Children.Add(new Border {Background=ThemeBrush("Card.Shade"),IsHitTestVisible=false});
        var content=new Grid {Margin=new Thickness(16)};
        var names=new StackPanel {VerticalAlignment=list?VerticalAlignment.Center:VerticalAlignment.Top,Margin=new Thickness(0,0,list?135:28,0)};
        names.Children.Add(new TextBlock {Text=(settings.Favorite?"★ ":"")+PlayerRules.Title(p),FontSize=16,FontWeight=FontWeights.SemiBold,Foreground=list?ThemeBrush("Text.Primary"):ThemeBrush("Card.Title"),TextTrimming=TextTrimming.CharacterEllipsis,MaxHeight=list?24:48,TextWrapping=list?TextWrapping.NoWrap:TextWrapping.Wrap,ToolTip=PlayerRules.Title(p)});
        if(list)names.Children.Add(new TextBlock {Text=state.NeedsAttention?"!  "+state.Label:p.Folder,FontSize=11,Foreground=ThemeBrush(state.NeedsAttention?"Warning.Text":"Text.Muted"),TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(0,4,0,0)});content.Children.Add(names);
        if(!list&&state.NeedsAttention)
        {
            var badge=new Border {CornerRadius=new CornerRadius(4),Padding=new Thickness(7,4,7,4),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(0,0,0,27),IsHitTestVisible=false,Child=new TextBlock {Text="!  "+state.Label,FontSize=11}};badge.SetResourceReference(Border.BackgroundProperty,"Warning.Surface");badge.SetResourceReference(TextElement.ForegroundProperty,"Warning.Text");content.Children.Add(badge);
        }
        var actionLabel=state.NeedsAttention?"문제 해결":state.Problem==LaunchProblem.Setup?"실행 파일 선택":"실행";
        content.Children.Add(new TextBlock {Text=multiSelect?(playerSelection.Contains(p.Id)?"✓  선택됨":"클릭해서 선택"):(state.NeedsAttention?"›  ":"▶  ")+actionLabel,FontSize=12,Foreground=list?ThemeBrush("Text.Secondary"):ThemeBrush("Card.Text"),VerticalAlignment=list?VerticalAlignment.Center:VerticalAlignment.Bottom,HorizontalAlignment=list?HorizontalAlignment.Right:HorizontalAlignment.Left,Margin=list?new Thickness(0,0,42,0):new Thickness(0),Opacity=.9});
        var surface=new Button {Content=content,Style=(Style)FindResource("CardSurface"),ToolTip=multiSelect?"선택 / 선택 해제":state.NeedsAttention?state.Message:state.Problem==LaunchProblem.Setup?"실행 파일 선택":"클릭해서 실행"};AutomationProperties.SetName(surface,PlayerRules.Title(p)+(multiSelect?(playerSelection.Contains(p.Id)?" 선택됨":" 선택하기"):state.NeedsAttention?" 문제 해결":" 실행"));
        surface.Click+=(_,_)=>{if(multiSelect){if(!playerSelection.Add(p.Id))playerSelection.Remove(p.Id);RenderPlayerItems();}else LaunchGame(p);};card.Children.Add(surface);
        if(multiSelect)
        {
            var chosen=playerSelection.Contains(p.Id);
            var glyph=new System.Windows.Shapes.Path {Data=Geometry.Parse(chosen?"M 0,5 L 4,9 L 12,0":"M 0,6 L 12,6 M 6,0 L 6,12"),Width=12,Height=12,Stretch=Stretch.Uniform,StrokeThickness=1.6,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
            glyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,chosen?"Accent.OnFill":"Accent.Text");
            var mark=new Border {Width=28,Height=28,CornerRadius=new CornerRadius(14),HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=list?VerticalAlignment.Center:VerticalAlignment.Top,Margin=new Thickness(0,10,10,10),IsHitTestVisible=false,Child=glyph};
            mark.SetResourceReference(Border.BackgroundProperty,chosen?"Accent.Fill":"Surface.Primary");mark.SetResourceReference(TextElement.ForegroundProperty,chosen?"Accent.OnFill":"Accent.Text");card.Children.Add(mark);
            if(chosen){var outline=new Border {BorderThickness=new Thickness(2),CornerRadius=new CornerRadius(8),IsHitTestVisible=false};outline.SetResourceReference(Border.BorderBrushProperty,"Accent.Fill");card.Children.Add(outline);}
        }
        else
        {
            var menu=new Button {Content="⋯",Style=(Style)FindResource("QuietIcon"),Foreground=list?ThemeBrush("Text.Muted"):ThemeBrush("Card.Text"),HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=list?VerticalAlignment.Center:VerticalAlignment.Top,Margin=new Thickness(0,7,7,list?7:0),ToolTip="상세 보기 · 편집 · 폴더"};AutomationProperties.SetName(menu,PlayerRules.Title(p)+" 메뉴");
            menu.Click+=(_,_)=>
            {
                var popup=new ContextMenu();void Item(string label,Action action){var item=new MenuItem {Header=label};item.Click+=(_,_)=>action();popup.Items.Add(item);}
                Item("상세 보기",()=>ShowGameDetails(p));Item("게임 정보 편집",()=>{if(EditGames([p]))RenderPlayerItems();});Item("게임 폴더 열기",()=>OpenSourceFolder(p.Source));
                Item("실행 설정",()=>{if(EditGames([p],launchOnly:true))RenderPlayerItems();});Item("폴더 다시 연결",async()=>await ReconnectGameAsync(p));
                if(state.NeedsAttention)Item("문제 해결",()=>ShowLaunchProblem(p,state));
                Item(settings.Hidden?"숨김 해제":"숨기기",()=>SetGamesHidden([p.Id],!settings.Hidden));
                Item(settings.Favorite?"즐겨찾기 해제":"즐겨찾기",()=>{var draft=Clone(playerDocument);draft.Games.Single(g=>g.Id==p.Id).Favorite=!settings.Favorite;if(CommitPlayerEdit(Clone(packages),CopyCatalog(),draft,[]))RenderPlayerItems();});
                popup.PlacementTarget=menu;popup.IsOpen=true;
            };card.Children.Add(menu);
        }
        return new Border {Width=list?Math.Max(740,PlayerPanel.ActualWidth-70):240,Child=card,CornerRadius=new CornerRadius(9),ClipToBounds=true,Margin=new Thickness(0,0,12,12),BorderBrush=ThemeBrush("Border.Subtle"),BorderThickness=new Thickness(1)};
    }
    void SetGamesHidden(IEnumerable<string> ids,bool hidden)
    {
        var selectedIds=ids.ToHashSet();if(selectedIds.Count==0)return;
        var draft=Clone(playerDocument);foreach(var g in draft.Games.Where(g=>selectedIds.Contains(g.Id)))g.Hidden=hidden;
        if(CommitPlayerEdit(Clone(packages),CopyCatalog(),draft,[])){playerSelection.Clear();RenderPlayerItems();Say(hidden?"선택한 게임을 숨겼습니다":"숨김을 해제했습니다");}
    }
    Brush? Artwork(GamePlay g,double width,double height,string? external=null)
    {
        try
        {
            if(external==null&&g.Image.Length==0)return null;var path=external??BackupPaths.Child(destination,g.Image);BackupPaths.NoLinks(path);
            if(!File.Exists(path)||new FileInfo(path).Length>50L*1024*1024)return null;
            if(!artworkCache.TryGetValue(path,out var bitmap))
            {var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=1000;image.UriSource=new Uri(path);image.EndInit();image.Freeze();bitmap=image;artworkCache[path]=image;}
            var ratio=bitmap.PixelWidth/(double)bitmap.PixelHeight;var target=width/height;double w=1,h=1;if(ratio>target)w=target/ratio;else h=ratio/target;
            return new ImageBrush(bitmap){ViewboxUnits=BrushMappingMode.RelativeToBoundingBox,Viewbox=new Rect(g.CropX*(1-w),g.CropY*(1-h),w,h),Stretch=Stretch.Fill};
        }
        catch(Exception ex) when(LibraryFault(ex)||ex is NotSupportedException or FileFormatException){return null;}
    }
    void ShowGameDetails(Package p)
    {
        var win=Modal("게임 정보",740,520);var stack=new StackPanel {Margin=new Thickness(26)};
        stack.Children.Add(Text(PlayerRules.Title(p),24));stack.Children.Add(Text(p.Folder,13,"#8393AA"));stack.Children.Add(Text(p.Source,12));
        stack.Children.Add(Text("장르 · "+string.Join(" · ",p.Genres),13));stack.Children.Add(Text("카테고리 · "+string.Join(" · ",p.Categories),13));
        var actions=new WrapPanel {Margin=new Thickness(0,24,0,0)};actions.Children.Add(Btn("내용 · 관련 문서 보기",()=>{var previous=selected;selected=p;try{ShowRealPreview();}finally{selected=previous;}}));
        actions.Children.Add(Btn("편집",()=>{win.Close();if(EditGames([p]))RenderPlayerItems();}));actions.Children.Add(Btn("닫기",()=>win.Close()));stack.Children.Add(actions);win.Content=stack;win.ShowDialog();
    }
    void LaunchGame(Package p)
    {
        if(!IsAdded(p)){selected=p;SetPage("organize");Render();Say("먼저 게임을 추가하세요.");return;}
        if(backupBusy||scanning||workspaceBlocked||playerError.Length>0||File.Exists(Edits().Pending)||ScopeChanges().HasPending){Info("실행 확인","진행 중인 작업 또는 편집 저장을 먼저 완료하세요.");return;}
        var current=PlayerStatus(p);if(current.NeedsAttention){ShowLaunchProblem(p,current);return;}
        try
        {
            var game=PlayFor(p);
            if(libraryDocument.Games.Any(g=>g.Id==p.Id)&&LibraryStore.ReadIdentity(p.Source)?.GameId!=p.Id)throw new GameLaunchException(LaunchProblem.Identity,GameLaunch.State(LaunchProblem.Identity).Message);
            if(game.Executable.Length==0){if(!ChooseFirstExecutable(p))return;game=PlayFor(packages.Single(q=>q.Id==p.Id));}
            var latest=packages.Find(q=>q.Id==p.Id)??p;var verified=GameLaunch.Inspect(latest,game,libraryDocument.Games.Any(g=>g.Id==latest.Id));if(verified.NeedsAttention){ShowLaunchProblem(latest,verified);return;}
            var process=PlayerStore.Launch(latest,game);process.Dispose();
            try{new PlayerStore(destination).Save(playerDocument);Say(PlayerRules.Title(p)+" 실행");}
            catch(Exception ex) when(LibraryFault(ex)){ErrorInfo("실행 기록 저장","게임은 실행됐지만 실행 기록을 저장하지 못했습니다.",ex.Message);}
            RenderPlayerItems();
        }
        catch(Exception ex) when(LibraryFault(ex)||ex is Win32Exception){var issue=GameLaunch.Classify(ex);launchFailures[p.Id]=issue;RenderPlayerItems();ShowLaunchProblem(p,issue,ex.Message);}
    }
}

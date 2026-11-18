using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IndieRun;

public partial class MainWindow
{
    PlayerDocument playerDocument=new();
    PlayerView playerView=new();
    string playerRoot="",playerError="",tagFilterText="";
    bool multiSelect;
    readonly HashSet<string> playerSelection=[];
    TextBox playerSearch=null!,filterTagSearch=null!;
    StackPanel playerFilters=null!,selectionBar=null!;
    WrapPanel playerCards=null!;
    ScrollViewer playerScroll=null!;
    TextBlock playerCount=null!;
    readonly Dictionary<string,BitmapSource> artworkCache=new();
    string DataRoot=>Path.Combine(AppContext.BaseDirectory,"data");
    EditTransaction Edits()=>new(DataRoot);
    static T Clone<T>(T value)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    static Grid WithHint(TextBox box,string hint)
    {
        var grid=new Grid();grid.Children.Add(box);var label=new TextBlock {Text=hint,Foreground=B("#98A6BA"),Margin=new Thickness(11,8,0,0),IsHitTestVisible=false,Visibility=box.Text.Length==0?Visibility.Visible:Visibility.Collapsed};grid.Children.Add(label);
        box.TextChanged+=(_,_)=>label.Visibility=box.Text.Length==0?Visibility.Visible:Visibility.Collapsed;return grid;
    }
    void LoadPlayer()
    {
        try{playerDocument=new PlayerStore(destination).Load();playerRoot=destination;playerError="";}
        catch(Exception ex) when(LibraryFault(ex)){playerDocument=new();playerRoot=destination;playerError=ex.Message;}
        foreach(var p in packages.Where(p=>BackupPaths.Id(p.Id)))if(!playerDocument.Games.Any(g=>g.Id==p.Id))playerDocument.Games.Add(new GamePlay {Id=p.Id});
    }
    void InitializePlayer()
    {
        var path=Path.Combine(DataRoot,"player-view.json");
        try{if(File.Exists(path))playerView=BackupJson.Load<PlayerView>(path,ValidateView);}catch(Exception ex) when(LibraryFault(ex)){Say("보기 설정을 확인해 주세요");Notice.ToolTip=ex.Message;}
        LoadPlayer();SetPage("play");RenderPlayer();
    }
    static void ValidateView(PlayerView v)
    {if(v.Query==null||v.TagIds==null||v.Layout is not ("cards" or "list")||v.Sort is not ("alias" or "recent" or "added")||v.Group is not ("all" or "favorites" or "recent"))throw new InvalidDataException("보기 설정을 확인하세요.");}
    void SaveView()
    {try{BackupJson.Save(Path.Combine(DataRoot,"player-view.json"),playerView,ValidateView);}catch(Exception ex) when(LibraryFault(ex)){Say("보기 설정 저장 실패");Notice.ToolTip=ex.Message;}}
    GamePlay PlayFor(Package p)
    {
        var game=playerDocument.Games.Find(g=>g.Id==p.Id);if(game!=null)return game;
        game=new GamePlay {Id=p.Id};playerDocument.Games.Add(game);return game;
    }
    async void Player_Click(object sender,RoutedEventArgs e)
    {if(backupBusy||scanning)return;await RefreshWorkspaceAsync(source,false,false);LoadPlayer();SetPage("play");RenderPlayer();}
    bool CommitPlayerEdit(List<Package> next,TagCatalog tags,PlayerDocument play,IEnumerable<string> edited,List<TagChange>? changes=null)
    {
        if(workspaceBlocked||workspaceLock==null||tagLoadError.Length>0||playerError.Length>0){Info("저장 확인","데이터 읽기 오류를 먼저 해결해 주세요.");return false;}
        try
        {
            saveTimer.Stop();
            var plan=Clone(CaptureWorkspace());plan.Packages=next;plan.Tags=tags.Tags;
            foreach(var p in plan.Packages){p.GenreIds=tags.Tags.Where(t=>t.Kind=="genre"&&p.Genres.Contains(t.Name)).Select(t=>t.Id).ToList();p.CategoryIds=tags.Tags.Where(t=>t.Kind=="category"&&p.Categories.Contains(t.Name)).Select(t=>t.Id).ToList();}
            var chosen=selected.Id;Edits().Commit(new EditBundle {Plan=plan,Tags=tags,Play=play,EditedIds=edited.ToList(),TagChanges=changes??[]});
            packages=next;catalog=tags;playerDocument=play;lastSavedSignature=null;
            selected=packages.Find(p=>p.Id==chosen)??packages.FirstOrDefault()??new();history.Clear();
            if(snapshot!=null)foreach(var p in packages){WorkspaceReconciler.Check(p,checkpoints.Find(c=>c.PackageId==p.Id),snapshot);IdentityReconciler.Flag(p,identityInventory,libraryDocument);}
            FlushWorkspace();Say("저장했습니다");return true;
        }
        catch(Exception ex) when(LibraryFault(ex)){Say("편집 저장 확인 필요 · 기록을 보관했습니다");Info("저장 확인",ex.Message);return false;}
    }
    void RenderPlayer()
    {
        if(playerRoot!=destination)LoadPlayer();
        PlayerContent.Children.Clear();
        var controls=new StackPanel();DockPanel.SetDock(controls,Dock.Top);PlayerContent.Children.Add(controls);
        var top=new DockPanel {Margin=new Thickness(0,0,0,16)};
        top.Children.Add(Text("어떤 게임을 할까요?",24));
        var options=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right};
        options.Children.Add(Btn(playerView.Layout=="cards"?"목록형 보기":"카드형 보기",()=>{playerView.Layout=playerView.Layout=="cards"?"list":"cards";SaveView();RenderPlayer();}));
        options.Children.Add(Btn(multiSelect?"선택 마침":"여러 개 선택",()=>{multiSelect=!multiSelect;playerSelection.Clear();RenderPlayer();}));top.Children.Add(options);controls.Children.Add(top);
        var searchRow=new DockPanel {Margin=new Thickness(0,0,0,14)};
        var original=new CheckBox {Content="원본 이름 포함",IsChecked=playerView.IncludeOriginal,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(14,0,0,0)};
        DockPanel.SetDock(original,Dock.Right);searchRow.Children.Add(original);
        var sorting=new ComboBox {Width=140,Margin=new Thickness(12,0,0,0),VerticalContentAlignment=VerticalAlignment.Center,ItemsSource=new[]{"별칭순","최근 실행순","추가순"},SelectedIndex=playerView.Sort=="recent"?1:playerView.Sort=="added"?2:0};DockPanel.SetDock(sorting,Dock.Right);searchRow.Children.Add(sorting);
        playerSearch=new TextBox {Text=playerView.Query,ToolTip="별칭 검색 · 별칭이 없으면 원본 이름 검색"};AutomationProperties.SetName(playerSearch,"게임 별칭 검색");searchRow.Children.Add(WithHint(playerSearch,"별칭으로 검색"));controls.Children.Add(searchRow);
        original.Click+=(_,_)=>{playerView.IncludeOriginal=original.IsChecked==true;ResetPlayerSelection();};
        sorting.SelectionChanged+=(_,_)=>{playerView.Sort=sorting.SelectedIndex==1?"recent":sorting.SelectedIndex==2?"added":"alias";ResetPlayerSelection();};
        playerSearch.TextChanged+=(_,_)=>{playerView.Query=playerSearch.Text;ResetPlayerSelection();};
        var groups=new WrapPanel {Margin=new Thickness(0,0,0,12)};
        foreach(var group in new[]{("all","전체"),("favorites","즐겨찾기"),("recent","최근 실행")})
        {var b=Btn(group.Item2,()=>{playerView.Group=group.Item1;ResetPlayerSelection();RenderPlayer();},playerView.Group==group.Item1);groups.Children.Add(b);}controls.Children.Add(groups);
        var tagRow=new DockPanel {Margin=new Thickness(0,0,0,8)};
        var reset=Btn("필터 초기화",()=>{playerView.TagIds.Clear();playerView.Query="";playerView.Group="all";playerSelection.Clear();SaveView();RenderPlayer();});DockPanel.SetDock(reset,Dock.Right);tagRow.Children.Add(reset);
        filterTagSearch=new TextBox {Text=tagFilterText,Width=210,HorizontalAlignment=HorizontalAlignment.Left,ToolTip="장르·카테고리 태그 검색"};AutomationProperties.SetName(filterTagSearch,"필터 태그 검색");var tagHint=WithHint(filterTagSearch,"태그 찾기");tagHint.HorizontalAlignment=HorizontalAlignment.Left;tagRow.Children.Add(tagHint);controls.Children.Add(tagRow);
        filterTagSearch.TextChanged+=(_,_)=>{tagFilterText=filterTagSearch.Text;RenderPlayerFilters();};
        playerFilters=new StackPanel();controls.Children.Add(playerFilters);RenderPlayerFilters();
        selectionBar=new StackPanel {Margin=new Thickness(0,4,0,10)};controls.Children.Add(selectionBar);
        playerCount=Text("",12,"#8090A8");controls.Children.Add(playerCount);
        if(playerError.Length>0)controls.Children.Add(Text("실행 설정 확인 필요 · "+playerError,12,"#AA7848"));
        if(File.Exists(Edits().Pending))controls.Children.Add(Btn("중단된 편집 저장 복구",async()=>
        {
            try{Edits().Recover();workspaceBlocked=false;LoadTags();var saved=workspaceStore.Load().Document;if(saved!=null)ApplyWorkspace(saved);await RefreshWorkspaceAsync(source,false,false);LoadPlayer();RenderPlayer();}
            catch(Exception ex) when(LibraryFault(ex)){Info("편집 복구",ex.Message);}
        }));
        playerCards=new WrapPanel {Margin=new Thickness(0,14,0,0)};
        playerScroll=new ScrollViewer {Content=playerCards,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};PlayerContent.Children.Add(playerScroll);RenderPlayerItems();
    }
    void ResetPlayerSelection(){playerSelection.Clear();SaveView();RenderPlayerFilters();RenderPlayerItems();}
    void RenderPlayerFilters()
    {
        playerFilters.Children.Clear();
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
        var result=packages.Where(p=>PlayerRules.Matches(p,PlayFor(p),playerView,catalog.Tags));
        return playerView.Sort=="recent"?result.OrderByDescending(p=>PlayFor(p).LastPlayedUtc).ThenBy(PlayerRules.Title):playerView.Sort=="added"?result.OrderByDescending(p=>PlayFor(p).AddedUtc).ThenBy(PlayerRules.Title):result.OrderBy(PlayerRules.Title,StringComparer.CurrentCultureIgnoreCase);
    }
    void RenderPlayerItems()
    {
        if(playerCards==null)return;var offset=playerScroll.VerticalOffset;var visible=VisibleGames().ToList();
        playerCards.Children.Clear();playerCards.ItemWidth=playerView.Layout=="cards"?252:double.NaN;selectionBar.Children.Clear();
        if(multiSelect)
        {
            var bar=new WrapPanel();bar.Children.Add(Text($"{playerSelection.Count}개 선택",14));
            bar.Children.Add(Btn($"현재 결과 {visible.Count}개 전체 선택",()=>{foreach(var p in visible)playerSelection.Add(p.Id);RenderPlayerItems();}));
            bar.Children.Add(Btn("선택 해제",()=>{playerSelection.Clear();RenderPlayerItems();}));
            var edit=Btn("태그 편집",()=>{if(EditGames(packages.Where(p=>playerSelection.Contains(p.Id)).ToList()))RenderPlayerItems();},true);edit.IsEnabled=playerSelection.Count>0;bar.Children.Add(edit);selectionBar.Children.Add(bar);
        }
        playerCount.Text=$"게임 {visible.Count}개"+(playerView.TagIds.Count>0?" · 선택 태그 모두 충족":"");
        foreach(var p in visible)playerCards.Children.Add(GameCard(p,playerView.Layout=="list"));
        if(visible.Count==0)playerCards.Children.Add(new Border {Padding=new Thickness(24),Child=Text("조건에 맞는 게임이 없습니다. 검색어 또는 태그를 해제해 보세요.",14,"#8594A9")});
        playerScroll.ScrollToVerticalOffset(offset);
    }
    Border GameCard(Package p,bool list)
    {
        var settings=PlayFor(p);var card=new Grid {Height=list?76:166};
        if(!list)card.Clip=new RectangleGeometry(new Rect(0,0,240,166),9,9);
        card.Background=list?B("#F2F5FA"):Artwork(settings,240,166)??B("#283C60");
        if(!list)card.Children.Add(new Border {Background=B("#071222"),Opacity=.58});
        var body=new DockPanel {Margin=new Thickness(14)};card.Children.Add(body);
        var actions=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=list?VerticalAlignment.Center:VerticalAlignment.Bottom};DockPanel.SetDock(actions,Dock.Right);
        if(multiSelect)
        {
            var check=new CheckBox {IsChecked=playerSelection.Contains(p.Id),VerticalAlignment=VerticalAlignment.Center};AutomationProperties.SetName(check,PlayerRules.Title(p)+" 선택");
            check.Click+=(_,_)=>{if(check.IsChecked==true)playerSelection.Add(p.Id);else playerSelection.Remove(p.Id);RenderPlayerItems();};actions.Children.Add(check);
            card.PreviewMouseLeftButtonUp+=(_,e)=>{e.Handled=true;if(playerSelection.Contains(p.Id))playerSelection.Remove(p.Id);else playerSelection.Add(p.Id);RenderPlayerItems();};
        }
        else
        {
            var menu=Btn("⋯",()=>{});menu.Padding=new Thickness(8,5,8,5);menu.ToolTip="상세 보기 · 편집 · 폴더";AutomationProperties.SetName(menu,PlayerRules.Title(p)+" 메뉴");
            menu.Click+=(_,_)=>
            {
                var popup=new ContextMenu();void Item(string label,Action action){var item=new MenuItem {Header=label};item.Click+=(_,_)=>action();popup.Items.Add(item);}
                Item("상세 보기",()=>ShowGameDetails(p));Item("게임 정보 편집",()=>{if(EditGames([p]))RenderPlayerItems();});Item("게임 폴더 열기",()=>OpenSourceFolder(p.Source));
                Item(settings.Favorite?"즐겨찾기 해제":"즐겨찾기",()=>{var draft=Clone(playerDocument);draft.Games.Single(g=>g.Id==p.Id).Favorite=!settings.Favorite;if(CommitPlayerEdit(Clone(packages),CopyCatalog(),draft,[]))RenderPlayerItems();});
                popup.PlacementTarget=menu;popup.IsOpen=true;
            };actions.Children.Add(menu);
            var run=Btn("▶ 실행",()=>LaunchGame(p),true);AutomationProperties.SetName(run,PlayerRules.Title(p)+" 실행");run.Padding=new Thickness(12,6,12,6);actions.Children.Add(run);
        }
        if(list)body.Children.Add(actions);else {DockPanel.SetDock(actions,Dock.Bottom);body.Children.Add(actions);}
        var names=new StackPanel {VerticalAlignment=list?VerticalAlignment.Center:VerticalAlignment.Top,Margin=new Thickness(0,0,8,0)};
        names.Children.Add(new TextBlock {Text=(settings.Favorite?"★ ":"")+PlayerRules.Title(p),FontSize=list?16:17,FontWeight=FontWeights.SemiBold,Foreground=list?B("#25334B"):Brushes.White,TextTrimming=TextTrimming.CharacterEllipsis,MaxHeight=48,TextWrapping=TextWrapping.Wrap});
        if(list)names.Children.Add(Text(p.Folder,11,"#8493A9"));body.Children.Add(names);
        return new Border {Width=list?Math.Max(740,PlayerPanel.ActualWidth-70):240,Child=card,CornerRadius=new CornerRadius(9),ClipToBounds=true,Margin=new Thickness(0,0,12,12),BorderBrush=playerSelection.Contains(p.Id)?B("#4167DA"):B("#E4E9F2"),BorderThickness=new Thickness(playerSelection.Contains(p.Id)?2:1)};
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
        if(backupBusy||scanning||workspaceBlocked||playerError.Length>0||File.Exists(Edits().Pending)){Info("실행 확인","진행 중인 작업 또는 편집 저장을 먼저 완료하세요.");return;}
        if(p.ReviewState is "identity" or "missing" or "unverified"){Info("게임 위치 확인",p.ReviewReason+"\n보관함에서 다시 확인하세요.");return;}
        try
        {
            var game=PlayFor(p);
            if(libraryDocument.Games.Any(g=>g.Id==p.Id)&&LibraryStore.ReadIdentity(p.Source)?.GameId!=p.Id)throw new IOException("게임 식별 파일이 변경됐습니다. 보관함에서 위치를 확인하세요.");
            if(game.Executable.Length==0){if(!EditGames([p],true))return;game=PlayFor(packages.Single(q=>q.Id==p.Id));}
            var process=PlayerStore.Launch(p,game);process.Dispose();new PlayerStore(destination).Save(playerDocument);Say(PlayerRules.Title(p)+" 실행을 요청했습니다");RenderPlayerItems();
        }
        catch(Exception ex) when(LibraryFault(ex)||ex is Win32Exception){Info("실행 확인",ex.Message);}
    }
}

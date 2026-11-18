using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace IndieRun;

public partial class MainWindow : Window
{
    List<Package> packages = [];
    readonly Stack<string> history = new();
    Package selected = null!;
    bool rendering;
    bool running;
    string destination = @"D:\IndieLibrary";
    string source = @"D:\Downloads";

    static Brush B(string value) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    static TextBlock Text(string value, double size = 13, string color = "#33435C") => new() { Text = value, FontSize = size, Foreground = color=="#33435C"?ThemeBrush("Text.Primary"):B(color), TextWrapping = TextWrapping.Wrap };
    static Button Btn(string label, Action action, bool primary = false)
    {
        var b = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0) };
        if (primary) b.Style = (Style)Application.Current.FindResource("Primary");
        b.Click += (_, _) => action();
        return b;
    }
    static Border Badge(string text, string color = "#5673AC") => new() { Background = B("#EFF3FA"), CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 3, 7, 3), Child = Text(text, 11, color), Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    public MainWindow()
    {
        InitializeComponent();
        LoadTags();
        InitializeWorkspace();
        selected = new Package();
        Render();
        ContentRendered += async (_,_) => {await StartWorkspaceAsync();if(IsVisible&&sessionReady)InitializePlayer();};
    }

    void LoadSamples()
    {
        packages = [
            new() { Id="A", Folder="MidnightTrain_v1.2", Name="MidnightTrain_v1.2", Alias="기차에서 도망치는 게임", Source=@"D:\Downloads\MidnightTrain_v1.2", Size="1.8 GB", Gb=1.8, Color="#527BDC", ArtTitle="MIDNIGHT\nTRAIN", Description="심야 열차를 배경으로 단서를 찾는 공포 퍼즐 게임.\n샘플 README: Game/Train.exe를 실행하세요. Comic 폴더에는 디지털 만화가 있습니다.", Genres=["공포","퍼즐"], Comic=true },
            new() { Id="B", Folder="LittleGarden_win", Name="LittleGarden_win", Alias="작은 정원", Source=@"D:\Downloads\LittleGarden_win", Size="420 MB", Gb=.42, Color="#548B75", ArtTitle="little\ngarden", Description="작은 정원을 가꾸며 퍼즐을 푸는 게임.\n샘플 README: Garden.exe 실행. 조작은 마우스를 사용합니다.", Genres=["퍼즐"] },
            new() { Id="C", Folder="itch_bundle_07", Name="itch_bundle_07", Alias="여름 잼 게임 묶음", Source=@"D:\Downloads\itch_bundle_07", Size="860 MB", Gb=.86, Color="#9471BF", ArtTitle="SUMMER\nJAM / 07", Description="두 개의 독립된 게임이 들어 있는 샘플 패키지.\nNightWalk와 CloudHop을 각각 등록하거나 이동 패키지를 분리할 수 있습니다.", Multi=true }
        ];
        foreach(var p in packages) p.Genres = p.Genres.Where(n=>catalog.Tags.Any(t=>t.Kind=="genre" && t.Name==n)).ToList();
        selected = packages[0]; history.Clear(); Render();
    }
    void SaveUndo() => history.Push(JsonSerializer.Serialize(CaptureWorkspace()));
    void Say(string value) => Notice.Text = value;

    void Render()
    {
        rendering=true;
        RenderOrganize();
        RefreshReviewDisplay();
        rendering=false;QueueWorkspaceSave();
    }
    void TreeRow(string label, string role, bool bold = false)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = Text(label, 12); if (bold) t.FontWeight = FontWeights.SemiBold;
        grid.Children.Add(t);
        var badge = Badge(role); Grid.SetColumn(badge, 1); grid.Children.Add(badge);
        TreeRows.Children.Add(grid);
    }
    void RenderCandidates()=>RenderOrganizeCandidates();
    void RenderPlan()=>RenderOrganizeFooter();
    void Search_Changed(object sender, TextChangedEventArgs e) { if (selected != null) RenderCandidates(); if(!rendering)QueueWorkspaceSave(); }
    void Alias_Changed(object sender, RoutedEventArgs e)
    {
        aliasEditing=false;
        if (rendering || selected.Alias == Alias.Text) return;
        SaveUndo(); selected.Alias = Alias.Text; SelectedTitle.Text = Alias.Text; RenderCandidates(); RenderPlan(); Say("별칭을 계획에 반영했습니다");
    }
    static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' ')) return "사용할 수 없는 폴더명입니다. 이동 전에 수정해 주세요.";
        if (name is "." or ".." || System.Text.RegularExpressions.Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return "Windows 예약 이름은 사용할 수 없습니다.";
        return name.Any(c => c > 127) ? "주의: 비ASCII 문자가 포함되어 일부 게임에서 경로 문제가 생길 수 있습니다." : "";
    }
    void Physical_Changed(object sender, RoutedEventArgs e)
    {
        physicalEditing=false;
        if (rendering || selected.Name == PhysicalName.Text) return;
        SaveUndo(); selected.Name = PhysicalName.Text; NameWarning.Text = ValidateName(selected.Name); RenderPlan(); Say("폴더 이름 변경을 예약했습니다");
    }
    void Content_Click(object sender, RoutedEventArgs e) { SaveUndo(); selected.LinkOnly = !selected.LinkOnly; Render(); Say("이동 범위만 변경했습니다 · 콘텐츠 관계는 유지됩니다"); }
    void Relation_Click(object sender,RoutedEventArgs e) { SaveUndo(); selected.ComicRelated=!selected.ComicRelated;Render();Say("논리 관계만 변경했습니다 · 이동 범위는 유지됩니다"); }
    void Split_Click(object sender, RoutedEventArgs e)
    {
        SaveUndo(); var originalSource=selected.Source; selected.Multi = false; selected.Alias = "밤길을 걷는 게임";
        selected.Name = "NightWalk"; selected.Folder = "NightWalk"; selected.Source += @"\NightWalk"; selected.Gb = .5; selected.Size = "500 MB"; selected.ArtTitle = "NIGHT\nWALK";
        packages.Add(new Package { Id="D", Folder="CloudHop", Name="CloudHop", Alias="구름 점프 게임", Source=originalSource+@"\CloudHop", Gb=.36, Size="360 MB", Color="#C18554", ArtTitle="CLOUD\nHOP", Description="여름 잼 묶음에서 분리한 독립 게임입니다.", Genres=[.. selected.Genres], Categories=[.. selected.Categories] });
        Render(); Say("각 하위 폴더를 별도 패키지로 분리했습니다 · 되돌리기 가능");
    }
    void AddPackage(Package p) { if(scanning||p.Excluded||p.ReviewState.Length>0||!packages.Contains(p))return; if (p.Planned || p.Done) { Say("이미 추가된 패키지입니다"); return; } SaveUndo(); p.Planned = true; Render(); Say("이동 계획에 추가했습니다 · 아직 파일은 이동하지 않습니다"); }
    void Add_Click(object sender, RoutedEventArgs e) => AddPackage(selected);
    void DropZone_Over(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent("IndieRun.Package") ? DragDropEffects.Move : DragDropEffects.None; DropZone.Background = B("#E6EEFF"); e.Handled = true; }
    void RestoreDropZone() { DropZone.Background=B("#EDF2FC");DropZone.BorderBrush=B("#C8D6EE");DropZone.BorderThickness=new Thickness(1);DropTitle.Text="게임 카드를 여기에"; }
    void DropZone_Leave(object sender, DragEventArgs e) => RestoreDropZone();
    void DropZone_Drop(object sender, DragEventArgs e) { RestoreDropZone(); if (e.Data.GetData("IndieRun.Package") is string id && packages.Find(p => p.Id == id) is Package p) AddPackage(p); e.Handled = true; }
    void Undo_Click(object sender, RoutedEventArgs e)
    {
        if(backupBusy||scanning||workspaceBlocked)return;
        if(history.Count==0){Say("되돌릴 편집이 없습니다");return;}
        var doc=JsonSerializer.Deserialize<WorkspaceDocument>(history.Pop())!;var id=selected.Id;
        packages=doc.Packages;checkpoints=doc.Checkpoints;selected=packages.Find(p=>p.Id==id&&!p.Excluded)??packages.FirstOrDefault(p=>!p.Excluded)??new Package();
        if(snapshot!=null)foreach(var p in packages)WorkspaceReconciler.Check(p,checkpoints.Find(c=>c.PackageId==p.Id),snapshot);
        Render();Say("이전 편집 상태로 되돌렸습니다");
    }
    async void Reset_Click(object sender, RoutedEventArgs e) { if(scanning){scanCancellation?.Cancel();return;} if (running) return; await ScanAsync(source,true); }
    void Destination_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title="보관 위치 미리보기 · 파일을 변경하지 않습니다" };
        if (picker.ShowDialog(this) == true) { destination=picker.FolderName; DestinationLabel.Text=destination; QueueWorkspaceSave(); Say(destination.Any(c=>c>127) ? "경로에 비ASCII 문자가 있습니다 · 호환성 주의" : "목적지 미리보기를 변경했습니다"); }
    }
    async void Source_Click(object sender,RoutedEventArgs e)=>await ManageSearchLocationsAsync();
    void Organize_Click(object sender, RoutedEventArgs e) { SetPage("organize"); Render(); Say("게임을 추가해도 폴더는 이동하지 않습니다."); }
    void Jobs_Click(object sender, RoutedEventArgs e)
    {
        SetPage("jobs"); RenderJobs();
    }
    void RenderJobs() => RenderBackupJobs();
    Window Modal(string title, double width=740, double height=570)
    {
        return new Window { Title=title,Owner=this,Width=width,Height=height,MinWidth=560,MinHeight=450,WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false,Background=ThemeBrush("Surface.Primary") };
    }
    void Preview_Click(object sender, RoutedEventArgs e)
    {
        if(!HasSelection)return;
        if(selected.IsReal){ShowRealPreview();return;}
        var p=selected; var win=Modal("내용 확인 · 읽기 전용",850,620);
        var dock=new DockPanel { Margin=new Thickness(25) };
        var title=new StackPanel(); title.Children.Add(Text(p.Alias.Length>0?p.Alias:p.Folder,23)); title.Children.Add(Text(p.Source,12,"#8794A8")); DockPanel.SetDock(title,Dock.Top); dock.Children.Add(title);
        var bottom=new DockPanel { Margin=new Thickness(0,20,0,0) }; DockPanel.SetDock(bottom,Dock.Bottom);
        var close=Btn("작업대로 돌아가기",()=>win.Close(),true); DockPanel.SetDock(close,Dock.Right); bottom.Children.Add(close); bottom.Children.Add(Text("샘플 자료 · 읽기 전용",12,"#8B96A5")); dock.Children.Add(bottom);
        var grid=new Grid { Margin=new Thickness(0,20,0,0) }; grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(190) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var nav=new StackPanel(); var view=new StackPanel { Margin=new Thickness(22,0,0,0) }; Grid.SetColumn(view,1);
        void ShowReadme() { view.Children.Clear();view.Children.Add(Text("Readme.txt",17)); view.Children.Add(new TextBlock {Text=p.Description,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,20,0,0),LineHeight=26}); }
        void ShowArt()
        {
            view.Children.Clear();
            var art=new Grid { Height=270,Background=new LinearGradientBrush(((SolidColorBrush)B(p.Color)).Color,Color.FromRgb(23,34,55),70) };
            art.Children.Add(new TextBlock {Text=p.ArtTitle,Foreground=Brushes.White,FontSize=42,FontWeight=FontWeights.Light,Margin=new Thickness(28),VerticalAlignment=VerticalAlignment.Center,LineHeight=52});
            view.Children.Add(art);view.Children.Add(new TextBlock {Text="가상 게임 표지 · 실제 게임 자료가 아닙니다",Foreground=B("#8794A8"),Margin=new Thickness(0,12,0,0),FontSize=12});
        }
        nav.Children.Add(Text("파일 선택",12,"#8390A3"));
        foreach(var b in new[]{Btn("cover.png",ShowArt),Btn("Readme.txt",ShowReadme),Btn("실행 후보 확인",()=>{view.Children.Clear();view.Children.Add(Text(p.Multi?"NightWalk.exe\nCloudHop.exe":p.Comic?"Game/Train.exe":p.Id=="C"?"NightWalk.exe":p.Id=="D"?"CloudHop.exe":"Garden.exe",18));view.Children.Add(Text("시안에서는 실행 후보만 표시합니다. 실제 게임은 실행하지 않습니다.",13,"#8390A3"));})}) { b.HorizontalContentAlignment=HorizontalAlignment.Left;b.Margin=new Thickness(0,10,0,0);nav.Children.Add(b); }
        grid.Children.Add(nav);grid.Children.Add(view);dock.Children.Add(grid);win.Content=dock;ShowArt();win.ShowDialog();
    }
    void Review_Click(object sender,RoutedEventArgs e)
    {
        var list=packages.Where(p=>!p.Excluded&&p.Planned&&!p.Done).ToList(); if(list.Count==0) return;
        if(list.Any(p=>p.IsReal)){ShowReadOnlyPlan(list);return;}
        if(list.Any(p=>ValidateName(p.Name).StartsWith("사용")||ValidateName(p.Name).StartsWith("Windows"))) { Info("이름 확인 필요","사용할 수 없는 폴더명이 있습니다. 세부 정리에서 수정해 주세요.");return; }
        if(list.GroupBy(p=>p.Name,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1)) { Info("이름 충돌","같은 이름의 목적지 폴더가 있습니다. 이름을 다르게 지정해 주세요.");return; }
        var win=Modal("이동 계획 확인",780,650);var dock=new DockPanel {Margin=new Thickness(28)};
        var top=new StackPanel();top.Children.Add(Text("이대로 정리할까요?",25));top.Children.Add(new TextBlock {Text=$"패키지 {list.Count}개 · 게임 {list.Sum(p=>p.Multi?2:1)}개 · 폴더명 변경 {list.Count(p=>p.Name!=p.Folder)}건",Margin=new Thickness(0,10,0,18)});
        top.Children.Add(Text("목적지  "+destination,13));
        top.Children.Add(Text($"추가 백업 공간 약 {list.Sum(p=>p.Gb):0.##} GB · 미분류 {list.Count(p=>p.Genres.Count==0)}개",13,"#6C7D96"));
        var warning=new Border { Background=B("#FFF6E6"),Padding=new Thickness(14),CornerRadius=new CornerRadius(7),Margin=new Thickness(0,18,0,18) };
        warning.Child=Text("이동 전 백업을 생성합니다. 작업 중에는 원본·목적지·백업이 함께 존재할 수 있습니다. 백업은 직접 삭제할 때까지 유지됩니다.",12,"#946C30");top.Children.Add(warning);
        if(destination.Any(c=>c>127)||list.Any(p=>p.Name.Any(c=>c>127))) top.Children.Add(Text("주의: 최종 경로에 비ASCII 문자가 포함되어 있습니다.",12,"#A46928"));
        DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);
        var bottom=new StackPanel {Margin=new Thickness(0,16,0,0)};DockPanel.SetDock(bottom,Dock.Bottom);
        var progress=new TextBlock {Text="실제 백업·이동 없이 화면 흐름만 체험합니다.",Foreground=B("#7D8BA1"),Margin=new Thickness(0,0,0,12)};bottom.Children.Add(progress);
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        buttons.Children.Add(Btn("계속 편집",()=>win.Close()));
        var start=new Button {Content="백업 후 이동 · 체험",Style=(Style)FindResource("Primary")};buttons.Children.Add(start);bottom.Children.Add(buttons);dock.Children.Add(bottom);
        var details=new StackPanel();
        foreach(var p in list)
        {
            var block=new StackPanel();block.Children.Add(Text(p.Alias.Length>0?p.Alias:p.Name,15));block.Children.Add(Text((p.LinkOnly?Path.Combine(p.Source,"Game"):p.Source)+"\n↓\n"+Path.Combine(destination,p.Name),12,"#7A889E"));
            block.Children.Add(Text($"장르: {(p.Genres.Count==0?"미분류":string.Join(", ",p.Genres))} · "+(p.LinkOnly?"Game 폴더만 이동 · 만화/설명서 원본 유지":"패키지 범위 함께 이동"),12,"#6C819D"));
            details.Children.Add(new Border {Child=block,Padding=new Thickness(12),Background=B("#F6F8FC"),Margin=new Thickness(0,0,0,10),CornerRadius=new CornerRadius(6)});
        }
        dock.Children.Add(new ScrollViewer {Content=details});win.Content=dock;
        var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(650)};int tick=0;
        string[] stages=["백업 생성 중… (시뮬레이션)","백업 검증 중… (시뮬레이션)","파일 이동 중… (시뮬레이션)","메타데이터 반영 중… (시뮬레이션)"];
        timer.Tick+=(_,_)=>
        {
            if(tick<stages.Length) {progress.Text=stages[tick++];return;}
            timer.Stop();foreach(var p in list) {p.Done=true;p.Planned=false;p.Backup=true;}history.Clear();running=false;win.Close();Render();Jobs_Click(this,new RoutedEventArgs());Say("시뮬레이션 완료 · 실제 파일 변경 없음");
        };
        start.Click+=(_,_)=>{running=true;start.IsEnabled=false;buttons.IsEnabled=false;timer.Start();};
        win.Closed+=(_,_)=>{timer.Stop();running=false;RenderPlan();};win.ShowDialog();
    }
}

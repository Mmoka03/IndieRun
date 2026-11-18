using IndieRun;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
class Program
{
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static void Set(object o,string name,object? value)=>o.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(o,value);
    static T Get<T>(object o,string name)=>(T)o.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(o)!;
    static object? Call(object o,string name,params object?[] args)=>o.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly)!.Invoke(o,args);
    static void Wait(Task task){var frame=new DispatcherFrame();task.ContinueWith(_=>Application.Current.Dispatcher.BeginInvoke(new Action(()=>frame.Continue=false)));Dispatcher.PushFrame(frame);task.GetAwaiter().GetResult();}
    static Package Game(string root,int n,bool added=false)=>new(){Id=Guid.NewGuid().ToString("N"),IsReal=true,Registered=added,Source=Path.Combine(root,"Game"+n),DetectedRoot=Path.Combine(root,"Game"+n),SearchRoot=root,Name="Game"+n,Folder="Game"+n,Alias="테스트 게임 "+n};
    static void Close(MainWindow w){Set(w,"closingWithoutSave",true);w.Close();}
    static void Refuse<T>(Action action) where T:Exception{try{action();}catch(T){return;}throw new Exception("Expected "+typeof(T).Name);}
    static IEnumerable<DependencyObject> Visual(DependencyObject root){yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var v in Visual(VisualTreeHelper.GetChild(root,i)))yield return v;}
    [STAThread]static int Main(string[] args)
    {
        var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);var results=new List<object>();int failed=0;
        void Test(string name,Action<string> run){var watch=Stopwatch.StartNew();try{var dir=Path.Combine(root,name);Directory.CreateDirectory(dir);run(dir);results.Add(new{name,passed=true,ms=watch.ElapsedMilliseconds});Console.WriteLine("PASS "+name);}catch(Exception ex){failed++;results.Add(new{name,passed=false,error=ex.ToString()});Console.WriteLine("FAIL "+name+" "+ex.Message);}}
        Test("entry-budget-does-not-return-partial-results",dir=>{for(int i=0;i<10;i++)File.WriteAllText(Path.Combine(dir,i+".txt"),"fixture");Refuse<ScanLimitException>(()=>GameScanner.Scan(dir,budget:new ScanBudget(3)));});
        Test("candidate-budget-counts-folders",dir=>{for(int i=0;i<3;i++){var d=Path.Combine(dir,"Game"+i);Directory.CreateDirectory(d);File.WriteAllText(Path.Combine(d,"Game.exe"),"harmless fixture");}Refuse<ScanLimitException>(()=>GameScanner.Scan(dir,budget:new ScanBudget(maxCandidates:2)));});
        Test("time-budget",dir=>{File.WriteAllText(Path.Combine(dir,"file"),"fixture");Refuse<ScanLimitException>(()=>GameScanner.Scan(dir,budget:new ScanBudget(maxTime:TimeSpan.Zero)));});
        Test("cancel-mid-enumeration",dir=>{for(int i=0;i<700;i++)File.WriteAllText(Path.Combine(dir,i+".txt"),"fixture");using var c=new CancellationTokenSource();Refuse<OperationCanceledException>(()=>GameScanner.Scan(dir,cancellation:c.Token,progress:new Counter(_=>c.Cancel())));});
        Test("budget-is-shared-across-roots",dir=>{var b=new ScanBudget(3);foreach(var d in new[]{"A","B"}){Directory.CreateDirectory(Path.Combine(dir,d));for(int i=0;i<2;i++)File.WriteAllText(Path.Combine(dir,d,i+".txt"),"fixture");}GameScanner.Scan(Path.Combine(dir,"A"),budget:b);Refuse<ScanLimitException>(()=>GameScanner.Scan(Path.Combine(dir,"B"),budget:b));});
        Test("background-preparation-cancellation-keeps-input",dir=>{var scan=new ScanSnapshot{Root=dir};var p=Game(dir,1);scan.Directories.Add(p.Source);scan.Files.Add(new(Path.Combine(p.Source,"Game.exe"),0,DateTime.UtcNow));var old=new List<Package>{p};using var c=new CancellationTokenSource();c.Cancel();Refuse<OperationCanceledException>(()=>ScanPreparation.Build(scan,old,[],[],Path.Combine(dir,"library"),c.Token));Check(old.Count==1,"Input changed");});
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};var styles=XDocument.Load(Path.Combine(AppContext.BaseDirectory,"styles-source.xaml")).Root!.Elements().Single().Elements().Single();app.Resources=(ResourceDictionary)XamlReader.Parse(styles.ToString(),new ParserContext{BaseUri=new Uri(AppContext.BaseDirectory)});SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Test("startup-does-not-scan-saved-range",dir=>
        {
            var input=Path.Combine(dir,"input");Directory.CreateDirectory(input);var games=Enumerable.Range(0,1500).Select(i=>Game(input,i)).ToList();
            var plan=new WorkspaceDocument{SearchRoot=input,SearchLocations=[new(){Path=input}],Destination=Path.Combine(dir,"library"),BackupRoot=Path.Combine(dir,"backups"),Packages=games};var store=new WorkspaceStore(Path.Combine(dir,"data","workspace.json"));store.Save(plan);
            var w=new MainWindow();try{new System.Windows.Interop.WindowInteropHelper(w).EnsureHandle();Set(w,"workspaceStore",store);int ticks=0;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(10)};timer.Tick+=(_,_)=>ticks++;timer.Start();Wait((Task)Call(w,"StartWorkspaceAsync")!);timer.Stop();Check(Get<ScanSnapshot?>(w,"snapshot")==null&&!Get<bool>(w,"scanning"),"Startup scanned");Check(Get<List<Package>>(w,"packages").Count==1500,"Saved list lost");var list=(StackPanel)w.FindName("CandidateList");var review=(StackPanel)w.FindName("CandidateReviewList");Check(list.Children.Count+review.Children.Count<=62,"Created all 1500 rows");Check(Get<List<Package>>(w,"packages").All(p=>p.ReviewState=="cached"),"Stale folder actions enabled");Check(ticks>0,"Dispatcher never yielded");}finally{Close(w);}
        });
        Test("candidate-pages-and-query",dir=>{var w=new MainWindow();try{var games=Enumerable.Range(0,145).Select(i=>Game(dir,i)).ToList();foreach(var p in games)p.ReviewState="cached";Set(w,"packages",games);Set(w,"selected",games[0]);Call(w,"RenderCandidates");var list=(StackPanel)w.FindName("CandidateList");var rows=(StackPanel)w.FindName("CandidateReviewList");Check(rows.Children.Count==60,"First page unbounded");var pager=list.Children.OfType<WrapPanel>().Single();pager.Children.OfType<Button>().Single(b=>b.Content as string=="다음").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(rows.Children.Count==60,"Second page invalid");var search=(TextBox)w.FindName("Search");search.Text="테스트 게임 144";Check(rows.Children.Count==1,"Query did not reset page");}finally{Close(w);}});
        Test("player-page-and-wpf-layout",dir=>{var w=new MainWindow();try{var games=Enumerable.Range(0,145).Select(i=>Game(dir,i,true)).ToList();Set(w,"packages",games);Set(w,"selected",games[0]);Set(w,"destination",Path.Combine(dir,"library"));Set(w,"playerRoot",Path.Combine(dir,"library"));Set(w,"playerDocument",new PlayerDocument{Games=games.Select(p=>new GamePlay{Id=p.Id}).ToList()});Call(w,"SetPage","play");Call(w,"RenderPlayer");Check(Get<WrapPanel>(w,"playerCards").Children.Count==60,"Player rendered all games");w.Width=1100;w.Height=750;var body=(FrameworkElement)w.Content;body.Measure(new Size(1100,750));body.Arrange(new Rect(0,0,1100,750));body.UpdateLayout();var bitmap=new RenderTargetBitmap(1100,750,96,96,PixelFormats.Pbgra32);bitmap.Render((Visual)w.Content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(Path.Combine(dir,"fixture-layout.png"));encoder.Save(output);}finally{Close(w);}});
        Test("large-search-completes-and-ui-responds",dir=>{var input=Path.Combine(dir,"input");Directory.CreateDirectory(input);for(int i=0;i<510;i++){var d=Path.Combine(input,"Fake"+i);Directory.CreateDirectory(d);File.WriteAllText(Path.Combine(d,"Game.exe"),"harmless fixture");}var w=new MainWindow();try{var old=Game(input,999);Set(w,"packages",new List<Package>{old});Set(w,"selected",old);Set(w,"source",input);Set(w,"destination",Path.Combine(dir,"library"));Set(w,"backupStorageRoot",Path.Combine(dir,"backups"));Set(w,"searchLocations",new List<SearchLocation>{new(){Path=input}});int ticks=0;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(5)};timer.Tick+=(_,_)=>ticks++;timer.Start();Wait((Task)Call(w,"RefreshWorkspaceAsync",input,false,false)!);timer.Stop();Check(Get<List<Package>>(w,"packages").Count==511&&Get<List<Package>>(w,"packages").Any(p=>p.Id==old.Id),"Large search omitted old or new games");Check(ticks>0&&!Get<bool>(w,"scanning")&&((Button)w.FindName("SourceButton")).IsEnabled,"UI did not recover");}finally{Close(w);}});
        File.WriteAllText(args[1],JsonSerializer.Serialize(new{total=results.Count,passed=results.Count-failed,failed,results},new JsonSerializerOptions{WriteIndented=true}));return failed==0?0:1;
    }
    sealed class Counter(Action<int> action):IProgress<int>{public void Report(int n)=>action(n);}
}

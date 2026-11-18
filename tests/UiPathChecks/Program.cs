using IndieRun;
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
    static void Check(bool ok,string text){if(!ok)throw new Exception(text);}
    static void Set(object o,string field,object? value)=>o.GetType().GetField(field,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(o,value);
    static T Get<T>(object o,string field)=>(T)o.GetType().GetField(field,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(o)!;
    static object? Call(object o,string method,params object?[] args)=>o.GetType().GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(o,args);
    static Package Fixture(string dir,bool selective=false)
    {
        var folder=Path.Combine(dir,"input","GeneratedGame");Directory.CreateDirectory(Path.Combine(folder,"Data"));Directory.CreateDirectory(Path.Combine(folder,"Extras"));
        File.WriteAllText(Path.Combine(folder,"Game.exe"),"harmless generated fixture; never execute");File.WriteAllBytes(Path.Combine(folder,"Data","large.bin"),Enumerable.Range(0,1536*1024).Select(i=>(byte)(i%251)).ToArray());File.WriteAllText(Path.Combine(folder,"Extras","guide.txt"),"kept or moved according to selection");
        var p=new Package{Id=Guid.NewGuid().ToString("N"),IsReal=true,Registered=false,Source=folder,DetectedRoot=folder,SearchRoot=Path.Combine(dir,"input"),Name="GeneratedGame",Folder="GeneratedGame",Alias="가상 게임"};if(selective)p.OmittedFolders.Add("Extras");return p;
    }
    static MainWindow Window(string dir,out Package p)
    {
        p=Fixture(dir);var scan=GameScanner.Scan(p.SearchRoot);scan.PrepareIndex();var plan=new WorkspaceDocument{SearchRoot=p.SearchRoot,SearchLocations=[new(){Path=p.SearchRoot}],Destination=Path.Combine(dir,"library"),BackupRoot=Path.Combine(dir,"backups"),Packages=[p],Checkpoints=[WorkspaceReconciler.Capture(p,scan)]};var store=new WorkspaceStore(Path.Combine(dir,"data","workspace.json"));store.Save(plan);
        var w=new MainWindow();new System.Windows.Interop.WindowInteropHelper(w).EnsureHandle();Set(w,"workspaceStore",store);Set(w,"workspaceLock",store.AcquireLock());Call(w,"ApplyWorkspace",plan);Set(w,"snapshot",scan);Set(w,"sessionReady",true);Set(w,"playerRoot",plan.Destination);Set(w,"playerDocument",new PlayerDocument());Set(w,"directEngineFactory",(Func<BackupEngine>)(()=>new(Path.Combine(dir,"records"))));return w;
    }
    static void Close(MainWindow w){Set(w,"closingWithoutSave",true);w.Close();}
    static IEnumerable<DependencyObject> Logical(DependencyObject root){yield return root;foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var nested in Logical(child))yield return nested;}
    static string Warning(string path)=>(string)typeof(MainWindow).GetMethod("ContainerPathWarning",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{path})!;
    static void Layout(MainWindow w,double width){w.Width=width;w.Height=800;var body=(FrameworkElement)w.Content;body.Measure(new Size(width,800));body.Arrange(new Rect(0,0,width,800));body.UpdateLayout();}
    static void Snapshot(MainWindow w,string filename){var body=(FrameworkElement)w.Content;var bitmap=new RenderTargetBitmap((int)body.ActualWidth,(int)body.ActualHeight,96,96,PixelFormats.Pbgra32);var bg=new DrawingVisual();using(var draw=bg.RenderOpen())draw.DrawRectangle(w.Background,null,new Rect(0,0,body.ActualWidth,body.ActualHeight));bitmap.Render(bg);bitmap.Render(body);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(filename);png.Save(file);}
    [STAThread]static int Main(string[] args)
    {
        var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);var results=new List<object>();int failed=0;
        void Test(string name,Action<string> action){try{var dir=Path.Combine(root,name);Directory.CreateDirectory(dir);action(dir);results.Add(new{name,passed=true});Console.WriteLine("PASS "+name);}catch(Exception ex){failed++;results.Add(new{name,passed=false,error=ex.ToString()});Console.WriteLine("FAIL "+name+" "+ex.Message);}}
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};var styles=XDocument.Load(Path.Combine(AppContext.BaseDirectory,"styles-source.xaml")).Root!.Elements().Single().Elements().Single();app.Resources=(ResourceDictionary)XamlReader.Parse(styles.ToString(),new ParserContext{BaseUri=new Uri(AppContext.BaseDirectory)});SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Test("ascii-container-is-quiet",dir=>Check(Warning(@"D:\Games\Library")=="","ASCII path warned"));
        Test("full-parent-path-is-checked",dir=>Check(Warning(@"D:\사용자\Games\Library").Length>0,"Unicode ancestor was ignored"));
        Test("unc-parent-path-is-checked",dir=>Check(Warning(@"\\server\共有\Library").Length>0,"Unicode UNC share was ignored"));
        foreach(var width in new[]{1090d,1350d})foreach(var list in new[]{false,true})
        {
            Test("empty-spacing-"+width+"-"+list,dir=>{var w=Window(dir,out _);try{Set(w,"packages",new List<Package>());Set(w,"searchLocations",new List<SearchLocation>());Get<PlayerView>(w,"playerView").Layout=list?"list":"cards";Call(w,"SetPage","play");Call(w,"RenderPlayer");Layout(w,width);var panel=Get<WrapPanel>(w,"playerCards");var empty=(StackPanel)((Border)panel.Children[0]).Child;var label=(TextBlock)empty.Children[0];var button=(Button)empty.Children[1];var gap=button.TranslatePoint(new Point(),empty).Y-label.TranslatePoint(new Point(0,label.ActualHeight),empty).Y;Check(gap>=13,"Empty action touches guidance: "+gap+" height="+label.ActualHeight+" button="+button.ActualHeight);Check(button.HorizontalAlignment==HorizontalAlignment.Left,"Empty action stretched");Check(button.Focusable&&button.IsTabStop,"Empty action cannot receive keyboard focus");Snapshot(w,Path.Combine(dir,"empty.png"));}finally{Close(w);}});
            Test("selection-action-spacing-"+width+"-"+list,dir=>{var w=Window(dir,out var p);try{p.Registered=true;Set(w,"multiSelect",true);Get<HashSet<string>>(w,"playerSelection").Add(p.Id);Get<PlayerView>(w,"playerView").Layout=list?"list":"cards";Call(w,"SetPage","play");Call(w,"RenderPlayer");Layout(w,width);var bar=Get<StackPanel>(w,"selectionBar");var edit=Logical(bar).OfType<Button>().Single(b=>b.Content as string=="태그 편집");var manage=Logical(bar).OfType<Button>().Single(b=>b.Content as string=="관리");var point=manage.TranslatePoint(new Point(),edit);Check(point.X-edit.ActualWidth>=7,"Tag edit and manage touch: "+point+" edit="+edit.ActualWidth);Check(Math.Abs(point.Y)<1,"Action alignment differs");Check(edit.IsEnabled&&manage.IsEnabled&&edit.Focusable&&manage.Focusable,"Actions disabled or not focusable");Snapshot(w,Path.Combine(dir,"selection.png"));}finally{Close(w);}});
        }
        Test("existing-library-risk-visible",dir=>{var w=Window(dir,out _);try{Set(w,"destination",Path.Combine(dir,"한글상위","Library"));Call(w,"RenderLibrary");Check(Logical((DependencyObject)w.FindName("LibraryContent")).OfType<TextBlock>().Any(t=>t.Text==Warning(Get<string>(w,"destination"))),"Existing library warning absent");}finally{Close(w);}});
        foreach(var unicodeParent in new[]{false,true})Test("move-container-excludes-game-name-"+unicodeParent,dir=>{var w=Window(dir,out var p);Window? dialog=null;try{p.Name="日本語ゲーム";var target=Path.Combine(dir,unicodeParent?"한글상위":"AsciiParent","Library");dialog=(Window)Call(w,"BuildMovePlan",new List<Package>{p},target)!;var body=(FrameworkElement)dialog.Content;body.Measure(new Size(840,720));body.Arrange(new Rect(0,0,840,720));body.UpdateLayout();var warnings=Logical(body).OfType<TextBlock>().Where(t=>t.Text.Contains("경로에 한글")).ToList();Check(warnings.Count==(unicodeParent?1:0),"Game folder name triggered parent warning or parent risk missing");Check(Logical(body).OfType<Button>().Single(b=>b.Content as string=="보관함으로 옮기기").IsEnabled,"Warning blocked move instead of advising");}finally{dialog?.Close();Close(w);}});
        File.WriteAllText(args[1],JsonSerializer.Serialize(new{total=results.Count,passed=results.Count-failed,failed,results},new JsonSerializerOptions{WriteIndented=true}));return failed==0?0:1;
    }
}

using IndieRun;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

class Program
{
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static T Copy<T>(T value)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    static void Refuse(Action action){try{action();}catch(IOException){return;}throw new Exception("Expected refusal");}
    static void Set(object o,string name,object? value)=>o.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(o,value);
    static T Get<T>(object o,string name)=>(T)o.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(o)!;
    static object? Call(object o,string name,params object?[] args)=>o.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly)!.Invoke(o,args);
    static IEnumerable<DependencyObject> Nodes(DependencyObject item){yield return item;foreach(var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>())foreach(var node in Nodes(child))yield return node;}
    static (Package Package,WorkspaceDocument Plan,PlayerDocument Play) Fixture(string root,bool registered=false)
    {
        var path=Path.Combine(root,"input","Generated");Directory.CreateDirectory(Path.Combine(path,"Data"));File.WriteAllText(Path.Combine(path,"Game.exe"),"generated fixture; never execute");File.WriteAllText(Path.Combine(path,"Data","asset.dat"),"generated data");
        var p=new Package{Id=Guid.NewGuid().ToString("N"),Source=path,DetectedRoot=path,SearchRoot=Path.GetDirectoryName(path)!,Folder="Generated",Name="Generated",Alias="가상 게임",IsReal=true,Registered=registered};
        if(registered)LibraryStore.EnsureIdentity(path,p.Id);
        var scan=GameScanner.Scan(p.SearchRoot);var plan=new WorkspaceDocument{SearchRoot=p.SearchRoot,SearchLocations=[new(){Path=p.SearchRoot}],Destination=Path.Combine(root,"library"),BackupRoot=Path.Combine(root,"backups"),SelectedId=p.Id,Packages=[p],Checkpoints=[WorkspaceReconciler.Capture(p,scan)]};
        return (p,plan,new PlayerDocument{Games=[new(){Id=p.Id,Executable="Game.exe"}]});
    }
    static RenameTarget Target(Package p)=>new(){PackageId=p.Id,Original=p.Source,Current=p.Source,Name="Renamed"};
    static MainWindow Window(string root,out Package package)
    {
        var f=Fixture(root);package=f.Package;var store=new WorkspaceStore(Path.Combine(root,"data","workspace.json"));store.Save(f.Plan);var w=new MainWindow();new System.Windows.Interop.WindowInteropHelper(w).EnsureHandle();Set(w,"workspaceStore",store);Set(w,"workspaceLock",store.AcquireLock());Call(w,"ApplyWorkspace",f.Plan);Set(w,"snapshot",GameScanner.Scan(f.Plan.SearchRoot));Set(w,"sessionReady",true);Set(w,"playerRoot",f.Plan.Destination);Set(w,"playerDocument",f.Play);Set(w,"catalog",new TagCatalog());return w;
    }
    static void Close(MainWindow w){Set(w,"closingWithoutSave",true);w.Close();}
    static void Render(Window w,string path,int width=1180,int height=780,bool checkBounds=true)
    {
        var body=(FrameworkElement)w.Content;body.Measure(new Size(width,height));body.Arrange(new Rect(0,0,width,height));body.UpdateLayout();
        foreach(var b in Nodes(body).OfType<Button>().Where(b=>checkBounds&&b.ActualWidth>0))
        {var at=b.TranslatePoint(new Point(),body);Check(at.X>=-1&&at.X+b.ActualWidth<=width+1,"Button clipped: "+b.Content);}
        var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);var background=new DrawingVisual();using(var drawing=background.RenderOpen())drawing.DrawRectangle(w.Background??(Brush)Application.Current.FindResource("Surface.Primary"),null,new Rect(0,0,width,height));image.Render(background);image.Render(body);var pixel=new byte[4];image.CopyPixels(new Int32Rect(0,0,1,1),pixel,4,0);Check(pixel[3]==255,"Screenshot background is transparent");var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var output=File.Create(path);png.Save(output);
    }
    [STAThread]static int Main(string[] args)
    {
        var root=Path.Combine(args[0],Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var results=new List<object>();int failed=0;
        void Test(string name,Action<string> action){var dir=Path.Combine(root,name);Directory.CreateDirectory(dir);try{action(dir);results.Add(new{name,passed=true});Console.WriteLine("PASS "+name);}catch(Exception ex){failed++;results.Add(new{name,passed=false,error=ex.ToString()});Console.WriteLine("FAIL "+name+": "+ex.Message);}}
        Test("changed-executable-outside-owned-scope-refused",dir=>{var f=Fixture(dir);Directory.CreateDirectory(Path.Combine(f.Package.Source,"Other"));File.WriteAllText(Path.Combine(f.Package.Source,"Other","Other.exe"),"fixture");f.Package.OmittedFolders.Add("Other");var draft=Copy(f.Play.Games[0]);draft.Executable="Other/Other.exe";Refuse(()=>PlayerStore.ValidateLaunchEdit(f.Package,f.Play.Games[0],draft));Check(f.Play.Games[0].Executable=="Game.exe","Original settings mutated");});
        Test("changed-missing-working-folder-refused-before-save",dir=>{var f=Fixture(dir);var draft=Copy(f.Play.Games[0]);draft.WorkingDirectory="Missing";Refuse(()=>PlayerStore.ValidateLaunchEdit(f.Package,f.Play.Games[0],draft));});
        Test("changed-missing-executable-refused-before-save",dir=>{var f=Fixture(dir);var draft=Copy(f.Play.Games[0]);draft.Executable="Missing.exe";Refuse(()=>PlayerStore.ValidateLaunchEdit(f.Package,f.Play.Games[0],draft));});
        Test("valid-launch-setting-edit-preserves-arguments",dir=>{var f=Fixture(dir);var draft=Copy(f.Play.Games[0]);draft.WorkingDirectory="Data";draft.Arguments="--flag \"two words\"";PlayerStore.ValidateLaunchEdit(f.Package,f.Play.Games[0],draft);Check(draft.Arguments.Contains("two words"),"Arguments changed");});
        Test("metadata-only-edit-does-not-demand-missing-game",dir=>{var f=Fixture(dir);var original=f.Play.Games[0];File.Delete(Path.Combine(f.Package.Source,"Game.exe"));var draft=Copy(original);draft.Favorite=true;PlayerStore.ValidateLaunchEdit(f.Package,original,draft);Check(draft.Favorite,"Favorite lost");});
        Test("metadata-edit-with-unset-executable-is-allowed",dir=>{var f=Fixture(dir);var original=new GamePlay{Id=f.Package.Id};PlayerStore.ValidateLaunchEdit(f.Package,original,Copy(original));Refuse(()=>PlayerStore.ValidateLaunchEdit(f.Package,original,Copy(original),true));});
        Test("renaming-wrong-game-id-is-blocked-before-backup",dir=>{var f=Fixture(dir,true);File.Delete(Path.Combine(f.Package.Source,LibraryStore.MarkerName));LibraryStore.EnsureIdentity(f.Package.Source,Guid.NewGuid().ToString("N"));var service=new RenameBatch(Path.Combine(dir,"data"));Refuse(()=>service.Begin(f.Plan,new(),f.Play,[Target(f.Package)]));Check(!service.HasPending&&!Directory.Exists(f.Plan.BackupRoot)&&Directory.Exists(f.Package.Source),"Rename or backup began on wrong game");});
        Test("renaming-missing-marker-is-blocked-before-backup",dir=>{var f=Fixture(dir,true);File.Delete(Path.Combine(f.Package.Source,LibraryStore.MarkerName));var service=new RenameBatch(Path.Combine(dir,"data"));Refuse(()=>service.Begin(f.Plan,new(),f.Play,[Target(f.Package)]));Check(!service.HasPending&&Directory.Exists(f.Package.Source),"Missing identity was accepted");});
        Test("registered-child-identity-is-checked-with-parent",dir=>{var f=Fixture(dir,true);var child=Copy(f.Package);child.Id=Guid.NewGuid().ToString("N");child.Source=child.DetectedRoot=Path.Combine(f.Package.Source,"Child");child.Name=child.Folder="Child";Directory.CreateDirectory(child.Source);LibraryStore.EnsureIdentity(child.Source,Guid.NewGuid().ToString("N"));f.Plan.Packages.Add(child);var service=new RenameBatch(Path.Combine(dir,"data"));Refuse(()=>service.Begin(f.Plan,new(),f.Play,[Target(f.Package)]));Check(!service.HasPending,"Parent bypassed child identity check");});
        Test("local-registration-without-marker-can-rename",dir=>{var f=Fixture(dir);f.Package.Registered=true;f.Package.LocalRegistration=true;var service=new FolderRename(Path.Combine(dir,"data"));var plan=service.Prepare(f.Plan,new(),f.Play,f.Package.Source,"Renamed");Check(plan.MarkerId.Length==0&&Directory.Exists(f.Package.Source),"Local-only registration wrongly blocked or moved");});
        Test("absent-child-does-not-block-existing-parent-rename",dir=>{var f=Fixture(dir,true);var child=Copy(f.Package);child.Id=Guid.NewGuid().ToString("N");child.Source=child.DetectedRoot=Path.Combine(f.Package.Source,"Absent");f.Plan.Packages.Add(child);FolderRename.VerifyIdentities(f.Plan,f.Package.Source);Check(!Directory.Exists(child.Source),"Absent child was created");});
        Test("identity-changed-after-batch-start-is-checked-at-step",dir=>{var f=Fixture(dir,true);var batch=new RenameBatch(Path.Combine(dir,"data"));batch.Begin(f.Plan,new(),f.Play,[Target(f.Package)]);File.Delete(Path.Combine(f.Package.Source,LibraryStore.MarkerName));LibraryStore.EnsureIdentity(f.Package.Source,Guid.NewGuid().ToString("N"));Refuse(()=>new FolderRename(Path.Combine(dir,"data")).Prepare(f.Plan,new(),f.Play,f.Package.Source,"Renamed"));Check(Directory.Exists(f.Package.Source),"Changed identity was renamed");});
        Test("tag-usage-keeps-offline-id-bindings-and-deduplicates",dir=>{var tag=new TagDefinition{Name="가상 태그",Kind="category"};var sameName=new TagDefinition{Name=tag.Name,Kind="genre"};var p=new Package{Id=Guid.NewGuid().ToString("N"),Alias="현재 별칭",Categories=[tag.Name],CategoryIds=[tag.Id],Excluded=true};var offline=Guid.NewGuid().ToString("N");var doc=new LibraryDocument{Games=[new(){Id=p.Id,Alias="이전 별칭",Categories=[tag.Name],CategoryIds=[tag.Id]},new(){Id=offline,Folder="Offline",CategoryIds=[tag.Id]}]};var usage=PlayerRules.TagUsage([p],doc,[tag,sameName]);Check(usage[tag.Id].Count==2&&usage[tag.Id].Single(x=>x.Id==p.Id).Title=="현재 별칭"&&usage[sameName.Id].Count==0,"Tag usage missing, duplicate, or wrong kind");});
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};var styles=XDocument.Load(Path.Combine(AppContext.BaseDirectory,"styles-source.xaml")).Root!.Elements().Single().Elements().Single();app.Resources=(ResourceDictionary)XamlReader.Parse(styles.ToString(),new ParserContext{BaseUri=new Uri(AppContext.BaseDirectory)});
        Test("unregistered-candidates-show-first-registration-action",dir=>{var w=Window(dir,out var p);try{Call(w,"SetPage","play");Call(w,"RenderPlayer");var panel=Get<DockPanel>(w,"PlayerContent");Check(Nodes(panel).OfType<TextBlock>().Any(t=>t.Text=="아직 실행목록에 등록한 게임이 없습니다."),"Unregistered candidates looked like search failure");var cards=Get<WrapPanel>(w,"playerCards");var button=Nodes(cards).OfType<Button>().Single(b=>b.Content as string=="게임 가져오기");Render(w,Path.Combine(root,"empty-registration.png"));Check(button.Margin.Top>=12&&button.ActualWidth>0,"Empty action spacing invalid");}finally{Close(w);}});
        Test("filtered-registered-library-keeps-search-empty-state",dir=>{var w=Window(dir,out var p);try{p.Registered=true;LibraryStore.EnsureIdentity(p.Source,p.Id);Get<PlayerView>(w,"playerView").Query="no-matching-generated-title";Call(w,"SetPage","play");Call(w,"RenderPlayer");Check(Nodes(Get<WrapPanel>(w,"playerCards")).OfType<TextBlock>().Any(t=>t.Text=="조건에 맞는 게임이 없습니다."),"Filter result replaced with first-registration cue");}finally{Close(w);}});
        Test("tag-manager-renders-snapshot-and-search-without-reread",dir=>{var w=Window(dir,out var p);try{var tags=Enumerable.Range(1,30).Select(i=>new TagDefinition{Name="가상 장르 "+i,Kind="genre"}).ToList();p.Genres=tags.Select(t=>t.Name).ToList();Set(w,"catalog",new TagCatalog{Tags=tags});var doc=new LibraryDocument{Games=[new(){Id=p.Id,Folder=p.Folder,LastKnownPath=p.Source,Genres=p.Genres,Scope=StoredScope.Capture(p)}]};var library=new LibraryStore(Get<string>(w,"destination"));library.Update(d=>d.Games=doc.Games);Call(w,"SetPage","tags");Call(w,"RenderModernTags");using var locked=new FileStream(Path.Combine(library.Root,LibraryStore.JsonName),FileMode.Open,FileAccess.ReadWrite,FileShare.None);var search=Nodes(Get<StackPanel>(w,"TagsContent")).OfType<TextBox>().Single();search.Text="가상 장르 2";Check(!Nodes(Get<StackPanel>(w,"TagsContent")).OfType<TextBlock>().Any(t=>t.Text.Contains("읽지 못했습니다")),"Tag search re-read locked library instead of snapshot");Render(w,Path.Combine(root,"tag-manager-search.png"));}finally{Close(w);}});
        Test("tag-usage-read-failure-has-retry-and-collapsed-detail",dir=>{var w=Window(dir,out var p);try{Set(w,"catalog",new TagCatalog{Tags=[new(){Name="가상 장르",Kind="genre"}]});var library=Get<string>(w,"destination");Directory.CreateDirectory(library);File.WriteAllText(Path.Combine(library,LibraryStore.JsonName),"{");Call(w,"SetPage","tags");Call(w,"RenderModernTags");var panel=Get<StackPanel>(w,"TagsContent");Check(Nodes(panel).OfType<Button>().Any(b=>b.Content as string=="다시 확인")&&Nodes(panel).OfType<Expander>().All(e=>!e.IsExpanded),"Tag error has no retry or exposes long detail");Render(w,Path.Combine(root,"tag-usage-error.png"));}finally{Close(w);}});
        Test("long-game-details-keep-close-and-edit-actions-visible",dir=>{var w=Window(dir,out var p);try{p.Genres=Enumerable.Range(1,80).Select(i=>"긴 가상 장르 "+i).ToList();p.Categories=Enumerable.Range(1,80).Select(i=>"긴 가상 카테고리 "+i).ToList();var detail=(Window)Call(w,"BuildGameDetails",p)!;try{Check(detail.Background is SolidColorBrush brush&&brush.Color.A==255,"Modal surface is not opaque");Render(detail,Path.Combine(root,"long-game-details.png"),740,520);var body=(FrameworkElement)detail.Content;foreach(var button in Nodes(body).OfType<Button>().Where(b=>b.Content as string is "편집" or "닫기")){var at=button.TranslatePoint(new Point(),body);Check(at.Y>=0&&at.Y+button.ActualHeight<=520,"Detail action clipped by long tags");}Check(Nodes(body).OfType<ScrollViewer>().Any(s=>s.ExtentHeight>s.ViewportHeight),"Long details do not scroll");}finally{detail.Close();}}finally{Close(w);}});
        var compactLayouts=new List<object>();
        foreach(var size in new[]{(Width:1100,Height:680),(Width:1024,Height:640)})foreach(var page in new[]{"organize","play","tags"})
        {
            var dir=Path.Combine(root,"compact-"+page+"-"+size.Width);Directory.CreateDirectory(dir);var w=Window(dir,out var p);
            try
            {
                Call(w,"SetPage",page);if(page=="play")Call(w,"RenderPlayer");else if(page=="tags")Call(w,"RenderModernTags");else Call(w,"Render");
                var output=Path.Combine(root,$"compact-{page}-{size.Width}.png");Render(w,output,size.Width,size.Height,false);var body=(FrameworkElement)w.Content;
                bool Available(DependencyObject element,bool ignoreScroll=false){for(DependencyObject? at=element;at!=null&&at is not System.Windows.Window;at=VisualTreeHelper.GetParent(at)??LogicalTreeHelper.GetParent(at)){if(at is UIElement ui&&ui.Visibility!=Visibility.Visible)return false;if(!ignoreScroll&&at is ScrollViewer)return false;}return true;}
                var buttons=Nodes(body).OfType<Button>().Where(b=>b.ActualWidth>0&&Available(b)).ToList();
                if(page=="tags"&&Get<StackPanel>(w,"TagsContent").Children.Count>0)buttons.AddRange(Nodes(Get<StackPanel>(w,"TagsContent").Children[0]).OfType<Button>().Where(b=>b.ActualWidth>0&&Available(b,true)));
                var overflow=buttons.Distinct().Select(b=>new{label=b.Content as string??b.Name,at=b.TranslatePoint(new Point(),body),width=b.ActualWidth,height=b.ActualHeight}).Where(b=>b.at.X< -1||b.at.Y< -1||b.at.X+b.width>size.Width+1||b.at.Y+b.height>size.Height+1).Select(b=>new{b.label,x=b.at.X,y=b.at.Y,b.width,b.height}).ToList();
                compactLayouts.Add(new{page,width=size.Width,height=size.Height,overflowButtons=overflow,output});
            }
            catch(Exception ex){compactLayouts.Add(new{page,width=size.Width,height=size.Height,error=ex.ToString()});}
            finally{Close(w);}
        }
        File.WriteAllText(Path.Combine(root,"compact-layouts.json"),JsonSerializer.Serialize(compactLayouts,new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(args[1],JsonSerializer.Serialize(new{root,total=results.Count,passed=results.Count-failed,failed,results},new JsonSerializerOptions{WriteIndented=true}));return failed==0?0:1;
    }
}

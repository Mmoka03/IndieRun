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
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    static Package Game(string root,string name="Game",bool marked=true)
    {
        var folder=Path.Combine(root,name);Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"Game.exe"),"generated fixture; never execute");
        var p=new Package{Id=Guid.NewGuid().ToString("N"),Source=folder,SearchRoot=root,DetectedRoot=folder,Folder=name,Name=name,IsReal=true,Registered=marked};
        if(marked)LibraryStore.EnsureIdentity(folder,p.Id);return p;
    }
    static ScanSnapshot Scan(string root){var scan=GameScanner.Scan(root);scan.SearchRoots.Add(root);scan.PrepareIndex();return scan;}
    static LibraryDocument Library(params Package[] games)=>new(){Games=games.Select(p=>new LibraryGame{Id=p.Id,Folder=p.Folder,LastKnownPath=p.Source,Executables=["Game.exe"],Scope=StoredScope.Capture(p)}).ToList()};
    static void CopyMarker(Package p,string folder){Directory.CreateDirectory(folder);File.Copy(Path.Combine(p.Source,LibraryStore.MarkerName),Path.Combine(folder,LibraryStore.MarkerName));}
    static void Set(object o,string field,object? value)=>o.GetType().GetField(field,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(o,value);
    static T Get<T>(object o,string field)=>(T)o.GetType().GetField(field,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(o)!;
    static object? Call(object o,string method,params object?[] args)=>o.GetType().GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly)!.Invoke(o,args);
    static IEnumerable<DependencyObject> Logical(DependencyObject root){yield return root;foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var item in Logical(child))yield return item;}
    static MainWindow Window(string root,out Package p)
    {
        p=Game(Path.Combine(root,"input"),marked:false);p.Registered=true;
        var scan=Scan(p.SearchRoot);var plan=new WorkspaceDocument{SearchRoot=p.SearchRoot,SearchLocations=[new(){Path=p.SearchRoot}],Destination=Path.Combine(root,"library"),BackupRoot=Path.Combine(root,"backups"),Packages=[p],Checkpoints=[WorkspaceReconciler.Capture(p,scan)]};
        var store=new WorkspaceStore(Path.Combine(root,"data","workspace.json"));store.Save(plan);var w=new MainWindow();new System.Windows.Interop.WindowInteropHelper(w).EnsureHandle();Set(w,"workspaceStore",store);Set(w,"workspaceLock",store.AcquireLock());Call(w,"ApplyWorkspace",plan);Set(w,"snapshot",scan);Set(w,"sessionReady",true);Set(w,"playerRoot",plan.Destination);Set(w,"playerDocument",new PlayerDocument{Games=[new(){Id=p.Id,Executable="Game.exe"}]});return w;
    }
    static void Render(Window window,string path)
    {
        const int width=620,height=460;var body=(FrameworkElement)window.Content;body.Measure(new Size(width,height));body.Arrange(new Rect(0,0,width,height));body.UpdateLayout();
        foreach(var button in Logical(body).OfType<Button>().Where(b=>b.ActualWidth>0)){var point=button.TranslatePoint(new Point(),body);Check(point.X>=-1&&point.X+button.ActualWidth<=width+1,"Button clipped: "+button.Content);}
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);var background=new DrawingVisual();using(var draw=background.RenderOpen())draw.DrawRectangle(window.Background,null,new Rect(0,0,width,height));bitmap.Render(background);bitmap.Render(body);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(path);png.Save(output);
    }
    [STAThread] static int Main(string[] args)
    {
        var root=Path.Combine(Path.GetFullPath(args[0]),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var results=new List<object>();int failed=0;
        void Test(string name,Action<string> run){var dir=Path.Combine(root,name);Directory.CreateDirectory(dir);try{run(dir);results.Add(new{name,passed=true});Console.WriteLine("PASS "+name);}catch(Exception ex){failed++;results.Add(new{name,passed=false,error=ex.ToString()});Console.WriteLine("FAIL "+name+": "+ex.Message);}}
        Test("broken-neighbor-marker-does-not-abort-local-rename-search",dir=>
        {
            var p=Game(Path.Combine(dir,"old"),"data");var old=Path.GetDirectoryName(p.Source)!;var broken=Path.Combine(dir,"00-broken","data");Directory.CreateDirectory(broken);File.WriteAllText(Path.Combine(broken,LibraryStore.MarkerName),"{broken");var next=Path.Combine(dir,"renamed");Directory.Move(old,next);
            var found=LocalIdentityProbe.Find([old],[p],[]);Check(found.GetValueOrDefault(old)==next,"Valid renamed root was not found past broken marker");Check(File.ReadAllText(Path.Combine(broken,LibraryStore.MarkerName))=="{broken","Broken marker was changed");
        });
        Test("corrupt-original-is-isolated-from-other-game-refresh",dir=>
        {
            var original=Game(dir,"Original");var copy=Path.Combine(dir,"scanned","Copy");CopyMarker(original,copy);File.WriteAllText(Path.Combine(copy,"Game.exe"),"generated");File.WriteAllText(Path.Combine(original.Source,LibraryStore.MarkerName),"{broken");var healthy=Game(Path.Combine(dir,"scanned"),"Healthy");var scan=Scan(Path.Combine(dir,"scanned"));var inventory=LibraryStore.Inspect(scan);var doc=Library(original,healthy);var checks=new List<FolderCheckpoint>();
            IdentityReconciler.Rebind([original,healthy],checks,doc,inventory,scan,Path.Combine(dir,"library"));new LibraryStore(Path.Combine(dir,"library")).Reconcile(doc,inventory,scan);Check(original.Source==Path.Combine(dir,"Original")&&inventory.Problems.Any(p=>p.Path==original.Source),"Original uncertainty was hidden");Check(doc.Games.Single(g=>g.Id==healthy.Id).Status=="External","One bad ID stopped healthy game refresh");Check(doc.Games.Single(g=>g.Id==original.Id).Status=="Unavailable","Unreadable ID was treated as missing or valid");
        });
        Test("durable-registration-restores-stale-unregistered-workspace",dir=>
        {
            var p=Game(dir);p.Registered=false;var scan=Scan(dir);var doc=Library(p);IdentityReconciler.AlignLocations([p],doc,LibraryStore.Inspect(scan));Check(p.Registered==true&&!p.LocalRegistration,"Persisted registration stayed a candidate");
        });
        Test("foreign-id-does-not-replace-local-registration",dir=>
        {
            var p=Game(dir,marked:false);p.Registered=true;p.LocalRegistration=true;var oldId=p.Id;var foreign=Guid.NewGuid().ToString("N");LibraryStore.EnsureIdentity(p.Source,foreign);var scan=Scan(dir);var inventory=LibraryStore.Inspect(scan);IdentityReconciler.Rebind([p],[],new(),inventory,scan,Path.Combine(dir,"library"));IdentityReconciler.Flag(p,inventory,new());Check(p.Id==oldId&&p.ReviewState=="identity","Foreign ID stole local metadata");
        });
        Test("foreign-id-does-not-orphan-execution-settings",dir=>
        {
            var p=Game(dir,marked:false);var oldId=p.Id;LibraryStore.EnsureIdentity(p.Source,Guid.NewGuid().ToString("N"));var scan=Scan(dir);IdentityReconciler.Rebind([p],[],new(),LibraryStore.Inspect(scan),scan,Path.Combine(dir,"library"),new HashSet<string>{oldId});Check(p.Id==oldId,"Configured candidate changed identity");
        });
        Test("foreign-id-does-not-break-related-content-link",dir=>
        {
            var owner=Game(dir,"Owner",false);var asset=Game(dir,"Asset",false);asset.IsContent=true;asset.ContentOwnerId=owner.Id;owner.RelatedFolders.Add(new(){Id=asset.Id,Source=asset.Source,Name=asset.Folder});var oldId=asset.Id;LibraryStore.EnsureIdentity(asset.Source,Guid.NewGuid().ToString("N"));var scan=Scan(dir);IdentityReconciler.Rebind([owner,asset],[],new(),LibraryStore.Inspect(scan),scan,Path.Combine(dir,"library"));Check(asset.Id==oldId&&owner.RelatedFolders.Single().Id==oldId,"Related item identity was replaced");
        });
        Test("marker-probe-outside-search-keeps-executable-metadata",dir=>
        {
            var p=Game(dir);var doc=Library(p);var elsewhere=Path.Combine(dir,"Other");Directory.CreateDirectory(elsewhere);var scan=Scan(elsewhere);var inventory=LibraryStore.Inspect(scan);IdentityProbe.RefreshKnown(inventory,[p.Source],[elsewhere]);new LibraryStore(Path.Combine(dir,"library")).Reconcile(doc,inventory,scan);Check(doc.Games.Single().Executables.SequenceEqual(new[]{"Game.exe"})&&doc.Games.Single().Status=="NotChecked","Marker-only probe erased executable metadata or claimed full scan");
        });
        Test("unsearched-game-is-not-reported-missing",dir=>
        {
            var p=Game(dir);var doc=Library(p);var other=Path.Combine(dir,"Other");Directory.CreateDirectory(other);var scan=Scan(other);new LibraryStore(Path.Combine(dir,"library")).Reconcile(doc,LibraryStore.Inspect(scan),scan);Check(doc.Games.Single().Status=="NotChecked"&&doc.Games.Single().Executables.Count==1,"Unsearched location reported missing");
        });
        Test("disabled-and-offline-locations-keep-distinct-status",dir=>
        {
            var p=Game(dir);var doc=Library(p);var scan=new ScanSnapshot{Root=dir};scan.InactivePaths.Add(p.Source);scan.UnreadablePaths.Add(p.Source);var store=new LibraryStore(Path.Combine(dir,"library"));store.Reconcile(doc,new(),scan);Check(doc.Games.Single().Status=="Paused","Disabled location reported access error");scan.InactivePaths.Clear();scan.OfflineRoots.Add(p.Source);store.Reconcile(doc,new(),scan);Check(doc.Games.Single().Status=="Offline"&&doc.Games.Single().Executables.Count==1,"Offline location lost metadata");
        });
        Test("observed-missing-marker-and-folder-remain-distinguishable",dir=>
        {
            var p=Game(dir);var doc=Library(p);var store=new LibraryStore(Path.Combine(dir,"library"));File.Delete(Path.Combine(p.Source,LibraryStore.MarkerName));var scan=Scan(dir);store.Reconcile(doc,LibraryStore.Inspect(scan),scan);Check(doc.Games.Single().Status=="IdentityMissing","Present folder missing marker not detected");Directory.Move(p.Source,Path.Combine(dir,"Other"));scan=Scan(dir);store.Reconcile(doc,LibraryStore.Inspect(scan),scan);Check(doc.Games.Single().Status=="Missing","Observed removed folder not detected");
        });
        Test("partial-folder-read-keeps-last-known-executable-list",dir=>
        {
            var p=Game(dir);var doc=Library(p);var scan=Scan(dir);scan.Files.RemoveAll(f=>f.Path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase));scan.UnreadablePaths.Add(Path.Combine(p.Source,"Game.exe"));scan.InvalidateIndex();new LibraryStore(Path.Combine(dir,"library")).Reconcile(doc,LibraryStore.Inspect(scan),scan);Check(doc.Games.Single().Status=="Unavailable"&&doc.Games.Single().Executables.SequenceEqual(new[]{"Game.exe"}),"Failed enumeration erased executable metadata");
        });
        Test("excluded-folder-access-failure-does-not-block-main-game",dir=>
        {
            var p=Game(dir);var omitted=Path.Combine(p.Source,"Excluded");Directory.CreateDirectory(omitted);p.OmittedFolders.Add("Excluded");var scan=Scan(dir);var baseline=WorkspaceReconciler.Capture(p,scan);scan.UnreadablePaths.Add(omitted);WorkspaceReconciler.Check(p,baseline,scan);Check(p.ReviewState=="","Excluded folder blocked game");var doc=Library(p);new LibraryStore(Path.Combine(dir,"library")).Reconcile(doc,LibraryStore.Inspect(scan),scan);Check(doc.Games.Single().Status=="External","Excluded folder blocked library status");p.OmittedFolders.Clear();WorkspaceReconciler.Check(p,baseline,scan);Check(p.ReviewState=="unverified","Included unreadable folder was ignored");
        });
        Test("ancestor-access-failure-still-blocks-game",dir=>
        {
            var p=Game(dir);var scan=Scan(dir);var baseline=WorkspaceReconciler.Capture(p,scan);scan.UnreadablePaths.Add(dir);WorkspaceReconciler.Check(p,baseline,scan);Check(p.ReviewState=="unverified","Unreadable ancestor was ignored");
        });
        Test("junction-is-visible-as-unverified-scope",dir=>
        {
            var p=Game(dir);var target=Path.Combine(dir,"ExternalData");Directory.CreateDirectory(target);File.WriteAllText(Path.Combine(target,"data.bin"),"generated");var link=Path.Combine(p.Source,"LinkedData");var start=new ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in new[]{"/c","mklink","/J",link,target})start.ArgumentList.Add(arg);using(var process=Process.Start(start)!){var output=process.StandardOutput.ReadToEnd()+process.StandardError.ReadToEnd();process.WaitForExit();Check(process.ExitCode==0,"Fixture junction creation failed: "+output);}var scan=Scan(p.Source);Check(scan.UnreadablePaths.Contains(link)&&!scan.Files.Any(f=>f.Path.EndsWith("data.bin")),"Junction silently omitted or traversed");WorkspaceReconciler.Check(p,WorkspaceReconciler.Capture(p,scan),scan);Check(p.ReviewState=="unverified","Incomplete linked scope was accepted");
        });
        Test("duplicate-status-takes-priority-over-paused-search",dir=>
        {
            var p=Game(dir);var copy=Path.Combine(dir,"Copy");CopyMarker(p,copy);var scan=Scan(dir);scan.InactivePaths.Add(p.Source);var doc=Library(p);new LibraryStore(Path.Combine(dir,"library")).Reconcile(doc,LibraryStore.Inspect(scan),scan);Check(doc.Games.Single().Status=="Duplicate","Duplicate ID hidden by paused status");
        });
        Test("drive-root-executable-does-not-create-empty-whole-drive-candidate",dir=>
        {
            var drive=@"Q:\";var game=Path.Combine(drive,"GeneratedGame");var scan=new ScanSnapshot{Root=drive};scan.Directories.AddRange([drive,game]);scan.Files.Add(new(Path.Combine(drive,"Launcher.exe"),10,DateTime.UnixEpoch));scan.Files.Add(new(Path.Combine(game,"Game.exe"),10,DateTime.UnixEpoch));var found=WorkspaceReconciler.Discover(scan,[]);Check(found.Count==1&&found[0].Source==game&&found[0].Folder.Length>0,"Drive root became a blank candidate or swallowed child game");Check(scan.Issues.Any(issue=>issue.Contains("드라이브 바로 아래")),"Skipped root executable has no explanation");
        });
        Test("corrupt-related-marker-does-not-abort-owner-relocation",dir=>
        {
            var old=Path.Combine(dir,"OldBundle");var owner=Game(old,"Main");var asset=Game(old,"Extras");asset.IsContent=true;asset.ContentOwnerId=owner.Id;owner.RelatedFolders.Add(new(){Id=asset.Id,Source=asset.Source,Name=asset.Folder});var before=Scan(dir);var checks=new List<FolderCheckpoint>{WorkspaceReconciler.Capture(owner,before),WorkspaceReconciler.Capture(asset,before)};var doc=Library(owner,asset);var next=Path.Combine(dir,"NewBundle");Directory.Move(old,next);File.WriteAllText(Path.Combine(next,"Extras",LibraryStore.MarkerName),"{broken");var scan=Scan(dir);IdentityReconciler.Rebind([owner,asset],checks,doc,LibraryStore.Inspect(scan),scan,Path.Combine(dir,"library"));Check(owner.Source==Path.Combine(next,"Main")&&asset.Source==Path.Combine(old,"Extras"),"Owner not relocated or corrupted related marker accepted");
        });
        var invalidMarkers=new Dictionary<string,string>
        {
            ["format"]=JsonSerializer.Serialize(new GameIdentity{Format="Other",GameId=Guid.NewGuid().ToString("N")}),
            ["version"]=JsonSerializer.Serialize(new GameIdentity{Version=999,GameId=Guid.NewGuid().ToString("N")}),
            ["id"]=JsonSerializer.Serialize(new GameIdentity{GameId="not-an-id"}),
            ["oversize"]=new string('x',5000)
        };
        foreach(var payload in invalidMarkers)Test("invalid-marker-"+payload.Key+"-does-not-abort-independent-search",dir=>
        {
            var p=Game(Path.Combine(dir,"Old"),"data");var old=Path.GetDirectoryName(p.Source)!;var broken=Path.Combine(dir,"Broken","data");Directory.CreateDirectory(broken);File.WriteAllText(Path.Combine(broken,LibraryStore.MarkerName),payload.Value);var next=Path.Combine(dir,"Renamed");Directory.Move(old,next);
            Check(LocalIdentityProbe.Find([old],[p],[]).GetValueOrDefault(old)==next,"InvalidDataException aborted local rename probe");
            var scan=Scan(next);var inventory=LibraryStore.Inspect(scan);IdentityProbe.RefreshKnown(inventory,[broken],[next]);Check(inventory.Problems.Any(problem=>problem.Path==broken)&&inventory.Locations[p.Id].Count==1,"Known invalid marker was not isolated");
            var tracked=new Package{Id=p.Id,Registered=true,IsReal=true,Source=broken,SearchRoot=dir,DetectedRoot=broken,Name="data",Folder="data"};IdentityReconciler.Rebind([tracked],[],Library(tracked),inventory,scan,Path.Combine(dir,"library"));Check(tracked.Source==broken&&inventory.Problems.Any(problem=>problem.Path==broken),"Invalid original caused relocation or abort");
            var candidate=new Package{Id=Guid.NewGuid().ToString("N"),Registered=false,IsReal=true,Source=broken,SearchRoot=dir,DetectedRoot=broken,Name="data",Folder="data"};var all=Scan(dir);Check(!FolderPolicy.Redundant(candidate,Path.Combine(next,"data"),all),"Invalid marker candidate was absorbed");var parent=new Package{Id=Guid.NewGuid().ToString("N"),Registered=true,Source=dir};var candidates=new List<Package>{parent,candidate};FolderPolicy.AbsorbOwnedCandidates(candidates,[],parent);Check(candidates.Contains(candidate),"Uncertain candidate removed");candidate.Registered=true;Check(!RegisteredTree.ExistingChild(parent,candidate,dir,dir),"Invalid child marker accepted");
            Check(File.ReadAllText(Path.Combine(broken,LibraryStore.MarkerName))==payload.Value,"Invalid marker rewritten");
        });
        Test("valid-json-invalid-related-identity-does-not-abort-owner-relocation",dir=>
        {
            var old=Path.Combine(dir,"OldBundle");var owner=Game(old,"Main");var asset=Game(old,"Extras");asset.IsContent=true;asset.ContentOwnerId=owner.Id;owner.RelatedFolders.Add(new(){Id=asset.Id,Source=asset.Source,Name=asset.Folder});var before=Scan(dir);var checks=new List<FolderCheckpoint>{WorkspaceReconciler.Capture(owner,before),WorkspaceReconciler.Capture(asset,before)};var doc=Library(owner,asset);var next=Path.Combine(dir,"NewBundle");Directory.Move(old,next);File.WriteAllText(Path.Combine(next,"Extras",LibraryStore.MarkerName),invalidMarkers["version"]);var scan=Scan(dir);IdentityReconciler.Rebind([owner,asset],checks,doc,LibraryStore.Inspect(scan),scan,Path.Combine(dir,"library"));Check(owner.Source==Path.Combine(next,"Main")&&asset.Source==Path.Combine(old,"Extras"),"InvalidDataException aborted independent owner relocation");
        });
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};var styles=XDocument.Load(Path.Combine(AppContext.BaseDirectory,"styles-source.xaml")).Root!.Elements().Single().Elements().Single();app.Resources=(ResourceDictionary)XamlReader.Parse(styles.ToString(),new ParserContext{BaseUri=new Uri(AppContext.BaseDirectory)});SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Test("previous-launch-failure-can-be-cleared-without-global-rescan",dir=>
        {
            var w=Window(dir,out var p);try
            {
                var scan=Get<ScanSnapshot>(w,"snapshot");var failures=Get<Dictionary<string,LaunchState>>(w,"launchFailures");failures[p.Id]=GameLaunch.State(LaunchProblem.InvalidProgram);Check(((LaunchState)Call(w,"PlayerStatus",p)!).Problem==LaunchProblem.InvalidProgram,"Fixture failure not recorded");bool seen=false;
                var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};timer.Tick+=(_,_)=>{var dialog=Application.Current.Windows.OfType<AppDialog>().LastOrDefault();if(dialog==null||seen)return;seen=true;Render(dialog,Path.Combine(root,"launch-failure-recheck.png"));var button=dialog.ActionButtons.Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="다시 확인");button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));};timer.Start();try{Call(w,"ShowLaunchProblem",p,failures[p.Id],"generated invalid executable error");}finally{timer.Stop();}
                Check(seen&&!failures.ContainsKey(p.Id)&&ReferenceEquals(scan,Get<ScanSnapshot>(w,"snapshot"))&&!Get<bool>(w,"scanning"),"Recheck retained stale failure or rescanned all games");Check(!((LaunchState)Call(w,"PlayerStatus",p)!).NeedsAttention,"Stale failure still blocks retry");
            }
            finally{Set(w,"closingWithoutSave",true);w.Close();}
        });
        File.WriteAllText(args[1],JsonSerializer.Serialize(new{total=results.Count,passed=results.Count-failed,failed,results},new JsonSerializerOptions{WriteIndented=true}));return failed==0?0:1;
    }
}

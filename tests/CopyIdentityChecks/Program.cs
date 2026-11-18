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
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    static string Refuse(Action action){try{action();}catch(Exception e)when(e is IOException or InvalidDataException){return e.Message;}throw new Exception("Expected refusal");}
    sealed record Fixture(string Root,string Data,Package Original,Package Copy,WorkspaceDocument Plan,TagCatalog Tags,PlayerDocument Play,LibraryDocument Library)
    {
        public EditBundle Prepare()=>CopyIdentity.Prepare(Plan,Tags,Play,Library,Copy.Source,Original.Id,[Original.Source,Copy.Source]);
    }
    static Fixture Setup(string root)
    {
        var input=Path.Combine(root,"input");var original=Path.Combine(input,"Original");var copy=Path.Combine(input,"Copy");Directory.CreateDirectory(original);Directory.CreateDirectory(copy);File.WriteAllText(Path.Combine(original,"Game.exe"),"generated fixture; never execute");File.Copy(Path.Combine(original,"Game.exe"),Path.Combine(copy,"Game.exe"));
        var p=new Package{Id=Guid.NewGuid().ToString("N"),Source=original,SearchRoot=input,DetectedRoot=original,Name="Original",Folder="Original",IsReal=true,Registered=true,Alias="원본 가상 게임"};LibraryStore.EnsureIdentity(original,p.Id);File.Copy(Path.Combine(original,LibraryStore.MarkerName),Path.Combine(copy,LibraryStore.MarkerName));
        var tag=new TagDefinition{Kind="category",Name="가상 분류"};var q=new Package{Id=Guid.NewGuid().ToString("N"),Source=copy,SearchRoot=input,DetectedRoot=copy,Name="Copy",Folder="Copy",IsReal=true,Registered=false,Alias="별도 가상 게임",Categories=[tag.Name],CategoryIds=[tag.Id]};
        var scan=GameScanner.Scan(input);var data=Path.Combine(root,"data");var library=Path.Combine(root,"library");var tags=new TagCatalog{Tags=[tag]};var plan=new WorkspaceDocument{SearchRoot=input,SearchLocations=[new(){Path=input}],Destination=library,BackupRoot=Path.Combine(root,"backups"),SelectedId=q.Id,Packages=[p,q],Tags=tags.Tags,Checkpoints=[WorkspaceReconciler.Capture(p,scan),WorkspaceReconciler.Capture(q,scan)]};
        var play=new PlayerDocument{Games=[new(){Id=p.Id,Executable="Game.exe",Favorite=false},new(){Id=q.Id,Executable="Game.exe",Favorite=true,Hidden=true,Arguments="--generated",WorkingDirectory=".",LastPlayedUtc=DateTime.UnixEpoch}]};new WorkspaceStore(Path.Combine(data,"workspace.json")).Save(plan);new PlayerStore(library).Save(play);var document=new LibraryStore(library).Register(p,tags.Tags,GameScanner.Scan(original)).Document;
        return new(root,data,p,q,plan,tags,play,document);
    }
    static void Fault(EditTransaction tx,string phase)=>typeof(EditTransaction).GetProperty("Checkpoint",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(tx,(Action<string>)(current=>{if(current==phase)throw new IOException("generated interruption "+phase);}));
    static void Set(object o,string field,object? value)=>o.GetType().GetField(field,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(o,value);
    static T Get<T>(object o,string field)=>(T)o.GetType().GetField(field,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(o)!;
    static object? Call(object o,string method,params object?[] args)=>o.GetType().GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly)!.Invoke(o,args);
    static IEnumerable<DependencyObject> Logical(DependencyObject root){yield return root;foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var item in Logical(child))yield return item;}
    static MainWindow Window(Fixture fixture)
    {
        var store=new WorkspaceStore(Path.Combine(fixture.Data,"workspace.json"));var w=new MainWindow();new System.Windows.Interop.WindowInteropHelper(w).EnsureHandle();Set(w,"workspaceStore",store);Set(w,"workspaceLock",store.AcquireLock());Call(w,"ApplyWorkspace",fixture.Plan);Set(w,"snapshot",GameScanner.Scan(fixture.Plan.SearchRoot));Set(w,"sessionReady",true);Set(w,"playerRoot",fixture.Plan.Destination);Set(w,"playerDocument",fixture.Play);Set(w,"libraryDocument",fixture.Library);Set(w,"catalog",fixture.Tags);Set(w,"workspaceBlocked",true);return w;
    }
    static void Render(Window window,string path)
    {
        const int width=620,height=460;var body=(FrameworkElement)window.Content;body.Measure(new Size(width,height));body.Arrange(new Rect(0,0,width,height));body.UpdateLayout();
        foreach(var button in Logical(body).OfType<Button>().Where(b=>b.ActualWidth>0)){var point=button.TranslatePoint(new Point(),body);Check(point.X>=-1&&point.X+button.ActualWidth<=width+1,"Button clipped: "+System.Windows.Automation.AutomationProperties.GetName(button));}
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);var background=new DrawingVisual();using(var draw=background.RenderOpen())draw.DrawRectangle(window.Background,null,new Rect(0,0,width,height));bitmap.Render(background);bitmap.Render(body);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(path);png.Save(output);
    }
    static void InspectDialog(Action open,Action<AppDialog> inspect)
    {
        bool seen=false;Exception? error=null;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
        timer.Tick+=(_,_)=>{var dialog=Application.Current.Windows.OfType<AppDialog>().LastOrDefault();if(dialog==null||seen)return;seen=true;try{inspect(dialog);}catch(Exception ex){error=ex;dialog.Close();}};
        timer.Start();try{open();}finally{timer.Stop();}Check(seen,"Expected save dialog missing");if(error!=null)throw error;
    }
    [STAThread] static int Main(string[] args)
    {
        var root=Path.Combine(Path.GetFullPath(args[0]),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var results=new List<object>();int failed=0;
        void Test(string name,Action<string> run){var dir=Path.Combine(root,name);Directory.CreateDirectory(dir);try{run(dir);results.Add(new{name,passed=true});Console.WriteLine("PASS "+name);}catch(Exception ex){failed++;results.Add(new{name,passed=false,error=ex.ToString()});Console.WriteLine("FAIL "+name+": "+ex.Message);}}
        Test("prepare-is-read-only-and-original-remains-connected",dir=>
        {
            var f=Setup(dir);var original=File.ReadAllBytes(Path.Combine(f.Original.Source,LibraryStore.MarkerName));var marker=File.ReadAllBytes(Path.Combine(f.Copy.Source,LibraryStore.MarkerName));var bundle=f.Prepare();Check(bundle.Version==2,"Older clients could ignore the identity phase");Check(LibraryStore.ReadIdentity(f.Copy.Source)?.GameId==f.Original.Id&&marker.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Copy.Source,LibraryStore.MarkerName))),"Preview wrote marker");Check(bundle.Plan.Packages.Single(p=>p.Id==f.Original.Id).Source==f.Original.Source&&original.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Original.Source,LibraryStore.MarkerName))),"Original connection changed");
        });
        Test("new-copy-registers-and-preserves-alias-tags-and-execution-settings",dir=>
        {
            var f=Setup(dir);f.Copy.ReviewState="identity";var bundle=f.Prepare();var id=bundle.CopyIdentityChanges.Single().NewId;new EditTransaction(f.Data).Commit(bundle);var plan=new WorkspaceStore(Path.Combine(f.Data,"workspace.json")).Load().Document!;var game=plan.Packages.Single(p=>p.Id==id);var play=new PlayerStore(f.Plan.Destination).Load();var stored=play.Games.Single(g=>g.Id==id);var library=new LibraryStore(f.Plan.Destination).Load();Check(game.Registered==true&&game.Alias==f.Copy.Alias&&game.Categories.SequenceEqual(f.Copy.Categories),"Copy metadata lost");Check(stored.Favorite&&stored.Hidden&&stored.Executable=="Game.exe"&&stored.Arguments=="--generated"&&stored.WorkingDirectory=="."&&stored.LastPlayedUtc==DateTime.UnixEpoch,"Execution settings lost");Check(library.Games.Count==2&&library.Games.Single(g=>g.Id==f.Original.Id).LastKnownPath==f.Original.Source&&LibraryStore.ReadIdentity(f.Original.Source)?.GameId==f.Original.Id&&LibraryStore.ReadIdentity(f.Copy.Source)?.GameId==id,"Original changed or copy not registered");Check(!play.Games.Any(g=>g.Id==f.Copy.Id)&&play.Games.Single(g=>g.Id==f.Original.Id).Favorite==false,"Original settings changed or orphaned candidate settings");Check(Directory.GetFiles(f.Copy.Source,LibraryStore.MarkerName+".previous-*").Length==1,"Old marker not preserved");WorkspaceReconciler.Check(game,plan.Checkpoints.Single(c=>c.PackageId==id),GameScanner.Scan(f.Copy.Source));Check(game.ReviewState=="","Transaction's marker files left false changes");
        });
        foreach(var phase in new[]{"intent","copy-identity","workspace","library"})Test("restart-recovers-after-"+phase,dir=>
        {
            var f=Setup(dir);var bundle=f.Prepare();var id=bundle.CopyIdentityChanges.Single().NewId;var tx=new EditTransaction(f.Data);Fault(tx,phase);Refuse(()=>tx.Commit(bundle));Check(File.Exists(tx.Pending),"Durable intent missing");new EditTransaction(f.Data).Recover();Check(!File.Exists(tx.Pending)&&LibraryStore.ReadIdentity(f.Copy.Source)?.GameId==id&&new LibraryStore(f.Plan.Destination).Load().Games.Any(g=>g.Id==id),"Recovery lost ID or registration");Check(new PlayerStore(f.Plan.Destination).Load().Games.Single(g=>g.Id==id).Favorite&&LibraryStore.ReadIdentity(f.Original.Source)?.GameId==f.Original.Id,"Recovery changed original or lost copy settings");
        });
        Test("original-selection-refused-before-marker-change",dir=>
        {
            var f=Setup(dir);var message=Refuse(()=>CopyIdentity.Prepare(f.Plan,f.Tags,f.Play,f.Library,f.Original.Source,f.Original.Id,[f.Original.Source,f.Copy.Source]));Check(message.Contains("원본")&&LibraryStore.ReadIdentity(f.Original.Source)?.GameId==f.Original.Id&&!File.Exists(new EditTransaction(f.Data).Pending),"Original was changed");
        });
        Test("unknown-scope-and-nested-identities-remain-unmodified",dir=>
        {
            var f=Setup(dir);f.Copy.ScopeReviewRequired=true;Refuse(()=>f.Prepare());f.Copy.ScopeReviewRequired=false;var nested=Path.Combine(f.Copy.Source,"Other");Directory.CreateDirectory(nested);LibraryStore.EnsureIdentity(nested,Guid.NewGuid().ToString("N"));Refuse(()=>f.Prepare());Check(LibraryStore.ReadIdentity(f.Copy.Source)?.GameId==f.Original.Id,"Unconfirmed scope identity was replaced");
        });
        Test("related-content-connection-is-not-silently-reassigned",dir=>
        {
            var f=Setup(dir);f.Copy.IsContent=true;f.Copy.ContentOwnerId=f.Original.Id;f.Original.RelatedFolders.Add(new(){Id=f.Copy.Id,Source=f.Copy.Source,Name=f.Copy.Name});Refuse(()=>f.Prepare());Check(LibraryStore.ReadIdentity(f.Copy.Source)?.GameId==f.Original.Id&&f.Original.RelatedFolders.Single().Id==f.Copy.Id,"Related link or marker changed");
        });
        Test("changed-files-before-commit-stop-before-id-change",dir=>
        {
            var f=Setup(dir);var bundle=f.Prepare();File.WriteAllText(Path.Combine(f.Copy.Source,"New.exe"),"generated");var tx=new EditTransaction(f.Data);Refuse(()=>tx.Commit(bundle));Check(LibraryStore.ReadIdentity(f.Copy.Source)?.GameId==f.Original.Id&&File.Exists(tx.Pending),"Changing content was accepted or intent lost");
        });
        Test("post-marker-game-changes-are-preserved-and-still-need-review",dir=>
        {
            var f=Setup(dir);var bundle=f.Prepare();var id=bundle.CopyIdentityChanges.Single().NewId;var tx=new EditTransaction(f.Data);Fault(tx,"copy-identity");Refuse(()=>tx.Commit(bundle));File.WriteAllText(Path.Combine(f.Copy.Source,"Later.exe"),"generated game change");new EditTransaction(f.Data).Recover();var plan=new WorkspaceStore(Path.Combine(f.Data,"workspace.json")).Load().Document!;var p=plan.Packages.Single(p=>p.Id==id);WorkspaceReconciler.Check(p,plan.Checkpoints.Single(c=>c.PackageId==id),GameScanner.Scan(p.Source));Check(p.ReviewState=="changed"&&p.AddedExecutables.Contains("Later.exe")&&File.ReadAllText(Path.Combine(p.Source,"Later.exe"))=="generated game change","Recovery erased or silently approved game changes");
        });
        Test("foreign-marker-during-recovery-is-not-overwritten",dir=>
        {
            var f=Setup(dir);var bundle=f.Prepare();var tx=new EditTransaction(f.Data);Fault(tx,"copy-identity");Refuse(()=>tx.Commit(bundle));var foreign=Guid.NewGuid().ToString("N");File.WriteAllText(Path.Combine(f.Copy.Source,LibraryStore.MarkerName),JsonSerializer.Serialize(new GameIdentity{GameId=foreign}));Refuse(()=>new EditTransaction(f.Data).Recover());Check(LibraryStore.ReadIdentity(f.Copy.Source)?.GameId==foreign&&File.Exists(tx.Pending),"Foreign marker overwritten or recovery intent lost");
        });
        Test("no-other-copy-no-new-id",dir=>
        {
            var f=Setup(dir);File.Delete(Path.Combine(f.Original.Source,LibraryStore.MarkerName));Refuse(()=>f.Prepare());Check(LibraryStore.ReadIdentity(f.Copy.Source)?.GameId==f.Original.Id,"Sole remaining identity unnecessarily changed");
        });
        Test("registered-folder-with-foreign-marker-cannot-be-replaced-as-copy",dir=>
        {
            var f=Setup(dir);var foreignId=Guid.NewGuid().ToString("N");var other=Path.Combine(dir,"ForeignOriginal");Directory.CreateDirectory(other);LibraryStore.EnsureIdentity(other,foreignId);File.WriteAllText(Path.Combine(f.Original.Source,LibraryStore.MarkerName),JsonSerializer.Serialize(new GameIdentity{GameId=foreignId}));
            f.Original.Registered=false;Refuse(()=>CopyIdentity.Prepare(f.Plan,f.Tags,f.Play,f.Library,f.Original.Source,foreignId,[f.Original.Source,other]));f.Original.Registered=true;Refuse(()=>CopyIdentity.Prepare(f.Plan,f.Tags,f.Play,new LibraryDocument(),f.Original.Source,foreignId,[f.Original.Source,other]));
            Check(LibraryStore.ReadIdentity(f.Original.Source)?.GameId==foreignId&&new LibraryStore(f.Plan.Destination).Load().Games.Single().Id==f.Original.Id&&!File.Exists(new EditTransaction(f.Data).Pending),"Existing registration was removed due to mismatched marker");
        });
        Test("excluded-copy-is-refused-before-identity-change",dir=>
        {
            var f=Setup(dir);f.Copy.Excluded=true;var message=Refuse(()=>f.Prepare());Check(message.Contains("제외를 해제")&&LibraryStore.ReadIdentity(f.Copy.Source)?.GameId==f.Original.Id&&!File.Exists(new EditTransaction(f.Data).Pending),"Excluded copy was invisibly registered");
        });
        Test("changed-before-marker-can-cancel-without-locking-app",dir=>
        {
            var f=Setup(dir);var before=File.ReadAllBytes(Path.Combine(f.Data,"workspace.json"));var bundle=f.Prepare();File.WriteAllText(Path.Combine(f.Copy.Source,"Changed.exe"),"generated external change");var tx=new EditTransaction(f.Data);Refuse(()=>tx.Commit(bundle));Check(tx.CanCancelUnappliedCopyIdentity()&&tx.CancelUnappliedCopyIdentity(),"Unapplied changed-folder intent cannot be cancelled");Check(!File.Exists(tx.Pending)&&Directory.GetFiles(f.Data,"edit-pending.json.cancelled-*.json").Length==1&&File.ReadAllBytes(Path.Combine(f.Data,"workspace.json")).SequenceEqual(before)&&LibraryStore.ReadIdentity(f.Copy.Source)?.GameId==f.Original.Id,"Cancel altered metadata, ID or discarded intent");new EditTransaction(f.Data).Commit(new(){Plan=f.Plan,Tags=f.Tags,Play=f.Play});Check(!File.Exists(tx.Pending),"Future editing remains blocked after cancel");
        });
        Test("intent-only-crash-can-be-cancelled",dir=>
        {
            var f=Setup(dir);var tx=new EditTransaction(f.Data);Fault(tx,"intent");Refuse(()=>tx.Commit(f.Prepare()));Check(tx.CanCancelUnappliedCopyIdentity()&&tx.CancelUnappliedCopyIdentity()&&LibraryStore.ReadIdentity(f.Copy.Source)?.GameId==f.Original.Id,"Pre-marker intent cannot safely cancel");
        });
        Test("assigned-marker-cannot-cancel-even-before-applied-flag-save",dir=>
        {
            var f=Setup(dir);var tx=new EditTransaction(f.Data);var bundle=f.Prepare();Fault(tx,"intent");Refuse(()=>tx.Commit(bundle));CopyIdentity.Apply(bundle);Check(!tx.CanCancelUnappliedCopyIdentity()&&!tx.CancelUnappliedCopyIdentity()&&File.Exists(tx.Pending),"Changed marker with old intent was cancellable");new EditTransaction(f.Data).Recover();Check(LibraryStore.ReadIdentity(f.Copy.Source)?.GameId==bundle.CopyIdentityChanges.Single().NewId,"Recovery after marker-only crash lost new ID");
        });
        Test("applied-phase-cannot-cancel-after-marker-is-manually-restored",dir=>
        {
            var f=Setup(dir);var tx=new EditTransaction(f.Data);var bundle=f.Prepare();Fault(tx,"copy-identity");Refuse(()=>tx.Commit(bundle));Check(JsonSerializer.Deserialize<EditBundle>(File.ReadAllText(tx.Pending))!.CopyIdentityApplied,"Applied phase not durable before metadata");File.WriteAllBytes(Path.Combine(f.Copy.Source,LibraryStore.MarkerName),Convert.FromBase64String(bundle.CopyIdentityChanges.Single().MarkerData));Check(!tx.CanCancelUnappliedCopyIdentity()&&!tx.CancelUnappliedCopyIdentity()&&File.Exists(tx.Pending),"A previously assigned identity phase was cancelled");
        });
        Test("cancel-does-not-fall-back-to-older-pending-backup",dir=>
        {
            var f=Setup(dir);var tx=new EditTransaction(f.Data);Fault(tx,"intent");Refuse(()=>tx.Commit(f.Prepare()));File.Copy(tx.Pending,tx.Pending+".bak");File.WriteAllText(tx.Pending,"{broken");Check(!tx.CanCancelUnappliedCopyIdentity(),"Corrupt primary was considered cancellable via backup");bool refused=false;try{tx.CancelUnappliedCopyIdentity();}catch(JsonException){refused=true;}Check(refused&&File.Exists(tx.Pending),"Corrupt primary journal was archived from stale evidence");
        });
        Test("ordinary-pending-edits-cannot-use-copy-cancellation",dir=>
        {
            var f=Setup(dir);var tx=new EditTransaction(f.Data);Fault(tx,"intent");Refuse(()=>tx.Commit(new(){Plan=f.Plan,Tags=f.Tags,Play=f.Play}));Check(!tx.CanCancelUnappliedCopyIdentity()&&!tx.CancelUnappliedCopyIdentity()&&File.Exists(tx.Pending),"Ordinary pending edit was cancellable by copy action");
        });
        Test("malformed-pending-objects-do-not-crash-cancel-availability",dir=>
        {
            var f=Setup(dir);var tx=new EditTransaction(f.Data);File.WriteAllText(tx.Pending,"{\"Version\":1,\"Plan\":null}");Check(!tx.CanCancelUnappliedCopyIdentity(),"Null plan was accepted for cancellation");Refuse(()=>tx.CancelUnappliedCopyIdentity());Check(File.Exists(tx.Pending),"Invalid journal was discarded");
        });
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};var styles=XDocument.Load(Path.Combine(AppContext.BaseDirectory,"styles-source.xaml")).Root!.Elements().Single().Elements().Single();app.Resources=(ResourceDictionary)XamlReader.Parse(styles.ToString(),new ParserContext{BaseUri=new Uri(AppContext.BaseDirectory)});SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Test("save-dialog-can-cancel-unapplied-copy-registration",dir=>
        {
            var f=Setup(dir);var tx=new EditTransaction(f.Data);var bundle=f.Prepare();File.WriteAllText(Path.Combine(f.Copy.Source,"Later.exe"),"generated external change");Refuse(()=>tx.Commit(bundle));var w=Window(f);
            try
            {
                InspectDialog(()=>Call(w,"ShowWorkspaceSaveIssue"),dialog=>{Render(dialog,Path.Combine(root,"copy-registration-cancel.png"));var cancel=dialog.ActionButtons.Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="등록 취소");cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));});
                Check(!File.Exists(tx.Pending)&&!Get<bool>(w,"workspaceBlocked")&&LibraryStore.ReadIdentity(f.Copy.Source)?.GameId==f.Original.Id,"UI cancellation did not unblock unchanged registration");
            }
            finally{Set(w,"closingWithoutSave",true);w.Close();}
        });
        Test("ordinary-edit-recovery-dialog-does-not-offer-copy-cancel",dir=>
        {
            var f=Setup(dir);var tx=new EditTransaction(f.Data);Fault(tx,"intent");Refuse(()=>tx.Commit(new(){Plan=f.Plan,Tags=f.Tags,Play=f.Play}));var w=Window(f);
            try
            {
                InspectDialog(()=>Call(w,"ShowWorkspaceSaveIssue"),dialog=>{Check(!dialog.ActionButtons.Any(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="등록 취소"),"Ordinary edit exposed destructive cancellation");Check(dialog.ActionButtons.Any(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="편집 복구"),"Recovery action missing");Render(dialog,Path.Combine(root,"ordinary-edit-recovery.png"));dialog.Close();});Check(File.Exists(tx.Pending)&&Get<bool>(w,"workspaceBlocked"),"Closing warning lost pending edit");
            }
            finally{Set(w,"closingWithoutSave",true);w.Close();}
        });
        File.WriteAllText(args[1],JsonSerializer.Serialize(new{total=results.Count,passed=results.Count-failed,failed,results},new JsonSerializerOptions{WriteIndented=true}));return failed==0?0:1;
    }
}

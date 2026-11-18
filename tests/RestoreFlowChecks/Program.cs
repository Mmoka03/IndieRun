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
    static void Check(bool value,string error){if(!value)throw new Exception(error);}
    static void Set(object o,string field,object? value)=>o.GetType().GetField(field,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(o,value);
    static T Get<T>(object o,string field)=>(T)o.GetType().GetField(field,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(o)!;
    static object? Call(object o,string method,params object?[] args)=>o.GetType().GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(o,args);
    static void Seam(BackupEngine e,string name,object value)=>typeof(BackupEngine).GetProperty(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(e,value);
    static void Wait(Task t){var frame=new DispatcherFrame();t.ContinueWith(_=>Application.Current.Dispatcher.BeginInvoke(new Action(()=>frame.Continue=false)));Dispatcher.PushFrame(frame);t.GetAwaiter().GetResult();}
    static T Run<T>(Func<Task<T>> f)=>Task.Run(f).GetAwaiter().GetResult();
    static void Refuse(Action action){try{action();}catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException){return;}throw new Exception("Expected refusal");}
    static IEnumerable<DependencyObject> Logical(DependencyObject root){yield return root;foreach(var c in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var d in Logical(c))yield return d;}
    static Package Fixture(string dir)
    {
        var root=Path.Combine(dir,"input","Generated");Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"Game.exe"),"generated fixture; never execute");File.WriteAllBytes(Path.Combine(root,"data.bin"),new byte[2*1024*1024]);
        return new(){Id=Guid.NewGuid().ToString("N"),IsReal=true,Registered=false,Source=root,DetectedRoot=root,SearchRoot=Path.GetDirectoryName(root)!,Folder="Generated",Name="Generated",Alias="가상 게임"};
    }
    static (Package Game,BackupEngine Engine,BackupJob Job) Backed(string dir)
    {var p=Fixture(dir);var e=new BackupEngine(Path.Combine(dir,"backups"));return(p,e,Run(()=>e.CreateAsync(p,[])));}
    static (Package Game,LibraryStore Library,MoveJob Move,IdentityInventory Inventory) SelectiveRegisteredMove(string dir)
    {
        var p=Fixture(dir);Directory.CreateDirectory(Path.Combine(p.Source,"Extras"));File.WriteAllText(Path.Combine(p.Source,"Extras","keep.txt"),"excluded fixture");p.OmittedFolders=["Extras"];var library=new LibraryStore(Path.Combine(dir,"library"));library.Register(p,[],GameScanner.Scan(p.SearchRoot));p.Registered=true;
        var engine=new BackupEngine(Path.Combine(dir,"backups"));var backup=Run(()=>engine.CreateAsync(p,[]));var move=Run(()=>engine.MoveAsync(backup.Id,Path.Combine(dir,"target","Generated")));p.Source=move.Destination;p.DetectedRoot=p.Source;p.OmittedFolders=[];return(p,library,move,LibraryStore.Inspect(GameScanner.Scan(p.Source)));
    }
    static void Manifest(BackupEngine e,BackupJob j){File.WriteAllText(Path.Combine(e.Root,j.Id,"manifest.json"),JsonSerializer.Serialize(j));}
    static void MoveRecord(BackupEngine e,BackupJob j,string state)
    {var m=new MoveJob{BackupId=j.Id,Source=j.SourceRoot,Destination=Path.Combine(Path.GetDirectoryName(e.Root)!,"target","Generated"),Status=state};var d=Path.Combine(e.Root,j.Id,"moves");Directory.CreateDirectory(d);File.WriteAllText(Path.Combine(d,m.Id+".json"),JsonSerializer.Serialize(m));}
    static void RestoreRecord(BackupEngine e,BackupJob j,string state)
    {var r=new RestoreJob{BackupId=j.Id,Status=state};r.TargetContainer=Path.Combine(Path.GetDirectoryName(e.Root)!,"restore-"+r.Id);var d=Path.Combine(e.Root,j.Id,"restores");Directory.CreateDirectory(d);File.WriteAllText(Path.Combine(d,r.Id+".json"),JsonSerializer.Serialize(r));}
    static MainWindow Window(string dir,Package p)
    {
        var scan=GameScanner.Scan(p.SearchRoot);scan.PrepareIndex();var doc=new WorkspaceDocument{SearchRoot=p.SearchRoot,SearchLocations=[new(){Path=p.SearchRoot}],Destination=Path.Combine(dir,"library"),BackupRoot=Path.Combine(dir,"backups"),Packages=[p],Checkpoints=[WorkspaceReconciler.Capture(p,scan)]};
        var store=new WorkspaceStore(Path.Combine(dir,"data","workspace.json"));store.Save(doc);var w=new MainWindow();new System.Windows.Interop.WindowInteropHelper(w).EnsureHandle();Set(w,"workspaceStore",store);Set(w,"workspaceLock",store.AcquireLock());Call(w,"ApplyWorkspace",doc);Set(w,"snapshot",scan);Set(w,"sessionReady",true);Set(w,"playerRoot",doc.Destination);Set(w,"playerDocument",new PlayerDocument());Set(w,"directEngineFactory",(Func<BackupEngine>)(()=>new(Path.Combine(dir,"records"))));return w;
    }
    static void Close(MainWindow w){Set(w,"closingWithoutSave",true);w.Close();}
    static void CancelNextDialog(Action action)
    {
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};bool found=false;timer.Tick+=(_,_)=>{var dialog=Application.Current.Windows.OfType<AppDialog>().LastOrDefault();if(dialog==null)return;found=true;timer.Stop();dialog.Close();};timer.Start();try{action();Check(found,"Expected change confirmation was not shown");}finally{timer.Stop();}
    }
    static void Capture(FrameworkElement body,string path,double width,double height)
    {
        body.Measure(new Size(width,height));body.Arrange(new Rect(0,0,width,height));body.UpdateLayout();
        foreach(var button in Logical(body).OfType<Button>().Where(b=>b.IsVisible&&b.ActualWidth>0)){var at=button.TranslatePoint(new Point(),body);Check(at.X>=-1&&at.X+button.ActualWidth<=width+1,"Clipped action: "+button.Content);}
        var bitmap=new RenderTargetBitmap((int)width,(int)height,96,96,PixelFormats.Pbgra32);var bg=new DrawingVisual();using(var d=bg.RenderOpen())d.DrawRectangle(Brushes.White,null,new Rect(0,0,width,height));bitmap.Render(bg);bitmap.Render(body);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);png.Save(stream);
    }
    [STAThread]static int Main(string[] args)
    {
        var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);var report=Path.GetFullPath(args[1]);var gallery=Path.Combine(root,"screens");Directory.CreateDirectory(gallery);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};var styles=XDocument.Load(Path.Combine(AppContext.BaseDirectory,"styles-source.xaml")).Root!.Elements().Single().Elements().Single();app.Resources=(ResourceDictionary)XamlReader.Parse(styles.ToString(),new ParserContext{BaseUri=new Uri(AppContext.BaseDirectory)});SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var results=new List<object>();var failed=0;
        void Test(string name,Action<string> action){var dir=Path.Combine(root,Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(dir);try{action(dir);results.Add(new{name,passed=true});Console.WriteLine("PASS "+name);}catch(Exception e){failed++;results.Add(new{name,passed=false,error=e.ToString()});Console.WriteLine("FAIL "+name+" "+e.Message);}}

        Package Ancestor(Package p)=>new(){Id=Guid.NewGuid().ToString("N"),IsReal=true,Registered=false,Source=p.SearchRoot,DetectedRoot=p.SearchRoot,SearchRoot=p.SearchRoot,Folder="input",Name="input"};
        (Package P,ScanSnapshot Scan) WithGuide(string dir){var p=Fixture(dir);Directory.CreateDirectory(Path.Combine(p.Source,"Guide"));File.WriteAllText(Path.Combine(p.Source,"Guide","guide.txt"),"generated guide");return(p,GameScanner.Scan(p.SearchRoot));}
        Test("exclude-guide-with-ancestor-through-move-undo-and-restore",dir=>{
            var (p,scan)=WithGuide(dir);var ancestor=Ancestor(p);File.WriteAllText(Path.Combine(ancestor.Source,"Launcher.exe"),"fixture");scan=GameScanner.Scan(p.SearchRoot);
            var draft=JsonSerializer.Deserialize<Package>(JsonSerializer.Serialize(p))!;CompositionPlan.Assign(draft,Path.Combine(p.Source,"Guide"),"exclude",new(),scan);
            var next=CompositionPlan.Apply(draft,[p,ancestor],scan);var main=next.Single(q=>q.Id==p.Id);var e=new BackupEngine(Path.Combine(dir,"backups"));var b=Run(()=>e.CreateAsync(main,[]));
            var move=Run(()=>e.MoveAsync(b.Id,Path.Combine(dir,"library","Generated")));
            Check(File.Exists(Path.Combine(p.Source,"Guide","guide.txt"))&&!File.Exists(Path.Combine(p.Source,"Game.exe"))&&File.Exists(Path.Combine(move.Destination,"Game.exe")),"Wrong selective transfer");
            Check(!Directory.Exists(Path.Combine(move.Destination,"Guide"))&&!b.Files.Any(f=>f.RelativePath.StartsWith("Guide")),"Excluded guide copied");
            Run(()=>e.UndoMoveAsync(b.Id,move.Id));Check(File.Exists(Path.Combine(p.Source,"Game.exe")),"Undo missing exe");
            var target=Path.Combine(dir,"restored");Directory.CreateDirectory(target);var restored=Run(()=>e.RestoreAsync(b.Id,target));
            Check(File.Exists(Path.Combine(e.RestoredPayload(b,restored),"Game.exe"))&&!Directory.Exists(Path.Combine(e.RestoredPayload(b,restored),"Guide")),"Restore exceeded backup scope");
        });
        foreach(var protectedKind in new[]{"registered","marker","explicit-scope","launch-settings"})Test("exclude-preserves-"+protectedKind,dir=>{
            var (p,scan)=WithGuide(dir);var other=Ancestor(p);
            if(protectedKind=="registered")other.Registered=true;
            if(protectedKind=="marker")LibraryStore.EnsureIdentity(other.Source,other.Id);
            if(protectedKind=="explicit-scope")other.ScopeHistory.Add(other.Source);
            var before=JsonSerializer.Serialize(new[]{p,other});var draft=JsonSerializer.Deserialize<Package>(JsonSerializer.Serialize(p))!;CompositionPlan.Assign(draft,Path.Combine(p.Source,"Guide"),"exclude",new(),scan);
            try{CompositionPlan.Apply(draft,[p,other],scan,protectedKind=="launch-settings"?new HashSet<string>{other.Id}:null);throw new Exception("Missing conflict");}
            catch(CompositionConflictException ex){Check(ex.Other.Id==other.Id&&ex.Folder.EndsWith("Guide"),"Conflict has no actionable identity");}
            Check(before==JsonSerializer.Serialize(new[]{p,other})&&File.Exists(Path.Combine(p.Source,"Guide","guide.txt")),"Rejected edit modified source");
        });
        Test("history-restore-visible-with-details-collapsed-and-after-undo",dir=>{
            var f=Backed(dir);var move=Run(()=>f.Engine.MoveAsync(f.Job.Id,Path.Combine(dir,"target","Generated")));Run(()=>f.Engine.UndoMoveAsync(f.Job.Id,move.Id));
            var history=new WorkHistoryStore(Path.Combine(dir,"history.json")).Refresh([f.Engine.Root]);var w=Window(dir,f.Game);
            try{Set(w,"workHistory",history);foreach(var record in history.Records.Where(r=>r.Kind is "Move" or "Backup")){
                var card=(Border)Call(w,"HistoryCard",record)!;Capture(card,Path.Combine(gallery,record.Kind+"-restore.png"),700,250);
                var button=Logical(card).OfType<Button>().Single(b=>b.Content as string=="백업에서 복원");Check(button.ActualWidth>0&&button.ActualHeight>0,"Restore hidden in details");
                Check(Logical(card).OfType<Expander>().All(e=>!e.IsExpanded),"Details unexpectedly expanded");}
                Call(w,"PaintHistory");Check(Get<StackPanel>(w,"JobsContent").Children.OfType<Expander>().Single(e=>e.Header?.ToString()?.StartsWith("완료 기록")==true).IsExpanded,"Completed records collapsed");
            }finally{Close(w);}
        });
        foreach(var state in new[]{"Deleted","Failed","Interrupted","Offline","WithoutBackup"})Test("history-no-restore-"+state,dir=>{
            var p=Fixture(dir);var w=Window(dir,p);try{var record=new WorkRecord{Kind="Backup",Root=Path.Combine(dir,"backups"),JobId=Guid.NewGuid().ToString("N"),State=state is "Offline" or "WithoutBackup"?"Completed":state,Availability=state=="Offline"?"Offline":"Connected",Verified=true,WithoutBackup=state=="WithoutBackup",BackupRemoved=state=="Deleted"};
            var card=(Border)Call(w,"HistoryCard",record)!;Check(!Logical(card).OfType<Button>().Any(b=>b.Content as string=="백업에서 복원"),"Unusable restore offered");}finally{Close(w);}
        });
        Test("manager-restore-visible-and-opens-plan-for-selected-backup",dir=>{
            var f=Backed(dir);var w=Window(dir,f.Game);Window? manager=null;
            try{manager=(Window)Call(w,"BuildBackupManagerAt",f.Engine)!;manager.Show();Wait(Task.Delay(350));Capture((FrameworkElement)manager.Content,Path.Combine(gallery,"backup-manager.png"),740,530);
                var button=Logical(manager).OfType<Button>().Single(b=>b.Content as string=="복원");Check(button.ActualWidth>0&&button.IsEnabled,"Restore not visible");
                bool saw=false;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};timer.Tick+=(_,_)=>{var plan=Application.Current.Windows.Cast<Window>().FirstOrDefault(x=>x.Title=="백업에서 복원");if(plan==null)return;saw=true;timer.Stop();Check(Logical(plan).OfType<TextBlock>().Any(t=>t.Text==PlayerRules.Title(f.Game)),"Wrong backup selected");plan.Close();};timer.Start();button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Wait(Task.Delay(200));timer.Stop();Check(saw&&!manager.IsVisible,"Manager did not route to restore plan");Check(f.Engine.ListRestores(f.Job.Id).Count==0,"Cancel created restore");
            }finally{manager?.Close();Close(w);}
        });
        foreach(var width in new[]{500,600})Test("restore-plan-layout-and-selection-"+width,dir=>{
            var f=Backed(dir);var w=Window(dir,f.Game);Window? plan=null;var target=Path.Combine(dir,"restored");Directory.CreateDirectory(target);string? accepted=null;
            try{Set(w,"restoreFolderPicker",(Func<Window,string?>)(_=>target));plan=(Window)Call(w,"BuildRestorePlan",f.Job,f.Engine.Root,(Action<string>)(p=>accepted=p))!;
                Capture((FrameworkElement)plan.Content,Path.Combine(gallery,"restore-plan-"+width+".png"),width,380);
                var start=Logical(plan).OfType<Button>().Single(b=>b.Name=="StartRestore");Check(!start.IsEnabled,"No target yet enabled");
                var choose=Logical(plan).OfType<Button>().Single(b=>b.Name=="ChooseRestoreFolder");Check(choose.Focusable&&start.IsTabStop,"Keyboard actions unavailable");choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(start.IsEnabled&&accepted==null,"Selection started restore prematurely");Capture((FrameworkElement)plan.Content,Path.Combine(gallery,"restore-selected-"+width+".png"),width,380);
                start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(accepted==target&&f.Engine.ListRestores(f.Job.Id).Count==0,"Wrong target or premature side effects");
            }finally{plan?.Close();Close(w);}
        });
        Test("restore-picker-cancel-and-invalid-target-remain-safe",dir=>{
            var f=Backed(dir);var w=Window(dir,f.Game);Window? plan=null;bool accepted=false;try{
                Set(w,"restoreFolderPicker",(Func<Window,string?>)(_=>null));plan=(Window)Call(w,"BuildRestorePlan",f.Job,f.Engine.Root,(Action<string>)(_=>accepted=true))!;
                var choose=Logical(plan).OfType<Button>().Single(b=>b.Name=="ChooseRestoreFolder");var start=Logical(plan).OfType<Button>().Single(b=>b.Name=="StartRestore");choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(!start.IsEnabled,"Cancel selected target");
                Set(w,"restoreFolderPicker",(Func<Window,string?>)(_=>f.Game.Source));choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(!start.IsEnabled&&!accepted,"Source target accepted");Capture((FrameworkElement)plan.Content,Path.Combine(gallery,"restore-location-warning.png"),500,410);
                Check(f.Engine.ListRestores(f.Job.Id).Count==0&&File.Exists(Path.Combine(f.Game.Source,"Game.exe")),"Invalid target wrote files");
            }finally{plan?.Close();Close(w);}
        });
        Test("composition-conflict-offers-inspection-and-keeps-draft",dir=>{
            var (p,scan)=WithGuide(dir);var other=Ancestor(p);other.Registered=true;var w=Window(dir,p);Window? editor=null;
            try{Set(w,"packages",new List<Package>{p,other});Set(w,"compositionSnapshot",scan);editor=(Window)Call(w,"BuildCompositionEditor",p)!;Capture((FrameworkElement)editor.Content,Path.Combine(gallery,"composition-before.png"),780,560);
                var tree=Logical(editor).OfType<TreeView>().Single();var root=(TreeViewItem)tree.Items[0];root.IsExpanded=true;var guide=root.Items.OfType<TreeViewItem>().Single(i=>(string?)i.Tag==Path.Combine(p.Source,"Guide"));guide.IsSelected=true;
                Logical(editor).OfType<Button>().Single(b=>b.Content as string=="포함하지 않음").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Logical(editor).OfType<Button>().Single(b=>b.Name=="CompositionApply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var inspect=Logical(editor).OfType<Button>().Single(b=>b.Content as string=="겹치는 항목 보기");Check(inspect.Visibility==Visibility.Visible&&p.OmittedFolders.Count==0,"No conflict action or modified live state");Capture((FrameworkElement)editor.Content,Path.Combine(gallery,"composition-conflict.png"),780,560);
                Logical(editor).OfType<Button>().Single(b=>b.Content as string=="본편에 포함").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }finally{editor?.Close();Close(w);}
        });

        Test("history-confirmed-restore-writes-selected-target-and-refreshes-records",dir=>{
            var f=Backed(dir);var w=Window(dir,f.Game);var target=Path.Combine(dir,"restored");Directory.CreateDirectory(target);bool saw=false;
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            try{Set(w,"restoreFolderPicker",(Func<Window,string?>)(_=>target));
                timer.Tick+=(_,_)=>{var plan=Application.Current.Windows.Cast<Window>().FirstOrDefault(x=>x.Title=="백업에서 복원");if(plan==null)return;timer.Stop();saw=true;
                    Logical(plan).OfType<Button>().Single(b=>b.Name=="ChooseRestoreFolder").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Logical(plan).OfType<Button>().Single(b=>b.Name=="StartRestore").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));};timer.Start();
                Wait((Task)Call(w,"RunHistoryBackupAsync",new WorkRecord{Root=f.Engine.Root,Kind="Backup",JobId=f.Job.Id,GameId=f.Game.Id,Title="가상 게임",State="Completed",Availability="Connected",Verified=true},"restore")!);
                var result=f.Engine.ListRestores(f.Job.Id).Single();Check(saw&&result.Status=="Completed"&&GameScanner.Within(result.TargetContainer,target),"UI restore did not complete at selected location");
                Check(File.Exists(Path.Combine(f.Engine.RestoredPayload(f.Job,result),"Game.exe"))&&File.Exists(Path.Combine(f.Game.Source,"Game.exe")),"Restoration failed or modified original");
                Check(Get<List<Package>>(w,"packages").Single().Source==f.Game.Source,"Restoring copy changed launch location");
                Check(Get<WorkHistoryDocument>(w,"workHistory").Records.Any(r=>r.Kind=="Restore"&&r.State=="Completed"),"Completion missing from history");
            }finally{timer.Stop();Close(w);}
        });
        File.WriteAllText(report,JsonSerializer.Serialize(new{total=results.Count,passed=results.Count-failed,failed,results},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"RESULT {results.Count-failed}/{results.Count}");return failed==0?0:1;
    }
}

using IndieRun;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;

public static class Program
{
    static void Check(bool pass,string message){if(!pass)throw new Exception(message);}
    static object? Call(object target,string name,params object?[] args)=>target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(target,args);
    static T Get<T>(object target,string name)=>(T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)!.GetValue(target)!;
    static void Set(object target,string name,object? value)=>target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)!.SetValue(target,value);
    static IEnumerable<T> Find<T>(DependencyObject root) where T:DependencyObject
    {if(root is T item)yield return item;foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var match in Find<T>(child))yield return match;}
    static void Wait(Task task)
    {var frame=new DispatcherFrame();task.ContinueWith(_=>Application.Current.Dispatcher.BeginInvoke(new Action(()=>frame.Continue=false)));Dispatcher.PushFrame(frame);task.GetAwaiter().GetResult();}
    [STAThread]
    public static int Main(string[] args)
    {
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var styles=XDocument.Load(Path.Combine(AppContext.BaseDirectory,"styles-source.xaml")).Root!.Elements().Single().Elements().Single();
        app.Resources=(ResourceDictionary)XamlReader.Parse(styles.ToString(),new ParserContext {BaseUri=new Uri(AppContext.BaseDirectory)});
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var results=new List<object>();int failed=0;
        void Test(string name,Action test){try{test();results.Add(new{name,status="PASS"});Console.WriteLine("PASS: "+name);}catch(Exception ex){failed++;results.Add(new{name,status="FAIL",error=ex.ToString()});Console.WriteLine("FAIL: "+name+" "+ex.Message);}}
        Test("mixed-results-retain-failed-and-unprocessed",()=>{var s=new OperationStatus("이동",[new("a","A"),new("b","B"),new("c","C")]);s.Begin("a");s.Mark("a","Completed");s.Begin("b");s.Mark("b","Failed","locked");s.Finish(true);Check(s.Done==1&&s.Failed==1&&s.Pending==1&&s.Heading.Contains("확인 필요"),"Counts or heading wrong");});
        Test("rollback-replaces-completed-count",()=>{var s=new OperationStatus("이동",[new("a","A")]);s.Mark("a","Completed");s.Mark("a","RolledBack");s.Finish(true);Check(s.Done==0&&s.Restored==1&&s.Heading=="이동 중단","Rollback reported complete");});
        Test("cancel-is-distinct-from-failure",()=>{var s=new OperationStatus("백업",[new("a","A"),new("b","B")]);s.Mark("a","Interrupted");s.Finish(true);Check(s.Failed==0&&s.Interrupted==1&&s.Pending==1&&s.Counts.Contains("중단 1"),"Cancellation mislabeled");});
        Test("phases-distinguish-backup-and-move-checks",()=>{Check(OperationStatus.ProgressLabel("Verifying")=="백업 확인 중"&&OperationStatus.ProgressLabel("MoveVerifying")=="이동 결과 확인 중","Ambiguous phase");});
        var root=Path.Combine(Path.GetFullPath(args[0]),"run-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var w=new MainWindow();
        var tags=new TagCatalog {Tags=[new(){Id=Guid.NewGuid().ToString("N"),Kind="genre",Name="퍼즐"},new(){Id=Guid.NewGuid().ToString("N"),Kind="category",Name="짧은 게임"}]};
        var packages=new List<Package>{new(){Id=Guid.NewGuid().ToString("N"),Alias="첫 게임",Folder="A",Name="A",Source=Path.Combine(root,"source","A"),Genres=["퍼즐"],Categories=["짧은 게임"]},new(){Id=Guid.NewGuid().ToString("N"),Alias="둘째 게임",Folder="B",Name="B",Source=Path.Combine(root,"source","B"),Genres=["퍼즐"]}};
        Set(w,"packages",packages);Set(w,"catalog",tags);Set(w,"destination",Path.Combine(root,"library"));Set(w,"playerRoot",Path.Combine(root,"library"));Set(w,"backupStorageRoot",Path.Combine(root,"backups"));Call(w,"SetPage","play");Call(w,"RenderPlayer");
        Test("filter-search-and-both-kinds-share-collapsed-parent",()=>{var e=Get<Expander>(w,"playerTagExpander");Check(!e.IsExpanded,"Default expanded");Check(Find<TextBox>((DependencyObject)e.Content).Any()&&Find<ToggleButton>((DependencyObject)e.Content).Count()==2,"Filter elements outside fold");});
        Test("collapsed-active-filters-still-apply-and-show-count",()=>{var view=Get<PlayerView>(w,"playerView");view.TagIds=tags.Tags.Select(t=>t.Id).ToList();Call(w,"RenderPlayer");var e=Get<Expander>(w,"playerTagExpander");Check(!e.IsExpanded&&e.Header.ToString()!.Contains("2개 적용 중"),"Hidden active filters");Check(Get<TextBlock>(w,"playerCount").Text.StartsWith("게임 1개"),"AND filtering changed");});
        Test("filter-open-state-survives-render",()=>{Get<Expander>(w,"playerTagExpander").IsExpanded=true;Call(w,"RenderPlayer");Check(Get<Expander>(w,"playerTagExpander").IsExpanded,"Lost expanded state");});
        Test("reset-clears-hidden-filters",()=>{var button=Find<Button>(Get<DockPanel>(w,"PlayerContent")).Single(b=>b.Content?.ToString()=="필터 초기화");button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(Get<PlayerView>(w,"playerView").TagIds.Count==0&&Get<TextBlock>(w,"playerCount").Text.StartsWith("게임 2개"),"Reset failed");});
        Test("fixed-summary-outside-record-scroll",()=>{var summary=Get<StackPanel>(w,"JobsSummary");Check(summary.Parent is DockPanel&&DockPanel.GetDock(summary)==Dock.Top,"Summary scrolls with records");});
        Test("live-and-result-summary-narrow-layout",()=>{var run=new OperationStatus("이동",[new("a",new string('가',60)),new("b","B")]);run.Begin("a");Set(w,"operationStatus",run);Set(w,"backupBusy",true);Call(w,"RenderOperationSummary");var summary=Get<StackPanel>(w,"JobsSummary");summary.Measure(new Size(650,500));summary.Arrange(new Rect(0,0,650,summary.DesiredSize.Height));Check(summary.DesiredSize.Height<350&&Find<Button>(summary).Single().Content?.ToString()=="작업 중지","Live controls clipped");Set(w,"backupBusy",false);run.Mark("a","Failed",new string('가',5000));run.Finish(true);Call(w,"RenderOperationSummary");summary.Measure(new Size(650,500));Check(summary.DesiredSize.Height<250&&Find<Expander>(summary).Single().IsExpanded==false,"Long failure expands summary");});
        Test("real-backup-move-and-result-update",()=>
        {
            var p=packages[0];Directory.CreateDirectory(p.Source);File.WriteAllText(Path.Combine(p.Source,"Game.exe"),"fixture");File.WriteAllBytes(Path.Combine(p.Source,"data.bin"),new byte[1024*1024]);p.DetectedRoot=p.Source;p.SearchRoot=Path.GetDirectoryName(p.Source)!;p.IsReal=true;
            var originalSource=p.Source;var stages=new List<string>();
            Func<BackupEngine,IProgress<BackupProgress>,CancellationToken,Task> action=async(engine,progress,token)=>
            {
                await (Task)Call(w,"BeginOperationItem",p,"백업 준비 중")!;
                var capture=new Capture(progress,stages);var backup=await engine.CreateAsync(p,[],capture,token);
                var move=await engine.MoveAsync(backup.Id,Path.Combine(root,"library","A"),progress:capture,cancellation:token);
                await (Task)Call(w,"MarkOperationItem",p.Id,"Completed",null)!;
            };
            Wait((Task)Call(w,"RunBackupOperation","이동",action,new List<Package>{p},null)!);
            var status=Get<OperationStatus>(w,"operationStatus");Check(status.Finished&&status.Done==1&&status.Failed==0,"Wrong result");Check(stages.Contains("Copying")&&stages.Contains("Verifying")&&stages.Contains("MoveChecking")&&stages.Contains("MoveVerifying"),"Missing progress stages");Check(Directory.Exists(Path.Combine(root,"library","A"))&&!Directory.Exists(originalSource),"Move failed");Check(new BackupEngine(Path.Combine(root,"backups")).ListJobs().Single().Status=="Completed","Backup not retained");
        });
        Test("handled-stop-shows-results-without-a-second-dialog",()=>
        {
            var p=packages[1];bool duplicate=false;var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(10)};
            timer.Tick+=(_,_)=>{foreach(var d in w.OwnedWindows.OfType<AppDialog>().ToList()){duplicate=true;d.Close();}};timer.Start();
            Func<BackupEngine,IProgress<BackupProgress>,CancellationToken,Task> action=async(engine,progress,token)=>
            {await (Task)Call(w,"BeginOperationItem",p,"백업 중")!;await (Task)Call(w,"MarkOperationItem",p.Id,"Failed","fixture locked")!;await w.Dispatcher.InvokeAsync(()=>Set(w,"stopPending",true));throw new IOException("사용자가 중단을 선택했습니다.");};
            try{Wait((Task)Call(w,"RunBackupOperation","이동",action,new List<Package>{p},null)!);}finally{timer.Stop();}
            var status=Get<OperationStatus>(w,"operationStatus");Check(!duplicate&&status.Finished&&status.Failed==1&&status.Stopped,"Duplicate prompt or lost failure");
        });
        Test("failed-backup-actions-precede-collapsed-completed-history",()=>
        {
            var source=Path.Combine(root,"locked-game");Directory.CreateDirectory(source);var file=Path.Combine(source,"Game.exe");File.WriteAllText(file,"fixture");
            var p=new Package {Id=Guid.NewGuid().ToString("N"),Source=source,DetectedRoot=source,SearchRoot=root,Folder="locked-game",Name="locked-game",Alias="실패한 게임",IsReal=true};
            using(var hold=File.Open(file,FileMode.Open,FileAccess.Read,FileShare.None))
            {try{Wait(new BackupEngine(Path.Combine(root,"backups")).CreateAsync(p,[]));throw new Exception("Failure expected");}catch(IOException){}}
            Call(w,"RenderBackupJobs");Wait((Task)Call(w,"RefreshHistoryAsync")!);var content=Get<StackPanel>(w,"JobsContent");var history=content.Children.OfType<Expander>().Single(e=>e.Header?.ToString()?.StartsWith("완료 기록")==true);Check(!history.IsExpanded,"History hides recovery actions");
            var first=content.Children.OfType<Border>().First();Check(Find<TextBlock>(first).Any(t=>t.Text=="실패한 게임")&&Find<Button>(first).Any(b=>b.Content?.ToString()=="백업 재개"),"Recovery actions not first");
        });
        Set(w,"closingWithoutSave",true);w.Close();
        File.WriteAllText(Path.GetFullPath(args[1]),JsonSerializer.Serialize(new{root,total=results.Count,passed=results.Count-failed,failed,results},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"RESULT: {results.Count-failed}/{results.Count}");return failed==0?0:1;
    }
    sealed class Capture(IProgress<BackupProgress> next,List<string> stages):IProgress<BackupProgress>
    {public void Report(BackupProgress value){stages.Add(value.Stage);next.Report(value);}}
}



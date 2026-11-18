using IndieRun;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
class Program
{
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static void Refuse(Action action){try{action();}catch(Exception ex) when(ex is IOException or InvalidDataException){return;}throw new Exception("Expected refusal");}
    static void Set(object o,string name,object? value)=>o.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(o,value);
    static object? Call(object o,string name)=>o.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(o,null);
    [STAThread]static int Main(string[] args)
    {
        var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);var results=new List<object>();int failed=0;
        void Test(string name,Action<string> run){try{var dir=Path.Combine(root,name);Directory.CreateDirectory(dir);run(dir);results.Add(new{name,passed=true});}catch(Exception ex){failed++;results.Add(new{name,passed=false,error=ex.ToString()});}}
        (WorkspaceDocument Plan,TagCatalog Tags,PlayerDocument Play,LibraryDocument Library) Data(string dir)
        {
            var src=Path.Combine(dir,"games","Garden");Directory.CreateDirectory(src);File.WriteAllText(Path.Combine(src,"Game.exe"),"harmless fixture");
            var p=new Package{Id=Guid.NewGuid().ToString("N"),Source=src,DetectedRoot=src,SearchRoot=Path.Combine(dir,"games"),Name="Garden",Folder="Garden",Alias="작은 정원",IsReal=true,Registered=true};LibraryStore.EnsureIdentity(src,p.Id);
            var plan=new WorkspaceDocument{SearchRoot=Path.Combine(dir,"games"),SearchLocations=[new(){Path=Path.Combine(dir,"games")}],Destination=Path.Combine(dir,"old"),BackupRoot=Path.Combine(dir,"backup"),Packages=[p]};
            var tags=new TagCatalog();var library=new LibraryStore(plan.Destination).Register(p,tags.Tags,GameScanner.Scan(plan.SearchRoot)).Document;
            Directory.CreateDirectory(Path.Combine(plan.Destination,".indierun-art"));File.WriteAllText(Path.Combine(plan.Destination,".indierun-art","cover.png"),"fixture artwork");
            var play=new PlayerDocument{Games=[new(){Id=p.Id,Favorite=true,Hidden=true,Executable="Game.exe",Arguments="--keep",Image=".indierun-art/cover.png"}]};new PlayerStore(plan.Destination).Save(play);new WorkspaceStore(Path.Combine(dir,"data","workspace.json")).Save(plan);return(plan,tags,play,library);
        }
        Test("missing-empty-location",dir=>{var f=Data(dir);var plan=f.Plan;plan.Destination=Path.Combine(dir,"nonexistent");plan.Packages=[];var b=LibraryLocation.Prepare(plan,new(),new(),new(),Path.Combine(dir,"new"));new EditTransaction(Path.Combine(dir,"data")).Commit(b);Check(new WorkspaceStore(Path.Combine(dir,"data","workspace.json")).Load().Document!.Destination==b.Plan.Destination,"Path not saved");});
        Test("metadata-art-and-files-preserved",dir=>{var f=Data(dir);var target=Path.Combine(dir,"new");var b=LibraryLocation.Prepare(f.Plan,f.Tags,f.Play,f.Library,target);new EditTransaction(Path.Combine(dir,"data")).Commit(b);var play=new PlayerStore(target).Load().Games.Single();var game=new LibraryStore(target).Load().Games.Single();Check(play.Favorite&&play.Hidden&&play.Arguments=="--keep"&&File.ReadAllText(Path.Combine(target,play.Image))=="fixture artwork","Settings lost");Check(game.Alias=="작은 정원"&&game.LastKnownPath==f.Plan.Packages[0].Source&&game.Status=="External"&&File.Exists(Path.Combine(game.LastKnownPath,"Game.exe")),"Game moved or lost");Check(File.Exists(new PlayerStore(f.Plan.Destination).FilePath),"Old settings removed");});
        foreach(var point in new[]{"intent","tags","workspace","play","library"})Test("recover-"+point,dir=>{var f=Data(dir);var b=LibraryLocation.Prepare(f.Plan,f.Tags,f.Play,f.Library,Path.Combine(dir,"new"));var tx=new EditTransaction(Path.Combine(dir,"data"));typeof(EditTransaction).GetProperty("Checkpoint",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(tx,(Action<string>)(p=>{if(p==point)throw new IOException("simulated interruption");}));Refuse(()=>tx.Commit(b));new EditTransaction(Path.Combine(dir,"data")).Recover();Check(new LibraryStore(b.Plan.Destination).Load().Games.Count==1&&new PlayerStore(b.Plan.Destination).Load().Games.Single().Favorite,"Recovery lost data");});
        Test("game-subfolder-refused",dir=>{var f=Data(dir);Refuse(()=>LibraryLocation.Prepare(f.Plan,f.Tags,f.Play,f.Library,Path.Combine(f.Plan.Packages[0].Source,"nested")));});
        Test("target-id-conflict-refused",dir=>{var f=Data(dir);var target=Path.Combine(dir,"new");new LibraryStore(target).Update(doc=>{var game=JsonSerializer.Deserialize<LibraryGame>(JsonSerializer.Serialize(f.Library.Games[0]))!;game.LastKnownPath=Path.Combine(dir,"other");doc.Games.Add(game);});Refuse(()=>LibraryLocation.Prepare(f.Plan,f.Tags,f.Play,f.Library,target));});
        Test("locked-player-refused-before-intent",dir=>{var f=Data(dir);var target=Path.Combine(dir,"new");new PlayerStore(target).Save(new());using var locked=new FileStream(new PlayerStore(target).FilePath,FileMode.Open,FileAccess.ReadWrite,FileShare.None);Refuse(()=>LibraryLocation.Prepare(f.Plan,f.Tags,f.Play,f.Library,target));Check(new WorkspaceStore(Path.Combine(dir,"data","workspace.json")).Load().Document!.Destination==f.Plan.Destination,"Old path changed");});
        Test("existing-target-keeps-its-other-games",dir=>{var f=Data(dir);var other=Data(Path.Combine(dir,"other"));var target=other.Plan.Destination;var id=new LibraryStore(target).Load().LibraryId;var b=LibraryLocation.Prepare(f.Plan,f.Tags,f.Play,f.Library,target);new EditTransaction(Path.Combine(dir,"data")).Commit(b);Check(new LibraryStore(target).Load().Games.Count==2&&new LibraryStore(target).Load().LibraryId==id&&new PlayerStore(target).Load().Games.Count==2,"Target overwritten");});
        Test("former-library-games-remain-searchable",dir=>{var f=Data(dir);var p=f.Plan.Packages[0];var before=p.Source;var moved=Path.Combine(f.Plan.Destination,"Garden");Directory.Move(before,moved);p.Source=p.DetectedRoot=moved;p.SearchRoot=f.Plan.Destination;var b=LibraryLocation.Prepare(f.Plan,f.Tags,f.Play,f.Library,Path.Combine(dir,"new"));Check(b.Plan.SearchLocations!.Any(l=>l.Enabled&&l.Path==f.Plan.Destination),"Old games no longer searched");});
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};var x=XDocument.Load(Path.Combine(AppContext.BaseDirectory,"styles-source.xaml"));var resource=x.Root!.Elements().Single();app.Resources=(ResourceDictionary)XamlReader.Parse(resource.Elements().Single().ToString(),new ParserContext{BaseUri=new Uri(AppContext.BaseDirectory)});
        Test("visible-location-actions",dir=>{var w=new MainWindow();Set(w,"destination",Path.Combine(dir,"missing"));try{Call(w,"RenderLibrary");var content=(StackPanel)w.FindName("LibraryContent");var actions=content.Children.OfType<WrapPanel>().Single();Check(actions.Children.OfType<Button>().Any(b=>b.Content as string=="위치 변경"),"Missing change action");foreach(var size in new[]{new Size(650,400),new Size(1100,700)}){content.Measure(size);content.Arrange(new Rect(size));content.UpdateLayout();Check(actions.Children.OfType<Button>().All(b=>b.TranslatePoint(new Point(),content).X+b.ActualWidth<=size.Width+1),"Actions clipped");}Check(content.Children.OfType<TextBlock>().Any(t=>t.Text.Contains("위치 변경")),"No recovery hint");}finally{Set(w,"closingWithoutSave",true);w.Close();}});
        File.WriteAllText(args[1],JsonSerializer.Serialize(new{total=results.Count,passed=results.Count-failed,failed,results},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"RESULT {results.Count-failed}/{results.Count}");return failed==0?0:1;
    }
}

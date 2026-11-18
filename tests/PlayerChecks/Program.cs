using IndieRun;
using System.Diagnostics;
using System.Text.Json;

static void Check(bool yes,string message){if(!yes)throw new Exception(message);}
static void Fails(Action action){try{action();}catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){return;}throw new Exception("Expected failure");}
static T Copy<T>(T item)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(item))!;
if(args[0]=="--probe"){File.WriteAllText(args[1],JsonSerializer.Serialize(new{directory=Environment.CurrentDirectory,arguments=args.Skip(2).ToArray()}));return;}
if(args[0]=="--edit-child")
{
    var bundle=JsonSerializer.Deserialize<EditBundle>(File.ReadAllText(Path.Combine(args[1],"bundle.json")))!;var tx=new EditTransaction(Path.Combine(args[1],"data"));
    tx.Checkpoint=point=>{if(point=="workspace"){Console.WriteLine("READY");Console.Out.Flush();Thread.Sleep(Timeout.Infinite);}};tx.Commit(bundle);return;
}
var run=Path.Combine(Path.GetFullPath(args[0]),"run-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(run);
int failed=0;var results=new List<object>();
async Task Run(string name,Func<string,Task> action)
{var root=Path.Combine(run,name);Directory.CreateDirectory(root);try{await action(root);results.Add(new{name,status="PASS"});Console.WriteLine("PASS: "+name);}catch(Exception ex){failed++;results.Add(new{name,status="FAIL",error=ex.Message,trace=ex.StackTrace});Console.WriteLine("FAIL: "+name+" · "+ex.Message);}}
Task Done()=>Task.CompletedTask;
var genre=new TagDefinition {Name="퍼즐",Kind="genre"};var horror=new TagDefinition {Name="공포",Kind="genre"};var category=new TagDefinition {Name="미완료",Kind="category"};var tags=new List<TagDefinition>{genre,horror,category};
Package Package(string root)=>new(){IsReal=true,Id=Guid.NewGuid().ToString("N"),Source=Path.Combine(root,"game"),SearchRoot=root,DetectedRoot=Path.Combine(root,"game"),Name="Original",Folder="Original",Alias="내 별칭",Genres=[genre.Name],GenreIds=[genre.Id],Categories=[category.Name],CategoryIds=[category.Id]};
(Package Package,EditBundle Bundle) Bundle(string root)
{
    var p=Package(root);Directory.CreateDirectory(p.Source);File.WriteAllBytes(Path.Combine(p.Source,"Game.exe"),[1,2,3]);
    return (p,new EditBundle {Plan=new WorkspaceDocument {SearchRoot=root,Destination=Path.Combine(root,"library"),Packages=[p],Tags=Copy(tags)},Tags=new TagCatalog {Tags=Copy(tags)},Play=new PlayerDocument {Games=[new GamePlay {Id=p.Id,Executable="Game.exe"}]},EditedIds=[p.Id]});
}
await Run("alias-default-original-option-and-empty-alias",root=>
{
    var p=Package(root);var g=new GamePlay{Id=p.Id};var v=new PlayerView{Query="Original"};Check(!PlayerRules.Matches(p,g,v,tags),"Original searched by default");v.IncludeOriginal=true;Check(PlayerRules.Matches(p,g,v,tags),"Original option ignored");v.IncludeOriginal=false;p.Alias="";Check(PlayerRules.Matches(p,g,v,tags),"Empty alias fallback failed");return Done();
});
await Run("all-selected-tags-are-and",root=>
{
    var p=Package(root);var g=new GamePlay{Id=p.Id};var v=new PlayerView{Query="별칭",TagIds=[genre.Id,category.Id]};Check(PlayerRules.Matches(p,g,v,tags),"Cross kind AND failed");v.TagIds.Add(horror.Id);Check(!PlayerRules.Matches(p,g,v,tags),"Same kind became OR");p.Genres.Add(horror.Name);Check(PlayerRules.Matches(p,g,v,tags),"All tags did not match");v.TagIds.Add("deleted");Check(!PlayerRules.Matches(p,g,v,tags),"Unknown filter silently ignored");return Done();
});
await Run("favorites-recent-and-excluded",root=>
{
    var p=Package(root);var g=new GamePlay{Id=p.Id};var v=new PlayerView{Group="favorites"};Check(!PlayerRules.Matches(p,g,v,tags),"Nonfavorite visible");g.Favorite=true;Check(PlayerRules.Matches(p,g,v,tags),"Favorite hidden");v.Group="recent";Check(!PlayerRules.Matches(p,g,v,tags),"Never played recent");g.LastPlayedUtc=DateTime.UtcNow;Check(PlayerRules.Matches(p,g,v,tags),"Recent hidden");p.Excluded=true;Check(!PlayerRules.Matches(p,g,v,tags),"Excluded visible");return Done();
});
await Run("batch-add-remove-leaves-other-tags",root=>
{
    var p=Package(root);var q=Package(root);q.Genres.Add(horror.Name);PlayerRules.ApplyTags(p,tags,new HashSet<string>{horror.Id},new HashSet<string>{category.Id});Check(p.Genres.Contains(genre.Name)&&p.Genres.Contains(horror.Name)&&p.Categories.Count==0,"Batch replaced unrelated tags");PlayerRules.ApplyTags(q,tags,new HashSet<string>(),new HashSet<string>{genre.Id});Check(q.Genres.SequenceEqual([horror.Name])&&q.Categories.Contains(category.Name),"Batch unrelated removal");Fails(()=>PlayerRules.ApplyTags(p,tags,new HashSet<string>{genre.Id},new HashSet<string>{genre.Id}));return Done();
});
await Run("draft-cancel-no-live-mutation",root=>
{
    var p=Package(root);var catalog=new TagCatalog{Tags=Copy(tags)};var draft=Copy(p);var draftTags=Copy(catalog);draft.Alias="changed";draftTags.Tags.Add(new TagDefinition{Name="draft"});draft.Genres.Clear();Check(p.Alias=="내 별칭"&&p.Genres.Count==1&&catalog.Tags.Count==3,"Live data changed before save");Check(!Directory.EnumerateFileSystemEntries(root).Any(),"Draft wrote files");return Done();
});
await Run("delete-tag-includes-missing-and-excluded-records",root=>
{
    var p=Package(root);p.Excluded=true;var doc=new LibraryDocument {Games=[new LibraryGame{Id=Guid.NewGuid().ToString("N"),Folder="missing",Status="Missing",Genres=[genre.Name],GenreIds=[genre.Id],Categories=[category.Name]}]};PlayerRules.ChangeTag([p],doc,genre,null);Check(p.Genres.Count==0&&p.GenreIds.Count==0&&doc.Games[0].Genres.Count==0&&doc.Games[0].GenreIds.Count==0,"Tag survived on missing/excluded");Check(doc.Games[0].Categories.Contains(category.Name),"Unrelated category removed");return Done();
});
await Run("rename-tag-preserves-membership",root=>
{
    var p=Package(root);p.GenreIds=[];PlayerRules.ChangeTag([p],new LibraryDocument(),genre,"新名称");Check(p.Genres.SequenceEqual(["新名称"]),"Rename lost string-bound membership");return Done();
});
await Run("old-player-schema-defaults-and-portable-paths",root=>
{
    var store=new PlayerStore(root);Check(store.Load().Games.Count==0,"Empty state not supported");var g=new GamePlay{Id=Guid.NewGuid().ToString("N"),Executable="Data/Game.exe",Image=".indierun-art/a.png",Favorite=true,CropX=.2,CropY=.8};store.Save(new PlayerDocument{Games=[g]});var read=store.Load().Games.Single();Check(read.Favorite&&read.Executable==g.Executable&&read.CropY==.8,"Settings roundtrip");return Done();
});
await Run("unsafe-launch-settings-rejected",root=>
{
    var p=Package(root);Directory.CreateDirectory(p.Source);File.WriteAllText(Path.Combine(p.Source,"Game.exe"),"fake");
    foreach(var exe in new[]{"../outside.exe","C:\\outside.exe","Game.bat"})Fails(()=>PlayerStore.StartInfo(p,new GamePlay{Id=p.Id,Executable=exe}));
    Fails(()=>PlayerStore.StartInfo(p,new GamePlay{Id=p.Id,Executable="Game.exe",WorkingDirectory="../outside"}));Fails(()=>PlayerStore.Validate(new PlayerDocument {Games=[new GamePlay {Id=p.Id,Image="../image.png"}]}));return Done();
});
await Run("missing-executable-and-working-folder-refuse",root=>
{
    var p=Package(root);Directory.CreateDirectory(p.Source);var game=new GamePlay {Id=p.Id,Executable="Gone.exe"};Fails(()=>PlayerStore.StartInfo(p,game));Check(game.LastPlayedUtc==null,"Failed launch recorded recent");File.WriteAllText(Path.Combine(p.Source,"Game.exe"),"fake");game.Executable="Game.exe";game.WorkingDirectory="missing";Fails(()=>PlayerStore.StartInfo(p,game));return Done();
});
await Run("explicit-working-folder-defaults-and-shell-disabled",root=>
{
    var p=Package(root);Directory.CreateDirectory(Path.Combine(p.Source,"Data"));File.WriteAllBytes(Path.Combine(p.Source,"Data","Game.exe"),[1]);var g=new GamePlay {Id=p.Id,Executable="Data/Game.exe",Arguments="--value \"two words\""};var info=PlayerStore.StartInfo(p,g);Check(!info.UseShellExecute&&info.WorkingDirectory==Path.Combine(p.Source,"Data")&&info.Arguments==g.Arguments,"Default working directory or argument mismatch");g.WorkingDirectory=".";Check(PlayerStore.StartInfo(p,g).WorkingDirectory==p.Source,"Root workdir ignored");return Done();
});
await Run("real-harmless-test-program-launch-and-recent",async root=>
{
    var p=Package(root);Directory.CreateDirectory(p.Source);foreach(var file in Directory.GetFiles(AppContext.BaseDirectory)){File.Copy(file,Path.Combine(p.Source,Path.GetFileName(file)));}
    var output=Path.Combine(root,"probe output.json");var game=new GamePlay {Id=p.Id,Executable=Path.GetFileName(Environment.ProcessPath!),Arguments="--probe \""+output+"\" \"two words\""};
    using var process=PlayerStore.Launch(p,game);await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));Check(process.ExitCode==0&&File.Exists(output)&&game.LastPlayedUtc!=null,"Test program did not launch");using var doc=JsonDocument.Parse(File.ReadAllText(output));Check(doc.RootElement.GetProperty("directory").GetString()==p.Source&&doc.RootElement.GetProperty("arguments")[0].GetString()=="two words","Arguments/working dir incorrect");
});
await Run("transaction-save-new-tag-and-player-state",root=>
{
    var (p,bundle)=Bundle(root);bundle.Tags.Tags.Add(new TagDefinition{Name="New tag"});bundle.Plan.Packages[0].Alias="updated";var tx=new EditTransaction(Path.Combine(root,"data"));tx.Commit(bundle);Check(!File.Exists(tx.Pending),"Intent not archived");Check(new WorkspaceStore(Path.Combine(root,"data","workspace.json")).Load().Document!.Packages[0].Alias=="updated","Plan not saved");Check(new TagCatalogStore(Path.Combine(root,"data","tags.json")).Load().Tags.Any(t=>t.Name=="New tag"),"New tag lost");Check(new PlayerStore(bundle.Plan.Destination).Load().Games.Single().Executable=="Game.exe","Player not saved");return Done();
});
foreach(var phase in new[]{"intent","tags","workspace","play","library"})await Run("interrupted-edit-replay-"+phase,root=>
{
    var (p,bundle)=Bundle(root);var tx=new EditTransaction(Path.Combine(root,"data"));tx.Checkpoint=point=>{if(point==phase)throw new IOException("Injected interruption");};Fails(()=>tx.Commit(bundle));Check(File.Exists(tx.Pending),"No durable intent");var recovery=new EditTransaction(Path.Combine(root,"data"));Check(recovery.Recover()&&!recovery.Recover(),"Recovery not idempotent");Check(new PlayerStore(bundle.Plan.Destination).Load().Games.Single().Id==p.Id,"Recovered player missing");return Done();
});
await Run("tag-delete-transaction-updates-offline-library-csv",root=>
{
    var (p,bundle)=Bundle(root);var store=new LibraryStore(bundle.Plan.Destination);Directory.CreateDirectory(p.Source);store.Register(p,tags,GameScanner.Scan(root));var missingId=Guid.NewGuid().ToString("N");store.Update(doc=>doc.Games.Add(new LibraryGame{Id=missingId,Folder="offline",Genres=[genre.Name],GenreIds=[genre.Id],Status="Missing"}));
    bundle.Tags.Tags.RemoveAll(t=>t.Id==genre.Id);PlayerRules.ChangeTag(bundle.Plan.Packages,new LibraryDocument(),genre,null);bundle.TagChanges=[new TagChange{Id=genre.Id,Kind=genre.Kind,OldName=genre.Name}];new EditTransaction(Path.Combine(root,"data")).Commit(bundle);var doc=store.Load();Check(doc.Games.All(g=>!g.Genres.Contains(genre.Name)&&!g.GenreIds.Contains(genre.Id))&&!doc.Tags.Any(t=>t.Id==genre.Id),"Offline tag survived delete");Check(!File.ReadAllText(Path.Combine(store.Root,LibraryStore.CsvName)).Contains(genre.Name),"CSV retained deleted tag");return Done();
});
await Run("changed-identity-refuses-execution",root=>
{
    var p=Package(root);Directory.CreateDirectory(p.Source);File.WriteAllBytes(Path.Combine(p.Source,"Game.exe"),[1,2,3]);LibraryStore.EnsureIdentity(p.Source,Guid.NewGuid().ToString("N"));Fails(()=>PlayerStore.StartInfo(p,new GamePlay {Id=p.Id,Executable="Game.exe"}));return Done();
});
await Run("real-process-kill-during-edit-replays",async root=>
{
    var (p,bundle)=Bundle(root);File.WriteAllText(Path.Combine(root,"bundle.json"),JsonSerializer.Serialize(bundle));
    var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in new[]{"--edit-child",root})start.ArgumentList.Add(arg);
    using var child=Process.Start(start)!;
    try{var line=await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));Check(line=="READY","Child did not reach checkpoint");child.Kill(true);await child.WaitForExitAsync();}
    finally{if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}}
    var tx=new EditTransaction(Path.Combine(root,"data"));Check(tx.Recover()&&!File.Exists(tx.Pending),"Killed save did not recover");Check(new PlayerStore(bundle.Plan.Destination).Load().Games.Single().Id==p.Id,"Player settings lost after kill");
});
await Run("locked-csv-keeps-pending-until-repaired",root=>
{
    var (p,bundle)=Bundle(root);var store=new LibraryStore(bundle.Plan.Destination);store.Register(p,tags,GameScanner.Scan(root));var tx=new EditTransaction(Path.Combine(root,"data"));
    using(var locked=new FileStream(Path.Combine(store.Root,LibraryStore.CsvName),FileMode.Open,FileAccess.ReadWrite,FileShare.None))Fails(()=>tx.Commit(bundle));Check(File.Exists(tx.Pending),"Partial save lost intent");Check(tx.Recover()&&!File.Exists(tx.Pending),"Repair did not complete");return Done();
});
File.WriteAllText(Path.GetFullPath(args[1]),JsonSerializer.Serialize(new{runRoot=run,total=results.Count,passed=results.Count-failed,failed,results},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"RESULT: {results.Count-failed}/{results.Count}");Environment.ExitCode=failed==0?0:1;

using IndieRun;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
static string Game(string root,string name="Original")
{var path=Path.Combine(root,name);Directory.CreateDirectory(Path.Combine(path,"Data"));File.WriteAllBytes(Path.Combine(path,"Data","Game.exe"),[1,2,3]);File.WriteAllText(Path.Combine(path,"readme.txt"),"game data");return path;}
static Package Package(string path)=>new(){IsReal=true,Id=Guid.NewGuid().ToString("N"),Source=path,SearchRoot=Path.GetDirectoryName(path)!,DetectedRoot=Path.Combine(path,"Data"),Folder=Path.GetFileName(path),Name=Path.GetFileName(path),Alias="별칭, \"日本語\"\n두 줄",Genres=["퍼즐","공포"],Categories=["내 분류"]};
static List<TagDefinition> Tags()=>[new(){Kind="genre",Name="퍼즐"},new(){Kind="genre",Name="공포"},new(){Kind="category",Name="내 분류"}];
static void CopyFolder(string source,string target)
{Directory.CreateDirectory(target);foreach(var dir in Directory.GetDirectories(source,"*",SearchOption.AllDirectories))Directory.CreateDirectory(Path.Combine(target,Path.GetRelativePath(source,dir)));foreach(var file in Directory.GetFiles(source,"*",SearchOption.AllDirectories))File.Copy(file,Path.Combine(target,Path.GetRelativePath(source,file)));}
static void Fails(Action action)
{try{action();}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException){return;}throw new Exception("Expected refusal");}
static List<List<string>> Csv(string text)
{
    var rows=new List<List<string>>();var row=new List<string>();var cell=new StringBuilder();bool quoted=false;
    for(int i=0;i<text.Length;i++)
    {
        var c=text[i];if(c=='"'){if(quoted&&i+1<text.Length&&text[i+1]=='"'){cell.Append('"');i++;}else quoted=!quoted;}
        else if(c==','&&!quoted){row.Add(cell.ToString());cell.Clear();}
        else if(c=='\n'&&!quoted){row.Add(cell.ToString().TrimEnd('\r'));rows.Add(row);row=[];cell.Clear();}
        else cell.Append(c);
    }
    Check(!quoted,"CSV quote not closed");return rows;
}
var run=Path.Combine(Path.GetFullPath(args[0]),"run-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(run);
var results=new List<object>();int failed=0;
async Task Run(string name,Func<string,Task> test)
{var root=Path.Combine(run,name);Directory.CreateDirectory(root);try{await test(root);results.Add(new{name,status="PASS"});Console.WriteLine("PASS: "+name);}catch(Exception ex){failed++;results.Add(new{name,status="FAIL",error=ex.Message,trace=ex.StackTrace});Console.WriteLine("FAIL: "+name+" · "+ex.Message);}}
Task Done()=>Task.CompletedTask;
await Run("registration-marker-metadata-and-csv",root=>
{
    var path=Game(root);var p=Package(path);var store=new LibraryStore(root);var before=File.ReadAllBytes(Path.Combine(path,"Data","Game.exe"));var update=store.Register(p,Tags(),GameScanner.Scan(root));
    Check(update.CsvError==null&&update.Document.Games.Single().Id==p.Id,"Registration failed");Check(LibraryStore.ReadIdentity(path)!.GameId==p.Id,"ID differs");
    var marker=File.ReadAllText(Path.Combine(path,LibraryStore.MarkerName));store.Register(p,Tags(),GameScanner.Scan(root));Check(File.ReadAllText(Path.Combine(path,LibraryStore.MarkerName))==marker,"ID changed");
    Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(path,"Data","Game.exe"))),"Game modified");var csvPath=Path.Combine(root,LibraryStore.CsvName);Check(File.ReadAllBytes(csvPath).Take(3).SequenceEqual(new byte[]{239,187,191}),"No UTF8 BOM");
    var rows=Csv(File.ReadAllText(csvPath));Check(rows[1].Count==8&&rows[1][2]==p.Alias,"CSV multiline/quotes mismatch");Check(rows[1][3].Contains("퍼즐")&&rows[1][5].Contains("Game.exe"),"Unreadable arrays");return Done();
});
await Run("manual-rename-preserves-alias-and-baseline",root=>
{
    var path=Game(root);var p=Package(path);var store=new LibraryStore(root);var doc=store.Register(p,Tags(),GameScanner.Scan(root)).Document;var checkpoints=new List<FolderCheckpoint>{WorkspaceReconciler.Capture(p,GameScanner.Scan(root))};var renamed=Path.Combine(root,"Renamed");Directory.Move(path,renamed);
    var scan=GameScanner.Scan(root);var inventory=LibraryStore.Inspect(scan);var packages=new List<Package>{p};IdentityReconciler.Rebind(packages,checkpoints,doc,inventory,scan,root);WorkspaceReconciler.Check(p,checkpoints.Single(),scan);
    Check(p.Source==renamed&&p.Alias.Contains("日本語")&&p.ReviewState=="","Rename lost data or falsely flagged");store.Reconcile(doc,inventory,scan);Check(doc.Games.Single().Folder=="Renamed","Catalog path not updated");return Done();
});
await Run("renamed-and-content-changed-still-requires-review",root=>
{
    var path=Game(root);var p=Package(path);var store=new LibraryStore(root);var doc=store.Register(p,Tags(),GameScanner.Scan(root)).Document;var checks=new List<FolderCheckpoint>{WorkspaceReconciler.Capture(p,GameScanner.Scan(root))};var next=Path.Combine(root,"Moved");Directory.Move(path,next);File.WriteAllText(Path.Combine(next,"readme.txt"),"new content after rename");
    var scan=GameScanner.Scan(root);IdentityReconciler.Rebind([p],checks,doc,LibraryStore.Inspect(scan),scan,root);WorkspaceReconciler.Check(p,checks.Single(),scan);Check(p.Source==next&&p.ReviewState=="changed","Content change silently approved");return Done();
});
await Run("duplicate-id-never-auto-merges",root=>
{
    var path=Game(root);var p=Package(path);var store=new LibraryStore(root);var doc=store.Register(p,Tags(),GameScanner.Scan(root)).Document;CopyFolder(path,Path.Combine(root,"Copy"));var scan=GameScanner.Scan(root);var inventory=LibraryStore.Inspect(scan);store.Reconcile(doc,inventory,scan);
    var checks=new List<FolderCheckpoint>{WorkspaceReconciler.Capture(p,scan)};IdentityReconciler.Rebind([p],checks,doc,inventory,scan,root);WorkspaceReconciler.Check(p,checks.Single(),scan);IdentityReconciler.Flag(p,inventory,doc);
    Check(inventory.Locations[p.Id].Count==2&&p.Source==path&&p.ReviewState=="identity"&&doc.Games.Single().Status=="Duplicate","Duplicate silently resolved");Fails(()=>store.Register(p,Tags(),scan));return Done();
});
await Run("copy-new-id-is-distinct-and-old-id-preserved",root=>
{
    var path=Game(root);var p=Package(path);var store=new LibraryStore(root);var doc=store.Register(p,Tags(),GameScanner.Scan(root)).Document;var copy=Path.Combine(root,"Copy");CopyFolder(path,copy);var id=LibraryStore.AssignCopyIdentity(copy,p.Id);Check(id!=p.Id&&LibraryStore.ReadIdentity(path)!.GameId==p.Id,"Wrong ID changed");
    Check(Directory.GetFiles(copy,LibraryStore.MarkerName+".previous-*").Length==1,"Former marker not preserved");var q=Package(copy);q.Id=id;store.Register(q,Tags(),GameScanner.Scan(root));Check(store.Load().Games.Count==2,"Separate copy not registered");return Done();
});
await Run("copy-split-candidate-adopts-remaining-id",root=>
{
    var path=Game(root);var p=Package(path);var store=new LibraryStore(root);var doc=store.Register(p,Tags(),GameScanner.Scan(root)).Document;var copy=Path.Combine(root,"Copy");CopyFolder(path,copy);var q=Package(copy);q.Alias="";q.Genres=[];q.Categories=[];var oldId=p.Id;p.Id=LibraryStore.AssignCopyIdentity(path,oldId);
    var scan=GameScanner.Scan(root);var packages=new List<Package>{p,q};var checks=new List<FolderCheckpoint>();IdentityReconciler.Rebind(packages,checks,doc,LibraryStore.Inspect(scan),scan,root);Check(q.Id==oldId&&q.Alias==doc.Games.Single().Alias&&p.Id!=oldId,"Remaining copy not linked");return Done();
});
await Run("missing-id-not-reconnected-by-name",root=>
{
    var path=Game(root);var p=Package(path);var store=new LibraryStore(root);var doc=store.Register(p,Tags(),GameScanner.Scan(root)).Document;File.Move(Path.Combine(path,LibraryStore.MarkerName),Path.Combine(root,"saved-marker.json"));var scan=GameScanner.Scan(root);var inv=LibraryStore.Inspect(scan);store.Reconcile(doc,inv,scan);WorkspaceReconciler.Check(p,WorkspaceReconciler.Capture(p,scan),scan);IdentityReconciler.Flag(p,inv,doc);
    Check(doc.Games.Single().Status=="IdentityMissing"&&p.ReviewState=="identity","Name assumed identity");return Done();
});
await Run("missing-game-retains-metadata",root=>
{
    var path=Game(root);var p=Package(path);var store=new LibraryStore(root);var doc=store.Register(p,Tags(),GameScanner.Scan(root)).Document;var elsewhere=Path.Combine(run,"off-scope-"+Guid.NewGuid().ToString("N"));Directory.Move(path,elsewhere);var scan=GameScanner.Scan(root);store.Reconcile(doc,LibraryStore.Inspect(scan),scan);Check(doc.Games.Single().Status=="Missing"&&doc.Games.Single().Alias==p.Alias,"Missing game erased");return Done();
});
await Run("portable-library-reinstall-import",root=>
{
    var first=Path.Combine(root,"old");Directory.CreateDirectory(first);var path=Game(first);var p=Package(path);var doc=new LibraryStore(first).Register(p,Tags(),GameScanner.Scan(first)).Document;var second=Path.Combine(root,"new");CopyFolder(first,second);var store=new LibraryStore(second);var loaded=store.Load();var scan=GameScanner.Scan(second);var inv=LibraryStore.Inspect(scan);store.Reconcile(loaded,inv,scan);var packages=new List<Package>();IdentityReconciler.Rebind(packages,[],loaded,inv,scan,second);
    Check(packages.Single().Id==p.Id&&packages.Single().Source==Path.Combine(second,"Original")&&packages.Single().Alias==p.Alias,"Portable import failed");Check(loaded.LibraryId==doc.LibraryId,"Library ID changed");return Done();
});
await Run("existing-candidate-import-keeps-user-edit",root=>
{
    var path=Game(root);var p=Package(path);var doc=new LibraryStore(root).Register(p,Tags(),GameScanner.Scan(root)).Document;var fresh=Package(path);fresh.Alias="user current alias";var scan=GameScanner.Scan(root);IdentityReconciler.Rebind([fresh],[],doc,LibraryStore.Inspect(scan),scan,root);Check(fresh.Id==p.Id&&fresh.Alias=="user current alias","Existing edits overwritten");return Done();
});
await Run("malformed-and-oversized-markers-preserved",root=>
{
    var a=Game(root,"A");var b=Game(root,"B");File.WriteAllText(Path.Combine(a,LibraryStore.MarkerName),"{bad");File.WriteAllText(Path.Combine(b,LibraryStore.MarkerName),new string('x',5000));var inv=LibraryStore.Inspect(GameScanner.Scan(root));Check(inv.Problems.Count==2&&inv.Locations.Count==0,"Invalid marker accepted");Fails(()=>LibraryStore.EnsureIdentity(a,Guid.NewGuid().ToString("N")));Check(File.ReadAllText(Path.Combine(a,LibraryStore.MarkerName))=="{bad","Invalid marker overwritten");return Done();
});
await Run("bad-relative-metadata-blocked",root=>
{
    var path=Game(root);var p=Package(path);var store=new LibraryStore(root);store.Register(p,Tags(),GameScanner.Scan(root));Fails(()=>store.Update(doc=>doc.Games[0].Folder="../escape"));Fails(()=>store.Update(doc=>doc.Games[0].Executables=["C:\\outside.exe"]));Check(store.Load().Games.Single().Folder=="Original","Invalid update saved");return Done();
});
await Run("corrupt-primary-recovers-and-preserves-invalid",root=>
{
    var path=Game(root);var p=Package(path);var store=new LibraryStore(root);store.Register(p,Tags(),GameScanner.Scan(root));store.Update(doc=>doc.Games[0].Alias="second");File.WriteAllText(Path.Combine(root,LibraryStore.JsonName),"{broken");Check(store.Load().Games.Single().Alias==p.Alias,"Previous valid record not recovered");store.Update(_=>{});Check(Directory.GetFiles(root,LibraryStore.JsonName+".invalid-*").Length>0,"Invalid record lost");return Done();
});
await Run("both-corrupt-block-writes",root=>
{
    var path=Game(root);var p=Package(path);var store=new LibraryStore(root);store.Register(p,Tags(),GameScanner.Scan(root));store.Update(_=>{});File.WriteAllText(Path.Combine(root,LibraryStore.JsonName),"bad-main");File.WriteAllText(Path.Combine(root,LibraryStore.JsonName+".bak"),"bad-bak");Fails(()=>store.Update(_=>{}));Check(File.ReadAllText(Path.Combine(root,LibraryStore.JsonName))=="bad-main","Bad metadata overwritten");return Done();
});
await Run("foreign-csv-and-note-never-overwritten",root=>
{
    var path=Game(root);File.WriteAllText(Path.Combine(root,LibraryStore.CsvName),"my csv");var store=new LibraryStore(root);var doc=store.Register(Package(path),Tags(),GameScanner.Scan(root));Check(doc.CsvError!=null&&File.ReadAllText(Path.Combine(root,LibraryStore.CsvName))=="my csv","Foreign CSV overwritten");File.WriteAllText(Path.Combine(path,LibraryStore.NoteName),"my memo");Fails(()=>LibraryStore.WriteNote(path,doc.Document.Games.Single()));Check(File.ReadAllText(Path.Combine(path,LibraryStore.NoteName))=="my memo","Foreign note overwritten");return Done();
});
await Run("csv-formulas-escaped-json-lossless",root=>
{
    var path=Game(root);var p=Package(path);var store=new LibraryStore(root);
    foreach(var alias in new[]{"=1+2"," +SUM(1,2)","-1+2","@thing","\ttext"})
    {p.Alias=alias;store.Register(p,Tags(),GameScanner.Scan(root));var row=Csv(File.ReadAllText(Path.Combine(root,LibraryStore.CsvName)))[1];Check(row[2].StartsWith('\'')&&store.Load().Games.Single().Alias==alias,"Formula not neutralized or JSON changed");}
    return Done();
});
await Run("notes-readable-and-updated-without-extra-game-change",root=>
{
    var path=Game(root);var p=Package(path);var game=new LibraryStore(root).Register(p,Tags(),GameScanner.Scan(root)).Document.Games.Single();LibraryStore.WriteNote(path,game);var note=File.ReadAllText(Path.Combine(path,LibraryStore.NoteName));Check(note.Contains(p.Alias)&&note.Contains("퍼즐")&&note.Contains("Game.exe"),"Note incomplete");game.Alias="updated";LibraryStore.WriteNote(path,game);Check(File.ReadAllText(Path.Combine(path,LibraryStore.NoteName)).Contains("updated")&&File.ReadAllText(Path.Combine(path,"readme.txt"))=="game data","Note update affected game");return Done();
});
await Run("write-denial-and-single-writer-lock",root=>
{
    var path=Game(root);var store=new LibraryStore(root);store.Register(Package(path),Tags(),GameScanner.Scan(root));
    using(var gate=new FileStream(Path.Combine(root,".indierun-library.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))Fails(()=>store.Update(_=>{}));
    var denied=Game(root,"Denied");var dir=new DirectoryInfo(denied);var original=dir.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    try{var acl=dir.GetAccessControl();acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.CreateFiles,AccessControlType.Deny));dir.SetAccessControl(acl);Fails(()=>LibraryStore.EnsureIdentity(denied,Guid.NewGuid().ToString("N")));}
    finally{var acl=new DirectorySecurity();acl.SetSecurityDescriptorSddlForm(original,AccessControlSections.Access);dir.SetAccessControl(acl);}
    Check(!File.Exists(Path.Combine(denied,LibraryStore.MarkerName)),"Denied marker created");return Done();
});
await Run("identity-backup-move-and-undo-compatible",async root=>
{
    var input=Path.Combine(root,"input");Directory.CreateDirectory(input);var path=Game(input);var p=Package(path);var library=Path.Combine(root,"library");var store=new LibraryStore(library);store.Register(p,Tags(),GameScanner.Scan(input));
    var engine=new BackupEngine(Path.Combine(root,"backups")){ForceCopyMove=true};var backup=await engine.CreateAsync(p,Tags());var target=Path.Combine(library,p.Name);var moved=await engine.MoveAsync(backup.Id,target);Check(LibraryStore.ReadIdentity(target)!.GameId==p.Id,"ID lost on move");await engine.UndoMoveAsync(backup.Id,moved.Id);Check(LibraryStore.ReadIdentity(path)!.GameId==p.Id,"ID lost on undo");
});
await Run("post-backup-note-preserved-on-undo-refusal",async root=>
{
    var input=Path.Combine(root,"input");Directory.CreateDirectory(input);var path=Game(input);var p=Package(path);var store=new LibraryStore(Path.Combine(root,"library"));var game=store.Register(p,Tags(),GameScanner.Scan(input)).Document.Games.Single();var engine=new BackupEngine(Path.Combine(root,"backups"));var backup=await engine.CreateAsync(p,Tags());var target=Path.Combine(store.Root,p.Name);var move=await engine.MoveAsync(backup.Id,target);LibraryStore.WriteNote(target,game);
    bool refused=false;try{await engine.UndoMoveAsync(backup.Id,move.Id);}catch(IOException){refused=true;}Check(refused&&File.Exists(Path.Combine(target,LibraryStore.NoteName)),"New memo lost during undo");
});
await Run("scan-only-creates-no-files",root=>
{
    var path=Game(root);var before=Directory.GetFiles(root,"*",SearchOption.AllDirectories).Order().ToArray();var scan=GameScanner.Scan(root);var inv=LibraryStore.Inspect(scan);var store=new LibraryStore(root);var doc=store.Load();store.Reconcile(doc,inv,scan);Check(before.SequenceEqual(Directory.GetFiles(root,"*",SearchOption.AllDirectories).Order()),"Read-only discovery wrote files");return Done();
});
await Run("secondary-root-copy-candidate-valid-workspace",root=>
{
    var input=Path.Combine(root,"input");Directory.CreateDirectory(input);var library=Path.Combine(root,"library");Directory.CreateDirectory(library);var path=Game(library);var p=Package(path);var doc=new LibraryStore(library).Register(p,Tags(),GameScanner.Scan(library)).Document;CopyFolder(path,Path.Combine(library,"Copy"));
    var scan=new ScanSnapshot {Root=input};var part=GameScanner.Scan(library);scan.Files.AddRange(part.Files);scan.Directories.AddRange(part.Directories);scan.Directories.Add(input);var inv=LibraryStore.Inspect(scan);var candidates=WorkspaceReconciler.Discover(scan,[p]);Check(candidates.Count==1,"Copy not detected");var copy=candidates.Single();IdentityReconciler.ExpandCandidate(copy,inv,library);Check(copy.Source==Path.Combine(library,"Copy"),"Copy scope did not include marker parent");
    var cp=WorkspaceReconciler.Capture(copy,scan);WorkspaceReconciler.Check(copy,cp,scan);IdentityReconciler.Flag(copy,inv,doc);Check(copy.ReviewState=="identity","Copy could move without ID resolution");
    WorkspaceStore.Validate(new WorkspaceDocument {SearchRoot=input,Destination=library,Packages=[p,copy],Checkpoints=[cp]});return Done();
});
await Run("registered-edits-sync-to-json-and-csv",root=>
{
    var path=Game(root);var p=Package(path);var store=new LibraryStore(root);var tags=Tags();store.Register(p,tags,GameScanner.Scan(root));p.Alias="새 별칭";p.Genres=["공포"];p.Categories=[];store.SaveMetadata([p],tags,LibraryStore.Inspect(GameScanner.Scan(root)));var game=store.Load().Games.Single();Check(game.Alias==p.Alias&&game.Genres.SequenceEqual(p.Genres)&&game.Categories.Count==0,"Edited metadata not saved");Check(Csv(File.ReadAllText(Path.Combine(root,LibraryStore.CsvName)))[1][2]==p.Alias,"CSV not synchronized");return Done();
});
await Run("registered-scope-cannot-create-second-marker",root=>
{
    var path=Game(root);var p=Package(path);var store=new LibraryStore(Path.Combine(root,"library"));store.Register(p,Tags(),GameScanner.Scan(path));var parent=Package(root);parent.Id=p.Id;parent.Name="parent";Fails(()=>store.Register(parent,Tags(),GameScanner.Scan(root)));Check(!File.Exists(Path.Combine(root,LibraryStore.MarkerName)),"ID duplicated onto ancestor");return Done();
});
File.WriteAllText(Path.GetFullPath(args[1]),JsonSerializer.Serialize(new{runRoot=run,total=results.Count,passed=results.Count-failed,failed,results},new JsonSerializerOptions {WriteIndented=true}));Console.WriteLine($"RESULT: {results.Count-failed}/{results.Count} passed");Environment.ExitCode=failed==0?0:1;

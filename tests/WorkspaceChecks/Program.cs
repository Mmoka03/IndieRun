using IndieRun;
using System.Text.Json;

static void Check(bool value,string name){if(!value)throw new Exception("FAIL: "+name);Console.WriteLine("PASS: "+name);}
var root=Path.GetFullPath(Path.Combine(args[0],Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
var input=Path.Combine(root,"input");var game=Path.Combine(input,"wrapper","Game");Directory.CreateDirectory(game);
File.WriteAllBytes(Path.Combine(game,"Game.exe"),[1,2,3]);File.WriteAllText(Path.Combine(game,"readme.txt"),"guide");Directory.CreateDirectory(Path.Combine(game,"Empty"));
var scan=GameScanner.Scan(input);var package=WorkspaceReconciler.Discover(scan,[]).Single();
package.Alias="내 게임";package.Name="NewGame";package.Genres=["퍼즐"];package.GenreIds=["tag-id"];package.Planned=true;package.ComicRelated=false;
var checkpoint=WorkspaceReconciler.Capture(package,scan);
var doc=new WorkspaceDocument {SearchRoot=input,Destination=Path.Combine(root,"library"),SelectedId=package.Id,Filter="내",Packages=[package],Checkpoints=[checkpoint],Tags=[new TagDefinition{Id="tag-id",Kind="genre",Name="퍼즐"}]};
var store=new WorkspaceStore(Path.Combine(root,"data","workspace.json"));using var sessionLock=store.AcquireLock();
store.Save(doc);var loaded=store.Load().Document!;
Check(loaded.Packages.Single().Alias=="내 게임"&&loaded.Packages.Single().Name=="NewGame"&&loaded.Packages.Single().Planned&&!loaded.Packages.Single().ComicRelated&&loaded.SelectedId==package.Id&&loaded.Filter=="내","round-trip edits, relationship flags, pending plan, selection and filter");
Check(loaded.Packages.Single().GenreIds.Single()=="tag-id","stable tag binding retained independently of label");
var archived=store.Archive();Check(archived!=null&&File.Exists(archived)&&File.ReadAllText(archived).Contains("내 게임"),"explicit new-plan archive preserves prior work independently of rotating backup");
WorkspaceReconciler.Check(package,checkpoint,scan);Check(package.ReviewState=="","unchanged folder restores without reconfirmation");
File.WriteAllText(Path.Combine(game,"readme.txt"),"changed guide text");var changed=GameScanner.Scan(input);
WorkspaceReconciler.Check(package,checkpoint,changed);Check(package.ReviewState=="changed"&&package.Planned&&package.Alias=="내 게임","changed content flagged while preserving user edits and pending plan");
store.Save(doc);var persisted=store.Load().Document!;WorkspaceReconciler.Check(persisted.Packages[0],persisted.Checkpoints[0],changed);
Check(persisted.Packages[0].ReviewState=="changed","saving while unconfirmed does not silently replace baseline");
var confirmed=WorkspaceReconciler.Capture(package,changed);WorkspaceReconciler.Check(package,confirmed,changed);Check(package.ReviewState=="","explicit current-state checkpoint clears review");
var renamed=Path.Combine(input,"wrapper","Renamed");Directory.Move(game,renamed);var moved=GameScanner.Scan(input);
WorkspaceReconciler.Check(package,checkpoint,moved);Check(package.ReviewState=="missing"&&package.Planned,"manual rename preserves missing registration");
Check(WorkspaceReconciler.Discover(moved,[package]).Count==1,"unmatched folder offered separately rather than falsely merged");
package.Excluded=true;store.Save(doc);var hidden=store.Load().Document!.Packages.Single();Check(hidden.Excluded&&hidden.Alias=="내 게임","excluded candidates keep their information");
WorkspaceReconciler.Check(package,checkpoint,moved);Check(package.ReviewState=="","excluded candidates do not trigger review");
var offline=new ScanSnapshot {Root=input};package.Excluded=false;WorkspaceReconciler.Check(package,checkpoint,offline);Check(package.ReviewState=="missing"&&package.Alias=="내 게임","offline root does not erase metadata");
try{using var duplicate=store.AcquireLock();Check(false,"single writer");}catch(IOException){Check(true,"single-writer lock");}
var savedAlias=store.Load().Document!.Packages.Single().Alias;doc.Packages[0].Alias="new version";store.Save(doc);
File.WriteAllText(store.FilePath,"{broken");var recovery=store.Load();Check(recovery.Recovered&&recovery.Document!.Packages.Single().Alias==savedAlias,"corrupt primary recovers previous valid save");
store.Save(recovery.Document!);Check(!store.Load().Recovered&&Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!,"*.invalid-*.json").Length==1,"repair preserves corrupt original and writes valid primary");
File.WriteAllText(store.FilePath+".tmp","{unfinished");Check(store.Load().Document!=null&&!store.Load().Recovered,"interrupted temporary write does not replace valid plan");
var invalid=JsonSerializer.Deserialize<WorkspaceDocument>(JsonSerializer.Serialize(doc))!;invalid.Version=99;
try{store.Save(invalid);Check(false,"unsupported version");}catch(InvalidDataException){Check(true,"unsupported version rejected before overwrite");}
invalid=JsonSerializer.Deserialize<WorkspaceDocument>(JsonSerializer.Serialize(doc))!;invalid.Checkpoints[0].Files.Add(new FileStamp("../escape",0,0));
try{store.Save(invalid);Check(false,"relative path traversal");}catch(InvalidDataException){Check(true,"invalid checkpoint path rejected before overwrite");}
Console.WriteLine("Workspace checks complete.");

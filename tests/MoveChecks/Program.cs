using IndieRun;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

static void Check(bool value,string message){if(!value)throw new Exception(message);}
static Package Package(string source)=>new(){IsReal=true,Id=Guid.NewGuid().ToString("N"),Source=source,DetectedRoot=source,SearchRoot=Path.GetDirectoryName(source)!,Name="RenamedGame",Folder=Path.GetFileName(source),Alias="이동 테스트"};
static string Fixture(string root)
{
    var source=Path.Combine(root,"source");Directory.CreateDirectory(Path.Combine(source,"日本語","Empty"));
    File.WriteAllBytes(Path.Combine(source,"000-data.bin"),RandomNumberGenerator.GetBytes(3*1024*1024));
    File.WriteAllBytes(Path.Combine(source,"Game.exe"),[1,2,3]);File.WriteAllText(Path.Combine(source,"guide.txt"),"안내 日本語");
    var file=Path.Combine(source,"日本語","readonly.dll");File.WriteAllBytes(file,[5,6,7]);File.SetAttributes(file,File.GetAttributes(file)|FileAttributes.ReadOnly);return source;
}
static string Destination(string root)=>Path.Combine(root,"library","RenamedGame");
static BackupEngine Engine(string root,bool copy=false)=>new(Path.Combine(root,"backups")){ForceCopyMove=copy};
static async Task Fails(Func<Task> action)
{try{await action();}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException){return;}throw new Exception("Expected failure");}
static void Matches(BackupJob backup,string path)
{
    Check(Directory.Exists(path),"Missing tree: "+path);var scan=GameScanner.Scan(path);Check(scan.Issues.Count==0,"Unreadable tree");
    Check(scan.Files.Count==backup.Files.Count,"File set mismatch");
    foreach(var f in backup.Files)
    {
        var p=Path.Combine(path,f.RelativePath);Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))==f.Sha256,"Hash mismatch");
        Check(File.GetLastWriteTimeUtc(p).Ticks==f.ModifiedTicks,"Timestamp mismatch");
        Check((File.GetAttributes(p)&FileAttributes.ReadOnly)==(f.Attributes&FileAttributes.ReadOnly),"Readonly mismatch");
    }
    Check(scan.Directories.Where(d=>d!=path).Select(d=>Path.GetRelativePath(path,d)).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(backup.Directories),"Directory mismatch");
}
if(args[0]=="--child")
{
    var root=args[1];var phase=args[2];var engine=Engine(root,args[4]=="copy");
    engine.Checkpoint=point=>{if(point==phase){Console.WriteLine("READY");Console.Out.Flush();Thread.Sleep(Timeout.Infinite);}};
    if(args.Length>5)await engine.UndoMoveAsync(args[3],args[5]);
    else await engine.MoveAsync(args[3],Destination(root));return;
}
var runRoot=Path.Combine(Path.GetFullPath(args[0]),"run-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(runRoot);
var results=new List<object>();int failed=0;
async Task Run(string name,Func<string,Task<object>> action)
{
    var root=Path.Combine(runRoot,name);Directory.CreateDirectory(root);
    try{var detail=await action(root);results.Add(new{name,status="PASS",detail});Console.WriteLine("PASS: "+name);}
    catch(Exception ex){failed++;results.Add(new{name,status="FAIL",detail=new{type=ex.GetType().Name,message=ex.Message,trace=ex.StackTrace}});Console.WriteLine("FAIL: "+name+" · "+ex.Message);}
}
async Task Kill(string root,string phase,string backupId,bool copy,string? moveId=null)
{
    var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
    foreach(var item in new[]{"--child",root,phase,backupId,copy?"copy":"rename"})start.ArgumentList.Add(item);if(moveId!=null)start.ArgumentList.Add(moveId);
    using var child=Process.Start(start)!;
    try
    {
        var signal=await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        if(signal!="READY")throw new Exception("Checkpoint not reached: "+await child.StandardError.ReadToEndAsync());
        child.Kill(true);await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
    finally{if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}}
}
foreach(var copy in new[]{false,true})await Run(copy?"copy-move-and-undo":"same-volume-move-and-undo",async root=>
{
    var source=Fixture(root);var engine=Engine(root,copy);var backup=await engine.CreateAsync(Package(source),[]);
    var move=await engine.MoveAsync(backup.Id,Destination(root));Check(move.Status=="Completed"&&!Directory.Exists(source),"Not moved");Matches(backup,Destination(root));Matches(backup,engine.Payload(backup));
    var undo=await engine.UndoMoveAsync(backup.Id,move.Id);Check(undo.Status=="RolledBack"&&!Directory.Exists(Destination(root)),"Not undone");Matches(backup,source);
    return new{copy,sourceRemoved=true,backupRetained=true,undo=true};
});
await Run("name-and-overlap-preflight",async root=>
{
    var source=Fixture(root);var engine=Engine(root);
    foreach(var name in new[]{"CON","nul.txt","COM1","bad.","a/b","a:b",".."})Check(BackupEngine.MoveNameError(name)!=null,"Invalid name allowed: "+name);
    Check(BackupEngine.MoveNameError("Game (2)")==null,"ASCII suffix rejected");
    await Fails(()=>Task.Run(()=>engine.Preflight([(source,Path.Combine(source,"nested"))])));
    await Fails(()=>Task.Run(()=>engine.Preflight([(source,Destination(root)),(source,Path.Combine(root,"other"))])));
    Directory.CreateDirectory(Destination(root));File.WriteAllText(Path.Combine(Destination(root),"keep.txt"),"user");
    await Fails(()=>Task.Run(()=>engine.Preflight([(source,Destination(root))])));Check(File.ReadAllText(Path.Combine(Destination(root),"keep.txt"))=="user","Collision changed");return new{blocked=true};
});
await Run("locked-source",async root=>
{
    var source=Fixture(root);var engine=Engine(root);var backup=await engine.CreateAsync(Package(source),[]);
    using(var locked=new FileStream(Path.Combine(source,"000-data.bin"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))await Fails(async()=>await engine.MoveAsync(backup.Id,Destination(root)));
    Matches(backup,source);Check(!Directory.Exists(Destination(root)),"Unexpected destination");return new{realLock=true};
});
await Run("denied-destination",async root=>
{
    var source=Fixture(root);var engine=Engine(root);var backup=await engine.CreateAsync(Package(source),[]);var library=Path.GetDirectoryName(Destination(root))!;Directory.CreateDirectory(library);
    var directory=new DirectoryInfo(library);var original=directory.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    try
    {
        var acl=directory.GetAccessControl();acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.CreateDirectories,AccessControlType.Deny));directory.SetAccessControl(acl);
        await Fails(async()=>await engine.MoveAsync(backup.Id,Destination(root)));
    }
    finally{var acl=new DirectorySecurity();acl.SetSecurityDescriptorSddlForm(original,AccessControlSections.Access);directory.SetAccessControl(acl);}
    Matches(backup,source);Check(!Directory.Exists(Destination(root)),"Unexpected destination");return new{realDeniedPermission=true,aclRestored=true};
});
await Run("capacity-and-cancellation",async root=>
{
    var source=Fixture(root);var engine=Engine(root,true);var backup=await engine.CreateAsync(Package(source),[]);engine.AvailableBytesOverride=()=>0;
    await Fails(async()=>await engine.MoveAsync(backup.Id,Destination(root)));Matches(backup,source);engine.AvailableBytesOverride=null;
    using var stop=new CancellationTokenSource();engine.Checkpoint=p=>{if(p=="Moving-chunk")stop.Cancel();};await Fails(async()=>await engine.MoveAsync(backup.Id,Destination(root),cancellation:stop.Token));
    Matches(backup,source);Check(!Directory.Exists(Destination(root)),"Cancellation published partial tree");return new{spaceInjected=true,cancelRollback=true};
});
await Run("changed-source-after-backup",async root=>
{
    var source=Fixture(root);var engine=Engine(root);var backup=await engine.CreateAsync(Package(source),[]);File.WriteAllText(Path.Combine(source,"guide.txt"),"new user content");
    await Fails(async()=>await engine.MoveAsync(backup.Id,Destination(root)));Check(File.ReadAllText(Path.Combine(source,"guide.txt"))=="new user content","User changes lost");Check(!Directory.Exists(Destination(root)),"Stale backup moved");return new{preserved=true};
});
await Run("corrupt-backup-blocks-move",async root=>
{
    var source=Fixture(root);var engine=Engine(root);var backup=await engine.CreateAsync(Package(source),[]);File.WriteAllText(Path.Combine(engine.Payload(backup),"guide.txt"),"corrupt");
    await Fails(async()=>await engine.MoveAsync(backup.Id,Destination(root)));Matches(backup,source);return new{blocked=true};
});
await Run("partial-source-cleanup-rolls-back",async root=>
{
    var source=Fixture(root);var engine=Engine(root,true);var backup=await engine.CreateAsync(Package(source),[]);bool injected=false;
    engine.Checkpoint=p=>{if(p=="move-remove-file"&&!injected){injected=true;throw new IOException("Injected after first real source deletion");}};
    await Fails(async()=>await engine.MoveAsync(backup.Id,Destination(root)));Check(injected,"No deletion exercised");Matches(backup,source);Check(!Directory.Exists(Destination(root)),"Rollback left destination");Check(engine.ListMoves().Single().Status=="RolledBack","Rollback status");return new{partialDeleteRecovered=true};
});
await Run("destination-edits-block-undo",async root=>
{
    var source=Fixture(root);var engine=Engine(root,true);var backup=await engine.CreateAsync(Package(source),[]);var move=await engine.MoveAsync(backup.Id,Destination(root));
    File.WriteAllText(Path.Combine(move.Destination,"guide.txt"),"user changed game");await Fails(async()=>await engine.UndoMoveAsync(backup.Id,move.Id));
    Check(File.ReadAllText(Path.Combine(move.Destination,"guide.txt"))=="user changed game","Destination overwritten");Check(engine.ListMoves().Single().Status=="RecoveryRequired","No recovery warning");Matches(backup,engine.Payload(backup));return new{userEditsPreserved=true};
});
foreach(var phase in new[]{"Moving-chunk","Moving-file","move-before-capture","move-captured","move-published","move-remove-file"})await Run("crash-copy-"+phase,async root=>
{
    var source=Fixture(root);var engine=Engine(root,true);var backup=await engine.CreateAsync(Package(source),[]);await Kill(root,phase,backup.Id,true);
    var move=engine.ListMoves().Single();Check(move.Status=="Interrupted","Crash not detected");await engine.ResumeMoveAsync(backup.Id,move.Id);Matches(backup,Destination(root));Check(!Directory.Exists(source),"Source remained");return new{processKilled=true,resumed=true};
});
await Run("crash-same-volume-publish",async root=>
{
    var source=Fixture(root);var engine=Engine(root);var backup=await engine.CreateAsync(Package(source),[]);await Kill(root,"move-published",backup.Id,false);
    var move=engine.ListMoves().Single();await engine.ResumeMoveAsync(backup.Id,move.Id);Matches(backup,Destination(root));Check(!Directory.Exists(source),"Source remained");return new{processKilled=true,resumed=true};
});
foreach(var phase in new[]{"undo-prepared","move-remove-file","undo-source-restored"})await Run("crash-undo-"+phase,async root=>
{
    var source=Fixture(root);var engine=Engine(root,true);var backup=await engine.CreateAsync(Package(source),[]);var move=await engine.MoveAsync(backup.Id,Destination(root));
    await Kill(root,phase,backup.Id,true,move.Id);var interrupted=engine.ListMoves().Single();Check(interrupted.Status=="Interrupted","Undo interruption missing");
    await engine.ResumeMoveAsync(backup.Id,move.Id);Matches(backup,source);Check(!Directory.Exists(Destination(root)),"Undo left destination");return new{processKilled=true,resumed=true};
});
await Run("original-location-conflict",async root=>
{
    var source=Fixture(root);var engine=Engine(root,true);var backup=await engine.CreateAsync(Package(source),[]);var move=await engine.MoveAsync(backup.Id,Destination(root));Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"keep.txt"),"other game");
    await Fails(async()=>await engine.UndoMoveAsync(backup.Id,move.Id));Check(File.ReadAllText(Path.Combine(source,"keep.txt"))=="other game","Original overwritten");Matches(backup,Destination(root));return new{conflictPreserved=true};
});
await Run("completed-batch-can-undo-in-reverse",async root=>
{
    var engine=Engine(root);var completed=new List<(BackupJob Backup,MoveJob Move)>();
    foreach(var name in new[]{"first","second"}){var source=Fixture(Path.Combine(root,name));var backup=await engine.CreateAsync(Package(source),[]);var move=await engine.MoveAsync(backup.Id,Path.Combine(root,"library",name),"batch");completed.Add((backup,move));}
    foreach(var item in completed.AsEnumerable().Reverse()){await engine.UndoMoveAsync(item.Backup.Id,item.Move.Id);Matches(item.Backup,item.Move.Source);}
    return new{completed=2,undone=2};
});
await Run("locked-rollback-can-resume",async root=>
{
    var source=Fixture(root);var engine=Engine(root,true);var backup=await engine.CreateAsync(Package(source),[]);FileStream? locked=null;
    engine.Checkpoint=p=>
    {
        if(p!="move-remove-file"||locked!=null)return;
        var quarantine=Directory.GetDirectories(root,".indierun-original-*").Single();
        locked=new FileStream(Path.Combine(quarantine,"guide.txt"),FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        throw new IOException("Injected lock while restoring the package");
    };
    try{await Fails(async()=>await engine.MoveAsync(backup.Id,Destination(root)));Check(engine.ListMoves().Single().Status=="RecoveryRequired","Recovery need not recorded");}
    finally{locked?.Dispose();}
    engine.Checkpoint=null;var move=engine.ListMoves().Single();await engine.ResumeMoveAsync(backup.Id,move.Id);Matches(backup,source);Check(!Directory.Exists(Destination(root)),"Recovery left destination");return new{realLockedRollback=true,recoveredAfterUnlock=true};
});
await Run("interrupted-move-can-undo",async root=>
{
    var source=Fixture(root);var engine=Engine(root,true);var backup=await engine.CreateAsync(Package(source),[]);await Kill(root,"move-captured",backup.Id,true);
    var move=engine.ListMoves().Single();await engine.UndoMoveAsync(backup.Id,move.Id);Matches(backup,source);Check(!Directory.Exists(Destination(root)),"Undo published destination");return new{userCanChooseRollback=true};
});
await Run("corrupt-move-primary-recovers",async root=>
{
    var source=Fixture(root);var engine=Engine(root);var backup=await engine.CreateAsync(Package(source),[]);var move=await engine.MoveAsync(backup.Id,Destination(root));
    var record=Path.Combine(engine.Root,backup.Id,"moves",move.Id+".json");File.WriteAllText(record,"{broken");
    var recovered=engine.ListMoves().Single();Check(recovered.Status=="Interrupted","Previous valid intent not recovered");await engine.ResumeMoveAsync(backup.Id,move.Id);Matches(backup,Destination(root));
    Check(Directory.GetFiles(Path.GetDirectoryName(record)!,"*.invalid-*").Length>0,"Corrupt record not preserved");return new{previousRecordRecovered=true};
});
if(args.Length>2)await Run("actual-cross-volume-move-and-undo",async root=>
{
    var source=Fixture(root);var engine=Engine(root);var backup=await engine.CreateAsync(Package(source),[]);var destination=Path.Combine(Path.GetFullPath(args[2]),Guid.NewGuid().ToString("N"),"Game");
    Check(!Path.GetPathRoot(source)!.Equals(Path.GetPathRoot(destination),StringComparison.OrdinalIgnoreCase),"Different drive required");
    var move=await engine.MoveAsync(backup.Id,destination);Check(move.CrossVolume,"Cross volume not used");Matches(backup,destination);Check(!Directory.Exists(source),"Source remained");
    await engine.UndoMoveAsync(backup.Id,move.Id);Matches(backup,source);Check(!Directory.Exists(destination),"Cross drive undo failed");return new{realDifferentDrives=true,undo=true};
});
File.WriteAllText(Path.GetFullPath(args[1]),JsonSerializer.Serialize(new{runRoot,total=results.Count,passed=results.Count-failed,failed,results},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"RESULT: {results.Count-failed}/{results.Count} passed");Environment.ExitCode=failed==0?0:1;

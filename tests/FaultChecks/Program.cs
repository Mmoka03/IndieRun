using IndieRun;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

static WorkspaceDocument Document(string root,string alias,int entries=1)
{
    var source=Path.Combine(root,"source");var p=new Package {IsReal=true,Id="fault-package",SearchRoot=root,Source=source,DetectedRoot=source,Folder="source",Name="Game",Alias=alias,Planned=true};
    return new WorkspaceDocument {SearchRoot=root,Destination=Path.Combine(root,"destination"),Packages=[p],Checkpoints=[new FolderCheckpoint {PackageId=p.Id,Source=source,Files=Enumerable.Range(0,entries).Select(i=>new FileStamp($"data/file-{i:D7}.bin",100+i,638000000000000000+i)).ToList()}]};
}
static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
static void SaveWithCheckpoint(WorkspaceStore store,WorkspaceDocument document,Action<string> checkpoint)
{
    try{typeof(WorkspaceStore).GetMethod("Save",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(store,[document,checkpoint]);}
    catch(TargetInvocationException ex)when(ex.InnerException!=null){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();}
}
static void Require(bool value,string message){if(!value)throw new Exception(message);}
static object Error(Exception ex)=>new {type=ex.GetType().Name,hresult=$"0x{ex.HResult:X8}",message=ex.Message};
static void RestoreAccess(DirectoryInfo directory,DirectorySecurity original)
{
    // SetAccessControl persists modified sections only. Rehydrate the access SDDL
    // so restoring an otherwise unmodified original descriptor actually writes it.
    var restored=new DirectorySecurity();
    restored.SetSecurityDescriptorSddlForm(original.GetSecurityDescriptorSddlForm(AccessControlSections.Access),AccessControlSections.Access);
    directory.SetAccessControl(restored);
}

if(args.Length<3){Console.Error.WriteLine("Usage: FaultChecks <test-run-directory> <source-dll-path> <report-json-path>");Environment.ExitCode=2;return;}

if(args[0]=="--child")
{
    var root=args[1];var stage=args[2];var store=new WorkspaceStore(Path.Combine(root,"workspace.json"));
    var doc=Document(root,"new-state",stage=="writing"?160000:1);
    SaveWithCheckpoint(store,doc,checkpoint=>
    {
        if(stage=="writing"&&checkpoint=="temporary-opened")
        {Console.WriteLine("READY");Console.Out.Flush();Console.ReadLine();}
        else if(checkpoint==(stage=="writing"?"temporary-flushed":stage))
        {Console.WriteLine("READY");Console.Out.Flush();Thread.Sleep(Timeout.Infinite);}
    });
    return;
}

var runRoot=Path.Combine(Path.GetFullPath(args[0]),"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(runRoot);
var sourceDll=Path.GetFullPath(args[1]);var reportPath=Path.GetFullPath(args[2]);
var originalDllHash=Hash(sourceDll);
var results=new List<object>();var failed=0;var skipped=0;
void Run(string name,Func<string,object> body)
{
    var root=Path.Combine(runRoot,name);Directory.CreateDirectory(root);
    try{var details=body(root);results.Add(new {name,status="PASS",details});Console.WriteLine("PASS: "+name);}
    catch(SkipException ex){skipped++;results.Add(new {name,status="SKIP",details=Error(ex)});Console.WriteLine("SKIP: "+name+" · "+ex.Message);}
    catch(Exception ex){failed++;results.Add(new {name,status="FAIL",details=Error(ex)});Console.WriteLine("FAIL: "+name+" · "+ex.Message);}
}
WorkspaceStore ExistingStore(string root)
{
    var store=new WorkspaceStore(Path.Combine(root,"workspace.json"));store.Save(Document(root,"backup-state"));store.Save(Document(root,"old-state"));return store;
}
async Task<(int ExitCode,long TempBytes)> KillAt(string root,string phase)
{
    var start=new ProcessStartInfo(Environment.ProcessPath!) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true};
    start.ArgumentList.Add("--child");start.ArgumentList.Add(root);start.ArgumentList.Add(phase);
    using var process=Process.Start(start)??throw new Exception("Child did not start");
    try
    {
        var line=await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(25));Require(line=="READY","No checkpoint handshake: "+line);
        if(phase=="writing")
        {
            await process.StandardInput.WriteLineAsync("GO");await process.StandardInput.FlushAsync();
            var watch=Stopwatch.StartNew();while(watch.Elapsed<TimeSpan.FromSeconds(10))
            {if(new FileInfo(Path.Combine(root,"workspace.json.tmp")).Length>4096)break;await Task.Delay(1);}
        }
        process.Kill(true);await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var temp=Path.Combine(root,"workspace.json.tmp");return (process.ExitCode,File.Exists(temp)?new FileInfo(temp).Length:0);
    }
    finally{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}}
}

Run("crash-during-json-write",root=>
{
    var store=ExistingStore(root);var before=Hash(store.FilePath);var killed=KillAt(root,"writing").GetAwaiter().GetResult();
    Require(before==Hash(store.FilePath),"Committed plan changed during interrupted write");Require(store.Load().Document!.Packages[0].Alias=="old-state","Old state not restored");
    var incomplete=false;try{using var json=JsonDocument.Parse(File.ReadAllText(store.FilePath+".tmp"));}catch(JsonException){incomplete=true;}
    Require(incomplete,"Did not catch a partial JSON write; repeat with a larger fixture");
    store.Save(Document(root,"retry-state"));Require(store.Load().Document!.Packages[0].Alias=="retry-state","Retry failed");
    return new {killed.ExitCode,killed.TempBytes,partialJson=incomplete,primaryUnchanged=true,retrySucceeded=true};
});
Run("crash-after-flush-before-replace",root=>
{
    var store=ExistingStore(root);var before=Hash(store.FilePath);var killed=KillAt(root,"temporary-flushed").GetAwaiter().GetResult();
    Require(Hash(store.FilePath)==before&&store.Load().Document!.Packages[0].Alias=="old-state","Old state lost before commit");
    return new {killed.ExitCode,killed.TempBytes,primaryUnchanged=true};
});
Run("crash-immediately-after-replace",root=>
{
    var store=ExistingStore(root);var killed=KillAt(root,"committed").GetAwaiter().GetResult();
    Require(store.Load().Document!.Packages[0].Alias=="new-state","New committed state not readable");
    var backup=JsonSerializer.Deserialize<WorkspaceDocument>(File.ReadAllText(store.FilePath+".bak"))!;Require(backup.Packages[0].Alias=="old-state","Previous state not retained");
    return new {killed.ExitCode,newStateReadable=true,previousBackupReadable=true};
});
Run("crash-on-first-save",root=>
{
    var store=new WorkspaceStore(Path.Combine(root,"workspace.json"));var killed=KillAt(root,"temporary-flushed").GetAwaiter().GetResult();
    Require(!File.Exists(store.FilePath)&&store.Load().Document==null,"Uncommitted first write treated as saved");
    store.Save(Document(root,"first-retry"));Require(store.Load().Document!.Packages[0].Alias=="first-retry","First-save retry failed");
    return new {killed.ExitCode,noFalseSavedState=true,retrySucceeded=true};
});
Run("locked-plan-file",root=>
{
    var store=ExistingStore(root);var before=Hash(store.FilePath);Exception? denial=null;
    using(var handle=new FileStream(store.FilePath,FileMode.Open,FileAccess.Read,FileShare.Read))
    {try{store.Save(Document(root,"blocked"));}catch(IOException ex){denial=ex;}}
    Require(denial!=null,"Lock did not refuse replacement");Require(Hash(store.FilePath)==before,"Locked plan was damaged");
    store.Save(Document(root,"retry"));Require(store.Load().Document!.Packages[0].Alias=="retry","Retry after unlocking failed");
    return new {error=Error(denial!),primaryUnchanged=true,retrySucceeded=true};
});
Run("readonly-plan-file",root=>
{
    var store=ExistingStore(root);var before=Hash(store.FilePath);var attributes=File.GetAttributes(store.FilePath);Exception? denial=null;
    try
    {File.SetAttributes(store.FilePath,attributes|FileAttributes.ReadOnly);try{store.Save(Document(root,"blocked"));}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){denial=ex;}}
    finally{File.SetAttributes(store.FilePath,attributes);}
    Require(denial!=null,"Read-only plan replacement not refused");Require(Hash(store.FilePath)==before,"Read-only plan was damaged");store.Save(Document(root,"retry"));
    Require(store.Load().Document!.Packages[0].Alias=="retry","Read-only retry failed");
    return new {error=Error(denial!),primaryUnchanged=true,retrySucceeded=true};
});
Run("access-denied-save-directory",root=>
{
    var store=ExistingStore(root);var before=Hash(store.FilePath);var directory=new DirectoryInfo(root);DirectorySecurity original;
    try{original=directory.GetAccessControl();}catch(UnauthorizedAccessException ex){throw new SkipException("ACL setup unavailable: "+ex.Message);}
    var restricted=directory.GetAccessControl();restricted.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.CreateFiles|FileSystemRights.CreateDirectories,AccessControlType.Deny));
    Exception? denial=null;
    try
    {directory.SetAccessControl(restricted);try{store.Save(Document(root,"blocked"));}catch(UnauthorizedAccessException ex){denial=ex;}}
    finally{RestoreAccess(directory,original);}
    Require(denial!=null,"ACL did not deny temporary file creation");Require(Hash(store.FilePath)==before,"Access-denied save damaged primary");store.Save(Document(root,"retry"));
    return new {error=Error(denial!),primaryUnchanged=true,aclRestored=true,retrySucceeded=store.Load().Document!.Packages[0].Alias=="retry"};
});
Run("injected-disk-full",root=>
{
    var store=ExistingStore(root);var before=Hash(store.FilePath);Exception? denial=null;
    try{SaveWithCheckpoint(store,Document(root,"blocked"),stage=>{if(stage=="temporary-flushed")throw new IOException("Injected disk full",unchecked((int)0x80070070));});}catch(IOException ex){denial=ex;}
    Require(denial!=null&&Hash(store.FilePath)==before,"Injected failure changed primary");
    return new {simulation=true,error=Error(denial!),primaryUnchanged=true,realDiskWasNotFilled=true};
});
Run("corrupted-primary-and-backup",root=>
{
    var store=ExistingStore(root);File.WriteAllText(store.FilePath,"{truncated");var recovery=store.Load();Require(recovery.Recovered&&recovery.Document!.Packages[0].Alias=="backup-state","Backup recovery failed");
    File.WriteAllText(store.FilePath+".bak","{also-truncated");var failedLoad=store.Load();Require(failedLoad.Document==null&&failedLoad.Error!=null,"Double corruption silently treated as valid");
    return new {singleCorruptionRecovered=true,doubleCorruptionDetected=true};
});
Run("os-file-move-locked-real-dll",root=>
{
    var source=Path.Combine(root,"UnityPlayer.dll");var target=Path.Combine(root,"Moved.dll");File.Copy(sourceDll,source);Exception? denial=null;
    using(var handle=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.None))
    {try{File.Move(source,target);}catch(IOException ex){denial=ex;}}
    Require(denial!=null&&File.Exists(source)&&!File.Exists(target),"Locked file not preserved at source");Require(Hash(source)==originalDllHash,"Locked file damaged");File.Move(source,target);Require(Hash(target)==originalDllHash,"Move after unlock changed bytes");
    return new {scope="OS primitive; not app move engine",error=Error(denial!),sourcePreserved=true,unlockThenMoveSucceeded=true};
});
Run("os-directory-move-with-locked-child",root=>
{
    var source=Path.Combine(root,"source");var target=Path.Combine(root,"destination");Directory.CreateDirectory(source);var dll=Path.Combine(source,"UnityPlayer.dll");File.Copy(sourceDll,dll);Exception? denial=null;
    using(var handle=new FileStream(dll,FileMode.Open,FileAccess.Read,FileShare.None))
    {try{Directory.Move(source,target);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){denial=ex;}}
    var actual=Directory.Exists(target)?target:source;Require(Hash(Path.Combine(actual,"UnityPlayer.dll"))==originalDllHash,"Directory rename changed payload");
    if(!Directory.Exists(target))Directory.Move(source,target);
    Require(Hash(Path.Combine(target,"UnityPlayer.dll"))==originalDllHash,"Directory move after unlock failed");
    return new {scope="OS primitive; not app move engine",outcome=denial==null?"directory rename succeeded despite locked child":"directory rename refused",error=denial==null?null:Error(denial),payloadIntact=true,unlockThenMoveSucceeded=true};
});
Run("os-file-move-access-denied",root=>
{
    var target=Path.Combine(root,"destination");Directory.CreateDirectory(target);var source=Path.Combine(root,"UnityPlayer.dll");File.Copy(sourceDll,source);
    var directory=new DirectoryInfo(target);var original=directory.GetAccessControl();var restricted=directory.GetAccessControl();restricted.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.CreateFiles|FileSystemRights.CreateDirectories,AccessControlType.Deny));Exception? denial=null;
    try{directory.SetAccessControl(restricted);try{File.Move(source,Path.Combine(target,"UnityPlayer.dll"));}catch(UnauthorizedAccessException ex){denial=ex;}}
    finally{RestoreAccess(directory,original);}
    Require(denial!=null&&File.Exists(source)&&Hash(source)==originalDllHash,"Access-denied move did not preserve source");
    File.Move(source,Path.Combine(target,"UnityPlayer.dll"));Require(Hash(Path.Combine(target,"UnityPlayer.dll"))==originalDllHash,"Move after ACL restoration failed");
    return new {scope="OS primitive; not app move engine",error=Error(denial!),sourcePreserved=true,aclRestored=true,retrySucceeded=true};
});
Run("os-partial-move-demonstration",root=>
{
    var source=Path.Combine(root,"source");var target=Path.Combine(root,"destination");Directory.CreateDirectory(source);Directory.CreateDirectory(target);
    File.WriteAllText(Path.Combine(source,"a-readme.txt"),"fixture guide");var dll=Path.Combine(source,"z-UnityPlayer.dll");File.Copy(sourceDll,dll);Exception? denial=null;
    using(var handle=new FileStream(dll,FileMode.Open,FileAccess.Read,FileShare.None))
    {try{foreach(var file in Directory.GetFiles(source).OrderBy(p=>p,StringComparer.Ordinal))File.Move(file,Path.Combine(target,Path.GetFileName(file)));}catch(IOException ex){denial=ex;}}
    var partial=File.Exists(Path.Combine(target,"a-readme.txt"))&&File.Exists(dll);Require(denial!=null&&partial,"Did not reproduce partial per-file move");
    File.Move(Path.Combine(target,"a-readme.txt"),Path.Combine(source,"a-readme.txt"));Require(Hash(dll)==originalDllHash&&Directory.GetFiles(target).Length==0,"Manual rollback fixture failed");
    return new {scope="deliberately naive test loop; NOT product code",error=Error(denial!),partialMoveReproduced=true,testFixtureRolledBack=true};
});

Require(Hash(sourceDll)==originalDllHash,"Original scan-input DLL changed");
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
var report=new {runRoot,total=results.Count,passed=results.Count-failed-skipped,failed,skipped,sourceGameFileUnchanged=true,results};
File.WriteAllText(reportPath,JsonSerializer.Serialize(report,new JsonSerializerOptions {WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
Console.WriteLine($"RESULT: total={results.Count} passed={results.Count-failed-skipped} failed={failed} skipped={skipped}");
Environment.ExitCode=failed==0?0:1;

sealed class SkipException(string message):Exception(message);

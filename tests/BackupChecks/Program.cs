using IndieRun;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

static void Require(bool value,string message){if(!value)throw new Exception(message);}
static Package Package(string source)=>new(){IsReal=true,Id="test-package",Source=source,DetectedRoot=source,SearchRoot=Path.GetDirectoryName(source)!,Name=Path.GetFileName(source),Folder=Path.GetFileName(source),Alias="백업 검수 게임",Genres=["퍼즐"],Categories=["테스트"]};
static string MakeSource(string root)
{
    var source=Path.Combine(root,"source");Directory.CreateDirectory(Path.Combine(source,"日本語","Empty"));
    File.WriteAllBytes(Path.Combine(source,"000-data.bin"),RandomNumberGenerator.GetBytes(4*1024*1024));
    File.WriteAllBytes(Path.Combine(source,"Game.exe"),[1,2,3]);File.WriteAllText(Path.Combine(source,"readme.txt"),"Test guide / 日本語 / 한국어");
    var ro=Path.Combine(source,"日本語","readonly.dll");File.WriteAllBytes(ro,[5,6,7,8]);File.SetAttributes(ro,File.GetAttributes(ro)|FileAttributes.ReadOnly);return source;
}
static Dictionary<string,string> HashTree(string path)=>Directory.GetFiles(path,"*",SearchOption.AllDirectories).ToDictionary(p=>Path.GetRelativePath(path,p),p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))),StringComparer.OrdinalIgnoreCase);
static void SameTree(string source,string target)
{
    var a=HashTree(source);var b=HashTree(target);Require(a.Count==b.Count&&a.All(p=>b.TryGetValue(p.Key,out var hash)&&hash==p.Value),"File content/tree mismatch");
    var dirs=Directory.GetDirectories(source,"*",SearchOption.AllDirectories).Select(p=>Path.GetRelativePath(source,p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Require(dirs.SetEquals(Directory.GetDirectories(target,"*",SearchOption.AllDirectories).Select(p=>Path.GetRelativePath(target,p))),"Empty directory/tree mismatch");
}
static void RestoreAccess(string path,string sddl){var acl=new DirectorySecurity();acl.SetSecurityDescriptorSddlForm(sddl,AccessControlSections.Access);new DirectoryInfo(path).SetAccessControl(acl);}
static async Task ExpectFailure(Func<Task> action,string label)
{try{await action();}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException){return;}throw new Exception("Expected failure: "+label);}

if(args.Length<2){Console.Error.WriteLine("Usage: BackupChecks <run-base> <report-json> [actual-game-directory]");return;}
if(args[0]=="--child")
{
    var root=args[1];var phase=args[2];var engine=new BackupEngine(Path.Combine(root,"backups"));
    engine.Checkpoint=stage=>
    {
        if(stage!=phase)return;
        if(stage.EndsWith("-chunk"))
        {
            var parts=Directory.GetFiles(root,"*.part",SearchOption.AllDirectories);if(!parts.Any(p=>new FileInfo(p).Length>=1024*1024))return;
        }
        Console.WriteLine("READY");Console.Out.Flush();Thread.Sleep(Timeout.Infinite);
    };
    if(phase.StartsWith("restore"))await engine.RestoreAsync(args[3],Path.Combine(root,"output"));
    else await engine.CreateAsync(Package(Path.Combine(root,"source")),[]);
    return;
}
var runRoot=Path.Combine(Path.GetFullPath(args[0]),"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(runRoot);
var results=new List<object>();var failed=0;
async Task Run(string name,Func<string,Task<object>> action)
{
    var root=Path.Combine(runRoot,name);Directory.CreateDirectory(root);
    try{var detail=await action(root);results.Add(new{name,status="PASS",detail});Console.WriteLine("PASS: "+name);}
    catch(Exception ex){failed++;results.Add(new{name,status="FAIL",detail=new {type=ex.GetType().Name,hresult=$"0x{ex.HResult:X8}",message=ex.Message,trace=ex.StackTrace}});Console.WriteLine("FAIL: "+name+" · "+ex.Message);}
}
async Task<int> KillAt(string root,string phase,string? id=null)
{
    var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
    foreach(var arg in new[]{"--child",root,phase})start.ArgumentList.Add(arg);if(id!=null)start.ArgumentList.Add(id);
    using var child=Process.Start(start)!;
    try
    {
        var line=await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        if(line!="READY")throw new Exception("Checkpoint not reached: "+await child.StandardError.ReadToEndAsync());
        child.Kill(true);await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));return child.ExitCode;
    }
    finally{if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}}
}

await Run("backup-and-restore",async root=>
{
    var source=MakeSource(root);var before=GameScanner.Scan(source).Fingerprint();var engine=new BackupEngine(Path.Combine(root,"backups"));
    var job=await engine.CreateAsync(Package(source),[new TagDefinition{Kind="category",Name="테스트"}]);Require(job.Status=="Completed"&&job.SourceVerified&&job.Files.All(f=>f.Sha256?.Length==64),"Backup falsely incomplete");SameTree(source,engine.Payload(job));
    var restore=await engine.RestoreAsync(job.Id,Path.Combine(root,"output"));Require(restore.Status=="Completed","Restore incomplete");SameTree(source,engine.RestoredPayload(job,restore));
    Require(File.Exists(Path.Combine(restore.TargetContainer,"game-info.json")),"Metadata not restored separately");
    foreach(var file in job.Files)Require(File.GetLastWriteTimeUtc(Path.Combine(engine.RestoredPayload(job,restore),file.RelativePath)).Ticks==file.ModifiedTicks,"File timestamp not restored");
    Require((File.GetAttributes(Path.Combine(engine.RestoredPayload(job,restore),"日本語","readonly.dll"))&FileAttributes.ReadOnly)!=0,"Read-only attribute lost");
    Require(before==GameScanner.Scan(source).Fingerprint(),"Source metadata changed");return new {job.Id,job.TotalBytes,files=job.Files.Count,unicode=true,emptyDirectories=true,readonlyPreserved=true,metadataPreserved=true};
});
await Run("locked-source-and-resume",async root=>
{
    var source=MakeSource(root);var engine=new BackupEngine(Path.Combine(root,"backups"));var baseline=HashTree(source);
    using(var handle=new FileStream(Path.Combine(source,"000-data.bin"),FileMode.Open,FileAccess.Read,FileShare.None))await ExpectFailure(async()=>await engine.CreateAsync(Package(source),[]),"locked source");
    var job=engine.ListJobs().Single();Require(job.Status=="Failed"&&!job.SourceVerified,"Locked source marked complete");
    job=await engine.ResumeAsync(job.Id);Require(job.Status=="Completed","Resume failed");SameTree(source,engine.Payload(job));Require(baseline.All(p=>HashTree(source)[p.Key]==p.Value),"Source changed");return new {failedThenResumed=true};
});
await Run("denied-target-and-resume",async root=>
{
    var source=MakeSource(root);var engine=new BackupEngine(Path.Combine(root,"backups"));string? payload=null,sddl=null;
    engine.Checkpoint=phase=>
    {
        if(phase!="backup-copying")return;var job=engine.ListJobs(false).Single();payload=engine.Payload(job);
        var dir=new DirectoryInfo(payload);var acl=dir.GetAccessControl();sddl=acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.CreateFiles,AccessControlType.Deny));dir.SetAccessControl(acl);
    };
    try{await ExpectFailure(async()=>await engine.CreateAsync(Package(source),[]),"target ACL");}finally{if(payload!=null&&sddl!=null)RestoreAccess(payload,sddl);engine.Checkpoint=null;}
    var job=engine.ListJobs().Single();Require(job.Status=="Failed","ACL refusal marked complete");job=await engine.ResumeAsync(job.Id);SameTree(source,engine.Payload(job));return new {denialReal=true,aclRestored=true,resumed=true};
});
await Run("insufficient-space",async root=>
{
    var source=MakeSource(root);var engine=new BackupEngine(Path.Combine(root,"backups")){AvailableBytesOverride=()=>0};
    await ExpectFailure(async()=>await engine.CreateAsync(Package(source),[]),"space");var job=engine.ListJobs().Single();Require(job.Status=="Failed"&&!job.SourceVerified,"Low-space backup complete");
    engine.AvailableBytesOverride=null;job=await engine.ResumeAsync(job.Id);Require(job.Status=="Completed","Space retry failed");return new {freeSpaceInjected=true,realDiskNotFilled=true};
});
await Run("cancel-and-resume",async root=>
{
    var source=MakeSource(root);var engine=new BackupEngine(Path.Combine(root,"backups"));using var cts=new CancellationTokenSource();engine.Checkpoint=phase=>{if(phase=="backup-chunk")cts.Cancel();};
    await ExpectFailure(async()=>await engine.CreateAsync(Package(source),[],cancellation:cts.Token),"cancel");var job=engine.ListJobs().Single();Require(job.Status=="Cancelled","Cancellation not recorded");
    engine.Checkpoint=null;job=await engine.ResumeAsync(job.Id);SameTree(source,engine.Payload(job));return new {cancelRecorded=true,resumed=true};
});
await Run("source-change-detected",async root=>
{
    var source=MakeSource(root);var engine=new BackupEngine(Path.Combine(root,"backups"));var changed=false;
    engine.Checkpoint=phase=>{if(phase=="backup-file-committed"&&!changed){changed=true;File.WriteAllText(Path.Combine(source,"readme.txt"),"changed during copy");}};
    await ExpectFailure(async()=>await engine.CreateAsync(Package(source),[]),"source modified");Require(engine.ListJobs().Single().Status=="Failed","Changed source marked complete");
    engine.Checkpoint=null;var fresh=await engine.CreateAsync(Package(source),[]);SameTree(source,engine.Payload(fresh));return new {changeDetected=true,freshBackupSucceeded=true};
});
await Run("tampered-backup-blocks-restore",async root=>
{
    var source=MakeSource(root);var engine=new BackupEngine(Path.Combine(root,"backups"));var job=await engine.CreateAsync(Package(source),[]);
    File.WriteAllText(Path.Combine(engine.Payload(job),"readme.txt"),"tampered");
    await ExpectFailure(async()=>await engine.RestoreAsync(job.Id,Path.Combine(root,"output")),"tampered restore");Require(engine.Load(job.Id).Status=="Invalid","Corrupt backup not marked invalid");
    Require(engine.ListRestores(job.Id).All(r=>r.Status=="Failed"&&!Directory.Exists(r.TargetContainer)),"Corrupt backup wrote restore payload");return new {restorationBlocked=true};
});
await Run("missing-backup-file",async root=>
{
    var source=MakeSource(root);var engine=new BackupEngine(Path.Combine(root,"backups"));var job=await engine.CreateAsync(Package(source),[]);
    File.Move(Path.Combine(engine.Payload(job),"readme.txt"),Path.Combine(root,"held-readme.txt"));await ExpectFailure(async()=>await engine.VerifyAsync(job.Id),"missing payload");Require(engine.Load(job.Id).Status=="Invalid","Missing file not detected");return new {missingDetected=true};
});
await Run("restore-with-source-unavailable",async root=>
{
    var source=MakeSource(root);var engine=new BackupEngine(Path.Combine(root,"backups"));var job=await engine.CreateAsync(Package(source),[]);var moved=Path.Combine(root,"source-moved");Directory.Move(source,moved);
    var restore=await engine.RestoreAsync(job.Id,Path.Combine(root,"output"));SameTree(moved,engine.RestoredPayload(job,restore));return new {sourceIndependentRestore=true};
});
await Run("unsafe-locations-rejected",async root=>
{
    var source=MakeSource(root);var nested=new BackupEngine(Path.Combine(source,"backup"));await ExpectFailure(async()=>await nested.CreateAsync(Package(source),[]),"nested backup");
    Require(!Directory.Exists(nested.Root),"Nested backup polluted source");var engine=new BackupEngine(Path.Combine(root,"backups"));var job=await engine.CreateAsync(Package(source),[]);
    await ExpectFailure(async()=>await engine.RestoreAsync(job.Id,source),"restore into source");await ExpectFailure(async()=>await engine.RestoreAsync(job.Id,engine.Root),"restore into backup");return new {overlapBlocked=true};
});
await Run("malicious-relative-path-rejected",async root=>
{
    var source=MakeSource(root);var engine=new BackupEngine(Path.Combine(root,"backups"));var job=await engine.CreateAsync(Package(source),[]);job.Files[0].RelativePath="..\\escape.bin";
    var path=Path.Combine(engine.Root,job.Id,"manifest.json");var json=JsonSerializer.Serialize(job);File.WriteAllText(path,json);File.WriteAllText(path+".bak",json);
    await ExpectFailure(async()=>await engine.RestoreAsync(job.Id,Path.Combine(root,"output")),"path traversal");Require(engine.ListJobs().Single().Status=="RecordError","Bad manifest accepted");return new {pathTraversalBlocked=true};
});
await Run("corrupt-record-recovery",async root=>
{
    var source=MakeSource(root);var engine=new BackupEngine(Path.Combine(root,"backups"));var job=await engine.CreateAsync(Package(source),[]);var path=Path.Combine(engine.Root,job.Id,"manifest.json");File.WriteAllText(path,"{broken");
    job=engine.ListJobs().Single();Require(job.Status=="Interrupted","Fallback record not marked interrupted");job=await engine.ResumeAsync(job.Id);SameTree(source,engine.Payload(job));Require(Directory.GetFiles(Path.GetDirectoryName(path)!,"*.invalid-*").Length>0,"Corrupt record not preserved");return new {recovered=true};
});
foreach(var phase in new[]{"backup-chunk","backup-file-committed","backup-verifying"})await Run("crash-"+phase,async root=>
{
    var source=MakeSource(root);var before=HashTree(source);var code=await KillAt(root,phase);var engine=new BackupEngine(Path.Combine(root,"backups"));var job=engine.ListJobs().Single();Require(job.Status=="Interrupted","Crash not identified");
    job=await engine.ResumeAsync(job.Id);SameTree(source,engine.Payload(job));Require(before.All(p=>HashTree(source)[p.Key]==p.Value),"Source modified after crash");return new {processKilled=true,exitCode=code,resumed=true};
});
foreach(var phase in new[]{"restore-chunk","restore-file-committed"})await Run("crash-"+phase,async root=>
{
    var source=MakeSource(root);var engine=new BackupEngine(Path.Combine(root,"backups"));var job=await engine.CreateAsync(Package(source),[]);var code=await KillAt(root,phase,job.Id);
    engine.ListJobs();var restore=engine.ListRestores(job.Id).Single();Require(restore.Status=="Interrupted","Restore crash not identified");restore=await engine.ResumeRestoreAsync(job.Id,restore.Id);SameTree(source,engine.RestoredPayload(job,restore));
    foreach(var file in job.Files)Require(File.GetLastWriteTimeUtc(Path.Combine(engine.RestoredPayload(job,restore),file.RelativePath)).Ticks==file.ModifiedTicks,"Resumed restore timestamp mismatch");
    return new {processKilled=true,exitCode=code,resumed=true};
});
await Run("changed-restore-file-not-overwritten",async root=>
{
    var source=MakeSource(root);var engine=new BackupEngine(Path.Combine(root,"backups"));var job=await engine.CreateAsync(Package(source),[]);await KillAt(root,"restore-file-committed",job.Id);
    engine.ListJobs();var restore=engine.ListRestores(job.Id).Single();var target=engine.RestoredPayload(job,restore);var changed=Directory.GetFiles(target,"*",SearchOption.AllDirectories).First();File.WriteAllText(changed,"user changed content");
    await ExpectFailure(async()=>await engine.ResumeRestoreAsync(job.Id,restore.Id),"existing restore modified");Require(File.ReadAllText(changed)=="user changed content","Existing restore overwritten");return new {userFilePreserved=true};
});
if(args.Length>2)await Run("actual-game-backup-and-restore",async root=>
{
    var source=Path.GetFullPath(args[2]);var before=GameScanner.Scan(source).Fingerprint();var hashes=HashTree(source);var engine=new BackupEngine(Path.Combine(root,"backups"));var job=await engine.CreateAsync(Package(source),[]);
    var restore=await engine.RestoreAsync(job.Id,Path.Combine(root,"output"));SameTree(source,engine.Payload(job));SameTree(source,engine.RestoredPayload(job,restore));
    var afterHashes=HashTree(source);Require(GameScanner.Scan(source).Fingerprint()==before&&hashes.All(p=>afterHashes[p.Key]==p.Value),"Actual source changed");
    return new {job.Id,job.TotalBytes,files=job.Files.Count,backup=engine.Payload(job),restored=engine.RestoredPayload(job,restore),sourceUnchanged=true};
});
var reportPath=Path.GetFullPath(args[1]);Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);File.WriteAllText(reportPath,JsonSerializer.Serialize(new {runRoot,total=results.Count,passed=results.Count-failed,failed,results},new JsonSerializerOptions {WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
Console.WriteLine($"RESULT: total={results.Count} passed={results.Count-failed} failed={failed}");Environment.ExitCode=failed==0?0:1;

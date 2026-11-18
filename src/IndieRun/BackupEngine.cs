using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace IndieRun;

public sealed partial class BackupEngine
{
    public static bool Removed(BackupJob job)=>job.Status is "Deleting" or "DeleteFailed" or "Deleted";
    public string Root { get; }
    internal Action<string>? Checkpoint { get; set; }
    internal Func<long>? AvailableBytesOverride { get; set; }
    static readonly string[] Active=["Preparing","Copying","Verifying","Restoring"];
    public BackupEngine(string root){Root=Path.GetFullPath(root);}
    FileStream Acquire()
    {
        BackupPaths.NoLinks(Root);Directory.CreateDirectory(Root);
        return new FileStream(Path.Combine(Root,"operation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    }
    string JobDirectory(string id)=>BackupPaths.Id(id)?Path.Combine(Root,id):throw new InvalidDataException("잘못된 백업 ID입니다.");
    string Manifest(string id)=>Path.Combine(JobDirectory(id),"manifest.json");
    public string Payload(BackupJob job)=>BackupPaths.Child(Path.Combine(JobDirectory(job.Id),"payload"),job.OriginalFolder);
    public string RestoredPayload(BackupJob job,RestoreJob restore)=>BackupPaths.Child(restore.TargetContainer,job.OriginalFolder);
    static void ValidateJob(BackupJob job)
    {
        if(job.Version!=1||!BackupPaths.Id(job.Id)||!Path.IsPathFullyQualified(job.SourceRoot)||!BackupPaths.Relative(job.OriginalFolder)||job.OriginalFolder.Contains('\\')||job.OriginalFolder.Contains('/')||job.Package==null||job.Tags==null||job.Files==null||job.Directories==null)throw new InvalidDataException("잘못된 백업 기록입니다.");
        job.Package.ValidateComposition();
        if(job.Files.Any(f=>f==null||!BackupPaths.Relative(f.RelativePath)||f.Length<0||f.ModifiedTicks<DateTime.MinValue.Ticks||f.ModifiedTicks>DateTime.MaxValue.Ticks||(f.Sha256!=null&&(f.Sha256.Length!=64||!f.Sha256.All(Uri.IsHexDigit)))))throw new InvalidDataException("잘못된 파일 기록입니다.");
        if(job.Directories.Any(d=>!BackupPaths.Relative(d))||job.Files.GroupBy(f=>f.RelativePath,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1)||job.Directories.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=job.Directories.Count||job.Files.Any(f=>job.Directories.Contains(f.RelativePath,StringComparer.OrdinalIgnoreCase)))throw new InvalidDataException("중복 또는 충돌 경로입니다.");
        if(job.Status=="Completed"&&(!job.Indexed||!job.SourceVerified||job.Files.Any(f=>f.Sha256==null)))throw new InvalidDataException("완료 조건을 충족하지 않은 백업입니다.");
    }
    public BackupJob Load(string id)
    {
        BackupPaths.NoLinks(JobDirectory(id));var job=BackupJson.Load<BackupJob>(Manifest(id),ValidateJob);
        if(job.Id!=id)throw new InvalidDataException("백업 ID가 일치하지 않습니다.");return job;
    }
    void Save(BackupJob job)
    {
        job.UpdatedUtc=DateTime.UtcNow;ValidateJob(job);BackupJson.Save(Manifest(job.Id),job,ValidateJob);
    }
    void Log(string id,string stage,string? detail=null)
    {
        var path=Path.Combine(JobDirectory(id),"events.jsonl");BackupPaths.NoLinks(path);
        using var stream=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.Read);
        using var writer=new StreamWriter(stream);writer.WriteLine(JsonSerializer.Serialize(new{utc=DateTime.UtcNow,stage,detail}));writer.Flush();stream.Flush(true);
    }
    void State(BackupJob job,string status,string? error=null){job.Status=status;job.Error=error;Save(job);Log(job.Id,status,error);}
    void Failure(BackupJob job,Exception error,string status)
    {
        job.Status=status;job.Error=error.Message;
        try{Save(job);Log(job.Id,status,error.Message);}catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException){job.Error+=" · 작업 기록 저장 실패: "+ex.Message;}
    }
    public List<BackupJob> ListJobs(bool recoverInterrupted=true)
    {
        if(!Directory.Exists(Root))return [];
        BackupPaths.NoLinks(Root);FileStream? recoveryLock=null;
        if(recoverInterrupted)try{recoveryLock=Acquire();}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
        try
        {
            var jobs=new List<BackupJob>();
            foreach(var dir in Directory.GetDirectories(Root).Where(d=>BackupPaths.Id(Path.GetFileName(d))))
            {
                var id=Path.GetFileName(dir);
                try
                {
                    var job=Load(id);
                    if(recoveryLock!=null&&Active.Contains(job.Status))State(job,"Interrupted","이전 작업이 완료되기 전에 중단됐습니다.");
                    if(recoveryLock!=null)foreach(var restore in ListRestores(id).Where(r=>Active.Contains(r.Status)))SaveRestore(restore,"Interrupted","이전 복원 작업이 중단됐습니다.");
                    jobs.Add(job);
                }
                catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException){jobs.Add(new BackupJob {Id=id,Status="RecordError",Error=ex.Message,OriginalFolder=id});}
            }
            return jobs.OrderByDescending(j=>j.CreatedUtc).ToList();
        }
        finally{recoveryLock?.Dispose();}
    }
    static ScanSnapshot Index(string root,CancellationToken cancellation,BackupJob? job=null)
    {
        BackupPaths.NoLinks(root);var scan=GameScanner.Scan(root,job?.Package.OmittedFolders.Select(r=>Path.Combine(root,r)),cancellation:cancellation);
        if(scan.Issues.Count>0)throw new IOException("일부 항목을 읽지 못했거나 연결 항목이 있습니다. 전체 백업을 중단합니다. "+scan.Issues[0]);return scan;
    }
    static void MatchTree(BackupJob job,ScanSnapshot scan,bool compareTimes)
    {
        var current=scan.Files.ToDictionary(f=>Path.GetRelativePath(scan.Root,f.Path),StringComparer.OrdinalIgnoreCase);
        var dirs=scan.Directories.Where(d=>!d.Equals(scan.Root,StringComparison.OrdinalIgnoreCase)).Select(d=>Path.GetRelativePath(scan.Root,d)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(current.Count!=job.Files.Count||!dirs.SetEquals(job.Directories)||job.Files.Any(f=>!current.TryGetValue(f.RelativePath,out var now)||now.Length!=f.Length||(compareTimes&&now.ModifiedUtc.Ticks!=f.ModifiedTicks)))throw new IOException(compareTimes?"백업 도중 원본 구성이 바뀌었습니다. 새 백업을 시작해 주세요.":"파일 또는 디렉터리 구성이 백업 기록과 다릅니다.");
    }
    void Space(string path,long bytes)
    {
        var available=AvailableBytesOverride?.Invoke()??new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
        if(available<bytes+16L*1024*1024)throw new IOException("백업/복원에 필요한 여유 공간이 부족합니다.",unchecked((int)0x80070070));
    }
    static async Task<string> Hash(string path,CancellationToken token)
    {
        BackupPaths.NoLinks(path);using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,FileOptions.Asynchronous|FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream,token));
    }
    async Task<string> Copy(string source,string temporary,string target,CancellationToken token,string phase)
    {
        BackupPaths.NoLinks(source);BackupPaths.NoLinks(temporary);BackupPaths.NoLinks(target);
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,FileOptions.Asynchronous|FileOptions.SequentialScan))
        using(var output=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None,1024*1024,FileOptions.Asynchronous))
        {
            var buffer=new byte[1024*1024];int read;
            while((read=await input.ReadAsync(buffer,token))>0){await output.WriteAsync(buffer.AsMemory(0,read),token);hash.AppendData(buffer,0,read);Checkpoint?.Invoke(phase+"-chunk");}
            token.ThrowIfCancellationRequested();output.Flush(true);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    public async Task<BackupJob> CreateAsync(Package package,IEnumerable<TagDefinition> tags,IProgress<BackupProgress>? progress=null,CancellationToken cancellation=default,string reason="manual",string operationGroupId="")
    {
        if(package.ScopeReviewRequired)throw new IOException("이전 범위 정보가 없습니다. 정리 화면에서 범위를 확인한 뒤 진행하세요.");
        package=JsonSerializer.Deserialize<Package>(JsonSerializer.Serialize(package))!;package.RestoreAutomaticExclusions();
        var source=Path.GetFullPath(package.Source);var leaf=new DirectoryInfo(source).Name;
        if(source.Equals(Path.GetPathRoot(source),StringComparison.OrdinalIgnoreCase)||!BackupPaths.Relative(leaf))throw new IOException("게임 디렉터리를 선택해 주세요.");
        if(GameScanner.Within(Root,source)||GameScanner.Within(source,Root))throw new IOException("원본과 백업 위치는 서로 분리해야 합니다.");
        using var operation=Acquire();
        var job=new BackupJob {OperationGroupId=operationGroupId,RetainedForRecovery=operationGroupId.Length>0,Reason=reason,SourceRoot=source,OriginalFolder=leaf,Package=JsonSerializer.Deserialize<Package>(JsonSerializer.Serialize(package))!,Tags=JsonSerializer.Deserialize<List<TagDefinition>>(JsonSerializer.Serialize(tags))!};
        Save(job);Log(job.Id,"Preparing");
        return await RunBackup(job,progress,cancellation);
    }
    public async Task<BackupJob> ResumeAsync(string id,IProgress<BackupProgress>? progress=null,CancellationToken cancellation=default)
    {
        using var operation=Acquire();var job=Load(id);
        if(job.WithoutBackup)throw new IOException("별도 백업이 없는 이동 기록입니다. 작업 기록에서 이동을 확인하세요.");
        if(Removed(job))throw new IOException("삭제한 백업은 재개할 수 없습니다.");
        if(job.Status=="Completed"||job.SourceVerified)throw new IOException("완료된 백업은 재개 대신 검증해 주세요.");
        if(GameScanner.Within(Root,job.SourceRoot)||GameScanner.Within(job.SourceRoot,Root))throw new IOException("원본과 백업 위치가 겹칩니다.");
        return await RunBackup(job,progress,cancellation);
    }
    async Task<BackupJob> RunBackup(BackupJob job,IProgress<BackupProgress>? progress,CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();var scan=Index(job.SourceRoot,token,job);
            if(!job.Indexed)
            {
                job.Directories=scan.Directories.Where(d=>!d.Equals(job.SourceRoot,StringComparison.OrdinalIgnoreCase)).Select(d=>Path.GetRelativePath(job.SourceRoot,d)).ToList();
                job.Files=scan.Files.Select(f=>new BackupFile {RelativePath=Path.GetRelativePath(job.SourceRoot,f.Path),Length=f.Length,ModifiedTicks=f.ModifiedUtc.Ticks,Attributes=File.GetAttributes(f.Path)}).ToList();
                job.TotalBytes=job.Files.Sum(f=>f.Length);job.Indexed=true;Save(job);
            }
            else MatchTree(job,scan,true);
            Space(Root,job.Files.Where(f=>f.Sha256==null).Sum(f=>f.Length));
            var payload=Payload(job);BackupPaths.NoLinks(payload);Directory.CreateDirectory(payload);
            foreach(var dir in job.Directories){var target=BackupPaths.Child(payload,dir);BackupPaths.NoLinks(target);Directory.CreateDirectory(target);}
            State(job,"Copying");Checkpoint?.Invoke("backup-copying");var done=0;long bytes=0;
            foreach(var file in job.Files)
            {
                token.ThrowIfCancellationRequested();var target=BackupPaths.Child(payload,file.RelativePath);var source=BackupPaths.Child(job.SourceRoot,file.RelativePath);
                var reuse=file.Sha256!=null&&File.Exists(target)&&new FileInfo(target).Length==file.Length&&await Hash(target,token)==file.Sha256;
                if(!reuse)
                {
                    var temp=Path.Combine(JobDirectory(job.Id),"staging",$"{done:D8}.part");
                    var digest=await Copy(source,temp,target,token,"backup");
                    if(file.Sha256!=null&&file.Sha256!=digest)throw new IOException("원본 내용이 기존 백업과 달라졌습니다. 새 백업을 시작해 주세요.");
                    if(new FileInfo(temp).Length!=file.Length)throw new IOException("복사한 크기가 원본 기록과 다릅니다.");
                    BackupPaths.NoLinks(target);File.Move(temp,target,true);Checkpoint?.Invoke("backup-file-committed");
                    File.SetLastWriteTimeUtc(target,new DateTime(file.ModifiedTicks,DateTimeKind.Utc));
                    File.SetAttributes(target,file.Attributes&(FileAttributes.ReadOnly|FileAttributes.Hidden|FileAttributes.System|FileAttributes.Archive));
                    file.Sha256=digest;Save(job);
                }
                bytes+=file.Length;progress?.Report(new BackupProgress("Copying",++done,job.Files.Count,bytes,file.RelativePath));
            }
            State(job,"Verifying");Checkpoint?.Invoke("backup-verifying");
            await VerifyPayload(job,payload,progress,token);
            MatchTree(job,Index(job.SourceRoot,token,job),true);
            foreach(var file in job.Files)if(await Hash(BackupPaths.Child(job.SourceRoot,file.RelativePath),token)!=file.Sha256)throw new IOException("검증 중 원본 내용이 바뀌었습니다. 새 백업을 시작해 주세요.");
            MatchTree(job,Index(job.SourceRoot,token,job),true);job.SourceVerified=true;State(job,"Completed");return job;
        }
        catch(OperationCanceledException ex){Failure(job,ex,"Cancelled");throw;}
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException){Failure(job,ex,"Failed");throw;}
    }
    async Task VerifyPayload(BackupJob job,string payload,IProgress<BackupProgress>? progress,CancellationToken token)
    {
        MatchTree(job,Index(payload,token,payload.Equals(job.SourceRoot,StringComparison.OrdinalIgnoreCase)?job:null),false);var done=0;long bytes=0;
        foreach(var file in job.Files)
        {
            if(file.Sha256==null||await Hash(BackupPaths.Child(payload,file.RelativePath),token)!=file.Sha256)throw new IOException("SHA-256 불일치 또는 누락: "+file.RelativePath);
            bytes+=file.Length;progress?.Report(new BackupProgress("Verifying",++done,job.Files.Count,bytes,file.RelativePath));
        }
    }
    public async Task<BackupJob> VerifyAsync(string id,IProgress<BackupProgress>? progress=null,CancellationToken cancellation=default)
    {
        using var operation=Acquire();var job=Load(id);
        if(job.WithoutBackup)throw new IOException("별도 백업이 없는 이동 기록입니다. 작업 기록에서 이동을 확인하세요.");
        if(Removed(job))throw new IOException("삭제한 백업은 검증할 수 없습니다.");
        if(!job.SourceVerified)throw new IOException("중단된 백업은 재개하여 원본 검증을 마쳐 주세요.");
        try{State(job,"Verifying");await VerifyPayload(job,Payload(job),progress,cancellation);State(job,"Completed");return job;}
        catch(OperationCanceledException ex){Failure(job,ex,"Cancelled");throw;}
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException){Failure(job,ex,"Invalid");throw;}
    }
    static void ValidateRestore(RestoreJob restore)
    {
        if(restore.Version!=1||!BackupPaths.Id(restore.Id)||!BackupPaths.Id(restore.BackupId)||!Path.IsPathFullyQualified(restore.TargetContainer)||Path.GetFileName(restore.TargetContainer)!="restore-"+restore.Id)throw new InvalidDataException("복원 기록이 올바르지 않습니다.");
    }
    string RestoreRecord(RestoreJob restore)=>Path.Combine(JobDirectory(restore.BackupId),"restores",restore.Id+".json");
    void SaveRestore(RestoreJob restore,string status,string? error=null)
    {
        ValidateRestore(restore);restore.Status=status;restore.Error=error;restore.UpdatedUtc=DateTime.UtcNow;BackupJson.Save(RestoreRecord(restore),restore,ValidateRestore);Log(restore.BackupId,"Restore:"+status,restore.Id+" "+error);
    }
    public List<RestoreJob> ListRestores(string id)
    {
        var folder=Path.Combine(JobDirectory(id),"restores");if(!Directory.Exists(folder))return [];BackupPaths.NoLinks(folder);
        var list=new List<RestoreJob>();foreach(var file in Directory.GetFiles(folder,"*.json"))
        {
            try
            {
                var restore=BackupJson.Load<RestoreJob>(file,ValidateRestore);
                if(restore.BackupId!=id||restore.Id!=Path.GetFileNameWithoutExtension(file))throw new InvalidDataException("복원 ID 불일치");list.Add(restore);
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException){list.Add(new RestoreJob {Id=Path.GetFileNameWithoutExtension(file),BackupId=id,Status="RecordError",Error=ex.Message});}
        }
        return list.OrderByDescending(r=>r.CreatedUtc).ToList();
    }
    public void CheckRestoreLocation(BackupJob job,string parent)
    {
        parent=Path.GetFullPath(parent);
        if(!Directory.Exists(parent))throw new IOException("복원할 위치를 찾을 수 없습니다. 다른 폴더를 선택하세요.");
        ValidateRestoreTarget(job,new RestoreJob{TargetContainer=Path.Combine(parent,"restore-"+Guid.NewGuid().ToString("N"))});
    }
    public async Task<RestoreJob> RestoreAsync(string id,string parent,IProgress<BackupProgress>? progress=null,CancellationToken cancellation=default,bool acknowledgeIncomplete=false)
    {
        using var operation=Acquire();var job=Load(id);if(job.WithoutBackup)throw new IOException("별도 백업이 없어 백업 복원을 사용할 수 없습니다.");if(job.LegacyIncomplete&&!acknowledgeIncomplete)throw new IOException("이전 버전에서 일부 폴더가 빠진 백업입니다. 누락된 데이터는 복원되지 않습니다. 확인 후 일부 파일 복원을 선택하세요.");parent=Path.GetFullPath(parent);
        var restore=new RestoreJob {BackupId=id,IncompleteScopeAccepted=acknowledgeIncomplete};restore.TargetContainer=Path.Combine(parent,"restore-"+restore.Id);
        ValidateRestoreTarget(job,restore);SaveRestore(restore,"Preparing");return await RunRestore(job,restore,progress,cancellation);
    }
    public async Task<RestoreJob> ResumeRestoreAsync(string id,string restoreId,IProgress<BackupProgress>? progress=null,CancellationToken cancellation=default,bool acknowledgeIncomplete=false)
    {
        using var operation=Acquire();if(!BackupPaths.Id(restoreId))throw new InvalidDataException("잘못된 복원 ID입니다.");
        var restore=BackupJson.Load<RestoreJob>(Path.Combine(JobDirectory(id),"restores",restoreId+".json"),ValidateRestore);
        if(restore.BackupId!=id||restore.Id!=restoreId||restore.Status=="Completed")throw new IOException("재개할 복원 작업을 확인해 주세요.");
        var job=Load(id);if(job.WithoutBackup)throw new IOException("별도 백업이 없어 백업 복원을 사용할 수 없습니다.");if(acknowledgeIncomplete)restore.IncompleteScopeAccepted=true;ValidateRestoreTarget(job,restore);return await RunRestore(job,restore,progress,cancellation);
    }
    void ValidateRestoreTarget(BackupJob job,RestoreJob restore)
    {
        BackupPaths.NoLinks(restore.TargetContainer);
        if(!Path.GetFileName(restore.TargetContainer).StartsWith("restore-",StringComparison.Ordinal)||GameScanner.Within(restore.TargetContainer,Root)||GameScanner.Within(Root,restore.TargetContainer)||GameScanner.Within(restore.TargetContainer,job.SourceRoot)||GameScanner.Within(job.SourceRoot,restore.TargetContainer))throw new IOException("원본/백업과 분리된 새 복원 위치를 선택해 주세요.");
    }
    async Task<RestoreJob> RunRestore(BackupJob job,RestoreJob restore,IProgress<BackupProgress>? progress,CancellationToken token)
    {
        try
        {
            if(job.LegacyIncomplete&&!restore.IncompleteScopeAccepted)throw new IOException("이전 버전의 일부 폴더 백업입니다. 누락 범위를 확인한 뒤 복원을 재개하세요.");
            if(job.Status!="Completed"||!job.SourceVerified)throw new IOException("검증 완료된 백업만 복원할 수 있습니다.");
            try{await VerifyPayload(job,Payload(job),progress,token);}
            catch(IOException ex){Failure(job,ex,"Invalid");throw;}
            var marker=Path.Combine(restore.TargetContainer,"restore-owner.json");
            if(Directory.Exists(restore.TargetContainer)&&Directory.EnumerateFileSystemEntries(restore.TargetContainer).Any())
            {
                var owner=BackupJson.Load<RestoreOwner>(marker,v=>{if(v.OperationId!=restore.Id||v.BackupId!=job.Id)throw new InvalidDataException("다른 복원 작업의 폴더입니다.");});
            }
            else {Directory.CreateDirectory(restore.TargetContainer);BackupJson.Save(marker,new RestoreOwner(restore.Id,job.Id));}
            var target=RestoredPayload(job,restore);BackupPaths.NoLinks(target);Directory.CreateDirectory(target);
            foreach(var directory in job.Directories){var path=BackupPaths.Child(target,directory);BackupPaths.NoLinks(path);Directory.CreateDirectory(path);}
            var existing=Index(target,token);
            if(existing.Files.Any(f=>!job.Files.Any(expected=>expected.RelativePath.Equals(Path.GetRelativePath(target,f.Path),StringComparison.OrdinalIgnoreCase)))||existing.Directories.Where(d=>!d.Equals(target,StringComparison.OrdinalIgnoreCase)).Any(d=>!job.Directories.Contains(Path.GetRelativePath(target,d),StringComparer.OrdinalIgnoreCase)))throw new IOException("복원 위치에 예상하지 못한 파일이 있습니다. 새 위치를 선택해 주세요.");
            // A resumed restore already occupies disk space. Only complete, verified files
            // reduce the remaining requirement; partial staging files are never trusted.
            long remaining=0;
            foreach(var file in job.Files)
            {
                token.ThrowIfCancellationRequested();var dest=BackupPaths.Child(target,file.RelativePath);
                if(!File.Exists(dest)){remaining=checked(remaining+file.Length);continue;}
                if(new FileInfo(dest).Length!=file.Length||await Hash(dest,token)!=file.Sha256)throw new IOException("기존 복원 파일이 변경됐습니다. 덮어쓰지 않습니다: "+file.RelativePath);
            }
            Space(restore.TargetContainer,remaining);
            SaveRestore(restore,"Restoring");var done=0;long bytes=0;
            foreach(var file in job.Files)
            {
                token.ThrowIfCancellationRequested();var dest=BackupPaths.Child(target,file.RelativePath);
                if(File.Exists(dest))
                {if(new FileInfo(dest).Length!=file.Length||await Hash(dest,token)!=file.Sha256)throw new IOException("기존 복원 파일이 변경됐습니다. 덮어쓰지 않습니다: "+file.RelativePath);}
                else
                {
                    var temporary=Path.Combine(restore.TargetContainer,"staging",$"{done:D8}.part");
                    var digest=await Copy(BackupPaths.Child(Payload(job),file.RelativePath),temporary,dest,token,"restore");
                    if(digest!=file.Sha256)throw new IOException("복원 중 백업 내용이 바뀌었습니다.");
                    File.Move(temporary,dest,false);Checkpoint?.Invoke("restore-file-committed");
                }
                var desiredTime=new DateTime(file.ModifiedTicks,DateTimeKind.Utc);
                if(File.GetLastWriteTimeUtc(dest)!=desiredTime)File.SetLastWriteTimeUtc(dest,desiredTime);
                var desiredAttributes=file.Attributes&(FileAttributes.ReadOnly|FileAttributes.Hidden|FileAttributes.System|FileAttributes.Archive);
                if((File.GetAttributes(dest)&(FileAttributes.ReadOnly|FileAttributes.Hidden|FileAttributes.System|FileAttributes.Archive))!=desiredAttributes)File.SetAttributes(dest,desiredAttributes);
                bytes+=file.Length;progress?.Report(new BackupProgress("Restoring",++done,job.Files.Count,bytes,file.RelativePath));
            }
            await VerifyPayload(job,target,progress,token);
            BackupJson.Save(Path.Combine(restore.TargetContainer,"game-info.json"),new {job.Package,job.Tags,job.SourceRoot});
            SaveRestore(restore,"Completed");return restore;
        }
        catch(OperationCanceledException ex){try{SaveRestore(restore,"Cancelled",ex.Message);}catch(Exception logError) when(logError is IOException or UnauthorizedAccessException){}throw;}
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {try{SaveRestore(restore,"Failed",ex.Message);}catch(Exception logError) when(logError is IOException or UnauthorizedAccessException){}throw;}
    }
}


using System.IO;
using System.Text.Json;

namespace IndieRun;

public sealed class MoveJob
{
    public bool WithoutBackup {get;set;}
    public int Version { get; set; }=1;
    public string Id { get; set; }=Guid.NewGuid().ToString("N");
    public string BackupId { get; set; }="";
    public string BatchId { get; set; }="";
    public string Source { get; set; }="";
    public string Destination { get; set; }="";
    public bool CrossVolume { get; set; }
    public bool PublishIntent { get; set; }
    public bool SourceCaptured { get; set; }
    public bool UndoPrepared { get; set; }
    public bool RestoreIntent { get; set; }
    public string Action { get; set; }="Move";
    public string Status { get; set; }="Preparing";
    public string Phase { get; set; }="Preparing";
    public string? Error { get; set; }
    public DateTime UpdatedUtc { get; set; }=DateTime.UtcNow;
}

public sealed partial class BackupEngine
{
    // Test seam exercises copy semantics on one volume; production uses volume identity.
    internal bool ForceCopyMove { get; set; }
    static bool Expected(Exception ex)=>ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException;
    public static string? MoveNameError(string name)
    {
        if(!BackupPaths.Relative(name)||name.Contains('/')||name.Contains('\\'))return "사용할 수 없는 폴더명입니다.";
        var stem=name.Split('.')[0].ToUpperInvariant();
        if(stem is "CON" or "PRN" or "AUX" or "NUL"||System.Text.RegularExpressions.Regex.IsMatch(stem,@"^(COM|LPT)[1-9¹²³]$"))return "Windows 예약 이름입니다.";
        return null;
    }
    static bool Overlap(string a,string b)=>GameScanner.Within(a,b)||GameScanner.Within(b,a);
    public void Preflight(IEnumerable<(string Source,string Destination)> items)
    {
        var all=items.Select(i=>(Source:Path.GetFullPath(i.Source),Destination:Path.GetFullPath(i.Destination))).ToList();
        if(all.Count==0)throw new IOException("이동할 패키지가 없습니다.");
        foreach(var item in all)
        {
            BackupPaths.NoLinks(item.Source);BackupPaths.NoLinks(item.Destination);
            if(!Directory.Exists(item.Source)||item.Source.Equals(Path.GetPathRoot(item.Source),StringComparison.OrdinalIgnoreCase))throw new IOException("원본 게임 폴더를 확인해 주세요.");
            var problem=MoveNameError(Path.GetFileName(item.Destination));if(problem!=null)throw new IOException(problem);
            if(Overlap(item.Source,item.Destination)||Overlap(item.Source,Root)||Overlap(item.Destination,Root))throw new IOException("원본·목적지·백업 위치는 서로 분리해야 합니다.");
            if(File.Exists(item.Destination)||Directory.Exists(item.Destination))throw new IOException("목적지 이름이 이미 있습니다. 다른 이름을 지정해 주세요: "+item.Destination);
        }
        for(var i=0;i<all.Count;i++)for(var j=i+1;j<all.Count;j++)
            if(Overlap(all[i].Source,all[j].Source)||Overlap(all[i].Destination,all[j].Destination)||Overlap(all[i].Source,all[j].Destination)||Overlap(all[j].Source,all[i].Destination))throw new IOException("패키지 이동 범위 또는 목적지가 겹칩니다.");
    }
    string MoveRecord(MoveJob move)=>Path.Combine(JobDirectory(move.BackupId),"moves",move.Id+".json");
    static string Stage(MoveJob move)=>Path.Combine(Path.GetDirectoryName(move.Destination)!,".indierun-move-"+move.Id);
    static string StagePayload(MoveJob move)=>Path.Combine(Stage(move),"payload");
    static string Quarantine(MoveJob move)=>Path.Combine(Path.GetDirectoryName(move.Source)!,".indierun-original-"+move.Id);
    public IEnumerable<string> MoveWorkingPaths()=>ListMoves(false).Where(m=>m.Status!="RecordError").SelectMany(m=>new[]{Stage(m),Quarantine(m)});
    static void ValidateMove(MoveJob move)
    {
        if(move.Version!=1||!BackupPaths.Id(move.Id)||!BackupPaths.Id(move.BackupId)||!Path.IsPathFullyQualified(move.Source)||!Path.IsPathFullyQualified(move.Destination)||MoveNameError(Path.GetFileName(move.Destination))!=null||move.Action is not ("Move" or "Undo"))throw new InvalidDataException("잘못된 이동 기록입니다.");
    }
    void BoundMove(MoveJob move,BackupJob backup)
    {
        ValidateMove(move);
        if(move.WithoutBackup!=backup.WithoutBackup)throw new InvalidDataException("이동 방식과 작업 기록이 일치하지 않습니다.");
        if(!move.Source.Equals(backup.SourceRoot,StringComparison.OrdinalIgnoreCase)||Overlap(move.Source,move.Destination)||Overlap(move.Source,Root)||Overlap(move.Destination,Root))throw new InvalidDataException("이동 기록 경로가 백업과 일치하지 않습니다.");
        foreach(var path in new[]{move.Source,move.Destination,Stage(move),Quarantine(move)})BackupPaths.NoLinks(path);
    }
    void MoveState(MoveJob move,string status,string phase,string? error=null)
    {
        move.Status=status;move.Phase=phase;move.Error=error;move.UpdatedUtc=DateTime.UtcNow;
        BackupJson.Save(MoveRecord(move),move,ValidateMove);Log(move.BackupId,"Move:"+status,move.Id+" "+phase+" "+error);
    }
    MoveJob LoadMove(string backupId,string id)
    {
        if(!BackupPaths.Id(id))throw new InvalidDataException("잘못된 이동 ID입니다.");
        var move=BackupJson.Load<MoveJob>(Path.Combine(JobDirectory(backupId),"moves",id+".json"),ValidateMove);
        if(move.Id!=id||move.BackupId!=backupId)throw new InvalidDataException("이동 ID 불일치");BoundMove(move,Load(backupId));return move;
    }
    public MoveJob ReadMove(string backupId,string id)=>LoadMove(backupId,id);
    public List<MoveJob> ListMoves(bool recoverInterrupted=true)
    {
        if(!Directory.Exists(Root))return [];
        FileStream? held=null;if(recoverInterrupted)try{held=Acquire();}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
        try
        {
            var result=new List<MoveJob>();
            foreach(var dir in Directory.GetDirectories(Root).Where(d=>BackupPaths.Id(Path.GetFileName(d))))
            {
                var records=Path.Combine(dir,"moves");if(!Directory.Exists(records))continue;
                try
                {
                    BackupPaths.NoLinks(records);
                    foreach(var path in Directory.GetFiles(records,"*.json"))
                    {
                        try
                        {
                            var move=LoadMove(Path.GetFileName(dir),Path.GetFileNameWithoutExtension(path));
                            if(held!=null&&move.Status=="Running")MoveState(move,"Interrupted",move.Phase,"이전 이동이 중단됐습니다. 재개 또는 원래 위치 복구를 선택해 주세요.");
                            result.Add(move);
                        }
                        catch(Exception ex) when(Expected(ex)){result.Add(new MoveJob {BackupId=Path.GetFileName(dir),Status="RecordError",Error=ex.Message});}
                    }
                }
                catch(Exception ex) when(Expected(ex)){result.Add(new MoveJob {BackupId=Path.GetFileName(dir),Status="RecordError",Error=ex.Message});}
            }
            return result.OrderByDescending(m=>m.UpdatedUtc).ToList();
        }
        finally{held?.Dispose();}
    }
    async Task VerifyOriginal(BackupJob backup,string path,CancellationToken token)
    {
        MatchTree(backup,Index(path,token,path.Equals(backup.SourceRoot,StringComparison.OrdinalIgnoreCase)?backup:null),true);await VerifyPayload(backup,path,null,token);
    }
    void EnsureStage(MoveJob move)
    {
        var marker=Path.Combine(Stage(move),"owner.json");
        if(Directory.Exists(Stage(move)))BackupJson.Load<RestoreOwner>(marker,v=>{if(v.OperationId!=move.Id||v.BackupId!=move.BackupId)throw new IOException("다른 작업의 임시 폴더입니다.");});
        else {Directory.CreateDirectory(Stage(move));BackupJson.Save(marker,new RestoreOwner(move.Id,move.BackupId));}
    }
    async Task CopyTree(BackupJob backup,string source,string target,MoveJob move,IProgress<BackupProgress>? progress,CancellationToken token,string phase)
    {
        BackupPaths.NoLinks(target);Directory.CreateDirectory(target);
        foreach(var dir in backup.Directories){var path=BackupPaths.Child(target,dir);BackupPaths.NoLinks(path);Directory.CreateDirectory(path);}
        var scan=Index(target,token,target.Equals(backup.SourceRoot,StringComparison.OrdinalIgnoreCase)?backup:null);
        if(scan.Files.Any(f=>!backup.Files.Any(b=>b.RelativePath.Equals(Path.GetRelativePath(target,f.Path),StringComparison.OrdinalIgnoreCase)))||scan.Directories.Where(d=>!d.Equals(target,StringComparison.OrdinalIgnoreCase)).Any(d=>!backup.Directories.Contains(Path.GetRelativePath(target,d),StringComparer.OrdinalIgnoreCase)))throw new IOException("작업 폴더에 예상하지 못한 항목이 있습니다. 덮어쓰지 않습니다.");
        int done=0;long bytes=0;
        foreach(var file in backup.Files)
        {
            token.ThrowIfCancellationRequested();var dest=BackupPaths.Child(target,file.RelativePath);
            if(File.Exists(dest))
            {if(new FileInfo(dest).Length!=file.Length||await Hash(dest,token)!=file.Sha256)throw new IOException("작업 파일이 변경됐습니다. 덮어쓰지 않습니다: "+file.RelativePath);}
            else
            {
                var temp=Path.Combine(Stage(move),"parts",$"{done:D8}.part");
                var hash=await Copy(BackupPaths.Child(source,file.RelativePath),temp,dest,token,phase);
                if(hash!=file.Sha256)throw new IOException("이동 중 원본 내용이 바뀌었습니다.");
                File.Move(temp,dest,false);Checkpoint?.Invoke(phase+"-file");
            }
            if(File.GetLastWriteTimeUtc(dest).Ticks!=file.ModifiedTicks)File.SetLastWriteTimeUtc(dest,new DateTime(file.ModifiedTicks,DateTimeKind.Utc));
            File.SetAttributes(dest,file.Attributes&(FileAttributes.ReadOnly|FileAttributes.Hidden|FileAttributes.System|FileAttributes.Archive));
            bytes+=file.Length;progress?.Report(new BackupProgress(phase,++done,backup.Files.Count,bytes,file.RelativePath));
        }
        await VerifyOriginal(backup,target,token);
    }
    sealed class PhaseProgress(IProgress<BackupProgress>? target,string phase):IProgress<BackupProgress>
    {public void Report(BackupProgress value)=>target?.Report(value with {Stage=phase});}
    // Only remove an exact, verified package tree. Never use recursive directory deletion.
    async Task RemoveVerified(BackupJob backup,string path,string survivor,CancellationToken token)
    {
        BackupPaths.NoLinks(path);BackupPaths.NoLinks(survivor);
        if(!Directory.Exists(path))return;
        await VerifyOriginal(backup,survivor,token);
        var scan=Index(path,token,path.Equals(backup.SourceRoot,StringComparison.OrdinalIgnoreCase)?backup:null);
        if(scan.Files.Any(f=>!backup.Files.Any(b=>b.RelativePath.Equals(Path.GetRelativePath(path,f.Path),StringComparison.OrdinalIgnoreCase)))||scan.Directories.Where(d=>!d.Equals(path,StringComparison.OrdinalIgnoreCase)).Any(d=>!backup.Directories.Contains(Path.GetRelativePath(path,d),StringComparer.OrdinalIgnoreCase)))throw new IOException("삭제 대상에 새 항목이 있어 중단했습니다.");
        foreach(var file in backup.Files)
        {
            token.ThrowIfCancellationRequested();var target=BackupPaths.Child(path,file.RelativePath);if(!File.Exists(target))continue;
            if(new FileInfo(target).Length!=file.Length||await Hash(target,token)!=file.Sha256)throw new IOException("원본이 변경되어 정리를 중단했습니다: "+file.RelativePath);
            BackupPaths.NoLinks(target);File.SetAttributes(target,File.GetAttributes(target)&~FileAttributes.ReadOnly);File.Delete(target);Checkpoint?.Invoke("move-remove-file");
        }
        foreach(var dir in backup.Directories.OrderByDescending(d=>d.Count(c=>c=='\\'||c=='/')).ThenByDescending(d=>d.Length))
        {var target=BackupPaths.Child(path,dir);if(Directory.Exists(target)){BackupPaths.NoLinks(target);Directory.Delete(target,false);}}
        Directory.Delete(path,false);
    }
    public async Task<MoveJob> MoveAsync(string backupId,string destination,string batchId="",IProgress<BackupProgress>? progress=null,CancellationToken cancellation=default)
    {
        using var held=Acquire();var backup=Load(backupId);if(Removed(backup))throw new IOException("삭제한 백업으로 이동할 수 없습니다.");destination=Path.GetFullPath(destination);
        Preflight([(backup.SourceRoot,destination)]);
        var move=new MoveJob {WithoutBackup=backup.WithoutBackup,BackupId=backupId,BatchId=batchId,Source=backup.SourceRoot,Destination=destination,CrossVolume=ForceCopyMove||!Path.GetPathRoot(backup.SourceRoot)!.Equals(Path.GetPathRoot(destination),StringComparison.OrdinalIgnoreCase)};
        MoveState(move,"Running","Preparing");return await RunMove(backup,move,progress,cancellation);
    }
    void RequireSourceDeparture(BackupJob backup,MoveJob move,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();BackupPaths.NoLinks(move.Source);
        if(File.Exists(move.Source))throw new IOException("원본 경로에 항목이 남아 이동 결과를 확인해야 합니다.");
        if(!Directory.Exists(move.Source))return;
        if(backup.Package.OmittedFolders.Count==0)throw new IOException("원본 폴더가 남아 있어 이동 완료로 처리하지 않았습니다.");
        var remaining=Index(move.Source,token,backup);
        if(remaining.Files.Count>0||remaining.Directories.Any(d=>!d.Equals(move.Source,StringComparison.OrdinalIgnoreCase)&&!backup.Package.OmittedFolders.Any(r=>GameScanner.Within(Path.Combine(move.Source,r),d))))throw new IOException("이동 대상 파일이나 폴더가 원래 위치에 남아 있습니다.");
    }
    public async Task<MoveJob> ResumeMoveAsync(string backupId,string id,IProgress<BackupProgress>? progress=null,CancellationToken cancellation=default)
    {
        using var held=Acquire();var move=LoadMove(backupId,id);var backup=Load(backupId);
        if(move.Status=="Completed")return move;
        if(move.Action=="Undo")
        {
            try{return await Undo(backup,move,progress,cancellation);}
            catch(Exception ex) when(Expected(ex)||ex is OperationCanceledException){MoveState(move,"RecoveryRequired",move.Phase,ex.Message);throw;}
        }
        return await RunMove(backup,move,progress,cancellation);
    }
    async Task<MoveJob> RunMove(BackupJob backup,MoveJob move,IProgress<BackupProgress>? progress,CancellationToken token)
    {
        try
        {
            BoundMove(move,backup);if(backup.Package.CandidateOmissions.Any(r=>backup.Package.OmittedFolders.Contains(r,StringComparer.OrdinalIgnoreCase)&&!backup.Package.Contents.Any(c=>c.Path.Equals(r,StringComparison.OrdinalIgnoreCase)&&c.Role is "game" or "exclude")))throw new IOException("이전 버전에서 하위 실행 폴더를 자동 제외한 작업입니다. 원래 위치로 되돌린 뒤 새 이동 계획을 확인하세요.");if(backup.Package.OmittedFolders.Count>0)return await RunSelectiveMove(backup,move,progress,token);if(backup.Status!="Completed"||!backup.SourceVerified)throw new IOException("검증 완료된 백업이 필요합니다.");
            MoveState(move,"Running","Checking");progress?.Report(new BackupProgress("MoveChecking",0,0,0,null));if(!backup.WithoutBackup)await VerifyPayload(backup,Payload(backup),new PhaseProgress(progress,"MoveChecking"),token);EnsureStage(move);
            var quarantine=Quarantine(move);var stage=StagePayload(move);
            if(!move.PublishIntent)
            {
                await VerifyOriginal(backup,move.Source,token);
                if(File.Exists(move.Destination)||Directory.Exists(move.Destination))throw new IOException("목적지 이름 충돌 · 다른 이름을 지정해 주세요.");
                Directory.CreateDirectory(Path.GetDirectoryName(move.Destination)!);
                // A create probe detects directory write denial before touching the source.
                var probe=Path.Combine(Stage(move),"write-probe");using(var test=new FileStream(probe,FileMode.OpenOrCreate,FileAccess.Write,FileShare.None)){test.Flush(true);}
                if(move.CrossVolume)
                {Space(move.Destination,backup.TotalBytes);MoveState(move,"Running","Copying");await CopyTree(backup,move.Source,stage,move,progress,token,"Moving");}
                await VerifyOriginal(backup,move.Source,token);token.ThrowIfCancellationRequested();
                move.PublishIntent=true;MoveState(move,"Running","PublishIntent");Checkpoint?.Invoke("move-before-capture");
            }
            if(move.CrossVolume)
            {
                if(Directory.Exists(move.Source))
                {
                    if(Directory.Exists(quarantine)||Directory.Exists(move.Destination))throw new IOException("원본과 작업 위치가 충돌합니다.");
                    await VerifyOriginal(backup,move.Source,token);await VerifyOriginal(backup,stage,token);
                    Directory.Move(move.Source,quarantine);Checkpoint?.Invoke("move-captured");
                    move.SourceCaptured=true;MoveState(move,"Running","Captured");
                }
                if(Directory.Exists(stage))
                {
                    if(!Directory.Exists(quarantine))throw new IOException("이동할 원본을 찾을 수 없습니다.");
                    await VerifyOriginal(backup,stage,token);Directory.Move(stage,move.Destination);Checkpoint?.Invoke("move-published");
                }
                if(!Directory.Exists(move.Destination))throw new IOException("목적지 복사본이 없습니다.");
                progress?.Report(new BackupProgress("MoveVerifying",0,0,0,null));await VerifyOriginal(backup,move.Destination,token);MoveState(move,"Running","RemovingOriginal");
                await RemoveVerified(backup,quarantine,move.Destination,token);
            }
            else
            {
                if(Directory.Exists(move.Source))
                {
                    await VerifyOriginal(backup,move.Source,token);Directory.Move(move.Source,move.Destination);Checkpoint?.Invoke("move-published");
                    move.SourceCaptured=true;MoveState(move,"Running","Published");
                }
                progress?.Report(new BackupProgress("MoveVerifying",0,0,0,null));await VerifyOriginal(backup,move.Destination,token);
            }
            RequireSourceDeparture(backup,move,token);MoveState(move,"Completed","Completed");Checkpoint?.Invoke("move-completed");return move;
        }
        catch(Exception ex) when(Expected(ex)||ex is OperationCanceledException)
        {
            // Cancellation never cancels rollback; preserve package completeness first.
            try{move.Action="Undo";MoveState(move,"Running","RollingBack",ex.Message);await Undo(backup,move,progress,CancellationToken.None);}
            catch(Exception recovery) when(Expected(recovery))
            {try{MoveState(move,"RecoveryRequired","RollingBack",ex.Message+" · 복구 필요: "+recovery.Message);}catch(Exception log) when(Expected(log)){} }
            throw;
        }
    }
    public async Task<MoveJob> UndoMoveAsync(string backupId,string id,IProgress<BackupProgress>? progress=null,CancellationToken cancellation=default)
    {
        using var held=Acquire();var move=LoadMove(backupId,id);var backup=Load(backupId);if(Removed(backup))throw new IOException("백업이 삭제되어 이동을 되돌릴 수 없습니다.");
        move.Action="Undo";MoveState(move,"Running","RollingBack");
        try{return await Undo(backup,move,progress,cancellation);}
        catch(Exception ex) when(Expected(ex)||ex is OperationCanceledException){try{MoveState(move,"RecoveryRequired","RollingBack",ex.Message);}catch(Exception log) when(Expected(log)){}throw;}
    }
    async Task<MoveJob> Undo(BackupJob backup,MoveJob move,IProgress<BackupProgress>? progress,CancellationToken token)
    {
        BoundMove(move,backup);
        if(backup.Package.OmittedFolders.Count>0)return await UndoSelectiveMove(backup,move,progress,token);
        progress?.Report(new BackupProgress("RollingBack",0,0,0,null));
        var quarantine=Quarantine(move);var stage=StagePayload(move);
        // When the source never left, a conflicting destination belongs to someone else.
        if(Directory.Exists(move.Source))
        {
            if(move.SourceCaptured&&!move.RestoreIntent)throw new IOException("원래 위치에 다른 폴더가 있습니다. 덮어쓰지 않습니다.");
            if(move.RestoreIntent&&Directory.Exists(quarantine))throw new IOException("복구 위치가 충돌합니다.");
            if(move.PublishIntent)await VerifyOriginal(backup,move.Source,token);
            MoveState(move,"RolledBack","RolledBack");await CleanupMoveStage(backup,move,token);return move;
        }
        if(!move.PublishIntent)throw new IOException("원본 위치가 외부에서 변경됐습니다. 백업을 별도 위치에 복원해 주세요.");
        EnsureStage(move);
        if(!move.CrossVolume)
        {
            await VerifyOriginal(backup,move.Destination,token);Directory.CreateDirectory(Path.GetDirectoryName(move.Source)!);
            move.RestoreIntent=true;MoveState(move,"Running","RestoringName");
            Directory.Move(move.Destination,move.Source);Checkpoint?.Invoke("undo-source-restored");
        }
        else
        {
            // Before publishing, leave any unrelated destination untouched.
            var published=!Directory.Exists(stage)&&Directory.Exists(move.Destination);
            if(published&&!move.UndoPrepared)await VerifyOriginal(backup,move.Destination,token);
            var survivor=backup.WithoutBackup?move.UndoPrepared&&Directory.Exists(quarantine)?quarantine:published?move.Destination:Directory.Exists(stage)?stage:quarantine:Payload(backup);
            if(backup.WithoutBackup)await VerifyOriginal(backup,survivor,token);else await VerifyPayload(backup,survivor,progress,token);
            MoveState(move,"Running","RestoringOriginal");
            await CopyTree(backup,survivor,quarantine,move,progress,token,"RollingBack");
            move.UndoPrepared=true;MoveState(move,"Running","RemovingDestination");Checkpoint?.Invoke("undo-prepared");
            if(published)await RemoveVerified(backup,move.Destination,quarantine,token);
            move.RestoreIntent=true;MoveState(move,"Running","RestoringName");
            Directory.CreateDirectory(Path.GetDirectoryName(move.Source)!);Directory.Move(quarantine,move.Source);Checkpoint?.Invoke("undo-source-restored");
        }
        move.SourceCaptured=false;MoveState(move,"RolledBack","RolledBack");await CleanupMoveStage(backup,move,token);return move;
    }
}

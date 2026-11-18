using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
namespace IndieRun;
public sealed partial class BackupEngine
{
    // Persist the source manifest and hashes only. No payload directory or game copy is created.
    public async Task<BackupJob> PrepareDirectMoveAsync(Package package,IEnumerable<TagDefinition> tags,IProgress<BackupProgress>? progress=null,CancellationToken cancellation=default)
    {
        if(package.ScopeReviewRequired)throw new IOException("이전 범위 정보가 없습니다. 정리 화면에서 범위를 확인한 뒤 진행하세요.");
        package=JsonSerializer.Deserialize<Package>(JsonSerializer.Serialize(package))!;package.RestoreAutomaticExclusions();
        var source=Path.GetFullPath(package.Source);var leaf=new DirectoryInfo(source).Name;
        if(source.Equals(Path.GetPathRoot(source),StringComparison.OrdinalIgnoreCase)||!BackupPaths.Relative(leaf))throw new IOException("게임 폴더를 선택하세요.");
        if(GameScanner.Within(Root,source)||GameScanner.Within(source,Root))throw new IOException("게임과 작업 기록 위치는 서로 분리해야 합니다.");
        using var held=Acquire();
        var job=new BackupJob{WithoutBackup=true,Reason="move-only",SourceRoot=source,OriginalFolder=leaf,Package=JsonSerializer.Deserialize<Package>(JsonSerializer.Serialize(package))!,Tags=JsonSerializer.Deserialize<List<TagDefinition>>(JsonSerializer.Serialize(tags))!};
        Save(job);Log(job.Id,"MovePreparing");
        try
        {
            var scan=Index(source,cancellation,job);
            job.Directories=scan.Directories.Where(d=>!d.Equals(source,StringComparison.OrdinalIgnoreCase)).Select(d=>Path.GetRelativePath(source,d)).ToList();
            job.Files=scan.Files.Select(f=>new BackupFile{RelativePath=Path.GetRelativePath(source,f.Path),Length=f.Length,ModifiedTicks=f.ModifiedUtc.Ticks,Attributes=File.GetAttributes(f.Path)}).ToList();
            job.TotalBytes=job.Files.Sum(f=>f.Length);job.Indexed=true;Save(job);int done=0;long bytes=0;
            foreach(var file in job.Files)
            {
                cancellation.ThrowIfCancellationRequested();file.Sha256=await Hash(BackupPaths.Child(source,file.RelativePath),cancellation);bytes+=file.Length;
                progress?.Report(new("MoveChecking",++done,job.Files.Count,bytes,file.RelativePath));Checkpoint?.Invoke("direct-hashed-file");
            }
            MatchTree(job,Index(source,cancellation,job),true);cancellation.ThrowIfCancellationRequested();job.SourceVerified=true;State(job,"Completed");Checkpoint?.Invoke("direct-prepared");return job;
        }
        catch(OperationCanceledException ex){Failure(job,ex,"Cancelled");throw;}
        catch(Exception ex) when(Expected(ex)){Failure(job,ex,"Failed");throw;}
    }
    static async Task<string> PrefixHash(string path,long bytes,CancellationToken token)
    {
        BackupPaths.NoLinks(path);using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,FileOptions.Asynchronous);
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[1024*1024];
        while(bytes>0){var read=await input.ReadAsync(buffer.AsMemory(0,(int)Math.Min(bytes,buffer.Length)),token);if(read==0)throw new IOException("원본 파일 크기가 변경됐습니다.");hash.AppendData(buffer,0,read);bytes-=read;}return Convert.ToHexString(hash.GetHashAndReset());
    }
    async Task CleanupMoveStage(BackupJob backup,MoveJob move,CancellationToken token)
    {
        if(!Directory.Exists(Stage(move)))return;
        EnsureStage(move);var survivor=backup.WithoutBackup?move.Source:Payload(backup);await VerifyOriginal(backup,survivor,token);
        var root=Stage(move);var files=new List<string>();var dirs=new List<string>();var pending=new Stack<string>();pending.Push(root);
        while(pending.TryPop(out var directory))
        {
            foreach(var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();BackupPaths.NoLinks(path);var relative=Path.GetRelativePath(root,path).Replace('\\','/');
                if(Directory.Exists(path))
                {
                    if(relative is not ("parts" or "payload")&&!backup.Directories.Any(d=>relative.Equals("payload/"+d.Replace('\\','/'),StringComparison.OrdinalIgnoreCase)))throw new IOException("임시 작업 폴더에 다른 폴더가 있어 정리를 중단했습니다.");
                    dirs.Add(path);pending.Push(path);continue;
                }
                if(relative is "owner.json" or "write-probe"){files.Add(path);continue;}
                if(relative.StartsWith("payload/",StringComparison.OrdinalIgnoreCase))
                {
                    var file=backup.Files.Find(f=>f.RelativePath.Replace('\\','/').Equals(relative[8..],StringComparison.OrdinalIgnoreCase));
                    if(file==null||await Hash(path,token)!=file.Sha256)throw new IOException("임시 이동 파일이 변경되어 유지했습니다.");
                }
                else if(relative.StartsWith("parts/",StringComparison.OrdinalIgnoreCase)&&System.Text.RegularExpressions.Regex.IsMatch(relative[6..],@"^\d{8}\.part$"))
                {
                    var index=int.Parse(relative.Substring(6,8));if(index>=backup.Files.Count)throw new IOException("임시 파일의 작업 번호가 올바르지 않습니다.");
                    var file=backup.Files[index];var length=new FileInfo(path).Length;
                    if(length>file.Length||await Hash(path,token)!=await PrefixHash(BackupPaths.Child(survivor,file.RelativePath),length,token))throw new IOException("임시 복사 파일이 변경되어 유지했습니다.");
                }
                else throw new IOException("임시 작업 폴더에 다른 파일이 있어 정리를 중단했습니다.");
                files.Add(path);
            }
        }
        await VerifyOriginal(backup,survivor,token);
        foreach(var file in files){BackupPaths.NoLinks(file);File.SetAttributes(file,File.GetAttributes(file)&~FileAttributes.ReadOnly);File.Delete(file);}
        foreach(var dir in dirs.OrderByDescending(d=>d.Length))Directory.Delete(dir,false);Directory.Delete(root,false);
    }
}

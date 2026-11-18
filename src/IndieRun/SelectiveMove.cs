using System.IO;
namespace IndieRun;

public sealed partial class BackupEngine
{
    // A projected package is always copied and verified before any selected source file is removed.
    // Excluded and independently moved subfolders are never captured by a whole-directory rename.
    async Task<MoveJob> RunSelectiveMove(BackupJob backup,MoveJob move,IProgress<BackupProgress>? progress,CancellationToken token)
    {
        if(backup.Status!="Completed"||!backup.SourceVerified)throw new IOException("검증 완료된 백업이 필요합니다.");
        await VerifyPayload(backup,Payload(backup),progress,token);EnsureStage(move);
        var stage=StagePayload(move);
        if(!move.PublishIntent)
        {
            await VerifyOriginal(backup,move.Source,token);
            if(Directory.Exists(move.Destination)||File.Exists(move.Destination))throw new IOException("목적지 이름이 이미 있습니다.");
            Space(move.Destination,backup.TotalBytes);MoveState(move,"Running","Copying");
            await CopyTree(backup,move.Source,stage,move,progress,token,"Moving");
            await VerifyOriginal(backup,move.Source,token);token.ThrowIfCancellationRequested();
            move.CrossVolume=true;move.PublishIntent=true;MoveState(move,"Running","PublishIntent");Checkpoint?.Invoke("selective-intent");
        }
        if(Directory.Exists(stage))
        {
            await VerifyOriginal(backup,stage,token);
            if(Directory.Exists(move.Destination)||File.Exists(move.Destination))throw new IOException("목적지가 변경됐습니다. 덮어쓰지 않습니다.");
            Directory.CreateDirectory(Path.GetDirectoryName(move.Destination)!);Directory.Move(stage,move.Destination);Checkpoint?.Invoke("selective-published");
        }
        await VerifyOriginal(backup,move.Destination,token);
        move.SourceCaptured=true;MoveState(move,"Running","RemovingOriginal");
        await RemoveSelectedSource(backup,move,token);
        MoveState(move,"Completed","Completed");Checkpoint?.Invoke("move-completed");return move;
    }
    async Task RemoveSelectedSource(BackupJob backup,MoveJob move,CancellationToken token)
    {
        if(!Directory.Exists(move.Source))return;
        var scan=Index(move.Source,token,backup);
        if(scan.Files.Any(f=>!backup.Files.Any(b=>b.RelativePath.Equals(Path.GetRelativePath(move.Source,f.Path),StringComparison.OrdinalIgnoreCase)))||scan.Directories.Where(d=>!d.Equals(move.Source,StringComparison.OrdinalIgnoreCase)).Any(d=>!backup.Directories.Contains(Path.GetRelativePath(move.Source,d),StringComparer.OrdinalIgnoreCase)))throw new IOException("원본에 새 항목이 있어 정리를 중단했습니다.");
        foreach(var file in backup.Files)
        {
            var path=BackupPaths.Child(move.Source,file.RelativePath);
            if(File.Exists(path)&&(new FileInfo(path).Length!=file.Length||await Hash(path,token)!=file.Sha256))throw new IOException("원본 파일이 변경되어 유지했습니다: "+file.RelativePath);
        }
        foreach(var file in backup.Files)
        {
            token.ThrowIfCancellationRequested();var path=BackupPaths.Child(move.Source,file.RelativePath);if(!File.Exists(path))continue;
            BackupPaths.NoLinks(path);if(await Hash(path,token)!=file.Sha256)throw new IOException("원본 파일이 변경됐습니다.");
            File.SetAttributes(path,File.GetAttributes(path)&~FileAttributes.ReadOnly);File.Delete(path);Checkpoint?.Invoke("selective-remove-file");
        }
        foreach(var dir in backup.Directories.OrderByDescending(d=>d.Length))
        {
            token.ThrowIfCancellationRequested();var path=BackupPaths.Child(move.Source,dir);BackupPaths.NoLinks(path);
            if(Directory.Exists(path)&&!Directory.EnumerateFileSystemEntries(path).Any())Directory.Delete(path,false);
        }
        if(Directory.Exists(move.Source)&&!Directory.EnumerateFileSystemEntries(move.Source).Any())Directory.Delete(move.Source,false);
    }
    async Task<MoveJob> UndoSelectiveMove(BackupJob backup,MoveJob move,IProgress<BackupProgress>? progress,CancellationToken token)
    {
        var stage=StagePayload(move);
        var published=move.PublishIntent&&!Directory.Exists(stage)&&Directory.Exists(move.Destination);
        if(!published&&!move.SourceCaptured)
        {
            await VerifyOriginal(backup,move.Source,token);MoveState(move,"RolledBack","RolledBack");return move;
        }
        // User changes to a published game stop undo before any source restoration or deletion.
        if(published&&!move.UndoPrepared)await VerifyOriginal(backup,move.Destination,token);
        await VerifyPayload(backup,Payload(backup),progress,token);EnsureStage(move);
        MoveState(move,"Running","RestoringOriginal");
        await CopyTree(backup,Payload(backup),move.Source,move,progress,token,"RollingBack");
        move.UndoPrepared=true;MoveState(move,"Running","RemovingDestination");Checkpoint?.Invoke("selective-undo-prepared");
        if(published)await RemoveVerified(backup,move.Destination,move.Source,token);
        move.SourceCaptured=false;MoveState(move,"RolledBack","RolledBack");return move;
    }
}

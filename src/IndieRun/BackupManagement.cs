using System.IO;

namespace IndieRun;

public sealed record BackupInventory(BackupJob Job,long StoredBytes,long ReclaimableBytes,string? Protection)
{
    public bool Selectable=>Protection==null;
}

public sealed partial class BackupEngine
{

    string? DeleteProtection(BackupJob job,List<MoveJob> moves,bool discardIncomplete=false)
    {
        if(job.RetainedForRecovery)return "이름 변경 복구에 필요한 백업입니다.";
        if(job.Status=="Deleted")return "삭제한 백업입니다.";
        if(job.Status is not ("Completed" or "Deleting" or "DeleteFailed")&&!(discardIncomplete&&job.Status is "Interrupted" or "Cancelled" or "Failed" or "Invalid"))return "백업을 완료하거나 오류를 해결한 뒤 삭제할 수 있습니다.";
        if(moves.Any(m=>m.BackupId==job.Id&&m.Status is not ("Completed" or "RolledBack")))return "이동 복구에 필요한 백업입니다.";
        if(ListRestores(job.Id).Any(r=>r.Status!="Completed"))return "복원 작업에 필요한 백업입니다.";
        return null;
    }
    static long TreeBytes(string path)
    {
        BackupPaths.NoLinks(path);if(!Directory.Exists(path))return 0;
        long total=0;foreach(var file in Directory.EnumerateFiles(path)){BackupPaths.NoLinks(file);total=checked(total+new FileInfo(file).Length);}
        foreach(var dir in Directory.EnumerateDirectories(path))total=checked(total+TreeBytes(dir));return total;
    }
    public List<BackupInventory> Inventory(bool discardIncomplete=false)
    {
        var moves=ListMoves(false);var result=new List<BackupInventory>();
        foreach(var job in ListJobs(false))
        {
            try
            {
                var size=TreeBytes(JobDirectory(job.Id));var reclaim=TreeBytes(Path.Combine(JobDirectory(job.Id),"payload"))+TreeBytes(Path.Combine(JobDirectory(job.Id),"staging"));
                result.Add(new(job,size,reclaim,DeleteProtection(job,moves,discardIncomplete)));
            }
            catch(Exception ex)when(Expected(ex)){result.Add(new(job,0,0,"용량·경로를 확인하지 못했습니다. 폴더를 확인하세요."));}
        }
        return result;
    }
    public void ConfirmExecution(string id,bool confirmed)
    {
        using var held=Acquire();var job=Load(id);if(job.Status!="Completed")throw new IOException("완료된 백업에만 실행 확인을 표시할 수 있습니다.");
        job.ExecutionConfirmedUtc=confirmed?DateTime.UtcNow:null;Save(job);
    }
    public void ReleaseRecoveryBackups(string groupId)
    {
        using var held=Acquire();foreach(var job in ListJobs(false).Where(j=>j.OperationGroupId==groupId&&j.RetainedForRecovery)){job.RetainedForRecovery=false;Save(job);}
    }
    // All selections are checked under the same operation lock before any payload is removed.
    // Keep manifests and move/restore history; a durable intent makes partial deletion retryable.
    public void DeleteBackups(IEnumerable<string> selection,bool discardIncomplete=false)
    {
        var ids=selection.Distinct().ToList();if(ids.Count==0)throw new IOException("삭제할 백업을 선택하세요.");
        using var held=Acquire();var jobs=ids.Select(Load).ToList();var moves=ListMoves(false);
        foreach(var job in jobs)
        {
            var problem=DeleteProtection(job,moves,discardIncomplete);if(problem!=null)throw new IOException(problem);
            _=TreeBytes(JobDirectory(job.Id));
        }
        foreach(var job in jobs)
        {
            State(job,"Deleting");Checkpoint?.Invoke("delete-intent");
            try
            {
                foreach(var part in new[]{"payload","staging"})DeleteTree(Path.Combine(JobDirectory(job.Id),part));
                job.DeletedUtc=DateTime.UtcNow;State(job,"Deleted");
            }
            catch(Exception ex)when(Expected(ex)){Failure(job,ex,"DeleteFailed");throw;}
        }
    }
    static void DeleteTree(string path)
    {
        BackupPaths.NoLinks(path);if(!Directory.Exists(path))return;
        foreach(var file in Directory.EnumerateFiles(path))
        {BackupPaths.NoLinks(file);File.SetAttributes(file,File.GetAttributes(file)&~FileAttributes.ReadOnly);File.Delete(file);}
        foreach(var dir in Directory.EnumerateDirectories(path))DeleteTree(dir);
        BackupPaths.NoLinks(path);File.SetAttributes(path,File.GetAttributes(path)&~FileAttributes.ReadOnly);Directory.Delete(path);
    }
}


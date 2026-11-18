using System.IO;
using System.Text.Json;
namespace IndieRun;
public sealed class RenameRecord
{
    public int Version {get;set;}=1;
    public string Id {get;set;}=Guid.NewGuid().ToString("N");
    public string OldPath {get;set;}="";
    public string NewPath {get;set;}="";
    public string Temporary {get;set;}="";
    public string MarkerId {get;set;}="";
    public FolderCheckpoint Expected {get;set;}=new();
    public EditBundle Bundle {get;set;}=new();
}
public sealed class FolderRename(string dataRoot)
{
    public string Pending=>Path.Combine(dataRoot,"rename-pending.json");
    public bool HasPending=>File.Exists(Pending);
    public Action<string>? Checkpoint {get;set;}
    static T Copy<T>(T value)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    static string Map(string path,string oldRoot,string next)=>GameScanner.Within(path,oldRoot)?Path.GetRelativePath(oldRoot,path) is "."?next:Path.Combine(next,Path.GetRelativePath(oldRoot,path)):path;
    public static bool Exact(string path)=>Directory.Exists(Path.GetDirectoryName(path))&&Directory.EnumerateDirectories(Path.GetDirectoryName(path)!).Any(p=>Path.GetFileName(p).Equals(Path.GetFileName(path),StringComparison.Ordinal));
    public static void VerifyIdentities(WorkspaceDocument plan,string root)
    {
        foreach(var p in plan.Packages.Where(p=>GameScanner.Within(p.Source,root)))
        {
            if(!p.Source.Equals(root,StringComparison.OrdinalIgnoreCase)&&!Directory.Exists(p.Source))continue;
            var marker=LibraryStore.ReadIdentity(p.Source);
            if(marker!=null&&marker.GameId!=p.Id||marker==null&&p.Registered==true&&!p.LocalRegistration)
                throw new IOException("등록된 게임과 폴더의 ID가 다릅니다. 게임 폴더를 다시 연결한 뒤 이름을 변경하세요.");
        }
    }
    static void Validate(RenameRecord r)
    {
        if(r.Version!=1||!BackupPaths.Id(r.Id)||!Path.IsPathFullyQualified(r.OldPath)||!Path.IsPathFullyQualified(r.NewPath)||r.OldPath==r.NewPath||!string.Equals(Path.GetDirectoryName(r.OldPath),Path.GetDirectoryName(r.NewPath),StringComparison.OrdinalIgnoreCase)||BackupEngine.MoveNameError(Path.GetFileName(r.NewPath))!=null||r.Temporary!=Path.Combine(Path.GetDirectoryName(r.OldPath)!,".indierun-rename-"+r.Id)||r.Expected==null||r.Expected.Source!=r.OldPath||r.Expected.Files==null||r.Expected.Directories==null||r.Expected.Files.Any(f=>!BackupPaths.Relative(f.RelativePath))||r.Expected.Directories.Any(d=>!BackupPaths.Relative(d))||r.Bundle==null||r.MarkerId==null||r.MarkerId.Length>0&&!BackupPaths.Id(r.MarkerId))throw new InvalidDataException("폴더명 변경 기록을 확인하세요.");
        WorkspaceStore.Validate(r.Bundle.Plan);PlayerStore.Validate(r.Bundle.Play);
    }
    public RenameRecord Prepare(WorkspaceDocument plan,TagCatalog tags,PlayerDocument play,string oldPath,string name)
    {
        if(HasPending||File.Exists(new EditTransaction(dataRoot).Pending))throw new IOException("이전 변경을 먼저 복구하세요.");
        var error=BackupEngine.MoveNameError(name);if(error!=null)throw new IOException(error);oldPath=Path.TrimEndingDirectorySeparator(Path.GetFullPath(oldPath));BackupPaths.NoLinks(oldPath);
        if(!plan.Packages.Any(p=>p.Source.Equals(oldPath,StringComparison.OrdinalIgnoreCase)||p.Contents.Any(c=>c.Role=="related"&&Path.Combine(p.Source,c.Path).Equals(oldPath,StringComparison.OrdinalIgnoreCase))))throw new IOException("게임 또는 관련 자료의 최상위 폴더만 변경할 수 있습니다.");
        VerifyIdentities(plan,oldPath);
        if(GameScanner.Within(dataRoot,oldPath)||GameScanner.Within(plan.Destination,oldPath)||plan.BackupRoot.Length>0&&GameScanner.Within(plan.BackupRoot,oldPath))throw new IOException("프로그램 정보나 보관함 전체의 이름은 여기서 변경할 수 없습니다.");
        var next=Path.Combine(Path.GetDirectoryName(oldPath)!,name);BackupPaths.NoLinks(next);
        if(oldPath==next)throw new IOException("현재 폴더명과 같습니다.");if((Directory.Exists(next)||File.Exists(next))&&!next.Equals(oldPath,StringComparison.OrdinalIgnoreCase))throw new IOException("같은 이름이 있습니다. 다른 이름을 입력하세요.");
        var scan=GameScanner.Scan(oldPath);if(scan.Issues.Count>0)throw new IOException("폴더 내용을 모두 확인하지 못했습니다. 접근 권한을 확인하세요.");
        var result=Copy(plan);var players=Copy(play);var changed=new List<string>();
        foreach(var p in result.Packages)
        {
            var prior=plan.Packages.Single(q=>q.Id==p.Id);var newSource=Map(prior.Source,oldPath,next);
            if(GameScanner.Within(prior.Source,oldPath)||GameScanner.Within(oldPath,prior.Source)||p.RelatedFolders.Any(r=>GameScanner.Within(r.Source,oldPath)))changed.Add(p.Id);
            foreach(var g in players.Games.Where(g=>g.Id==p.Id))
            {
                string Rebase(string relative){if(relative.Length==0)return "";var absolute=relative=="."?prior.Source:BackupPaths.Child(prior.Source,relative);return Path.GetRelativePath(newSource,Map(absolute,oldPath,next));}
                g.Executable=Rebase(g.Executable);g.WorkingDirectory=Rebase(g.WorkingDirectory);
            }
            foreach(var c in p.Contents)c.Path=Path.GetRelativePath(newSource,Map(Path.Combine(prior.Source,c.Path),oldPath,next));
            p.OmittedFolders=p.OmittedFolders.Select(r=>Path.GetRelativePath(newSource,Map(Path.Combine(prior.Source,r),oldPath,next))).ToList();
            foreach(var r in p.RelatedFolders){r.Source=Map(r.Source,oldPath,next);r.Name=Path.GetFileName(r.Source);}
            p.Source=newSource;p.DetectedRoot=Map(p.DetectedRoot,oldPath,next);p.SearchRoot=Map(p.SearchRoot,oldPath,next);if(changed.Contains(p.Id))p.ScopeHistory.Clear();
            if(prior.Source!=newSource){p.Folder=Path.GetFileName(newSource);if(p.Name==prior.Folder)p.Name=p.Folder;}
        }
        foreach(var c in result.Checkpoints)
        {
            var previous=c.Source;var newRoot=Map(previous,oldPath,next);c.Files=c.Files.Select(f=>f with{RelativePath=Path.GetRelativePath(newRoot,Map(Path.Combine(previous,f.RelativePath),oldPath,next))}).OrderBy(f=>f.RelativePath,StringComparer.OrdinalIgnoreCase).ToList();c.Directories=c.Directories.Select(d=>Path.GetRelativePath(newRoot,Map(Path.Combine(previous,d),oldPath,next))).OrderBy(d=>d,StringComparer.OrdinalIgnoreCase).ToList();c.Source=newRoot;
        }
        result.SearchRoot=Map(result.SearchRoot,oldPath,next);if(result.SearchLocations!=null){foreach(var l in result.SearchLocations)l.Path=Map(l.Path,oldPath,next);result.SearchLocations=result.SearchLocations.GroupBy(l=>l.Path,StringComparer.OrdinalIgnoreCase).Select(g=>new SearchLocation{Path=g.Key,Enabled=g.Any(l=>l.Enabled)}).ToList();}
        var record=new RenameRecord{OldPath=oldPath,NewPath=next,MarkerId=LibraryStore.ReadIdentity(oldPath)?.GameId??"",Expected=WorkspaceReconciler.Capture(new(){Id="rename",Source=oldPath},scan),Bundle=new(){Plan=result,Tags=Copy(tags),Play=players,EditedIds=changed,ReconnectedIds=changed.Where(id=>result.Checkpoints.Any(c=>c.PackageId==id)).ToList()}};record.Temporary=Path.Combine(Path.GetDirectoryName(oldPath)!,".indierun-rename-"+record.Id);Validate(record);return record;
    }
    void Check(RenameRecord r,string path)
    {
        BackupPaths.NoLinks(path);var scan=GameScanner.Scan(path);if(scan.Issues.Count>0)throw new IOException("폴더를 모두 확인하지 못했습니다.");var stamp=WorkspaceReconciler.Capture(new(){Source=path},scan);
        if(!stamp.Files.SequenceEqual(r.Expected.Files)||!stamp.Directories.SequenceEqual(r.Expected.Directories)||r.MarkerId.Length>0&&LibraryStore.ReadIdentity(path)?.GameId!=r.MarkerId)throw new IOException("변경 중 폴더 내용이나 식별 정보가 달라졌습니다. 파일을 유지하고 중단했습니다.");
    }
    public void Commit(RenameRecord record)
    {
        Validate(record);if(HasPending||File.Exists(new EditTransaction(dataRoot).Pending))throw new IOException("이전 변경을 먼저 복구하세요.");Check(record,record.OldPath);
        if(File.Exists(Pending+".bak"))File.Move(Pending+".bak",Pending+".previous-"+Guid.NewGuid().ToString("N"));BackupJson.Save(Pending,record,Validate);Checkpoint?.Invoke("intent");Apply(record);
    }
    public bool AbandonUnchanged()
    {
        if(!HasPending||File.Exists(new EditTransaction(dataRoot).Pending))return false;var r=BackupJson.Load<RenameRecord>(Pending,Validate);
        if(!Exact(r.OldPath)||Exact(r.Temporary))return false;Check(r,r.OldPath);File.Move(Pending,Pending+".cancelled-"+Guid.NewGuid().ToString("N"));return true;
    }
    public bool Recover(){if(!HasPending)return false;Apply(BackupJson.Load<RenameRecord>(Pending,Validate));return true;}
    void Apply(RenameRecord r)
    {
        Validate(r);var old=Exact(r.OldPath);var temporary=Exact(r.Temporary);var next=Exact(r.NewPath);
        if(new[]{old,temporary,next}.Count(v=>v)!=1)throw new IOException("폴더 위치가 충돌하거나 폴더를 찾지 못했습니다. 변경 기록을 유지했습니다.");
        if(old){Check(r,r.OldPath);BackupPaths.NoLinks(r.Temporary);if(Directory.Exists(r.NewPath)&&!r.NewPath.Equals(r.OldPath,StringComparison.OrdinalIgnoreCase)||File.Exists(r.NewPath))throw new IOException("변경할 위치에 다른 항목이 있습니다.");Directory.Move(r.OldPath,r.Temporary);Checkpoint?.Invoke("captured");temporary=true;}
        if(temporary){Check(r,r.Temporary);BackupPaths.NoLinks(r.NewPath);if(Directory.Exists(r.NewPath)||File.Exists(r.NewPath))throw new IOException("같은 이름의 폴더가 생겨 중단했습니다.");Directory.Move(r.Temporary,r.NewPath);Checkpoint?.Invoke("moved");}
        Check(r,r.NewPath);var edits=new EditTransaction(dataRoot);
        if(File.Exists(edits.Pending)){var pending=BackupJson.Load<EditBundle>(edits.Pending,_=>{});if(JsonSerializer.Serialize(pending)!=JsonSerializer.Serialize(r.Bundle))throw new IOException("다른 편집 기록을 먼저 확인하세요.");edits.Recover();}else edits.Commit(r.Bundle);
        Checkpoint?.Invoke("metadata");var last=Path.Combine(dataRoot,"rename-last.json");BackupPaths.NoLinks(last);BackupPaths.NoLinks(last+".bak");if(File.Exists(last))File.Replace(Pending,last,last+".bak",true);else File.Move(Pending,last);
    }
}

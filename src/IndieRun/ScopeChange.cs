using System.IO;
using System.Text.Json;
namespace IndieRun;

public sealed class ScopeChangeRecord
{
    public int Version {get;set;}=1;
    public string Id {get;set;}="";
    public string OldRoot {get;set;}="";
    public string NewRoot {get;set;}="";
    public string Anchor {get;set;}="";
    public string MarkerData {get;set;}="";
    public FolderCheckpoint Expected {get;set;}=new();
    public EditBundle Bundle {get;set;}=new();
}
public sealed class ScopeChange(string dataRoot)
{
    public string Pending=>Path.Combine(dataRoot,"scope-pending.json");
    public bool HasPending=>File.Exists(Pending);
    public Action<string>? Checkpoint {get;set;}
    static T Copy<T>(T item)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(item))!;
    public static string? Problem(Package p,string path,IEnumerable<Package> games)
    {
        path=Path.GetFullPath(path);
        if(!GameScanner.Within(path,p.SearchRoot)||path.Equals(p.SearchRoot,StringComparison.OrdinalIgnoreCase)||!GameScanner.Within(p.DetectedRoot,path))return "실행 폴더를 포함하는 범위를 선택하세요. 탐색 위치 전체는 포함하지 않습니다.";
        if(games.Any(q=>q.Id!=p.Id&&(GameScanner.Within(path,q.Source)||GameScanner.Within(q.Source,path))))return "다른 게임이 함께 포함됩니다. 범위는 변경하지 않았습니다.";
        return null;
    }
    static string Rebase(string oldRoot,string newRoot,string relative,bool folder=false)
    {
        if(relative.Length==0)return "";
        var absolute=relative=="."?oldRoot:BackupPaths.Child(oldRoot,relative);
        if(!GameScanner.Within(absolute,newRoot))throw new IOException(folder?"작업 폴더가 새 범위 밖에 있습니다. 실행 설정을 먼저 확인하세요.":"실행 파일이 새 범위 밖에 있습니다.");
        var result=Path.GetRelativePath(newRoot,absolute);if(result=="."&&!folder)throw new IOException("실행 파일 범위를 확인하세요.");return result;
    }
    public ScopeChangeRecord Prepare(WorkspaceDocument plan,TagCatalog tags,PlayerDocument play,LibraryDocument library,string id,string path)
    {
        if(HasPending||File.Exists(new EditTransaction(dataRoot).Pending))throw new IOException("이전 저장을 먼저 복구하세요.");
        var prior=plan.Packages.Single(p=>p.Id==id);path=Path.GetFullPath(path);BackupPaths.NoLinks(path);BackupPaths.NoLinks(prior.Source);
        var required=play.Games.Find(g=>g.Id==id);var anchorModel=Copy(prior);if(required?.Executable.Length>0)anchorModel.DetectedRoot=Path.GetDirectoryName(BackupPaths.Child(prior.Source,required.Executable))!;
        var error=Problem(anchorModel,path,plan.Packages);if(error!=null)throw new IOException(error);
        if(prior.ReviewState.Length>0)throw new IOException("현재 폴더 상태를 먼저 확인하세요.");
        if(path.Equals(prior.Source,StringComparison.OrdinalIgnoreCase))throw new IOException("현재 범위와 같습니다.");
        var anchor=GameScanner.Within(path,prior.Source)?prior.Source:path;
        if(GameScanner.Within(dataRoot,anchor))throw new IOException("프로그램 정보를 포함하는 폴더는 범위로 지정할 수 없습니다.");
        var scan=GameScanner.Scan(anchor);if(scan.UnreadablePaths.Count>0||scan.Issues.Count>0)throw new IOException("범위 안의 모든 폴더를 확인할 수 없습니다. 위치와 접근 권한을 확인하세요.");
        var marker=LibraryStore.ReadIdentity(prior.Source);
        if(marker!=null&&marker.GameId!=id||marker==null&&library.Games.Any(g=>g.Id==id))throw new IOException("기존 게임 식별 정보를 먼저 확인하세요.");
        var oldMarker=Path.Combine(prior.Source,LibraryStore.MarkerName);
        if(scan.Files.Any(f=>Path.GetFileName(f.Path)==LibraryStore.MarkerName&&!f.Path.Equals(oldMarker,StringComparison.OrdinalIgnoreCase)))throw new IOException("새 범위에 다른 식별 파일이 있습니다. 보관함에서 먼저 확인하세요.");
        var next=Copy(plan);var p=next.Packages.Single(p=>p.Id==id);var nextPlay=Copy(play);var game=nextPlay.Games.Find(g=>g.Id==id);if(game==null){game=new(){Id=id};nextPlay.Games.Add(game);}
        game.Executable=Rebase(prior.Source,path,game.Executable);game.WorkingDirectory=Rebase(prior.Source,path,game.WorkingDirectory,true);
        p.DetectedRoot=anchorModel.DetectedRoot;p.Source=path;p.Folder=Path.GetFileName(path);if(p.Name==prior.Folder)p.Name=p.Folder;p.ScopeHistory.Clear();p.LocalRegistration=marker==null;
        var size=scan.Files.Where(f=>GameScanner.Within(f.Path,path)).Sum(f=>f.Length);p.Gb=size/(1024d*1024*1024);p.Size=size>=1024L*1024*1024?$"{size/(1024d*1024*1024):0.##} GB":size>=1024*1024?$"{size/(1024d*1024):0.##} MB":size>=1024?$"{size/1024d:0.#} KB":$"{size} B";
        next.Checkpoints.RemoveAll(c=>c.PackageId==id);next.Checkpoints.Add(WorkspaceReconciler.Capture(p,scan));
        var expected=WorkspaceReconciler.Capture(new Package{Id=id,Source=anchor},scan);expected.Files.RemoveAll(f=>Path.GetFileName(f.RelativePath)==LibraryStore.MarkerName);
        return new(){Id=id,OldRoot=prior.Source,NewRoot=path,Anchor=anchor,Expected=expected,MarkerData=marker==null?"":Convert.ToBase64String(File.ReadAllBytes(oldMarker)),Bundle=new(){Plan=next,Tags=Copy(tags),Play=nextPlay,EditedIds=[id],ReconnectedIds=[id]}};
    }
    static void Validate(ScopeChangeRecord r)
    {
        if(r.Version!=1||!BackupPaths.Id(r.Id)||!Path.IsPathFullyQualified(r.OldRoot)||!Path.IsPathFullyQualified(r.NewRoot)||!Path.IsPathFullyQualified(r.Anchor)||!GameScanner.Within(r.OldRoot,r.Anchor)||!GameScanner.Within(r.NewRoot,r.Anchor)||r.OldRoot==r.NewRoot||r.Expected==null||r.Expected.Files==null||r.Expected.Directories==null||r.Bundle==null||r.Bundle.Plan==null||r.Bundle.Play==null||r.Bundle.Tags==null||r.MarkerData==null||r.MarkerData.Length>6000)throw new InvalidDataException("범위 변경 기록을 확인하세요.");
        WorkspaceStore.Validate(r.Bundle.Plan);PlayerStore.Validate(r.Bundle.Play);
        var game=r.Bundle.Plan.Packages.Single(p=>p.Id==r.Id);if(!game.Source.Equals(r.NewRoot,StringComparison.OrdinalIgnoreCase)||Problem(game,r.NewRoot,r.Bundle.Plan.Packages)!=null||!GameScanner.Within(game.DetectedRoot,r.OldRoot)||!GameScanner.Within(r.OldRoot,game.SearchRoot)||r.Expected.Source!=r.Anchor||r.Expected.Files.Any(f=>!BackupPaths.Relative(f.RelativePath))||r.Expected.Directories.Any(d=>!BackupPaths.Relative(d)))throw new InvalidDataException("범위 변경 경로를 확인하세요.");
        if(r.MarkerData.Length>0){try{var marker=JsonSerializer.Deserialize<GameIdentity>(Convert.FromBase64String(r.MarkerData));if(marker?.GameId!=r.Id||marker.Format!="IndieRun.GameIdentity"||marker.Version!=1)throw new InvalidDataException("기존 식별 정보가 올바르지 않습니다.");}catch(FormatException ex){throw new InvalidDataException("기존 식별 정보가 올바르지 않습니다.",ex);}}
    }
    public void Commit(ScopeChangeRecord record)
    {
        if(HasPending||File.Exists(new EditTransaction(dataRoot).Pending))throw new IOException("이전 변경을 먼저 복구하세요.");
        Validate(record);CheckFiles(record);if(File.Exists(Pending+".bak"))File.Move(Pending+".bak",Pending+".previous-"+Guid.NewGuid().ToString("N"));BackupJson.Save(Pending,record,Validate);Checkpoint?.Invoke("intent");Apply(record);
    }
    public bool Recover(){if(!HasPending)return false;Apply(BackupJson.Load<ScopeChangeRecord>(Pending,Validate));return true;}
    ScanSnapshot CheckFiles(ScopeChangeRecord r)
    {
        BackupPaths.NoLinks(r.Anchor);var scan=GameScanner.Scan(r.Anchor);
        if(scan.UnreadablePaths.Count>0||scan.Issues.Count>0)throw new IOException("범위 변경 중 접근할 수 없는 항목이 있습니다.");
        var current=WorkspaceReconciler.Capture(new Package{Id=r.Id,Source=r.Anchor},scan);current.Files.RemoveAll(f=>Path.GetFileName(f.RelativePath)==LibraryStore.MarkerName);
        if(!current.Files.SequenceEqual(r.Expected.Files)||!current.Directories.SequenceEqual(r.Expected.Directories))throw new IOException("범위 변경 중 폴더 내용이 달라졌습니다. 기록을 유지했습니다. 원래 상태를 확인한 뒤 복구하세요.");
        var allowed=new[]{Path.Combine(r.OldRoot,LibraryStore.MarkerName),Path.Combine(r.NewRoot,LibraryStore.MarkerName)};
        foreach(var f in scan.Files.Where(f=>Path.GetFileName(f.Path)==LibraryStore.MarkerName))if(!allowed.Contains(f.Path,StringComparer.OrdinalIgnoreCase)||LibraryStore.ReadIdentity(Path.GetDirectoryName(f.Path)!)?.GameId!=r.Id)throw new IOException("식별 정보가 달라졌습니다. 자동으로 덮어쓰지 않습니다.");
        return scan;
    }
    void Apply(ScopeChangeRecord r)
    {
        Validate(r);CheckFiles(r);var oldMarker=Path.Combine(r.OldRoot,LibraryStore.MarkerName);var newMarker=Path.Combine(r.NewRoot,LibraryStore.MarkerName);
        if(r.MarkerData.Length>0)
        {
            var old=LibraryStore.ReadIdentity(r.OldRoot);var next=LibraryStore.ReadIdentity(r.NewRoot);
            if(old==null&&next==null)throw new IOException("원래 식별 파일이 없습니다. 자동으로 새 게임을 만들지 않습니다.");
            if(next==null&&LibraryStore.EnsureIdentity(r.NewRoot,r.Id)!=r.Id)throw new IOException("새 범위의 ID가 다릅니다.");
            Checkpoint?.Invoke("new-marker");
            if(File.Exists(oldMarker))
            {
                if(!File.ReadAllBytes(oldMarker).SequenceEqual(Convert.FromBase64String(r.MarkerData)))throw new IOException("기존 식별 파일이 변경되어 제거하지 않았습니다.");
                BackupPaths.NoLinks(oldMarker);File.Delete(oldMarker);
            }
            Checkpoint?.Invoke("old-marker-removed");
        }
        else if(File.Exists(oldMarker)||File.Exists(newMarker))throw new IOException("새 식별 파일이 나타났습니다. 먼저 확인하세요.");
        var actual=CheckFiles(r);var p=r.Bundle.Plan.Packages.Single(p=>p.Id==r.Id);r.Bundle.Plan.Checkpoints.RemoveAll(c=>c.PackageId==r.Id);r.Bundle.Plan.Checkpoints.Add(WorkspaceReconciler.Capture(p,actual));BackupJson.Save(Pending,r,Validate);
        var edits=new EditTransaction(dataRoot);
        if(File.Exists(edits.Pending))
        {
            var pending=BackupJson.Load<EditBundle>(edits.Pending,_=>{});if(JsonSerializer.Serialize(pending)!=JsonSerializer.Serialize(r.Bundle))throw new IOException("다른 편집 저장이 남아 있습니다. 기록을 확인하세요.");edits.Recover();
        }
        else edits.Commit(r.Bundle);
        Checkpoint?.Invoke("metadata");var last=Path.Combine(dataRoot,"scope-last.json");BackupPaths.NoLinks(last);BackupPaths.NoLinks(last+".bak");if(File.Exists(last))File.Replace(Pending,last,last+".bak",true);else File.Move(Pending,last);
    }
}

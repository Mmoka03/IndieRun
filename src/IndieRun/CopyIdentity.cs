using System.IO;
using System.Text.Json;
namespace IndieRun;

public sealed class CopyIdentityChange
{
    public string Folder {get;set;}="";
    public string OldId {get;set;}="";
    public string NewId {get;set;}="";
    public string MarkerData {get;set;}="";
    public FolderCheckpoint Expected {get;set;}=new();
}

// The existing edit journal owns both the marker change and registration. Recovery never invents a second new ID.
public static class CopyIdentity
{
    static T Copy<T>(T value)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    static string PreviousName(CopyIdentityChange change)=>LibraryStore.MarkerName+".previous-"+change.NewId;
    static bool IdentityFile(string relative,CopyIdentityChange change)=>relative.Equals(LibraryStore.MarkerName,StringComparison.OrdinalIgnoreCase)||relative.Equals(PreviousName(change),StringComparison.OrdinalIgnoreCase);
    public static bool IsOriginal(WorkspaceDocument plan,LibraryDocument library,string folder,string id)=>
        library.Games.Any(g=>g.LastKnownPath.Equals(folder,StringComparison.OrdinalIgnoreCase))||plan.Packages.Any(p=>p.Registered!=false&&p.Source.Equals(folder,StringComparison.OrdinalIgnoreCase));
    public static EditBundle Prepare(WorkspaceDocument plan,TagCatalog tags,PlayerDocument play,LibraryDocument library,string folder,string expectedId,IEnumerable<string> knownPaths)
    {
        folder=Path.GetFullPath(folder);FolderPolicy.RequireEditable(folder);BackupPaths.NoLinks(folder);
        if(IsOriginal(plan,library,folder,expectedId))throw new IOException("현재 등록된 원본입니다. 원본의 연결을 유지하려면 다른 복사본을 선택하세요.");
        if(GameScanner.Within(plan.Destination,folder)||plan.BackupRoot.Length>0&&GameScanner.Within(folder,plan.BackupRoot))throw new IOException("보관함 전체나 백업 안의 폴더는 복사본으로 등록할 수 없습니다.");
        if(LibraryStore.ReadIdentity(folder)?.GameId!=expectedId)throw new IOException("게임 ID가 변경됐습니다. 다시 확인한 뒤 복사본을 선택하세요.");
        bool another=false;
        foreach(var path in knownPaths.Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase))
        {if(path.Equals(folder,StringComparison.OrdinalIgnoreCase))continue;try{if(LibraryStore.ReadIdentity(path)?.GameId==expectedId)another=true;}catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException){}}
        if(!another)throw new IOException("같은 ID의 다른 폴더를 확인하지 못했습니다. 다시 검색한 뒤 복사본을 선택하세요.");
        var candidates=plan.Packages.Where(p=>p.Source.Equals(folder,StringComparison.OrdinalIgnoreCase)).ToList();
        if(candidates.Count==0)throw new IOException("먼저 이 폴더를 탐색 위치에 추가하고 정리 화면에서 범위를 확인하세요.");
        if(candidates.Count>1)throw new IOException("같은 폴더에 연결된 항목이 여러 개입니다. 정리 화면에서 구성을 먼저 확인하세요.");
        var selected=candidates.Single();
        if(selected.Excluded)throw new IOException("정리 화면에서 제외를 해제한 뒤 다시 시도하세요.");
        if(selected.ScopeReviewRequired||selected.Contents.Count>0||selected.RelatedFolders.Count>0||selected.OmittedFolders.Count>0||selected.IsRelatedContent||selected.CompositionParentId.Length>0||plan.Packages.Any(p=>p.Contents.Any(c=>c.Id==selected.Id)||p.RelatedFolders.Any(r=>r.Id==selected.Id)))throw new IOException("이 폴더의 범위나 관련 자료 연결을 먼저 확인하세요. 연결된 항목의 ID는 함께 바꾸지 않습니다.");
        if(plan.Packages.Any(p=>p.Id!=selected.Id&&(GameScanner.Within(folder,p.Source)&&p.Includes(folder)&&FolderPolicy.OwnsScope(p)||GameScanner.Within(p.Source,folder)&&(FolderPolicy.OwnsScope(p)||play.Games.Any(g=>g.Id==p.Id&&LaunchPathRepair.HasSettings(g))))))throw new IOException("별도로 등록하거나 편집한 다른 항목과 범위가 겹칩니다. 정리 화면에서 구성을 확인하세요.");
        var scan=GameScanner.Scan(folder);if(scan.UnreadablePaths.Count>0)throw new IOException("폴더의 모든 파일을 확인하지 못했습니다. 접근 권한과 연결 폴더를 확인하세요.");
        if(scan.Files.Any(f=>Path.GetFileName(f.Path).Equals(LibraryStore.MarkerName,StringComparison.OrdinalIgnoreCase)&&!Path.GetDirectoryName(f.Path)!.Equals(folder,StringComparison.OrdinalIgnoreCase)))throw new IOException("하위 폴더에도 게임 ID가 있습니다. 정리 화면에서 별도 게임과 관련 자료의 구성을 먼저 확인하세요.");
        var verified=Copy(selected);WorkspaceReconciler.Check(verified,plan.Checkpoints.Find(c=>c.PackageId==selected.Id),scan);
        if(verified.ReviewState.Length>0)throw new IOException("폴더 구성이 달라졌습니다. 정리 화면에서 현재 구성을 확인한 뒤 복사본을 등록하세요.");
        if(!scan.Files.Any(f=>selected.Includes(f.Path)&&Path.GetExtension(f.Path).Equals(".exe",StringComparison.OrdinalIgnoreCase)))throw new IOException("선택한 범위에 실행 파일이 없습니다. 정리 화면에서 범위를 확인하세요.");
        var next=Copy(plan);var players=Copy(play);var p=next.Packages.Single(q=>q.Id==selected.Id);var newId=Guid.NewGuid().ToString("N");
        p.Id=newId;p.Registered=true;p.LocalRegistration=false;p.Planned=false;p.Done=false;p.ScopeHistory.Clear();p.CompositionMoveState="IdentitySeparated:"+newId;
        var settings=players.Games.Find(g=>g.Id==selected.Id);if(settings!=null){var copied=Copy(settings);copied.Id=newId;players.Games.Add(copied);if(selected.Id!=expectedId)players.Games.Remove(settings);}
        else players.Games.Add(new(){Id=newId});
        if(next.SelectedId==selected.Id)next.SelectedId=newId;
        next.Checkpoints.RemoveAll(c=>c.PackageId==selected.Id);next.Checkpoints.Add(WorkspaceReconciler.Capture(p,scan));
        var change=new CopyIdentityChange{Folder=folder,OldId=expectedId,NewId=newId,MarkerData=Convert.ToBase64String(File.ReadAllBytes(Path.Combine(folder,LibraryStore.MarkerName))),Expected=WorkspaceReconciler.Capture(new(){Id=newId,Source=folder},scan)};
        change.Expected.Files.RemoveAll(f=>IdentityFile(f.RelativePath,change));
        var bundle=new EditBundle{Version=2,Plan=next,Tags=Copy(tags),Play=players,ImportedIds=[newId],EditedIds=[newId],ReconnectedIds=[newId],RemovedIds=selected.Id!=expectedId&&library.Games.Any(g=>g.Id==selected.Id)?[selected.Id]:[],CopyIdentityChanges=[change]};
        Validate(bundle);return bundle;
    }
    public static void Validate(EditBundle bundle)
    {
        if(bundle.CopyIdentityChanges==null||bundle.CopyIdentityChanges.Count>1||bundle.CopyIdentityApplied&&bundle.CopyIdentityChanges.Count==0||bundle.CopyIdentityChanges.Count>0&&bundle.Version!=2||bundle.Version==2&&bundle.CopyIdentityChanges.Count!=1)throw new InvalidDataException("복사본 구분 기록을 확인하세요.");
        foreach(var change in bundle.CopyIdentityChanges)
        {
            if(change==null||!Path.IsPathFullyQualified(change.Folder)||!BackupPaths.Id(change.OldId)||!BackupPaths.Id(change.NewId)||change.OldId==change.NewId||change.MarkerData==null||change.MarkerData.Length>6000||change.Expected==null||change.Expected.Source!=change.Folder||change.Expected.Files==null||change.Expected.Directories==null||change.Expected.Files.Any(f=>f==null||!BackupPaths.Relative(f.RelativePath))||change.Expected.Directories.Any(p=>!BackupPaths.Relative(p))||!bundle.ImportedIds.Contains(change.NewId)||!bundle.EditedIds.Contains(change.NewId)||!bundle.Plan.Packages.Any(p=>p.Id==change.NewId&&p.Source==change.Folder)||!bundle.Plan.Checkpoints.Any(c=>c.PackageId==change.NewId))throw new InvalidDataException("복사본 구분 기록을 확인하세요.");
            try{var old=JsonSerializer.Deserialize<GameIdentity>(Convert.FromBase64String(change.MarkerData));if(old?.Format!="IndieRun.GameIdentity"||old.Version!=1||!Guid.TryParseExact(old.GameId,"N",out var id)||id.ToString("N")!=change.OldId)throw new InvalidDataException("기존 식별 정보가 올바르지 않습니다.");}
            catch(FormatException ex){throw new InvalidDataException("기존 식별 정보가 올바르지 않습니다.",ex);}
        }
    }
    public static void Apply(EditBundle bundle)
    {
        Validate(bundle);
        foreach(var change in bundle.CopyIdentityChanges)
        {
            FolderPolicy.RequireEditable(change.Folder);BackupPaths.NoLinks(change.Folder);
            var marker=Path.Combine(change.Folder,LibraryStore.MarkerName);var previous=Path.Combine(change.Folder,PreviousName(change));var temp=marker+".copy-"+change.NewId+".tmp";
            BackupPaths.NoLinks(marker);BackupPaths.NoLinks(previous);BackupPaths.NoLinks(temp);
            var oldBytes=Convert.FromBase64String(change.MarkerData);var newBytes=JsonSerializer.SerializeToUtf8Bytes(new GameIdentity{GameId=change.NewId});var current=LibraryStore.ReadIdentity(change.Folder);
            if(current?.GameId==change.OldId)
            {
                if(!File.ReadAllBytes(marker).SequenceEqual(oldBytes))throw new IOException("기존 식별 파일 내용이 변경되어 덮어쓰지 않았습니다.");
                var scan=GameScanner.Scan(change.Folder);var check=WorkspaceReconciler.Capture(new(){Id=change.NewId,Source=change.Folder},scan);check.Files.RemoveAll(f=>IdentityFile(f.RelativePath,change)||f.RelativePath==Path.GetFileName(temp));
                if(scan.UnreadablePaths.Count>0||!check.Files.SequenceEqual(change.Expected.Files)||!check.Directories.SequenceEqual(change.Expected.Directories))throw new IOException("복사본 구분 중 폴더 구성이 달라졌습니다. 식별 파일을 바꾸지 않고 기록을 유지했습니다.");
                if(File.Exists(previous)&&!File.ReadAllBytes(previous).SequenceEqual(oldBytes))throw new IOException("기존 식별 파일 보관 위치에 다른 파일이 있습니다.");
                if(File.Exists(temp)){if(!File.ReadAllBytes(temp).SequenceEqual(newBytes))throw new IOException("식별 파일 준비 기록을 확인하세요.");}
                else using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(newBytes);output.Flush(true);}
                if(!File.ReadAllBytes(marker).SequenceEqual(oldBytes))throw new IOException("식별 정보가 변경되어 작업을 중지했습니다.");
                File.Replace(temp,marker,previous,true);
            }
            else if(current?.GameId!=change.NewId||!File.ReadAllBytes(marker).SequenceEqual(newBytes))throw new IOException("복사본의 게임 ID가 변경됐습니다. 다른 ID를 덮어쓰지 않고 기록을 유지했습니다.");
            // Only this transaction's marker files are accepted; later changes to game data still need review.
            var baseline=bundle.Plan.Checkpoints.Single(c=>c.PackageId==change.NewId);
            baseline.Files.RemoveAll(f=>IdentityFile(f.RelativePath,change));
            foreach(var path in new[]{marker,previous})if(File.Exists(path)){var info=new FileInfo(path);baseline.Files.Add(new(Path.GetFileName(path),info.Length,info.LastWriteTimeUtc.Ticks));}
            baseline.Files=baseline.Files.OrderBy(f=>f.RelativePath,StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}

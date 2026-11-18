using System.IO;
namespace IndieRun;
public static class FolderPolicy
{
    public static bool Passive(Package p)=>p.Registered==false&&!p.Planned&&!p.Done&&!p.IsRelatedContent&&!p.PortableMetadata&&p.Alias.Length==0&&p.Genres.Count==0&&p.Categories.Count==0&&p.GenreIds.Count==0&&p.CategoryIds.Count==0&&p.ScopeHistory.Count==0&&p.Contents.Count==0&&p.RelatedFolders.Count==0&&p.OmittedFolders.All(r=>p.CandidateOmissions.Contains(r,StringComparer.OrdinalIgnoreCase))&&p.Name==p.Folder;
    public static Dictionary<string,HashSet<string>> CandidateBranches(ScanSnapshot scan,IEnumerable<string>? extra=null)
    {
        var groups=GameScanner.CandidateGroups(scan).ToList();
        var result=groups.Select(g=>g.Key).Concat(extra??[]).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(r=>r,_=>new HashSet<string>(StringComparer.OrdinalIgnoreCase),StringComparer.OrdinalIgnoreCase);
        foreach(var root in groups.Where(g=>g.Any(f=>NormalExe(f.Path))).Select(g=>g.Key).Concat((extra??[]).Where(scan.ContainsDirectory)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var child=root;
            for(var parent=Path.GetDirectoryName(child);parent!=null;child=parent,parent=Path.GetDirectoryName(parent))
                if(result.TryGetValue(parent,out var branches))branches.Add(Path.GetRelativePath(parent,child));
        }
        return result;
    }
    public static void ApplyCandidateBoundaries(Package p,ScanSnapshot scan,Dictionary<string,HashSet<string>> layout,ISet<string>? protectedIds=null)
    {
        RestoreAutomaticExclusions(p);
        if(!scan.ContainsDirectory(p.Source))return;
        var include=p.Inclusion();var bytes=scan.FilesUnder(p.Source).Where(f=>include(f.Path)).Sum(f=>f.Length);p.Gb=bytes/(1024d*1024*1024);
        p.Size=bytes>=1024L*1024*1024?$"{p.Gb:0.##} GB":bytes>=1024*1024?$"{bytes/(1024d*1024):0.##} MB":bytes>=1024?$"{bytes/1024d:0.#} KB":$"{bytes} B";
    }
    public static bool OwnsScope(Package p)=>p.Registered!=false||p.Planned||p.Done||p.PortableMetadata||p.ScopeHistory.Count>0||p.Contents.Count>0||p.RelatedFolders.Count>0||p.OmittedFolders.Count>0||File.Exists(Path.Combine(p.Source,LibraryStore.MarkerName));
    public static bool UnownedAncestor(Package p,string root)=>!OwnsScope(p)&&!p.IsRelatedContent&&!p.Source.Equals(root,StringComparison.OrdinalIgnoreCase)&&GameScanner.Within(root,p.Source);
    public static void RestoreAutomaticExclusions(Package p)=>p.RestoreAutomaticExclusions();
    public static void RestoreAutomaticExclusions(List<Package> packages)
    {
        var inferredParents=packages.Where(p=>p.CandidateOmissions.Count>0).ToDictionary(p=>p.Id);
        foreach(var child in packages)if(inferredParents.TryGetValue(child.CompositionParentId,out var parent)&&!parent.Contents.Any(c=>c.Id==child.Id&&c.Role is "game" or "exclude"))child.CompositionParentId="";
        foreach(var p in packages)RestoreAutomaticExclusions(p);
    }
    public static List<string> AbsorbOwnedCandidates(List<Package> packages,List<FolderCheckpoint> checkpoints,Package parent,ISet<string>? protectedIds=null)
    {
        var removed=new List<string>();if(parent.Registered==false)return removed;
        foreach(var q in packages.Where(q=>q.Id!=parent.Id&&Passive(q)&&!q.Excluded&&protectedIds?.Contains(q.Id)!=true&&parent.Includes(q.Source)))
        {
            try{var marker=LibraryStore.ReadIdentity(q.Source);if(marker==null||q.Source.Equals(parent.Source,StringComparison.OrdinalIgnoreCase)&&marker.GameId==parent.Id)removed.Add(q.Id);}
            catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException){}
        }
        packages.RemoveAll(q=>removed.Contains(q.Id));checkpoints.RemoveAll(c=>removed.Contains(c.PackageId));return removed;
    }
    public static bool NormalExe(string path)=>Path.GetExtension(path).Equals(".exe",StringComparison.OrdinalIgnoreCase)&&!GameScanner.Auxiliary(path)&&Path.GetFileNameWithoutExtension(path).ToLowerInvariant() is not ("setup" or "dxwebsetup" or "vcredist_x86" or "vcredist_x64")&&!Path.GetFileName(path).StartsWith("unins",StringComparison.OrdinalIgnoreCase);
    public static bool BackupPayload(string folder)
    {
        for(var p=Path.GetFullPath(folder);p!=null;p=Path.GetDirectoryName(p))
        {
            if(!Path.GetFileName(p).Equals("payload",StringComparison.OrdinalIgnoreCase))continue;
            var job=Path.GetDirectoryName(p);var root=job==null?null:Path.GetDirectoryName(job);
            if(job!=null&&root!=null&&BackupPaths.Id(Path.GetFileName(job))&&File.Exists(Path.Combine(root,"operation.lock"))&&(File.Exists(Path.Combine(job,"manifest.json"))||File.Exists(Path.Combine(job,"manifest.json.bak"))))return true;
        }
        return false;
    }
    public static void RequireEditable(string folder)
    {if(BackupPayload(folder))throw new IOException("백업 안의 파일은 게임으로 등록하거나 식별 정보를 변경할 수 없습니다. 백업 관리에서 복원하세요.");}
    public static void Registration(Package p,ScanSnapshot scan,string id)
    {
        RequireEditable(p.Source);
        foreach(var pair in LibraryStore.Inspect(scan).Locations)
            if(pair.Key!=id&&pair.Value.Any(p.Includes)&&LibraryStore.ReadIdentity(p.Source)?.GameId!=id)throw new IOException("이 범위에 이미 등록된 다른 게임이나 자료가 있습니다. 기존 게임의 범위 수정 또는 구성 수정을 사용하세요.");
        for(var parent=Path.GetDirectoryName(p.Source);parent!=null;parent=Path.GetDirectoryName(parent))
        {
            var marker=LibraryStore.ReadIdentity(parent);if(marker==null)continue;
            if(marker.GameId!=id&&marker.GameId!=p.CompositionParentId)throw new IOException("이미 등록된 게임 안의 폴더입니다. 기존 게임에서 별도 게임으로 나눈 뒤 등록하세요.");
            break;
        }
    }
    public static bool Redundant(Package candidate,string gameRoot,ScanSnapshot scan)
    {
        if(!Passive(candidate)||candidate.Excluded||!scan.ContainsDirectory(candidate.Source)||WorkspaceReconciler.HasUnreadable(candidate,scan))return false;
        try{if(LibraryStore.ReadIdentity(candidate.Source)!=null&&!candidate.Source.Equals(gameRoot,StringComparison.OrdinalIgnoreCase))return false;}
        catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException){return false;}
        if(GameScanner.Within(candidate.Source,gameRoot))return true;
        if(!GameScanner.Within(gameRoot,candidate.Source))return false;
        return !scan.FilesUnder(candidate.Source).Any(f=>NormalExe(f.Path)&&!GameScanner.Within(f.Path,gameRoot));
    }
    public static bool Overlap(Package p,Package other,string root)=>other.Id!=p.Id&&(GameScanner.Within(other.Source,root)&&!p.OmittedFolders.Any(r=>GameScanner.Within(other.Source,Path.Combine(root,r)))||GameScanner.Within(root,other.Source)&&other.Includes(root));
    public static bool Conflict(Package p,Package other,string root,ScanSnapshot? scan=null)
    {return ScopeOverlap(p,other,root)&&!UnownedAncestor(other,root)&&!(scan!=null&&(VerifiedMissing(other.Source,scan)||Redundant(other,root,scan)));}
    public static bool ScopeOverlap(Package p,Package other,string root)=>other.Id!=p.Id&&(GameScanner.Within(other.Source,root)&&!p.OmittedFolders.Any(r=>GameScanner.Within(other.Source,Path.Combine(p.Source,r)))||GameScanner.Within(root,other.Source)&&other.Includes(root));
    public static void RebaseScopeExclusions(Package p,string root)
    {
        List<string> Rebase(IEnumerable<string> entries)=>entries.Select(r=>Path.Combine(p.Source,r)).Where(r=>GameScanner.Within(r,root)&&!r.Equals(root,StringComparison.OrdinalIgnoreCase)).Select(r=>Path.GetRelativePath(root,r)).ToList();
        p.OmittedFolders=Rebase(p.OmittedFolders);p.CandidateOmissions=Rebase(p.CandidateOmissions);
    }
    public static bool VerifiedMissing(string path,ScanSnapshot scan)
    {
        if(scan.ContainsDirectory(path)||Directory.Exists(path)||File.Exists(path)||scan.UnreadablePaths.Any(r=>GameScanner.Within(r,path)||GameScanner.Within(path,r)))return false;
        for(var parent=Path.GetDirectoryName(path);parent!=null;parent=Path.GetDirectoryName(parent))if(scan.ContainsDirectory(parent))return true;
        return false;
    }
    public static string ConflictDetails(Package other,string root)
    {
        var kind=other.IsRelatedContent?"관련 자료":other.Registered!=false?"등록된 게임":other.Excluded?"제외한 후보":"미등록 후보";
        var title=other.Alias.Length>0?other.Alias:other.Folder;
        return $"항목: {title}\n종류: {kind}\n기록된 범위: {other.Source}\n선택한 범위: {root}\n폴더 상태: {(Directory.Exists(other.Source)?"존재함":"현재 확인되지 않음")}";
    }
    public static bool RootAllowed(string root)=>!Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(Path.GetFullPath(root))!),StringComparison.OrdinalIgnoreCase)&&!File.Exists(Path.Combine(root,LibraryStore.JsonName))&&!BackupPayload(root);
    public static string Boundary(Package p,string root,IEnumerable<string> searchRoots)=>searchRoots.Where(r=>GameScanner.Within(root,r)).OrderByDescending(r=>r.Length).FirstOrDefault()??(GameScanner.Within(root,p.SearchRoot)?p.SearchRoot:Path.GetDirectoryName(root)!);
}

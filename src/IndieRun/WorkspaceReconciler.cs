using System.IO;
namespace IndieRun;

public static class WorkspaceReconciler
{
    // A folder deliberately left out of this game must not block the selected scope.
    public static bool HasUnreadable(Package package,ScanSnapshot scan)=>scan.UnreadablePaths.Any(path=>GameScanner.Within(package.Source,path)||package.Includes(path));
    public static FolderCheckpoint Capture(Package package,ScanSnapshot scan)
    {
        var include=package.Inclusion();return new()
    {
        PackageId=package.Id,Source=package.Source,
        Files=scan.FilesUnder(package.Source).Where(f=>include(f.Path)).Select(f=>new FileStamp(Path.GetRelativePath(package.Source,f.Path),f.Length,f.ModifiedUtc.Ticks)).OrderBy(f=>f.RelativePath,StringComparer.OrdinalIgnoreCase).ToList(),
        Directories=scan.DirectoriesUnder(package.Source).Where(d=>include(d)&&!d.Equals(package.Source,StringComparison.OrdinalIgnoreCase)).Select(d=>Path.GetRelativePath(package.Source,d)).OrderBy(d=>d,StringComparer.OrdinalIgnoreCase).ToList()
    };
    }
    public static void Check(Package package,FolderCheckpoint? baseline,ScanSnapshot scan)
    {
        package.AddedExecutables=[];package.ReviewDetails=[];package.ReviewState="";package.ReviewReason="";
        if(package.Excluded)return;
        if(scan.InactivePaths.Any(root=>GameScanner.Within(package.Source,root)))
        {package.ReviewState="paused";package.ReviewReason="탐색이 꺼졌거나 등록 해제된 위치입니다";return;}
        if(scan.OfflineRoots.Any(root=>GameScanner.Within(package.Source,root)))
        {package.ReviewState="offline";package.ReviewReason="탐색 위치에 연결할 수 없습니다 · 연결 후 다시 검색하세요";return;}
        if(!scan.ContainsDirectory(package.Source))
        {package.ReviewState="missing";package.ReviewReason="원본 폴더 위치를 확인해 주세요";return;}
        if(HasUnreadable(package,scan))
        {package.ReviewState="unverified";package.ReviewReason="일부 파일을 읽지 못했습니다";return;}
        if(baseline==null||!baseline.Source.Equals(package.Source,StringComparison.OrdinalIgnoreCase))
        {package.ReviewState="changed";package.ReviewReason="현재 폴더 구성을 확인해 주세요";return;}
        var current=Capture(package,scan);
        var previous=baseline.Files.ToDictionary(f=>f.RelativePath,StringComparer.OrdinalIgnoreCase);
        var now=current.Files.ToDictionary(f=>f.RelativePath,StringComparer.OrdinalIgnoreCase);
        package.AddedExecutables=now.Keys.Except(previous.Keys,StringComparer.OrdinalIgnoreCase).Where(path=>Path.GetExtension(path).Equals(".exe",StringComparison.OrdinalIgnoreCase)).OrderBy(path=>path,StringComparer.OrdinalIgnoreCase).ToList();
        foreach(var entry in now)
        {
            if(!previous.TryGetValue(entry.Key,out var old))package.ReviewDetails.Add("추가 · "+entry.Key);
            else if(old.Length!=entry.Value.Length||old.ModifiedTicks!=entry.Value.ModifiedTicks)package.ReviewDetails.Add("변경 · "+entry.Key);
        }
        foreach(var path in previous.Keys.Except(now.Keys,StringComparer.OrdinalIgnoreCase))package.ReviewDetails.Add("없음 · "+path);
        foreach(var path in current.Directories.Except(baseline.Directories,StringComparer.OrdinalIgnoreCase))package.ReviewDetails.Add("폴더 추가 · "+path);
        foreach(var path in baseline.Directories.Except(current.Directories,StringComparer.OrdinalIgnoreCase))package.ReviewDetails.Add("폴더 없음 · "+path);
        if(package.ReviewDetails.Count>0){package.ReviewState="changed";package.ReviewReason=package.AddedExecutables.Count>0?$"실행 파일 {package.AddedExecutables.Count}개가 추가됐습니다.":$"폴더 내용 {package.ReviewDetails.Count}건이 달라졌습니다.";}
    }
    public static List<Package> Discover(ScanSnapshot scan,IEnumerable<Package> existing,CancellationToken token=default)
    {
        var old=existing.ToList();var result=new List<Package>();
        var layout=FolderPolicy.CandidateBranches(scan,old.Select(p=>p.Source));var coverage=new PackageCoverage(old);
        var groups=GameScanner.CandidateGroups(scan).ToList();var primaryRoots=groups.Where(g=>g.Any(f=>FolderPolicy.NormalExe(f.Path))).Select(g=>g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool PrimaryAncestor(string path){for(var parent=Path.GetDirectoryName(path);parent!=null;parent=Path.GetDirectoryName(parent))if(primaryRoots.Contains(parent))return true;return false;}

        var sizes=groups.ToDictionary(g=>g.Key,_=>0L,StringComparer.OrdinalIgnoreCase);
        foreach(var file in scan.Files)
        {
            token.ThrowIfCancellationRequested();
            for(var parent=Path.GetDirectoryName(file.Path);parent!=null;parent=Path.GetDirectoryName(parent))
                if(sizes.TryGetValue(parent,out var bytes))sizes[parent]=bytes+file.Length;
        }
        foreach(var group in groups)
        {
            token.ThrowIfCancellationRequested();
            if(Path.TrimEndingDirectorySeparator(group.Key).Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(group.Key)!),StringComparison.OrdinalIgnoreCase))
            {
                var issue="드라이브 바로 아래의 실행 파일은 별도 게임 폴더에 모은 뒤 검색하세요: "+group.Key;
                if(!scan.Issues.Contains(issue))scan.Issues.Add(issue);continue;
            }
            if(coverage.Contains(group.Key))continue;
            if(group.All(f=>!FolderPolicy.NormalExe(f.Path))&&(layout[group.Key].Count>0||PrimaryAncestor(group.Key)))continue;
            if(group.All(f=>!FolderPolicy.NormalExe(f.Path))&&old.Any(p=>p.Registered!=false&&GameScanner.Within(p.Source,group.Key))&&!scan.FilesUnder(group.Key).Any(f=>FolderPolicy.NormalExe(f.Path)&&!old.Any(p=>p.Registered!=false&&p.Includes(f.Path))))continue;
            var bytes=sizes[group.Key];
            var size=bytes>=1024L*1024*1024?$"{bytes/(1024d*1024*1024):0.##} GB":bytes>=1024*1024?$"{bytes/(1024d*1024):0.#} MB":bytes>=1024?$"{bytes/1024d:0.#} KB":$"{bytes} B";
            var candidate=new Package {Registered=false,IsReal=true,Id=Guid.NewGuid().ToString("N"),Source=group.Key,DetectedRoot=group.Key,SearchRoot=scan.SearchRoots.Where(r=>GameScanner.Within(group.Key,r)).OrderByDescending(r=>r.Length).FirstOrDefault()??(GameScanner.Within(group.Key,scan.Root)?scan.Root:Path.GetDirectoryName(group.Key)!),Folder=Path.GetFileName(group.Key),Name=Path.GetFileName(group.Key),Gb=bytes/(1024d*1024*1024),Size=size};
            FolderPolicy.ApplyCandidateBoundaries(candidate,scan,layout);
            result.Add(candidate);
        }
        return result;
    }
}

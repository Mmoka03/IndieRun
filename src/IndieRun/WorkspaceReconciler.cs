using System.IO;
namespace IndieRun;

public static class WorkspaceReconciler
{
    public static FolderCheckpoint Capture(Package package,ScanSnapshot scan) => new()
    {
        PackageId=package.Id,Source=package.Source,
        Files=scan.Files.Where(f=>GameScanner.Within(f.Path,package.Source)).Select(f=>new FileStamp(Path.GetRelativePath(package.Source,f.Path),f.Length,f.ModifiedUtc.Ticks)).OrderBy(f=>f.RelativePath,StringComparer.OrdinalIgnoreCase).ToList(),
        Directories=scan.Directories.Where(d=>GameScanner.Within(d,package.Source)&&!d.Equals(package.Source,StringComparison.OrdinalIgnoreCase)).Select(d=>Path.GetRelativePath(package.Source,d)).OrderBy(d=>d,StringComparer.OrdinalIgnoreCase).ToList()
    };
    public static void Check(Package package,FolderCheckpoint? baseline,ScanSnapshot scan)
    {
        package.ReviewDetails=[];package.ReviewState="";package.ReviewReason="";
        if(package.Excluded)return;
        if(!scan.Directories.Contains(package.Source,StringComparer.OrdinalIgnoreCase))
        {package.ReviewState="missing";package.ReviewReason="원본 폴더 위치를 확인해 주세요";return;}
        if(scan.UnreadablePaths.Any(p=>GameScanner.Within(p,package.Source)||GameScanner.Within(package.Source,p)))
        {package.ReviewState="unverified";package.ReviewReason="일부 파일을 읽지 못했습니다";return;}
        if(baseline==null||!baseline.Source.Equals(package.Source,StringComparison.OrdinalIgnoreCase))
        {package.ReviewState="changed";package.ReviewReason="현재 폴더 구성을 확인해 주세요";return;}
        var current=Capture(package,scan);
        var previous=baseline.Files.ToDictionary(f=>f.RelativePath,StringComparer.OrdinalIgnoreCase);
        var now=current.Files.ToDictionary(f=>f.RelativePath,StringComparer.OrdinalIgnoreCase);
        foreach(var entry in now)
        {
            if(!previous.TryGetValue(entry.Key,out var old))package.ReviewDetails.Add("추가 · "+entry.Key);
            else if(old.Length!=entry.Value.Length||old.ModifiedTicks!=entry.Value.ModifiedTicks)package.ReviewDetails.Add("변경 · "+entry.Key);
        }
        foreach(var path in previous.Keys.Except(now.Keys,StringComparer.OrdinalIgnoreCase))package.ReviewDetails.Add("없음 · "+path);
        foreach(var path in current.Directories.Except(baseline.Directories,StringComparer.OrdinalIgnoreCase))package.ReviewDetails.Add("폴더 추가 · "+path);
        foreach(var path in baseline.Directories.Except(current.Directories,StringComparer.OrdinalIgnoreCase))package.ReviewDetails.Add("폴더 없음 · "+path);
        if(package.ReviewDetails.Count>0){package.ReviewState="changed";package.ReviewReason=$"폴더 내용 {package.ReviewDetails.Count}건이 달라졌어요";}
    }
    public static List<Package> Discover(ScanSnapshot scan,IEnumerable<Package> existing)
    {
        var old=existing.ToList();var result=new List<Package>();
        foreach(var group in GameScanner.CandidateGroups(scan))
        {
            if(old.Any(p=>GameScanner.Within(group.Key,p.Source)))continue;
            var bytes=scan.Files.Where(f=>GameScanner.Within(f.Path,group.Key)).Sum(f=>f.Length);
            result.Add(new Package {IsReal=true,Id=Guid.NewGuid().ToString("N"),Source=group.Key,DetectedRoot=group.Key,SearchRoot=GameScanner.Within(group.Key,scan.Root)?scan.Root:Path.GetDirectoryName(group.Key)!,Folder=Path.GetFileName(group.Key),Name=Path.GetFileName(group.Key),Gb=bytes/(1024d*1024*1024)});
        }
        return result;
    }
}

using System.IO;
namespace IndieRun;
public static class PathProof
{
    public static string? RebaseTree(string oldRoot,string newRoot,string path)
    {
        var a=Path.GetFullPath(oldRoot);var b=Path.GetFullPath(newRoot);
        while(Path.GetFileName(a).Equals(Path.GetFileName(b),StringComparison.OrdinalIgnoreCase)&&Path.GetDirectoryName(a) is {} ap&&Path.GetDirectoryName(b) is {} bp){a=ap;b=bp;}
        return GameScanner.Within(path,a)?Path.Combine(b,Path.GetRelativePath(a,path)):null;
    }
    static string? Inside(string relative,string root)
    {
        if(root==".")return relative;
        var prefix=root.TrimEnd('\\','/')+Path.DirectorySeparatorChar;
        return relative.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)?relative[prefix.Length..]:null;
    }
    public static bool DirectoryMatches(FolderCheckpoint checkpoint,string oldRelative,string candidate,ScanSnapshot scan,bool allowEmpty=false)
    {
        if(!scan.ContainsDirectory(candidate)||scan.UnreadablePaths.Any(p=>GameScanner.Within(p,candidate)||GameScanner.Within(candidate,p)))return false;
        var old=checkpoint.Files.Select(f=>(Path:Inside(f.RelativePath,oldRelative),Stamp:f)).Where(f=>f.Path!=null).ToList();if(old.Count==0&&!allowEmpty)return false;
        var now=scan.FilesUnder(candidate).Take(old.Count+1).ToDictionary(f=>Path.GetRelativePath(candidate,f.Path),StringComparer.OrdinalIgnoreCase);
        if(now.Count!=old.Count||old.Any(f=>!now.TryGetValue(f.Path!,out var n)||n.Length!=f.Stamp.Length||n.ModifiedUtc.Ticks!=f.Stamp.ModifiedTicks))return false;
        var dirs=checkpoint.Directories.Select(d=>Inside(d,oldRelative)).Where(d=>d!=null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual=scan.DirectoriesUnder(candidate).Where(d=>!d.Equals(candidate,StringComparison.OrdinalIgnoreCase)).Select(d=>Path.GetRelativePath(candidate,d)).ToHashSet(StringComparer.OrdinalIgnoreCase);return dirs.SetEquals(actual);
    }
    public static string? FindDirectory(FolderCheckpoint checkpoint,string oldRelative,string root,ScanSnapshot scan)
    {
        var sample=checkpoint.Files.Select(f=>(Path:Inside(f.RelativePath,oldRelative),Stamp:f)).FirstOrDefault(f=>f.Path!=null);if(sample.Path==null)return null;
        var depth=sample.Path.Split(Path.DirectorySeparatorChar).Length;
        var candidates=scan.FilesUnder(root).Where(f=>Path.GetFileName(f.Path).Equals(Path.GetFileName(sample.Path),StringComparison.OrdinalIgnoreCase)&&f.Length==sample.Stamp.Length&&f.ModifiedUtc.Ticks==sample.Stamp.ModifiedTicks).Select(f=>{var dir=f.Path;for(int i=0;i<depth;i++)dir=Path.GetDirectoryName(dir)!;return dir;}).Where(d=>GameScanner.Within(d,root)).Distinct(StringComparer.OrdinalIgnoreCase);
        var matches=candidates.Where(d=>DirectoryMatches(checkpoint,oldRelative,d,scan)).Take(2).ToList();return matches.Count==1?matches[0]:null;
    }
    public static void FollowRelated(List<Package> packages,List<FolderCheckpoint> checkpoints,Dictionary<string,string> before,ScanSnapshot scan)
    {
        foreach(var asset in packages.Where(p=>p.IsRelatedContent&&!Directory.Exists(p.Source)))
        {
            var check=checkpoints.Find(c=>c.PackageId==asset.Id);if(check==null)continue;
            var matches=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var owner in ContentLinks.Owners(packages,asset.Id))
            {
                if(!before.TryGetValue(owner.Id,out var prior)||prior==owner.Source)continue;
                var candidate=RebaseTree(prior,owner.Source,asset.Source);if(candidate==null||!scan.ContainsDirectory(candidate))continue;
                try{var marker=LibraryStore.ReadIdentity(candidate);if(marker?.GameId==asset.Id||marker==null&&DirectoryMatches(check,".",candidate,scan))matches.Add(candidate);}
                catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException){continue;}
            }
            if(matches.Count!=1)continue;var next=matches.Single();var old=asset.Source;asset.Source=next;asset.DetectedRoot=GameScanner.Within(asset.DetectedRoot,old)?Path.Combine(next,Path.GetRelativePath(old,asset.DetectedRoot)):next;asset.Folder=Path.GetFileName(next);if(asset.Name==Path.GetFileName(old))asset.Name=asset.Folder;asset.SearchRoot=FolderPolicy.Boundary(asset,next,scan.SearchRoots);asset.ScopeHistory.Clear();asset.CompositionMoveState="Reconnected:"+Guid.NewGuid().ToString("N");check.Source=next;
        }
        ContentLinks.Normalize(packages);
    }
}

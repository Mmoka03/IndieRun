using System.IO;
namespace IndieRun;
public static class LaunchPathRepair
{
    public static bool HasSettings(GamePlay g)=>g.Hidden||g.Favorite||g.Executable.Length>0||g.Arguments.Length>0||g.WorkingDirectory.Length>0||g.Image.Length>0||g.LastPlayedUtc!=null;
    public static void Apply(IEnumerable<Package> packages,IEnumerable<FolderCheckpoint> oldChecks,PlayerDocument play,ScanSnapshot scan,IEnumerable<Package>? previous=null)
    {
        foreach(var p in packages)
        {
            if(p.ScopeReviewRequired||!scan.ContainsDirectory(p.Source)||scan.UnreadablePaths.Any(r=>GameScanner.Within(r,p.Source)||GameScanner.Within(p.Source,r)))continue;
            var g=play.Games.Find(g=>g.Id==p.Id);if(g==null||g.Executable.Length==0)continue;
            var executable=Path.GetFullPath(Path.Combine(p.Source,g.Executable));
            var missing=!scan.FilesUnder(p.Source).Any(f=>f.Path.Equals(executable,StringComparison.OrdinalIgnoreCase));
            var workMissing=g.WorkingDirectory.Length>0&&!scan.ContainsDirectory(Path.GetFullPath(Path.Combine(p.Source,g.WorkingDirectory)));
            if(!missing&&!workMissing)continue;
            if(p.ReviewState is "identity" or "relocation" or "offline" or "paused")continue;
            p.ReviewState="launch";p.ReviewReason=missing?"실행 파일 위치를 확인하세요.":"작업 폴더 위치를 확인하세요.";
            var check=oldChecks.FirstOrDefault(c=>c.PackageId==p.Id);var oldDir=Path.GetDirectoryName(g.Executable);oldDir=string.IsNullOrEmpty(oldDir)?".":oldDir;
            var candidate=check==null?null:PathProof.FindDirectory(check,oldDir,p.Source,scan);if(candidate!=null)p.ReviewDetails.Add("확인할 실행 폴더 후보: "+candidate);

        }
    }
}

using System.IO;
using System.Text.Json;
namespace IndieRun;

public static class ReconnectPlanner
{
    // Return a new plan. Validation and previews never change live metadata or game files.
    public static WorkspaceDocument Prepare(WorkspaceDocument original,string id,string folder,ScanSnapshot scan,LibraryDocument library,PlayerDocument play)
    {
        folder=Path.GetFullPath(folder);BackupPaths.NoLinks(folder);GameLaunch.RequireFolder(folder,LaunchProblem.FolderMissing);
        if(GameScanner.Within(original.Destination,folder))throw new IOException("보관함 전체를 게임 폴더로 연결할 수 없습니다.");
        if(!scan.Root.Equals(folder,StringComparison.OrdinalIgnoreCase)||!scan.Directories.Contains(folder,StringComparer.OrdinalIgnoreCase)||scan.UnreadablePaths.Count>0)throw new IOException("폴더의 모든 파일을 확인하지 못했습니다. 접근 권한을 확인하세요.");
        if(!scan.Files.Any(f=>f.Path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)))throw new IOException("이 폴더에 실행 파일이 없습니다.");
        var prior=original.Packages.Single(p=>p.Id==id);var marker=LibraryStore.ReadIdentity(folder);var registered=library.Games.Any(g=>g.Id==id);
        if(marker!=null&&marker.GameId!=id)throw new IOException("다른 게임의 ID가 있는 폴더입니다.");
        if(registered&&marker==null)throw new IOException("게임 ID가 없는 폴더입니다. 기존 ID 파일이 있는 게임 폴더를 선택하세요.");
        var inventory=LibraryStore.Inspect(scan);
        if(inventory.Problems.Count>0||inventory.Locations.Any(pair=>pair.Key!=id||pair.Value.Count!=1||!pair.Value[0].Equals(folder,StringComparison.OrdinalIgnoreCase)))throw new IOException("하위 폴더에 별도로 등록된 게임이 있습니다. 연결할 게임 폴더를 직접 선택하세요.");
        if(!prior.Source.Equals(folder,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(prior.Source))throw new IOException("기존 게임 폴더가 남아 있습니다. 복사본 여부를 먼저 확인하세요.");
        var overlaps=original.Packages.Where(p=>p.Id!=id&&(GameScanner.Within(folder,p.Source)||GameScanner.Within(p.Source,folder))).ToList();
        foreach(var other in overlaps)
        {
            var settings=play.Games.Find(g=>g.Id==other.Id);
            if(!other.Source.Equals(folder,StringComparison.OrdinalIgnoreCase)||other.Excluded||other.Planned||other.Done||other.Alias.Length>0||other.Genres.Count>0||other.Categories.Count>0||library.Games.Any(g=>g.Id==other.Id)||settings!=null&&(settings.Hidden||settings.Favorite||settings.Executable.Length>0||settings.Arguments.Length>0||settings.WorkingDirectory.Length>0||settings.Image.Length>0||settings.LastPlayedUtc!=null))throw new IOException("다른 게임의 등록 범위와 겹칩니다. 보관함에서 기존 연결을 확인하세요.");
        }
        var next=JsonSerializer.Deserialize<WorkspaceDocument>(JsonSerializer.Serialize(original))!;
        var removed=overlaps.Select(p=>p.Id).ToHashSet();next.Packages.RemoveAll(p=>removed.Contains(p.Id));next.Checkpoints.RemoveAll(c=>removed.Contains(c.PackageId)||c.PackageId==id);
        var game=next.Packages.Single(p=>p.Id==id);var detected=Path.GetRelativePath(prior.Source,prior.DetectedRoot);var oldName=game.Folder;
        if(next.SearchLocations!=null&&!next.SearchLocations.Any(l=>GameScanner.Within(folder,l.Path)))SearchLocationRules.Add(next.SearchLocations,folder);
        game.ScopeHistory.Clear();game.Source=folder;game.SearchRoot=next.SearchLocations?.Where(l=>l.Enabled&&GameScanner.Within(folder,l.Path)).OrderByDescending(l=>l.Path.Length).FirstOrDefault()?.Path??(GameScanner.Within(folder,next.SearchRoot)?next.SearchRoot:folder);game.Folder=Path.GetFileName(folder);
        game.DetectedRoot=detected=="."?folder:Path.Combine(folder,detected);if(!scan.Directories.Contains(game.DetectedRoot,StringComparer.OrdinalIgnoreCase))game.DetectedRoot=GameScanner.CandidateGroups(scan).First().Key;
        if(!game.Planned||game.Name==oldName)game.Name=game.Folder;
        game.Gb=scan.Files.Sum(f=>f.Length)/(1024d*1024*1024);game.Size=$"{scan.Files.Sum(f=>f.Length)/1024d:0.#} KB";
        next.Checkpoints.Add(WorkspaceReconciler.Capture(game,scan));next.SelectedId=id;WorkspaceStore.Validate(next);return next;
    }
}

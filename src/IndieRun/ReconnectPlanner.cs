using System.IO;
using System.Text.Json;
namespace IndieRun;

public static class ReconnectPlanner
{
    // Return a new plan. Validation and previews never change live metadata or game files.
    public static WorkspaceDocument Prepare(WorkspaceDocument original,string id,string folder,ScanSnapshot scan,LibraryDocument library,PlayerDocument play)
    =>PrepareWithContext(original,id,folder,scan,library,play,scan);
    public static WorkspaceDocument PrepareWithContext(WorkspaceDocument original,string id,string folder,ScanSnapshot scan,LibraryDocument library,PlayerDocument play,ScanSnapshot context)
    {
        original=JsonSerializer.Deserialize<WorkspaceDocument>(JsonSerializer.Serialize(original))!;FolderPolicy.RestoreAutomaticExclusions(original.Packages);
        var layout=FolderPolicy.CandidateBranches(context,original.Packages.Select(p=>p.Source));
        var protectedIds=play.Games.Where(LaunchPathRepair.HasSettings).Select(g=>g.Id).ToHashSet();
        foreach(var candidate in original.Packages)FolderPolicy.ApplyCandidateBoundaries(candidate,context,layout,protectedIds);
        folder=Path.GetFullPath(folder);FolderPolicy.RequireEditable(folder);
        var own=LibraryStore.Inspect(scan).Locations.GetValueOrDefault(id);
        if(LibraryStore.ReadIdentity(folder)==null&&own?.Count==1)
        {
            folder=own[0];var child=new ScanSnapshot{Root=folder};child.SearchRoots.AddRange(scan.SearchRoots);child.Directories.AddRange(scan.DirectoriesUnder(folder));child.Files.AddRange(scan.FilesUnder(folder));child.UnreadablePaths.AddRange(scan.UnreadablePaths.Where(p=>GameScanner.Within(p,folder)));scan=child;
        }BackupPaths.NoLinks(folder);GameLaunch.RequireFolder(folder,LaunchProblem.FolderMissing);
        if(GameScanner.Within(original.Destination,folder))throw new IOException("보관함 전체를 게임 폴더로 연결할 수 없습니다.");
        if(!scan.Root.Equals(folder,StringComparison.OrdinalIgnoreCase)||!scan.Directories.Contains(folder,StringComparer.OrdinalIgnoreCase)||scan.UnreadablePaths.Count>0)throw new IOException("폴더의 모든 파일을 확인하지 못했습니다. 접근 권한을 확인하세요.");
        if(!scan.Files.Any(f=>f.Path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)))throw new IOException("이 폴더에 실행 파일이 없습니다.");
        var prior=original.Packages.Single(p=>p.Id==id);var marker=LibraryStore.ReadIdentity(folder);var registered=library.Games.Any(g=>g.Id==id);
        if(marker!=null&&marker.GameId!=id)throw new IOException("다른 게임의 ID가 있는 폴더입니다.");
        if(registered&&marker==null)throw new IOException("게임 ID가 없는 폴더입니다. 기존 ID 파일이 있는 게임 폴더를 선택하세요.");
        var inventory=LibraryStore.Inspect(scan);
        var before=original.Packages.ToDictionary(p=>p.Id,p=>p.Source);
        Dictionary<string,string> group;
        if(!registered&&marker==null&&inventory.Locations.Count==0&&inventory.Problems.Count==0)group=new(StringComparer.OrdinalIgnoreCase){{id,folder}};
        else if(inventory.Problems.Count>0||!RegisteredTree.TryRelocation(prior,folder,original.Packages,before,inventory,scan,out group))throw new IOException("하위 식별 정보나 복사본을 확인하세요. 기존에 연결된 폴더 구조와 일치할 때 함께 연결할 수 있습니다.");
        if(!prior.Source.Equals(folder,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(prior.Source))throw new IOException("기존 게임 폴더가 남아 있습니다. 복사본 여부를 먼저 확인하세요.");
        var overlaps=original.Packages.Where(p=>!group.ContainsKey(p.Id)&&FolderPolicy.Overlap(prior,p,folder)&&!FolderPolicy.UnownedAncestor(p,folder)&&!FolderPolicy.VerifiedMissing(p.Source,context)).ToList();
        foreach(var other in overlaps)
        {
            var settings=play.Games.Find(g=>g.Id==other.Id);
            if(!FolderPolicy.Redundant(other,folder,context)||library.Games.Any(g=>g.Id==other.Id)||settings!=null&&LaunchPathRepair.HasSettings(settings))throw new IOException("기존 항목의 범위와 겹칩니다. 아래 항목의 위치와 구성을 확인하세요.\n\n"+FolderPolicy.ConflictDetails(other,folder));
        }
        overlaps.AddRange(original.Packages.Where(q=>q.Id!=id&&!overlaps.Contains(q)&&GameScanner.Within(folder,q.Source)&&FolderPolicy.Redundant(q,folder,context)&&!library.Games.Any(g=>g.Id==q.Id)&&!play.Games.Any(g=>g.Id==q.Id&&LaunchPathRepair.HasSettings(g))));
        var next=JsonSerializer.Deserialize<WorkspaceDocument>(JsonSerializer.Serialize(original))!;
        var removed=overlaps.Select(p=>p.Id).ToHashSet();next.Packages.RemoveAll(p=>removed.Contains(p.Id));next.Checkpoints.RemoveAll(c=>removed.Contains(c.PackageId));
        RegisteredTree.Apply(next.Packages,next.Checkpoints,group,context);
        var game=next.Packages.Single(p=>p.Id==id);var detected=Path.GetRelativePath(prior.Source,prior.DetectedRoot);var oldName=game.Folder;
        if(next.SearchLocations!=null&&!next.SearchLocations.Any(l=>GameScanner.Within(folder,l.Path)))SearchLocationRules.Add(next.SearchLocations,folder);
        game.CompositionMoveState="Reconnected:"+Guid.NewGuid().ToString("N");game.Done=false;
        game.ScopeHistory.Clear();game.Source=folder;game.SearchRoot=next.SearchLocations?.Where(l=>l.Enabled&&GameScanner.Within(folder,l.Path)).OrderByDescending(l=>l.Path.Length).FirstOrDefault()?.Path??(GameScanner.Within(folder,next.SearchRoot)?next.SearchRoot:folder);game.Folder=Path.GetFileName(folder);
        game.DetectedRoot=detected=="."?folder:Path.Combine(folder,detected);
        if(!game.Planned||game.Name==oldName)game.Name=game.Folder;
        game.Gb=scan.Files.Sum(f=>f.Length)/(1024d*1024*1024);game.Size=$"{scan.Files.Sum(f=>f.Length)/1024d:0.#} KB";
        PathProof.FollowRelated(next.Packages,next.Checkpoints,original.Packages.ToDictionary(p=>p.Id,p=>p.Source),context);
        LaunchPathRepair.Apply(next.Packages,original.Checkpoints,System.Text.Json.JsonSerializer.Deserialize<PlayerDocument>(System.Text.Json.JsonSerializer.Serialize(play))!,context,original.Packages);
        foreach(var mapped in next.Packages.Where(p=>group.ContainsKey(p.Id))){next.Checkpoints.RemoveAll(c=>c.PackageId==mapped.Id);next.Checkpoints.Add(WorkspaceReconciler.Capture(mapped,context));}next.SelectedId=id;WorkspaceStore.Validate(next);return next;
    }
}

using System.IO;
namespace IndieRun;

public static class IdentityReconciler
{
    public static void ExpandCandidate(Package p,IdentityInventory inventory,string libraryRoot)
    {
        var markedRoot=inventory.Locations.Values.SelectMany(paths=>paths).Concat(inventory.Problems.Select(problem=>problem.Path)).Where(path=>!path.Equals(libraryRoot,StringComparison.OrdinalIgnoreCase)&&GameScanner.Within(p.Source,path)).OrderByDescending(path=>path.Length).FirstOrDefault();
        if(markedRoot==null)return;
        p.Source=markedRoot;p.SearchRoot=Path.GetDirectoryName(markedRoot)!;p.Folder=Path.GetFileName(markedRoot);p.Name=p.Folder;
    }
    public static void Rebind(List<Package> packages,List<FolderCheckpoint> checkpoints,LibraryDocument doc,IdentityInventory inventory,ScanSnapshot scan,string libraryRoot)
    {
        foreach(var pair in inventory.Locations.Where(p=>p.Value.Count==1))
        {
            if(packages.Any(p=>p.Id==pair.Key))continue;
            var candidate=packages.Find(p=>p.Source.Equals(pair.Value[0],StringComparison.OrdinalIgnoreCase));
            if(candidate==null||doc.Games.Any(g=>g.Id==candidate.Id))continue;
            var oldId=candidate.Id;candidate.Id=pair.Key;foreach(var c in checkpoints.Where(c=>c.PackageId==oldId))c.PackageId=pair.Key;
            var stored=doc.Games.Find(g=>g.Id==pair.Key);
            if(stored!=null&&candidate.Alias.Length==0&&candidate.Genres.Count==0&&candidate.Categories.Count==0)
            {candidate.Alias=stored.Alias;candidate.Genres=[..stored.Genres];candidate.Categories=[..stored.Categories];candidate.GenreIds=[..stored.GenreIds];candidate.CategoryIds=[..stored.CategoryIds];}
        }
        foreach(var game in doc.Games)
        {
            if(packages.Any(p=>p.Id==game.Id))continue;
            var paths=inventory.Locations.GetValueOrDefault(game.Id);
            var path=paths?.Count==1?paths[0]:Path.Combine(libraryRoot,game.Folder);
            if(packages.Any(p=>GameScanner.Within(path,p.Source)||GameScanner.Within(p.Source,path)))continue;
            var p=new Package {IsReal=true,Id=game.Id,Source=path,SearchRoot=Path.GetDirectoryName(path)!,DetectedRoot=path,Folder=Path.GetFileName(path),Name=Path.GetFileName(path),Alias=game.Alias,IsContent=game.IsContent,ContentOwnerId=game.ContentOwnerId,CompositionParentId=game.CompositionParentId,Genres=[..game.Genres],Categories=[..game.Categories],GenreIds=[..game.GenreIds],CategoryIds=[..game.CategoryIds]};
            var executable=game.Executables.FirstOrDefault();if(executable!=null)p.DetectedRoot=Path.GetDirectoryName(Path.Combine(path,executable))!;
            packages.Add(p);
            if(paths?.Count==1)checkpoints.Add(WorkspaceReconciler.Capture(p,scan));
        }
        foreach(var p in packages)
        {
            if(!inventory.Locations.TryGetValue(p.Id,out var paths)||paths.Count!=1)continue;
            var next=paths[0];if(next.Equals(p.Source,StringComparison.OrdinalIgnoreCase))continue;
            if(packages.Any(q=>q!=p&&(GameScanner.Within(next,q.Source)||GameScanner.Within(q.Source,next))))continue;
            var relative=Path.GetRelativePath(p.Source,p.DetectedRoot);var oldFolder=p.Folder;
            p.ScopeHistory.Clear();p.Source=next;p.SearchRoot=Path.GetDirectoryName(next)!;p.DetectedRoot=relative=="."?next:Path.Combine(next,relative);p.Folder=Path.GetFileName(next);
            if(!p.Planned||p.Name==oldFolder)p.Name=p.Folder;
            var baseline=checkpoints.Find(c=>c.PackageId==p.Id);if(baseline!=null)baseline.Source=next;
        }
        foreach(var marker in inventory.Locations.Where(pair=>pair.Value.Count==1))
        {
            var root=marker.Value[0];if(root.Equals(libraryRoot,StringComparison.OrdinalIgnoreCase)||packages.Any(p=>p.Id==marker.Key||GameScanner.Within(root,p.Source)||GameScanner.Within(p.Source,root)))continue;
            var p=new Package {IsReal=true,Id=marker.Key,Source=root,SearchRoot=Path.GetDirectoryName(root)!,DetectedRoot=root,Folder=Path.GetFileName(root),Name=Path.GetFileName(root)};
            packages.Add(p);checkpoints.Add(WorkspaceReconciler.Capture(p,scan));
        }
    }
    public static void Flag(Package p,IdentityInventory inventory,LibraryDocument doc)
    {
        if(p.Excluded)return;
        var problem=inventory.Problems.FirstOrDefault(e=>e.Path.Equals(p.Source,StringComparison.OrdinalIgnoreCase));
        if(problem!=null){p.ReviewState="identity";p.ReviewReason="식별 파일 확인 필요 · "+problem.Message;return;}
        var pair=inventory.Locations.FirstOrDefault(pair=>pair.Value.Any(path=>path.Equals(p.Source,StringComparison.OrdinalIgnoreCase)));
        if(pair.Value!=null&&(pair.Value.Count>1||pair.Key!=p.Id))
        {p.ReviewState="identity";p.ReviewReason=pair.Value.Count>1?"같은 ID의 복사본이 있습니다 · 보관함에서 구분하세요":"기존 등록과 게임 ID가 다릅니다 · 보관함에서 확인하세요";return;}
        if(doc.Games.Any(g=>g.Id==p.Id)&&!inventory.Locations.ContainsKey(p.Id)&&p.ReviewState is not ("missing" or "unverified" or "paused" or "offline"))
        {p.ReviewState="identity";p.ReviewReason="게임 식별 파일이 없습니다 · 보관함에서 다시 등록하세요";}
    }
}

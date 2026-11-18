using System.IO;
namespace IndieRun;
public static class RegisteredTree
{
    public static bool ExistingChild(Package parent,Package child,string oldRoot,string nextRoot)
    {
        if(parent.Id==child.Id||parent.Registered==false||child.Registered==false||child.Source.Equals(oldRoot,StringComparison.OrdinalIgnoreCase)||!GameScanner.Within(child.Source,oldRoot)||!parent.Includes(child.Source)||!GameScanner.Within(child.Source,nextRoot))return false;
        try{return LibraryStore.ReadIdentity(child.Source)?.GameId==child.Id;}
        catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException){return false;}
    }
    public static bool TryRelocation(Package parent,string target,IEnumerable<Package> packages,Dictionary<string,string> before,IdentityInventory inventory,ScanSnapshot scan,out Dictionary<string,string> map)
    {
        map=new(StringComparer.OrdinalIgnoreCase);var old=before.GetValueOrDefault(parent.Id,parent.Source);
        if(!inventory.Locations.TryGetValue(parent.Id,out var roots)||roots.Count!=1||!roots[0].Equals(target,StringComparison.OrdinalIgnoreCase)||Directory.Exists(old)&&!old.Equals(target,StringComparison.OrdinalIgnoreCase))return false;
        var known=packages.ToDictionary(p=>p.Id,StringComparer.OrdinalIgnoreCase);
        foreach(var pair in inventory.Locations.Where(pair=>pair.Key!=parent.Id&&pair.Value.Any(path=>GameScanner.Within(path,target))))
        {
            if(pair.Value.Count!=1||!known.TryGetValue(pair.Key,out var child))return false;
            var source=before.GetValueOrDefault(child.Id,child.Source);
            if(source.Equals(old,StringComparison.OrdinalIgnoreCase)||!GameScanner.Within(source,old))return false;
            var next=Path.Combine(target,Path.GetRelativePath(old,source));
            if(!pair.Value[0].Equals(next,StringComparison.OrdinalIgnoreCase)||Directory.Exists(source)&&!source.Equals(next,StringComparison.OrdinalIgnoreCase))return false;
            map[child.Id]=next;
        }
        foreach(var child in known.Values)
        {
            var source=before.GetValueOrDefault(child.Id,child.Source);if(child.Id==parent.Id||source.Equals(old,StringComparison.OrdinalIgnoreCase)||!GameScanner.Within(source,old))continue;
            var next=Path.Combine(target,Path.GetRelativePath(old,source));
            if(inventory.Locations.TryGetValue(child.Id,out var locations)&&(locations.Count!=1||!locations[0].Equals(next,StringComparison.OrdinalIgnoreCase)))return false;
            if(Directory.Exists(source)&&!source.Equals(next,StringComparison.OrdinalIgnoreCase))return false;
            if(scan.ContainsDirectory(next))map[child.Id]=next;
        }
        map[parent.Id]=target;return true;
    }
    public static void Apply(List<Package> packages,List<FolderCheckpoint> checks,Dictionary<string,string> map,ScanSnapshot scan)
    {
        foreach(var pair in map)
        {
            var p=packages.Single(p=>p.Id==pair.Key);if(p.Source.Equals(pair.Value,StringComparison.OrdinalIgnoreCase))continue;
            var relative=Path.GetRelativePath(p.Source,p.DetectedRoot);var folder=p.Folder;
            p.Source=pair.Value;p.DetectedRoot=Path.Combine(pair.Value,relative);p.SearchRoot=FolderPolicy.Boundary(p,pair.Value,scan.SearchRoots);p.ScopeHistory.Clear();p.CompositionMoveState="Reconnected:"+Guid.NewGuid().ToString("N");p.Done=false;p.Folder=Path.GetFileName(pair.Value);if(!p.Planned||p.Name==folder)p.Name=p.Folder;
            foreach(var c in checks.Where(c=>c.PackageId==p.Id))c.Source=p.Source;
        }
        ContentLinks.Normalize(packages);
    }
}

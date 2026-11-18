using System.IO;
namespace IndieRun;
public static class IdentityProbe
{
    // Probe already known marker locations only. This never searches arbitrary drives or writes a marker.
    public static void RefreshKnown(IdentityInventory inventory,IEnumerable<string> known,IEnumerable<string> freshRoots,CancellationToken token=default)
    {
        var roots=freshRoots.ToArray();
        foreach(var pair in inventory.Locations.ToList())
        {pair.Value.RemoveAll(path=>!roots.Any(root=>GameScanner.Within(path,root)));if(pair.Value.Count==0)inventory.Locations.Remove(pair.Key);}
        inventory.Problems.RemoveAll(problem=>!roots.Any(root=>GameScanner.Within(problem.Path,root)));
        foreach(var path in known.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();if(roots.Any(root=>GameScanner.Within(path,root)))continue;
            try
            {
                var marker=LibraryStore.ReadIdentity(path);if(marker==null)continue;
                if(!inventory.Locations.TryGetValue(marker.GameId,out var paths))inventory.Locations[marker.GameId]=paths=[];
                if(!paths.Contains(path,StringComparer.OrdinalIgnoreCase))paths.Add(path);
            }
            catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
            {inventory.Problems.Add(new(path,ex.Message));}
        }
    }
}

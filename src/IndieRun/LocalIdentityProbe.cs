using System.Diagnostics;
using System.IO;
namespace IndieRun;
// Only probes sibling paths at the old relative depth. Never performs a recursive disk search.
public static class LocalIdentityProbe
{
    public static Dictionary<string,string> Find(IEnumerable<string> roots,IEnumerable<Package> games,IEnumerable<string> excluded,CancellationToken token=default)
    {
        var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);var blocked=excluded.ToArray();var timer=Stopwatch.StartNew();int visited=0;
        foreach(var root in roots.Where(r=>!Directory.Exists(r)))
        {
            var ancestor=Path.GetDirectoryName(root);while(ancestor!=null&&!Directory.Exists(ancestor))ancestor=Path.GetDirectoryName(ancestor);
            if(ancestor==null||!FolderPolicy.RootAllowed(ancestor)||blocked.Any(b=>GameScanner.Within(ancestor,b)))continue;
            var guesses=new HashSet<string>(StringComparer.OrdinalIgnoreCase);bool ambiguous=false;
            foreach(var game in games.Where(p=>p.Registered!=false&&!Directory.Exists(p.Source)&&GameScanner.Within(p.Source,root)))
            {
                var tail=Path.GetRelativePath(ancestor,game.Source).Split(Path.DirectorySeparatorChar);if(tail.Length==0)continue;
                var suffix=string.Join(Path.DirectorySeparatorChar,tail.Skip(1));var rootDepth=Path.GetRelativePath(ancestor,root).Split(Path.DirectorySeparatorChar).Length;
                try
                {
                    var hits=new List<string>();
                    foreach(var sibling in Directory.EnumerateDirectories(ancestor))
                    {
                        token.ThrowIfCancellationRequested();if(++visited>256||timer.ElapsedMilliseconds>2000)return result;
                        if(blocked.Any(b=>GameScanner.Within(sibling,b))||(File.GetAttributes(sibling)&FileAttributes.ReparsePoint)!=0)continue;
                        var candidate=suffix.Length==0?sibling:Path.Combine(sibling,suffix);
                        try{BackupPaths.NoLinks(candidate);if(LibraryStore.ReadIdentity(candidate)?.GameId==game.Id)hits.Add(candidate);}catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException){}
                    }
                    if(hits.Count>1){ambiguous=true;break;}if(hits.Count==0)continue;
                    var next=hits[0];for(int i=rootDepth;i<tail.Length;i++)next=Path.GetDirectoryName(next)!;guesses.Add(next);
                }
                catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
            }
            if(!ambiguous&&guesses.Count==1)result[root]=guesses.Single();
        }
        return result;
    }
}

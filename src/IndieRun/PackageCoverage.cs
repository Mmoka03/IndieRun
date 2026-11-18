using System.IO;
namespace IndieRun;
// Snapshot inclusion rules; add newly discovered packages without rescanning every other candidate.
public sealed class PackageCoverage
{
    readonly Dictionary<string,List<(Func<string,bool> Includes,bool Owns)>> roots=new(StringComparer.OrdinalIgnoreCase);
    public PackageCoverage(IEnumerable<Package> packages){foreach(var p in packages)Add(p);}
    public void Add(Package p)
    {
        var root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.Source));
        if(!roots.TryGetValue(root,out var rules))roots[root]=rules=[];
        rules.Add((p.Inclusion(),FolderPolicy.OwnsScope(p)));
    }
    public bool Contains(string path)
    {
        path=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        for(var ancestor=path;ancestor!=null;ancestor=Path.GetDirectoryName(ancestor))if(roots.TryGetValue(ancestor,out var rules)&&rules.Any(rule=>(ancestor.Equals(path,StringComparison.OrdinalIgnoreCase)||rule.Owns)&&rule.Includes(path)))return true;
        return false;
    }
}

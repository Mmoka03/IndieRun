using System.IO;
namespace IndieRun;
public sealed record PreparedScan(List<Package> Packages,List<FolderCheckpoint> Checkpoints,LibraryDocument Library,IdentityInventory Inventory,List<TagDefinition> Tags);
public static class ScanPreparation
{
    public static PreparedScan Build(ScanSnapshot scan,List<Package> packages,List<FolderCheckpoint> checkpoints,List<TagDefinition> tags,string destination,CancellationToken token)
    {
        scan.PrepareIndex(token);
        var library=new LibraryStore(destination).Load();var inventory=LibraryStore.Inspect(scan,token);var store=new LibraryStore(destination);
        token.ThrowIfCancellationRequested();store.Reconcile(library,inventory,scan,token);
        foreach(var tag in library.Tags)if(!tags.Any(t=>t.Id==tag.Id||t.Kind==tag.Kind&&t.Name==tag.Name))tags.Add(tag);
        IdentityReconciler.Rebind(packages,checkpoints,library,inventory,scan,destination);
        var byId=checkpoints.ToDictionary(c=>c.PackageId);
        foreach(var p in packages){token.ThrowIfCancellationRequested();WorkspaceReconciler.Check(p,byId.GetValueOrDefault(p.Id),scan);}
        var roots=packages.Select(p=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.Source))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var p in WorkspaceReconciler.Discover(scan,packages,token))
        {
            token.ThrowIfCancellationRequested();IdentityReconciler.ExpandCandidate(p,inventory,destination);
            if(GameScanner.CoveredBy(p.DetectedRoot,roots))continue;
            roots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.Source)));
            p.Color=new[]{"#527BDC","#548B75","#9471BF","#C18554","#547F93"}[packages.Count%5];
            var baseline=WorkspaceReconciler.Capture(p,scan);packages.Add(p);checkpoints.Add(baseline);
            if(scan.UnreadablePaths.Any(path=>GameScanner.Within(path,p.Source)||GameScanner.Within(p.Source,path))){p.ReviewState="unverified";p.ReviewReason="일부 파일을 읽지 못했습니다";}
        }
        ContentLinks.Normalize(packages);
        foreach(var p in packages){token.ThrowIfCancellationRequested();IdentityReconciler.Flag(p,inventory,library);}
        return new(packages,checkpoints,library,inventory,tags);
    }
}

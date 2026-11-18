using System.IO;
namespace IndieRun;
public sealed record PreparedScan(List<Package> Packages,List<FolderCheckpoint> Checkpoints,LibraryDocument Library,IdentityInventory Inventory,List<TagDefinition> Tags);
public static class ScanPreparation
{
    public static PreparedScan Build(ScanSnapshot scan,List<Package> packages,List<FolderCheckpoint> checkpoints,List<TagDefinition> tags,string destination,CancellationToken token,ISet<string>? protectedIds=null,IReadOnlyDictionary<string,(string Source,string Destination)>? confirmedMoves=null)
    {
        scan.PrepareIndex(token);
        FolderPolicy.RestoreAutomaticExclusions(packages);
        var layout=FolderPolicy.CandidateBranches(scan,packages.Select(p=>p.Source));
        foreach(var candidate in packages)FolderPolicy.ApplyCandidateBoundaries(candidate,scan,layout,protectedIds);
        var library=new LibraryStore(destination).Load();var inventory=LibraryStore.Inspect(scan,token);var store=new LibraryStore(destination);
        store.ApplyObservedLocations(library,packages,inventory,confirmedMoves);
        token.ThrowIfCancellationRequested();
        foreach(var tag in library.Tags)if(!tags.Any(t=>t.Id==tag.Id||t.Kind==tag.Kind&&t.Name==tag.Name))tags.Add(tag);
        IdentityReconciler.Rebind(packages,checkpoints,library,inventory,scan,destination,protectedIds);
        store.Reconcile(library,inventory,scan,token);
        IdentityReconciler.AlignLocations(packages,library,inventory);
        foreach(var parent in packages.Where(p=>p.Registered!=false).ToList())FolderPolicy.AbsorbOwnedCandidates(packages,checkpoints,parent,protectedIds);
        var byId=checkpoints.ToDictionary(c=>c.PackageId);
        foreach(var p in packages){token.ThrowIfCancellationRequested();WorkspaceReconciler.Check(p,byId.GetValueOrDefault(p.Id),scan);}
        var coverage=new PackageCoverage(packages);
        foreach(var p in WorkspaceReconciler.Discover(scan,packages,token))
        {
            token.ThrowIfCancellationRequested();IdentityReconciler.ExpandCandidate(p,inventory,destination);
            if(coverage.Contains(p.DetectedRoot))continue;coverage.Add(p);
            p.Color=new[]{"#527BDC","#548B75","#9471BF","#C18554","#547F93"}[packages.Count%5];
            var baseline=WorkspaceReconciler.Capture(p,scan);packages.Add(p);checkpoints.Add(baseline);
            if(WorkspaceReconciler.HasUnreadable(p,scan)){p.ReviewState="unverified";p.ReviewReason="일부 파일을 읽지 못했습니다";}
        }
        ContentLinks.Normalize(packages);
        foreach(var p in packages){token.ThrowIfCancellationRequested();IdentityReconciler.Flag(p,inventory,library);}
        return new(packages,checkpoints,library,inventory,tags);
    }
}

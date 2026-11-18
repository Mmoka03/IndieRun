using System.IO;
using System.Text.Json;
namespace IndieRun;

// Preparation only copies artwork. The durable edit journal switches all metadata together.
public static class LibraryLocation
{
    static T Copy<T>(T value)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    public static EditBundle Prepare(WorkspaceDocument plan,TagCatalog tags,PlayerDocument play,LibraryDocument library,string target)
    {
        WorkspaceStore.Validate(plan);PlayerStore.Validate(play);LibraryStore.Validate(library);
        target=Path.GetFullPath(target);BackupPaths.NoLinks(target);
        if(plan.Packages.Any(p=>GameScanner.Within(target,p.Source)))throw new IOException("게임 폴더 안은 보관함 위치로 지정할 수 없습니다.");
        var store=new LibraryStore(target);var merged=store.Load();var settings=new PlayerStore(target).Load();
        foreach(var game in library.Games)
        {
            var prior=merged.Games.Find(g=>g.Id==game.Id);
            if(prior!=null&&!prior.LastKnownPath.Equals(game.LastKnownPath,StringComparison.OrdinalIgnoreCase))throw new IOException("같은 ID의 게임이 다른 위치에 등록되어 있습니다. 다른 보관함을 선택하세요.");
            var next=Copy(game);next.Status=Directory.Exists(next.LastKnownPath)?GameScanner.Within(next.LastKnownPath,target)?"Present":"External":"Missing";
            if(next.Status=="Present")next.Folder=Path.GetRelativePath(target,next.LastKnownPath);
            merged.Games.RemoveAll(g=>g.Id==next.Id);merged.Games.Add(next);
        }
        foreach(var tag in tags.Tags)
        {
            var prior=merged.Tags.Find(t=>t.Id==tag.Id);
            if(prior!=null&&(prior.Kind!=tag.Kind||prior.Name!=tag.Name)||merged.Tags.Any(t=>t.Id!=tag.Id&&t.Kind==tag.Kind&&t.Name.Equals(tag.Name,StringComparison.OrdinalIgnoreCase)))throw new IOException("선택한 보관함의 태그와 충돌합니다. 다른 폴더를 선택하세요.");
            if(prior==null)merged.Tags.Add(Copy(tag));
        }
        Directory.CreateDirectory(target);
        // Fail before recording an intent when the destination is not writable.
        var probe=Path.Combine(target,".indierun-write-"+Guid.NewGuid().ToString("N"));
        using(var output=new FileStream(probe,FileMode.CreateNew,FileAccess.Write,FileShare.None,1,FileOptions.DeleteOnClose)){output.WriteByte(0);output.Flush(true);}
        foreach(var game in play.Games)
        {
            var next=Copy(game);
            if(next.Image.Length>0)
            {
                var original=BackupPaths.Child(plan.Destination,next.Image);BackupPaths.NoLinks(original);
                next.Image=".indierun-art/"+Guid.NewGuid().ToString("N")+Path.GetExtension(original);
                if(File.Exists(original))
                {
                    var image=BackupPaths.Child(target,next.Image);BackupPaths.NoLinks(image);Directory.CreateDirectory(Path.GetDirectoryName(image)!);File.Copy(original,image,false);
                }
            }
            settings.Games.RemoveAll(g=>g.Id==next.Id);settings.Games.Add(next);
        }
        var nextPlan=Copy(plan);nextPlan.Destination=target;
        if(Directory.Exists(plan.Destination)&&plan.Packages.Any(p=>GameScanner.Within(p.Source,plan.Destination)))
        {
            nextPlan.SearchLocations??=[new(){Path=plan.SearchRoot}];
            if(!nextPlan.SearchLocations.Any(l=>l.Enabled&&GameScanner.Within(plan.Destination,l.Path)))nextPlan.SearchLocations.Add(new(){Path=plan.Destination});
        }
        var nextTags=Copy(tags);foreach(var tag in merged.Tags)if(!nextTags.Tags.Any(t=>t.Id==tag.Id))nextTags.Tags.Add(Copy(tag));nextPlan.Tags=nextTags.Tags;
        return new EditBundle{Plan=nextPlan,Tags=nextTags,Play=settings,LibrarySeed=merged};
    }
}

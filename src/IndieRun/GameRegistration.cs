using System.IO;
namespace IndieRun;

public sealed record RegistrationResult(bool LocalOnly,string Id,LibraryUpdate? Library);
public static class GameRegistration
{
    public static bool IsAdded(Package p)=>p.Registered!=false;
    public static RegistrationResult Add(Package p,IEnumerable<TagDefinition> tags,ScanSnapshot scan,string libraryRoot,bool allowLocal=true,bool allowMissingIdentity=false)
    {
        if(p.Excluded||!p.IsReal||p.ReviewState.Length>0&&!(allowMissingIdentity&&p.ReviewState=="identity"))throw new IOException("현재 폴더 상태를 먼저 확인하세요.");
        GameLaunch.RequireFolder(p.Source,LaunchProblem.FolderMissing);
        if(GameScanner.Within(libraryRoot,p.Source))throw new IOException("보관함 전체를 게임으로 추가할 수 없습니다.");
        var inventory=LibraryStore.Inspect(scan);var marker=LibraryStore.ReadIdentity(p.Source);
        if(inventory.Problems.Any(i=>i.Path.Equals(p.Source,StringComparison.OrdinalIgnoreCase)))throw new IOException("식별 파일을 먼저 확인하세요.");
        var id=marker?.GameId??p.Id;
        if(inventory.Locations.TryGetValue(id,out var locations)&&locations.Any(path=>!path.Equals(p.Source,StringComparison.OrdinalIgnoreCase)))throw new IOException("같은 ID의 다른 폴더가 있습니다. 보관함에서 확인하세요.");
        try{LibraryStore.EnsureIdentity(p.Source,p.Id);}
        catch(UnauthorizedAccessException) when(allowLocal&&marker==null)
        {
            // No marker may have appeared concurrently; an unreadable/new marker must not be bypassed.
            if(LibraryStore.ReadIdentity(p.Source)!=null)throw;
            return new(true,p.Id,null);
        }
        var update=new LibraryStore(libraryRoot).Register(p,tags,scan);
        return new(false,LibraryStore.ReadIdentity(p.Source)!.GameId,update);
    }
}

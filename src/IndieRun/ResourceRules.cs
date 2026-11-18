using System.IO;
using System.Text.Json;
namespace IndieRun;
public sealed record ResourceEntry(string Id,string Path,string Title,bool External,int Owners)
{
    public string Kind=>Owners>1?"공유 자료":External?"연결된 자료":"본편 내 자료";
}
public static class ResourceRules
{
    static T Copy<T>(T value)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    public static List<ResourceEntry> Entries(Package p,IEnumerable<Package> all)=>p.Contents.Where(c=>c.Role=="related").Select(c=>new ResourceEntry(c.Id,System.IO.Path.Combine(p.Source,c.Path),ContentLinks.Title(c),false,1)).Concat(p.RelatedFolders.Select(r=>new ResourceEntry(r.Id,r.Source,ContentLinks.Title(r),true,ContentLinks.Owners(all,r.Id).Count))).ToList();
    public static bool CanOpen(string path)=>ReadOnlyPreview.IsText(path)||ReadOnlyPreview.IsImage(path)||new[]{".pdf",".epub",".mp3",".ogg",".wav",".mp4",".webm"}.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());
    public static List<Package> Alias(List<Package> current,string ownerId,ResourceEntry entry,string alias)
    {
        alias=alias.Trim();if(alias.Length>120||alias.Any(char.IsControl))throw new IOException("표시 이름은 120자 이내로 입력하세요.");var next=Copy(current);var owner=next.Single(p=>p.Id==ownerId);
        if(entry.External){foreach(var p in next)foreach(var r in p.RelatedFolders.Where(r=>r.Id==entry.Id))r.Alias=alias;var asset=next.Find(p=>p.Id==entry.Id);if(asset!=null)asset.Alias=alias;}
        else owner.Contents.Single(c=>c.Id==entry.Id&&c.Role=="related").Alias=alias;return next;
    }
    public static List<Package> Detach(List<Package> current,string ownerId,ResourceEntry entry)
    {
        var next=Copy(current);var owner=next.Single(p=>p.Id==ownerId);if(entry.External)owner.RelatedFolders.RemoveAll(r=>r.Id==entry.Id);else owner.Contents.RemoveAll(c=>c.Id==entry.Id&&c.Role=="related");ContentLinks.Normalize(next);return next;
    }
    public static List<Package> RemoveGame(List<Package> current,string id)
    {
        var next=Copy(current);var p=next.Single(p=>p.Id==id);p.Excluded=true;p.Registered=false;p.Planned=false;p.RelatedFolders.Clear();ContentLinks.Normalize(next);return next;
    }
    public static List<Package> Relink(List<Package> current,string id,string path)
    {
        path=System.IO.Path.GetFullPath(path);BackupPaths.NoLinks(path);if(!Directory.Exists(path))throw new IOException("자료 폴더를 선택하세요.");var original=current.Find(p=>p.Id==id&&p.IsRelatedContent);if(original==null){var link=current.SelectMany(p=>p.RelatedFolders).FirstOrDefault(r=>r.Id==id)??throw new IOException("자료 연결을 찾지 못했습니다.");original=new(){Id=id,IsReal=true,IsContent=true,Registered=false,Source=link.Source,DetectedRoot=link.Source,SearchRoot=System.IO.Path.GetDirectoryName(link.Source)!,Folder=link.Name,Name=link.Name,Alias=link.Alias};}
        if(current.Any(p=>p.Id!=id&&(p.Includes(path)||GameScanner.Within(p.Source,path))))throw new IOException("다른 게임이나 자료와 범위가 겹칩니다.");
        var marker=LibraryStore.ReadIdentity(path);if(marker!=null&&marker.GameId!=id||GameRegistration.IsAdded(original)&&marker==null)throw new IOException("원래 자료의 식별 파일을 확인하세요. 다른 자료로 자동 연결하지 않습니다.");
        if(Directory.Exists(original.Source)&&!original.Source.Equals(path,StringComparison.OrdinalIgnoreCase)&&LibraryStore.ReadIdentity(original.Source)?.GameId==id)throw new IOException("원래 위치에도 같은 자료가 있습니다. 복사본인지 먼저 확인하세요.");
        var next=Copy(current);var asset=next.Find(p=>p.Id==id);if(asset==null){asset=Copy(original);next.Add(asset);}asset.Source=path;asset.DetectedRoot=path;asset.SearchRoot=System.IO.Path.GetDirectoryName(path)!;asset.Folder=System.IO.Path.GetFileName(path);asset.Name=asset.Folder;asset.ScopeHistory.Clear();ContentLinks.Normalize(next);return next;
    }
}

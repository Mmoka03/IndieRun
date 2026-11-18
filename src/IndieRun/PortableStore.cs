using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
namespace IndieRun;

public sealed class PortableGame
{
    public StoredScope? Scope {get;set;}
    public string Format {get;set;}="IndieRun.PortableGame";
    public int Version {get;set;}=1;
    public string Id {get;set;}="";
    public string Folder {get;set;}="";
    public string Alias {get;set;}="";
    public List<TagDefinition> Tags {get;set;}=[];
    public List<string> GenreIds {get;set;}=[];
    public List<string> CategoryIds {get;set;}=[];
    public GamePlay Play {get;set;}=new();
    public bool IsContent {get;set;}
    public string ContentOwnerId {get;set;}="";
    public string CompositionParentId {get;set;}="";
    public List<FolderAssignment> Contents {get;set;}=[];
    public List<RelatedFolder> RelatedFolders {get;set;}=[];
    public string Artwork {get;set;}="";
    public string ArtworkHash {get;set;}="";
}
public static class PortableStore
{
    public const string DirectoryName=GameScanner.MetadataDirectoryName;
    static T Copy<T>(T value)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    static string Container(string parent,string id){if(!BackupPaths.Id(id))throw new InvalidDataException("게임 ID를 확인하세요.");return Path.Combine(Path.GetFullPath(parent),DirectoryName,id);}
    static void Validate(PortableGame p)
    {
        if(p.Format!="IndieRun.PortableGame"||p.Version!=1||!BackupPaths.Id(p.Id)||!BackupPaths.Relative(p.Folder)||p.Folder.IndexOfAny(['\\','/'])>=0||p.Alias==null||p.Tags==null||p.GenreIds==null||p.CategoryIds==null||p.Play==null||p.Play.Id!=p.Id||p.Artwork==null||p.ArtworkHash==null||(p.Play.Image==null||p.Play.Image.Length>0))throw new InvalidDataException("게임 정보 파일을 확인하세요.");
        if(p.Contents==null||p.RelatedFolders==null||p.ContentOwnerId==null||p.CompositionParentId==null||p.ContentOwnerId.Length>0&&!BackupPaths.Id(p.ContentOwnerId)||p.CompositionParentId.Length>0&&!BackupPaths.Id(p.CompositionParentId)||p.Contents.Count>1000||p.RelatedFolders.Count>1000||p.Contents.Any(c=>c==null||c.Alias==null||c.Alias.Length>120||!BackupPaths.Id(c.Id)||!BackupPaths.Relative(c.Path)||c.Role!="related")||p.RelatedFolders.Any(r=>r==null||r.Alias==null||r.Alias.Length>120||r.Source==null||r.Name==null||!BackupPaths.Id(r.Id)||BackupEngine.MoveNameError(r.Name)!=null||r.Source.Length>0))throw new InvalidDataException("콘텐츠 연결 정보를 확인하세요.");
        StoredScope.Validate(p.Scope);PlayerStore.Validate(new(){Games=[p.Play]});
        if(p.Tags.Any(t=>t==null||!BackupPaths.Id(t.Id)||string.IsNullOrWhiteSpace(t.Name)||t.Name.Length>40||t.Name.Any(char.IsControl)||t.Kind is not ("genre" or "category"))||p.Tags.GroupBy(t=>t.Id).Any(g=>g.Count()>1)||p.GenreIds.Any(id=>!p.Tags.Any(t=>t.Id==id&&t.Kind=="genre"))||p.CategoryIds.Any(id=>!p.Tags.Any(t=>t.Id==id&&t.Kind=="category")))throw new InvalidDataException("게임 태그 정보를 확인하세요.");
        if(p.Artwork.Length>0&&(!BackupPaths.Relative(p.Artwork)||p.Artwork.IndexOfAny(['\\','/'])>=0||!ReadOnlyPreview.IsImage(p.Artwork)||p.ArtworkHash.Length!=64||!p.ArtworkHash.All(Uri.IsHexDigit)))throw new InvalidDataException("이미지 경로를 확인하세요.");
    }
    static string Hash(string path){using var f=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(f));}
    public static void Export(string targetParent,string folder,Package p,GamePlay play,TagCatalog catalog,string settingsRoot)
    {
        var container=Container(targetParent,p.Id);BackupPaths.NoLinks(container);var path=Path.Combine(container,"game.json");
        if(File.Exists(path)&&BackupJson.Load<PortableGame>(path,Validate).Id!=p.Id)throw new IOException("다른 게임의 정보 파일은 덮어쓰지 않습니다."); // Never silently replace a foreign/corrupt document.
        var used=catalog.Tags.Where(t=>t.Kind=="genre"?p.Genres.Contains(t.Name):p.Categories.Contains(t.Name)).Select(Copy).ToList();
        var record=new PortableGame {Id=p.Id,Folder=folder,Alias=p.Alias,Tags=used,GenreIds=used.Where(t=>t.Kind=="genre").Select(t=>t.Id).ToList(),CategoryIds=used.Where(t=>t.Kind=="category").Select(t=>t.Id).ToList(),Play=Copy(play)};record.Scope=StoredScope.Capture(p);record.IsContent=p.IsRelatedContent;record.ContentOwnerId=p.ContentOwnerId;record.CompositionParentId=p.CompositionParentId;record.Contents=Copy(p.Contents.Where(c=>c.Role=="related").ToList());record.RelatedFolders=p.RelatedFolders.Select(r=>new RelatedFolder{Id=r.Id,Name=r.Name,Alias=r.Alias,Together=r.Together}).ToList();record.Play.Id=p.Id;record.Play.Image="";
        if(play.Image.Length>0)
        {
            var image=BackupPaths.Child(settingsRoot,play.Image);BackupPaths.NoLinks(image);var info=new FileInfo(image);if(!ReadOnlyPreview.IsImage(image)||!info.Exists||info.Length>50L*1024*1024)throw new IOException("게임 이미지를 확인하세요. 정보 파일은 유지했습니다.");
            var hash=Hash(image);record.Artwork=hash+Path.GetExtension(image).ToLowerInvariant();record.ArtworkHash=hash;
            Directory.CreateDirectory(container);var target=Path.Combine(container,record.Artwork);BackupPaths.NoLinks(target);
            if(!File.Exists(target)){var temp=target+".tmp-"+Guid.NewGuid().ToString("N");BackupPaths.NoLinks(temp);File.Copy(image,temp,false);if(Hash(temp)!=hash)throw new IOException("이미지 복사 검증에 실패했습니다.");File.Move(temp,target,false);}
            else if(Hash(target)!=hash)throw new IOException("보관된 이미지가 변경되어 덮어쓰지 않았습니다.");
        }
        Validate(record);
        if(File.Exists(path)&&JsonSerializer.Serialize(BackupJson.Load<PortableGame>(path,Validate))==JsonSerializer.Serialize(record))return;
        BackupJson.Save(path,record,Validate);
    }
    public static PortableGame? Read(string gameRoot)
    {
        var marker=LibraryStore.ReadIdentity(gameRoot);if(marker==null)return null;var parent=Path.GetDirectoryName(Path.GetFullPath(gameRoot))!;var path=Path.Combine(Container(parent,marker.GameId),"game.json");
        BackupPaths.NoLinks(path);if(!File.Exists(path))return null;if(new FileInfo(path).Length>1024*1024)throw new InvalidDataException("게임 정보 파일이 너무 큽니다.");
        var record=BackupJson.Load<PortableGame>(path,Validate);if(record.Id!=marker.GameId)throw new IOException("폴더와 게임 정보가 일치하지 않습니다.");
        return record;
    }
    public static GamePlay Import(PortableGame record,Package p,TagCatalog catalog,string settingsRoot)
    {
        Validate(record);if(LibraryStore.ReadIdentity(p.Source)?.GameId!=record.Id||p.Id!=record.Id)throw new IOException("게임 식별 정보가 달라 불러오지 않았습니다.");
        var tags=Copy(catalog);var mapped=new Dictionary<string,string>();
        foreach(var tag in record.Tags)
        {
            var existing=tags.Tags.Find(t=>t.Kind==tag.Kind&&t.Name.Equals(tag.Name,StringComparison.OrdinalIgnoreCase));
            if(existing==null){existing=Copy(tag);if(tags.Tags.Any(t=>t.Id==existing.Id))existing.Id=Guid.NewGuid().ToString("N");tags.Tags.Add(existing);}mapped[tag.Id]=existing.Id;
        }
        var play=Copy(record.Play);
        if(record.Artwork.Length>0)
        {
            var container=Container(Path.GetDirectoryName(p.Source)!,p.Id);var source=Path.Combine(container,record.Artwork);BackupPaths.NoLinks(source);
            if(!File.Exists(source)||new FileInfo(source).Length>50L*1024*1024||Hash(source)!=record.ArtworkHash)throw new IOException("게임 이미지를 확인하지 못했습니다. 기존 정보는 유지됩니다.");
            var name=record.Artwork;var artRoot=Path.Combine(settingsRoot,".indierun-art");BackupPaths.NoLinks(artRoot);Directory.CreateDirectory(artRoot);var target=Path.Combine(artRoot,name);BackupPaths.NoLinks(target);
            if(!File.Exists(target)){var temp=target+".tmp-"+Guid.NewGuid().ToString("N");BackupPaths.NoLinks(temp);File.Copy(source,temp,false);if(Hash(temp)!=record.ArtworkHash)throw new IOException("이미지 검증에 실패했습니다.");File.Move(temp,target,false);}else if(Hash(target)!=record.ArtworkHash)throw new IOException("기존 이미지는 덮어쓰지 않습니다.");
            play.Image=".indierun-art/"+name;
        }
        p.IsContent=record.IsContent;p.ContentOwnerId=record.ContentOwnerId;p.CompositionParentId=record.CompositionParentId;p.Contents=Copy(record.Contents);p.RelatedFolders=record.RelatedFolders.Select(r=>new RelatedFolder{Id=r.Id,Name=r.Name,Alias=r.Alias,Together=r.Together,Source=Path.Combine(Path.GetDirectoryName(p.Source)!,r.Name)}).ToList();
        StoredScope.Apply(record.Scope,p);
        p.Alias=record.Alias;p.GenreIds=record.GenreIds.Select(id=>mapped[id]).ToList();p.CategoryIds=record.CategoryIds.Select(id=>mapped[id]).ToList();p.Genres=tags.Tags.Where(t=>p.GenreIds.Contains(t.Id)).Select(t=>t.Name).ToList();p.Categories=tags.Tags.Where(t=>p.CategoryIds.Contains(t.Id)).Select(t=>t.Name).ToList();p.Registered=true;p.PortableMetadata=true;catalog.Tags=tags.Tags;return play;
    }
}

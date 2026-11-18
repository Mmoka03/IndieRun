using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace IndieRun;

public sealed record FileStamp(string RelativePath,long Length,long ModifiedTicks);
public sealed class FolderCheckpoint
{
    public string PackageId { get; set; } = "";
    public string Source { get; set; } = "";
    public List<FileStamp> Files { get; set; } = [];
    public List<string> Directories { get; set; } = [];
}
public sealed class SearchLocation
{
    public string Path {get;set;}="";
    public bool Enabled {get;set;}=true;
}
public static class SearchLocationRules
{
    public static List<string> MinimalRoots(IEnumerable<string> paths)
    {var all=paths.Select(p=>System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(p))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();return all.Where(p=>!all.Any(q=>!q.Equals(p,StringComparison.OrdinalIgnoreCase)&&GameScanner.Within(p,q))).ToList();}
    public static bool Add(List<SearchLocation> locations,string path)
    {path=System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));if(locations.Any(l=>GameScanner.Within(path,l.Path)))return false;locations.RemoveAll(l=>GameScanner.Within(l.Path,path));locations.Add(new(){Path=path});return true;}
}
public sealed class WorkspaceDocument
{
    public int Version { get; set; } = 1;
    public DateTime SavedUtc { get; set; }
    public string SearchRoot { get; set; } = "";
    public List<SearchLocation>? SearchLocations {get;set;}
    public string Destination { get; set; } = "";
    public string BackupRoot { get; set; } = "";
    public string SelectedId { get; set; } = "";
    public string Filter { get; set; } = "";
    public List<Package> Packages { get; set; } = [];
    public List<FolderCheckpoint> Checkpoints { get; set; } = [];
    public List<TagDefinition> Tags { get; set; } = [];
    public bool IdentityNoticeAccepted {get;set;}
}
public sealed record WorkspaceLoad(WorkspaceDocument? Document,bool Recovered,string? Error);

public sealed class WorkspaceStore(string file)
{
    public const long MaxFileBytes=512L*1024*1024;
    public string FilePath => file;
    static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    public FileStream AcquireLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        return new FileStream(file+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    }
    public static void Validate(WorkspaceDocument doc)
    {
        if(doc.Version!=1||doc.Packages==null||doc.Checkpoints==null||doc.Tags==null||!Path.IsPathFullyQualified(doc.SearchRoot)||!Path.IsPathFullyQualified(doc.Destination)||doc.Filter==null||doc.SelectedId==null)throw new InvalidDataException("지원하지 않는 계획 형식입니다.");
        if(doc.SearchLocations!=null&&(doc.SearchLocations.Count>1000||doc.SearchLocations.Any(l=>l==null||!Path.IsPathFullyQualified(l.Path))||doc.SearchLocations.GroupBy(l=>Path.GetFullPath(l.Path),StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1)))throw new InvalidDataException("탐색 위치를 확인하세요.");
        if(doc.BackupRoot==null||(doc.BackupRoot.Length>0&&!Path.IsPathFullyQualified(doc.BackupRoot)))throw new InvalidDataException("백업 위치가 올바르지 않습니다.");
        if(doc.Packages.Count>100000||doc.Packages.Any(p=>p==null||string.IsNullOrWhiteSpace(p.Id)||!p.IsReal||!Path.IsPathFullyQualified(p.SearchRoot)||!Path.IsPathFullyQualified(p.Source)||!Path.IsPathFullyQualified(p.DetectedRoot)||!GameScanner.Within(p.Source,p.SearchRoot)||!GameScanner.Within(p.DetectedRoot,p.Source)||p.Name==null||p.Folder==null||p.Alias==null||p.Genres==null||p.Categories==null||p.GenreIds==null||p.CategoryIds==null||p.Genres.Any(n=>n==null)||p.Categories.Any(n=>n==null)||p.Color==null))throw new InvalidDataException("게임 경로나 정보가 올바르지 않습니다.");
        foreach(var p in doc.Packages)p.ValidateComposition();
        if(doc.Checkpoints.Any(c=>c==null)||doc.Packages.GroupBy(p=>p.Id).Any(g=>g.Count()>1)||doc.Checkpoints.GroupBy(c=>c.PackageId).Any(g=>g.Count()>1))throw new InvalidDataException("중복 또는 잘못된 식별자가 있습니다.");
        if(doc.Packages.Any(p=>p.ScopeHistory==null||p.ScopeHistory.Count>200||p.ScopeHistory.Any(path=>!Path.IsPathFullyQualified(path)||!GameScanner.Within(path,p.SearchRoot)||!GameScanner.Within(p.DetectedRoot,path))))throw new InvalidDataException("범위 변경 기록이 올바르지 않습니다.");
        if(doc.Packages.Any(p=>!System.Text.RegularExpressions.Regex.IsMatch(p.Color,@"^#[0-9a-fA-F]{6}$")))throw new InvalidDataException("색상 기록이 올바르지 않습니다.");
        var packagesById=doc.Packages.ToDictionary(p=>p.Id,StringComparer.Ordinal);
        foreach(var checkpoint in doc.Checkpoints)
        {
            var p=packagesById.GetValueOrDefault(checkpoint.PackageId);
            if(p==null||!Path.IsPathFullyQualified(checkpoint.Source)||!checkpoint.Source.Equals(p.Source,StringComparison.OrdinalIgnoreCase)||checkpoint.Files==null||checkpoint.Directories==null||checkpoint.Files.Any(f=>f==null||f.Length<0||!SafeRelative(f.RelativePath))||checkpoint.Directories.Any(d=>!SafeRelative(d)))throw new InvalidDataException("확인 기준이 올바르지 않습니다.");
            if(checkpoint.Files.GroupBy(f=>f.RelativePath,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))throw new InvalidDataException("중복 파일 기록이 있습니다.");
        }
        if(doc.Tags.Any(t=>t==null||string.IsNullOrWhiteSpace(t.Id)||string.IsNullOrWhiteSpace(t.Name)||(t.Kind!="genre"&&t.Kind!="category")))throw new InvalidDataException("태그 기록이 올바르지 않습니다.");
    }
    static bool SafeRelative(string? path) => !string.IsNullOrWhiteSpace(path)&&!Path.IsPathRooted(path)&&!path.Split('\\','/').Any(part=>part is ".." or ".");
    WorkspaceDocument Read(string path)
    {
        var info=new FileInfo(path);if(info.Length>MaxFileBytes)throw new InvalidDataException("계획 파일이 너무 큽니다.");
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        var doc=JsonSerializer.Deserialize<WorkspaceDocument>(input)??throw new InvalidDataException("빈 계획입니다.");Validate(doc);return doc;
    }
    public WorkspaceLoad Load()
    {
        string? error=null;
        if(System.IO.File.Exists(file))
        {
            try{return new(Read(file),false,null);}
            catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException){error=ex.Message;}
        }
        if(System.IO.File.Exists(file+".bak"))
        {
            try{return new(Read(file+".bak"),true,error);}
            catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException){error??=ex.Message;}
        }
        return new(null,false,error);
    }
    public string? Archive()
    {
        if(!System.IO.File.Exists(file))return null;
        Read(file);
        var archive=file+".history-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".json";
        System.IO.File.Copy(file,archive,false);return archive;
    }
    public void Save(WorkspaceDocument doc) => Save(doc,null);
    // Deterministic crash-test seam. Normal application calls never set a checkpoint.
    internal void Save(WorkspaceDocument doc,Action<string>? checkpoint)
    {
        Validate(doc);Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp=file+".tmp";
        using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None))
        {checkpoint?.Invoke("temporary-opened");JsonSerializer.Serialize(stream,doc,JsonOptions);if(stream.Length>MaxFileBytes)throw new InvalidDataException("계획 파일이 너무 큽니다.");stream.Flush(true);}
        checkpoint?.Invoke("temporary-flushed");
        if(!System.IO.File.Exists(file)){System.IO.File.Move(temp,file);checkpoint?.Invoke("committed");return;}
        var valid=true;
        try{Read(file);}
        catch(Exception ex) when(ex is IOException or InvalidDataException or JsonException or ArgumentException or NotSupportedException){valid=false;}
        var backup=valid?file+".bak":file+".invalid-"+Guid.NewGuid().ToString("N")+".json";
        System.IO.File.Replace(temp,file,backup,true);
        checkpoint?.Invoke("committed");
    }
}


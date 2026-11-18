using System.Text.Json.Serialization;
namespace IndieRun;
public sealed class Package
{
    public bool IsContent {get;set;}
    [JsonIgnore] public bool IsRelatedContent=>IsContent||ContentOwnerId.Length>0;
    public List<FolderAssignment> Contents {get;set;}=[];
    public List<RelatedFolder> RelatedFolders {get;set;}=[];
    public List<string> OmittedFolders {get;set;}=[];
    public string CompositionMoveState {get;set;}="";
    public string PlannedContainer {get;set;}="";
    public string ContentOwnerId {get;set;}="";
    public string CompositionParentId {get;set;}="";
    public bool Includes(string path)=>GameScanner.Within(path,Source)&&!OmittedFolders.Any(r=>GameScanner.Within(path,System.IO.Path.Combine(Source,r)));
    public void ValidateComposition()
    {
        bool Relative(string r)=>!string.IsNullOrWhiteSpace(r)&&!System.IO.Path.IsPathRooted(r)&&r.Split('\\','/').All(c=>c.Length>0&&c is not ("." or "..")&&c.IndexOfAny(System.IO.Path.GetInvalidFileNameChars())<0);
        if(Contents==null||RelatedFolders==null||OmittedFolders==null||PlannedContainer==null||(PlannedContainer.Length>0&&!Relative(PlannedContainer))||ContentOwnerId==null||CompositionParentId==null||(ContentOwnerId.Length>0&&!Guid.TryParseExact(ContentOwnerId,"N",out _))||(CompositionParentId.Length>0&&!Guid.TryParseExact(CompositionParentId,"N",out _))||Contents.Count>1000||RelatedFolders.Count>1000||Contents.Any(c=>c==null||c.Alias==null||c.Alias.Length>120||!Relative(c.Path)||c.Role is not ("related" or "game" or "exclude")||!Guid.TryParseExact(c.Id,"N",out _))||OmittedFolders.Any(r=>!Relative(r))||RelatedFolders.Any(r=>r==null||r.Alias==null||r.Alias.Length>120||!Guid.TryParseExact(r.Id,"N",out _)||!System.IO.Path.IsPathFullyQualified(r.Source)||string.IsNullOrWhiteSpace(r.Name)||r.Name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars())>=0||r.Name is "." or "..")||Contents.GroupBy(c=>c.Path,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1)||RelatedFolders.GroupBy(r=>r.Id).Any(g=>g.Count()>1))throw new System.IO.InvalidDataException("게임 구성 정보를 확인하세요.");
    }
    // Null is a legacy workspace entry, which was already visible in the player.
    public bool? Registered { get; set; }
    public bool LocalRegistration { get; set; }
    public bool PortableMetadata { get; set; }
    public List<string> ScopeHistory { get; set; } = [];
    public bool Excluded { get; set; }
    public List<string> GenreIds { get; set; } = [];
    public List<string> CategoryIds { get; set; } = [];
    [JsonIgnore] public string ReviewState { get; set; } = "";
    [JsonIgnore] public string ReviewReason { get; set; } = "";
    [JsonIgnore] public List<string> ReviewDetails { get; set; } = [];
    public bool IsReal { get; set; }
    public string DetectedRoot { get; set; } = "";
    public string SearchRoot { get; set; } = "";
    public string Id { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Name { get; set; } = "";
    public string Alias { get; set; } = "";
    public string Source { get; set; } = "";
    public string Size { get; set; } = "";
    public double Gb { get; set; }
    public string Color { get; set; } = "#527BDC";
    public string ArtTitle { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Genres { get; set; } = [];
    public List<string> Categories { get; set; } = [];
    public bool Comic { get; set; }
    public bool ComicRelated { get; set; } = true;
    public bool LinkOnly { get; set; }
    public bool Multi { get; set; }
    public bool Planned { get; set; }
    public bool Done { get; set; }
    public bool Backup { get; set; }
}


public sealed class FolderAssignment
{
    public string Alias {get;set;}="";
    public string Path {get;set;}="";
    public string Role {get;set;}="related";
    public string Id {get;set;}=Guid.NewGuid().ToString("N");
}
public sealed class RelatedFolder
{
    public string Alias {get;set;}="";
    public string Id {get;set;}=Guid.NewGuid().ToString("N");
    public string Source {get;set;}="";
    public string Name {get;set;}="";
    public bool Together {get;set;}
}

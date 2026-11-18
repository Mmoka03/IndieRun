using System.Text.Json.Serialization;
namespace IndieRun;
public sealed class Package
{
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


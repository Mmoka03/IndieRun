using System.IO;
using System.Text;
using System.Text.Json;

namespace IndieRun;

public sealed class TagDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "category";
    public string Name { get; set; } = "";
}

public sealed class TagCatalog
{
    public int Version { get; set; } = 1;
    public List<TagDefinition> Tags { get; set; } = [];
    public static TagCatalog Initial() => new() { Tags = new[] { "공포", "퍼즐", "액션", "어드벤처" }.Select(n=>new TagDefinition {Kind="genre",Name=n}).ToList() };
}

public sealed class TagCatalogStore(string path)
{
    public TagCatalog Load()
    {
        if(!File.Exists(path)) return TagCatalog.Initial();
        var result=JsonSerializer.Deserialize<TagCatalog>(File.ReadAllText(path,Encoding.UTF8)) ?? throw new InvalidDataException("태그 파일이 비어 있습니다.");
        if(result.Version!=1 || result.Tags==null || result.Tags.Any(t=>t==null || string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.Name) || t.Name.Length>40 || (t.Kind!="genre" && t.Kind!="category"))) throw new InvalidDataException("지원하지 않는 태그 형식입니다.");
        if(result.Tags.GroupBy(t=>t.Id).Any(g=>g.Count()>1) || result.Tags.GroupBy(t=>(t.Kind,t.Name.ToUpperInvariant())).Any(g=>g.Count()>1)) throw new InvalidDataException("중복 태그가 있습니다.");
        return result;
    }
    public void Save(TagCatalog catalog)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary=path+".tmp";
        File.WriteAllText(temporary,JsonSerializer.Serialize(catalog,new JsonSerializerOptions {WriteIndented=true}),new UTF8Encoding(false));
        File.Move(temporary,path,true);
    }
}

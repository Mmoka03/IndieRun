using System.IO;
using System.Text;
using System.Text.Json;

namespace IndieRun;

public sealed class GameIdentity
{
    public string Format { get; set; }="IndieRun.GameIdentity";
    public int Version { get; set; }=1;
    public string GameId { get; set; }="";
}
public sealed class LibraryGame
{
    public bool IsContent {get;set;}
    public string ContentOwnerId {get;set;}="";
    public string CompositionParentId {get;set;}="";
    public string Id { get; set; }="";
    public string Folder { get; set; }="";
    public string LastKnownPath { get; set; }="";
    public string Alias { get; set; }="";
    public List<string> Genres { get; set; }=[];
    public List<string> Categories { get; set; }=[];
    public List<string> GenreIds { get; set; }=[];
    public List<string> CategoryIds { get; set; }=[];
    public List<string> Executables { get; set; }=[];
    public string Status { get; set; }="Present";
}
public sealed class LibraryDocument
{
    public string Format { get; set; }="IndieRun.Library";
    public int Version { get; set; }=1;
    public string LibraryId { get; set; }=Guid.NewGuid().ToString("N");
    public List<LibraryGame> Games { get; set; }=[];
    public List<TagDefinition> Tags { get; set; }=[];
}
public sealed record IdentityProblem(string Path,string Message);
public sealed class IdentityInventory
{
    public Dictionary<string,List<string>> Locations { get; }=new(StringComparer.OrdinalIgnoreCase);
    public List<IdentityProblem> Problems { get; }=[];
}
public sealed record LibraryUpdate(LibraryDocument Document,string? CsvError);

public sealed class LibraryStore
{
    public const string MarkerName=".indierun-id.json";
    public const string NoteName="indierun-info.txt";
    public const string JsonName="indierun-library.json";
    public const string CsvName="indierun-library.csv";
    public const string CsvHeader="GameId,Folder,Alias,Genres,Categories,Executables,Status,LastKnownPath";
    public string Root { get; }
    public LibraryStore(string root){Root=Path.GetFullPath(root);}
    static bool Fault(Exception ex)=>ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException;
    static void ValidateIdentity(GameIdentity marker)
    {if(marker.Format!="IndieRun.GameIdentity"||marker.Version!=1||!BackupPaths.Id(marker.GameId))throw new InvalidDataException("게임 식별 파일 형식을 확인해 주세요.");}
    public static GameIdentity? ReadIdentity(string folder)
    {
        var path=Path.Combine(folder,MarkerName);BackupPaths.NoLinks(path);if(!File.Exists(path))return null;
        if(new FileInfo(path).Length>4096)throw new InvalidDataException("게임 식별 파일이 너무 큽니다.");
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        var marker=JsonSerializer.Deserialize<GameIdentity>(input)??throw new InvalidDataException("빈 식별 파일입니다.");ValidateIdentity(marker);marker.GameId=Guid.Parse(marker.GameId).ToString("N");return marker;
    }
    public static string EnsureIdentity(string folder,string preferredId)
    {
        BackupPaths.NoLinks(folder);if(!Directory.Exists(folder))throw new DirectoryNotFoundException(folder);
        var old=ReadIdentity(folder);if(old!=null)return old.GameId;
        var marker=new GameIdentity {GameId=BackupPaths.Id(preferredId)?preferredId:Guid.NewGuid().ToString("N")};
        var path=Path.Combine(folder,MarkerName);BackupPaths.NoLinks(path);var temp=path+".creating-"+Guid.NewGuid().ToString("N");BackupPaths.NoLinks(temp);
        using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(output,marker);output.Flush(true);}
        try{File.Move(temp,path,false);}catch(IOException){var concurrent=ReadIdentity(folder);if(concurrent!=null)return concurrent.GameId;throw;}
        return marker.GameId;
    }
    public static string AssignCopyIdentity(string folder,string expectedId)
    {
        var previous=ReadIdentity(folder)??throw new IOException("식별 파일이 없습니다.");
        if(previous.GameId!=expectedId)throw new IOException("식별 정보가 변경됐습니다. 다시 확인하세요.");
        var marker=new GameIdentity {GameId=Guid.NewGuid().ToString("N")};
        // Preserve the former identity for review; never silently replace an unknown file.
        var path=Path.Combine(folder,MarkerName);var temp=path+".tmp-"+Guid.NewGuid().ToString("N");BackupPaths.NoLinks(temp);
        using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(output,marker);output.Flush(true);}
        if(ReadIdentity(folder)?.GameId!=expectedId)throw new IOException("식별 정보가 변경됐습니다.");
        File.Replace(temp,path,path+".previous-"+Guid.NewGuid().ToString("N"),true);return marker.GameId;
    }
    public static IdentityInventory Inspect(ScanSnapshot scan,CancellationToken token=default)
    {
        var result=new IdentityInventory();
        foreach(var file in scan.Files.Where(f=>Path.GetFileName(f.Path).Equals(MarkerName,StringComparison.OrdinalIgnoreCase)))
        {
            token.ThrowIfCancellationRequested();
            var folder=Path.GetDirectoryName(file.Path)!;
            try
            {
                var marker=ReadIdentity(folder);if(marker==null)continue;
                if(!result.Locations.TryGetValue(marker.GameId,out var paths))result.Locations[marker.GameId]=paths=[];
                if(!paths.Contains(folder,StringComparer.OrdinalIgnoreCase))paths.Add(folder);
            }
            catch(Exception ex) when(Fault(ex)){result.Problems.Add(new(folder,ex.Message));}
        }
        return result;
    }
    internal static void Validate(LibraryDocument doc)
    {
        if(doc.Format!="IndieRun.Library"||doc.Version!=1||!BackupPaths.Id(doc.LibraryId)||doc.Games==null||doc.Tags==null||doc.Games.Count>100000)throw new InvalidDataException("지원하지 않는 보관함 형식입니다.");
        if(doc.Games.Any(g=>g==null||!BackupPaths.Id(g.Id)||!BackupPaths.Relative(g.Folder)||g.LastKnownPath==null||g.ContentOwnerId==null||g.CompositionParentId==null||g.ContentOwnerId.Length>0&&!BackupPaths.Id(g.ContentOwnerId)||g.CompositionParentId.Length>0&&!BackupPaths.Id(g.CompositionParentId)||g.Alias==null||g.Genres==null||g.Categories==null||g.GenreIds==null||g.CategoryIds==null||g.Executables==null||g.Status==null||g.Executables.Any(e=>!BackupPaths.Relative(e))||g.Genres.Any(s=>s==null)||g.Categories.Any(s=>s==null)))throw new InvalidDataException("보관함 게임 정보가 올바르지 않습니다.");
        if(doc.Games.GroupBy(g=>g.Id,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1)||doc.Tags.Any(t=>t==null||string.IsNullOrWhiteSpace(t.Id)||string.IsNullOrWhiteSpace(t.Name)||t.Name.Length>40||t.Kind is not ("genre" or "category"))||doc.Tags.GroupBy(t=>t.Id).Any(g=>g.Count()>1))throw new InvalidDataException("중복 ID 또는 잘못된 태그입니다.");
    }
    public LibraryDocument Load()
    {
        var path=Path.Combine(Root,JsonName);BackupPaths.NoLinks(path);
        return File.Exists(path)||File.Exists(path+".bak")?BackupJson.Load<LibraryDocument>(path,Validate):new LibraryDocument();
    }
    public LibraryUpdate Update(Action<LibraryDocument> edit)
    {
        BackupPaths.NoLinks(Root);Directory.CreateDirectory(Root);
        var gate=Path.Combine(Root,".indierun-library.lock");BackupPaths.NoLinks(gate);
        using var held=new FileStream(gate,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var doc=Load();edit(doc);Validate(doc);BackupJson.Save(Path.Combine(Root,JsonName),doc,Validate);
        string? csvError=null;try{WriteCsv(doc);}catch(Exception ex) when(Fault(ex)){csvError=ex.Message;}
        return new(doc,csvError);
    }
    public LibraryUpdate Register(Package package,IEnumerable<TagDefinition> tags,ScanSnapshot scan)
    {
        if(GameScanner.Within(Root,package.Source))throw new IOException("보관함 전체 또는 보관함을 포함하는 폴더를 게임으로 등록할 수 없습니다.");
        var inventory=Inspect(scan);var current=ReadIdentity(package.Source);
        if(current==null&&inventory.Locations.TryGetValue(package.Id,out var marked)&&marked.Any(path=>!path.Equals(package.Source,StringComparison.OrdinalIgnoreCase)))throw new IOException("기존 ID가 다른 폴더에 있습니다. 등록된 게임의 범위를 임의로 변경하지 마세요.");
        if(current!=null&&inventory.Locations.TryGetValue(current.GameId,out var paths)&&paths.Count>1)throw new IOException("같은 ID의 복사본이 있습니다. 보관함 탭에서 먼저 구분하세요.");
        return Update(doc=>
        {
            var id=EnsureIdentity(package.Source,package.Id);
            var game=doc.Games.Find(g=>g.Id==id);if(game==null){game=new LibraryGame {Id=id};doc.Games.Add(game);}
            CopyMetadata(game,package,tags);
            game.Folder=GameScanner.Within(package.Source,Root)?Path.GetRelativePath(Root,package.Source):package.Name;
            if(!BackupPaths.Relative(game.Folder))throw new IOException("보관할 폴더명을 확인해 주세요.");
            game.LastKnownPath=package.Source;game.Executables=scan.Files.Where(f=>package.Includes(f.Path)&&Path.GetExtension(f.Path).Equals(".exe",StringComparison.OrdinalIgnoreCase)).Select(f=>Path.GetRelativePath(package.Source,f.Path)).Order().ToList();
            game.Status=GameScanner.Within(package.Source,Root)?"Present":"External";
            MergeTags(doc,tags);
        });
    }
    static void CopyMetadata(LibraryGame game,Package p,IEnumerable<TagDefinition> tags)
    {
        game.IsContent=p.IsRelatedContent;game.ContentOwnerId=p.ContentOwnerId;game.CompositionParentId=p.CompositionParentId;game.Alias=p.Alias;game.Genres=[..p.Genres];game.Categories=[..p.Categories];
        game.GenreIds=tags.Where(t=>t.Kind=="genre"&&p.Genres.Contains(t.Name)).Select(t=>t.Id).ToList();
        game.CategoryIds=tags.Where(t=>t.Kind=="category"&&p.Categories.Contains(t.Name)).Select(t=>t.Id).ToList();
    }
    static void MergeTags(LibraryDocument doc,IEnumerable<TagDefinition> tags)
    {
        foreach(var tag in tags){var old=doc.Tags.Find(t=>t.Id==tag.Id);if(old!=null){old.Name=tag.Name;old.Kind=tag.Kind;}else doc.Tags.Add(new TagDefinition {Id=tag.Id,Name=tag.Name,Kind=tag.Kind});}
    }
    public LibraryUpdate SaveMetadata(IEnumerable<Package> packages,IEnumerable<TagDefinition> tags,IdentityInventory inventory)
    {
        return Update(doc=>
        {
            MergeTags(doc,tags);
            foreach(var p in packages.Where(p=>!p.Excluded))
            {
                var game=doc.Games.Find(g=>g.Id==p.Id);if(game==null)continue;
                if(!inventory.Locations.TryGetValue(p.Id,out var paths)||paths.Count!=1||!paths[0].Equals(p.Source,StringComparison.OrdinalIgnoreCase))continue;
                CopyMetadata(game,p,tags);
            }
        });
    }
    public void Reconcile(LibraryDocument doc,IdentityInventory inventory,ScanSnapshot scan,CancellationToken token=default)
    {
        foreach(var game in doc.Games)
        {
            token.ThrowIfCancellationRequested();
            if(!inventory.Locations.TryGetValue(game.Id,out var paths)||paths.Count==0)
            {
                var expected=game.LastKnownPath.Length>0?game.LastKnownPath:Path.Combine(Root,game.Folder);
                game.Status=scan.UnreadablePaths.Any(p=>GameScanner.Within(p,expected)||GameScanner.Within(expected,p))?"Unavailable":scan.ContainsDirectory(expected)?"IdentityMissing":"Missing";continue;
            }
            if(paths.Count>1){game.Status="Duplicate";continue;}
            var path=paths[0];game.Status=GameScanner.Within(path,Root)?"Present":"External";
            if(GameScanner.Within(path,Root)&&!path.Equals(Root,StringComparison.OrdinalIgnoreCase))game.Folder=Path.GetRelativePath(Root,path);
            game.LastKnownPath=path;
            game.Executables=scan.FilesUnder(path).Where(f=>Path.GetExtension(f.Path).Equals(".exe",StringComparison.OrdinalIgnoreCase)).Select(f=>Path.GetRelativePath(path,f.Path)).Order().ToList();
        }
    }
    static string CsvCell(string value)
    {
        var trimmed=value.TrimStart(' ','\t','\r','\n');
        if(trimmed.Length>0&&"=+-@".Contains(trimmed[0])||value.StartsWith('\t')||value.StartsWith('\r')||value.StartsWith('\n'))value="'"+value;
        return "\""+value.Replace("\"","\"\"")+"\"";
    }
    public void WriteCsv(LibraryDocument doc)
    {
        Validate(doc);var path=Path.Combine(Root,CsvName);BackupPaths.NoLinks(path);
        if(File.Exists(path)){using var reader=new StreamReader(path,Encoding.UTF8);if(reader.ReadLine()!=CsvHeader)throw new IOException("같은 이름의 CSV는 자동으로 덮어쓰지 않습니다. 다른 곳에 보관 후 다시 저장하세요.");}
        var lines=new StringBuilder(CsvHeader+"\r\n");
        foreach(var game in doc.Games.OrderBy(g=>g.Folder,StringComparer.OrdinalIgnoreCase))
            lines.AppendLine(string.Join(',',new[]{game.Id,game.Folder,game.Alias,JsonSerializer.Serialize(game.Genres,new JsonSerializerOptions {Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}),JsonSerializer.Serialize(game.Categories,new JsonSerializerOptions {Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}),JsonSerializer.Serialize(game.Executables,new JsonSerializerOptions {Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}),game.Status,game.LastKnownPath}.Select(CsvCell)));
        WriteText(path,lines.ToString());
    }
    static void WriteText(string path,string value)
    {
        BackupPaths.NoLinks(path);BackupPaths.NoLinks(path+".bak");var temp=path+".tmp";BackupPaths.NoLinks(temp);
        using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None))
        {var preamble=Encoding.UTF8.GetPreamble();stream.Write(preamble);stream.Write(Encoding.UTF8.GetBytes(value));stream.Flush(true);}
        if(File.Exists(path))File.Replace(temp,path,path+".bak",true);else File.Move(temp,path);
    }
    public static void WriteNote(string folder,LibraryGame game)
    {
        if(ReadIdentity(folder)?.GameId!=game.Id)throw new IOException("게임 ID가 일치하지 않습니다.");
        var path=Path.Combine(folder,NoteName);BackupPaths.NoLinks(path);
        var header="IndieRun game info / ID: "+game.Id;
        if(File.Exists(path)){using var reader=new StreamReader(path,Encoding.UTF8);if(reader.ReadLine()!=header)throw new IOException("같은 이름의 사용자 파일이 있습니다. 덮어쓰지 않습니다.");}
        WriteText(path,header+"\r\n별칭: "+game.Alias+"\r\n폴더: "+Path.GetFileName(folder)+"\r\n장르: "+string.Join(" / ",game.Genres)+"\r\n카테고리: "+string.Join(" / ",game.Categories)+"\r\n실행 후보: "+string.Join(" / ",game.Executables)+"\r\n이 메모는 작성 당시의 정보입니다. 이후 수동 변경을 자동 추적하지 않습니다.\r\n");
    }
}

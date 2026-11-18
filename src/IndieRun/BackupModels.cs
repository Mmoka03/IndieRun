using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace IndieRun;

public sealed class BackupFile
{
    public string RelativePath { get; set; } = "";
    public long Length { get; set; }
    public long ModifiedTicks { get; set; }
    public FileAttributes Attributes { get; set; }
    public string? Sha256 { get; set; }
}
public sealed class BackupJob
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Status { get; set; } = "Preparing";
    public string SourceRoot { get; set; } = "";
    public string OriginalFolder { get; set; } = "";
    public bool Indexed { get; set; }
    public bool SourceVerified { get; set; }
    public long TotalBytes { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string? Error { get; set; }
    public Package Package { get; set; } = new();
    public List<TagDefinition> Tags { get; set; } = [];
    public List<string> Directories { get; set; } = [];
    public List<BackupFile> Files { get; set; } = [];
}
public sealed class RestoreJob
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string BackupId { get; set; } = "";
    public string TargetContainer { get; set; } = "";
    public string Status { get; set; } = "Preparing";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string? Error { get; set; }
}
public sealed record BackupProgress(string Stage,int Completed,int Total,long Bytes,string? File);
public sealed record RestoreOwner(string OperationId,string BackupId);

internal static class BackupJson
{
    static readonly JsonSerializerOptions Options=new(){WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    public static T Load<T>(string path,Action<T> validate)
    {
        Exception? error=null;
        foreach(var candidate in new[]{path,path+".bak"})
        {
            if(!File.Exists(candidate))continue;
            try
            {
                if(new FileInfo(candidate).Length>128L*1024*1024)throw new InvalidDataException("작업 기록이 너무 큽니다.");
                BackupPaths.NoLinks(candidate);
                using var input=new FileStream(candidate,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                var value=JsonSerializer.Deserialize<T>(input)??throw new InvalidDataException("빈 작업 기록입니다.");validate(value);return value;
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException){error=ex;}
        }
        throw new InvalidDataException("작업 기록을 읽지 못했습니다. 원본 기록과 데이터는 보존했습니다.",error);
    }
    public static void Save<T>(string path,T value,Action<T>? validate=null)
    {
        BackupPaths.NoLinks(Path.GetDirectoryName(path)!);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        BackupPaths.NoLinks(path);BackupPaths.NoLinks(path+".tmp");BackupPaths.NoLinks(path+".bak");
        validate?.Invoke(value);var temp=path+".tmp";
        using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None))
        {JsonSerializer.Serialize(stream,value,Options);stream.Flush(true);}
        if(!File.Exists(path)){File.Move(temp,path);return;}
        var valid=true;
        try
        {
            using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            if(validate!=null){var previous=JsonSerializer.Deserialize<T>(input)??throw new InvalidDataException();validate(previous);}
            else {using var parsed=JsonDocument.Parse(input);}
        }
        catch(Exception ex) when(ex is JsonException or InvalidDataException or ArgumentException){valid=false;}
        File.Replace(temp,path,valid?path+".bak":path+".invalid-"+Guid.NewGuid().ToString("N"),true);
    }
}
internal static class BackupPaths
{
    public static bool Id(string value)=>Guid.TryParseExact(value,"N",out _);
    public static bool Relative(string value)
    {
        if(string.IsNullOrWhiteSpace(value)||Path.IsPathRooted(value))return false;
        return value.Split('\\','/').All(part=>part.Length>0&&part!="."&&part!=".."&&part.IndexOfAny(Path.GetInvalidFileNameChars())<0&&!part.EndsWith('.')&&!part.EndsWith(' '));
    }
    public static string Child(string root,string relative)
    {
        if(!Relative(relative))throw new InvalidDataException("잘못된 파일 경로입니다.");
        var path=Path.GetFullPath(Path.Combine(root,relative));if(!GameScanner.Within(path,root)||path.Equals(root,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("범위 밖 경로입니다.");return path;
    }
    public static void NoLinks(string path)
    {
        path=Path.GetFullPath(path);
        for(var current=path;!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
        {
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("연결 파일/폴더는 백업·복원 경로로 사용할 수 없습니다: "+current);
        }
    }
}

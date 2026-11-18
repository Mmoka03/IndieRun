using System.IO;
using System.Text.Json;
namespace IndieRun;
// Optional on old metadata. Null means the prior scope was not recorded, rather than "include everything".
public sealed class StoredScope
{
    public int Version {get;set;}=1;
    public List<string> OmittedFolders {get;set;}=[];
    public List<FolderAssignment> Contents {get;set;}=[];
    static T Copy<T>(T value)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    public static void Validate(StoredScope? scope)
    {
        if(scope==null)return;
        if(scope.Version!=1||scope.OmittedFolders==null||scope.Contents==null||scope.Contents.Count>1000||scope.OmittedFolders.Any(r=>!BackupPaths.Relative(r))||scope.OmittedFolders.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=scope.OmittedFolders.Count)throw new InvalidDataException("저장된 게임 범위를 확인하세요.");
        new Package{OmittedFolders=scope.OmittedFolders,Contents=scope.Contents}.ValidateComposition();
    }
    public static StoredScope? Capture(Package p)=>p.ScopeReviewRequired?null:new(){OmittedFolders=[..p.OmittedFolders],Contents=Copy(p.Contents)};
    public static void Apply(StoredScope? scope,Package p)
    {
        Validate(scope);
        if(scope==null){p.ScopeReviewRequired=true;return;}
        p.OmittedFolders=[..scope.OmittedFolders];p.Contents=Copy(scope.Contents);p.ScopeReviewRequired=false;
    }
}

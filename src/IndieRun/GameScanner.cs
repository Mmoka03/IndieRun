using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace IndieRun;

public sealed record ScannedFile(string Path, long Length, DateTime ModifiedUtc);
public sealed class ScanSnapshot
{
    public string Root { get; init; } = "";
    public List<string> SearchRoots {get;}=[];
    public List<string> InactivePaths {get;}=[];
    public List<string> OfflineRoots {get;}=[];
    public List<string> Directories { get; } = [];
    public List<ScannedFile> Files { get; } = [];
    public List<string> Issues { get; } = [];
    public List<string> UnreadablePaths { get; } = [];
    public int Visited { get; set; }
    public string Fingerprint()
    {
        var rows=Files.OrderBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).Select(f=>$"{f.Path}|{f.Length}|{f.ModifiedUtc.Ticks}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',rows))));
    }
}

// This module only enumerates directories and file metadata. It never launches or writes game files.
public static class GameScanner
{
    public const string MetadataDirectoryName=".indierun-meta";
    public static bool Within(string path,string root) =>
        string.Equals(System.IO.Path.GetFullPath(path).TrimEnd('\\','/'),System.IO.Path.GetFullPath(root).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase)
        || System.IO.Path.GetFullPath(path).StartsWith(System.IO.Path.GetFullPath(root).TrimEnd('\\','/')+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);

    public static bool Auxiliary(string file)
    {
        var name=System.IO.Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        return name is "config" or "configuration" or "uninstall" or "unins000" or "dxsetup" || name.StartsWith("unitycrashhandler") || name.StartsWith("crashreport") || name.StartsWith("vc_redist");
    }
    public static ScanSnapshot Scan(string root,IEnumerable<string>? exclusions=null,CancellationToken cancellation=default,IProgress<int>? progress=null)
    {
        root=System.IO.Path.GetFullPath(root);
        if(!Directory.Exists(root))throw new DirectoryNotFoundException(root);
        if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)throw new IOException("연결 폴더는 검색 시작 위치로 사용할 수 없습니다.");
        var excluded=(exclusions??[]).Select(System.IO.Path.GetFullPath).ToArray();
        if(excluded.Any(e=>Within(root,e)))throw new IOException("검색 위치가 제외 경로에 포함됩니다.");
        var result=new ScanSnapshot {Root=root};
        result.Directories.Add(root);
        var pending=new Stack<string>();pending.Push(root);
        while(pending.TryPop(out var directory))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                foreach(var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes=File.GetAttributes(path);
                        if((attributes&FileAttributes.ReparsePoint)!=0){result.Issues.Add($"연결 항목 제외: {path}");continue;}
                        if((attributes&FileAttributes.Directory)!=0)
                        {
                            if(Path.GetFileName(path).Equals(MetadataDirectoryName,StringComparison.OrdinalIgnoreCase)||excluded.Any(e=>Within(path,e)))continue;
                            result.Directories.Add(path);pending.Push(path);
                        }
                        else
                        {
                            var info=new FileInfo(path);result.Files.Add(new ScannedFile(path,info.Length,info.LastWriteTimeUtc));
                        }
                        result.Visited++;
                        if(result.Visited%250==0)progress?.Report(result.Visited);
                    }
                    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){result.UnreadablePaths.Add(path);result.Issues.Add($"접근 생략: {path} ({ex.Message})");}
                }
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){result.UnreadablePaths.Add(directory);result.Issues.Add($"폴더 생략: {directory} ({ex.Message})");}
        }
        progress?.Report(result.Visited);return result;
    }
    public static IEnumerable<IGrouping<string,ScannedFile>> CandidateGroups(ScanSnapshot scan) => scan.Files
        .Where(f=>System.IO.Path.GetExtension(f.Path).Equals(".exe",StringComparison.OrdinalIgnoreCase))
        .GroupBy(f=>System.IO.Path.GetDirectoryName(f.Path)!,StringComparer.OrdinalIgnoreCase)
        .OrderBy(g=>g.Key,StringComparer.OrdinalIgnoreCase);
}

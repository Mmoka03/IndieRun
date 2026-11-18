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
    ScannedFile[]? indexedFiles;
    string[]? indexedDirectories;
    HashSet<string>? directorySet;
    int indexedFileCount,indexedDirectoryCount;
    public void PrepareIndex(CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        indexedFiles=Files.OrderBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).ToArray();
        indexedDirectories=Directories.OrderBy(d=>d,StringComparer.OrdinalIgnoreCase).ToArray();
        directorySet=Directories.ToHashSet(StringComparer.OrdinalIgnoreCase);indexedFileCount=Files.Count;indexedDirectoryCount=Directories.Count;
        token.ThrowIfCancellationRequested();
    }
    public void InvalidateIndex(){indexedFiles=null;indexedDirectories=null;directorySet=null;}
    static int LowerBound(int count,Func<int,string> path,string value)
    {
        int low=0,high=count;while(low<high){var middle=low+(high-low)/2;if(StringComparer.OrdinalIgnoreCase.Compare(path(middle),value)<0)low=middle+1;else high=middle;}return low;
    }
    public IEnumerable<ScannedFile> FilesUnder(string root)
    {
        if(indexedFiles==null||indexedFileCount!=Files.Count)return Files.Where(f=>GameScanner.Within(f.Path,root));
        var prefix=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))+Path.DirectorySeparatorChar;
        if(Path.EndsInDirectorySeparator(Path.GetFullPath(root)))prefix=Path.GetFullPath(root);
        return FileRange(prefix);
    }
    IEnumerable<ScannedFile> FileRange(string prefix)
    {var files=indexedFiles!;for(int i=LowerBound(files.Length,n=>files[n].Path,prefix);i<files.Length&&files[i].Path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase);i++)yield return files[i];}
    public bool ContainsDirectory(string root)=>directorySet!=null&&indexedDirectoryCount==Directories.Count?directorySet.Contains(root):Directories.Contains(root,StringComparer.OrdinalIgnoreCase);
    public IEnumerable<string> DirectoriesUnder(string root)
    {
        if(indexedDirectories==null||indexedDirectoryCount!=Directories.Count)return Directories.Where(d=>GameScanner.Within(d,root));
        return DirectoryRange(Path.GetFullPath(root));
    }
    IEnumerable<string> DirectoryRange(string root)
    {
        if(ContainsDirectory(root))yield return root;
        var prefix=Path.EndsInDirectorySeparator(root)?root:root+Path.DirectorySeparatorChar;var dirs=indexedDirectories!;
        for(int i=LowerBound(dirs.Length,n=>dirs[n],prefix);i<dirs.Length&&dirs[i].StartsWith(prefix,StringComparison.OrdinalIgnoreCase);i++)yield return dirs[i];
    }
    public string Fingerprint()
    {
        var rows=Files.OrderBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).Select(f=>$"{f.Path}|{f.Length}|{f.ModifiedUtc.Ticks}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',rows))));
    }
}

// This module only enumerates directories and file metadata. It never launches or writes game files.
public static class GameScanner
{
    public static bool CoveredBy(string path,HashSet<string> roots)
    {
        for(var current=Path.GetFullPath(path);current!=null;current=Path.GetDirectoryName(current))
            if(roots.Contains(Path.TrimEndingDirectorySeparator(current)))return true;
        return false;
    }
    public const string MetadataDirectoryName=".indierun-meta";
    public static bool Within(string path,string root) =>
        string.Equals(System.IO.Path.GetFullPath(path).TrimEnd('\\','/'),System.IO.Path.GetFullPath(root).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase)
        || System.IO.Path.GetFullPath(path).StartsWith(System.IO.Path.GetFullPath(root).TrimEnd('\\','/')+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);

    public static bool Auxiliary(string file)
    {
        var name=System.IO.Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        return name is "config" or "configuration" or "uninstall" or "unins000" or "dxsetup" || name.StartsWith("unitycrashhandler") || name.StartsWith("crashreport") || name.StartsWith("vc_redist");
    }
    public static ScanSnapshot Scan(string root,IEnumerable<string>? exclusions=null,CancellationToken cancellation=default,IProgress<int>? progress=null,ScanBudget? budget=null)
    {
        root=System.IO.Path.GetFullPath(root);
        if(!Directory.Exists(root))throw new DirectoryNotFoundException(root);
        if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)throw new IOException("연결 폴더는 검색 시작 위치로 사용할 수 없습니다.");
        var excluded=(exclusions??[]).Select(System.IO.Path.GetFullPath).ToArray();
        if(excluded.Any(e=>Within(root,e)))throw new IOException("검색 위치가 제외 경로에 포함됩니다.");
        var result=new ScanSnapshot {Root=root};
        result.Directories.Add(root);
        var pending=new Stack<string>();pending.Push(root);
        var reportClock=System.Diagnostics.Stopwatch.StartNew();
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
                        budget?.Visit(path,(attributes&FileAttributes.Directory)!=0);
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
                        if(result.Visited%250==0&&reportClock.ElapsedMilliseconds>=150){progress?.Report(result.Visited);reportClock.Restart();}
                    }
                    catch(ScanLimitException){throw;}
                    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){result.UnreadablePaths.Add(path);result.Issues.Add($"접근 생략: {path} ({ex.Message})");}
                }
            }
            catch(ScanLimitException){throw;}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){result.UnreadablePaths.Add(directory);result.Issues.Add($"폴더 생략: {directory} ({ex.Message})");}
        }
        progress?.Report(result.Visited);cancellation.ThrowIfCancellationRequested();return result;
    }
    public static IEnumerable<IGrouping<string,ScannedFile>> CandidateGroups(ScanSnapshot scan) => scan.Files
        .Where(f=>System.IO.Path.GetExtension(f.Path).Equals(".exe",StringComparison.OrdinalIgnoreCase))
        .GroupBy(f=>System.IO.Path.GetDirectoryName(f.Path)!,StringComparer.OrdinalIgnoreCase)
        .OrderBy(g=>g.Key,StringComparer.OrdinalIgnoreCase);
}

public sealed class ScanLimitException():IOException("메모리 여유가 부족해 검색을 중지했습니다. 탐색 위치를 나눠서 검색해 주세요.");
public sealed class ScanBudget(int maxEntries=int.MaxValue,int maxCandidates=int.MaxValue,TimeSpan? maxTime=null,long? maxManagedBytes=null)
{
    readonly System.Diagnostics.Stopwatch clock=System.Diagnostics.Stopwatch.StartNew();
    readonly HashSet<string> candidates=new(StringComparer.OrdinalIgnoreCase);
    int entries;
    readonly long managedLimit=maxManagedBytes??Math.Min(2L*1024*1024*1024,Math.Max(256L*1024*1024,(long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes*.4)));
    public void Visit(string path,bool directory)
    {
        if(++entries>maxEntries||maxTime.HasValue&&clock.Elapsed>maxTime.Value||entries%4096==0&&GC.GetTotalMemory(false)>managedLimit)throw new ScanLimitException();
        if(!directory&&Path.GetExtension(path).Equals(".exe",StringComparison.OrdinalIgnoreCase)&&!GameScanner.Auxiliary(path))
            if(candidates.Add(Path.GetDirectoryName(path)!)&&candidates.Count>maxCandidates)throw new ScanLimitException();
    }
}

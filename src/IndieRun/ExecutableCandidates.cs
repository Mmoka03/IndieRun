using System.IO;

namespace IndieRun;

public sealed record ExecutableCandidate(string RelativePath,bool Auxiliary)
{
    public string Name=>Path.GetFileName(RelativePath);
    public string Folder=>Path.GetDirectoryName(RelativePath) is {Length:>0} folder?folder:"게임 폴더";
}
public sealed record ExecutableSearch(List<ExecutableCandidate> Candidates,List<string> Issues);
public static class ExecutableCandidates
{
    public static bool IsAuxiliary(string relative)
    {
        var name=Path.GetFileNameWithoutExtension(relative).ToLowerInvariant();
        return GameScanner.Auxiliary(relative)||name is "setup" or "dxwebsetup" or "vcredist_x86" or "vcredist_x64"||name.StartsWith("unins",StringComparison.Ordinal);
    }
    public static ExecutableCandidate ValidateSelection(string root,string file)
    {
        root=Path.GetFullPath(root);file=Path.GetFullPath(file);
        var relative=Path.GetRelativePath(root,file);
        if(!BackupPaths.Relative(relative)||!Path.GetExtension(file).Equals(".exe",StringComparison.OrdinalIgnoreCase))throw new IOException("이 게임 폴더 안의 EXE 파일을 선택하세요.");
        BackupPaths.NoLinks(file);GameLaunch.RequireFile(file);
        return new(relative,IsAuxiliary(relative));
    }
    public static ExecutableSearch Find(string root,CancellationToken token=default)
    {
        root=Path.GetFullPath(root);GameLaunch.RequireFolder(root,LaunchProblem.FolderMissing);
        var found=new List<ExecutableCandidate>();var issues=new List<string>();var pending=new Stack<string>();pending.Push(root);
        while(pending.TryPop(out var folder))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                BackupPaths.NoLinks(folder);
                foreach(var path in Directory.EnumerateFileSystemEntries(folder))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var flags=File.GetAttributes(path);
                        if((flags&FileAttributes.ReparsePoint)!=0){issues.Add("연결 항목 제외: "+path);continue;}
                        if((flags&FileAttributes.Directory)!=0)pending.Push(path);
                        else if(Path.GetExtension(path).Equals(".exe",StringComparison.OrdinalIgnoreCase))found.Add(new(Path.GetRelativePath(root,path),IsAuxiliary(path)));
                    }
                    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){issues.Add(path+": "+ex.Message);}
                }
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){issues.Add(folder+": "+ex.Message);}
        }
        var folderName=Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
        var ordered=found.DistinctBy(c=>c.RelativePath,StringComparer.OrdinalIgnoreCase).OrderBy(c=>c.Auxiliary)
            .ThenByDescending(c=>Path.GetFileNameWithoutExtension(c.Name).Equals(folderName,StringComparison.OrdinalIgnoreCase))
            .ThenBy(c=>c.RelativePath.Count(ch=>ch=='\\'||ch=='/')).ThenBy(c=>c.RelativePath,StringComparer.OrdinalIgnoreCase).ToList();
        return new(ordered,issues);
    }
    public static ExecutableCandidate? Default(ExecutableSearch search)
    {var main=search.Candidates.Where(c=>!c.Auxiliary).ToList();return main.Count==1?main[0]:null;}
}

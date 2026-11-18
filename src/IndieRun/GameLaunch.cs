using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace IndieRun;

public enum LaunchProblem { None, Setup, FolderMissing, ExecutableMissing, WorkingFolderMissing, Identity, Access, InvalidProgram, Settings, Failed, LocationOffline }
public sealed record LaunchState(LaunchProblem Problem,string Label,string Message)
{
    public bool NeedsAttention=>Problem is not (LaunchProblem.None or LaunchProblem.Setup);
}
public sealed class GameLaunchException(LaunchProblem problem,string message):IOException(message)
{public LaunchProblem Problem {get;}=problem;}

public static class GameLaunch
{
    public static LaunchState State(LaunchProblem problem)=>problem switch
    {
        LaunchProblem.LocationOffline=>new(problem,"위치 연결 확인","탐색 위치에 연결할 수 없습니다. 드라이브와 폴더 위치를 확인한 뒤 다시 검색하세요."),
        LaunchProblem.None=>new(problem,"",""),
        LaunchProblem.Setup=>new(problem,"실행 파일 선택","실행할 파일을 선택하세요."),
        LaunchProblem.FolderMissing=>new(problem,"폴더 확인","게임 폴더를 찾을 수 없습니다. 현재 위치를 선택해 다시 연결하세요."),
        LaunchProblem.ExecutableMissing=>new(problem,"실행 파일 확인","지정한 실행 파일을 찾을 수 없습니다. 실행 설정에서 다시 선택하세요."),
        LaunchProblem.WorkingFolderMissing=>new(problem,"작업 폴더 확인","지정한 작업 폴더가 없습니다. 실행 설정에서 경로를 수정하거나 비워 두세요."),
        LaunchProblem.Identity=>new(problem,"게임 ID 확인","등록된 게임과 폴더의 식별 정보가 일치하지 않습니다. 보관함에서 확인하세요."),
        LaunchProblem.Access=>new(problem,"접근 확인","파일이나 폴더에 접근할 수 없습니다. 권한과 다른 프로그램의 사용 여부를 확인하세요."),
        LaunchProblem.InvalidProgram=>new(problem,"실행 파일 확인","Windows에서 실행할 수 없는 파일입니다. 실행 파일을 다시 선택하세요."),
        LaunchProblem.Settings=>new(problem,"실행 설정 확인","실행 설정을 읽지 못했습니다. 설정과 경로를 확인하세요."),
        _=>new(problem,"실행 실패","게임을 시작하지 못했습니다. 오류 내용을 확인하세요.")
    };
    public static LaunchState Classify(Exception ex)=>State(ex switch
    {
        GameLaunchException issue=>issue.Problem,
        UnauthorizedAccessException=>LaunchProblem.Access,
        IOException io when (io.HResult&65535) is 5 or 32 or 33=>LaunchProblem.Access,
        Win32Exception win=>win.NativeErrorCode switch {5 or 32 or 33 or 740 or 1223=>LaunchProblem.Access,2=>LaunchProblem.ExecutableMissing,3 or 267=>LaunchProblem.WorkingFolderMissing,193 or 216=>LaunchProblem.InvalidProgram,_=>LaunchProblem.Failed},
        JsonException or InvalidDataException or ArgumentException=>LaunchProblem.Settings,
        _=>LaunchProblem.Failed
    });
    public static LaunchState Inspect(Package p,GamePlay game,bool registered=false)
    {
        try
        {
            if(p.ReviewState=="offline")return State(LaunchProblem.LocationOffline);
            RequireFolder(p.Source,LaunchProblem.FolderMissing);
            var marker=ReadIdentity(p.Source);
            if(marker!=null&&marker.GameId!=p.Id||registered&&marker==null||p.ReviewState=="identity")return State(LaunchProblem.Identity);
            if(p.ReviewState=="unverified")return State(LaunchProblem.Access);
            if(game.Executable.Length==0)return State(LaunchProblem.Setup);
            PlayerStore.StartInfo(p,game);return State(LaunchProblem.None);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException){return Classify(ex);}
    }
    internal static GameIdentity? ReadIdentity(string folder)
    {
        try{return LibraryStore.ReadIdentity(folder);}
        catch(Exception ex) when(ex is JsonException or InvalidDataException){throw new GameLaunchException(LaunchProblem.Identity,"게임 식별 파일을 읽지 못했습니다. 보관함에서 확인하세요.");}
    }
    internal static void RequireFolder(string path,LaunchProblem missing)
    {
        try{BackupPaths.NoLinks(path);if((File.GetAttributes(path)&FileAttributes.Directory)==0)throw new GameLaunchException(missing,State(missing).Message);}
        catch(FileNotFoundException){throw new GameLaunchException(missing,State(missing).Message);}
        catch(DirectoryNotFoundException){throw new GameLaunchException(missing,State(missing).Message);}
    }
    internal static void RequireFile(string path)
    {
        try{BackupPaths.NoLinks(path);if((File.GetAttributes(path)&FileAttributes.Directory)!=0)throw new GameLaunchException(LaunchProblem.ExecutableMissing,State(LaunchProblem.ExecutableMissing).Message);}
        catch(FileNotFoundException){throw new GameLaunchException(LaunchProblem.ExecutableMissing,State(LaunchProblem.ExecutableMissing).Message);}
        catch(DirectoryNotFoundException){throw new GameLaunchException(LaunchProblem.ExecutableMissing,State(LaunchProblem.ExecutableMissing).Message);}
    }
}

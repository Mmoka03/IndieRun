using System.Diagnostics;
using System.IO;
namespace IndieRun;
public static class PreviewLaunch
{
    public static ProcessStartInfo StartInfo(Package package,GamePlay? stored,string relative,bool registered=false)
    {
        var picked=ExecutableCandidates.ValidateSelection(package.Source,BackupPaths.Child(package.Source,relative));
        if(!package.Includes(Path.Combine(package.Source,picked.RelativePath)))throw new IOException("별도 게임이나 제외한 폴더의 파일입니다.");
        var same=stored!=null&&string.Equals(stored.Executable,relative,StringComparison.OrdinalIgnoreCase);
        var game=new GamePlay{Id=package.Id,Executable=relative,Arguments=same?stored!.Arguments:"",WorkingDirectory=same?stored!.WorkingDirectory:""};
        var state=GameLaunch.Inspect(package,game,registered);if(state.NeedsAttention)throw new GameLaunchException(state.Problem,state.Message);return PlayerStore.StartInfo(package,game);
    }
}

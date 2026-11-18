using System.IO;
using System.Windows;
using Microsoft.Win32;
namespace IndieRun;
public partial class MainWindow
{
    void ChangeLibraryLocation()
    {
        if(running||backupBusy||scanning)return;
        if(workspaceBlocked||workspaceLock==null||tagLoadError.Length>0||playerError.Length>0||RenamePending||ScopeChanges().HasPending||File.Exists(Edits().Pending))
        {Info("보관함 위치 변경","진행 중인 작업이나 저장 오류를 먼저 해결하세요.");return;}
        var initial=destination;while(!Directory.Exists(initial)&&Path.GetDirectoryName(initial) is string parent&&parent!=initial)initial=parent;
        var picker=new OpenFolderDialog{Title="보관함으로 사용할 폴더 선택",InitialDirectory=Directory.Exists(initial)?initial:AppContext.BaseDirectory};
        if(picker.ShowDialog(this)!=true)return;
        if(Path.GetFullPath(picker.FolderName).TrimEnd('\\').Equals(Path.GetFullPath(destination).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))return;
        var message="등록 정보와 실행 설정을 유지합니다. 게임 파일은 현재 위치에 남습니다.";
        if(ContainerPathWarning(picker.FolderName) is {Length:>0} pathWarning)message+="\n"+pathWarning;
        if(!Confirm("보관함 위치 변경",message,"위치 변경",picker.FolderName))return;
        try
        {
            saveTimer.Stop();
            var current=Library().Load();
            var bundle=LibraryLocation.Prepare(Clone(CaptureWorkspace()),Clone(catalog),Clone(playerDocument),current,picker.FolderName);
            Edits().Commit(bundle);
            ApplyWorkspace(bundle.Plan);catalog=bundle.Tags;playerDocument=bundle.Play;playerRoot=destination;libraryDocument=Library().Load();libraryError="";artworkCache.Clear();lastSavedSignature=null;
            Render();RenderLibrary();if(playerCards!=null)RenderPlayer();FlushWorkspace();Say("보관함 위치를 변경했습니다. 게임 파일은 이동하지 않았습니다.");
        }
        catch(Exception ex) when(LibraryFault(ex))
        {if(File.Exists(Edits().Pending))workspaceBlocked=true;ErrorInfo("보관함 위치 변경",File.Exists(Edits().Pending)?"저장이 중단됐습니다. 프로그램을 다시 열면 남은 저장을 복구합니다.":"위치를 변경하지 못했습니다. 폴더의 접근 권한과 저장 상태를 확인하세요.",ex.Message);}
    }
    void OpenLibraryFolder()
    {
        if(Directory.Exists(destination)){OpenSourceFolder(destination);return;}
        if(Confirm("보관함 위치 확인","저장된 위치를 찾을 수 없습니다. 사용할 폴더를 다시 선택하세요.","폴더 선택",destination))ChangeLibraryLocation();
    }
}

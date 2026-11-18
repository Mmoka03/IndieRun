using System.Windows;
using System.IO;
namespace IndieRun;
public partial class MainWindow
{
    WorkspaceSaveIssue? workspaceSaveIssue;
    void SaveIssue(WorkspaceSaveIssue issue,string detail)
    {
        workspaceSaveIssue=issue;SaveStatus.Content="저장 문제 확인";SaveStatus.ToolTip=issue.Message+"\n"+issue.Code+"\n클릭해서 확인하세요.";
        saveIssueDetail=detail;
    }
    string saveIssueDetail="";
    bool HasMetadataSaveIssue=>libraryError.Length>0||portableErrors.Count>0;
    void ShowMetadataSaveIssue()
    {
        while(HasMetadataSaveIssue)
        {
            var details=(libraryError.Length>0?"보관함 정보\n"+libraryError+"\n\n":"")+string.Join("\n\n",portableErrors.Select(pair=>"게임 정보 파일\n"+pair.Value));
            var choice=Choose("정보 파일 저장 확인","정보 파일을 읽거나 저장하지 못했습니다. 파일을 사용 중인 앱이나 폴더의 접근 권한을 확인하세요.",[new("close","닫기"),new("library","보관함에서 확인"),new("retry","다시 저장",true)],"close",details,DialogTone.Warning);
            if(choice=="library"){SetPage("library");RenderLibrary();return;}
            if(choice!="retry")return;
            if(snapshot==null||scanning||backupBusy||workspaceBlocked){Info("정보 파일 저장 확인","진행 중인 작업을 마친 뒤 ‘다시 검색’으로 폴더를 확인하세요.");return;}
            if(!FlushWorkspace()){ShowWorkspaceSaveIssue();return;}
        }
        Say("정보 파일을 저장했습니다.");
    }
    async Task<bool> RecoverPendingEditAsync()
    {
        if(scanning||running||backupBusy)return false;
        if(BatchRenames().HasPending){ShowRenameProgress();return false;}
        if(!File.Exists(Edits().Pending)&&!ScopeChanges().HasPending&&!RenamePending)return false;
        saveTimer.Stop();running=true;IsEnabled=false;Say("편집 저장을 복구하고 있습니다…");
        try
        {
            await Task.Run(()=>{Renames().Recover();ScopeChanges().Recover();Edits().Recover();});
            var loaded=await Task.Run(()=>workspaceStore.Load());
            if(loaded.Document==null)throw new IOException(loaded.Error??"복구한 설정을 읽지 못했습니다.");
            ApplyWorkspace(loaded.Document);LoadTags();ResolveTagBindings(loaded.Document);LoadPlayer();libraryDocument=await Task.Run(()=>Library().Load());
            snapshot=null;identityInventory=new();workspaceBlocked=false;workspaceSaveIssue=null;saveIssueDetail="";lastSavedSignature=null;history.Clear();
            foreach(var p in packages.Where(p=>!p.Excluded)){p.ReviewState="cached";p.ReviewReason="복구한 목록입니다. 폴더 작업 전에 다시 확인하세요.";}
            SaveStatus.Content="저장 복구됨";Say("편집 저장을 복구했습니다. 필요한 폴더만 다시 확인할 수 있습니다.");return true;
        }
        catch(Exception ex)when(LibraryFault(ex)){workspaceBlocked=true;SaveIssue(new("SAVE-RECOVERY","중단된 편집 저장을 복구해야 합니다."),ex.Message);ErrorInfo("편집 복구","위치와 파일 접근 상태를 확인한 뒤 다시 시도하세요. 복구 기록은 유지됩니다.",ex.Message);return false;}
        finally{running=false;IsEnabled=true;Render();if(playerCards!=null)RenderPlayer();}
    }
    async void ShowWorkspaceSaveIssue()
    {
        while(true)
        {
        var pending=File.Exists(Edits().Pending)||ScopeChanges().HasPending||RenamePending;
        var canCancel=pending&&Edits().CanCancelUnappliedCopyIdentity();
        var issue=pending?new WorkspaceSaveIssue("SAVE-RECOVERY",canCancel?"복사본 등록이 중단됐습니다. 게임 ID와 등록 정보는 아직 바뀌지 않았습니다.":"중단된 편집 저장을 복구해야 합니다."):workspaceSaveIssue??new WorkspaceSaveIssue("SAVE-BLOCKED","진행 중인 편집 복구 또는 설정 파일 상태를 확인해야 합니다.");
        var actions=new List<DialogChoice>{new("close","닫기"),new("copy","오류 코드 복사")};
        if(canCancel)actions.Add(new("cancel","등록 취소"));actions.Add(new(pending?"recover":"retry",pending?"편집 복구":"다시 저장",true));
        var choice=Choose("저장 문제 확인",issue.Message+"\n저장 문제를 해결한 뒤 폴더 이동·백업을 시작할 수 있습니다.",actions.ToArray(),"close",saveIssueDetail.Length>0?saveIssueDetail:null,DialogTone.Warning,subject:issue.Code);
        if(choice=="copy")
        {try{Clipboard.SetText(issue.Code);Say("오류 코드를 복사했습니다. 게임명과 경로는 포함되지 않습니다.");}catch(System.Runtime.InteropServices.COMException){Say("복사하지 못했습니다. 오류 코드를 직접 확인해 주세요.");}}
        else if(choice=="retry")
        {if(FlushWorkspace()){RenderPlan();if(HasMetadataSaveIssue)ShowMetadataSaveIssue();else Say("저장했습니다.");}else continue;}
        else if(choice=="recover")await RecoverPendingEditAsync();
        else if(choice=="cancel")
        {
            try
            {
                if(!Edits().CancelUnappliedCopyIdentity()){Info("등록 취소 확인","작업 상태가 바뀌어 취소하지 않았습니다. 편집 복구로 현재 상태를 확인하세요.");return;}
                workspaceBlocked=false;workspaceSaveIssue=null;saveIssueDetail="";FlushWorkspace();Render();Say("복사본 등록을 취소했습니다. 게임 ID와 파일은 유지됩니다.");
            }
            catch(Exception ex)when(LibraryFault(ex)){ErrorInfo("등록 취소 확인","작업 기록을 보관하지 못했습니다. 기록과 게임 파일은 유지됩니다.",ex.Message);}
        }
        break;
        }
    }
}

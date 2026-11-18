using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace IndieRun;

public partial class MainWindow
{
    Package? scopeDraft;
    readonly List<string> scopeDraftHistory=[];
    ScopeChange ScopeChanges()=>new(DataRoot);
    Package ScopeView=>scopeDraft??selected;
    string ExecutableScopeRoot=>playerDocument.Games.Find(g=>g.Id==selected.Id) is {Executable.Length:>0} game?Path.GetDirectoryName(Path.Combine(selected.Source,game.Executable))!:selected.DetectedRoot;
    Package ScopeAnchor(){var p=Clone(selected);p.DetectedRoot=ExecutableScopeRoot;return p;}
    bool ScopeDirty=>scopeDraft!=null&&!scopeDraft.Source.Equals(selected.Source,StringComparison.OrdinalIgnoreCase);
    bool LeaveScopeEdit()
    {
        if(scopeDraft==null)return true;
        if(ScopeDirty&&!Confirm("범위 수정 취소","적용하지 않은 범위 수정이 있습니다.","수정 취소"))return false;
        scopeDraft=null;scopeDraftHistory.Clear();return true;
    }
    void EditScope_Click(object sender,RoutedEventArgs e)
    {
        if(selected.Contents.Count>0||selected.RelatedFolders.Count>0){Info("구성 수정","구성 수정에서 폴더 지정을 먼저 해제하세요.");return;}
        if(!HasSelection||!IsAdded(selected)||scanning||running||backupBusy||workspaceBlocked||ScopeChanges().HasPending)return;
        scopeDraft=Clone(selected);scopeDraftHistory.Clear();Render();Say("포함할 폴더를 확인한 뒤 범위를 적용하세요.");
    }
    void DraftScope(string path,bool remember=true)
    {
        if(scopeDraft==null||snapshot==null)return;path=Path.GetFullPath(path);
        var problem=ScopeChange.Problem(ScopeAnchor(),path,packages);if(problem!=null){ShowOrganizeWarning(problem);return;}
        if(!snapshot.Directories.Contains(path,StringComparer.OrdinalIgnoreCase)){ShowOrganizeWarning("해당 폴더를 다시 검색해 주세요.");return;}
        if(scopeDraft.Source.Equals(path,StringComparison.OrdinalIgnoreCase))return;
        if(remember)scopeDraftHistory.Add(scopeDraft.Source);else if(scopeDraftHistory.Count>0)scopeDraftHistory.RemoveAt(scopeDraftHistory.Count-1);
        scopeDraft.Source=path;scopeDraft.Folder=Path.GetFileName(path);Render();
    }
    void ExecutableScope_Click(object sender,RoutedEventArgs e){if(scopeDraft!=null)DraftScope(ExecutableScopeRoot);}
    void CancelScope_Click(object sender,RoutedEventArgs e){scopeDraft=null;scopeDraftHistory.Clear();Render();Say("범위 수정을 취소했습니다.");}
    async void ApplyScope_Click(object sender,RoutedEventArgs e)
    {
        if(!ScopeDirty||scopeDraft==null||backupBusy||running||scanning||workspaceBlocked)return;
        if(!FlushWorkspace()){Say("현재 작업을 먼저 저장하세요.");return;}
        var applied=false;saveTimer.Stop();if(playerRoot!=destination)LoadPlayer();var path=scopeDraft.Source;var id=selected.Id;running=true;Workbench.IsEnabled=false;FooterActions.IsEnabled=false;
        foreach(var b in new[]{PlayerNav,OrganizeNav,TagsNav,LibraryNav,JobsNav,RescanButton})b.IsEnabled=false;
        try
        {
            var service=ScopeChanges();var plan=Clone(CaptureWorkspace());var tags=CopyCatalog();var play=Clone(playerDocument);var library=Clone(libraryDocument);
            await Task.Run(()=>{var record=service.Prepare(plan,tags,play,library,id,path);service.Commit(record);});
            applied=true;scopeDraft=null;scopeDraftHistory.Clear();ApplyWorkspace(workspaceStore.Load().Document!);LoadPlayer();lastSavedSignature=null;history.Clear();Say("범위를 변경했습니다 · 게임 파일은 이동하지 않았습니다.");
        }
        catch(Exception ex) when(LibraryFault(ex)){if(ScopeChanges().HasPending)workspaceBlocked=true;ErrorInfo("범위 적용 확인","변경을 완료하지 못했습니다. 원본 게임 파일은 유지됩니다. 남은 기록이 있으면 복구하세요.",ex.Message);}
        finally{running=false;Workbench.IsEnabled=true;FooterActions.IsEnabled=true;foreach(var b in new[]{PlayerNav,OrganizeNav,TagsNav,LibraryNav,JobsNav,RescanButton})b.IsEnabled=true;}
        if(applied&&!ScopeChanges().HasPending){await RefreshWorkspaceAsync(source,false,false);Say("범위를 변경했습니다 · 게임 파일은 이동하지 않았습니다.");}else Render();
    }
    async void RecoverScope_Click(object sender,RoutedEventArgs e)
    {
        if(running||backupBusy||scanning)return;
        try{ScopeChanges().Recover();workspaceBlocked=false;scopeDraft=null;ApplyWorkspace(workspaceStore.Load().Document!);LoadTags();LoadPlayer();await RefreshWorkspaceAsync(source,false,false);Say("범위 변경을 복구했습니다.");}
        catch(Exception ex) when(LibraryFault(ex)){workspaceBlocked=true;ErrorInfo("범위 변경 복구","폴더 위치·접근 권한·변경된 파일을 확인한 뒤 다시 시도하세요.",ex.Message);}
    }
    void RenderScopeEditActions()
    {
        var editing=scopeDraft!=null;
        var outer=HasSelection?Directory.GetParent(selected.Source)?.FullName:null;var possible=HasSelection&&(selected.Source!=ExecutableScopeRoot||outer!=null&&ScopeChange.Problem(selected,outer,packages)==null);
        EditScopeButton.Visibility=HasSelection&&IsAdded(selected)&&!editing&&possible?Visibility.Visible:Visibility.Collapsed;EditScopeButton.IsEnabled=!scanning&&!backupBusy&&!running&&selected.ReviewState.Length==0&&!workspaceBlocked;
        if(editing)
        {
            ScopeActions.Visibility=Visibility.Visible;OriginalPath.Text=scopeDraft!.Source;OriginalPath.ToolTip=OriginalPath.Text;SelectedState.Text="범위 미리보기";
            var parent=Directory.GetParent(scopeDraft.Source)?.FullName;IncludeParentButton.Visibility=parent!=null&&GameScanner.Within(parent,selected.SearchRoot)&&!parent.Equals(selected.SearchRoot,StringComparison.OrdinalIgnoreCase)&&parent!=scopeDraftHistory.LastOrDefault()?Visibility.Visible:Visibility.Collapsed;IncludeParentButton.ToolTip=parent;
            ResetScopeButton.Visibility=scopeDraftHistory.Count>0?Visibility.Visible:Visibility.Collapsed;OrganizeInfoButton.Visibility=Visibility.Collapsed;MoveSelectedButton.Visibility=Visibility.Collapsed;
        }
        ExecutableScopeButton.Visibility=editing&&scopeDraftHistory.Count==0&&ScopeView.Source!=ExecutableScopeRoot?Visibility.Visible:Visibility.Collapsed;
        CancelScopeButton.Visibility=ApplyScopeButton.Visibility=editing?Visibility.Visible:Visibility.Collapsed;ApplyScopeButton.IsEnabled=ScopeDirty;
        RecoverScopeButton.Visibility=ScopeChanges().HasPending?Visibility.Visible:Visibility.Collapsed;
        if(ScopeChanges().HasPending)ShowOrganizeWarning("이전 범위 변경을 먼저 복구하세요.");
    }
}

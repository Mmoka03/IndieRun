using System.IO;
namespace IndieRun;
public partial class MainWindow
{
    bool ConfirmStoredScope(Package p)
    {
        if(snapshot==null||!snapshot.ContainsDirectory(p.Source)||WorkspaceReconciler.HasUnreadable(p,snapshot))
        {Info("범위 확인","폴더를 다시 검색한 뒤 범위를 확인하세요.");return false;}
        if(!Confirm("이동 범위 확인","이전 버전의 기록에는 제외 설정이 없습니다. 현재 폴더 구성을 확인한 뒤 진행하세요.","현재 범위로 확인",p.Source+"\n\n제외할 폴더가 있다면 취소한 뒤 구성 수정에서 지정하세요."))return false;
        var plan=Clone(CaptureWorkspace());var next=plan.Packages.Single(q=>q.Id==p.Id);next.ScopeReviewRequired=false;
        plan.Checkpoints.RemoveAll(c=>c.PackageId==p.Id);plan.Checkpoints.Add(WorkspaceReconciler.Capture(next,snapshot));
        var saved=CommitPlayerEdit(plan.Packages,CopyCatalog(),Clone(playerDocument),[p.Id],prepared:plan);
        if(saved){p.ScopeReviewRequired=false;p.ReviewState=packages.Single(q=>q.Id==p.Id).ReviewState;Render();}
        return saved;
    }
}

using System.Windows;
namespace IndieRun;
public partial class MainWindow
{
    void ShowScopeProblem(Package game,string path,string message)
    {
        var other=packages.FirstOrDefault(q=>(FolderPolicy.Conflict(game,q,path,snapshot)&&!RegisteredTree.ExistingChild(game,q,game.Source,path))||FolderPolicy.ScopeOverlap(game,q,path)&&!RegisteredTree.ExistingChild(game,q,game.Source,path)&&!FolderPolicy.UnownedAncestor(q,path)&&!(snapshot!=null&&FolderPolicy.VerifiedMissing(q.Source,snapshot))&&playerDocument.Games.Any(g=>g.Id==q.Id&&LaunchPathRepair.HasSettings(g)));
        if(other==null){ShowOrganizeWarning(message);return;}
        ShowOrganizeWarning("기존 항목의 범위와 겹칩니다.");
        var answer=Choose("범위 확인","기존 항목의 범위와 겹칩니다.",[new("close","닫기"),new("show","겹치는 항목 보기",true)],"close",FolderPolicy.ConflictDetails(other,path),DialogTone.Warning);
        if(answer!="show")return;
        scopeDraft=null;scopeDraftHistory.Clear();selected=other;Search.Text="";Render();
    }
}

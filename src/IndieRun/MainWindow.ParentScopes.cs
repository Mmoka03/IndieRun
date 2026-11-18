using System.IO;
namespace IndieRun;
public partial class MainWindow
{
    bool ConfirmParentScopes(List<Package> requested)
    {
        var original=requested.ToList();
        var unique=original.GroupBy(p=>Path.GetFullPath(p.Source),StringComparer.OrdinalIgnoreCase).Select(g=>g.OrderByDescending(p=>FolderPolicy.OwnsScope(p)).ThenByDescending(p=>p.Id==selected.Id).First()).ToList();
        var parents=unique.Where(p=>!unique.Any(q=>q.Id!=p.Id&&q.Source.Length<p.Source.Length&&q.Includes(p.Source))).ToList();
        var nested=new List<(Package Parent,List<Package> Children)>();
        foreach(var p in parents)
        {
            var children=packages.Where(q=>q.Id!=p.Id&&!q.Source.Equals(p.Source,StringComparison.OrdinalIgnoreCase)&&p.Includes(q.Source)&&!(snapshot!=null&&FolderPolicy.VerifiedMissing(q.Source,snapshot))).ToList();
            var blocker=children.FirstOrDefault(q=>IsAdded(q)||!FolderPolicy.Passive(q)||q.Excluded||File.Exists(Path.Combine(q.Source,LibraryStore.MarkerName))||playerDocument.Games.Any(g=>g.Id==q.Id&&LaunchPathRepair.HasSettings(g)));
            if(blocker!=null&&!IsAdded(p))
            {
                ErrorInfo("범위 확인","하위 폴더에 별도로 등록하거나 편집한 항목이 있습니다. 구성을 확인한 뒤 진행하세요.",FolderPolicy.ConflictDetails(blocker,p.Source));return false;
            }
            if(children.Count>0)nested.Add((p,children));
        }
        if(nested.Count>0||parents.Count!=original.Count)
        {
            var details=string.Join("\n\n",nested.Select(item=>"선택한 폴더: "+item.Parent.Source+"\n함께 포함할 하위 후보:\n"+string.Join('\n',item.Children.Select(c=>c.Source))));
            var text=nested.Any(item=>item.Children.Any(IsAdded))?"하위 항목의 등록 정보는 유지하며 폴더 전체를 함께 처리합니다. 하위 항목도 새 위치로 연결합니다.":"하위 실행 후보와 데이터 폴더를 모두 포함합니다. 하위 후보는 별도 게임으로 등록하거나 이동하지 않습니다.";
            if(!Confirm("포함 범위 확인",text,"폴더 전체로 진행",details))return false;
        }
        requested.RemoveAll(p=>!parents.Any(q=>q.Id==p.Id));
        foreach(var child in original.Where(p=>!parents.Any(q=>q.Id==p.Id)))organizeSelection.Remove(child.Id);
        return true;
    }
}

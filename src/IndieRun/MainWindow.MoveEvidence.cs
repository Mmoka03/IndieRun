using System.IO;
namespace IndieRun;
public partial class MainWindow
{
    async void ShowMoveLocationIssue(Package p)
    {
        if(!moveLocationIssues.TryGetValue(p.Id,out var issue))return;
        var choices=new List<DialogChoice>{new("close","닫기")};
        if(Directory.Exists(issue.Move.Source))choices.Add(new("source","원래 폴더"));
        if(Directory.Exists(issue.Move.Destination))choices.Add(new("target","도착 폴더"));
        if(issue.Move.Status=="Completed"&&Directory.Exists(issue.Move.Destination)&&!BackupEngine.Removed(issue.Backup))choices.Add(new("restore","원래 위치로 되돌리기"));
        choices.Add(new("reconnect","게임 폴더 다시 찾기",true));
        var selected=Choose("이동 위치 확인",issue.Evidence.Message,choices.ToArray(),"close","원래 위치: "+issue.Move.Source+"\n도착 위치: "+issue.Move.Destination,DialogTone.Warning,stacked:choices.Count>4,subject:PlayerRules.Title(p));
        if(selected=="source")OpenSourceFolder(issue.Move.Source);
        else if(selected=="target")OpenSourceFolder(issue.Move.Destination);
        else if(selected=="restore"){if(Confirm("원래 위치로 되돌리기",issue.Move.WithoutBackup?"별도 백업은 없습니다. 현재 파일을 원래 위치로 옮기며, 충돌이 있으면 중단합니다.":"파일을 원래 위치로 되돌립니다. 기존 파일과 충돌하면 복구를 중단하고 백업을 유지합니다.","복구",issue.Move.Source))await RunMoveJob(issue.Move,true);}
        else if(selected=="reconnect")await ReconnectGameAtAsync(p,issue.Evidence.KnownFolder);
    }
}

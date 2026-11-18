using System.IO;
namespace IndieRun;
public static class IntakeEvidence
{
    public static string ActionLabel(string solution)=>solution switch{"Save"=>"다시 저장","Resume"=>"이동 재개","Undo"=>"되돌리기 재개","Locate"=>"게임 폴더 다시 찾기","Name"=>"도착 폴더명 변경","BackupLocation"=>"백업 폴더 찾기","RecordLocation"=>"작업 기록 폴더 찾기",_=>"정리에서 확인"};
    public static IntakeResult Inspect(IntakeResult row,IReadOnlyList<WorkRecord> records)
    {
        var threshold=row.SinceUtc> DateTime.MinValue.AddSeconds(1)?row.SinceUtc.AddSeconds(-1):DateTime.MinValue;
        var recent=records.Where(r=>r.GameId==row.Id&&r.UpdatedUtc>=threshold&&r.Source.Equals(row.Source,StringComparison.OrdinalIgnoreCase)).OrderByDescending(r=>r.UpdatedUtc).ToList();
        var move=recent.FirstOrDefault(r=>r.Kind=="Move"&&r.Destination.Equals(row.Destination,StringComparison.OrdinalIgnoreCase));var backup=recent.FirstOrDefault(r=>r.Kind=="Backup");
        var confirmedMove=false;var location="위치 확인 필요";var state=row.State;var solution=row.Solution;var backupText=row.Backup.Length>0?row.Backup:"만들지 않음";
        try
        {
            BackupPaths.NoLinks(row.Source);var source=Directory.Exists(row.Source);var target=row.Destination.Length>0&&Directory.Exists(row.Destination);
            if(row.Destination.Length==0){location=source?"등록한 폴더에 있음":"폴더를 찾을 수 없음";if(!source){state="게임 폴더 확인 필요";solution="Locate";}}
            else if(move!=null&&move.Availability=="Connected")
            {
                var engine=new BackupEngine(move.Root);var job=engine.Load(move.JobId);var journal=engine.ReadMove(move.JobId,move.OperationId);var evidence=MoveEvidence.Inspect(job,journal);
                if(evidence.MatchesRecord){confirmedMove=journal.Status=="Completed";location=journal.Status=="Completed"?evidence.RetainedSource?"보관함으로 이동됨 · 제외한 자료는 이동 전 위치에 있음":"보관함으로 이동됨":"이동 전 위치로 돌아옴";if(journal.Status=="Completed"&&row.Saved)state="이동 완료";}
                else if(journal.Status is "Interrupted" or "RecoveryRequired" or "Running"){location="이동이 끝나지 않음 · 작업 기록으로 재개 가능";solution=journal.Action=="Move"?"Resume":"Undo";}
                else{state="이동 위치 확인 필요";solution="Locate";}
            }
            else if(source&&!target)location="이동 전 위치에 있음";
            else if(source&&target){location="양쪽 폴더가 있어 확인 필요";solution="Name";}
            else if(!source&&!target){location="폴더를 찾을 수 없음";solution="Locate";}
            if(backup!=null)
            {
                backupText=backup.Availability!="Connected"?"기록 있음 · 백업 위치 확인 필요":backup.State=="Completed"&&backup.Verified?"생성 완료":"백업 미완료";
                if(backup.Availability!="Connected")solution="BackupLocation";
                else if(backup.State=="Completed"&&!Directory.Exists(new BackupEngine(backup.Root).Payload(new BackupEngine(backup.Root).Load(backup.JobId))))backupText="기록 있음 · 백업 파일 확인 필요";
            }
            if(move?.WithoutBackup==true)backupText="사용 안 함";
            if(move!=null&&move.Availability!="Connected"){location="작업 위치 연결 필요 · 현재 파일 확인 전";solution=move.WithoutBackup?"RecordLocation":"BackupLocation";}
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException){location="위치 확인 필요";solution="Locate";}
        if(state=="이동 완료"&&!confirmedMove){state="이동 위치 확인 필요";if(solution=="Review")solution="Locate";}
        if(!row.Saved){state=row.Destination.Length>0&&confirmedMove?"파일 이동 완료 · 정보 저장 필요":"등록·작업 정보 저장 필요";solution="Save";}
        if(row.State.Contains("저장",StringComparison.Ordinal)&&!row.Success)solution="Save";
        return row with{State=state,Location=location,Backup=backupText,Solution=solution,Record=move??backup,Detail=row.Detail??move?.Error??backup?.Error};
    }
}

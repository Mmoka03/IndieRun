namespace IndieRun;
public partial class MainWindow
{
    bool ConfirmLegacyRestore(BackupJob job)
    {
        if(!job.LegacyIncomplete)return false;
        return Confirm("일부 폴더 백업 복원","이전 버전에서 하위 폴더가 빠진 백업입니다. 아래 데이터는 복원되지 않습니다. 원본이 남아 있다면 이동 되돌리기를 먼저 확인하세요.","백업에 있는 파일만 복원",string.Join('\n',job.Package.CandidateOmissions),destructive:false);
    }
}

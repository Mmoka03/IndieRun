using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
namespace IndieRun;
public partial class MainWindow
{
    WorkHistoryDocument workHistory=new();
    Task? historyRefresh;
    string historyError="";
    internal Func<WorkHistoryStore>? historyFactory=null;
    WorkHistoryStore History()=>historyFactory?.Invoke()??new(Path.Combine(Path.GetDirectoryName(workspaceStore.FilePath)!,"work-history.json"));
    IEnumerable<string> HistoryRoots()=>workHistory.Roots.Concat(new[]{BackupService().Root,DirectMoveService().Root}).Select(Path.TrimEndingDirectorySeparator).Distinct(StringComparer.OrdinalIgnoreCase);
    bool HistoryLoading=>historyRefresh is {IsCompleted:false};
    Task RefreshHistoryAsync()=>HistoryLoading?historyRefresh!:historyRefresh=RefreshHistoryCoreAsync();
    async Task RefreshHistoryCoreAsync()
    {
        try
        {
            var roots=HistoryRoots().ToArray();var store=History();
            var cached=await Task.Run(store.Load);if(workHistory.Records.Count==0){workHistory=cached;foreach(var record in workHistory.Records)record.Availability="Checking";if(page=="jobs"&&!closingWithoutSave)PaintHistory();}
            var known=packages.ToDictionary(p=>p.Id,p=>(p.Source,p.CompositionMoveState));
            workHistory=await Task.Run(()=>
            {
                return store.Refresh(roots,fresh=>
                {
                foreach(var entry in fresh.Records.Where(r=>r.Kind=="Move").GroupBy(r=>r.GameId).Select(g=>g.OrderByDescending(r=>r.UpdatedUtc).First()))
                {
                    if(entry.Availability!="Connected"||entry.State is not ("Completed" or "RolledBack")||!known.TryGetValue(entry.GameId,out var current)||current.CompositionMoveState.StartsWith("Reconnected:",StringComparison.Ordinal)||!current.Source.Equals(entry.Source,StringComparison.OrdinalIgnoreCase)&&!current.Source.Equals(entry.Destination,StringComparison.OrdinalIgnoreCase))continue;
                    try{var engine=new BackupEngine(entry.Root);var evidence=MoveEvidence.Inspect(engine.Load(entry.JobId),engine.ReadMove(entry.JobId,entry.OperationId));entry.LocationIssue=!evidence.MatchesRecord;if(entry.LocationIssue)entry.Error=evidence.Message;}
                    catch(Exception ex)when(LibraryFault(ex)){entry.LocationIssue=true;entry.Error=ex.Message;}
                }
                });
            });historyError="";
        }
        catch(Exception ex)when(LibraryFault(ex)){historyError=ex.Message;foreach(var r in workHistory.Records)r.Availability="Unavailable";}
        finally{historyRefresh=null;if(page=="jobs"&&!closingWithoutSave)PaintHistory();}
    }
    void RenderBackupJobs(){PaintHistory();if(!HistoryLoading)_=RefreshHistoryAsync();}
    void PaintHistory()
    {
        JobsContent.Children.Clear();RenderOperationSummary();
        var heading=new DockPanel{Margin=new Thickness(0,0,0,16)};
        var check=Btn("다시 확인",async()=>await RefreshHistoryAsync());check.IsEnabled=!HistoryLoading&&!backupBusy;DockPanel.SetDock(check,Dock.Right);heading.Children.Add(check);
        var change=Btn("백업 위치 변경",async()=>await ChooseBackupRootAsync());change.IsEnabled=!backupBusy&&!fileOperationPreparing;DockPanel.SetDock(change,Dock.Right);heading.Children.Add(change);
        var manage=Btn("백업 관리",ShowBackupManager);manage.IsEnabled=!backupBusy;manage.ToolTip="현재 백업 위치의 백업 관리";DockPanel.SetDock(manage,Dock.Right);heading.Children.Add(manage);
        var pathLabel=WorkText("백업 위치 · "+Path.GetFileName(Path.TrimEndingDirectorySeparator(backupStorageRoot)),12,"Text.Muted");pathLabel.VerticalAlignment=VerticalAlignment.Center;pathLabel.TextTrimming=TextTrimming.CharacterEllipsis;pathLabel.TextWrapping=TextWrapping.NoWrap;pathLabel.ToolTip=backupStorageRoot;pathLabel.Margin=new Thickness(0,0,12,0);heading.Children.Add(pathLabel);JobsContent.Children.Add(heading);
        if(HistoryLoading)JobsContent.Children.Add(WorkText("저장된 기록을 표시하고 있습니다. 위치를 확인하는 중입니다…",12,"Text.Muted"));
        if(historyError.Length>0){var error=new StackPanel();error.Children.Add(WorkText("작업 기록을 확인하지 못했습니다. 원본 기록과 파일은 보존했습니다.",13,"Warning.Text"));error.Children.Add(WorkDetails(historyError));JobsContent.Children.Add(WorkCard(error,"Warning.Surface"));}
        var records=workHistory.Records;var attention=records.Where(r=>r.Attention).ToList();
        foreach(var record in PageItems(attention,"history-attention","",JobsContent,PaintHistory))JobsContent.Children.Add(HistoryCard(record));
        var done=records.Where(r=>!r.Attention).GroupBy(r=>(r.Root.ToUpperInvariant(),r.JobId)).OrderByDescending(g=>g.Max(r=>r.UpdatedUtc)).SelectMany(g=>g.OrderBy(r=>r.Kind=="Move"?0:r.Kind=="Backup"?1:2).ThenByDescending(r=>r.UpdatedUtc)).ToList();
        if(done.Count>0){var completed=new StackPanel();foreach(var record in PageItems(done,"history-done","",completed,PaintHistory))completed.Children.Add(HistoryCard(record));JobsContent.Children.Add(new Expander{Header=$"완료 기록 · 이동 {done.Count(r=>r.Kind=="Move")}건 · 백업 {done.Count(r=>r.Kind=="Backup")}개",Content=completed,IsExpanded=true,Margin=new Thickness(0,8,0,16)});}
        if(records.Count==0&&!HistoryLoading&&historyError.Length==0)JobsContent.Children.Add(WorkText("아직 작업 기록이 없습니다.",13,"Text.Muted"));
        JobsContent.Children.Add(new Expander{Header="다른 위치의 기록",Content=Btn("이전 백업 위치 추가",async()=>{var picker=new OpenFolderDialog{Title="백업 기록이 보관된 상위 폴더 선택"};if(picker.ShowDialog(this)==true)try{await AddHistoryRootAsync(picker.FolderName);}catch(Exception ex)when(LibraryFault(ex)){ErrorInfo("기록 폴더 확인","같은 작업 기록이 있는 폴더인지 확인하세요.",ex.Message);}}),Margin=new Thickness(0,12,0,0)});
        if(BatchRenames().HasPending||File.Exists(BatchRenames().Last))JobsContent.Children.Add(Btn("이름 변경 기록",()=>ShowRenameProgress()));
    }
    static string HistoryStatus(WorkRecord record)=>record.Kind=="Move"?MoveStatus(record.State):record.Kind=="Restore"?record.State switch
    {
        "Completed"=>record.PartialCoverage?"일부 복원 완료":"복원 완료",
        "Preparing"=>"복원 준비 중", "Restoring"=>"복원 중", "Cancelled"=>"복원 중지", "Failed"=>"복원 확인 필요", _=>"복원 기록 확인 필요"
    }:BackupState(record.State);
    Border HistoryCard(WorkRecord record)
    {
        var body=new StackPanel();body.Children.Add(WorkText(record.Title.Length>0?record.Title:"작업 기록",17,"Text.Primary"));
        var availability=record.Availability switch{"Offline"=>record.WithoutBackup?"작업 기록 위치 연결 안 됨":"백업 위치 연결 안 됨","Unavailable"=>"기록 위치 접근 확인 필요","MissingRecord"=>"원본 기록 확인 필요","Checking"=>"위치 확인 중",_=>""};
        if(record.PartialCoverage)body.Children.Add(WorkText(record.Kind=="Restore"?"일부 폴더 복원 · 제외된 데이터는 복원되지 않음":record.WithoutBackup?"이전 버전의 일부 폴더 이동 기록":"이전 버전의 일부 폴더 백업",12,"Warning.Text"));
        var kind=record.Kind switch{"Move"=>record.WithoutBackup?"백업 없이 이동":"백업 후 이동","Restore"=>"백업에서 복원","Preparation"=>"이동 준비",_=>"백업"};
        body.Children.Add(WorkText(availability.Length>0?availability:record.LocationIssue?"이동 위치 확인 필요":HistoryStatus(record),13,record.Attention?"Warning.Text":"Success.Text"));
        body.Children.Add(WorkText(kind+" · "+record.UpdatedUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm"),12,"Text.Muted"));
        if(availability.Length>0)body.Children.Add(WorkText("마지막 기록 · "+(HistoryStatus(record))+" · 파일 상태는 확인되지 않았습니다.",12));
        var actions=new WrapPanel{Margin=new Thickness(0,12,0,0)};body.Children.Add(actions);
        void Action(string label,Action run,bool primary=false){var b=Btn(label,run,primary);b.IsEnabled=!backupBusy&&!fileOperationPreparing&&record.Availability!="Checking";b.Margin=new Thickness(0,0,8,6);actions.Children.Add(b);}
        if(record.Availability!="Connected")
        {
            Action("다시 확인",async()=>await RefreshHistoryAsync(),true);
            if(BackupPaths.Id(record.JobId))Action(record.WithoutBackup?"작업 기록 폴더 찾기":"백업 폴더 찾기",async()=>await FindHistoryRootAsync(record));
        }
        else if(record.LocationIssue){Action("게임 폴더 다시 찾기",async()=>{var game=packages.Find(p=>p.Id==record.GameId);if(game!=null)await ReconnectGameAsync(game);else ReviewHistoryGame(record.GameId);},true);}
        else if(record.State=="RecordError"){Action("다시 확인",async()=>await RefreshHistoryAsync(),true);if(BackupPaths.Id(record.JobId))Action(record.WithoutBackup?"작업 기록 폴더 찾기":"백업 폴더 찾기",async()=>await FindHistoryRootAsync(record));}
        else
        {
            if(record.Kind=="Move"&&record.State is "Interrupted" or "RecoveryRequired")Action(record.Action=="Move"?"이동 재개":"되돌리기 재개",async()=>await RunHistoryMoveAsync(record,record.Action!="Move"),true);
            else if(record.Kind=="Backup"&&record.State is "Deleting" or "DeleteFailed")Action("삭제 다시 시도",()=>ShowBackupManagerFor(record.Root),true);
            else if(record.Kind=="Backup"&&!record.Verified&&!record.BackupRemoved)Action("백업 재개",async()=>await RunHistoryBackupAsync(record,"resume"),true);
            else if(record.Kind=="Restore"&&record.State is "Interrupted" or "Cancelled" or "Failed")Action("복원 재개",async()=>await RunHistoryBackupAsync(record,"restore-resume"),true);
            else if(record.Kind=="Backup"&&CanRestore(record))Action("백업에서 복원",async()=>await RunHistoryBackupAsync(record,"restore"),true);
            else if(record.Kind=="Preparation")Action("정리에서 확인",()=>ReviewHistoryGame(record.GameId));
        }
        if(record.Kind=="Move"&&record.Availability=="Connected"&&!record.BackupRemoved)
        {
            var backup=workHistory.Records.FirstOrDefault(r=>r.Kind=="Backup"&&r.JobId==record.JobId&&r.Root.Equals(record.Root,StringComparison.OrdinalIgnoreCase)&&CanRestore(r));
            if(backup!=null)Action("백업에서 복원",async()=>await RunHistoryBackupAsync(backup,"restore"));
        }
        var other=new StackPanel();
        if(record.Availability=="Connected"&&record.State!="RecordError")
        {
            if(record.Kind=="Move"&&!record.BackupRemoved&&record.State is "Completed" or "Interrupted" or "RecoveryRequired")other.Children.Add(Btn("원래 위치로 되돌리기",async()=>{if(Confirm("원래 위치로 되돌리기","게임을 종료한 뒤 진행하세요. 파일 충돌이나 변경이 있으면 중단합니다.","되돌리기"))await RunHistoryMoveAsync(record,true);}));
            if(record.Kind=="Move"&&record.BackupRemoved)other.Children.Add(WorkText("백업이 삭제되어 이 작업을 되돌릴 수 없습니다.",12,"Text.Muted"));
            if(record.Kind=="Backup"&&CanRestore(record))other.Children.Add(Btn("다시 검증",async()=>await RunHistoryBackupAsync(record,"verify")));
            if(record.Kind=="Move")other.Children.Add(Btn("게임 폴더 열기",()=>OpenSourceFolder(record.State=="Completed"?record.Destination:record.Source)));
            if(record.Kind=="Backup")other.Children.Add(Btn("백업 폴더 열기",()=>OpenSourceFolder(Path.Combine(record.Root,record.JobId))));
            if(record.Kind=="Restore")other.Children.Add(Btn("복원 폴더 열기",()=>OpenSourceFolder(record.Destination)));
            if(record.Kind=="Backup")other.Children.Add(Btn("이 위치의 백업 관리",()=>ShowBackupManagerFor(record.Root)));
        }
        other.Children.Add(WorkDetails("작업 위치: "+record.Root+"\n원본: "+record.Source+"\n도착: "+record.Destination+(record.Error==null?"":"\n\n"+record.Error)));
        body.Children.Add(new Expander{Header="다른 방법·상세 정보",Content=other,Margin=new Thickness(0,8,0,0)});
        return WorkCard(body,record.Attention?"Warning.Surface":"Surface.Secondary");
    }
    void ReviewHistoryGame(string id){SetPage("organize");var p=packages.Find(p=>p.Id==id);if(p!=null){selected=p;organizeSelection.Clear();organizeSelection.Add(id);}Render();}
    async Task AddHistoryRootAsync(string root)
    {if(backupBusy)return;if(historyRefresh!=null)await historyRefresh;await Task.Run(()=>{BackupPaths.NoLinks(root);if(!Directory.Exists(root))throw new IOException("기록 폴더를 찾지 못했습니다.");History().Register(new[]{root});});await RefreshHistoryAsync();PaintHistory();}
    async Task FindHistoryRootAsync(WorkRecord record)
    {var picker=new OpenFolderDialog{Title=record.WithoutBackup?"작업 기록이 보관된 폴더 선택":"백업 기록이 보관된 상위 폴더 선택"};if(picker.ShowDialog(this)==true)try{await ReconnectHistoryRootAsync(record,picker.FolderName);}catch(Exception ex)when(LibraryFault(ex)){ErrorInfo("기록 폴더 확인","같은 작업 기록이 있는 폴더인지 확인하세요.",ex.Message);}}
    async Task ReconnectHistoryRootAsync(WorkRecord record,string root)
    {if(historyRefresh!=null)await historyRefresh;workHistory=await Task.Run(()=>History().Reconnect(record,root));await RefreshHistoryAsync();PaintHistory();}
    async Task RunHistoryMoveAsync(WorkRecord record,bool undo)
    {
        try{var move=await Task.Run(()=>new BackupEngine(record.Root).ReadMove(record.JobId,record.OperationId));await RunMoveJob(move,undo);}
        catch(Exception ex)when(LibraryFault(ex)||ex is InvalidOperationException){ReportMoveProblem("이동 기록 확인","원본 기록을 확인하지 못해 파일을 변경하지 않았습니다.",ex.Message);}
    }
    static bool CanRestore(WorkRecord record)=>record.Availability=="Connected"&&record.State=="Completed"&&record.Verified&&!record.BackupRemoved&&!record.WithoutBackup;
    async Task RunHistoryBackupAsync(WorkRecord record,string action)
    {
        if(backupBusy||fileOperationPreparing)return;
        var engine=new BackupEngine(record.Root);string? target=null;
        var acknowledge=false;
        if(action.StartsWith("restore",StringComparison.Ordinal))
        {
            BackupJob job;
            try{job=await Task.Run(()=>engine.Load(record.JobId));}
            catch(Exception ex)when(LibraryFault(ex)){ErrorInfo("백업 확인","백업 기록을 읽지 못했습니다. 위치와 접근 권한을 확인하세요.",ex.Message);return;}
            if(job.WithoutBackup||job.Status!="Completed"||!job.SourceVerified){ErrorInfo("백업 확인","복원할 수 있는 완료된 백업이 없습니다.","작업 기록에서 백업 상태를 다시 확인하세요.");return;}
            acknowledge=ConfirmLegacyRestore(job);if(job.LegacyIncomplete&&!acknowledge)return;
            if(action=="restore")
            {
                var dialog=BuildRestorePlan(job,engine.Root,path=>target=path);dialog.ShowDialog();if(target==null)return;
            }
        }
        await RunFileOperation(action=="verify"?"백업 검증":action.StartsWith("restore")?(acknowledge?"일부 폴더 복원":"복원"):"백업 재개",async(e,p,t)=>{if(action=="resume")await e.ResumeAsync(record.JobId,p,t);else if(action=="verify")await e.VerifyAsync(record.JobId,p,t);else if(action=="restore-resume")await e.ResumeRestoreAsync(record.JobId,record.OperationId,p,t,acknowledge);else await e.RestoreAsync(record.JobId,target!,p,t,acknowledge);},null,record.Title,engine,record.WithoutBackup);
    }
}

using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace IndieRun;

public partial class MainWindow
{
    string backupStorageRoot=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","backups"));
    bool backupBusy;
    bool fileOperationPreparing;
    bool backupCloseRequested;
    Task? backupTask;
    CancellationTokenSource? backupCancellation;
    ProgressBar? backupLiveBar;
    double backupPercent;
    static string BackupState(string value)=>value switch {"Preparing"=>"준비 중","Copying"=>"백업 중","Verifying"=>"백업 확인 중","Completed"=>"백업 완료","Interrupted"=>"중단됨","Cancelled"=>"취소됨","Failed"=>"실패","Invalid"=>"검증 실패","RecordError"=>"기록 확인 필요","Deleting"=>"삭제 중단","DeleteFailed"=>"삭제 확인 필요","Deleted"=>"백업 삭제됨","Restoring"=>"복원 중","Moving"=>"이동 중","RollingBack"=>"원래 위치로 되돌리는 중",_=>value};
    internal Func<BackupEngine>? backupEngineFactory=null;
    BackupEngine BackupService()=>backupEngineFactory?.Invoke()??new(backupStorageRoot);
    async Task RecoverBackupsAsync()
    {
        try
        {
            var service=BackupService();var interrupted=await Task.Run(()=>{var jobs=service.ListJobs();return AllMoves(true).Count(m=>m.Status is "Interrupted" or "RecoveryRequired")+jobs.Count(j=>j.Status=="Interrupted")+jobs.Sum(j=>service.ListRestores(j.Id).Count(r=>r.Status=="Interrupted"));});
            if(interrupted>0)Say($"중단된 이동·백업·복원 {interrupted}건 · 작업 탭에서 확인하세요");
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException){Say("백업 기록을 확인하지 못했습니다");Notice.ToolTip=ex.Message;}
    }
    async Task ChooseBackupRootAsync()
    {
        if(RenamePending||backupBusy||fileOperationPreparing){Info("백업 위치 변경","진행 중인 작업을 먼저 완료하거나 되돌리세요.");return;}
        var picker=new OpenFolderDialog{Title="앞으로 백업을 보관할 폴더 선택"};if(picker.ShowDialog(this)==true)await ChangeBackupRootAsync(picker.FolderName);
    }
    async Task<bool> ChangeBackupRootAsync(string target)
    {
        if(backupBusy||fileOperationPreparing||RenamePending)return false;
        try
        {
            await RefreshHistoryAsync();if(historyError.Length>0)throw new IOException(historyError);
            if(workHistory.Records.Any(r=>r.Root.Equals(BackupService().Root,StringComparison.OrdinalIgnoreCase)&&r.Kind=="Move"&&r.State is "Running" or "Interrupted" or "RecoveryRequired" or "RecordError")){Info("이동 기록 확인","중단된 이동을 먼저 완료하거나 되돌리세요.");return false;}
            await Task.Run(()=>History().Register(new[]{BackupService().Root,Path.GetFullPath(target)}));
            var old=backupStorageRoot;backupStorageRoot=Path.GetFullPath(target);if(!FlushWorkspace()){backupStorageRoot=old;ShowWorkspaceSaveIssue();return false;}
            await RefreshHistoryAsync();Say("백업 위치를 변경했습니다. 이전 작업 기록도 유지됩니다.");return true;
        }
        catch(Exception ex)when(LibraryFault(ex)){ErrorInfo("백업 위치 변경","작업 기록을 보관하지 못해 위치를 변경하지 않았습니다.",ex.Message);return false;}
    }
    async Task StartBackupBatchAsync(List<Package> list)
    {
        if(list.Count==0||backupBusy||fileOperationPreparing||movePreparing||scanning||running)return;
        if(!await VerifyCurrentSelectionAsync(list,false))return;
        if(list.Any(p=>p.ReviewState.Length>0)){Info("원본 확인","변경되었거나 찾을 수 없는 패키지를 먼저 확인해 주세요.");return;}
        if(!FlushWorkspace()){ShowWorkspaceSaveIssue();return;}
        var captured=JsonSerializer.Deserialize<List<Package>>(JsonSerializer.Serialize(list))!;
        var acceptedChecks=list.Where(p=>verifiedTransferChecks.ContainsKey(p.Id)).ToDictionary(p=>p.Id,p=>Clone(verifiedTransferChecks[p.Id]));
        var tags=JsonSerializer.Deserialize<List<TagDefinition>>(JsonSerializer.Serialize(catalog.Tags))!;
        await RunBackupOperation("백업",async(engine,progress,token)=>
        {
            for(var i=0;i<captured.Count;i++)
            {
                token.ThrowIfCancellationRequested();var p=captured[i];
                await BeginOperationItem(p,"백업 준비 중");
                if(!acceptedChecks.TryGetValue(p.Id,out var accepted))throw new IOException("백업 전 확인 기록이 없습니다. 다시 검색한 뒤 백업하세요.");
                var before=GameScanner.Scan(p.Source,cancellation:token);var probe=Clone(p);WorkspaceReconciler.Check(probe,accepted,before);
                if(probe.ReviewState.Length>0)throw new IOException("확인 후 폴더 내용이 달라졌거나 읽을 수 없습니다. 다시 검색한 뒤 백업하세요.");
                var job=await engine.CreateAsync(p,tags,progress,token);
                await MarkOperationItem(p.Id,"Completed");
                await Dispatcher.InvokeAsync(()=>{var original=packages.Find(q=>q.Id==job.Package.Id);if(original!=null)original.Backup=true;QueueWorkspaceSave();RenderBackupJobs();});
            }
        },captured);
    }
    Task RunBackupOperation(string name,Func<BackupEngine,IProgress<BackupProgress>,CancellationToken,Task> action,IReadOnlyList<Package>? batch=null,string? subject=null)=>RunFileOperation(name,action,batch,subject,BackupService());
    async Task RunFileOperation(string name,Func<BackupEngine,IProgress<BackupProgress>,CancellationToken,Task> action,IReadOnlyList<Package>? batch,string? subject,BackupEngine service,bool withoutBackup=false)
    {
        if(backupBusy||fileOperationPreparing||scanning)return;
        fileOperationPreparing=true;
        operationStatus=new OperationStatus(name,batch==null?[new OperationItem("single",subject??name)]:batch.Select(p=>new OperationItem(p.Id,PlayerRules.Title(p)))){WithoutBackup=withoutBackup};
        if(batch==null)operationStatus.Begin("single");
        await RefreshHistoryAsync();
        try{await Task.Run(()=>History().Register(new[]{service.Root}));}
        catch(Exception ex)when(LibraryFault(ex)){fileOperationPreparing=false;foreach(var item in operationStatus.Items){item.State="Failed";item.Error=ex.Message;}operationStatus.Finish(true);ReportMoveProblem("작업 기록 확인","작업 기록을 보관하지 못해 파일 작업을 시작하지 않았습니다.",ex.Message);return;}
        backupBusy=true;fileOperationPreparing=false;stopPending=false;backupCloseRequested=false;backupCancellation=new CancellationTokenSource();backupPercent=-1;liveDetail="";
        SetPage("jobs");PlayerNav.IsEnabled=false;LibraryNav.IsEnabled=false;SaveStatus.IsEnabled=false;Workbench.IsEnabled=false;OrganizeNav.IsEnabled=TagsNav.IsEnabled=RescanButton.IsEnabled=SourceButton.IsEnabled=false;RenderBackupJobs();
        var run=operationStatus;var progress=new Progress<BackupProgress>(value=>
        {
            if(!backupBusy||operationStatus!=run||run.Finished)return;
            backupPercent=value.Total>0?100d*value.Completed/value.Total:-1;
            run.Stage=OperationStatus.ProgressLabel(value.Stage);liveDetail=value.File??"";UpdateOperationLive();
        });
        bool stopped=false;
        try
        {
            backupTask=Task.Run(()=>action(service,progress,backupCancellation.Token));await backupTask;
            if(batch==null&&run.Items.Single().State=="Pending")run.Mark("single","Completed");
            run.Finish(stopped||stopPending);Say(run.Heading);
        }
        catch(OperationCanceledException)
        {stopped=true;var current=run.Items.Find(i=>i.Id==run.CurrentId);if(current?.State=="Pending")run.Mark(current.Id,"Interrupted");Say(name+" 중단 · 작업 기록을 확인하세요");}
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            stopped=stopPending;var current=run.Items.Find(i=>i.Id==run.CurrentId);if(current!=null&&current.State is not ("Failed" or "Interrupted" or "RolledBack"))run.Mark(current.Id,"Failed",ex.Message);
            Say(name+" · 확인 필요");Notice.ToolTip=ex.Message;if(!backupCloseRequested&&!stopPending&&!intakeInProgress)ErrorInfo(name+" 확인","작업을 완료하지 못했습니다. 아래 작업 기록에서 재개하거나 복구할 수 있습니다.",ex.Message);
        }
        finally
        {
            if(snapshot!=null){var observed=snapshot;run.Stage="목록 확인 중";UpdateOperationLive();await Task.Run(()=>observed.PrepareIndex());}
            if(!backupCloseRequested)await SyncMovedPackagesAsync();
            await RefreshHistoryAsync();await RefreshHistoryAsync();
            run.Finish(stopped||stopPending);backupBusy=false;backupTask=null;backupCancellation.Dispose();backupCancellation=null;
            if(run.NeedsAttention||run.Stopped)Say(run.Heading);
            PlayerNav.IsEnabled=true;LibraryNav.IsEnabled=true;SaveStatus.IsEnabled=true;Workbench.IsEnabled=true;OrganizeNav.IsEnabled=TagsNav.IsEnabled=RescanButton.IsEnabled=SourceButton.IsEnabled=true;RenderBackupJobs();QueueWorkspaceSave();
        }
    }
    async void BackupClosing(object? sender,CancelEventArgs e)
    {
        if(e.Cancel||!backupBusy)return;e.Cancel=true;backupCloseRequested=true;backupCancellation?.Cancel();
        if(backupTask!=null)try{await backupTask;}catch(Exception){}
        while(backupBusy)await Task.Delay(25);
        await Dispatcher.InvokeAsync(Close);
    }
}




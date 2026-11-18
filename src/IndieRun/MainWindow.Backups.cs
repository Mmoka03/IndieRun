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
    bool backupCloseRequested;
    Task? backupTask;
    CancellationTokenSource? backupCancellation;
    ProgressBar? backupLiveBar;
    double backupPercent;
    static string BackupState(string value)=>value switch {"Preparing"=>"준비 중","Copying"=>"백업 중","Verifying"=>"백업 확인 중","Completed"=>"백업 완료","Interrupted"=>"중단됨","Cancelled"=>"취소됨","Failed"=>"실패","Invalid"=>"검증 실패","RecordError"=>"기록 확인 필요","Deleting"=>"삭제 중단","DeleteFailed"=>"삭제 확인 필요","Deleted"=>"백업 삭제됨","Restoring"=>"복원 중","Moving"=>"이동 중","RollingBack"=>"원래 위치 복구 중",_=>value};
    BackupEngine BackupService()=>new(backupStorageRoot);
    async Task RecoverBackupsAsync()
    {
        try
        {
            var service=BackupService();var jobs=await Task.Run(()=>service.ListJobs());
            var interrupted=service.ListMoves().Count(m=>m.Status is "Interrupted" or "RecoveryRequired")+jobs.Count(j=>j.Status=="Interrupted")+jobs.Sum(j=>service.ListRestores(j.Id).Count(r=>r.Status=="Interrupted"));
            if(interrupted>0)Say($"중단된 이동·백업·복원 {interrupted}건 · 작업 탭에서 확인하세요");
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException){Say("백업 기록을 확인하지 못했습니다");Notice.ToolTip=ex.Message;}
    }
    void ChooseBackupRoot()
    {
        if(RenamePending){Info("이름 변경 복구","진행 중인 이름 변경을 먼저 완료하거나 되돌리세요.");return;}
        if(backupBusy)return;
        try
        {
            if(BackupService().ListMoves().Any(m=>m.Status is "Running" or "Interrupted" or "RecoveryRequired" or "RecordError"))
            {Info("이동 기록 확인","중단된 이동을 먼저 복구하거나 완료해 주세요.");return;}
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException){ErrorInfo("이동 기록 확인","이동 기록을 읽지 못했습니다.",ex.Message);return;}
        var picker=new OpenFolderDialog {Title="백업을 보관할 폴더 선택"};
        if(picker.ShowDialog(this)!=true)return;
        backupStorageRoot=picker.FolderName;QueueWorkspaceSave();if(page=="jobs")RenderBackupJobs();
    }
    void RenderBackupJobs()
    {
        JobsContent.Children.Clear();RenderOperationSummary();
        var heading=new DockPanel {Margin=new Thickness(0,0,0,14)};
        if(BatchRenames().HasPending||File.Exists(BatchRenames().Last)){var names=Btn("이름 변경 기록",()=>ShowRenameProgress());names.IsEnabled=!backupBusy;DockPanel.SetDock(names,Dock.Right);heading.Children.Add(names);}
        var manage=Btn("백업 관리",ShowBackupManager);manage.IsEnabled=!backupBusy;DockPanel.SetDock(manage,Dock.Right);heading.Children.Add(manage);
        var change=Btn("백업 위치 변경",()=>{ChooseBackupRoot();RenderBackupJobs();});change.IsEnabled=!backupBusy;DockPanel.SetDock(change,Dock.Right);heading.Children.Add(change);
        var folder=Btn("폴더 열기",()=>OpenSourceFolder(backupStorageRoot));folder.IsEnabled=Directory.Exists(backupStorageRoot);DockPanel.SetDock(folder,Dock.Right);heading.Children.Add(folder);
        heading.Children.Add(new TextBlock {Text="백업 위치 · "+Path.GetFileName(Path.TrimEndingDirectorySeparator(backupStorageRoot)),FontSize=12,Foreground=ThemeBrush("Text.Muted"),TextTrimming=TextTrimming.CharacterEllipsis,ToolTip=backupStorageRoot,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,18,0)});JobsContent.Children.Add(heading);
        List<BackupJob> jobs;var service=BackupService();
        try{jobs=service.ListJobs(!backupBusy);}catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException){JobsContent.Children.Add(WorkText("백업 기록을 읽지 못했습니다.",13,"Warning.Text"));JobsContent.Children.Add(WorkDetails(ex.Message));return;}
        RenderMoveJobs(service,jobs,JobsContent,true);
        var deleteIssues=jobs.Count(j=>j.Status is "Deleting" or "DeleteFailed");if(deleteIssues>0)manage.Content=$"백업 관리 · 삭제 확인 {deleteIssues}개";
        var completedRecords=new StackPanel();var completedMoves=RenderMoveJobs(service,jobs,completedRecords,false);
        if(jobs.Count==0){JobsContent.Children.Add(WorkText(backupBusy?"작업 기록을 준비하고 있습니다.":"아직 백업이 없습니다.",13,"Text.Muted"));return;}
        JobsContent.Children.Add(new TextBlock {Text=$"백업 {jobs.Count(j=>j.Status!="Deleted")}개 · {FormatBytes(jobs.Where(j=>j.Status=="Completed").Sum(j=>j.TotalBytes))}",FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,18)});
        var completedBackups=0;
        foreach(var job in jobs.Where(j=>!BackupEngine.Removed(j)))
        {
            var card=new StackPanel();var title=string.IsNullOrWhiteSpace(job.Package.Alias)?job.OriginalFolder:job.Package.Alias;
            card.Children.Add(Text(title,18));card.Children.Add(new TextBlock {Text=$"{BackupState(job.Status)} · {job.Files.Count}개 파일 · {FormatBytes(job.TotalBytes)}",Foreground=ThemeBrush(job.Status=="Completed"?"Success.Text":"Warning.Text"),Margin=new Thickness(0,6,0,8),FontSize=12});
            if(job.Status!="Completed")card.Children.Add(WorkText(job.Status=="RecordError"?"기록을 읽지 못했습니다. 백업 폴더를 확인하세요.":job.SourceVerified?"백업을 다시 확인해 주세요.":"백업이 끝나지 않았습니다. 재개하거나 백업 폴더를 확인하세요.",12));
            card.Children.Add(WorkDetails("원본: "+job.SourceRoot+"\n백업: "+Path.Combine(backupStorageRoot,job.Id)+(job.Error==null?"":"\n\n"+job.Error)));
            var actions=new WrapPanel {Margin=new Thickness(0,12,0,0)};
            if(job.Status!="RecordError")
            {
                if(!job.SourceVerified)actions.Children.Add(Btn("백업 재개",async()=>await RunBackupOperation("백업 재개",async(engine,progress,token)=>{await engine.ResumeAsync(job.Id,progress,token);},subject:title))); 
                if(job.SourceVerified)actions.Children.Add(Btn("다시 검증",async()=>await RunBackupOperation("백업 검증",async(engine,progress,token)=>{await engine.VerifyAsync(job.Id,progress,token);},subject:title))); 
                if(job.Status=="Completed")actions.Children.Add(Btn("별도 위치에 복원",async()=>
                {
                    var picker=new OpenFolderDialog {Title="새 복원 폴더를 만들 위치 선택"};if(picker.ShowDialog(this)!=true)return;
                    await RunBackupOperation("복원",async(engine,progress,token)=>{await engine.RestoreAsync(job.Id,picker.FolderName,progress,token);},subject:title);
                }));
            }
            actions.Children.Add(Btn("백업 폴더",()=>OpenSourceFolder(Path.Combine(backupStorageRoot,job.Id))));
            foreach(Button button in actions.Children){button.IsEnabled=!backupBusy;button.Margin=new Thickness(0,0,8,6);}card.Children.Add(actions);
            if(job.Status!="RecordError")foreach(var restore in service.ListRestores(job.Id))
            {
                var row=new StackPanel {Margin=new Thickness(0,16,0,0)};
                row.Children.Add(WorkText(restore.Status=="Completed"?"복원 완료":"복원 · "+BackupState(restore.Status),13,restore.Status=="Completed"?"Success.Text":"Warning.Text"));
                row.Children.Add(WorkDetails("복원 위치: "+restore.TargetContainer+(restore.Error==null?"":"\n\n"+restore.Error)));
                var restoreActions=new WrapPanel {Margin=new Thickness(0,8,0,0)};
                if(restore.Status is "Interrupted" or "Cancelled" or "Failed")restoreActions.Children.Add(Btn("복원 재개",async()=>await RunBackupOperation("복원 재개",async(engine,progress,token)=>{await engine.ResumeRestoreAsync(job.Id,restore.Id,progress,token);},subject:title))); 
                if(Directory.Exists(restore.TargetContainer))restoreActions.Children.Add(Btn("복원 폴더",()=>OpenSourceFolder(restore.TargetContainer)));
                foreach(Button button in restoreActions.Children){button.IsEnabled=!backupBusy;button.Margin=new Thickness(0,0,8,6);}row.Children.Add(restoreActions);card.Children.Add(row);
            }
            var attention=job.Status!="Completed"||service.ListRestores(job.Id).Any(r=>r.Status!="Completed");
            if(attention){JobsContent.Children.Add(WorkCard(card));}
            else{completedBackups++;completedRecords.Children.Add(WorkCard(card));}
        }
        if(completedRecords.Children.Count>0){var history=new Expander {Header="완료 기록"+(completedMoves>0?$" · 이동 {completedMoves}건":"")+(completedBackups>0?$" · 백업 {completedBackups}개":""),Content=completedRecords,IsExpanded=false,Margin=new Thickness(0,4,0,16)};history.SetResourceReference(ForegroundProperty,"Text.Secondary");JobsContent.Children.Add(history);}
        JobsContent.Children.Add(WorkText("백업 관리에서 용량을 확인하고, 필요한 백업만 남길 수 있습니다.",12,"Text.Muted"));
    }
    async Task StartBackupBatchAsync(List<Package> list)
    {
        if(backupBusy)return;
        if(list.Any(p=>p.ReviewState.Length>0)){Info("원본 확인","변경되었거나 찾을 수 없는 패키지를 먼저 확인해 주세요.");return;}
        if(!FlushWorkspace()){Info("계획 저장","정리 계획을 먼저 저장해 주세요.");return;}
        var captured=JsonSerializer.Deserialize<List<Package>>(JsonSerializer.Serialize(list))!;
        var tags=JsonSerializer.Deserialize<List<TagDefinition>>(JsonSerializer.Serialize(catalog.Tags))!;
        await RunBackupOperation("백업",async(engine,progress,token)=>
        {
            for(var i=0;i<captured.Count;i++)
            {
                token.ThrowIfCancellationRequested();var p=captured[i];
                await BeginOperationItem(p,"백업 준비 중");
                var job=await engine.CreateAsync(p,tags,progress,token);
                await MarkOperationItem(p.Id,"Completed");
                await Dispatcher.InvokeAsync(()=>{var original=packages.Find(q=>q.Id==job.Package.Id);if(original!=null)original.Backup=true;QueueWorkspaceSave();RenderBackupJobs();});
            }
        },captured);
    }
    async Task RunBackupOperation(string name,Func<BackupEngine,IProgress<BackupProgress>,CancellationToken,Task> action,IReadOnlyList<Package>? batch=null,string? subject=null)
    {
        if(backupBusy||scanning)return;
        operationStatus=new OperationStatus(name,batch==null?[new OperationItem("single",subject??name)]:batch.Select(p=>new OperationItem(p.Id,PlayerRules.Title(p))));
        if(batch==null)operationStatus.Begin("single");
        backupBusy=true;stopPending=false;backupCloseRequested=false;backupCancellation=new CancellationTokenSource();backupPercent=-1;liveDetail="";
        SetPage("jobs");PlayerNav.IsEnabled=false;LibraryNav.IsEnabled=false;SaveStatus.IsEnabled=false;Workbench.IsEnabled=false;OrganizeNav.IsEnabled=TagsNav.IsEnabled=RescanButton.IsEnabled=SourceButton.IsEnabled=false;RenderBackupJobs();
        var run=operationStatus;var service=BackupService();var progress=new Progress<BackupProgress>(value=>
        {
            if(!backupBusy||operationStatus!=run||run.Finished)return;
            backupPercent=value.Total>0?100d*value.Completed/value.Total:-1;
            run.Stage=OperationStatus.ProgressLabel(value.Stage);liveDetail=value.File??"";UpdateOperationLive();
        });
        bool stopped=false;
        try
        {
            backupTask=Task.Run(()=>action(service,progress,backupCancellation.Token));await backupTask;
            if(batch==null)run.Mark("single","Completed");
            Say(run.Failed>0?name+" · 결과 확인 필요":name+" 완료");
        }
        catch(OperationCanceledException)
        {stopped=true;var current=run.Items.Find(i=>i.Id==run.CurrentId);if(current?.State=="Pending")run.Mark(current.Id,"Interrupted");Say(name+" 중단 · 작업 기록을 확인하세요");}
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            stopped=stopPending;var current=run.Items.Find(i=>i.Id==run.CurrentId);if(current!=null&&current.State is not ("Failed" or "Interrupted" or "RolledBack"))run.Mark(current.Id,"Failed",ex.Message);
            Say(name+" · 확인 필요");Notice.ToolTip=ex.Message;if(!backupCloseRequested&&!stopPending)ErrorInfo(name+" 확인","작업을 완료하지 못했습니다. 아래 작업 기록에서 재개하거나 복구할 수 있습니다.",ex.Message);
        }
        finally
        {
            run.Finish(stopped);backupBusy=false;backupTask=null;backupCancellation.Dispose();backupCancellation=null;
            SyncMovedPackages();
            PlayerNav.IsEnabled=true;LibraryNav.IsEnabled=true;SaveStatus.IsEnabled=true;Workbench.IsEnabled=true;OrganizeNav.IsEnabled=TagsNav.IsEnabled=RescanButton.IsEnabled=SourceButton.IsEnabled=true;RenderBackupJobs();QueueWorkspaceSave();
        }
    }
    async void BackupClosing(object? sender,CancelEventArgs e)
    {
        if(e.Cancel||!backupBusy)return;e.Cancel=true;backupCloseRequested=true;backupCancellation?.Cancel();
        if(backupTask!=null)try{await backupTask;}catch(Exception){}
        await Dispatcher.InvokeAsync(Close);
    }
}




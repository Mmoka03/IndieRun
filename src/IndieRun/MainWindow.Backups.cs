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
    TextBlock? backupLiveLabel;
    ProgressBar? backupLiveBar;
    string backupLiveText="";
    double backupPercent;
    string backupBatchLabel="";
    static string BackupState(string value)=>value switch {"Preparing"=>"준비 중","Copying"=>"백업 중","Verifying"=>"검증 중","Completed"=>"검증 완료","Interrupted"=>"중단됨","Cancelled"=>"취소됨","Failed"=>"실패","Invalid"=>"검증 실패","RecordError"=>"기록 확인 필요","Restoring"=>"복원 중","Moving"=>"이동 복사 중","RollingBack"=>"원래 위치 복구 중",_=>value};
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
        if(backupBusy)return;
        try
        {
            if(BackupService().ListMoves().Any(m=>m.Status is "Running" or "Interrupted" or "RecoveryRequired" or "RecordError"))
            {Info("이동 기록 확인","중단된 이동을 먼저 복구하거나 완료해 주세요.");return;}
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException){Info("이동 기록 확인",ex.Message);return;}
        var picker=new OpenFolderDialog {Title="백업을 보관할 폴더 선택"};
        if(picker.ShowDialog(this)!=true)return;
        backupStorageRoot=picker.FolderName;QueueWorkspaceSave();if(page=="jobs")RenderBackupJobs();
    }
    void RenderBackupJobs()
    {
        JobsContent.Children.Clear();backupLiveLabel=null;backupLiveBar=null;
        var heading=new DockPanel {Margin=new Thickness(0,0,0,14)};
        var change=Btn("백업 위치 변경",()=>{ChooseBackupRoot();RenderBackupJobs();});change.IsEnabled=!backupBusy;DockPanel.SetDock(change,Dock.Right);heading.Children.Add(change);
        var folder=Btn("폴더 열기",()=>OpenSourceFolder(backupStorageRoot));folder.IsEnabled=Directory.Exists(backupStorageRoot);DockPanel.SetDock(folder,Dock.Right);heading.Children.Add(folder);
        heading.Children.Add(Text("이동 · 백업 · 복원",23));JobsContent.Children.Add(heading);
        JobsContent.Children.Add(new TextBlock {Text=backupStorageRoot,FontSize=12,Foreground=B("#8796AA"),TextTrimming=TextTrimming.CharacterEllipsis,ToolTip=backupStorageRoot,Margin=new Thickness(0,0,0,20)});
        if(backupBusy)
        {
            var live=new StackPanel();backupLiveLabel=Text(backupLiveText,14);backupLiveBar=new ProgressBar {Height=6,Minimum=0,Maximum=100,Value=backupPercent,Margin=new Thickness(0,12,0,12)};
            live.Children.Add(backupLiveLabel);live.Children.Add(backupLiveBar);live.Children.Add(Btn("작업 중지",()=>{backupCancellation?.Cancel();backupLiveText="중단 기록을 저장하고 있어요…";if(backupLiveLabel!=null)backupLiveLabel.Text=backupLiveText;}));
            JobsContent.Children.Add(new Border {Child=live,Background=B("#EFF4FF"),CornerRadius=new CornerRadius(8),Padding=new Thickness(18),Margin=new Thickness(0,0,0,20)});
        }
        List<BackupJob> jobs;var service=BackupService();
        try{jobs=service.ListJobs(!backupBusy);}catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException){JobsContent.Children.Add(Text("기록을 읽지 못했습니다: "+ex.Message,13,"#A57A47"));return;}
        RenderMoveJobs(service);
        if(jobs.Count==0){JobsContent.Children.Add(Text(backupBusy?"백업을 준비하고 있어요":"아직 백업이 없습니다. 정리 화면에서 패키지를 담고 시작하세요.",13,"#8C99AD"));return;}
        JobsContent.Children.Add(new TextBlock {Text=$"완료 백업 {jobs.Count(j=>j.Status=="Completed")}개 · {jobs.Where(j=>j.Status=="Completed").Sum(j=>j.TotalBytes)/(1024d*1024*1024):0.##} GB",FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,18)});
        foreach(var job in jobs)
        {
            var card=new StackPanel();var title=string.IsNullOrWhiteSpace(job.Package.Alias)?job.OriginalFolder:job.Package.Alias;
            card.Children.Add(Text(title,18));card.Children.Add(new TextBlock {Text=$"{BackupState(job.Status)} · {job.Files.Count}개 파일 · {FormatBytes(job.TotalBytes)}",Foreground=B(job.Status=="Completed"?"#4D8B77":"#A38350"),Margin=new Thickness(0,6,0,8),FontSize=12});
            if(job.Error!=null)card.Children.Add(Text(job.Error,12,"#A38350"));
            var actions=new WrapPanel {Margin=new Thickness(0,12,0,0)};
            if(job.Status!="RecordError")
            {
                if(!job.SourceVerified)actions.Children.Add(Btn("백업 재개",async()=>await RunBackupOperation("백업 재개",async(engine,progress,token)=>{await engine.ResumeAsync(job.Id,progress,token);}))); 
                if(job.SourceVerified)actions.Children.Add(Btn("다시 검증",async()=>await RunBackupOperation("백업 검증",async(engine,progress,token)=>{await engine.VerifyAsync(job.Id,progress,token);}))); 
                if(job.Status=="Completed")actions.Children.Add(Btn("별도 위치에 복원",async()=>
                {
                    var picker=new OpenFolderDialog {Title="새 복원 폴더를 만들 위치 선택"};if(picker.ShowDialog(this)!=true)return;
                    await RunBackupOperation("복원",async(engine,progress,token)=>{var restored=await engine.RestoreAsync(job.Id,picker.FolderName,progress,token);backupLiveText="복원 완료: "+restored.TargetContainer;});
                }));
            }
            actions.Children.Add(Btn("백업 폴더",()=>OpenSourceFolder(Path.Combine(backupStorageRoot,job.Id))));
            foreach(Button button in actions.Children)button.IsEnabled=!backupBusy;card.Children.Add(actions);
            if(job.Status!="RecordError")foreach(var restore in service.ListRestores(job.Id))
            {
                var row=new StackPanel {Margin=new Thickness(0,16,0,0)};
                row.Children.Add(Text("복원 · "+BackupState(restore.Status),13));
                row.Children.Add(Text(restore.TargetContainer,12,"#8A98AA"));if(restore.Error!=null)row.Children.Add(Text(restore.Error,12,"#A38350"));
                var restoreActions=new WrapPanel {Margin=new Thickness(0,8,0,0)};
                if(restore.Status is "Interrupted" or "Cancelled" or "Failed")restoreActions.Children.Add(Btn("복원 재개",async()=>await RunBackupOperation("복원 재개",async(engine,progress,token)=>{await engine.ResumeRestoreAsync(job.Id,restore.Id,progress,token);}))); 
                if(Directory.Exists(restore.TargetContainer))restoreActions.Children.Add(Btn("복원 폴더",()=>OpenSourceFolder(restore.TargetContainer)));
                foreach(Button button in restoreActions.Children)button.IsEnabled=!backupBusy;row.Children.Add(restoreActions);card.Children.Add(row);
            }
            JobsContent.Children.Add(new Border {Child=card,Background=B("#F7F9FD"),CornerRadius=new CornerRadius(8),Padding=new Thickness(18),Margin=new Thickness(0,0,0,16)});
        }
        JobsContent.Children.Add(Text("백업은 자동 삭제하지 않습니다. 정상 작동 확인 후 백업 폴더에서 직접 관리하세요.",12,"#8997AC"));
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
                await Dispatcher.InvokeAsync(()=>backupBatchLabel=$"{i+1}/{captured.Count} · {(string.IsNullOrWhiteSpace(p.Alias)?p.Folder:p.Alias)}");
                var job=await engine.CreateAsync(p,tags,progress,token);
                await Dispatcher.InvokeAsync(()=>{var original=packages.Find(q=>q.Id==job.Package.Id);if(original!=null)original.Backup=true;QueueWorkspaceSave();RenderBackupJobs();});
            }
        });
    }
    async Task RunBackupOperation(string name,Func<BackupEngine,IProgress<BackupProgress>,CancellationToken,Task> action)
    {
        if(backupBusy||scanning)return;
        backupBusy=true;backupCancellation=new CancellationTokenSource();backupLiveText=name+" 준비 중…";backupPercent=0;backupBatchLabel="";
        SetPage("jobs");SaveStatus.IsEnabled=false;Workbench.IsEnabled=false;OrganizeNav.IsEnabled=TagsNav.IsEnabled=RescanButton.IsEnabled=SourceButton.IsEnabled=false;RenderBackupJobs();
        var service=BackupService();var progress=new Progress<BackupProgress>(value=>
        {
            backupPercent=value.Total>0?100d*value.Completed/value.Total:0;
            backupLiveText=$"{backupBatchLabel} · {BackupState(value.Stage)} {value.Completed}/{value.Total}";
            if(backupLiveLabel!=null)backupLiveLabel.Text=backupLiveText;if(backupLiveBar!=null)backupLiveBar.Value=backupPercent;
        });
        try
        {
            backupTask=Task.Run(()=>action(service,progress,backupCancellation.Token));await backupTask;
            Say(name+" 완료 · 작업 기록에서 확인하세요");
        }
        catch(OperationCanceledException){Say(name+" 중단 · 파일과 기록을 보관했습니다");}
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {Say(name+" 실패 · 파일과 기록을 보관했습니다");Notice.ToolTip=ex.Message;if(!backupCloseRequested)Info(name+" 확인",ex.Message);}
        finally
        {
            backupBusy=false;backupTask=null;backupCancellation.Dispose();backupCancellation=null;
            SyncMovedPackages();
            SaveStatus.IsEnabled=true;Workbench.IsEnabled=true;OrganizeNav.IsEnabled=TagsNav.IsEnabled=RescanButton.IsEnabled=SourceButton.IsEnabled=true;RenderBackupJobs();QueueWorkspaceSave();
        }
    }
    async void BackupClosing(object? sender,CancelEventArgs e)
    {
        if(e.Cancel||!backupBusy)return;e.Cancel=true;backupCloseRequested=true;backupCancellation?.Cancel();
        if(backupTask!=null)try{await backupTask;}catch(Exception){}
        await Dispatcher.InvokeAsync(Close);
    }
}

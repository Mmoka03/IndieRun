using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace IndieRun;

public partial class MainWindow
{
    void SyncMovedPackages()
    {
        var service=BackupService();
        try
        {
            var seen=new HashSet<string>();
            foreach(var move in service.ListMoves(!backupBusy).Where(m=>m.Status is "Completed" or "RolledBack"))
            {
                var backup=service.Load(move.BackupId);if(!seen.Add(backup.Package.Id))continue;
                ApplyMoveToWorkspace(backup,move);
            }
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException){Say("이동 기록 확인 필요");Notice.ToolTip=ex.Message;}
    }
    void ApplyMoveToWorkspace(BackupJob backup,MoveJob move)
    {
        var p=packages.Find(p=>p.Id==backup.Package.Id);if(p==null)return;
        if(!p.Source.Equals(move.Source,StringComparison.OrdinalIgnoreCase)&&!p.Source.Equals(move.Destination,StringComparison.OrdinalIgnoreCase))return;
        var done=move.Status=="Completed";var root=done?move.Destination:move.Source;
        var executableRoot=Path.GetRelativePath(backup.SourceRoot,backup.Package.DetectedRoot);
        p.Source=root;p.SearchRoot=done?Path.GetDirectoryName(root)!:backup.Package.SearchRoot;
        p.DetectedRoot=executableRoot=="."?root:Path.Combine(root,executableRoot);
        p.Folder=Path.GetFileName(root);p.Name=done?p.Folder:backup.Package.Name;
        p.Backup=true;p.Done=done;p.Planned=!done;p.ReviewState="";p.ReviewReason="";
        if(snapshot!=null)
        {
            snapshot.Files.RemoveAll(f=>GameScanner.Within(f.Path,move.Source)||GameScanner.Within(f.Path,move.Destination));
            snapshot.Directories.RemoveAll(d=>GameScanner.Within(d,move.Source)||GameScanner.Within(d,move.Destination));
            snapshot.Directories.Add(root);snapshot.Directories.AddRange(backup.Directories.Select(d=>Path.Combine(root,d)));
            snapshot.Files.AddRange(backup.Files.Select(f=>new ScannedFile(Path.Combine(root,f.RelativePath),f.Length,new DateTime(f.ModifiedTicks,DateTimeKind.Utc))));
        }
        checkpoints.RemoveAll(c=>c.PackageId==p.Id);
        checkpoints.Add(new FolderCheckpoint {PackageId=p.Id,Source=root,Files=backup.Files.Select(f=>new FileStamp(f.RelativePath,f.Length,f.ModifiedTicks)).ToList(),Directories=backup.Directories.ToList()});
        history.Clear();QueueWorkspaceSave();
    }
    void RenderMoveJobs(BackupEngine service)
    {
        List<MoveJob> moves;
        try{moves=service.ListMoves(!backupBusy);}catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException){JobsContent.Children.Add(Text("이동 기록 확인 필요: "+ex.Message,13,"#A57A47"));return;}
        if(moves.Count==0)return;
        JobsContent.Children.Add(new TextBlock {Text="이동 기록",FontSize=19,Margin=new Thickness(0,0,0,14)});
        foreach(var move in moves)
        {
            var row=new StackPanel();row.Children.Add(Text(Path.GetFileName(move.Destination),17));
            row.Children.Add(Text(MoveStatus(move.Status),13,move.Status=="Completed"?"#4D8B77":"#A38350"));
            row.Children.Add(Text(move.Source+"\n→ "+move.Destination,12,"#8796AA"));
            if(move.Error!=null)row.Children.Add(Text(move.Error,12,"#A38350"));
            var buttons=new WrapPanel {Margin=new Thickness(0,12,0,0)};
            if(move.Status is "Interrupted" or "RecoveryRequired")
            {
                if(move.Action=="Move")buttons.Children.Add(Btn("이동 재개",async()=>await RunMoveJob(move,false)));
                buttons.Children.Add(Btn("원래 위치로 복구",async()=>await RunMoveJob(move,true)));
            }
            if(move.Status=="Completed")
            {
                buttons.Children.Add(Btn("이동 폴더",()=>OpenSourceFolder(move.Destination)));
                buttons.Children.Add(Btn("이동 되돌리기",async()=>
                {
                    if(MessageBox.Show(this,"게임을 종료한 뒤 진행하세요. 이 패키지를 원래 위치로 되돌릴까요?\n"+move.Source,"이동 되돌리기",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
                    await RunMoveJob(move,true);
                }));
            }
            foreach(Button button in buttons.Children)button.IsEnabled=!backupBusy;
            row.Children.Add(buttons);JobsContent.Children.Add(new Border {Child=row,Padding=new Thickness(18),Background=B("#F2F6FE"),CornerRadius=new CornerRadius(8),Margin=new Thickness(0,0,0,16)});
        }
    }
    static string MoveStatus(string status)=>status switch {"Completed"=>"이동 완료","RolledBack"=>"원래 위치 복구 완료","Interrupted"=>"중단됨 · 작업 선택 필요","RecoveryRequired"=>"복구 필요","Running"=>"이동 처리 중","RecordError"=>"기록 확인 필요",_=>status};
    async Task RunMoveJob(MoveJob move,bool undo)
    {
        await RunBackupOperation(undo?"이동 복구":"이동 재개",async(engine,progress,token)=>
        {
            var result=undo?await engine.UndoMoveAsync(move.BackupId,move.Id,progress,token):await engine.ResumeMoveAsync(move.BackupId,move.Id,progress,token);
            await Dispatcher.InvokeAsync(()=>ApplyMoveToWorkspace(engine.Load(move.BackupId),result));
        });
    }
    async Task StartMoveBatchAsync(List<Package> list)
    {
        if(backupBusy||scanning)return;
        if(list.Any(p=>p.ReviewState.Length>0)){Info("원본 확인","변경되었거나 찾을 수 없는 패키지를 먼저 확인해 주세요.");return;}
        var service=BackupService();
        try
        {
            if(service.ListMoves().Any(m=>m.Status is "Interrupted" or "RecoveryRequired" or "Running" or "RecordError"))throw new IOException("작업 탭에서 이전 이동을 먼저 복구하거나 완료해 주세요.");
            service.Preflight(list.Select(p=>(p.Source,Path.Combine(destination,p.Name))));
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException){Info("이동 전 확인",ex.Message);return;}
        if(!FlushWorkspace()){Info("계획 저장","정리 계획을 먼저 저장해 주세요.");return;}
        foreach(var p in list)if(!RegisterGame(p))return;
        if(!FlushWorkspace())return;
        var captured=JsonSerializer.Deserialize<List<Package>>(JsonSerializer.Serialize(list))!;
        var tags=JsonSerializer.Deserialize<List<TagDefinition>>(JsonSerializer.Serialize(catalog.Tags))!;
        var target=destination;var batchId=Guid.NewGuid().ToString("N");
        await RunBackupOperation("이동",async(engine,progress,token)=>
        {
            var completed=new List<MoveJob>();
            for(int i=0;i<captured.Count;i++)
            {
                var p=captured[i];
                await Dispatcher.InvokeAsync(()=>backupBatchLabel=$"{i+1}/{captured.Count} · {(p.Alias.Length>0?p.Alias:p.Folder)}");
                try
                {
                    token.ThrowIfCancellationRequested();
                    var backup=await engine.CreateAsync(p,tags,progress,token);
                    var moved=await engine.MoveAsync(backup.Id,Path.Combine(target,p.Name),batchId,progress,token);completed.Add(moved);
                    await Dispatcher.InvokeAsync(()=>{ApplyMoveToWorkspace(backup,moved);FlushWorkspace();RenderBackupJobs();});
                }
                catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
                {
                    var pending=engine.ListMoves(false).Any(m=>m.Status is "RecoveryRequired" or "Running" or "Interrupted");
                    var choice=await Dispatcher.InvokeAsync(()=>backupCloseRequested?"stop":MoveFailureChoice(ex.Message,completed.Count,!pending&&ex is not OperationCanceledException));
                    if(choice=="undo")
                    {
                        foreach(var previous in completed.AsEnumerable().Reverse())
                        {
                            var undone=await engine.UndoMoveAsync(previous.BackupId,previous.Id,progress,CancellationToken.None);
                            await Dispatcher.InvokeAsync(()=>{ApplyMoveToWorkspace(engine.Load(previous.BackupId),undone);FlushWorkspace();});
                        }
                    }
                    if(choice=="continue")continue;
                    throw new IOException(pending?"이동이 중단됐습니다. 작업 탭에서 복구가 필요한 패키지를 확인해 주세요.":"이동을 중단했습니다. 완료된 작업과 백업은 선택에 따라 보관했습니다.",ex);
                }
            }
        });
    }
    string MoveFailureChoice(string error,int completed,bool canContinue)
    {
        var choice="stop";var win=Modal("이동 중단 확인",620,370);var stack=new StackPanel {Margin=new Thickness(26)};
        stack.Children.Add(Text("다음 작업을 선택하세요",22));stack.Children.Add(Text(error,13,"#A38350"));
        stack.Children.Add(new TextBlock {Text=$"완료 {completed}개 · 백업은 유지됩니다",Margin=new Thickness(0,16,0,16)});
        void Add(string label,string value){var button=Btn(label,()=>{choice=value;win.Close();});button.Margin=new Thickness(0,0,0,10);stack.Children.Add(button);}
        if(canContinue)Add("실패한 패키지는 남기고 다음 작업 계속","continue");
        Add("중단 · 완료한 이동 유지","stop");
        if(completed>0)Add("중단 · 완료한 이동도 되돌리기","undo");
        win.Content=stack;win.ShowDialog();return choice;
    }
}

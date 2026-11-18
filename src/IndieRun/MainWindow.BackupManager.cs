using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace IndieRun;
public partial class MainWindow
{
    static string BackupPurpose(BackupJob job)=>job.Reason=="move"?"이동 전":job.Reason=="rename"?"이름 변경 전":job.Reason=="manual"?"수동 백업":"기존 백업";
    void ShowBackupManager()=>BuildBackupManager().ShowDialog();
    Window BuildBackupManager()
    {
        var win=Modal("백업 관리",900,720);win.MinWidth=740;win.MinHeight=560;var dock=new DockPanel{Margin=new Thickness(26)};var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);dock.Children.Add(header);
        var summary=WorkText("용량 확인 중…",22,"Text.Primary");header.Children.Add(summary);header.Children.Add(CompactPath(backupStorageRoot));
        var notice=WorkText("실행 확인 표시는 직접 남깁니다. 백업은 자동으로 삭제하지 않습니다.",12,"Text.Muted");notice.Margin=new Thickness(0,12,0,16);header.Children.Add(notice);
        var bottom=new StackPanel{Margin=new Thickness(0,16,0,0)};DockPanel.SetDock(bottom,Dock.Bottom);dock.Children.Add(bottom);var selection=WorkText("삭제할 백업을 선택하세요.",13);bottom.Children.Add(selection);var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};bottom.Children.Add(actions);
        bool busy=false,closed=false;var picked=new HashSet<string>();var inventory=new List<BackupInventory>();var cards=new StackPanel();dock.Children.Add(new ScrollViewer{Content=cards,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
        var refresh=Btn("새로고침",()=>{});var clear=Btn("선택 해제",()=>{});var remove=Btn("선택한 백업 삭제",()=>{});var close=Btn("닫기",()=>win.Close());close.IsCancel=true;actions.Children.Add(refresh);actions.Children.Add(clear);actions.Children.Add(remove);actions.Children.Add(close);win.Content=dock;
        bool Pending()=>RenamePending||ScopeChanges().HasPending||File.Exists(Edits().Pending);
        void Update()
        {
            var selected=inventory.Where(i=>picked.Contains(i.Job.Id)).ToList();selection.Text=selected.Count==0?"삭제할 백업을 선택하세요.":$"{selected.Count}개 선택 · 약 {FormatBytes(selected.Sum(i=>i.ReclaimableBytes))} 확보";
            remove.IsEnabled=!busy&&!backupBusy&&!Pending()&&selected.Count>0;clear.IsEnabled=!busy&&picked.Count>0;refresh.IsEnabled=close.IsEnabled=!busy;
        }
        async Task Reload()
        {
            busy=true;Update();cards.IsEnabled=false;
            try
            {
                var latest=await Task.Run(()=>BackupService().Inventory());if(closed)return;inventory=latest;picked.RemoveWhere(id=>!inventory.Any(i=>i.Job.Id==id&&i.Selectable));
                summary.Text="백업 전체 "+FormatBytes(inventory.Sum(i=>i.StoredBytes));notice.Text=Pending()?"중단된 편집·이름 변경을 복구한 뒤 백업을 삭제할 수 있습니다.":"실행 확인 표시는 직접 남깁니다. 백업은 자동으로 삭제하지 않습니다.";Draw();
            }
            catch(Exception ex)when(LibraryFault(ex)){notice.Text="백업을 확인하지 못했습니다. 위치와 권한을 확인하세요.";notice.ToolTip=ex.Message;cards.Children.Clear();cards.Children.Add(WorkDetails(ex.Message));}
            finally{busy=false;cards.IsEnabled=true;Update();}
        }
        void Draw()
        {
            cards.Children.Clear();var visible=inventory.Where(i=>i.Job.Status!="Deleted").ToList();
            foreach(var group in visible.GroupBy(i=>i.Job.Package.Id.Length>0?i.Job.Package.Id:i.Job.Id).OrderByDescending(g=>g.Max(i=>i.Job.CreatedUtc)))
            {
                var latest=group.OrderByDescending(i=>i.Job.CreatedUtc).First();var current=packages.Find(p=>p.Id==latest.Job.Package.Id);var title=current==null?PlayerRules.Title(latest.Job.Package):PlayerRules.Title(current);if(string.IsNullOrWhiteSpace(title))title=latest.Job.OriginalFolder;
                var label=WorkText(title,16,"Text.Primary");label.FontWeight=FontWeights.SemiBold;label.Margin=new Thickness(0,12,0,10);cards.Children.Add(label);
                foreach(var entry in group.OrderByDescending(i=>i.Job.CreatedUtc))
                {
                    var job=entry.Job;var panel=new StackPanel();var row=new DockPanel();var pick=Btn(picked.Contains(job.Id)?"선택됨":"선택",()=>{});pick.MinWidth=76;pick.Margin=new Thickness(12,0,0,0);pick.IsEnabled=entry.Selectable&&!backupBusy&&!Pending();DockPanel.SetDock(pick,Dock.Right);row.Children.Add(pick);
                    var info=new StackPanel{VerticalAlignment=VerticalAlignment.Center,ToolTip="작업 기록 포함 "+FormatBytes(entry.StoredBytes)};info.Children.Add(WorkText(job.CreatedUtc.ToLocalTime().ToString("yyyy.MM.dd  HH:mm")+" · "+FormatBytes(entry.ReclaimableBytes),14,"Text.Primary"));var state=entry.Protection??(job.Status is "Deleting" or "DeleteFailed"?"삭제 중단 · 다시 선택해 삭제하세요.":job.ExecutionConfirmedUtc.HasValue?"실행 확인됨 · "+job.ExecutionConfirmedUtc.Value.ToLocalTime().ToString("yyyy.MM.dd"):"실행 확인 전");info.Children.Add(WorkText(BackupPurpose(job)+" · "+state,11,entry.Protection!=null||job.Status is "Deleting" or "DeleteFailed"?"Warning.Text":"Text.Muted"));row.Children.Add(info);panel.Children.Add(row);
                    pick.Click+=(_,_)=>{if(!picked.Add(job.Id))picked.Remove(job.Id);pick.Content=picked.Contains(job.Id)?"선택됨":"선택";pick.Style=(Style)FindResource(picked.Contains(job.Id)?"Primary":typeof(Button));Update();};if(picked.Contains(job.Id))pick.Style=(Style)FindResource("Primary");
                    var details=new StackPanel();details.Children.Add(WorkText("원래 위치: "+job.SourceRoot+"\n백업 용도: "+(job.Reason=="move"?"이동 전":job.Reason=="rename"?"폴더명 변경 전":job.Reason=="manual"?"수동 백업":"기존 백업")+"\n상태: "+BackupState(job.Status),12));if(job.Error!=null)details.Children.Add(WorkDetails(job.Error,"오류 내용"));
                    var options=new WrapPanel{Margin=new Thickness(0,12,0,0)};options.Children.Add(Btn("백업 폴더 열기",()=>OpenSourceFolder(Path.Combine(backupStorageRoot,job.Id))));
                    if(job.Status=="Completed")options.Children.Add(Btn(job.ExecutionConfirmedUtc.HasValue?"실행 확인 표시 해제":"실행 확인 표시",async()=>
                    {
                        if(busy||backupBusy)return;busy=true;cards.IsEnabled=false;Update();try{await Task.Run(()=>BackupService().ConfirmExecution(job.Id,!job.ExecutionConfirmedUtc.HasValue));}catch(Exception ex)when(LibraryFault(ex)){ErrorInfo("실행 확인 표시","표시를 저장하지 못했습니다.",ex.Message);}finally{busy=false;cards.IsEnabled=true;}await Reload();
                    }));details.Children.Add(options);panel.Children.Add(new Expander{Header="자세히",Content=details,Margin=new Thickness(0,10,0,0)});cards.Children.Add(WorkCard(panel));
                }
            }
            if(visible.Count==0)cards.Children.Add(WorkText("보관 중인 백업이 없습니다.",14,"Text.Muted"));
        }
        clear.Click+=(_,_)=>{picked.Clear();Draw();Update();};refresh.Click+=async(_,_)=>await Reload();
        remove.Click+=async(_,_)=>
        {
            if(busy||backupBusy||Pending())return;var chosen=inventory.Where(i=>picked.Contains(i.Job.Id)&&i.Selectable).ToList();if(chosen.Count==0)return;
            var selectedNames=string.Join("\n",chosen.Take(6).Select(i=>PlayerRules.Title(i.Job.Package)+" · "+i.Job.CreatedUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss")+" · "+BackupPurpose(i.Job)));if(chosen.Count>6)selectedNames+=$"\n외 {chosen.Count-6}개";
            if(!Confirm("백업을 삭제할까요?",$"백업 {chosen.Count}개 · 약 {FormatBytes(chosen.Sum(i=>i.ReclaimableBytes))} 확보\n\n"+selectedNames+"\n\n게임 파일과 작업 기록은 유지됩니다. 삭제한 백업으로 복원하거나 이동을 되돌릴 수 없습니다.","백업 삭제",string.Join("\n",chosen.Select(i=>i.Job.SourceRoot)),win,true))return;
            busy=true;cards.IsEnabled=false;notice.Text="선택한 백업 삭제 중…";Update();
            try{await Task.Run(()=>BackupService().DeleteBackups(chosen.Select(i=>i.Job.Id)));picked.Clear();Say("선택한 백업을 삭제했습니다");}
            catch(Exception ex)when(LibraryFault(ex)){ErrorInfo("백업 삭제","일부 백업을 삭제하지 못했습니다. 상태를 확인한 뒤 다시 선택해 삭제하세요.",ex.Message);}
            finally{busy=false;cards.IsEnabled=true;}await Reload();if(page=="jobs")RenderBackupJobs();
        };
        win.Closing+=(_,e)=>{if(busy)e.Cancel=true;};win.Closed+=(_,_)=>closed=true;win.Loaded+=async(_,_)=>await Reload();Update();return win;
    }
}


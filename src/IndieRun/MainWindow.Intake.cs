using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
namespace IndieRun;
public partial class MainWindow
{
    async Task OpenIntakeAsync(List<Package> list)
    {
        if(list.Count==0||running||scanning||backupBusy||scopeDraft!=null)return;
        if(workspaceBlocked||workspaceLock==null||File.Exists(Edits().Pending)||ScopeChanges().HasPending||RenamePending){ShowWorkspaceSaveIssue();return;}
        if(!await VerifyTransferSelectionAsync(list))return;
        BuildImportPlan(list,destination).ShowDialog();
    }
    async Task BeginGameImportAsync()
    {
        if(running||scanning||backupBusy||!LeaveScopeEdit())return;
        if(workspaceBlocked||workspaceLock==null||RenamePending||ScopeChanges().HasPending||File.Exists(Edits().Pending)){ShowWorkspaceSaveIssue();return;}
        var picker=new OpenFolderDialog{Title="게임이 있는 폴더 선택"};if(picker.ShowDialog(this)!=true)return;
        try
        {
            if((File.GetAttributes(picker.FolderName)&FileAttributes.ReparsePoint)!=0)throw new IOException("연결 폴더는 탐색 위치로 사용할 수 없습니다.");
            var blocked=ProtectedGameRoots();if(blocked.Any(p=>GameScanner.Within(picker.FolderName,p)))throw new IOException("프로그램·소스·백업 폴더는 선택할 수 없습니다.");
            var old=Clone(searchLocations);SearchLocationRules.Add(searchLocations,picker.FolderName);foreach(var location in searchLocations.Where(l=>GameScanner.Within(picker.FolderName,l.Path)))location.Enabled=true;
            if(!FlushWorkspace()){searchLocations=old;ShowWorkspaceSaveIssue();return;}
            SetPage("organize");await ScanIntakeFolderAsync(picker.FolderName);
        }
        catch(Exception ex)when(LibraryFault(ex)){ErrorInfo("게임 가져오기","폴더 위치와 접근 권한을 확인한 뒤 다시 시도하세요.",ex.Message);}
    }
    string? intakeScanRoot;
    async Task ScanIntakeFolderAsync(string root)
    {
        intakeScanRoot=root;try{await RefreshWorkspaceAsync(root,false,false);}finally{intakeScanRoot=null;}
    }
    Package IntakeGame(Package p)=>packages.Find(q=>GameRegistration.SameEntry(q,p))??p;
    internal bool suppressIntakeResult=false;
    bool registrationSaveFailed;
    bool intakeInProgress;
    string intakeEarlyError="";
    internal List<IntakeResult> lastIntakeResults=[];
    void ReportMoveProblem(string title,string message,string detail){if(intakeInProgress)intakeEarlyError=message+"\n"+detail;else ErrorInfo(title,message,detail);}
    async Task ExecuteIntakeAsync(IntakeSelection plan,string target)
    {
        var verify=plan.Games.Where(p=>plan.Moving.Contains(p.Id)||!IsAdded(p)).ToList();var moving=plan.MoveItems(target,packages);verify.AddRange(moving.Where(p=>!verify.Any(q=>q.Id==p.Id)));
        if(verify.Count>0&&!await VerifyCurrentSelectionAsync(verify,false))return;
        foreach(var p in plan.Games.Concat(moving)){var live=packages.Find(q=>q.Id==p.Id);if(live!=null){p.ReviewState=live.ReviewState;p.ReviewReason=live.ReviewReason;}}
        var since=DateTime.UtcNow;var rows=new List<IntakeResult>();intakeInProgress=true;intakeEarlyError="";
        try
        {
            registrationSaveFailed=false;var staying=plan.Games.Where(p=>!plan.Moving.Contains(p.Id)).Select(IntakeGame).ToList();
            var already=staying.Where(IsAdded).Select(p=>p.Id).ToHashSet();
            if(staying.Any(p=>!IsAdded(p)))await RegisterGamesAtLocationAsync(staying,false);
            var saved=FlushWorkspace()&&!registrationSaveFailed;
            foreach(var p in staying)rows.Add(new(p.Id,PlayerRules.Title(p),!IsAdded(p)?"등록 실패":!saved?"등록 정보 저장 필요":already.Contains(p.Id)?"이미 등록됨":"등록 완료"){Source=p.Source,Registration=IsAdded(p)?"등록됨":"등록되지 않음",SinceUtc=since,Saved=saved,Detail=registrationErrors.GetValueOrDefault(p.Id)});
            var moved=plan.MoveItems(target,packages);var previousOperation=operationStatus;
            var before=moved.ToDictionary(p=>p.Id,p=>(p.Source,Destination:CompositionPlan.Target(p,target)));
            if(moved.Count>0&&saved&&!workspaceBlocked)await ExecuteMoveListWithModeAsync(moved,target,!plan.Backup);
            await RefreshHistoryAsync();await RefreshHistoryAsync();
            foreach(var p in moved)
            {
                var item=ReferenceEquals(previousOperation,operationStatus)?null:operationStatus?.Items.Find(i=>i.Id==p.Id);
                var prior=before.GetValueOrDefault(p.Id,(p.Source,CompositionPlan.Target(p,target)));var live=packages.Find(q=>q.Id==p.Id)??IntakeGame(p);
                rows.Add(new(p.Id,PlayerRules.Title(p),item==null?"이동하지 않음":item.State=="Completed"?"이동 완료":OperationStatus.ItemLabel(item.State)){Source=prior.Source,Destination=prior.Item2,Registration=IsAdded(live)?"등록됨":"등록되지 않음",SinceUtc=since,Saved=saved&&item?.State!="SavePending",Backup=plan.Backup?"만들지 않음":"사용 안 함",Detail=item?.Error??intakeEarlyError});
            }
            var records=workHistory.Records.ToArray();lastIntakeResults=await Task.Run(()=>rows.Select(r=>IntakeEvidence.Inspect(r,records)).ToList());
        }
        finally{intakeInProgress=false;}
        Render();if(page=="play")RenderPlayer();
        if(!suppressIntakeResult&&!backupCloseRequested)BuildIntakeResult(lastIntakeResults).ShowDialog();
    }
    Window BuildIntakeResult(List<IntakeResult> results)
    {
        var attention=results.Any(r=>!r.Success);var win=Modal("가져오기 결과",680,attention?Math.Min(720,420+results.Count(r=>!r.Success)*100):440);var dock=new DockPanel{Margin=new Thickness(28)};
        var footer=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,18,0,0)};DockPanel.SetDock(footer,Dock.Bottom);dock.Children.Add(footer);
        footer.Children.Add(Btn("계속 정리",()=>{win.Close();SetPage("organize");Render();}));
        var view=Btn("실행 목록에서 보기",()=>{win.Close();SetPage("play");LoadPlayer();RenderPlayer();},true);view.IsEnabled=results.Any(r=>r.Success||r.Registration=="등록됨");footer.Children.Add(view);
        var body=new StackPanel();var title=WorkText(attention?"확인이 필요한 게임이 있습니다.":"가져오기를 완료했습니다.",20,"Text.Primary");title.Margin=new Thickness(0,0,0,14);body.Children.Add(title);
        var completed=results.Count(r=>r.Success);var counts=WorkText($"완료 {completed}개"+(attention?$" · 확인 필요 {results.Count-completed}개":""),13);counts.Margin=new Thickness(0,0,0,14);body.Children.Add(counts);
        foreach(var r in results.Where(r=>!r.Success))
        {
            var card=new StackPanel();card.Children.Add(WorkText(r.Title,17,"Text.Primary"));card.Children.Add(WorkText(r.State,13,"Warning.Text"));
            var facts=new StackPanel{Margin=new Thickness(0,12,0,10)};
            void Fact(string label,string value){if(value.Length==0)return;var line=new Grid{Margin=new Thickness(0,4,0,0)};line.ColumnDefinitions.Add(new(){Width=new GridLength(78)});line.ColumnDefinitions.Add(new());line.Children.Add(WorkText(label,12,"Text.Muted"));var text=WorkText(value,13,"Text.Primary");Grid.SetColumn(text,1);line.Children.Add(text);facts.Children.Add(line);}
            Fact("실행 목록",r.Registration);Fact("게임 폴더",r.Location);Fact("백업",r.Backup);card.Children.Add(facts);
            var solve=Btn(IntakeEvidence.ActionLabel(r.Solution),async()=>{win.Close();await SolveIntakeResultAsync(r,results);},true);solve.HorizontalAlignment=HorizontalAlignment.Left;card.Children.Add(solve);
            var other=new StackPanel();if(r.Solution!="Review")other.Children.Add(Btn("정리에서 확인",()=>{win.Close();ReviewHistoryGame(r.Id);}));if(r.Record!=null)other.Children.Add(Btn("작업 기록",()=>{win.Close();SetPage("jobs");RenderBackupJobs();}));
            other.Children.Add(WorkDetails("이동 전: "+r.Source+(r.Destination.Length==0?"":"\n도착: "+r.Destination)+(string.IsNullOrWhiteSpace(r.Detail)?"":"\n\n"+r.Detail)));card.Children.Add(new Expander{Header="다른 방법·상세 정보",Content=other,Margin=new Thickness(0,12,0,0)});body.Children.Add(WorkCard(card,"Warning.Surface"));
        }
        body.Children.Add(WorkDetails(string.Join("\n",results.Select(r=>r.Title+" · "+r.State+(r.Registration.Length>0?" · "+r.Registration:""))),"전체 결과"));dock.Children.Add(new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});win.Content=dock;return win;
    }
}

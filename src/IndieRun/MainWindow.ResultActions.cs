using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace IndieRun;
public partial class MainWindow
{
    async Task SolveIntakeResultAsync(IntakeResult row,List<IntakeResult> results)
    {
        if(row.Solution=="Review"){ReviewHistoryGame(row.Id);return;}
        if(row.Solution=="Locate"){var p=packages.Find(p=>p.Id==row.Id);if(p!=null)await ReconnectGameAsync(p);return;}
        if(row.Solution=="Name"){BuildDestinationNameFix(row).ShowDialog();return;}
        var saved=row.Saved;string? error=null;intakeInProgress=true;intakeEarlyError="";
        try
        {
            if(row.Solution=="Save")
            {
                if(workspaceBlocked||File.Exists(Edits().Pending))throw new IOException("중단된 저장과 설정 파일 상태를 먼저 확인하세요. 기존 정보는 보존했습니다.");
                new PlayerStore(destination).Save(playerDocument);saved=FlushWorkspace();
                if(saved){registrationSaveFailed=false;foreach(var i in operationStatus?.Items.Where(i=>i.State=="SavePending")??[])i.State="Completed";}
            }
            else if(row.Solution is "BackupLocation" or "RecordLocation"&&row.Record!=null)await FindHistoryRootAsync(row.Record);
            else if(row.Record!=null)await RunHistoryMoveAsync(row.Record,row.Solution=="Undo");
        }
        catch(Exception ex)when(LibraryFault(ex)){error=ex.Message;if(row.Solution=="Save")saved=false;}
        finally{intakeInProgress=false;}
        error??=intakeEarlyError.Length>0?intakeEarlyError:null;
        await RefreshHistoryAsync();var records=workHistory.Records.ToArray();var updated=row with{Saved=saved,Detail=error??row.Detail};
        if(saved&&row.Solution=="Save")updated=updated with{State=row.Destination.Length==0?"등록 완료":"이동 완료",Solution="Review"};
        updated=await Task.Run(()=>IntakeEvidence.Inspect(updated,records));
        var next=results.Select(r=>r.Id==row.Id?updated:r).ToList();lastIntakeResults=next;if(!suppressIntakeResult)BuildIntakeResult(next).ShowDialog();
    }
    Window BuildDestinationNameFix(IntakeResult row)
    {
        var win=Modal("도착 폴더명 변경",620,430);var dock=new DockPanel{Margin=new Thickness(28)};var footer=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,18,0,0)};DockPanel.SetDock(footer,Dock.Bottom);dock.Children.Add(footer);
        footer.Children.Add(Btn("취소",()=>win.Close()));var apply=Btn("이동 설정 확인",()=>{},true);footer.Children.Add(apply);
        var body=new StackPanel();body.Children.Add(WorkText(row.Title,20,"Text.Primary"));body.Children.Add(WorkText("이동 후 사용할 폴더명을 입력하세요. 이 화면에서는 파일을 변경하지 않습니다.",13));
        var box=new TextBox{Text=Path.GetFileName(row.Destination),Margin=new Thickness(0,16,0,8)};body.Children.Add(box);var note=WorkText("",12,"Warning.Text");body.Children.Add(note);
        void Update(){var problem=BackupEngine.MoveNameError(box.Text);if(problem!=null){note.Text=problem;apply.IsEnabled=false;return;}var target=Path.Combine(Path.GetDirectoryName(row.Destination)!,box.Text);note.Text=Directory.Exists(target)||File.Exists(target)?"같은 이름의 폴더가 있습니다.":"";apply.IsEnabled=note.Text.Length==0;}
        box.TextChanged+=(_,_)=>Update();apply.Click+=(_,_)=>{var p=packages.Find(p=>p.Id==row.Id);if(p==null)return;var copy=Clone(p);copy.Name=box.Text;pendingMovePlan=null;win.Close();OpenMovePlan([copy],Path.GetDirectoryName(row.Destination)!);};
        Update();dock.Children.Add(body);win.Content=dock;return win;
    }
}

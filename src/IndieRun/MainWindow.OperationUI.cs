using System.Windows;
using System.Windows.Controls;

namespace IndieRun;

public partial class MainWindow
{
    OperationStatus? operationStatus;
    TextBlock? liveGame,livePhase,liveCounts,liveFile;
    string liveDetail="";
    static TextBlock WorkText(string text,double size=13,string role="Text.Secondary")
    {var t=new TextBlock {Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap};t.SetResourceReference(ForegroundProperty,role);return t;}
    static Border WorkCard(UIElement content,string role="Surface.Secondary")
    {var card=new Border {Child=content,Padding=new Thickness(18),CornerRadius=new CornerRadius(9),Margin=new Thickness(0,0,0,14)};card.SetResourceReference(BackgroundProperty,role);return card;}
    static Expander WorkDetails(string details,string label="자세히")
    {
        var text=new TextBox {Text=details,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,BorderThickness=new Thickness(0),Background=System.Windows.Media.Brushes.Transparent,Padding=new Thickness(0,8,0,0)};
        text.SetResourceReference(ForegroundProperty,"Text.Muted");
        var expander=new Expander {Header=label,Content=new ScrollViewer {Content=text,MaxHeight=180},Margin=new Thickness(0,10,0,0)};expander.SetResourceReference(ForegroundProperty,"Text.Muted");return expander;
    }
    void RenderOperationSummary()
    {
        JobsSummary.Children.Clear();liveGame=livePhase=liveCounts=liveFile=null;backupLiveBar=null;
        var run=operationStatus;if(run==null)return;
        var body=new StackPanel();var title=WorkText(run.Heading,18,"Text.Primary");title.FontWeight=FontWeights.SemiBold;body.Children.Add(title);
        liveCounts=WorkText(run.Counts,12);liveCounts.Margin=new Thickness(0,6,0,0);body.Children.Add(liveCounts);
        if(backupBusy)
        {
            liveGame=WorkText(run.Position+"  "+run.Current,15,"Text.Primary");liveGame.Margin=new Thickness(0,16,0,4);body.Children.Add(liveGame);
            livePhase=WorkText(run.Stage);body.Children.Add(livePhase);
            backupLiveBar=new ProgressBar {Height=6,Minimum=0,Maximum=100,Value=backupPercent,IsIndeterminate=backupPercent<0,Margin=new Thickness(0,12,0,10)};
            backupLiveBar.SetResourceReference(ForegroundProperty,"Accent.Fill");backupLiveBar.SetResourceReference(BackgroundProperty,"Accent.Surface");body.Children.Add(backupLiveBar);
            var actions=new DockPanel();var stop=Btn("작업 중지",()=>{backupCancellation?.Cancel();stopPending=true;UpdateOperationLive();});stop.IsEnabled=!stopPending;stop.Click+=(_,_)=>stop.IsEnabled=false;DockPanel.SetDock(stop,Dock.Right);actions.Children.Add(stop);
            liveFile=WorkText(liveDetail,11,"Text.Muted");liveFile.TextTrimming=TextTrimming.CharacterEllipsis;liveFile.TextWrapping=TextWrapping.NoWrap;liveFile.ToolTip=liveDetail;liveFile.VerticalAlignment=VerticalAlignment.Center;actions.Children.Add(liveFile);body.Children.Add(actions);
        }
        else
        {
            var note=WorkText(run.SavePending>0?"파일 작업은 끝났습니다. 상단의 ‘저장 문제 확인’에서 다시 저장하세요.":run.NeedsAttention||run.Stopped?run.WithoutBackup?"아래 이동 기록에서 재개하거나 원래 위치로 되돌리세요. 별도 백업은 없습니다.":"아래 기록에서 재개하거나 복구하세요. 만들어진 백업은 유지됩니다.":run.WithoutBackup?"백업 없이 이동했습니다. 게임이 정상 작동하는지 확인하세요.":run.Name=="백업"?"백업을 완료했습니다. 백업은 직접 삭제할 때까지 보관합니다.":"게임이 정상 작동하는지 확인하세요. 생성한 백업은 직접 삭제할 때까지 유지됩니다.",12);note.Margin=new Thickness(0,12,0,0);body.Children.Add(note);
            body.Children.Add(WorkDetails(string.Join("\n\n",run.Items.Select(i=>$"{i.Title} · {OperationStatus.ItemLabel(i.State)}"+(i.Error==null?"":"\n"+i.Error))),"게임별 결과"));
        }
        JobsSummary.Children.Add(WorkCard(body,run.Finished&&(run.NeedsAttention||run.Stopped)?"Warning.Surface":"Accent.Surface"));
    }
    bool stopPending;
    void UpdateOperationLive()
    {
        var run=operationStatus;if(run==null)return;
        if(liveGame!=null)liveGame.Text=run.Position+"  "+run.Current;
        if(liveCounts!=null)liveCounts.Text=run.Counts;
        if(livePhase!=null)livePhase.Text=stopPending?"중단 요청됨 · 현재 파일과 기록을 정리하고 있습니다":run.Stage+(backupPercent>=0?$" · 현재 단계 {backupPercent:0}%":"");
        if(backupLiveBar!=null){backupLiveBar.IsIndeterminate=backupPercent<0;backupLiveBar.Value=Math.Max(0,backupPercent);}
        if(liveFile!=null){liveFile.Text=liveDetail;liveFile.ToolTip=liveDetail;}
    }
    Task BeginOperationItem(Package package,string stage)=>Dispatcher.InvokeAsync(()=>
    {operationStatus?.Begin(package.Id);if(operationStatus!=null)operationStatus.Stage=stage;backupPercent=-1;liveDetail="";UpdateOperationLive();}).Task;
    Task MarkOperationItem(string id,string state,string? error=null)=>Dispatcher.InvokeAsync(()=>
    {operationStatus?.Mark(id,state,error);UpdateOperationLive();}).Task;
}



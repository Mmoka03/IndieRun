using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
namespace IndieRun;

public partial class MainWindow
{
    internal Func<Window,string?>? restoreFolderPicker=null;
    Window BuildRestorePlan(BackupJob job,string backupRoot,Action<string> accept)
    {
        var win=Modal("백업에서 복원",620,440);win.MinWidth=520;win.MinHeight=400;
        var body=new DockPanel{Margin=new Thickness(26)};win.Content=body;
        var footer=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,20,0,0)};
        DockPanel.SetDock(footer,Dock.Bottom);body.Children.Add(footer);
        string? target=null;
        var cancel=Btn("취소",()=>win.Close());cancel.IsCancel=true;footer.Children.Add(cancel);
        var restore=Btn("복원 시작",()=>{if(target==null)return;accept(target);win.Close();},true);restore.Name="StartRestore";restore.Margin=new Thickness(0);restore.IsEnabled=false;footer.Children.Add(restore);
        var content=new StackPanel();body.Children.Add(new ScrollViewer{Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
        content.Children.Add(WorkText("백업에서 복원",22,"Text.Primary"));
        var title=WorkText(PlayerRules.Title(job.Package),16,"Text.Primary");title.Margin=new Thickness(0,16,0,4);content.Children.Add(title);
        content.Children.Add(WorkText(job.CreatedUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm")+" · "+BackupPurpose(job)+" · "+FormatBytes(job.TotalBytes),12,"Text.Muted"));
        var label=WorkText("복원할 위치",13,"Text.Primary");label.Margin=new Thickness(0,22,0,8);content.Children.Add(label);
        var row=new DockPanel();content.Children.Add(row);
        var path=WorkText("위치를 선택하세요.",13,"Text.Muted");path.VerticalAlignment=VerticalAlignment.Center;path.TextTrimming=TextTrimming.CharacterEllipsis;path.TextWrapping=TextWrapping.NoWrap;path.Margin=new Thickness(0,0,12,0);
        var error=WorkText("",12,"Warning.Text");error.Margin=new Thickness(0,10,0,0);error.Visibility=Visibility.Collapsed;
        var choose=Btn("위치 선택",()=>
        {
            var selected=restoreFolderPicker!=null?restoreFolderPicker(win):PickRestoreFolder(win);
            if(selected==null)return;
            try
            {
                new BackupEngine(backupRoot).CheckRestoreLocation(job,selected);
                target=selected;path.Text=selected;path.ToolTip=selected;path.Foreground=ThemeBrush("Text.Primary");restore.IsEnabled=true;error.Visibility=Visibility.Collapsed;
            }
            catch(Exception ex)when(LibraryFault(ex)){target=null;restore.IsEnabled=false;path.Text=selected;path.ToolTip=selected;error.Text=ex.Message;error.Visibility=Visibility.Visible;}
        });choose.Name="ChooseRestoreFolder";choose.Margin=new Thickness(0);DockPanel.SetDock(choose,Dock.Right);row.Children.Add(choose);row.Children.Add(path);
        var hint=WorkText("선택한 위치에 새 폴더를 만들어 복원합니다.",12,"Text.Muted");hint.Margin=new Thickness(0,10,0,0);content.Children.Add(hint);content.Children.Add(error);
        var details=WorkDetails("백업은 이 기록에서 자동으로 선택됩니다.\n기존 파일과 실행 목록의 연결은 바뀌지 않습니다.\n복원 폴더: restore-작업번호 / "+job.OriginalFolder,"복원 안내");details.Margin=new Thickness(0,14,0,0);content.Children.Add(details);
        return win;
    }
    static string? PickRestoreFolder(Window owner)
    {
        var picker=new OpenFolderDialog{Title="복원한 게임을 저장할 위치 선택"};return picker.ShowDialog(owner)==true?picker.FolderName:null;
    }
}

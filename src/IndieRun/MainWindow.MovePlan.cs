using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
namespace IndieRun;
public partial class MainWindow
{
    MoveSelection? pendingMovePlan;
    void OpenMovePlan(List<Package> list,string target)=>BuildMovePlan(list,target).ShowDialog();
    Window BuildMovePlan(List<Package> list,string target)
    {
        var key=MoveSelection.Key(list,packages);if(pendingMovePlan?.Signature!=key)pendingMovePlan=new(list,packages);var session=pendingMovePlan!;session.Refresh(packages);
        var win=Modal("이동 계획 확인",840,720);var dock=new DockPanel{Margin=new Thickness(28)};var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);dock.Children.Add(header);
        var summary=WorkText("",21,"Text.Primary");header.Children.Add(summary);
        var paths=new StackPanel{Margin=new Thickness(0,16,0,10)};header.Children.Add(paths);var targetLabel=WorkText(target,12);targetLabel.TextWrapping=TextWrapping.NoWrap;targetLabel.TextTrimming=TextTrimming.CharacterEllipsis;targetLabel.ToolTip=target;
        var targetRow=new DockPanel();var choose=Btn("변경",()=>{});DockPanel.SetDock(choose,Dock.Right);targetRow.Children.Add(choose);var targetInfo=new StackPanel();targetInfo.Children.Add(WorkText("도착 위치",11,"Text.Muted"));targetInfo.Children.Add(targetLabel);targetRow.Children.Add(targetInfo);paths.Children.Add(targetRow);
        var backupLabel=WorkText(backupStorageRoot,12);backupLabel.TextWrapping=TextWrapping.NoWrap;backupLabel.TextTrimming=TextTrimming.CharacterEllipsis;backupLabel.ToolTip=backupStorageRoot;var backupRow=new DockPanel{Margin=new Thickness(0,12,0,0)};var backupChoose=Btn("변경",()=>{});DockPanel.SetDock(backupChoose,Dock.Right);backupRow.Children.Add(backupChoose);var backupInfo=new StackPanel();backupInfo.Children.Add(WorkText("백업 위치",11,"Text.Muted"));backupInfo.Children.Add(backupLabel);backupRow.Children.Add(backupInfo);paths.Children.Add(backupRow);
        var storage=WorkText("",12,"Text.Secondary");header.Children.Add(storage);var volume=WorkDetails("","드라이브별 여유 공간");header.Children.Add(volume);
        var footer=new StackPanel{Margin=new Thickness(0,18,0,0)};DockPanel.SetDock(footer,Dock.Bottom);dock.Children.Add(footer);
        var warning=WorkText("게임을 종료한 뒤 진행하세요. 백업은 정상 작동 확인 후 직접 삭제하세요.",12,"Warning.Text");footer.Children.Add(warning);
        footer.Children.Add(WorkText("이동에서 뺀 자료는 현재 위치와 연결을 유지합니다.",12,"Text.Muted"));
        var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};footer.Children.Add(actions);
        var back=Btn("계속 편집",()=>win.Close());back.IsCancel=true;actions.Children.Add(back);var backup=Btn("백업만 실행",async()=>{var selected=session.Items(target);win.Close();await StartBackupBatchAsync(selected);});actions.Children.Add(backup);
        var run=Btn("백업 후 이동",async()=>
        {
            var selected=session.Items(target);try{CompositionPlan.Preflight(BackupService(),selected,target);}catch(Exception ex)when(LibraryFault(ex)){warning.Text=ex.Message;return;}
            if(selected.Select(p=>CompositionPlan.Target(p,target)).Any(path=>path.Any(c=>c>127))&&!Confirm("경로 문자 확인","경로에 한글·일본어 등이 포함되어 있어 일부 게임이 실행되지 않을 수 있습니다.","이 경로로 이동",owner:win))return;
            win.Close();pendingMovePlan=null;await ExecuteMoveListAsync(selected,target);
        },true);actions.Children.Add(run);
        var rows=new StackPanel();var expand=new Expander{Header="구성 확인",Content=rows,Margin=new Thickness(0,18,0,0)};dock.Children.Add(new ScrollViewer{Content=expand,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
        void Update()
        {
            var selected=session.Items(target);var games=selected.Count(p=>!p.IsRelatedContent);var content=selected.Count-games;summary.Text=$"게임 {games}개"+(content>0?$" · 관련 자료 {content}개":"")+" 이동";
            var total=selected.Sum(PackageBytes);var copy=selected.Where(p=>p.OmittedFolders.Count>0||!string.Equals(Path.GetPathRoot(p.Source),Path.GetPathRoot(target),StringComparison.OrdinalIgnoreCase)).Sum(PackageBytes);storage.Text="추가 공간 예상 · 백업 "+FormatBytes(total)+(copy>0?" + 이동 복사 "+FormatBytes(copy):"");
            var drives=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);void Add(string path,long bytes){var root=Path.GetPathRoot(Path.GetFullPath(path))!;drives[root]=drives.GetValueOrDefault(root)+bytes;}Add(backupStorageRoot,total);if(copy>0)Add(target,copy);
            volume.Content=WorkText(string.Join("\n",drives.Select(d=>{try{return d.Key+" · 필요 약 "+FormatBytes(d.Value)+" / 사용 가능 "+FormatBytes(new DriveInfo(d.Key).AvailableFreeSpace);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException){return d.Key+" · 여유 공간 확인 필요";}})),12);
            run.IsEnabled=backup.IsEnabled=selected.Count>0&&selected.All(p=>p.ReviewState.Length==0)&&selected.All(p=>BackupEngine.MoveNameError(p.Name)==null);
            foreach(var row in rows.Children.OfType<Border>()){if(row.Tag is not Package p)continue;var stack=(StackPanel)row.Child;var route=stack.Children.OfType<Expander>().FirstOrDefault();if(route?.Content is StackPanel panel&&panel.Children.OfType<TextBlock>().FirstOrDefault() is {} path)
                {
                    var chosen=selected.Find(q=>q.Id==p.Id)??p;var destinationPath=CompositionPlan.Target(chosen,target);path.Text="원본: "+p.Source+"\n도착: "+destinationPath;
                    var note=panel.Children.OfType<TextBlock>().FirstOrDefault(t=>t.Tag as string=="name-status");if(note!=null)note.Text=BackupEngine.MoveNameError(p.Name)??(File.Exists(destinationPath)||Directory.Exists(destinationPath)?"같은 이름의 폴더가 있습니다. 다른 이름을 입력하세요.":destinationPath.Any(c=>c>127)?"일부 게임은 한글·일본어 등이 포함된 경로에서 실행되지 않을 수 있습니다.":"");
                }}
        }
        foreach(var p in session.Available)
        {
            var stack=new StackPanel();var top=new DockPanel();var state=WorkText("",12,"Text.Muted");
            if(!session.Requested.Contains(p.Id))
            {
                var choice=new System.Windows.Controls.Primitives.UniformGrid{Columns=2,Width=144,Margin=new Thickness(12,0,0,0)};var include=Btn("포함",()=>{});var exclude=Btn("제외",()=>{});include.Margin=new Thickness(0,0,6,0);exclude.Margin=new Thickness(0);choice.Children.Add(include);choice.Children.Add(exclude);DockPanel.SetDock(choice,Dock.Right);top.Children.Add(choice);
                // The standard button style is restored through its resource key, without adding a checkbox.
                void RefreshToggle(){var picked=session.Selected.Contains(p.Id);include.Style=(Style)FindResource(picked?"Primary":typeof(Button));exclude.Style=(Style)FindResource(!picked?"Primary":typeof(Button));include.IsEnabled=p.ReviewState.Length==0;exclude.IsEnabled=p.ReviewState.Length==0;state.Text=p.ReviewState.Length>0?"이동 불가 · 위치 확인 필요 · 연결 유지":(session.Shared.Contains(p.Id)?"다른 게임도 사용하는 자료 · ":"")+(picked?"이번 이동에 포함":"현재 위치와 연결 유지");}
                if(p.ReviewState.Length>0){var ownerGame=packages.Find(o=>session.Requested.Contains(o.Id)&&o.RelatedFolders.Any(r=>r.Id==p.Id));if(ownerGame!=null){var locate=Btn("위치 확인",()=>{win.Close();ShowResources(ownerGame);});locate.Margin=new Thickness(0,6,0,0);locate.HorizontalAlignment=HorizontalAlignment.Left;state.Tag=locate;}}
                include.Click+=(_,_)=>{session.Selected.Add(p.Id);RefreshToggle();Update();};exclude.Click+=(_,_)=>{session.Selected.Remove(p.Id);RefreshToggle();Update();};RefreshToggle();
            }
            else state.Text="직접 선택한 게임";
            var title=WorkText(PlayerRules.Title(p),16,"Text.Primary");title.TextTrimming=TextTrimming.CharacterEllipsis;top.Children.Add(title);stack.Children.Add(top);stack.Children.Add(state);if(state.Tag is Button locationAction)stack.Children.Add(locationAction);
            var route=WorkText("",12);var details=new Expander{Header="이동 경로와 폴더명",Margin=new Thickness(0,10,0,0)};stack.Children.Add(details);
            var options=new StackPanel();options.Children.Add(route);var name=new TextBox{Text=p.Name,Margin=new Thickness(0,8,0,0),ToolTip="실제 이동 후 폴더명"};name.TextChanged+=(_,_)=>{p.Name=name.Text;Update();};options.Children.Add(name);var nameStatus=WorkText("",12,"Warning.Text");nameStatus.Tag="name-status";nameStatus.Margin=new Thickness(0,6,0,0);options.Children.Add(nameStatus);details.Content=options;
            rows.Children.Add(new Border{Child=stack,Tag=p,Background=ThemeBrush("Surface.Secondary"),CornerRadius=new CornerRadius(7),Padding=new Thickness(14),Margin=new Thickness(0,0,0,12)});
        }
        choose.Click+=(_,_)=>{var picker=new OpenFolderDialog{Title="보관함으로 옮길 위치 선택",InitialDirectory=target};if(picker.ShowDialog(win)==true){target=picker.FolderName;targetLabel.Text=target;targetLabel.ToolTip=target;Update();}};
        backupChoose.Click+=(_,_)=>{ChooseBackupRoot();backupLabel.Text=backupStorageRoot;backupLabel.ToolTip=backupStorageRoot;Update();};
        Update();win.Content=dock;return win;
    }
}


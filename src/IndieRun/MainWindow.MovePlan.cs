using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using Microsoft.Win32;
namespace IndieRun;
public partial class MainWindow
{
    MoveSelection? pendingMovePlan;
    void OpenMovePlan(List<Package> list,string target)=>BuildMovePlan(list,target).ShowDialog();
    Window BuildMovePlan(List<Package> list,string target)
    {
        var key=MoveSelection.Key(list,packages);if(pendingMovePlan?.Signature!=key)pendingMovePlan=new(list,packages);pendingMovePlan.Refresh(packages);
        return BuildIntakePlan(new IntakeSelection(pendingMovePlan,true),target,false);
    }
    Window BuildImportPlan(List<Package> list,string target)=>BuildIntakePlan(new IntakeSelection(new MoveSelection(list,packages)),target,true);
    Window BuildIntakePlan(IntakeSelection plan,string target,bool importing)
    {
        Action refresh=()=>{};var session=plan.Composition;var win=Modal(importing?"게임 가져오기":"보관함으로 옮기기",840,720);win.Tag=plan;
        var dock=new DockPanel{Margin=new Thickness(28)};var summary=WorkText("",21,"Text.Primary");summary.Margin=new Thickness(0,0,0,16);DockPanel.SetDock(summary,Dock.Top);dock.Children.Add(summary);
        var footer=new StackPanel{Margin=new Thickness(0,16,0,0)};DockPanel.SetDock(footer,Dock.Bottom);dock.Children.Add(footer);
        var resultHint=WorkText("",12,"Text.Secondary");footer.Children.Add(resultHint);var warning=WorkText("",12,"Warning.Text");warning.Margin=new Thickness(0,6,0,0);footer.Children.Add(warning);
        var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};footer.Children.Add(actions);
        var cancel=Btn("취소",()=>win.Close());cancel.IsCancel=true;actions.Children.Add(cancel);var run=Btn("실행 목록에 등록",()=>{},true);run.Name="CommitIntake";run.Margin=new Thickness(0);actions.Children.Add(run);
        var body=new StackPanel();dock.Children.Add(new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
        Button ModeButton(string title,string detail,Action choose)
        {
            var button=Btn("",choose);button.HorizontalContentAlignment=HorizontalAlignment.Left;button.Padding=new Thickness(16);var content=new StackPanel();content.Children.Add(WorkText(title,15,"Text.Primary"));var note=WorkText(detail,12,"Text.Secondary");note.Margin=new Thickness(0,6,0,0);content.Children.Add(note);button.Content=content;button.Tag=title;AutomationProperties.SetName(button,title);return button;
        }
        Button here=null!,move=null!;
        var choices=new Grid{Margin=new Thickness(0,0,0,18)};choices.ColumnDefinitions.Add(new ColumnDefinition());choices.ColumnDefinitions.Add(new ColumnDefinition());
        here=ModeButton("이 위치에서 사용","게임 폴더를 옮기지 않고 실행 목록에 등록합니다.",()=>{plan.SetAll(false);refresh();});here.Name="UseHereChoice";here.Margin=new Thickness(0,0,6,0);choices.Children.Add(here);
        move=ModeButton("보관함으로 옮기기","게임 폴더를 옮기고 실행 목록에 등록합니다.",()=>{plan.SetAll(true);refresh();});move.Name="MoveChoice";move.Margin=new Thickness(6,0,0,0);Grid.SetColumn(move,1);choices.Children.Add(move);choices.Visibility=importing?Visibility.Visible:Visibility.Collapsed;body.Children.Add(choices);
        var moveSettings=new StackPanel{Name="MoveSettings"};body.Children.Add(moveSettings);
        var paths=new StackPanel{Margin=new Thickness(0,0,0,10)};moveSettings.Children.Add(paths);
        var targetLabel=WorkText(target,12);targetLabel.TextWrapping=TextWrapping.NoWrap;targetLabel.TextTrimming=TextTrimming.CharacterEllipsis;targetLabel.ToolTip=target;
        var targetRow=new DockPanel();var choose=Btn("변경",()=>{});choose.Margin=new Thickness(12,0,0,0);DockPanel.SetDock(choose,Dock.Right);targetRow.Children.Add(choose);var targetInfo=new StackPanel();targetInfo.Children.Add(WorkText("도착 위치",11,"Text.Muted"));targetInfo.Children.Add(targetLabel);targetRow.Children.Add(targetInfo);paths.Children.Add(targetRow);
        var pathWarning=ContainerPathNote(target);moveSettings.Children.Add(pathWarning);
        var backup=new ToggleButton{Name="BackupBeforeMove",Content="이동 전 백업 만들기 · 권장",IsChecked=true,Style=(Style)FindResource("TagChip"),HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,4,0,0)};AutomationProperties.SetName(backup,"이동 전 백업 만들기");moveSettings.Children.Add(backup);
        var backupNote=WorkText("",12,"Text.Muted");backupNote.Margin=new Thickness(0,8,0,10);moveSettings.Children.Add(backupNote);
        var backupSettings=new Expander{Header="백업 위치",Margin=new Thickness(0,0,0,10)};moveSettings.Children.Add(backupSettings);var backupRow=new DockPanel();var backupChoose=Btn("변경",()=>{});backupChoose.Margin=new Thickness(12,0,0,0);DockPanel.SetDock(backupChoose,Dock.Right);backupRow.Children.Add(backupChoose);var backupLabel=WorkText(backupStorageRoot,12);backupLabel.TextTrimming=TextTrimming.CharacterEllipsis;backupLabel.TextWrapping=TextWrapping.NoWrap;backupRow.Children.Add(backupLabel);backupSettings.Content=backupRow;
        var storage=WorkText("",12,"Text.Secondary");moveSettings.Children.Add(storage);var volume=WorkDetails("","드라이브별 여유 공간");moveSettings.Children.Add(volume);
        var perGameRows=new StackPanel();var perGame=new Expander{Header="게임별 설정",Content=perGameRows,Margin=new Thickness(0,16,0,0),Visibility=importing&&plan.Games.Count>1?Visibility.Visible:Visibility.Collapsed};body.Children.Add(perGame);
        var overrides=new Dictionary<string,(Button Here,Button Move,TextBlock Status)>();
        foreach(var game in importing&&plan.Games.Count>1?plan.Games:new List<Package>())
        {
            var row=new StackPanel();row.Children.Add(WorkText(PlayerRules.Title(game),15,"Text.Primary"));var status=WorkText("",12);row.Children.Add(status);var buttons=new WrapPanel{Margin=new Thickness(0,10,0,0)};var a=Btn("이 위치에서 사용",()=>{plan.Moving.Remove(game.Id);refresh();});var b=Btn("보관함으로 옮기기",()=>{plan.Moving.Add(game.Id);refresh();});buttons.Children.Add(a);buttons.Children.Add(b);row.Children.Add(buttons);perGameRows.Children.Add(WorkCard(row));overrides[game.Id]=(a,b,status);
        }
        var rows=new StackPanel();var expand=new Expander{Header="구성 확인",Content=rows,Margin=new Thickness(0,18,0,0)};body.Children.Add(expand);
        void Select(Button button,bool selected){button.SetResourceReference(BackgroundProperty,selected?"Accent.Surface":"Surface.Primary");button.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty,selected?"Accent.Fill":"Border.Subtle");button.BorderThickness=new Thickness(1);if(button.Tag is string title&&button.Content is StackPanel panel&&panel.Children[0] is TextBlock label)label.Text=(selected?"✓ ":"")+title;}
        void Update()
        {
            var moved=plan.MoveItems(target,packages);var count=plan.Games.Count;var moveCount=plan.Moving.Count;var added=plan.Games.Count(p=>!plan.Moving.Contains(p.Id)&&!IsAdded(p));var already=plan.Games.Count(p=>!plan.Moving.Contains(p.Id)&&IsAdded(p));
            win.Height=moveCount>0?720:480;summary.Text=count==1?PlayerRules.Title(plan.Games[0]):$"게임 {count}개";Select(here,moveCount==0);Select(move,moveCount==count);moveSettings.Visibility=expand.Visibility=moveCount>0?Visibility.Visible:Visibility.Collapsed;
            foreach(var game in plan.Games){if(!overrides.TryGetValue(game.Id,out var o))continue;var isMove=plan.Moving.Contains(game.Id);Select(o.Here,!isMove);Select(o.Move,isMove);o.Status.Text=isMove?"보관함으로 이동":IsAdded(game)?"이미 실행 목록에 등록됨":"실행 목록에 등록";}
            backup.Content=plan.Backup?"이동 전 백업 만들기 · 권장":"이동 전 백업 만들기";backupNote.Text=plan.Backup?"백업은 직접 삭제할 때까지 보관합니다.":"백업 없이 이동합니다.";backupNote.SetResourceReference(ForegroundProperty,plan.Backup?"Text.Muted":"Warning.Text");backupSettings.Visibility=plan.Backup?Visibility.Visible:Visibility.Collapsed;
            pathWarning.Text=ContainerPathWarning(target);pathWarning.Visibility=pathWarning.Text.Length==0?Visibility.Collapsed:Visibility.Visible;
            var copy=moved.Where(p=>p.OmittedFolders.Count>0||!string.Equals(Path.GetPathRoot(p.Source),Path.GetPathRoot(target),StringComparison.OrdinalIgnoreCase)).Sum(PackageBytes);var total=moved.Sum(PackageBytes);storage.Text="추가 공간 예상 · "+FormatBytes(copy+(plan.Backup?total:0));
            var drives=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);void Add(string path,long bytes){if(bytes==0)return;var root=Path.GetPathRoot(Path.GetFullPath(path))!;drives[root]=drives.GetValueOrDefault(root)+bytes;}if(plan.Backup)Add(backupStorageRoot,total);Add(target,copy);
            volume.Content=WorkText(drives.Count==0?"같은 드라이브의 폴더 전체 이동은 대용량 복사 공간이 필요하지 않습니다.":string.Join("\n",drives.Select(d=>{try{return d.Key+" · 필요 약 "+FormatBytes(d.Value)+" / 사용 가능 "+FormatBytes(new DriveInfo(d.Key).AvailableFreeSpace);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException){return d.Key+" · 여유 공간 확인 필요";}})),12);
            resultHint.Text=moveCount==0?(already==count?"이미 실행 목록에 등록된 게임입니다.":$"실행 목록 등록 {added}개"+(already>0?$" · 이미 등록됨 {already}개":"")):(added>0?$"실행 목록 등록 {added}개 · 이동 {moveCount}개":"이동 "+moveCount+"개")+" · "+(plan.Backup?"백업 후 이동":"백업 없이 이동");
            warning.Text=moveCount>0?"게임을 종료한 뒤 진행하세요.":"";run.Content=moveCount==0?(added==0?"실행 목록에서 보기":"실행 목록에 등록"):added>0?"등록하고 옮기기":"보관함으로 옮기기";
            run.IsEnabled=count>0&&plan.Games.Where(p=>!IsAdded(p)||plan.Moving.Contains(p.Id)).All(p=>p.ReviewState.Length==0)&&moved.All(p=>p.ReviewState.Length==0&&BackupEngine.MoveNameError(p.Name)==null);
            foreach(var row in rows.Children.OfType<Border>()){if(row.Tag is not Package p)continue;row.Visibility=moved.Any(q=>q.Id==p.Id)||!session.Requested.Contains(p.Id)?Visibility.Visible:Visibility.Collapsed;var stack=(StackPanel)row.Child;var route=stack.Children.OfType<Expander>().FirstOrDefault();if(route?.Content is StackPanel panel&&panel.Children.OfType<TextBlock>().FirstOrDefault() is {} path){var chosen=moved.Find(q=>q.Id==p.Id)??p;var destinationPath=CompositionPlan.Target(chosen,target);path.Text="원본: "+p.Source+"\n도착: "+destinationPath;var note=panel.Children.OfType<TextBlock>().FirstOrDefault(t=>t.Tag as string=="name-status");if(note!=null)note.Text=BackupEngine.MoveNameError(p.Name)??(File.Exists(destinationPath)||Directory.Exists(destinationPath)?"같은 이름의 폴더가 있습니다. 다른 이름을 입력하세요.":"");}}
        }
        foreach(var p in session.Available)
        {
            var stack=new StackPanel();var top=new DockPanel();var state=WorkText("",12,"Text.Muted");
            if(!session.Requested.Contains(p.Id))
            {
                var choice=new System.Windows.Controls.Primitives.UniformGrid{Columns=2,Width=144,Margin=new Thickness(12,0,0,0)};var include=Btn("포함",()=>{});var exclude=Btn("제외",()=>{});include.Margin=new Thickness(0,0,6,0);exclude.Margin=new Thickness(0);choice.Children.Add(include);choice.Children.Add(exclude);DockPanel.SetDock(choice,Dock.Right);top.Children.Add(choice);
                // The standard button style is restored through its resource key, without adding a checkbox.
                void RefreshToggle(){var picked=session.Selected.Contains(p.Id);include.Style=(Style)FindResource(picked?"Primary":typeof(Button));exclude.Style=(Style)FindResource(!picked?"Primary":typeof(Button));include.IsEnabled=p.ReviewState.Length==0;exclude.IsEnabled=p.ReviewState.Length==0;state.Text=p.ReviewState.Length>0?"이동 불가 · 자료 폴더 확인 필요":(session.Shared.Contains(p.Id)?"다른 게임도 사용하는 자료 · ":"")+(picked?"이번 이동에 포함":"이동하지 않음 · 자료 연결 유지");}
                if(p.ReviewState.Length>0){var ownerGame=packages.Find(o=>session.Requested.Contains(o.Id)&&o.RelatedFolders.Any(r=>r.Id==p.Id));if(ownerGame!=null){var locate=Btn("위치 확인",()=>{win.Close();ShowResources(ownerGame);});locate.Margin=new Thickness(0,6,0,0);locate.HorizontalAlignment=HorizontalAlignment.Left;state.Tag=locate;}}
                include.Click+=(_,_)=>{session.Selected.Add(p.Id);RefreshToggle();refresh();};exclude.Click+=(_,_)=>{session.Selected.Remove(p.Id);RefreshToggle();refresh();};RefreshToggle();
            }
            else state.Text="선택한 게임";
            var title=WorkText(PlayerRules.Title(p),16,"Text.Primary");title.TextTrimming=TextTrimming.CharacterEllipsis;top.Children.Add(title);stack.Children.Add(top);stack.Children.Add(state);if(state.Tag is Button locationAction)stack.Children.Add(locationAction);
            var route=WorkText("",12);var details=new Expander{Header="이동 경로와 폴더명",Margin=new Thickness(0,10,0,0)};stack.Children.Add(details);
            var options=new StackPanel();options.Children.Add(route);var name=new TextBox{Text=p.Name,Margin=new Thickness(0,8,0,0),ToolTip="실제 이동 후 폴더명"};name.TextChanged+=(_,_)=>{p.Name=name.Text;refresh();};options.Children.Add(name);var nameStatus=WorkText("",12,"Warning.Text");nameStatus.Tag="name-status";nameStatus.Margin=new Thickness(0,6,0,0);options.Children.Add(nameStatus);details.Content=options;
            rows.Children.Add(new Border{Child=stack,Tag=p,Background=ThemeBrush("Surface.Secondary"),CornerRadius=new CornerRadius(7),Padding=new Thickness(14),Margin=new Thickness(0,0,0,12)});
        }
        choose.Click+=(_,_)=>{var picker=new OpenFolderDialog{Title="도착 폴더 선택",InitialDirectory=target};if(picker.ShowDialog(win)==true){target=picker.FolderName;targetLabel.Text=target;targetLabel.ToolTip=target;refresh();}};
        backupChoose.Click+=async(_,_)=>{await ChooseBackupRootAsync();backupLabel.Text=backupStorageRoot;backupLabel.ToolTip=backupStorageRoot;refresh();};
        backup.Click+=(_,_)=>{plan.Backup=backup.IsChecked==true;refresh();};
        run.Click+=async(_,_)=>
        {
            var moved=plan.MoveItems(target,packages);if(moved.Count>0){try{CompositionPlan.Preflight(plan.Backup?BackupService():DirectMoveService(),moved,target);}catch(Exception ex)when(LibraryFault(ex)){warning.Text=ex.Message;return;}
            if(!plan.Backup&&!Confirm("백업 없이 이동","별도 백업을 만들지 않습니다. 파일이 손상되거나 삭제되면 백업으로 복원할 수 없습니다.","백업 없이 이동",owner:win))return;
            if(ContainerPathWarning(target).Length>0&&!Confirm("경로 문자 확인",ContainerPathWarning(target),"이 경로로 이동",owner:win))return;}
            identityNoticeAccepted=true;
            if(moved.Count==0&&plan.Games.All(IsAdded)){win.Close();SetPage("play");RenderPlayer();return;}
            win.Close();pendingMovePlan=null;await ExecuteIntakeAsync(plan,target);
        };
        if(plan.Games.Any(p=>!IsAdded(p)))body.Children.Add(WorkDetails("게임을 구별하기 위한 식별 파일을 게임 폴더에 저장합니다. 저장 권한이 없으면 프로그램 내부에 등록하며, 폴더를 옮긴 뒤에는 게임 폴더를 다시 찾아야 할 수 있습니다.","등록 안내"));
        refresh=Update;refresh();win.Content=dock;return win;
    }
}

using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace IndieRun;
public partial class MainWindow
{
    FolderRename Renames()=>new(DataRoot);
    RenameBatch BatchRenames()=>new(DataRoot);
    bool RenamePending=>Renames().HasPending||BatchRenames().HasPending;
    void RenameFolder(Package game,string? path=null,Window? owner=null)
    {
        path??=game.Source;OpenRenameEditor([new(){PackageId=game.Id,Original=path,Current=path,Name=Path.GetFileName(path)}],owner);
    }
    void RenameGames(IEnumerable<Package> games)=>OpenRenameEditor(games.Select(p=>new RenameTarget{PackageId=p.Id,Original=p.Source,Current=p.Source,Name=Path.GetFileName(p.Source)}).ToList());
    void OpenRenameEditor(List<RenameTarget> targets,Window? owner=null)
    {
        if(targets.Count==0||running||scanning||backupBusy||workspaceBlocked||RenamePending)return;
        var win=BuildRenameEditor(targets,owner);win.ShowDialog();
    }
    Window BuildRenameEditor(List<RenameTarget> targets,Window? owner=null)
    {
        var win=Modal("폴더명 변경",1000,730);win.MinWidth=820;win.MinHeight=560;if(owner!=null)win.Owner=owner;var dock=new DockPanel{Margin=new Thickness(26)};var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);dock.Children.Add(header);
        header.Children.Add(WorkText(targets.Count==1?"폴더명 변경":$"폴더 {targets.Count}개 이름 변경",22,"Text.Primary"));header.Children.Add(WorkText("게임을 종료한 뒤 진행하세요. 별칭과 파일 내용은 유지됩니다.",12,"Text.Muted"));
        var included=targets.Select(t=>t.Key).ToHashSet();var rows=new StackPanel();var entries=new Dictionary<string,(TextBox Box,TextBlock Preview,TextBlock Note,Button Toggle)>();
        var rules=new StackPanel();var fields=new Grid();for(int i=0;i<4;i++)fields.ColumnDefinitions.Add(new());var ruleBoxes=new List<TextBox>();var labels=new[]{"찾을 내용","바꿀 내용","앞에 붙이기","뒤에 붙이기"};
        for(int i=0;i<4;i++){var panel=new StackPanel{Margin=new Thickness(0,0,10,0)};panel.Children.Add(WorkText(labels[i],11,"Text.Muted"));var box=new TextBox{Margin=new Thickness(0,6,0,0)};System.Windows.Automation.AutomationProperties.SetName(box,labels[i]);ruleBoxes.Add(box);panel.Children.Add(box);Grid.SetColumn(panel,i);fields.Children.Add(panel);}rules.Children.Add(fields);
        var make=Btn("전체 변경안 만들기",()=>{foreach(var t in targets.Where(t=>included.Contains(t.Key))){var leaf=Path.GetFileName(t.Original);if(ruleBoxes[0].Text.Length>0)leaf=leaf.Replace(ruleBoxes[0].Text,ruleBoxes[1].Text,StringComparison.Ordinal);entries[t.Key].Box.Text=ruleBoxes[2].Text+leaf+ruleBoxes[3].Text;}});make.HorizontalAlignment=HorizontalAlignment.Left;make.Margin=new Thickness(0,12,0,0);rules.Children.Add(make);rules.Children.Add(WorkText("현재 폴더명을 기준으로 변경안을 다시 만듭니다.",11,"Text.Muted"));
        if(targets.Count>1)header.Children.Add(new Expander{Header="일괄 규칙",Content=rules,Margin=new Thickness(0,16,0,12)});
        var footer=new StackPanel{Margin=new Thickness(0,16,0,0)};DockPanel.SetDock(footer,Dock.Bottom);dock.Children.Add(footer);var warning=WorkText("",12,"Warning.Text");footer.Children.Add(warning);var summary=WorkText("",12,"Text.Muted");footer.Children.Add(summary);var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};footer.Children.Add(actions);var reset=Btn("초기화",()=>{foreach(var t in targets)entries[t.Key].Box.Text=Path.GetFileName(t.Original);});actions.Children.Add(reset);var close=Btn("취소",()=>win.Close());close.IsCancel=true;actions.Children.Add(close);var apply=Btn("백업 후 이름 변경",()=>{},true);actions.Children.Add(apply);bool submitted=false;
        void Update()
        {
            foreach(var t in targets)t.Name=entries[t.Key].Box.Text;var picked=targets.Where(t=>included.Contains(t.Key)).ToList();var changed=picked.Where(t=>t.Name!=Path.GetFileName(t.Original)).ToList();var plan=CaptureWorkspace();var errors=RenameBatch.Problems(plan,picked,DataRoot);warning.Text=errors.FirstOrDefault()??(changed.Any(t=>RenameBatch.Destination(t,picked).Any(c=>c>127))?"한글·일본어 등이 포함된 경로에서는 일부 게임이 실행되지 않을 수 있습니다.":"");summary.Text=$"변경 {changed.Count}개 · 변경할 폴더 전체를 먼저 백업합니다.";apply.IsEnabled=changed.Count>0&&errors.Count==0;
            foreach(var t in targets)
            {
                var row=entries[t.Key];row.Box.IsEnabled=included.Contains(t.Key);row.Toggle.Content=included.Contains(t.Key)?"제외":"다시 포함";row.Preview.Text=t.Name==Path.GetFileName(t.Original)?"변경 없음":Path.GetFileName(t.Original)+" → "+t.Name;row.Preview.ToolTip=row.Preview.Text;row.Note.Text=!included.Contains(t.Key)?"이번 변경에서 제외":BackupEngine.MoveNameError(t.Name)??errors.FirstOrDefault(e=>e.StartsWith(Path.GetFileName(t.Original)+": "))??"";row.Note.Visibility=row.Note.Text.Length>0?Visibility.Visible:Visibility.Collapsed;
            }
        }
        foreach(var t in targets)
        {
            var panel=new StackPanel();var row=new Grid();row.ColumnDefinitions.Add(new(){Width=new GridLength(2,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=new GridLength(3,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});panel.Children.Add(row);
            var game=packages.Find(p=>p.Id==t.PackageId);var title=WorkText(game==null?Path.GetFileName(t.Original):PlayerRules.Title(game),15,"Text.Primary");title.MaxHeight=42;title.TextTrimming=TextTrimming.CharacterEllipsis;var names=new StackPanel{Margin=new Thickness(0,0,16,0),VerticalAlignment=VerticalAlignment.Center};names.Children.Add(title);var current=WorkText(Path.GetFileName(t.Original),11,"Text.Muted");current.TextWrapping=TextWrapping.NoWrap;current.TextTrimming=TextTrimming.CharacterEllipsis;current.ToolTip=t.Original;names.Children.Add(current);row.Children.Add(names);
            var next=new StackPanel();Grid.SetColumn(next,1);row.Children.Add(next);var box=new TextBox{Text=t.Name};System.Windows.Automation.AutomationProperties.SetName(box,"변경할 폴더명 · "+title.Text);next.Children.Add(box);var preview=WorkText("",11,"Text.Muted");preview.TextWrapping=TextWrapping.NoWrap;preview.TextTrimming=TextTrimming.CharacterEllipsis;preview.Margin=new Thickness(0,5,0,0);next.Children.Add(preview);var toggle=Btn("제외",()=>{});toggle.Margin=new Thickness(12,0,0,0);toggle.VerticalAlignment=VerticalAlignment.Top;Grid.SetColumn(toggle,2);row.Children.Add(toggle);
            var note=WorkText("",12,"Warning.Text");note.Margin=new Thickness(0,6,0,0);panel.Children.Add(note);panel.Children.Add(WorkDetails(t.Original,"현재 위치"));entries.Add(t.Key,(box,preview,note,toggle));var card=WorkCard(panel);card.Padding=new Thickness(14);rows.Children.Add(card);
            box.TextChanged+=(_,_)=>{if(entries.Count==targets.Count)Update();};toggle.Click+=(_,_)=>{if(!included.Add(t.Key))included.Remove(t.Key);Update();};
        }
        dock.Children.Add(new ScrollViewer{Content=rows,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});win.Content=dock;Update();
        apply.Click+=(_,_)=>
        {
            Update();if(!apply.IsEnabled||!FlushWorkspace())return;var chosen=targets.Where(t=>included.Contains(t.Key)).ToList();if(chosen.Any(t=>RenameBatch.Destination(t,chosen).Any(c=>c>127))&&!Confirm("경로 문자 확인","일부 게임이 실행되지 않을 수 있습니다.","이 이름으로 변경",owner:win))return;
            try{saveTimer.Stop();BatchRenames().Begin(Clone(CaptureWorkspace()),CopyCatalog(),Clone(playerDocument),chosen);submitted=true;win.Close();owner?.Close();ShowRenameProgress(true);}
            catch(Exception ex)when(LibraryFault(ex)){warning.Text=ex.Message;}
        };
        win.Closing+=(_,e)=>{if(!submitted&&targets.Any(t=>t.Name!=Path.GetFileName(t.Original))&&!DiscardChanges(win))e.Cancel=true;};return win;
    }
    void ShowRenameProgress(bool start=false)
    {
        var batch=BatchRenames();RenameBatchRecord? record;
        try{record=batch.HasPending?batch.Load():batch.LoadLast();}catch(Exception ex)when(LibraryFault(ex)){ErrorInfo("이름 변경 기록","작업 기록을 읽지 못했습니다.",ex.Message);return;}
        if(record==null){Info("이름 변경 기록","아직 이름 변경 기록이 없습니다.");return;}
        BuildRenameProgress(record,start).ShowDialog();
    }
    Window BuildRenameProgress(RenameBatchRecord record,bool start)
    {
        var win=Modal("폴더명 변경 결과",900,680);win.MinWidth=760;win.MinHeight=540;var dock=new DockPanel{Margin=new Thickness(26)};var heading=new StackPanel();DockPanel.SetDock(heading,Dock.Top);dock.Children.Add(heading);var title=WorkText("",22,"Text.Primary");heading.Children.Add(title);var status=WorkText("",12,"Text.Muted");status.Margin=new Thickness(0,8,0,14);heading.Children.Add(status);
        var footer=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,16,0,0)};DockPanel.SetDock(footer,Dock.Bottom);dock.Children.Add(footer);var resume=Btn("계속 진행",()=>{});var keep=Btn("중단 · 완료 항목 유지",()=>{});var undo=Btn("모두 되돌리기",()=>{});var reverse=Btn("이름 되돌리기",()=>{});var close=Btn("닫기",()=>win.Close());close.IsCancel=true;foreach(var b in new[]{resume,keep,undo,reverse,close})footer.Children.Add(b);var rows=new StackPanel();dock.Children.Add(new ScrollViewer{Content=rows,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});win.Content=dock;
        var details=WorkDetails("","오류 내용");details.Visibility=Visibility.Collapsed;heading.Children.Add(details);bool busy=false;CancellationTokenSource? cancellation=null;var stop=Btn("작업 중지",()=>cancellation?.Cancel());footer.Children.Insert(0,stop);
        void Draw()
        {
            rows.Children.Clear();var pending=BatchRenames().HasPending;title.Text=pending?"이름 변경 확인 필요":record.Status=="Completed"?"이름 변경 완료":record.Status=="RolledBack"?"원래 이름으로 복구했습니다":"이름 변경을 중단했습니다";
            if(!busy)status.Text=pending?"서로 연결된 변경은 함께 완료하거나 되돌립니다. 백업은 유지됩니다.":"필요할 때 게임을 실행해 확인하세요. 정상 작동 표시는 직접 남깁니다.";
            foreach(var t in record.Items)
            {
                var p=packages.Find(p=>p.Id==t.PackageId);var panel=new StackPanel();panel.Children.Add(WorkText(p==null?Path.GetFileName(t.Original):PlayerRules.Title(p),16,"Text.Primary"));panel.Children.Add(WorkText(Path.GetFileName(t.Original)+" → "+(pending?"처리 결과 확인 중":Path.GetFileName(t.Current)),12));
                if(!pending&&Directory.Exists(t.Current))
                {
                    var actions=new WrapPanel{Margin=new Thickness(0,10,0,0)};if(p!=null)actions.Children.Add(Btn("게임 실행",()=>LaunchGame(p)));actions.Children.Add(Btn("폴더 열기",()=>OpenSourceFolder(t.Current)));
                    if(t.BackupId.Length>0){var ack=Btn("정상 작동 확인 표시",()=>{});try{if(new BackupEngine(record.Current.Plan.BackupRoot).Load(t.BackupId).ExecutionConfirmedUtc.HasValue){ack.Content="사용자 확인 완료";ack.IsEnabled=false;}}catch(Exception ex)when(LibraryFault(ex)){ack.IsEnabled=false;}ack.Click+=(_,_)=>{try{new BackupEngine(record.Current.Plan.BackupRoot).ConfirmExecution(t.BackupId,true);ack.Content="사용자 확인 완료";ack.IsEnabled=false;}catch(Exception ex)when(LibraryFault(ex)){ErrorInfo("실행 확인 표시","확인 표시를 저장하지 못했습니다.",ex.Message);}};actions.Children.Add(ack);}panel.Children.Add(actions);
                }
                panel.Children.Add(WorkDetails("원래 위치: "+t.Original+"\n현재 기록: "+t.Current,"위치"));rows.Children.Add(WorkCard(panel));
            }
            foreach(var button in new[]{resume,keep,undo})button.Visibility=pending&&!busy?Visibility.Visible:Visibility.Collapsed;reverse.Visibility=!pending&&record.Items.Any(t=>t.Current!=t.Original)?Visibility.Visible:Visibility.Collapsed;stop.Visibility=busy?Visibility.Visible:Visibility.Collapsed;close.IsEnabled=!busy;rows.IsEnabled=!busy;
        }
        async Task Execute(string mode)
        {
            if(busy)return;busy=running=true;saveTimer.Stop();cancellation=new();details.Visibility=Visibility.Collapsed;Draw();title.Text="이름 변경 처리 중";var progress=new Progress<string>(s=>status.Text=s);
            try{record=await Task.Run(()=>mode=="Forward"?BatchRenames().ContinueAsync(progress,cancellation.Token):BatchRenames().StopAsync(mode=="Undo",progress,cancellation.Token));}
            catch(Exception ex)when(LibraryFault(ex)||ex is OperationCanceledException){status.Text="작업을 중단했습니다. 재개하거나 되돌릴 수 있습니다.";details.Content=WorkText(ex.Message,12);details.Visibility=Visibility.Visible;if(BatchRenames().HasPending)record=BatchRenames().Load();}
            finally
            {
                cancellation.Dispose();cancellation=null;running=busy=false;workspaceBlocked=RenamePending;var saved=workspaceStore.Load().Document;if(saved!=null)ApplyWorkspace(saved);LoadTags();LoadPlayer();history.Clear();lastSavedSignature=null;if(!RenamePending)await RefreshWorkspaceAsync(source,false,false);if(page=="play")RenderPlayer();else if(page=="jobs")RenderBackupJobs();else Render();Draw();
            }
        }
        resume.Click+=async(_,_)=>await Execute("Forward");keep.Click+=async(_,_)=>await Execute("Keep");undo.Click+=async(_,_)=>await Execute("Undo");
        reverse.Click+=(_,_)=>{var targets=record.Items.Where(t=>t.Current!=t.Original).Select(t=>new RenameTarget{PackageId=t.PackageId,Original=t.Current,Current=t.Current,Name=Path.GetFileName(t.Original)}).ToList();win.Close();OpenRenameEditor(targets);};
        win.Closing+=(_,e)=>{if(busy)e.Cancel=true;};win.Loaded+=async(_,_)=>{if(start)await Execute("Forward");};Draw();return win;
    }
    async void RecoverRename_Click(object sender,RoutedEventArgs e)
    {
        if(running||scanning||backupBusy)return;if(BatchRenames().HasPending){ShowRenameProgress();return;}running=true;
        try{await Task.Run(()=>Renames().Recover());workspaceBlocked=false;ApplyWorkspace(workspaceStore.Load().Document!);LoadTags();LoadPlayer();running=false;await RefreshWorkspaceAsync(source,false,false);Say("폴더명 변경을 복구했습니다.");}
        catch(Exception ex)when(LibraryFault(ex)){workspaceBlocked=true;ErrorInfo("폴더명 변경 복구","원래 위치와 변경할 위치, 접근 권한을 확인한 뒤 다시 시도하세요.",ex.Message);}
        finally{running=false;Render();}
    }
}


using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
namespace IndieRun;

public partial class MainWindow
{
    void OrganizeOptions_Click(object sender,RoutedEventArgs e)
    {
        if(!HasSelection||running||scanning||backupBusy||scopeDraft!=null)return;
        var menu=new ContextMenu();var edit=new MenuItem {Header="구성 수정",IsEnabled=selected.ReviewState.Length==0&&!workspaceBlocked};edit.Click+=(_,_)=>EditComposition(selected);menu.Items.Add(edit);if(HasResources(selected)){var resources=new MenuItem{Header="관련 자료"};resources.Click+=(_,_)=>ShowResources(selected);menu.Items.Add(resources);}var rename=new MenuItem{Header="폴더명 변경"};rename.Click+=(_,_)=>RenameFolder(selected);menu.Items.Add(rename);var remove=new MenuItem{Header="목록에서 제거"};remove.Click+=(_,_)=>RemoveGame(selected);menu.Items.Add(remove);menu.PlacementTarget=(Button)sender;menu.IsOpen=true;
    }
    ScanSnapshot? compositionSnapshot;
    async void EditComposition(Package original)
    {
        if(running||scanning||backupBusy||workspaceBlocked||!LeaveScopeEdit())return;
        running=true;Workbench.IsEnabled=FooterActions.IsEnabled=false;
        var navs=new[]{PlayerNav,OrganizeNav,TagsNav,LibraryNav,JobsNav,RescanButton};foreach(var nav in navs)nav.IsEnabled=false;
        try{compositionSnapshot=await Task.Run(()=>GameScanner.Scan(original.Source));}
        catch(Exception ex) when(LibraryFault(ex)){ErrorInfo("구성 확인","폴더 위치와 접근 권한을 확인한 뒤 다시 시도하세요.",ex.Message);return;}
        finally{running=false;Workbench.IsEnabled=FooterActions.IsEnabled=true;foreach(var nav in navs)nav.IsEnabled=true;}
        if(!IsVisible){compositionSnapshot=null;return;}
        try{BuildCompositionEditor(original)?.ShowDialog();}finally{compositionSnapshot=null;}
    }
    Window? BuildCompositionEditor(Package original)
    {
        if(snapshot==null||running||scanning||backupBusy||workspaceBlocked||!LeaveScopeEdit())return null;
        var draft=Clone(original);var initial=JsonSerializer.Serialize(draft);var scan=new ScanSnapshot{Root=snapshot.Root};scan.Files.AddRange(snapshot.Files.Where(f=>compositionSnapshot==null||!GameScanner.Within(f.Path,original.Source)));scan.Directories.AddRange(snapshot.Directories.Where(d=>compositionSnapshot==null||!GameScanner.Within(d,original.Source)));
        if(compositionSnapshot!=null){scan.Files.AddRange(compositionSnapshot.Files);scan.Directories.AddRange(compositionSnapshot.Directories);scan.UnreadablePaths.AddRange(compositionSnapshot.UnreadablePaths);scan.Issues.AddRange(compositionSnapshot.Issues);}
        var win=Modal("구성 수정",980,730);win.MinWidth=800;win.MinHeight=600;
        var root=new DockPanel{Margin=new Thickness(26)};
        var header=new StackPanel{Margin=new Thickness(0,0,0,18)};var title=WorkText(PlayerRules.Title(original),21,"Text.Primary");title.TextWrapping=TextWrapping.NoWrap;title.TextTrimming=TextTrimming.CharacterEllipsis;title.ToolTip=title.Text;header.Children.Add(title);header.Children.Add(WorkText("폴더를 선택해 구성을 지정하세요. 파일 이동은 최종 확인 후 진행합니다.",12,"Text.Muted"));DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
        var footer=new DockPanel{Margin=new Thickness(0,18,0,0)};DockPanel.SetDock(footer,Dock.Bottom);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(actions,Dock.Right);footer.Children.Add(actions);
        bool accepted=false,busy=false;var cancel=Btn("취소",()=>win.Close());var apply=Btn("구성 적용",()=>{},true);apply.Name="CompositionApply";cancel.IsCancel=true;cancel.Margin=new Thickness(0,0,8,0);apply.Margin=new Thickness(0);actions.Children.Add(cancel);actions.Children.Add(apply);
        var summary=WorkText("",12,"Text.Secondary");summary.VerticalAlignment=VerticalAlignment.Center;summary.Margin=new Thickness(0,0,16,0);footer.Children.Add(summary);root.Children.Add(footer);
        var body=new Grid();body.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});body.ColumnDefinitions.Add(new(){Width=new GridLength(300)});root.Children.Add(body);
        var left=new DockPanel{Margin=new Thickness(0,0,20,0)};body.Children.Add(left);
        var addRelated=Btn("자료 폴더 연결",()=>{});addRelated.HorizontalAlignment=HorizontalAlignment.Left;addRelated.Margin=new Thickness(0,0,0,12);DockPanel.SetDock(addRelated,Dock.Top);left.Children.Add(addRelated);
        var tree=new TreeView{Name="CompositionTree",BorderThickness=new Thickness(1),BorderBrush=ThemeBrush("Border.Subtle"),Background=ThemeBrush("Surface.Primary"),Padding=new Thickness(8)};ScrollViewer.SetHorizontalScrollBarVisibility(tree,ScrollBarVisibility.Disabled);tree.Resources.Add(typeof(TreeViewItem),Application.Current.FindResource("CompositionTreeItem"));left.Children.Add(tree);
        var right=new StackPanel();Grid.SetColumn(right,1);body.Children.Add(new ScrollViewer{Content=right,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});Grid.SetColumn(body.Children[body.Children.Count-1],1);
        var folderTitle=WorkText("폴더 선택",16,"Text.Primary");folderTitle.TextTrimming=TextTrimming.CharacterEllipsis;right.Children.Add(folderTitle);
        var folderPath=WorkText("",11,"Text.Muted");folderPath.TextTrimming=TextTrimming.CharacterEllipsis;folderPath.TextWrapping=TextWrapping.NoWrap;folderPath.Margin=new Thickness(0,6,0,16);right.Children.Add(folderPath);
        var rolePanel=new StackPanel();right.Children.Add(rolePanel);
        var note=WorkText("",12,"Warning.Text");note.Margin=new Thickness(0,14,0,0);right.Children.Add(note);
        var advanced=new Expander{Header="배치 옵션",Margin=new Thickness(0,20,0,0),Visibility=Visibility.Collapsed};var together=new CheckBox{Content="본편과 같은 폴더 아래에 모으기",Margin=new Thickness(0,12,0,0)};advanced.Content=together;right.Children.Add(advanced);
        var detach=Btn("연결 해제",()=>{});detach.Margin=new Thickness(0,14,0,0);detach.Visibility=Visibility.Collapsed;detach.HorizontalAlignment=HorizontalAlignment.Left;right.Children.Add(detach);
        right.Children.Add(WorkDetails("본편의 내부 파일은 그대로 유지됩니다. DLL·데이터 폴더를 제외하면 실행되지 않을 수 있습니다. 제외한 폴더는 원래 위치에 남고, 별도 게임은 독립된 폴더로 이동합니다. 연결을 해제해도 파일을 삭제하지 않습니다.","구성 안내"));
        string chosen=draft.Source;RelatedFolder? external=null;bool updating=false;
        void UpdateSummary(){summary.Text=CompositionPlan.Summary(draft);apply.IsEnabled=JsonSerializer.Serialize(draft)!=initial&&!busy;}
        void ShowChoice(string path,RelatedFolder? related=null)
        {
            updating=true;chosen=path;external=related;folderTitle.Text=Path.GetFileName(path);folderTitle.ToolTip=path;folderPath.Text=path;folderPath.ToolTip=path;rolePanel.Children.Clear();note.Text="";
            advanced.Visibility=detach.Visibility=related!=null?Visibility.Visible:Visibility.Collapsed;together.IsChecked=related?.Together==true;
            if(related!=null){rolePanel.Children.Add(OrganizeChip("관련 콘텐츠","Composition.Related"));note.Text=ContentLinks.Owners(packages,related.Id).Count>1?"공유 자료 · 기본 이동 대상에서 제외됩니다.":"별도 폴더로 유지하며 본편과 연결합니다.";}
            else
            {
                var active=CompositionPlan.Role(draft,path);
                foreach(var role in new[]{"main","related","game","exclude"})
                {
                    var button=Btn(role=="main"?"본편에 포함":CompositionPlan.Label(role),()=>
                    {
                        try{CompositionPlan.Assign(draft,path,role,playerDocument.Games.Find(g=>g.Id==original.Id)??new(),scan);foreach(var part in draft.Contents){var prior=original.Contents.Find(c=>c.Path.Equals(part.Path,StringComparison.OrdinalIgnoreCase));if(prior!=null)part.Id=prior.Id;}Rebuild();ShowChoice(path);UpdateSummary();}
                        catch(Exception ex) when(LibraryFault(ex)){note.Text=ex.Message;}
                    });button.Margin=new Thickness(0,0,0,8);button.HorizontalContentAlignment=HorizontalAlignment.Left;button.Padding=new Thickness(13,11,13,11);
                    if(active==role){button.Background=ThemeBrush(CompositionPlan.Color(role)+".Surface");button.Foreground=ThemeBrush(CompositionPlan.Color(role)+".Text");button.BorderBrush=ThemeBrush("Border.Focus");}
                    rolePanel.Children.Add(button);
                }
            }
            updating=false;
        }
        TreeViewItem Node(string path,int depth)
        {
            var role=CompositionPlan.Role(draft,path);var row=new DockPanel{LastChildFill=true,Margin=new Thickness(0,4,0,4)};
            var chip=OrganizeChip(CompositionPlan.Label(role),CompositionPlan.Color(role));DockPanel.SetDock(chip,Dock.Right);row.Children.Add(chip);
            var label=OrganizeLabel(Path.GetFileName(path),13);label.Margin=new Thickness(0,0,10,0);label.ToolTip=path;row.Children.Add(label);
            var node=new TreeViewItem{IsSelected=path.Equals(chosen,StringComparison.OrdinalIgnoreCase),Header=row,Tag=path,HorizontalContentAlignment=HorizontalAlignment.Stretch,Padding=new Thickness(0,2,0,2)};
            node.Selected+=(_,e)=>{if(e.OriginalSource==node){ShowChoice(path);e.Handled=true;}};
            var dirs=scan.Directories.Where(d=>string.Equals(Path.GetDirectoryName(d),path,StringComparison.OrdinalIgnoreCase)).OrderBy(d=>d,StringComparer.OrdinalIgnoreCase).ToList();
            if(dirs.Count>0){node.Items.Add(new TreeViewItem{Header="…"});var loaded=false;node.Expanded+=(_,e)=>{if(e.OriginalSource!=node||loaded)return;loaded=true;node.Items.Clear();foreach(var dir in dirs.Take(150))node.Items.Add(Node(dir,depth+1));if(dirs.Count>150)node.Items.Add(new TreeViewItem{Header="폴더가 많습니다 · 더 작은 범위를 선택하세요",IsEnabled=false});};}
            node.IsExpanded=depth==0||GameScanner.Within(chosen,path);return node;
        }
        void Rebuild()
        {
            tree.Items.Clear();tree.Items.Add(Node(draft.Source,0));
            foreach(var related in draft.RelatedFolders)
            {
                var row=new StackPanel();row.Children.Add(OrganizeLabel(related.Name,13));row.Children.Add(OrganizeChip("연결된 자료","Composition.Related"));var node=new TreeViewItem{Header=row,Margin=new Thickness(0,12,0,0),ToolTip=related.Source};node.Selected+=(_,e)=>{if(e.OriginalSource==node){ShowChoice(related.Source,related);e.Handled=true;}};tree.Items.Add(node);
            }
            UpdateSummary();
        }
        together.Click+=(_,_)=>{if(!updating&&external!=null){external.Together=together.IsChecked==true;UpdateSummary();note.Text=external.Together?"보관 위치에 별도 묶음 폴더를 만들어 원래 폴더명 그대로 모읍니다.":"별도 폴더로 유지하며 본편과 함께 이동합니다.";}};
        detach.Click+=(_,_)=>{if(external==null)return;draft.RelatedFolders.Remove(external);Rebuild();ShowChoice(draft.Source);};
        addRelated.Click+=async (_,_)=>
        {
            var picker=new OpenFolderDialog{Title="연결할 자료 폴더 선택"};if(picker.ShowDialog(win)!=true)return;
            var path=Path.GetFullPath(picker.FolderName);if(GameScanner.Within(path,draft.Source)||GameScanner.Within(draft.Source,path)||draft.RelatedFolders.Any(r=>GameScanner.Within(path,r.Source)||GameScanner.Within(r.Source,path))){note.Text="본편 밖의 겹치지 않는 자료 폴더를 선택하세요.";return;}
            busy=true;apply.IsEnabled=cancel.IsEnabled=addRelated.IsEnabled=false;
            try
            {
                var extra=await Task.Run(()=>GameScanner.Scan(path));if(extra.Issues.Count>0)throw new IOException("자료 폴더를 모두 읽지 못했습니다. 접근 권한을 확인하세요.");
                scan.InvalidateIndex();scan.Files.RemoveAll(f=>GameScanner.Within(f.Path,path));scan.Files.AddRange(extra.Files);scan.Directories.AddRange(extra.Directories.Where(d=>!scan.Directories.Contains(d,StringComparer.OrdinalIgnoreCase)));
                var related=new RelatedFolder{Source=path,Name=Path.GetFileName(path)};draft.RelatedFolders.Add(related);
                // Validate before adding a visible connection; cancel still has no persistent effects.
                try{CompositionPlan.Apply(draft,packages,scan);}catch{draft.RelatedFolders.Remove(related);throw;}
                Rebuild();ShowChoice(path,related);
            }
            catch(Exception ex) when(LibraryFault(ex)){note.Text=ex.Message;}
            finally{busy=false;cancel.IsEnabled=addRelated.IsEnabled=true;UpdateSummary();}
        };
        apply.Click+=(_,_)=>
        {
            try
            {
                var next=CompositionPlan.Apply(draft,packages,scan);var plan=Clone(CaptureWorkspace());plan.Packages=next;plan.Checkpoints.RemoveAll(c=>!next.Any(p=>p.Id==c.PackageId)||c.PackageId==draft.Id);plan.Checkpoints.Add(WorkspaceReconciler.Capture(next.Single(p=>p.Id==draft.Id),scan));foreach(var added in next.Where(p=>!plan.Checkpoints.Any(c=>c.PackageId==p.Id)))plan.Checkpoints.Add(WorkspaceReconciler.Capture(added,scan));
                foreach(var related in draft.RelatedFolders)if(searchLocations.Any(l=>!l.Enabled&&GameScanner.Within(related.Source,l.Path)))throw new IOException("연결할 자료 위치의 탐색을 먼저 켜주세요.");
                foreach(var related in draft.RelatedFolders)if(!searchLocations.Any(l=>l.Enabled&&GameScanner.Within(related.Source,l.Path)))SearchLocationRules.Add(plan.SearchLocations!,related.Source);
                if(draft.OmittedFolders.Except(original.OmittedFolders,StringComparer.OrdinalIgnoreCase).Any()&&!Confirm("구성 확인","별도 게임은 따로 이동하고, 제외한 폴더는 현재 위치에 남습니다. 실행에 필요한 폴더인지 확인하세요.","구성 적용",owner:win))return;
                var edited=next.Where(p=>packages.Find(q=>q.Id==p.Id) is not {} prior||JsonSerializer.Serialize(p)!=JsonSerializer.Serialize(prior)).Select(p=>p.Id).Append(draft.Id).Distinct().ToList();
                if(!CommitPlayerEdit(next,CopyCatalog(),Clone(playerDocument),edited,prepared:plan,reconnectedScan:scan))return;
                accepted=true;win.Close();Render();Say("구성을 적용했습니다 · 파일은 이동하지 않았습니다.");
            }
            catch(Exception ex) when(LibraryFault(ex)){note.Text=ex.Message;}
        };
        win.Closing+=(_,e)=>{if(busy){e.Cancel=true;return;}if(!accepted&&JsonSerializer.Serialize(draft)!=initial&&!Confirm("구성 수정 취소","적용하지 않은 구성 수정이 있습니다.","수정 취소",owner:win))e.Cancel=true;};
        Rebuild();ShowChoice(draft.Source);win.Content=root;return win;
    }
}

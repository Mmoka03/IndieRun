using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace IndieRun;

public partial class MainWindow
{
    LibraryDocument libraryDocument=new();
    IdentityInventory identityInventory=new();
    string libraryError="";
    readonly List<string> libraryChanges=[];
    LibraryStore Library()=>new(destination);
    bool HasLibraryFile=>File.Exists(Path.Combine(destination,LibraryStore.JsonName))||File.Exists(Path.Combine(destination,LibraryStore.JsonName+".bak"));
    void InspectLibrary(ScanSnapshot scan,List<Package> current)
    {
        identityInventory=LibraryStore.Inspect(scan);libraryError="";libraryChanges.Clear();
        try
        {
            libraryDocument=Library().Load();
            foreach(var game in libraryDocument.Games)
                if(identityInventory.Locations.TryGetValue(game.Id,out var paths)&&paths.Count==1&&game.LastKnownPath.Length>0&&!game.LastKnownPath.Equals(paths[0],StringComparison.OrdinalIgnoreCase))libraryChanges.Add(game.Folder+" → "+paths[0]);
            var checkedLibrary=scan.IsPartial?new LibraryDocument{LibraryId=libraryDocument.LibraryId,Tags=libraryDocument.Tags,Games=libraryDocument.Games.Where(g=>identityInventory.Locations.ContainsKey(g.Id)).ToList()}:libraryDocument;

            foreach(var tag in libraryDocument.Tags)
                if(!catalog.Tags.Any(t=>t.Id==tag.Id||t.Kind==tag.Kind&&t.Name==tag.Name))catalog.Tags.Add(new TagDefinition {Id=tag.Id,Kind=tag.Kind,Name=tag.Name});
            IdentityReconciler.Rebind(current,checkpoints,checkedLibrary,identityInventory,scan,destination,playerDocument.Games.Where(LaunchPathRepair.HasSettings).Select(g=>g.Id).ToHashSet());
            Library().Reconcile(checkedLibrary,identityInventory,scan);IdentityReconciler.AlignLocations(current,checkedLibrary,identityInventory);
        }
        catch(Exception ex) when(LibraryFault(ex)){libraryError=ex.Message;libraryDocument=new();}
    }
    static bool LibraryFault(Exception ex)=>ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException;
    void SaveLibraryMetadata()
    {
        if(snapshot==null||!HasLibraryFile||backupBusy||scanning||workspaceBlocked)return;
        try
        {
            var known=identityInventory.Locations.Values.SelectMany(paths=>paths).Concat(libraryDocument.Games.Select(g=>g.LastKnownPath)).Where(path=>path.Length>0).ToArray();
            var current=LibraryStore.Inspect(snapshot);
            // Saving must not discard conflicts found outside the current scan.
            IdentityProbe.RefreshKnown(current,known.Concat(current.Locations.Values.SelectMany(paths=>paths)).ToArray(),[]);
            identityInventory=current;
            var store=Library();
            var update=store.SaveMetadata(packages,catalog.Tags,identityInventory,observedMoveLocations);
            if(!snapshot.IsPartial)update=store.Update(doc=>{store.Reconcile(doc,identityInventory,snapshot);IdentityReconciler.AlignLocations(packages,doc,identityInventory);});
            libraryDocument=update.Document;libraryError=update.CsvError??"";
        }
        catch(Exception ex) when(LibraryFault(ex)){libraryError=ex.Message;}
        if(libraryError.Length>0){SaveStatus.Content="계획 저장됨 · 보관함 확인";SaveStatus.ToolTip=libraryError;}
    }
    bool RegisterGame(Package p, bool quiet=false)
    {
        if(snapshot==null||p.Excluded||!Directory.Exists(p.Source))return false;
        try
        {
            RequireUserGameFolder(p.Source);var marker=LibraryStore.ReadIdentity(p.Source);
            // Transfer plans contain copies; object identity does not identify a game registration.
            if(marker!=null&&packages.Any(q=>q!=p&&q.Id.Equals(marker.GameId,StringComparison.OrdinalIgnoreCase)&&!GameRegistration.SameEntry(q,p)))throw new IOException("같은 ID가 다른 게임 항목에 등록되어 있습니다. 보관함에서 복사본이나 폴더 연결을 확인하세요.");
            var repair=!quiet&&marker==null&&libraryDocument.Games.Any(g=>g.Id==p.Id&&g.Status=="IdentityMissing"&&g.LastKnownPath.Equals(p.Source,StringComparison.OrdinalIgnoreCase));
            var oldId=p.Id;var result=GameRegistration.Add(p,catalog.Tags,snapshot,destination,allowLocal:quiet,allowMissingIdentity:repair);
            if(result.LocalOnly){p.Registered=true;p.LocalRegistration=true;p.ScopeHistory.Clear();history.Clear();return true;}
            var update=result.Library!;
            var id=LibraryStore.ReadIdentity(p.Source)!.GameId;
            p.Id=id;foreach(var c in checkpoints.Where(c=>c.PackageId==oldId))c.PackageId=id;
            var path=Path.Combine(p.Source,LibraryStore.MarkerName);var stamp=new FileInfo(path);
            snapshot.InvalidateIndex();snapshot.Files.RemoveAll(f=>f.Path.Equals(path,StringComparison.OrdinalIgnoreCase));snapshot.Files.Add(new ScannedFile(path,stamp.Length,stamp.LastWriteTimeUtc));
            var baseline=checkpoints.Find(c=>c.PackageId==id);
            if(baseline!=null)
            {
                baseline.Files.RemoveAll(f=>f.RelativePath.Equals(LibraryStore.MarkerName,StringComparison.OrdinalIgnoreCase));
                baseline.Files.Add(new FileStamp(LibraryStore.MarkerName,stamp.Length,stamp.LastWriteTimeUtc.Ticks));
            }
            libraryDocument=update.Document;libraryError=update.CsvError??"";
            identityInventory=LibraryStore.Inspect(snapshot);WorkspaceReconciler.Check(p,baseline,snapshot);IdentityReconciler.Flag(p,identityInventory,libraryDocument);
            p.Registered=true;p.LocalRegistration=false;p.ScopeHistory.Clear();FolderPolicy.AbsorbOwnedCandidates(packages,checkpoints,p,playerDocument.Games.Where(LaunchPathRepair.HasSettings).Select(g=>g.Id).ToHashSet());history.Clear();QueueWorkspaceSave();
            if(update.CsvError!=null&&!quiet)ErrorInfo("CSV 저장 확인","게임 등록은 저장했지만 CSV를 갱신하지 못했습니다.",update.CsvError);
            return true;
        }
        catch(Exception ex) when(LibraryFault(ex)){registrationErrors[p.Id]=ex.Message;if(!quiet&&!intakeInProgress)ErrorInfo("실행 목록 등록 실패","실행 목록에 등록하지 못했습니다. 위치와 권한을 확인한 뒤 다시 시도하세요.",ex.Message);return false;}
    }
    void Library_Click(object sender,RoutedEventArgs e)
    {if(backupBusy||scanning)return;SetPage("library");try{libraryDocument=Library().Load();}catch(Exception ex) when(LibraryFault(ex)){libraryError=ex.Message;}RenderLibrary();}
    void RegisterSelected_Click(object sender,RoutedEventArgs e)
    {
        if(!HasSelection||backupBusy||scanning)return;
        if(RegisterGame(selected)){FlushWorkspace();Render();Say("실행 목록에 등록했습니다.");}
    }
    static string LibraryStatus(string value)=>value switch {"LocationCheck"=>"위치 확인 필요","Present"=>"보관 중","External"=>"보관함 밖","Missing"=>"찾을 수 없음","IdentityMissing"=>"ID 확인 필요","Unavailable"=>"접근 확인 필요","Duplicate"=>"ID 중복","Paused"=>"탐색 꺼짐","Offline"=>"위치 연결 확인","NotChecked"=>"아직 확인하지 않음",_=>value};
    void RenderLibrary()
    {
        LibraryContent.Children.Clear();
        var top=new DockPanel();var refresh=Btn("다시 확인",async()=>{await RefreshWorkspaceAsync(source,false,false);RenderLibrary();});DockPanel.SetDock(refresh,Dock.Right);top.Children.Add(refresh);
        top.Children.Add(new TextBlock {Text=destination,ToolTip=destination,TextTrimming=TextTrimming.CharacterEllipsis,Foreground=B("#8796AA"),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,18,0)});top.Margin=new Thickness(0,0,0,18);LibraryContent.Children.Add(top);
        var actions=new WrapPanel {Margin=new Thickness(0,0,0,18)};
        var save=Btn("정보 파일 · CSV 저장",()=>{SaveLibraryMetadata();RenderLibrary();Say(libraryError.Length==0?"보관함 정보와 CSV를 저장했습니다":"보관함 저장 확인 필요");if(libraryError.Length>0)ShowMetadataSaveIssue();});save.IsEnabled=HasLibraryFile&&snapshot!=null&&!scanning&&!backupBusy&&!workspaceBlocked;save.ToolTip=snapshot==null?"‘다시 확인’으로 폴더를 확인한 뒤 저장할 수 있습니다.":null;actions.Children.Add(save);
        actions.Children.Add(Btn("폴더 열기",OpenLibraryFolder));actions.Children.Add(Btn("위치 변경",ChangeLibraryLocation));LibraryContent.Children.Add(actions);
        LibraryContent.Children.Add(ContainerPathNote(destination));
        if(!Directory.Exists(destination))LibraryContent.Children.Add(WorkText("저장된 보관함 위치를 찾을 수 없습니다. ‘위치 변경’에서 폴더를 선택하세요.",13,"Warning.Text"));
        if(libraryError.Length>0)
        {
            var issue=new StackPanel();issue.Children.Add(WorkText("보관함 정보 파일을 확인하세요.",13,"Warning.Text"));
            var retry=Btn("저장 문제 확인",ShowMetadataSaveIssue);retry.HorizontalAlignment=HorizontalAlignment.Left;retry.Margin=new Thickness(0,10,0,0);issue.Children.Add(retry);issue.Children.Add(WorkDetails(libraryError));LibraryContent.Children.Add(WorkCard(issue,"Warning.Surface"));
        }
        foreach(var change in libraryChanges)LibraryContent.Children.Add(Text("위치 변경 · "+change,12,"#6484AC"));
        if(identityInventory.Problems.Count>0)
        {
            var issue=new StackPanel();issue.Children.Add(WorkText($"게임 식별 파일 {identityInventory.Problems.Count:N0}개를 확인하세요.",13,"Warning.Text"));
            issue.Children.Add(WorkDetails(string.Join("\n\n",identityInventory.Problems.Take(100).Select(problem=>problem.Path+"\n"+problem.Message))+(identityInventory.Problems.Count>100?"\n\n처음 100개를 표시합니다.":"")));LibraryContent.Children.Add(WorkCard(issue,"Warning.Surface"));
        }
        foreach(var pair in identityInventory.Locations.Where(pair=>pair.Value.Count>1))
        {
            var box=new StackPanel();box.Children.Add(Text("같은 ID의 복사본 · 자동 연결하지 않았습니다",16,"#A38350"));
            foreach(var path in pair.Value)
            {
                var row=new DockPanel {Margin=new Thickness(0,10,0,0)};var split=Btn("이 복사본에 새 ID",async()=>
                {
                    if(!Confirm("복사본 구분","선택한 폴더에 새 ID를 부여해 별도 게임으로 등록합니다. 폴더명은 유지됩니다.","별도 게임으로 등록",path))return;
                    await AssignCopyIdentityAsync(path,pair.Key);
                });
                if(libraryDocument.Games.Any(g=>g.LastKnownPath.Equals(path,StringComparison.OrdinalIgnoreCase))||packages.Any(p=>p.Registered!=false&&p.Source.Equals(path,StringComparison.OrdinalIgnoreCase))){split.Content="등록된 폴더";split.IsEnabled=false;split.ToolTip="현재 연결을 유지합니다. 다른 복사본을 선택해 별도 게임으로 등록하세요.";}
                DockPanel.SetDock(split,Dock.Right);row.Children.Add(split);row.Children.Add(Text(path,12));box.Children.Add(row);
            }
            LibraryContent.Children.Add(new Border {Child=box,Background=B("#FFF7E9"),CornerRadius=new CornerRadius(8),Padding=new Thickness(16),Margin=new Thickness(0,0,0,16)});
        }
        var registeredIds=libraryDocument.Games.Select(g=>g.Id).ToHashSet();
        foreach(var candidate in PageItems(packages.Where(p=>!p.Excluded&&GameScanner.Within(p.Source,destination)&&!registeredIds.Contains(p.Id)).ToList(),"library-candidates","",LibraryContent,RenderLibrary))
        {
            var row=new DockPanel {Margin=new Thickness(0,0,0,14)};var register=Btn("등록",()=>{if(RegisterGame(candidate)){FlushWorkspace();RenderLibrary();}});DockPanel.SetDock(register,Dock.Right);row.Children.Add(register);row.Children.Add(Text("새로 발견 · 미등록 · "+candidate.Folder,14));LibraryContent.Children.Add(row);
        }
        if(libraryDocument.Games.Count==0)LibraryContent.Children.Add(WorkText("정리 화면에서 게임을 가져오면 등록 정보가 표시됩니다.",13,"Text.Muted"));
        foreach(var game in PageItems(libraryDocument.Games.OrderBy(g=>g.Alias.Length>0?g.Alias:g.Folder).ToList(),"library","",LibraryContent,RenderLibrary))
        {
            var box=new StackPanel();box.Children.Add(Text(game.Alias.Length>0?game.Alias:game.Folder,18));
            box.Children.Add(Text(LibraryStatus(game.Status)+" · "+game.Folder,12,"#8190A5"));
            box.Children.Add(Text(string.Join(" · ",game.Genres.Concat(game.Categories)),12,"#6484AC"));
            var buttons=new WrapPanel {Margin=new Thickness(0,12,0,0)};
            var p=packages.Find(p=>p.Id==game.Id);var paths=identityInventory.Locations.GetValueOrDefault(game.Id);
            if(p!=null)buttons.Children.Add(Btn("분류 · 별칭 편집",()=>{if(EditGames([p]))RenderLibrary();}));
            if(p!=null&&game.Status is "Missing" or "Unavailable" or "IdentityMissing")buttons.Children.Add(Btn("게임 폴더 다시 찾기",async()=>await ReconnectGameAsync(p)));
            if(game.Status is "Paused" or "Offline" or "NotChecked")buttons.Children.Add(Btn("탐색 위치",async()=>await ManageSearchLocationsAsync()));
            if(paths?.Count==1)
            {
                buttons.Children.Add(Btn("게임 폴더",()=>OpenSourceFolder(paths[0])));
                var note=Btn("정보 메모장 남기기",()=>
                {
                    try{SaveLibraryMetadata();var current=libraryDocument.Games.Find(g=>g.Id==game.Id)??game;LibraryStore.WriteNote(paths[0],current);Say("게임 폴더에 정보 메모장을 저장했습니다 · 다시 검색하면 변경을 확인할 수 있어요");}
                    catch(Exception ex) when(LibraryFault(ex)){ErrorInfo("메모장 저장","정보 메모장을 저장하지 못했습니다.",ex.Message);}
                });note.ToolTip="선택 사항 · 현재 별칭과 분류를 게임 폴더에 저장합니다. 기존 백업 이후의 파일 추가이므로 이전 원래 위치로 되돌리기는 변경 확인으로 중단될 수 있습니다.";buttons.Children.Add(note);
            }
            if(game.Status=="IdentityMissing"&&p!=null)buttons.Children.Add(Btn("이 폴더 다시 등록",()=>{if(RegisterGame(p)){FlushWorkspace();RenderLibrary();}}));
            box.Children.Add(buttons);LibraryContent.Children.Add(new Border {Child=box,Padding=new Thickness(18),Background=B("#F5F8FE"),CornerRadius=new CornerRadius(8),Margin=new Thickness(0,0,0,12)});
        }
        LibraryContent.Children.Add(WorkDetails("검색 범위 밖으로 직접 옮긴 게임은 새 위치를 검색해야 찾을 수 있습니다. CSV는 자동 생성하며, CSV를 수정해도 프로그램에 반영되지는 않습니다.","보관함 안내"));
    }
}

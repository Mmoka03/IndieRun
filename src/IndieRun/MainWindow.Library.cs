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
            Library().Reconcile(libraryDocument,identityInventory,scan);
            foreach(var tag in libraryDocument.Tags)
                if(!catalog.Tags.Any(t=>t.Id==tag.Id||t.Kind==tag.Kind&&t.Name==tag.Name))catalog.Tags.Add(new TagDefinition {Id=tag.Id,Kind=tag.Kind,Name=tag.Name});
            IdentityReconciler.Rebind(current,checkpoints,libraryDocument,identityInventory,scan,destination);
        }
        catch(Exception ex) when(LibraryFault(ex)){libraryError=ex.Message;libraryDocument=new();}
    }
    static bool LibraryFault(Exception ex)=>ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException;
    void SaveLibraryMetadata()
    {
        if(snapshot==null||!HasLibraryFile||backupBusy||scanning||workspaceBlocked)return;
        try
        {
            identityInventory=LibraryStore.Inspect(snapshot);
            var store=Library();
            store.SaveMetadata(packages,catalog.Tags,identityInventory);
            var update=store.Update(doc=>store.Reconcile(doc,identityInventory,snapshot));
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
            var marker=LibraryStore.ReadIdentity(p.Source);
            if(marker!=null&&packages.Any(q=>q!=p&&q.Id==marker.GameId))throw new IOException("이 ID의 기존 등록이 있습니다. 먼저 복사본 또는 폴더 연결을 확인하세요.");
            var repair=!quiet&&marker==null&&libraryDocument.Games.Any(g=>g.Id==p.Id&&g.Status=="IdentityMissing"&&g.LastKnownPath.Equals(p.Source,StringComparison.OrdinalIgnoreCase));
            var oldId=p.Id;var result=GameRegistration.Add(p,catalog.Tags,snapshot,destination,allowLocal:quiet,allowMissingIdentity:repair);
            if(result.LocalOnly){p.Registered=true;p.LocalRegistration=true;p.ScopeHistory.Clear();history.Clear();return true;}
            var update=result.Library!;
            var id=LibraryStore.ReadIdentity(p.Source)!.GameId;
            p.Id=id;foreach(var c in checkpoints.Where(c=>c.PackageId==oldId))c.PackageId=id;
            var path=Path.Combine(p.Source,LibraryStore.MarkerName);var stamp=new FileInfo(path);
            snapshot.Files.RemoveAll(f=>f.Path.Equals(path,StringComparison.OrdinalIgnoreCase));snapshot.Files.Add(new ScannedFile(path,stamp.Length,stamp.LastWriteTimeUtc));
            var baseline=checkpoints.Find(c=>c.PackageId==id);
            if(baseline!=null)
            {
                baseline.Files.RemoveAll(f=>f.RelativePath.Equals(LibraryStore.MarkerName,StringComparison.OrdinalIgnoreCase));
                baseline.Files.Add(new FileStamp(LibraryStore.MarkerName,stamp.Length,stamp.LastWriteTimeUtc.Ticks));
            }
            libraryDocument=update.Document;libraryError=update.CsvError??"";
            identityInventory=LibraryStore.Inspect(snapshot);WorkspaceReconciler.Check(p,baseline,snapshot);IdentityReconciler.Flag(p,identityInventory,libraryDocument);
            p.Registered=true;p.LocalRegistration=false;p.ScopeHistory.Clear();history.Clear();QueueWorkspaceSave();
            if(update.CsvError!=null&&!quiet)ErrorInfo("CSV 저장 확인","게임 등록은 저장했지만 CSV를 갱신하지 못했습니다.",update.CsvError);
            return true;
        }
        catch(Exception ex) when(LibraryFault(ex)){registrationErrors[p.Id]=ex.Message;if(!quiet)ErrorInfo("게임 추가 확인","게임을 추가하지 못했습니다. 위치와 권한을 확인한 뒤 다시 시도하세요.",ex.Message);return false;}
    }
    async void Library_Click(object sender,RoutedEventArgs e)
    {if(backupBusy||scanning)return;SetPage("library");await RefreshWorkspaceAsync(source,false,false);RenderLibrary();}
    void RegisterSelected_Click(object sender,RoutedEventArgs e)
    {
        if(!HasSelection||backupBusy||scanning)return;
        if(RegisterGame(selected)){FlushWorkspace();Render();Say("보관함에 등록했습니다 · 고유 ID와 분류 정보를 저장했어요");}
    }
    static string LibraryStatus(string value)=>value switch {"Present"=>"보관 중","External"=>"보관함 밖","Missing"=>"찾을 수 없음","IdentityMissing"=>"ID 확인 필요","Unavailable"=>"접근 확인 필요","Duplicate"=>"ID 중복",_=>value};
    void RenderLibrary()
    {
        LibraryContent.Children.Clear();
        var top=new DockPanel();var refresh=Btn("다시 확인",async()=>{await RefreshWorkspaceAsync(source,false,false);RenderLibrary();});DockPanel.SetDock(refresh,Dock.Right);top.Children.Add(refresh);
        top.Children.Add(new TextBlock {Text=destination,ToolTip=destination,TextTrimming=TextTrimming.CharacterEllipsis,Foreground=B("#8796AA"),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,18,0)});top.Margin=new Thickness(0,0,0,18);LibraryContent.Children.Add(top);
        var actions=new WrapPanel {Margin=new Thickness(0,0,0,18)};
        var save=Btn("메타데이터 · CSV 저장",()=>{SaveLibraryMetadata();RenderLibrary();Say(libraryError.Length==0?"보관함 정보와 CSV를 저장했습니다":"보관함 저장 확인 필요");});save.IsEnabled=HasLibraryFile;actions.Children.Add(save);
        actions.Children.Add(Btn("보관함 폴더",()=>OpenSourceFolder(destination)));LibraryContent.Children.Add(actions);
        if(libraryError.Length>0)LibraryContent.Children.Add(Text("보관함 확인 필요 · "+libraryError,13,"#A38350"));
        foreach(var change in libraryChanges)LibraryContent.Children.Add(Text("위치 변경 · "+change,12,"#6484AC"));
        foreach(var problem in identityInventory.Problems)LibraryContent.Children.Add(Text("식별 파일 확인 · "+problem.Path+" · "+problem.Message,12,"#A38350"));
        foreach(var pair in identityInventory.Locations.Where(pair=>pair.Value.Count>1))
        {
            var box=new StackPanel();box.Children.Add(Text("같은 ID의 복사본 · 자동 연결하지 않았습니다",16,"#A38350"));
            foreach(var path in pair.Value)
            {
                var row=new DockPanel {Margin=new Thickness(0,10,0,0)};var split=Btn("이 복사본에 새 ID",async()=>
                {
                    if(!Confirm("복사본 구분","선택한 폴더에 새 ID를 부여해 별도 게임으로 등록합니다. 폴더명은 유지됩니다.","별도 게임으로 등록",path))return;
                    try
                    {
                        LibraryStore.AssignCopyIdentity(path,pair.Key);
                        // A previously discovered copy has its own workspace edits; keep those.
                        var copy=packages.Find(p=>p.Source.Equals(path,StringComparison.OrdinalIgnoreCase));
                        if(copy!=null)
                        {
                            var id=LibraryStore.ReadIdentity(path)!.GameId;var old=copy.Id;copy.Id=id;foreach(var c in checkpoints.Where(c=>c.PackageId==old))c.PackageId=id;
                            RegisterGame(copy);
                        }
                        await RefreshWorkspaceAsync(source,false,false);RenderLibrary();
                    }
                    catch(Exception ex) when(LibraryFault(ex)){ErrorInfo("복사본 구분","복사본을 구분하지 못했습니다.",ex.Message);}
                });DockPanel.SetDock(split,Dock.Right);row.Children.Add(split);row.Children.Add(Text(path,12));box.Children.Add(row);
            }
            LibraryContent.Children.Add(new Border {Child=box,Background=B("#FFF7E9"),CornerRadius=new CornerRadius(8),Padding=new Thickness(16),Margin=new Thickness(0,0,0,16)});
        }
        foreach(var candidate in packages.Where(p=>!p.Excluded&&GameScanner.Within(p.Source,destination)&&!libraryDocument.Games.Any(g=>g.Id==p.Id)))
        {
            var row=new DockPanel {Margin=new Thickness(0,0,0,14)};var register=Btn("등록",()=>{if(RegisterGame(candidate)){FlushWorkspace();RenderLibrary();}});DockPanel.SetDock(register,Dock.Right);row.Children.Add(register);row.Children.Add(Text("새로 발견 · 미등록 · "+candidate.Folder,14));LibraryContent.Children.Add(row);
        }
        if(libraryDocument.Games.Count==0)LibraryContent.Children.Add(Text("정리 화면에서 게임을 선택해 ‘선택한 게임 추가’을 누르세요. 이동할 게임은 백업 전에 자동 등록합니다.",13,"#8190A5"));
        foreach(var game in libraryDocument.Games.OrderBy(g=>g.Alias.Length>0?g.Alias:g.Folder))
        {
            var box=new StackPanel();box.Children.Add(Text(game.Alias.Length>0?game.Alias:game.Folder,18));
            box.Children.Add(Text(LibraryStatus(game.Status)+" · "+game.Folder,12,"#8190A5"));
            box.Children.Add(Text(string.Join(" · ",game.Genres.Concat(game.Categories)),12,"#6484AC"));
            var buttons=new WrapPanel {Margin=new Thickness(0,12,0,0)};
            var p=packages.Find(p=>p.Id==game.Id);var paths=identityInventory.Locations.GetValueOrDefault(game.Id);
            if(p!=null)buttons.Children.Add(Btn("분류 · 별칭 편집",()=>{if(EditGames([p]))RenderLibrary();}));
            if(p!=null&&game.Status is "Missing" or "Unavailable" or "IdentityMissing")buttons.Children.Add(Btn("폴더 다시 연결",async()=>await ReconnectGameAsync(p)));
            if(paths?.Count==1)
            {
                buttons.Children.Add(Btn("게임 폴더",()=>OpenSourceFolder(paths[0])));
                var note=Btn("정보 메모장 남기기",()=>
                {
                    try{SaveLibraryMetadata();var current=libraryDocument.Games.Find(g=>g.Id==game.Id)??game;LibraryStore.WriteNote(paths[0],current);Say("게임 폴더에 정보 메모장을 저장했습니다 · 다시 검색하면 변경을 확인할 수 있어요");}
                    catch(Exception ex) when(LibraryFault(ex)){ErrorInfo("메모장 저장","정보 메모장을 저장하지 못했습니다.",ex.Message);}
                });note.ToolTip="선택 사항 · 현재 별칭과 분류를 게임 폴더에 저장합니다. 기존 백업 이후의 파일 추가이므로 이전 이동 되돌리기는 변경 확인으로 중단될 수 있습니다.";buttons.Children.Add(note);
            }
            if(game.Status=="IdentityMissing"&&p!=null)buttons.Children.Add(Btn("이 폴더 다시 등록",()=>{if(RegisterGame(p)){FlushWorkspace();RenderLibrary();}}));
            box.Children.Add(buttons);LibraryContent.Children.Add(new Border {Child=box,Padding=new Thickness(18),Background=B("#F5F8FE"),CornerRadius=new CornerRadius(8),Margin=new Thickness(0,0,0,12)});
        }
        LibraryContent.Children.Add(Text("검색 범위 밖의 수동 이동은 새 위치를 검색해야 찾을 수 있습니다. CSV는 자동 생성하며 수정 사항을 다시 읽지는 않습니다.",12,"#8796AA"));
    }
}

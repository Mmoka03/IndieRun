using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace IndieRun;

public partial class MainWindow
{
    static Brush ThemeBrush(string key)=>(Brush)Application.Current.FindResource(key);
    static TextBlock ThemeText(string text,double size=13,string key="Text.Primary")
    {var block=new TextBlock {Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap};block.SetResourceReference(TextBlock.ForegroundProperty,key);return block;}
    LaunchState PlayerStatus(Package p)
    {
        if(moveLocationIssues.TryGetValue(p.Id,out var issue))return new(LaunchProblem.Identity,"이동 위치 확인",issue.Evidence.Message);
        var state=GameLaunch.Inspect(p,PlayFor(p),libraryDocument.Games.Any(g=>g.Id==p.Id));
        return !state.NeedsAttention&&launchFailures.TryGetValue(p.Id,out var failed)?failed:state;
    }
    void ShowLaunchProblem(Package p,LaunchState state,string? detail=null)
    {
        if(moveLocationIssues.ContainsKey(p.Id)){ShowMoveLocationIssue(p);return;}
        var choices=new List<DialogChoice>{new("close","닫기")};
        if(state.Problem==LaunchProblem.LocationOffline){choices.Add(new("locations","탐색 위치",true));choices.Add(new("refresh","다시 확인"));}
        else if(state.Problem==LaunchProblem.FolderMissing)choices.Add(new("reconnect","게임 폴더 다시 찾기",true));
        else if(state.Problem==LaunchProblem.Identity){choices.Add(new("reconnect","게임 폴더 다시 찾기"));choices.Add(new("library","보관함 확인",true));}
        else if(state.Problem==LaunchProblem.Access)choices.Add(new("refresh","다시 확인",true));
        else
        {
            choices.Add(new("settings","실행 설정",true));
            if(state.Problem is LaunchProblem.InvalidProgram or LaunchProblem.Failed or LaunchProblem.Settings)choices.Add(new("refresh","다시 확인"));
        }
        var choice=Choose(state.Label,state.Message,choices.ToArray(),"close",p.Source+(string.IsNullOrWhiteSpace(detail)?"":"\n"+detail),DialogTone.Warning,subject:PlayerRules.Title(p));
        if(choice=="locations")LocationsFromDialog();
        else if(choice=="settings"){if(EditGames([packages.Find(q=>q.Id==p.Id)??p],launchOnly:true,advancedLaunch:state.Problem==LaunchProblem.WorkingFolderMissing))RenderPlayerItems();}
        else if(choice=="reconnect")ReconnectFromDialog(p);
        else if(choice=="library"){SetPage("library");RenderLibrary();}
        else if(choice=="refresh")RefreshFromDialog(p);
    }
    async void LocationsFromDialog()=>await ManageSearchLocationsAsync();
    async void ReconnectFromDialog(Package p)=>await ReconnectGameAsync(p);
    async void RefreshFromDialog(Package p)
    {
        launchFailures.Remove(p.Id);
        if(p.ReviewState is "offline" or "unverified")await RefreshWorkspaceAsync(source,false,false);
        RenderPlayerItems();
    }
    bool ConfirmConnection(Package p,string folder,bool identified)
    {
        var body=new StackPanel();body.Children.Add(ThemeText("기존 위치",11,"Text.Muted"));body.Children.Add(ThemeText(p.Source,12,"Text.Secondary"));var label=ThemeText("새 위치",11,"Text.Muted");label.Margin=new Thickness(0,16,0,0);body.Children.Add(label);body.Children.Add(ThemeText(folder,13));
        var dialog=new AppDialog(DialogOwner(),"게임 폴더 다시 찾기",PlayerRules.Title(p)+"\n"+(identified?"같은 게임 ID를 확인했습니다.":"별칭·태그와 실행 설정을 새 위치에 연결합니다."),[new("cancel","취소"),new("connect","이 폴더 사용",true)],"cancel",extra:body);
        return dialog.Open()=="connect";
    }
    Task ReconnectGameAsync(Package p)=>ReconnectGameAtAsync(p,null);
    async Task ReconnectGameAtAsync(Package p,string? suggestedFolder)
    {
        if(scanning||backupBusy||workspaceBlocked||workspaceLock==null||File.Exists(Edits().Pending))return;
        var initial=suggestedFolder??p.Source;while(!Directory.Exists(initial)&&Directory.GetParent(initial) is { } parent)initial=parent.FullName;
        var picker=new OpenFolderDialog {Title="게임의 현재 폴더 선택",InitialDirectory=initial};if(picker.ShowDialog(this)!=true)return;
        try
        {
            RequireUserGameFolder(picker.FolderName);
            scanning=true;IsEnabled=false;var original=Clone(CaptureWorkspace());var settings=Clone(playerDocument);var stored=Clone(libraryDocument);var protectedRoots=ProtectedGameRoots();
            var contextRoot=original.Packages.Where(q=>FolderPolicy.Passive(q)&&GameScanner.Within(picker.FolderName,q.Source)).Select(q=>q.Source).Append(picker.FolderName).OrderBy(r=>r.Length).First();
            async Task<ScanSnapshot> ScanFolder()
            {Say("폴더 확인 중…");return await Task.Run(()=>GameScanner.Scan(contextRoot,protectedRoots));}
            var part=await ScanFolder();
            WorkspaceDocument Prepare(ScanSnapshot context){var scan=new ScanSnapshot{Root=picker.FolderName};scan.SearchRoots.AddRange(searchLocations.Where(l=>l.Enabled).Select(l=>l.Path));scan.Files.AddRange(context.FilesUnder(picker.FolderName));scan.Directories.AddRange(context.DirectoriesUnder(picker.FolderName));scan.UnreadablePaths.AddRange(context.UnreadablePaths.Where(r=>GameScanner.Within(r,picker.FolderName)));return ReconnectPlanner.PrepareWithContext(original,p.Id,picker.FolderName,scan,stored,settings,context);}
            var plan=await Task.Run(()=>Prepare(part));var resolved=plan.Packages.Single(q=>q.Id==p.Id).Source;RequireUserGameFolder(resolved);
            if(!ConfirmConnection(p,resolved,LibraryStore.ReadIdentity(resolved)!=null)){Say("연결을 취소했습니다");return;}
            var checkedPart=await ScanFolder();
            static string Fingerprint(ScanSnapshot scan)=>scan.Fingerprint()+string.Join('\n',scan.Directories.Order(StringComparer.OrdinalIgnoreCase));
            if(Fingerprint(part)!=Fingerprint(checkedPart))throw new IOException("확인 중 폴더 내용이 변경됐습니다. 게임 폴더를 다시 선택하세요.");
            plan=await Task.Run(()=>Prepare(checkedPart));
            var repairedPlay=Clone(playerDocument);await Task.Run(()=>LaunchPathRepair.Apply(plan.Packages,original.Checkpoints,repairedPlay,checkedPart,original.Packages));
            plan.Packages.Single(q=>q.Id==p.Id).Size=FormatBytes(checkedPart.FilesUnder(resolved).Sum(f=>f.Length));
            var merged=new ScanSnapshot {Root=source,IsPartial=snapshot==null||snapshot.IsPartial};
            if(snapshot!=null)
            {merged.SearchRoots.AddRange(snapshot.SearchRoots);merged.InactivePaths.AddRange(snapshot.InactivePaths.Where(r=>!GameScanner.Within(picker.FolderName,r)));merged.OfflineRoots.AddRange(snapshot.OfflineRoots.Where(r=>!GameScanner.Within(picker.FolderName,r)));merged.Files.AddRange(snapshot.Files.Where(f=>!GameScanner.Within(f.Path,contextRoot)));merged.Directories.AddRange(snapshot.Directories.Where(d=>!GameScanner.Within(d,contextRoot)));merged.UnreadablePaths.AddRange(snapshot.UnreadablePaths.Where(d=>!GameScanner.Within(d,contextRoot)));merged.Issues.AddRange(snapshot.Issues);}
            if(!merged.SearchRoots.Any(root=>GameScanner.Within(picker.FolderName,root)))merged.SearchRoots.Add(picker.FolderName);
            merged.Files.AddRange(checkedPart.Files);merged.Directories.AddRange(checkedPart.Directories);
            if(!CommitPlayerEdit(plan.Packages,CopyCatalog(),repairedPlay,plan.Packages.Where(q=>q.Id==p.Id||original.Packages.Any(o=>o.Id==q.Id&&o.Source!=q.Source)).Select(q=>q.Id),prepared:plan,reconnectedScan:merged))return;
            moveLocationIssues.Remove(p.Id);recentMoveIds.Add(p.Id);resultPages.Remove("organize");
            if(page=="play")RenderPlayerItems();else if(page=="library")RenderLibrary();else Render();Say("게임 폴더 위치를 변경했습니다.");
        }
        catch(Exception ex) when(LibraryFault(ex)){ErrorInfo("폴더 연결","게임 폴더 위치를 변경하지 못했습니다.",ex.Message);Say("게임 폴더 위치를 변경하지 못했습니다");}
        finally{scanning=false;IsEnabled=true;}
    }
}

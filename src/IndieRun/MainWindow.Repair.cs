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
        var state=GameLaunch.Inspect(p,PlayFor(p),libraryDocument.Games.Any(g=>g.Id==p.Id));
        return !state.NeedsAttention&&launchFailures.TryGetValue(p.Id,out var failed)?failed:state;
    }
    void ShowLaunchProblem(Package p,LaunchState state,string? detail=null)
    {
        var choices=new List<DialogChoice>{new("close","닫기")};
        if(state.Problem==LaunchProblem.LocationOffline){choices.Add(new("locations","탐색 위치",true));choices.Add(new("refresh","다시 확인"));}
        else if(state.Problem==LaunchProblem.FolderMissing)choices.Add(new("reconnect","폴더 다시 연결",true));
        else if(state.Problem==LaunchProblem.Identity){choices.Add(new("reconnect","폴더 다시 연결"));choices.Add(new("library","보관함 확인",true));}
        else if(state.Problem==LaunchProblem.Access)choices.Add(new("refresh","다시 확인",true));
        else choices.Add(new("settings","실행 설정",true));
        var choice=Choose(state.Label,state.Message,choices.ToArray(),"close",p.Source+(string.IsNullOrWhiteSpace(detail)?"":"\n"+detail),DialogTone.Warning,subject:PlayerRules.Title(p));
        if(choice=="locations")LocationsFromDialog();
        else if(choice=="settings"){if(EditGames([packages.Find(q=>q.Id==p.Id)??p],launchOnly:true,advancedLaunch:state.Problem==LaunchProblem.WorkingFolderMissing))RenderPlayerItems();}
        else if(choice=="reconnect")ReconnectFromDialog(p);
        else if(choice=="library"){SetPage("library");RenderLibrary();}
        else if(choice=="refresh")RefreshFromDialog(p);
    }
    async void LocationsFromDialog()=>await ManageSearchLocationsAsync();
    async void ReconnectFromDialog(Package p)=>await ReconnectGameAsync(p);
    async void RefreshFromDialog(Package p){launchFailures.Remove(p.Id);await RefreshWorkspaceAsync(source,false,false);RenderPlayerItems();}
    bool ConfirmConnection(Package p,string folder,bool identified)
    {
        var body=new StackPanel();body.Children.Add(ThemeText("기존 위치",11,"Text.Muted"));body.Children.Add(ThemeText(p.Source,12,"Text.Secondary"));var label=ThemeText("새 위치",11,"Text.Muted");label.Margin=new Thickness(0,16,0,0);body.Children.Add(label);body.Children.Add(ThemeText(folder,13));
        var dialog=new AppDialog(DialogOwner(),"폴더 다시 연결",PlayerRules.Title(p)+"\n"+(identified?"같은 게임 ID를 확인했습니다.":"별칭·태그와 실행 설정을 새 위치에 연결합니다."),[new("cancel","취소"),new("connect","연결",true)],"cancel",extra:body);
        return dialog.Open()=="connect";
    }
    async Task ReconnectGameAsync(Package p)
    {
        if(scanning||backupBusy||workspaceBlocked||workspaceLock==null||File.Exists(Edits().Pending))return;
        var initial=p.Source;while(!Directory.Exists(initial)&&Directory.GetParent(initial) is { } parent)initial=parent.FullName;
        var picker=new OpenFolderDialog {Title="게임의 현재 폴더 선택",InitialDirectory=initial};if(picker.ShowDialog(this)!=true)return;
        try
        {
            async Task<ScanSnapshot> ScanFolder()
            {scanning=true;IsEnabled=false;Say("폴더 확인 중…");try{return await Task.Run(()=>GameScanner.Scan(picker.FolderName));}finally{scanning=false;IsEnabled=true;}}
            var part=await ScanFolder();var plan=ReconnectPlanner.Prepare(CaptureWorkspace(),p.Id,picker.FolderName,part,libraryDocument,playerDocument);
            if(!ConfirmConnection(p,picker.FolderName,LibraryStore.ReadIdentity(picker.FolderName)!=null)){Say("연결을 취소했습니다");return;}
            var checkedPart=await ScanFolder();
            static string Fingerprint(ScanSnapshot scan)=>string.Join('\n',scan.Files.OrderBy(f=>f.Path).Select(f=>$"{f.Path}|{f.Length}|{f.ModifiedUtc.Ticks}"));
            if(Fingerprint(part)!=Fingerprint(checkedPart))throw new IOException("확인 중 폴더 내용이 변경됐습니다. 다시 연결하세요.");
            plan=ReconnectPlanner.Prepare(CaptureWorkspace(),p.Id,picker.FolderName,checkedPart,libraryDocument,playerDocument);
            plan.Packages.Single(q=>q.Id==p.Id).Size=FormatBytes(checkedPart.Files.Sum(f=>f.Length));
            var merged=new ScanSnapshot {Root=source};
            if(snapshot!=null)
            {merged.SearchRoots.AddRange(snapshot.SearchRoots);merged.InactivePaths.AddRange(snapshot.InactivePaths.Where(r=>!GameScanner.Within(picker.FolderName,r)));merged.OfflineRoots.AddRange(snapshot.OfflineRoots.Where(r=>!GameScanner.Within(picker.FolderName,r)));merged.Files.AddRange(snapshot.Files.Where(f=>!GameScanner.Within(f.Path,picker.FolderName)));merged.Directories.AddRange(snapshot.Directories.Where(d=>!GameScanner.Within(d,picker.FolderName)));merged.UnreadablePaths.AddRange(snapshot.UnreadablePaths.Where(d=>!GameScanner.Within(d,picker.FolderName)));merged.Issues.AddRange(snapshot.Issues);}
            merged.Files.AddRange(checkedPart.Files);merged.Directories.AddRange(checkedPart.Directories);
            if(!CommitPlayerEdit(plan.Packages,CopyCatalog(),Clone(playerDocument),[p.Id],prepared:plan,reconnectedScan:merged))return;
            if(page=="play")RenderPlayerItems();else if(page=="library")RenderLibrary();else Render();Say("폴더를 다시 연결했습니다");
        }
        catch(Exception ex) when(LibraryFault(ex)){ErrorInfo("폴더 연결","폴더를 연결하지 못했습니다.",ex.Message);Say("폴더를 연결하지 못했습니다");}
    }
}

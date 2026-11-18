using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace IndieRun;

public partial class ExecutablePicker:Window
{
    readonly Package package;
    readonly bool preview;
    readonly Func<string,bool> save;
    readonly CancellationTokenSource cancellation=new();
    readonly Dictionary<string,Image> icons=new(StringComparer.OrdinalIgnoreCase);
    bool syncing,closed;
    public ExecutableCandidate? Selected {get;private set;}
    public bool Confirmed {get;private set;}
    public bool AdvancedRequested {get;private set;}
    public ExecutablePicker(Window? owner,Package package,Func<string,bool> save,bool preview=false)
    {
        InitializeComponent();Owner=owner;this.package=package;this.save=save;this.preview=preview;
        if(owner==null)WindowStartupLocation=WindowStartupLocation.CenterScreen;
        MaxHeight=Math.Max(MinHeight,Math.Min(700,SystemParameters.WorkArea.Height-48));
        if(preview){Title="확인할 게임 실행";LaunchButton.Content="이 파일 실행";AdvancedButton.Visibility=Visibility.Collapsed;PickerHint.Text="확인용으로 실행합니다. 기본 실행 파일은 바꾸지 않습니다.";}
        GameTitle.Text=PlayerRules.Title(package);GameTitle.ToolTip=GameTitle.Text;
        Loaded+=async(_,_)=>await LoadCandidatesAsync();
        Closed+=(_,_)=>{closed=true;cancellation.Cancel();};
    }
    async Task LoadCandidatesAsync()
    {
        CandidateCount.Text="실행 파일을 찾고 있습니다…";BrowseButton.IsEnabled=false;
        try
        {
            var search=await Task.Run(()=>ExecutableCandidates.Find(package.Source,cancellation.Token),cancellation.Token);
            if(closed)return;
            search.Candidates.RemoveAll(c=>!package.Includes(Path.Combine(package.Source,c.RelativePath)));
            ShowCandidates(search);
            BrowseButton.IsEnabled=true;LoadingBar.Visibility=Visibility.Collapsed;
            var main=search.Candidates.Count(c=>!c.Auxiliary);
            Height=Math.Min(MaxHeight,Math.Max(MinHeight,310+Math.Min(main,4)*72+(main<search.Candidates.Count?30:0)));
            var initial=ExecutableCandidates.Default(search);
            if(initial!=null)Select(initial);
            if(search.Issues.Count>0){Status.Text="일부 위치를 확인하지 못했습니다. 필요하면 직접 선택하세요.";ShowDetails(string.Join('\n',search.Issues.Take(30)));}
            if(Selected!=null)MainCandidates.Focus();else BrowseButton.Focus();
            foreach(var candidate in search.Candidates)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var bitmap=await Task.Run(()=>ExecutableIcons.Read(Path.Combine(package.Source,candidate.RelativePath)),cancellation.Token);
                if(closed)return;
                if(bitmap!=null&&icons.TryGetValue(candidate.RelativePath,out var icon))icon.Source=bitmap;
            }
        }
        catch(OperationCanceledException){}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if(closed)return;
            CandidateCount.Text="실행 파일을 확인하지 못했습니다.";Status.Text="폴더 위치와 접근 권한을 확인하거나 직접 선택하세요.";ShowDetails(ex.Message);
            BrowseButton.IsEnabled=true;LoadingBar.Visibility=Visibility.Collapsed;
        }
    }
    void ShowCandidates(ExecutableSearch search)
    {
        MainCandidates.Items.Clear();AuxiliaryCandidates.Items.Clear();icons.Clear();
        foreach(var candidate in search.Candidates)AddCandidate(candidate);
        var main=search.Candidates.Count(c=>!c.Auxiliary);var auxiliary=search.Candidates.Count-main;
        CandidateCount.Text=main>0?$"실행 후보 {main}개":auxiliary>0?"게임 실행 후보가 없습니다. 직접 선택하거나 보조 목록을 확인하세요.":"실행 파일을 찾지 못했습니다. 직접 선택해 주세요.";
        AuxiliarySection.Visibility=auxiliary>0?Visibility.Visible:Visibility.Collapsed;AuxiliarySection.Header=$"보조 프로그램 추정 · {auxiliary}개";
        AuxiliarySection.ToolTip="설정·설치·오류 보고용 파일로 추정됩니다. 게임 실행 파일일 수도 있습니다.";
        LaunchButton.IsEnabled=false;SelectedFile.Text="선택한 파일 없음";
    }
    ListBoxItem AddCandidate(ExecutableCandidate candidate)
    {
        var content=new Grid();content.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(44)});content.ColumnDefinitions.Add(new ColumnDefinition());
        var image=new Image {Width=32,Height=32,VerticalAlignment=VerticalAlignment.Center};icons[candidate.RelativePath]=image;
        var iconBox=new Border {Width=34,Height=34,CornerRadius=new CornerRadius(6),Margin=new Thickness(0,0,10,0),Child=image};iconBox.SetResourceReference(Border.BackgroundProperty,"Surface.Primary");content.Children.Add(iconBox);
        var labels=new StackPanel {VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(labels,1);
        var name=new TextBlock {Text=candidate.Name,FontSize=15,FontWeight=FontWeights.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis,ToolTip=candidate.Name};name.SetResourceReference(ForegroundProperty,"Text.Primary");labels.Children.Add(name);
        var folder=new TextBlock {Text=candidate.Folder,FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis,ToolTip=candidate.RelativePath,Margin=new Thickness(0,4,0,0)};folder.SetResourceReference(ForegroundProperty,"Text.Muted");labels.Children.Add(folder);content.Children.Add(labels);
        var item=new ListBoxItem {Content=content,Tag=candidate,ToolTip=candidate.RelativePath};AutomationProperties.SetName(item,candidate.RelativePath+(candidate.Auxiliary?" · 보조 추정":""));
        (candidate.Auxiliary?AuxiliaryCandidates:MainCandidates).Items.Add(item);return item;
    }
    void Select(ExecutableCandidate candidate)
    {
        var list=candidate.Auxiliary?AuxiliaryCandidates:MainCandidates;
        var item=list.Items.Cast<ListBoxItem>().FirstOrDefault(i=>((ExecutableCandidate)i.Tag).RelativePath.Equals(candidate.RelativePath,StringComparison.OrdinalIgnoreCase))??AddCandidate(candidate);
        if(candidate.Auxiliary){AuxiliarySection.Visibility=Visibility.Visible;AuxiliarySection.IsExpanded=true;AuxiliarySection.Header=$"보조 프로그램 추정 · {AuxiliaryCandidates.Items.Count}개";}
        list.SelectedItem=item;list.ScrollIntoView(item);
    }
    void Candidate_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(syncing)return;
        var list=(ListBox)sender;
        if(list.SelectedItem is ListBoxItem item)
        {
            syncing=true;(list==MainCandidates?AuxiliaryCandidates:MainCandidates).SelectedItem=null;syncing=false;
            Selected=(ExecutableCandidate)item.Tag;SelectedFile.Text="선택: "+Selected.RelativePath;SelectedFile.ToolTip=Selected.RelativePath;
            Status.Text=Selected.Auxiliary?"보조 프로그램으로 추정되는 파일입니다.":"";LaunchButton.IsEnabled=true;
        }
        else if(MainCandidates.SelectedItem==null&&AuxiliaryCandidates.SelectedItem==null){Selected=null;LaunchButton.IsEnabled=false;SelectedFile.Text="선택한 파일 없음";}
    }
    void Browse_Click(object sender,RoutedEventArgs e)
    {
        var picker=new OpenFileDialog {Title="이 게임 폴더 안의 실행 파일 선택",Filter="실행 파일 (*.exe)|*.exe",InitialDirectory=package.Source};
        if(picker.ShowDialog(this)!=true)return;
        try
        {
            var candidate=ExecutableCandidates.ValidateSelection(package.Source,picker.FileName);if(!package.Includes(picker.FileName))throw new IOException("본편에 포함된 폴더의 실행 파일을 선택하세요.");Select(candidate);
            var bitmap=ExecutableIcons.Read(picker.FileName);if(bitmap!=null)icons[candidate.RelativePath].Source=bitmap;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException){Status.Text="이 게임 폴더 안의 실행 파일을 선택하세요.";ShowDetails(ex.Message);}
    }
    void Confirm_Click(object sender,RoutedEventArgs e)
    {
        if(Selected==null)return;
        try
        {
            ExecutableCandidates.ValidateSelection(package.Source,Path.Combine(package.Source,Selected.RelativePath));
            LaunchButton.IsEnabled=false;
            if(!save(Selected.RelativePath)){Status.Text=preview?"실행하지 못했습니다. 파일과 권한을 확인하세요.":"설정을 저장하지 못했습니다. 저장 상태를 확인한 뒤 다시 시도하세요.";return;}
            Confirmed=true;Close();
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException)
        {Status.Text=ex is GameLaunchException?ex.Message:"파일을 확인하지 못했습니다. 위치와 접근 권한을 확인하세요.";ShowDetails(ex.Message);}
        finally{if(!closed)LaunchButton.IsEnabled=Selected!=null;}
    }
    void ShowDetails(string text){DetailText.Text=text;Details.Visibility=Visibility.Visible;}
    void Advanced_Click(object sender,RoutedEventArgs e){AdvancedRequested=true;Close();}
    void Cancel_Click(object sender,RoutedEventArgs e)=>Close();
}

internal static class ExecutableIcons
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct FileInfo
    {public nint Icon;public int IconIndex;public uint Attributes;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string DisplayName;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=80)]public string TypeName;}
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,EntryPoint="SHGetFileInfoW")] static extern nint GetFileInfo(string path,uint attributes,ref FileInfo info,uint size,uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(nint icon);
    public static BitmapSource? Read(string path)
    {
        var info=new FileInfo();
        try
        {
            BackupPaths.NoLinks(path);if(!File.Exists(path)||GetFileInfo(path,0,ref info,(uint)Marshal.SizeOf<FileInfo>(),0x101)==0||info.Icon==0)return null;
            var bitmap=Imaging.CreateBitmapSourceFromHIcon(info.Icon,Int32Rect.Empty,BitmapSizeOptions.FromWidthAndHeight(32,32));bitmap.Freeze();return bitmap;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or ExternalException or InvalidOperationException){return null;}
        finally{if(info.Icon!=0)DestroyIcon(info.Icon);}
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace IndieRun;

public enum DialogTone { Information, Warning, Destructive }
public sealed record DialogChoice(string Id,string Label,bool Primary=false,bool Dangerous=false);

// No action runs here: callers act only on the returned choice. X and Escape select CancelId.
public sealed class AppDialog:Window
{
    public string Result {get;private set;}
    public Grid Layout {get;}=new();
    public ScrollViewer BodyScroll {get;}=new(){VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    public Panel ActionPanel {get;}
    public IReadOnlyList<Button> ActionButtons {get;}
    static TextBlock Label(string text,double size,string role)
    {var label=new TextBlock {Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap};label.SetResourceReference(TextBlock.ForegroundProperty,role);return label;}
    public AppDialog(Window? owner,string title,string message,IReadOnlyList<DialogChoice> choices,string cancelId,string? details=null,DialogTone tone=DialogTone.Information,string? defaultId=null,FrameworkElement? extra=null,bool stacked=false,string? subject=null)
    {
        if(choices.Count==0||choices.Select(c=>c.Id).Distinct().Count()!=choices.Count||!choices.Any(c=>c.Id==cancelId)||defaultId!=null&&!choices.Any(c=>c.Id==defaultId))throw new ArgumentException("확인창 선택 구성이 올바르지 않습니다.");
        Style=(Style)Application.Current.FindResource(typeof(Window));
        Result=cancelId;Title=title;Owner=owner;ShowInTaskbar=false;WindowStartupLocation=owner==null?WindowStartupLocation.CenterScreen:WindowStartupLocation.CenterOwner;
        Width=600;MinWidth=440;MinHeight=230;MaxHeight=Math.Max(230,Math.Min(720,SystemParameters.WorkArea.Height-64));SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.CanResize;UseLayoutRounding=true;
        SetResourceReference(BackgroundProperty,"Surface.Primary");SetResourceReference(ForegroundProperty,"Text.Primary");
        KeyboardNavigation.SetTabNavigation(Layout,KeyboardNavigationMode.Cycle);
        Layout.Margin=new Thickness(28);Layout.RowDefinitions.Add(new RowDefinition());Layout.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});Content=Layout;
        var body=new StackPanel();BodyScroll.Content=body;BodyScroll.MaxHeight=MaxHeight-160;Layout.Children.Add(BodyScroll);
        var heading=new Grid {Margin=new Thickness(0,0,0,16)};heading.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(44)});heading.ColumnDefinitions.Add(new ColumnDefinition());
        var icon=new Border {Width=30,Height=30,CornerRadius=new CornerRadius(7),VerticalAlignment=VerticalAlignment.Top,HorizontalAlignment=HorizontalAlignment.Left,Child=Label(tone==DialogTone.Information?"i":"!",18,tone==DialogTone.Destructive?"Danger.Text":tone==DialogTone.Warning?"Warning.Text":"Accent.Text")};
        icon.SetResourceReference(Border.BackgroundProperty,tone==DialogTone.Destructive?"Danger.Surface":tone==DialogTone.Warning?"Warning.Surface":"Accent.Surface");((TextBlock)icon.Child).HorizontalAlignment=HorizontalAlignment.Center;((TextBlock)icon.Child).VerticalAlignment=VerticalAlignment.Center;AutomationProperties.SetName(icon,tone==DialogTone.Information?"안내":"주의");heading.Children.Add(icon);
        var caption=Label(title,21,"Text.Primary");caption.FontWeight=FontWeights.SemiBold;caption.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(caption,1);heading.Children.Add(caption);body.Children.Add(heading);
        if(!string.IsNullOrWhiteSpace(subject)){var context=Label(subject,12,"Text.Muted");context.Margin=new Thickness(0,0,0,12);body.Children.Add(context);}
        if(message.Length>0)body.Children.Add(Label(message,14,"Text.Secondary"));
        if(extra!=null){extra.Margin=new Thickness(0,16,0,0);body.Children.Add(extra);}
        if(!string.IsNullOrWhiteSpace(details))
        {
            var text=new TextBox {Text=details,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,BorderThickness=new Thickness(0),Padding=new Thickness(0,8,0,0),Background=Brushes.Transparent};text.SetResourceReference(ForegroundProperty,"Text.Muted");AutomationProperties.SetName(text,"상세 정보");
            var expander=new Expander {Header="자세히",Content=text,Margin=new Thickness(0,18,0,0)};expander.SetResourceReference(ForegroundProperty,"Text.Muted");body.Children.Add(expander);
        }
        ActionPanel=stacked?new StackPanel():new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right};ActionPanel.Margin=new Thickness(0,24,0,0);Grid.SetRow(ActionPanel,1);Layout.Children.Add(ActionPanel);
        var buttons=new List<Button>();
        foreach(var choice in choices)
        {
            var button=new Button {Content=new TextBlock {Text=choice.Label,TextWrapping=TextWrapping.Wrap,TextAlignment=TextAlignment.Center},MinHeight=38,Padding=new Thickness(14,9,14,9),Margin=stacked?new Thickness(0,0,0,8):new Thickness(8,4,0,0),IsDefault=choice.Id==(defaultId??cancelId),IsCancel=choice.Id==cancelId};
            if(choice.Primary)button.Style=(Style)Application.Current.FindResource("Primary");
            if(choice.Dangerous){button.SetResourceReference(BackgroundProperty,"Danger.Fill");button.SetResourceReference(BorderBrushProperty,"Danger.Fill");button.SetResourceReference(ForegroundProperty,"Accent.OnFill");}
            AutomationProperties.SetName(button,choice.Label);button.Click+=(_,_)=>{Result=choice.Id;Close();};buttons.Add(button);ActionPanel.Children.Add(button);
        }
        ActionButtons=buttons;ContentRendered+=(_,_)=>{buttons.First(b=>b.IsDefault).Focus();QueueScreenFit();};
        SourceInitialized+=(_,_)=>FitMonitor();
        SizeChanged+=(_,_)=>QueueScreenFit();
        // Reserve the footer's real height even when several labels wrap or the text scale changes.
        ActionPanel.SizeChanged+=(_,_)=>{BodyScroll.MaxHeight=Math.Max(70,MaxHeight-Layout.Margin.Top-Layout.Margin.Bottom-ActionPanel.ActualHeight-40);MinHeight=Math.Min(MaxHeight,Math.Max(230,ActionPanel.ActualHeight+170));};

    }
    public string Open(){ShowDialog();return Result;}
    void QueueScreenFit()=>Dispatcher.BeginInvoke(new Action(()=>{if(!IsVisible)return;FitMonitor();KeepOnScreen();}),System.Windows.Threading.DispatcherPriority.ContextIdle);
    void FitMonitor()
    {
        if(!TryWorkArea(out var area))return;var dpi=VisualTreeHelper.GetDpi(this);
        MaxHeight=Math.Max(MinHeight,Math.Min(720,(area.Bottom-area.Top-32)/dpi.DpiScaleY));MaxWidth=Math.Max(MinWidth,(area.Right-area.Left-32)/dpi.DpiScaleX);
        BodyScroll.MaxHeight=Math.Max(70,MaxHeight-Layout.Margin.Top-Layout.Margin.Bottom-ActionPanel.ActualHeight-40);
    }
    // Keep expanding content and the fixed action area inside this monitor's work area.
    [StructLayout(LayoutKind.Sequential)] struct NativeRect {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo {public int Size;public NativeRect Monitor,Work;public uint Flags;}
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint window,uint flags);
    [DllImport("user32.dll",EntryPoint="GetMonitorInfoW")] static extern bool GetMonitorInfo(nint monitor,ref MonitorInfo info);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint window,out NativeRect rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    bool TryWorkArea(out NativeRect area)
    {var info=new MonitorInfo {Size=Marshal.SizeOf<MonitorInfo>()};var handle=new WindowInteropHelper(this).Handle;var ok=handle!=0&&GetMonitorInfo(MonitorFromWindow(handle,2),ref info);area=info.Work;return ok;}
    void KeepOnScreen()
    {
        if(!IsLoaded||!TryWorkArea(out var area))return;var handle=new WindowInteropHelper(this).Handle;if(!GetWindowRect(handle,out var rect))return;
        var x=Math.Clamp(rect.Left,area.Left+8,Math.Max(area.Left+8,area.Right-(rect.Right-rect.Left)-8));var y=Math.Clamp(rect.Top,area.Top+8,Math.Max(area.Top+8,area.Bottom-(rect.Bottom-rect.Top)-8));
        if(x!=rect.Left||y!=rect.Top)SetWindowPos(handle,0,x,y,0,0,0x0015);
    }
}

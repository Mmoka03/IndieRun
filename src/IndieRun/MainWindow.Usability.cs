using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
namespace IndieRun;
public partial class MainWindow
{
    static FrameworkElement CompactPath(string path)
    {
        var panel=new StackPanel{Margin=new Thickness(0,7,0,0)};var detail=WorkDetails(path,"위치 · "+(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is {Length:>0} leaf?leaf:path));
        var content=new StackPanel();var original=(UIElement)detail.Content;detail.Content=null;content.Children.Add(original);var copy=Btn("경로 복사",()=>{try{Clipboard.SetText(path);}catch(System.Runtime.InteropServices.COMException){}});copy.HorizontalAlignment=HorizontalAlignment.Left;copy.Margin=new Thickness(0,8,0,0);content.Children.Add(copy);detail.Content=content;detail.Margin=new Thickness(0);detail.ToolTip=path;panel.Children.Add(detail);return panel;
    }
    static void PrepareModal(Window win)
    {
        KeyboardNavigation.SetTabNavigation(win,KeyboardNavigationMode.Cycle);
        win.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape&&!win.OwnedWindows.Cast<Window>().Any(w=>w.IsVisible)&&!(Keyboard.FocusedElement is ComboBox c&&c.IsDropDownOpen)){e.Handled=true;win.Close();}};
        win.ContentRendered+=(_,_)=>win.Dispatcher.BeginInvoke(new Action(()=>
        {
            if(!win.IsVisible)return;var nodes=UiNodes(win.Content as DependencyObject).OfType<Control>().Where(c=>c.IsVisible&&c.IsEnabled).ToList();
            var edit=nodes.FirstOrDefault(c=>c is TextBox t&&!t.IsReadOnly)??nodes.FirstOrDefault(c=>c is ComboBox)??nodes.FirstOrDefault(c=>c is Button b&&!b.IsCancel);
            edit?.Focus();
        }),System.Windows.Threading.DispatcherPriority.ContextIdle);
    }
    static IEnumerable<DependencyObject> UiNodes(DependencyObject? root)
    {if(root==null)yield break;yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in UiNodes(VisualTreeHelper.GetChild(root,i)))yield return child;}
}



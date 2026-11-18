using System.Windows;
namespace IndieRun;

public partial class MainWindow
{
    Window DialogOwner()=>OwnedWindows.Cast<Window>().Where(w=>w.IsVisible).Select(w=>w.OwnedWindows.Cast<Window>().LastOrDefault(c=>c.IsVisible)??w).LastOrDefault()??this;
    string Choose(string title,string message,DialogChoice[] choices,string cancelId,string? details=null,DialogTone tone=DialogTone.Information,Window? owner=null,string? defaultId=null,bool stacked=false,string? subject=null)
        =>new AppDialog(owner??DialogOwner(),title,message,choices,cancelId,details,tone,defaultId,stacked:stacked,subject:subject).Open();
    bool Confirm(string title,string message,string action,string? details=null,Window? owner=null,bool destructive=false)
        =>Choose(title,message,[new("cancel","취소"),new("confirm",action,true,destructive)],"cancel",details,destructive?DialogTone.Destructive:DialogTone.Warning,owner)=="confirm";
    void Info(string title,string value)
        =>Choose(title,value,[new("close","확인",true)],"close");
    void ErrorInfo(string title,string message,string detail)
        =>Choose(title,message,[new("close","확인",true)],"close",detail,DialogTone.Warning);
}

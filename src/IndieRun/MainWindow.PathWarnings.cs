using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace IndieRun;
public partial class MainWindow
{
    // Call with user-selected container paths, never append a game's own folder name.
    static string ContainerPathWarning(string path)
    {
        if(string.IsNullOrWhiteSpace(path))return "";
        try{return Path.GetFullPath(path).Any(c=>c>127)?"경로에 한글·일본어 등이 포함되어 일부 게임이 실행되지 않을 수 있습니다.":"";}
        catch(Exception ex)when(ex is ArgumentException or NotSupportedException or IOException){return "";}
    }
    static TextBlock ContainerPathNote(string path)
    {
        var text=ContainerPathWarning(path);var note=WorkText(text,12,"Warning.Text");note.Margin=new Thickness(0,8,0,12);note.Visibility=text.Length==0?Visibility.Collapsed:Visibility.Visible;return note;
    }
}

using System.IO;
namespace IndieRun;
public partial class MainWindow
{
    string[] ProtectedGameRoots()=>HistoryRoots().Concat(new[]{AppContext.BaseDirectory,DataRoot,Path.Combine(projectRoot,"backups"),Path.Combine(projectRoot,"src")}).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    void RequireUserGameFolder(string folder)
    {
        FolderPolicy.RequireEditable(folder);
        if(ProtectedGameRoots().Any(root=>GameScanner.Within(folder,root)||GameScanner.Within(root,folder)))throw new IOException("프로그램이나 백업 보관 위치는 게임 폴더로 사용할 수 없습니다.");
    }
}

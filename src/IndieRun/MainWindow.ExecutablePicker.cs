using System.IO;

namespace IndieRun;

public partial class MainWindow
{
    bool ChooseFirstExecutable(Package package)
    {
        if(File.Exists(Edits().Pending)){Info("실행 설정 저장","이전 편집 저장을 먼저 복구하세요.");return false;}
        var picker=new ExecutablePicker(this,package,relative=>SaveFirstExecutable(package.Id,relative));
        picker.ShowDialog();
        if(picker.AdvancedRequested)return EditGames([packages.Find(p=>p.Id==package.Id)??package],firstLaunch:true,launchOnly:true);
        return picker.Confirmed;
    }
    bool SaveFirstExecutable(string id,string relative)
    {
        if(File.Exists(Edits().Pending))throw new IOException("실행 화면에서 이전 편집 저장을 먼저 복구하세요.");
        var package=packages.Single(p=>p.Id==id);var next=Clone(playerDocument);var game=next.Games.Single(g=>g.Id==id);game.Executable=relative;
        var state=GameLaunch.Inspect(package,game,libraryDocument.Games.Any(g=>g.Id==id));
        if(state.NeedsAttention)throw new GameLaunchException(state.Problem,state.Message);
        PlayerStore.StartInfo(package,game);
        return CommitPlayerEdit(Clone(packages),CopyCatalog(),next,[id]);
    }
}

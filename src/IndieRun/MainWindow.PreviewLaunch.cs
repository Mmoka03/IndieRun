using System.Diagnostics;
using System.IO;
using System.Windows;
namespace IndieRun;
public partial class MainWindow
{
    bool previewStarting;
    async Task PreviewGameAsync(Package package,Window owner)
    {
        if(previewStarting||running||backupBusy||scanning||workspaceBlocked||RenamePending||File.Exists(Edits().Pending))return;
        var consent=Path.Combine(DataRoot,"preview-launch-consent.json");bool accepted=false;
        try{if(File.Exists(consent))accepted=BackupJson.Load<bool>(consent,_=>{});}catch(Exception ex)when(LibraryFault(ex)){}
        if(!accepted)
        {
            if(!Confirm("확인용 게임 실행","실제 게임이 실행되며 세이브나 설정 파일이 만들어질 수 있습니다. 기본 실행 파일은 바꾸지 않습니다.","계속",owner:owner))return;
            try{BackupJson.Save(consent,true);}catch(Exception ex)when(LibraryFault(ex)){ErrorInfo("실행 안내 저장","안내 확인을 저장하지 못했습니다.",ex.Message);return;}
        }
        previewStarting=true;
        try
        {
            var search=await Task.Run(()=>ExecutableCandidates.Find(package.Source));search.Candidates.RemoveAll(c=>!package.Includes(Path.Combine(package.Source,c.RelativePath)));var stored=playerDocument.Games.Find(g=>g.Id==package.Id);
            bool Run(string relative)
            {
                try{var info=PreviewLaunch.StartInfo(package,stored,relative,libraryDocument.Games.Any(g=>g.Id==package.Id));using var process=Process.Start(info)??throw new IOException("게임을 시작하지 못했습니다.");Say("확인용으로 실행했습니다 · 기본 실행 파일은 유지됩니다");return true;}
                catch(System.ComponentModel.Win32Exception ex){var state=GameLaunch.Classify(ex);throw new GameLaunchException(state.Problem,state.Message+"\n"+ex.Message);}
            }
            if(stored?.Executable.Length>0){Run(stored.Executable);return;}
            var only=search.Issues.Count==0?ExecutableCandidates.Default(search):null;if(only!=null){Run(only.RelativePath);return;}
            new ExecutablePicker(owner,package,Run,preview:true).ShowDialog();
        }
        catch(Exception ex)when(LibraryFault(ex)){ErrorInfo("확인용 실행","게임을 실행하지 못했습니다. 위치와 실행 후보를 확인하세요.",ex.Message);}
        finally{previewStarting=false;}
    }
}

using System.Windows;
namespace IndieRun;
public partial class MainWindow
{
    void ReviewAddedExecutables(Package p)
    {
        var choice=Choose("실행 파일 추가 확인","추가된 실행 파일도 현재 게임 범위에 포함됩니다. 별개의 게임이라면 구성 수정에서 나눌 수 있습니다.",
            [new("later","나중에"),new("compose","구성 수정"),new("accept","현재 범위로 확인",true)],"later",
            string.Join('\n',p.ReviewDetails.Take(300)),DialogTone.Warning,subject:PlayerRules.Title(p));
        if(choice=="accept")AcceptCurrentCheckpoint(p);else if(choice=="compose")EditComposition(p);
    }
    void ShowTransferIdentityIssue(Package p)
    {
        var message=p.ReviewReason.StartsWith("같은 ID",StringComparison.Ordinal)?"같은 ID가 여러 폴더에서 확인됐습니다. 보관함에서 복사본을 구분하세요.":"게임 식별 정보나 폴더 연결을 확인해야 합니다. 보관함에서 위치와 상태를 확인하세요.";
        if(Choose("게임 ID 확인",message,[new("later","나중에"),new("library","보관함에서 확인",true)],"later",
            p.ReviewReason+"\n\n검색한 폴더와 이미 알려진 위치를 확인했습니다. 검색하지 않은 다른 위치의 복사본까지 확인한 것은 아닙니다.",DialogTone.Warning,subject:PlayerRules.Title(p))=="library")
        {SetPage("library");RenderLibrary();}
    }
}

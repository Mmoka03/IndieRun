using System.IO;
using System.Text.Json;
namespace IndieRun;
public sealed record WorkspaceSaveIssue(string Code,string Message)
{
    public static WorkspaceSaveIssue From(Exception ex)
    {
        if(ex is UnauthorizedAccessException)return new("SAVE-ACCESS","설정 폴더에 쓸 권한이 없습니다.");
        if(ex is InvalidDataException)
        {
            if(ex.Message.Contains("확인 기준"))return new("SAVE-CHECKPOINT","게임 위치와 폴더 확인 기록이 맞지 않습니다.");
            if(ex.Message.Contains("너무 큽니다"))return new("SAVE-SIZE","설정 파일이 저장 가능한 크기를 넘었습니다.");
            if(ex.Message.Contains("중복"))return new("SAVE-DUPLICATE","설정에 중복된 식별 정보가 있습니다.");
            if(ex.Message.Contains("경로"))return new("SAVE-PATH","저장할 게임 경로 정보를 확인해야 합니다.");
            return new("SAVE-DATA","저장할 설정 정보가 올바르지 않습니다.");
        }
        if(ex is JsonException)return new("SAVE-FORMAT","설정 파일의 형식을 확인해야 합니다.");
        if(ex is IOException)
            return (ex.HResult&65535) switch{32 or 33=>new("SAVE-LOCKED","다른 프로그램이 설정 파일을 사용하고 있습니다."),112 or 39=>new("SAVE-SPACE","설정 드라이브의 여유 공간이 부족합니다."),5=>new("SAVE-ACCESS","설정 폴더에 접근할 수 없습니다."),_=>new("SAVE-IO","설정 파일을 저장하지 못했습니다.")};
        return new("SAVE-FAILED","설정을 저장하지 못했습니다.");
    }
}

using System.IO;
namespace IndieRun;
public sealed record MoveLocationEvidence(bool MatchesRecord,bool RetainedSource,string? KnownFolder,string Message);
// A historical journal is evidence of an action, not proof of the game's current location.
public static class MoveEvidence
{
    enum Presence {Absent,Owned,Other,Uncertain}
    static Presence Inspect(string folder,BackupJob backup)
    {
        if(!Directory.Exists(folder))return Presence.Absent;
        BackupPaths.NoLinks(folder);var marker=LibraryStore.ReadIdentity(folder);
        if(marker!=null)return marker.GameId.Equals(backup.Package.Id,StringComparison.OrdinalIgnoreCase)?Presence.Owned:Presence.Other;
        var executables=backup.Files.Where(f=>Path.GetExtension(f.RelativePath).Equals(".exe",StringComparison.OrdinalIgnoreCase)).Take(8).ToList();
        var probes=executables.Count>0?executables:backup.Files.Where(f=>!f.RelativePath.Equals(LibraryStore.MarkerName,StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
        if(!probes.Any(f=>File.Exists(BackupPaths.Child(folder,f.RelativePath))))return Presence.Absent;
        return backup.Files.Any(f=>f.RelativePath.Equals(LibraryStore.MarkerName,StringComparison.OrdinalIgnoreCase))?Presence.Uncertain:Presence.Owned;
    }
    public static MoveLocationEvidence Inspect(BackupJob backup,MoveJob move)
    {
        try
        {
            if(move.WithoutBackup!=backup.WithoutBackup||move.BackupId!=backup.Id||!Path.TrimEndingDirectorySeparator(Path.GetFullPath(move.Source)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(backup.SourceRoot)),StringComparison.OrdinalIgnoreCase))return new(false,false,null,"이동 기록과 백업의 원본 위치가 다릅니다.");
            if(move.Status=="Completed"&&CompositionPlan.AnchorProblem(backup.Package)!=null)return new(false,false,null,"이동 계획에서 본편 실행 폴더가 제외되어 있습니다. 원래 위치로 복구한 뒤 게임 구성을 확인하세요.");
            var source=Inspect(move.Source,backup);var target=Inspect(move.Destination,backup);
            var expected=move.Status=="Completed"?target:source;var opposite=move.Status=="Completed"?source:target;
            var matches=move.Status is "Completed" or "RolledBack"&&expected==Presence.Owned&&opposite is not (Presence.Owned or Presence.Uncertain);
            var retained=matches&&move.Status=="Completed"&&backup.Package.OmittedFolders.Count>0&&Directory.Exists(move.Source);
            string? known=source==Presence.Owned&&target!=Presence.Owned?move.Source:target==Presence.Owned&&source!=Presence.Owned?move.Destination:null;
            return new(matches,retained,known,matches?retained?"이동에서 제외한 자료는 원래 위치에 남아 있습니다.":"":"이동 기록과 실제 위치가 다릅니다. 폴더를 다시 연결해 주세요.");
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {return new(false,false,null,"이동 결과의 폴더 위치와 식별 정보를 확인하지 못했습니다.");}
    }
}

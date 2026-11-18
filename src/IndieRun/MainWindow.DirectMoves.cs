using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace IndieRun;
public partial class MainWindow
{
    internal Func<BackupEngine>? directEngineFactory=null;
    BackupEngine DirectMoveService()=>directEngineFactory?.Invoke()??new(Path.Combine(DataRoot,"move-records"));
    BackupEngine MoveService(MoveJob move)=>workHistory.Records.Find(r=>r.Kind=="Move"&&r.OperationId==move.Id&&r.JobId==move.BackupId) is {} record?new(record.Root):move.WithoutBackup?DirectMoveService():BackupService();
    List<MoveJob> AllMoves(bool recover)
    {
        var regular=HistoryRoots().Where(root=>!root.Equals(DirectMoveService().Root,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(root)).SelectMany(root=>new BackupEngine(root).ListMoves(recover)).ToList();var direct=DirectMoveService().ListMoves(recover);
        foreach(var move in direct.Where(m=>m.Status=="RecordError"))move.WithoutBackup=true;
        return regular.Concat(direct).OrderByDescending(m=>m.UpdatedUtc).ToList();
    }
    IEnumerable<string> AllMoveWorkingPaths()=>HistoryRoots().Where(Directory.Exists).SelectMany(root=>new BackupEngine(root).MoveWorkingPaths());
}

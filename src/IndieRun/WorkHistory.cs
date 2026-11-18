using System.IO;
using System.Collections.Concurrent;
using System.Text.Json;
namespace IndieRun;

public sealed class WorkRecord
{
    public string Root {get;set;}="";
    public string Kind {get;set;}="Backup";
    public string JobId {get;set;}="";
    public string OperationId {get;set;}="";
    public string GameId {get;set;}="";
    public string Title {get;set;}="";
    public string State {get;set;}="";
    public string Action {get;set;}="Move";
    public string Source {get;set;}="";
    public string Destination {get;set;}="";
    public string Availability {get;set;}="Checking";
    public string? Error {get;set;}
    public bool WithoutBackup {get;set;}
    public bool BackupRemoved {get;set;}
    public bool LocationIssue {get;set;}
    public bool Verified {get;set;}
    public bool PartialCoverage {get;set;}
    public long Bytes {get;set;}
    public DateTime UpdatedUtc {get;set;}
    public string Key=>Root.ToUpperInvariant()+"|"+Kind+"|"+JobId+"|"+OperationId;
    public bool Attention=>State!="Deleted"&&(PartialCoverage||LocationIssue||Kind=="Preparation"||Availability!="Connected"||State is not ("Completed" or "RolledBack"));
}
public sealed class WorkHistoryDocument
{
    public int Version {get;set;}=1;
    public List<string> Roots {get;set;}=[];
    public List<WorkRecord> Records {get;set;}=[];
}
// The index contains summaries only. Manifests and payloads remain authoritative for actions.
public sealed class WorkHistoryStore(string file)
{
    public string FilePath {get;}=Path.GetFullPath(file);
    static readonly ConcurrentDictionary<string,object> Gates=new(StringComparer.OrdinalIgnoreCase);
    object Gate=>Gates.GetOrAdd(FilePath,_=>new object());
    internal Action<string>? InspectingRoot {get;set;}
    static bool Expected(Exception e)=>e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException;
    static void Validate(WorkHistoryDocument doc)
    {
        if(doc.Version!=1||doc.Roots==null||doc.Records==null||doc.Roots.Any(r=>r==null||!Path.IsPathFullyQualified(r))||doc.Records.Any(r=>r==null||!Path.IsPathFullyQualified(r.Root)||r.Title==null||r.State==null||r.GameId==null||r.JobId==null||r.OperationId==null||r.Source==null||r.Destination==null||r.Availability==null||r.Kind is not ("Backup" or "Move" or "Restore" or "Preparation")))throw new InvalidDataException("작업 기록 색인을 확인할 수 없습니다. 원본 기록은 보존했습니다.");
        if(doc.Records.GroupBy(r=>r.Key,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))throw new InvalidDataException("중복 작업 기록입니다.");
    }
    static string NormalizeRoot(string path)=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public WorkHistoryDocument Load()
    {
        lock(Gate){var doc=File.Exists(FilePath)||File.Exists(FilePath+".bak")?BackupJson.Load<WorkHistoryDocument>(FilePath,Validate):new();doc.Roots=doc.Roots.Select(NormalizeRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();foreach(var r in doc.Records)r.Root=NormalizeRoot(r.Root);doc.Records=doc.Records.GroupBy(r=>r.Key,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToList();return doc;}
    }
    public void Save(WorkHistoryDocument doc){lock(Gate)BackupJson.Save(FilePath,doc,Validate);}
    public WorkHistoryDocument Register(IEnumerable<string> roots)
    {
        lock(Gate)
        {
        var doc=Load();doc.Roots=doc.Roots.Concat(roots.Select(NormalizeRoot)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();Save(doc);return doc;
            }
    }
    public WorkHistoryDocument Refresh(IEnumerable<string> roots,Action<WorkHistoryDocument>? inspect=null)
    {
        lock(Gate)
        {
        var doc=Load();doc.Roots=doc.Roots.Concat(roots.Select(NormalizeRoot)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach(var root in doc.Roots)
        {
            var old=doc.Records.Where(r=>r.Root.Equals(root,StringComparison.OrdinalIgnoreCase)).ToList();
            try
            {
                InspectingRoot?.Invoke(root);
                if(!Directory.Exists(root)){foreach(var r in old)r.Availability=r.State=="Deleted"?"Connected":"Offline";continue;}
                // Previous backup locations can contain crash-era Running records too.
                // Recovery only changes those records when the engine acquires its operation
                // lock; a live operation keeps the lock and remains untouched.
                var engine=new BackupEngine(root);var jobs=engine.ListJobs();var moves=engine.ListMoves();var fresh=new List<WorkRecord>();
                foreach(var job in jobs)
                {
                    WorkRecord Record(string kind,string id,string state,string destination,string? error)=>new(){Root=root,Kind=kind,JobId=job.Id,OperationId=id,GameId=job.Package.Id,Title=job.Package.Alias.Length>0?job.Package.Alias:job.OriginalFolder,State=state,Source=job.SourceRoot,Destination=destination,WithoutBackup=job.WithoutBackup,BackupRemoved=BackupEngine.Removed(job),Verified=job.SourceVerified,PartialCoverage=job.LegacyIncomplete&&(kind!="Move"||state!="RolledBack"),Bytes=job.TotalBytes,UpdatedUtc=job.UpdatedUtc,Availability="Connected",Error=error};
                    if(!job.WithoutBackup)fresh.Add(Record("Backup","",job.Status,Path.Combine(root,job.Id),job.Error));
                    else if(!moves.Any(m=>m.BackupId==job.Id))fresh.Add(Record("Preparation","",job.Status,job.SourceRoot,job.Error));
                    foreach(var move in moves.Where(m=>m.BackupId==job.Id))
                    {var r=Record("Move",move.Id,move.Status,move.Destination,move.Error);r.Action=move.Action;r.Source=move.Source;r.WithoutBackup=move.WithoutBackup;r.UpdatedUtc=move.UpdatedUtc;fresh.Add(r);}
                    if(job.Status!="RecordError")foreach(var restore in engine.ListRestores(job.Id))
                    {var r=Record("Restore",restore.Id,restore.Status,restore.TargetContainer,restore.Error);r.UpdatedUtc=restore.UpdatedUtc;fresh.Add(r);}
                }
                foreach(var move in moves.Where(m=>!jobs.Any(j=>j.Id==m.BackupId)))fresh.Add(new(){Root=root,Kind="Move",JobId=move.BackupId,OperationId=move.Id,State="RecordError",Title="이동 기록",Source=move.Source,Destination=move.Destination,Availability="MissingRecord",Error=move.Error,UpdatedUtc=move.UpdatedUtc});
                foreach(var r in fresh.Where(r=>r.State=="RecordError")){var last=old.Find(o=>o.Kind==r.Kind&&o.JobId==r.JobId);if(last!=null){r.Title=last.Title;r.GameId=last.GameId;r.Source=last.Source;r.Destination=last.Destination;r.Bytes=last.Bytes;}}
                var keys=fresh.Select(r=>r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach(var r in old.Where(r=>r.JobId!="root-error"&&!keys.Contains(r.Key))){r.Availability="MissingRecord";fresh.Add(r);}
                doc.Records.RemoveAll(r=>r.Root.Equals(root,StringComparison.OrdinalIgnoreCase));doc.Records.AddRange(fresh);
            }
            catch(Exception e)when(Expected(e))
            {
                foreach(var r in old){r.Availability="Unavailable";r.Error=e.Message;}
                if(old.Count==0)doc.Records.Add(new(){Root=root,Kind="Preparation",JobId="root-error",Title="작업 기록",State="RecordError",Availability="Unavailable",Error=e.Message,UpdatedUtc=DateTime.UtcNow});
            }
        }
        doc.Records=doc.Records.OrderByDescending(r=>r.UpdatedUtc).ToList();inspect?.Invoke(doc);Save(doc);return doc;
            }
    }
    public WorkHistoryDocument Reconnect(WorkRecord selected,string newRoot)
    {
        lock(Gate)
        {
        newRoot=NormalizeRoot(newRoot);var engine=new BackupEngine(newRoot);var job=engine.Load(selected.JobId);
        if(job.Package.Id!=selected.GameId)throw new IOException("같은 작업 기록을 찾지 못했습니다. 폴더를 다시 선택하세요.");
        if(selected.Kind=="Move"&&!engine.ListMoves(false).Any(m=>m.Id==selected.OperationId&&m.BackupId==selected.JobId))throw new IOException("이동 기록이 일치하지 않습니다.");
        var doc=Load();var verified=new HashSet<string>();
        foreach(var group in doc.Records.Where(r=>r.Root.Equals(selected.Root,StringComparison.OrdinalIgnoreCase)).GroupBy(r=>r.JobId))
        {
            try{var match=engine.Load(group.Key);if(group.All(r=>r.GameId==match.Package.Id))verified.Add(group.Key);}catch(Exception e)when(Expected(e)){}
        }
        foreach(var r in doc.Records.Where(r=>r.Root.Equals(selected.Root,StringComparison.OrdinalIgnoreCase)&&verified.Contains(r.JobId))){r.Root=newRoot;r.Availability="Checking";}
        doc.Records=doc.Records.GroupBy(r=>r.Key,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToList();doc.Roots=doc.Roots.Append(newRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();Save(doc);return Refresh([]);
            }
    }
}

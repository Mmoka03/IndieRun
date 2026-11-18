using System.IO;
using System.Text.Json;
namespace IndieRun;

public sealed class RenameTarget
{
    public string Key {get;set;}=Guid.NewGuid().ToString("N");
    public string PackageId {get;set;}="";
    public string Original {get;set;}="";
    public string Current {get;set;}="";
    public string Name {get;set;}="";
    public string BackupId {get;set;}="";
}
public sealed class RenameGroup
{
    public List<string> Keys {get;set;}=[];
    public string Direction {get;set;}="Forward";
    public string State {get;set;}="Pending";
    public List<string> Staged {get;set;}=[];
    public List<string> Finished {get;set;}=[];
}
public sealed class RenameBatchRecord
{
    public int Version {get;set;}=1;
    public string Id {get;set;}=Guid.NewGuid().ToString("N");
    public string Mode {get;set;}="Forward";
    public string Status {get;set;}="Pending";
    public List<RenameTarget> Items {get;set;}=[];
    public List<RenameGroup> Groups {get;set;}=[];
    public EditBundle Current {get;set;}=new();
    public RenameRecord? Active {get;set;}
    public string ActiveKey {get;set;}="";
    public string ActivePhase {get;set;}="";
}

public sealed class RenameBatch(string dataRoot)
{
    public string Pending=>Path.Combine(dataRoot,"rename-batch-pending.json");
    public string Last=>Path.Combine(dataRoot,"rename-batch-last.json");
    public bool HasPending=>File.Exists(Pending);
    public Action<string>? Checkpoint {get;set;}
    static T Copy<T>(T value)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    static string Map(string path,string old,string next)=>GameScanner.Within(path,old)?Path.GetFullPath(Path.Combine(next,Path.GetRelativePath(old,path))):path;
    static string LocalTarget(RenameTarget item)=>Path.Combine(Path.GetDirectoryName(item.Original)!,item.Name);
    public static string Destination(RenameTarget item,IEnumerable<RenameTarget> items)
    {
        var parent=items.Where(t=>t.Key!=item.Key&&GameScanner.Within(item.Original,t.Original)).OrderByDescending(t=>t.Original.Length).FirstOrDefault();
        return parent==null?LocalTarget(item):Path.GetFullPath(Path.Combine(Destination(parent,items),Path.GetRelativePath(parent.Original,Path.GetDirectoryName(item.Original)!),item.Name));
    }
    public static List<string> Problems(WorkspaceDocument plan,IReadOnlyList<RenameTarget> items,string dataRoot)
    {
        var result=new List<string>();var originals=items.Select(i=>i.Original).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(string.IsNullOrWhiteSpace(plan.BackupRoot))result.Add("백업 위치를 먼저 선택하세요.");
        foreach(var t in items)
        {
            var prefix=Path.GetFileName(t.Original)+": ";var error=BackupEngine.MoveNameError(t.Name);if(error!=null){result.Add(prefix+error);continue;}
            if(!Directory.Exists(t.Original)){result.Add(prefix+"폴더 위치를 확인하세요.");continue;}
            if(!plan.Packages.Any(p=>p.Id==t.PackageId&&(p.Source.Equals(t.Original,StringComparison.OrdinalIgnoreCase)||p.Contents.Any(c=>c.Role=="related"&&Path.Combine(p.Source,c.Path).Equals(t.Original,StringComparison.OrdinalIgnoreCase)))))result.Add(prefix+"게임이나 지정된 관련 자료 폴더만 변경할 수 있습니다.");
            if(new[]{dataRoot,plan.Destination,plan.BackupRoot}.Where(s=>s.Length>0).Any(s=>GameScanner.Within(s,t.Original))||plan.BackupRoot.Length>0&&GameScanner.Within(t.Original,plan.BackupRoot))result.Add(prefix+"프로그램 정보·보관함·백업과 분리된 폴더를 선택하세요.");
            var local=LocalTarget(t);if((File.Exists(local)||Directory.Exists(local))&&!originals.Contains(local))result.Add(prefix+"같은 이름의 폴더나 파일이 있습니다.");
            try{BackupPaths.NoLinks(t.Original);BackupPaths.NoLinks(local);}catch(IOException ex){result.Add(prefix+ex.Message);}
        }
        if(originals.Count!=items.Count)result.Add("같은 폴더를 두 번 지정할 수 없습니다.");
        if(!result.Any()&&items.GroupBy(t=>Destination(t,items),StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))result.Add("변경 후 폴더명이 서로 겹칩니다.");
        return result;
    }
    static void Validate(RenameBatchRecord r)
    {
        if(r.Version!=1||!BackupPaths.Id(r.Id)||r.Mode is not ("Forward" or "Keep" or "Undo")||r.Items==null||r.Groups==null||r.Current==null||r.Items.Count==0||r.Items.Any(t=>!BackupPaths.Id(t.Key)||!BackupPaths.Id(t.PackageId)||!Path.IsPathFullyQualified(t.Original)||!Path.IsPathFullyQualified(t.Current)||BackupEngine.MoveNameError(t.Name)!=null||t.BackupId.Length>0&&!BackupPaths.Id(t.BackupId))||r.Items.Select(t=>t.Key).Distinct().Count()!=r.Items.Count)throw new InvalidDataException("이름 변경 기록을 확인하세요.");
        var all=r.Groups.SelectMany(g=>g.Keys).ToList();if(all.Count!=r.Items.Count||all.Distinct().Count()!=all.Count||all.Any(k=>!r.Items.Any(t=>t.Key==k))||r.Groups.Any(g=>g.Direction is not ("Forward" or "Undo")||g.Staged.Concat(g.Finished).Any(k=>!g.Keys.Contains(k)))||r.Active!=null&&(!r.Items.Any(i=>i.Key==r.ActiveKey)||r.ActivePhase is not ("stage" or "finish")))throw new InvalidDataException("이름 변경 순서를 확인하세요.");
        WorkspaceStore.Validate(r.Current.Plan);PlayerStore.Validate(r.Current.Play);
    }
    public RenameBatchRecord Load()=>BackupJson.Load<RenameBatchRecord>(Pending,Validate);
    public RenameBatchRecord? LoadLast()=>File.Exists(Last)?BackupJson.Load<RenameBatchRecord>(Last,Validate):null;
    void Save(RenameBatchRecord r)=>BackupJson.Save(Pending,r,Validate);
    public RenameBatchRecord Begin(WorkspaceDocument plan,TagCatalog tags,PlayerDocument play,List<RenameTarget> targets)
    {
        if(HasPending||new FolderRename(dataRoot).HasPending||File.Exists(new EditTransaction(dataRoot).Pending))throw new IOException("이전 변경을 먼저 복구하세요.");
        var errors=Problems(plan,targets,dataRoot);if(errors.Count>0)throw new IOException(string.Join("\n",errors));
        var items=Copy(targets.Where(t=>t.Name!=Path.GetFileName(t.Original)).ToList());if(items.Count==0)throw new IOException("변경할 폴더명을 입력하세요.");
        foreach(var t in items){t.Current=t.Original;t.BackupId="";}
        var groups=items.Select(i=>new RenameGroup{Keys=[i.Key]}).ToList();
        for(int i=0;i<items.Count;i++)for(int j=i+1;j<items.Count;j++)
        {
            var a=items[i];var b=items[j];if(!(GameScanner.Within(a.Original,b.Original)||GameScanner.Within(b.Original,a.Original)||LocalTarget(a).Equals(b.Original,StringComparison.OrdinalIgnoreCase)||LocalTarget(b).Equals(a.Original,StringComparison.OrdinalIgnoreCase)))continue;
            var ga=groups.Single(g=>g.Keys.Contains(a.Key));var gb=groups.Single(g=>g.Keys.Contains(b.Key));if(ga!=gb){ga.Keys.AddRange(gb.Keys);groups.Remove(gb);}
        }
        var record=new RenameBatchRecord{Items=items,Groups=groups,Current=new(){Plan=Copy(plan),Tags=Copy(tags),Play=Copy(play)}};
        if(File.Exists(Pending+".bak"))File.Move(Pending+".bak",Pending+".previous-"+Guid.NewGuid().ToString("N"));Save(record);Checkpoint?.Invoke("batch-intent");return record;
    }
    public void ResolveActive()
    {
        if(!HasPending)return;var r=Load();if(r.Active==null)return;Resolve(r);
    }
    void Resolve(RenameBatchRecord r)
    {
        if(r.Active==null)return;var active=r.Active;var service=new FolderRename(dataRoot){Checkpoint=p=>Checkpoint?.Invoke("step-"+p)};
        if(service.HasPending)
        {
            var existing=BackupJson.Load<RenameRecord>(service.Pending,_=>{});if(existing.Id!=active.Id)throw new IOException("다른 이름 변경 기록을 먼저 복구하세요.");service.Recover();
        }
        else
        {
            var lastPath=Path.Combine(dataRoot,"rename-last.json");var done=File.Exists(lastPath)&&BackupJson.Load<RenameRecord>(lastPath,_=>{}).Id==active.Id;
            if(!done)service.Commit(active);
        }
        foreach(var item in r.Items)item.Current=Map(item.Current,active.OldPath,active.NewPath);
        r.Current=Copy(active.Bundle);var group=r.Groups.Single(g=>g.Keys.Contains(r.ActiveKey));(r.ActivePhase=="stage"?group.Staged:group.Finished).Add(r.ActiveKey);r.Active=null;r.ActiveKey=r.ActivePhase="";Save(r);Checkpoint?.Invoke("step-recorded");
    }
    async Task Backups(RenameBatchRecord r,IProgress<string>? progress,CancellationToken token)
    {
        var service=new BackupEngine(r.Current.Plan.BackupRoot);
        foreach(var t in r.Items)
        {
            token.ThrowIfCancellationRequested();if(t.BackupId.Length>0)continue;
            var prior=service.ListJobs(false).FirstOrDefault(j=>j.OperationGroupId==r.Id&&j.SourceRoot==t.Original&&j.Status=="Completed");
            if(prior==null)
            {
                progress?.Report(Path.GetFileName(t.Original)+" · 백업 중");var p=r.Current.Plan.Packages.Single(p=>p.Id==t.PackageId);
                var sample=new Package{Id=t.PackageId,IsReal=true,IsContent=p.Source!=t.Original,Source=t.Original,DetectedRoot=t.Original,SearchRoot=Path.GetDirectoryName(t.Original)!,Folder=Path.GetFileName(t.Original),Name=Path.GetFileName(t.Original),Alias=PlayerRules.Title(p)};
                var unfinished=service.ListJobs(false).FirstOrDefault(j=>j.OperationGroupId==r.Id&&j.SourceRoot==t.Original&&!BackupEngine.Removed(j));
                prior=unfinished==null?await service.CreateAsync(sample,r.Current.Tags.Tags,cancellation:token,reason:"rename",operationGroupId:r.Id):unfinished.SourceVerified?await service.VerifyAsync(unfinished.Id,cancellation:token):await service.ResumeAsync(unfinished.Id,cancellation:token);
            }
            t.BackupId=prior.Id;Save(r);Checkpoint?.Invoke("backup-recorded");
        }
    }
    void Step(RenameBatchRecord r,RenameTarget t,string name,string phase)
    {
        var service=new FolderRename(dataRoot);r.Active=service.Prepare(Copy(r.Current.Plan),Copy(r.Current.Tags),Copy(r.Current.Play),t.Current,name);r.ActiveKey=t.Key;r.ActivePhase=phase;Save(r);Checkpoint?.Invoke("step-intent");Resolve(r);
    }
    void Group(RenameBatchRecord r,RenameGroup group,bool undo,IProgress<string>? progress,CancellationToken token)
    {
        var direction=undo?"Undo":"Forward";if(group.Direction!=direction){group.Direction=direction;group.Staged.Clear();group.Finished.Clear();Save(r);}
        var items=group.Keys.Select(k=>r.Items.Single(i=>i.Key==k)).ToList();
        if(undo&&items.All(t=>t.Current==t.Original)){group.State="RolledBack";Save(r);return;}
        if(!undo&&group.Staged.Count==0)
        {
            var backups=new BackupEngine(r.Current.Plan.BackupRoot);foreach(var t in items)
            {
                var job=backups.Load(t.BackupId);var scan=GameScanner.Scan(t.Current);var stamp=WorkspaceReconciler.Capture(new(){Source=t.Current},scan);
                if(job.Status!="Completed"||scan.Issues.Count>0||!stamp.Files.SequenceEqual(job.Files.Select(f=>new FileStamp(f.RelativePath,f.Length,f.ModifiedTicks)).OrderBy(f=>f.RelativePath,StringComparer.OrdinalIgnoreCase))||!stamp.Directories.SequenceEqual(job.Directories.OrderBy(d=>d,StringComparer.OrdinalIgnoreCase)))throw new IOException("백업 후 폴더 내용이 바뀌었습니다. 파일을 유지하고 중단했습니다.");
            }
        }
        foreach(var t in items.OrderByDescending(t=>t.Original.Length))
        {
            if(group.Staged.Contains(t.Key))continue;token.ThrowIfCancellationRequested();progress?.Report(Path.GetFileName(t.Original)+" · "+(undo?"되돌릴 준비":"이름 변경 중"));
            Step(r,t,".indierun-batch-"+r.Id[..8]+"-"+t.Key[..8]+(undo?"-undo":""),"stage");
        }
        foreach(var t in items.OrderBy(t=>t.Original.Length))
        {
            if(group.Finished.Contains(t.Key))continue;token.ThrowIfCancellationRequested();Step(r,t,undo?Path.GetFileName(t.Original):t.Name,"finish");
        }
        group.State=undo?"RolledBack":"Completed";Save(r);Checkpoint?.Invoke("group-completed");
    }
    public async Task<RenameBatchRecord> ContinueAsync(IProgress<string>? progress=null,CancellationToken token=default)
    {
        var r=Load();Resolve(r);if(r.Mode=="Forward")
        {
            await Backups(r,progress,token);foreach(var group in r.Groups.Where(g=>g.State!="Completed"))Group(r,group,false,progress,token);
        }
        else foreach(var group in r.Groups.AsEnumerable().Reverse().Where(g=>r.Mode=="Undo"||g.State!="Completed"))Group(r,group,true,progress,token);
        r.Status=r.Mode=="Forward"?"Completed":r.Mode=="Undo"?"RolledBack":"Stopped";Save(r);new BackupEngine(r.Current.Plan.BackupRoot).ReleaseRecoveryBackups(r.Id);
        BackupPaths.NoLinks(Last);if(File.Exists(Last))File.Replace(Pending,Last,Last+".bak",true);else File.Move(Pending,Last);return r;
    }
    public async Task<RenameBatchRecord> StopAsync(bool undoAll,IProgress<string>? progress=null,CancellationToken token=default)
    {
        var r=Load();if(r.Active!=null)
        {
            var service=new FolderRename(dataRoot);if(service.AbandonUnchanged()||!service.HasPending&&FolderRename.Exact(r.Active.OldPath)&&!FolderRename.Exact(r.Active.Temporary)&&!FolderRename.Exact(r.Active.NewPath))
            {r.Active=null;r.ActiveKey=r.ActivePhase="";Save(r);}else Resolve(r);
        }
        r.Mode=undoAll?"Undo":"Keep";Save(r);return await ContinueAsync(progress,token);
    }
}

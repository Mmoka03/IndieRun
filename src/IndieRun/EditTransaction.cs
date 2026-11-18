using System.IO;
using System.Text.Json;
namespace IndieRun;

public sealed class TagChange
{
    public string Id { get; set; }="";
    public string Kind { get; set; }="";
    public string OldName { get; set; }="";
    public string? NewName { get; set; }
}
public sealed class EditBundle
{
    public LibraryDocument? LibrarySeed {get;set;}
    public int Version { get; set; }=1;
    public WorkspaceDocument Plan { get; set; }=new();
    public TagCatalog Tags { get; set; }=new();
    public PlayerDocument Play { get; set; }=new();
    public List<string> EditedIds { get; set; }=[];
    public List<TagChange> TagChanges { get; set; }=[];
    public List<string> ReconnectedIds { get; set; }=[];
    public List<string> RemovedIds {get;set;}=[];
    public List<string> ImportedIds {get;set;}=[];
    public List<CopyIdentityChange> CopyIdentityChanges {get;set;}=[];
    public bool CopyIdentityApplied {get;set;}
}
// A durable intent is replayed after an interrupted multi-file edit. No game files are rewritten.
public sealed class EditTransaction(string dataRoot)
{
    public string Pending=>Path.Combine(dataRoot,"edit-pending.json");
    internal Action<string>? Checkpoint { get; set; }
    static void Validate(EditBundle bundle)
    {
        if(bundle.Version is not (1 or 2)||bundle.Plan==null||bundle.Tags==null||bundle.Play==null||bundle.EditedIds==null||bundle.TagChanges==null||bundle.TagChanges.Any(change=>change==null)||bundle.ReconnectedIds==null||bundle.ImportedIds==null||bundle.RemovedIds==null)throw new InvalidDataException("편집 기록을 확인하세요.");
        WorkspaceStore.Validate(bundle.Plan);PlayerStore.Validate(bundle.Play);if(bundle.LibrarySeed!=null)LibraryStore.Validate(bundle.LibrarySeed);
        if(bundle.RemovedIds.Any(id=>!BackupPaths.Id(id))||bundle.ImportedIds.Any(id=>!bundle.Plan.Packages.Any(p=>p.Id==id)||!bundle.Plan.Checkpoints.Any(c=>c.PackageId==id))||bundle.ReconnectedIds.Any(id=>!bundle.Plan.Packages.Any(p=>p.Id==id)||!bundle.Plan.Checkpoints.Any(c=>c.PackageId==id)))throw new InvalidDataException("편집 기록을 확인하세요.");
        CopyIdentity.Validate(bundle);
        if(bundle.Tags.Version!=1||bundle.Tags.Tags==null||bundle.Tags.Tags.Any(t=>t==null||string.IsNullOrWhiteSpace(t.Name)||t.Name.Length>40||t.Name.Any(char.IsControl)||t.Kind is not ("genre" or "category"))||bundle.Tags.Tags.GroupBy(t=>t.Id).Any(g=>g.Count()>1)||bundle.Tags.Tags.GroupBy(t=>(t.Kind,t.Name.ToUpperInvariant())).Any(g=>g.Count()>1))throw new InvalidDataException("태그 정보를 확인하세요.");
    }
    public void Commit(EditBundle bundle)
    {
        Validate(bundle);if(File.Exists(Pending))throw new IOException("이전 편집 저장을 먼저 복구하세요.");
        if(File.Exists(Pending+".bak"))File.Move(Pending+".bak",Pending+".previous-"+Guid.NewGuid().ToString("N"));
        BackupJson.Save(Pending,bundle,Validate);Checkpoint?.Invoke("intent");Apply(bundle);
    }
    public bool Recover()
    {if(!File.Exists(Pending))return false;Apply(BackupJson.Load<EditBundle>(Pending,Validate));return true;}
    public bool CanCancelUnappliedCopyIdentity()
    {
        try{return CancelUnappliedCopyIdentity(false);}
        catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException){return false;}
    }
    public bool CancelUnappliedCopyIdentity()=>CancelUnappliedCopyIdentity(true);
    bool CancelUnappliedCopyIdentity(bool archive)
    {
        if(!File.Exists(Pending))return false;BackupPaths.NoLinks(Pending);
        // Do not fall back to an older journal: only the current durable phase proves that no metadata was written.
        using var input=new FileStream(Pending,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
        if(input.Length>128L*1024*1024)throw new InvalidDataException("편집 기록이 너무 큽니다.");
        var bundle=JsonSerializer.Deserialize<EditBundle>(input)??throw new InvalidDataException("빈 편집 기록입니다.");Validate(bundle);
        if(bundle.CopyIdentityApplied||bundle.CopyIdentityChanges.Count!=1)return false;
        var change=bundle.CopyIdentityChanges.Single();var marker=Path.Combine(change.Folder,LibraryStore.MarkerName);BackupPaths.NoLinks(marker);
        if(!File.Exists(marker))return false;
        using var heldMarker=new FileStream(marker,FileMode.Open,FileAccess.Read,FileShare.Read);
        var original=Convert.FromBase64String(change.MarkerData);if(heldMarker.Length!=original.Length)return false;
        var current=new byte[original.Length];heldMarker.ReadExactly(current);if(!current.SequenceEqual(original))return false;
        if(archive){var cancelled=Pending+".cancelled-"+Guid.NewGuid().ToString("N")+".json";BackupPaths.NoLinks(cancelled);File.Move(Pending,cancelled,false);}
        return true;
    }
    void Apply(EditBundle bundle)
    {
        var root=bundle.Plan.Destination;BackupPaths.NoLinks(root);
        if(bundle.CopyIdentityChanges.Count>0){CopyIdentity.Apply(bundle);bundle.CopyIdentityApplied=true;BackupJson.Save(Pending,bundle,Validate);Checkpoint?.Invoke("copy-identity");}
        new TagCatalogStore(Path.Combine(dataRoot,"tags.json")).Save(bundle.Tags);Checkpoint?.Invoke("tags");
        new WorkspaceStore(Path.Combine(dataRoot,"workspace.json")).Save(bundle.Plan);Checkpoint?.Invoke("workspace");
        new PlayerStore(root).Save(bundle.Play);Checkpoint?.Invoke("play");
        var store=new LibraryStore(root);
        if(bundle.LibrarySeed!=null||bundle.ImportedIds.Count>0||bundle.RemovedIds.Count>0||File.Exists(Path.Combine(root,LibraryStore.JsonName))||File.Exists(Path.Combine(root,LibraryStore.JsonName+".bak")))
        {
            var result=store.Update(doc=>
            {
                if(bundle.LibrarySeed!=null){doc.Games= bundle.LibrarySeed.Games;doc.Tags=bundle.LibrarySeed.Tags;}
                doc.Games.RemoveAll(g=>bundle.RemovedIds.Contains(g.Id));
                foreach(var change in bundle.TagChanges)
                {
                    PlayerRules.ChangeTag([],doc,new TagDefinition {Id=change.Id,Kind=change.Kind,Name=change.OldName},change.NewName);
                    if(change.NewName==null)doc.Tags.RemoveAll(t=>t.Id==change.Id);else foreach(var tag in doc.Tags.Where(t=>t.Id==change.Id))tag.Name=change.NewName;
                }
                foreach(var id in bundle.ImportedIds)
                {
                    var imported=bundle.Plan.Packages.Single(p=>p.Id==id);
                    if(LibraryStore.ReadIdentity(imported.Source)?.GameId!=id)throw new IOException("불러오는 게임의 식별 정보가 변경됐습니다.");
                    if(doc.Games.Any(g=>g.Id==id))continue;
                    doc.Games.Add(new LibraryGame {Id=id,Folder=GameScanner.Within(imported.Source,root)?Path.GetRelativePath(root,imported.Source):imported.Folder,LastKnownPath=imported.Source,Status=GameScanner.Within(imported.Source,root)?"Present":"External",Executables=bundle.Plan.Checkpoints.Single(c=>c.PackageId==id).Files.Where(f=>f.RelativePath.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)).Select(f=>f.RelativePath).Order().ToList()});
                }
                foreach(var p in bundle.Plan.Packages.Where(p=>bundle.EditedIds.Contains(p.Id)))
                {
                    var game=doc.Games.Find(g=>g.Id==p.Id);if(game==null)continue;
                    game.Scope=StoredScope.Capture(p);game.IsContent=p.IsRelatedContent;game.ContentOwnerId=p.ContentOwnerId;game.CompositionParentId=p.CompositionParentId;game.Alias=p.Alias;game.Genres=[..p.Genres];game.Categories=[..p.Categories];game.GenreIds=[..p.GenreIds];game.CategoryIds=[..p.CategoryIds];
                }
                foreach(var tag in bundle.Tags.Tags){var prior=doc.Tags.Find(t=>t.Id==tag.Id);if(prior==null)doc.Tags.Add(new TagDefinition {Id=tag.Id,Kind=tag.Kind,Name=tag.Name});else{prior.Name=tag.Name;prior.Kind=tag.Kind;}}
                foreach(var id in bundle.ReconnectedIds)
                {
                    var p=bundle.Plan.Packages.Single(p=>p.Id==id);var game=doc.Games.Find(g=>g.Id==id);if(game==null)continue;game.Scope=StoredScope.Capture(p);
                    game.LastKnownPath=p.Source;game.Folder=GameScanner.Within(p.Source,root)?Path.GetRelativePath(root,p.Source):p.Name;
                    game.Status=GameScanner.Within(p.Source,root)?"Present":"External";
                    game.Executables=bundle.Plan.Checkpoints.Single(c=>c.PackageId==id).Files.Where(f=>f.RelativePath.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)).Select(f=>f.RelativePath).Order().ToList();
                }
            });
            if(result.CsvError!=null)throw new IOException("메타데이터는 저장됐지만 CSV를 갱신하지 못했습니다. 파일을 닫고 재시도하세요. "+result.CsvError);
        }
        Checkpoint?.Invoke("library");var completed=Path.Combine(dataRoot,"edit-last.json");BackupPaths.NoLinks(completed);BackupPaths.NoLinks(completed+".bak");
        if(File.Exists(completed))File.Replace(Pending,completed,completed+".bak",true);else File.Move(Pending,completed);
    }
}

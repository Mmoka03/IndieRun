using System.IO;
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
    public int Version { get; set; }=1;
    public WorkspaceDocument Plan { get; set; }=new();
    public TagCatalog Tags { get; set; }=new();
    public PlayerDocument Play { get; set; }=new();
    public List<string> EditedIds { get; set; }=[];
    public List<TagChange> TagChanges { get; set; }=[];
}
// A durable intent is replayed after an interrupted multi-file edit. No game files are rewritten.
public sealed class EditTransaction(string dataRoot)
{
    public string Pending=>Path.Combine(dataRoot,"edit-pending.json");
    internal Action<string>? Checkpoint { get; set; }
    static void Validate(EditBundle bundle)
    {
        if(bundle.Version!=1||bundle.EditedIds==null||bundle.TagChanges==null)throw new InvalidDataException("편집 기록 확인 필요");
        WorkspaceStore.Validate(bundle.Plan);PlayerStore.Validate(bundle.Play);
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
    void Apply(EditBundle bundle)
    {
        var root=bundle.Plan.Destination;BackupPaths.NoLinks(root);
        new TagCatalogStore(Path.Combine(dataRoot,"tags.json")).Save(bundle.Tags);Checkpoint?.Invoke("tags");
        new WorkspaceStore(Path.Combine(dataRoot,"workspace.json")).Save(bundle.Plan);Checkpoint?.Invoke("workspace");
        new PlayerStore(root).Save(bundle.Play);Checkpoint?.Invoke("play");
        var store=new LibraryStore(root);
        if(File.Exists(Path.Combine(root,LibraryStore.JsonName))||File.Exists(Path.Combine(root,LibraryStore.JsonName+".bak")))
        {
            var result=store.Update(doc=>
            {
                foreach(var change in bundle.TagChanges)
                {
                    PlayerRules.ChangeTag([],doc,new TagDefinition {Id=change.Id,Kind=change.Kind,Name=change.OldName},change.NewName);
                    if(change.NewName==null)doc.Tags.RemoveAll(t=>t.Id==change.Id);else foreach(var tag in doc.Tags.Where(t=>t.Id==change.Id))tag.Name=change.NewName;
                }
                foreach(var p in bundle.Plan.Packages.Where(p=>bundle.EditedIds.Contains(p.Id)))
                {
                    var game=doc.Games.Find(g=>g.Id==p.Id);if(game==null)continue;
                    game.Alias=p.Alias;game.Genres=[..p.Genres];game.Categories=[..p.Categories];game.GenreIds=[..p.GenreIds];game.CategoryIds=[..p.CategoryIds];
                }
                foreach(var tag in bundle.Tags.Tags){var prior=doc.Tags.Find(t=>t.Id==tag.Id);if(prior==null)doc.Tags.Add(new TagDefinition {Id=tag.Id,Kind=tag.Kind,Name=tag.Name});else{prior.Name=tag.Name;prior.Kind=tag.Kind;}}
            });
            if(result.CsvError!=null)throw new IOException("메타데이터는 저장됐지만 CSV를 갱신하지 못했습니다. 파일을 닫고 재시도하세요. "+result.CsvError);
        }
        Checkpoint?.Invoke("library");var completed=Path.Combine(dataRoot,"edit-last.json");BackupPaths.NoLinks(completed);BackupPaths.NoLinks(completed+".bak");
        if(File.Exists(completed))File.Replace(Pending,completed,completed+".bak",true);else File.Move(Pending,completed);
    }
}

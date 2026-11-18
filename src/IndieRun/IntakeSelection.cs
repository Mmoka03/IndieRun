namespace IndieRun;

// Per-operation choices are deliberately not saved as defaults for the next import.
public sealed class IntakeSelection
{
    public MoveSelection Composition {get;}
    public HashSet<string> Moving {get;}=[];
    public bool Backup {get;set;}=true;
    public IntakeSelection(MoveSelection composition,bool moveAll=false)
    {Composition=composition;if(moveAll)SetAll(true);}
    public List<Package> Games=>Composition.Available.Where(p=>Composition.Requested.Contains(p.Id)).ToList();
    public void SetAll(bool move){Moving.Clear();if(move)Moving.UnionWith(Composition.Requested);}
    public List<Package> MoveItems(string target,IEnumerable<Package> all)
    {
        var scoped=new MoveSelection(Games.Where(p=>Moving.Contains(p.Id)),all);
        scoped.Selected.IntersectWith(Composition.Selected);
        foreach(var p in scoped.Available)if(Composition.Available.Find(q=>q.Id==p.Id) is {} draft)p.Name=draft.Name;
        return scoped.Items(target);
    }
}

public sealed record IntakeResult(string Id,string Title,string State)
{
    public string Registration {get;init;}="";
    public string Location {get;init;}="";
    public string Backup {get;init;}="";
    public string Solution {get;init;}="Review";
    public string Source {get;init;}="";
    public string Destination {get;init;}="";
    public string? Detail {get;init;}
    public WorkRecord? Record {get;init;}
    public DateTime SinceUtc {get;init;}
    public bool Saved {get;init;}=true;
    public bool Success=>State is "등록 완료" or "이미 등록됨" or "이동 완료";
}

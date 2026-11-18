using System.IO;
using System.Text.Json;
namespace IndieRun;
public sealed class MoveSelection
{
    public List<Package> Available {get;}=[];
    public HashSet<string> Requested {get;}=[];
    public HashSet<string> Selected {get;}=[];
    public HashSet<string> Shared {get;}=[];
    public string Signature {get;}
    static Package Copy(Package p){var c=JsonSerializer.Deserialize<Package>(JsonSerializer.Serialize(p))!;c.ReviewState=p.ReviewState;c.ReviewReason=p.ReviewReason;return c;}
    public static string Key(IEnumerable<Package> request,IEnumerable<Package> all)
    {
        var chosen=request.ToList();var list=all.ToList();var refs=chosen.SelectMany(p=>p.RelatedFolders).Select(r=>r.Id).ToHashSet();
        return string.Join("|",chosen.OrderBy(p=>p.Id).Select(p=>p.Id+":"+p.Source+":"+string.Join(",",p.RelatedFolders.OrderBy(r=>r.Id).Select(r=>r.Id+"="+r.Source+"="+r.Together))))+"#"+string.Join(",",list.Where(p=>refs.Contains(p.Id)).OrderBy(p=>p.Id).Select(p=>p.Id+"="+ContentLinks.Owners(list,p.Id).Count));
    }
    public void Refresh(IEnumerable<Package> all)
    {
        var list=all.ToList();foreach(var p in Available){var current=list.Find(q=>q.Id==p.Id);if(current!=null){if(current.Alias.Length>0||!p.IsRelatedContent)p.Alias=current.Alias;p.ReviewState=current.ReviewState;p.ReviewReason=current.ReviewReason;}if(!Directory.Exists(p.Source)){p.ReviewState="missing";p.ReviewReason="위치 확인 필요";}}
    }
    public MoveSelection(IEnumerable<Package> request,IEnumerable<Package> all)
    {
        var list=all.ToList();var chosen=request.ToList();Signature=Key(chosen,list);
        foreach(var p in chosen){Requested.Add(p.Id);Selected.Add(p.Id);Available.Add(Copy(p));}
        foreach(var owner in chosen)foreach(var r in owner.RelatedFolders)
        {
            if(Available.Any(p=>p.Id==r.Id))continue;
            var content=list.Find(p=>p.Id==r.Id);if(content==null)content=new(){Id=r.Id,IsReal=true,IsContent=true,Registered=false,Source=r.Source,DetectedRoot=r.Source,SearchRoot=Path.GetDirectoryName(r.Source)!,Folder=Path.GetFileName(r.Source),Name=r.Name};
            var copy=Copy(content);copy.Name=r.Name;if(r.Alias.Length>0)copy.Alias=r.Alias;Available.Add(copy);
            if(ContentLinks.Owners(list,r.Id).Count>1)Shared.Add(r.Id);
            if(!Directory.Exists(copy.Source)){copy.ReviewState="missing";copy.ReviewReason="위치 확인 필요";}
            if(!Shared.Contains(copy.Id)&&copy.ReviewState.Length==0)Selected.Add(copy.Id);
        }
    }
    public List<Package> Items(string root)
    {
        var result=Available.Where(p=>Selected.Contains(p.Id)).Select(Copy).ToList();foreach(var p in result)p.PlannedContainer="";
        foreach(var owner in result.Where(p=>!p.IsRelatedContent))
        foreach(var link in owner.RelatedFolders.Where(r=>r.Together&&!Shared.Contains(r.Id)))
        {var content=result.Find(p=>p.Id==link.Id);if(content==null)continue;owner.PlannedContainer=owner.Name+"_bundle";content.PlannedContainer=owner.PlannedContainer;}
        return result.Where(p=>Requested.Contains(p.Id)||!p.Source.Equals(CompositionPlan.Target(p,root),StringComparison.OrdinalIgnoreCase)).OrderBy(p=>p.IsRelatedContent?1:0).ThenByDescending(p=>p.Source.Length).ToList();
    }
}


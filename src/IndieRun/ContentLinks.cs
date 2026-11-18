using System.IO;
namespace IndieRun;
public static class ContentLinks
{
    public static List<Package> Owners(IEnumerable<Package> all,string id)=>all.Where(p=>p.RelatedFolders.Any(r=>r.Id==id)).ToList();
    public static void Normalize(List<Package> all)
    {
        foreach(var p in all.Where(p=>p.IsRelatedContent||Owners(all,p.Id).Count>0))
        {
            p.IsContent=true;var owners=Owners(all,p.Id);p.ContentOwnerId=owners.FirstOrDefault()?.Id??"";
            foreach(var owner in owners)foreach(var link in owner.RelatedFolders.Where(r=>r.Id==p.Id)){link.Source=p.Source;link.Name=Path.GetFileName(p.Source);}
        }
    }
    public static string Title(RelatedFolder link)=>string.IsNullOrWhiteSpace(link.Alias)?link.Name:link.Alias;
    public static string Title(FolderAssignment part)=>string.IsNullOrWhiteSpace(part.Alias)?Path.GetFileName(part.Path):part.Alias;
}

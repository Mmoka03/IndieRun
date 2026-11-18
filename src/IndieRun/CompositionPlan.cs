using System.IO;
using System.Text.Json;
namespace IndieRun;

public static class CompositionPlan
{
    static T Copy<T>(T value)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    public static string Label(string role)=>role switch{"related"=>"관련 콘텐츠","game"=>"별도 게임","exclude"=>"포함하지 않음",_=>"본편"};
    public static string Color(string role)=>role switch{"related"=>"Composition.Related","game"=>"Composition.Game","exclude"=>"Neutral",_=>"Accent"};
    public static string Role(Package p,string path)=>p.Contents.Where(c=>GameScanner.Within(path,Path.Combine(p.Source,c.Path))).OrderByDescending(c=>c.Path.Length).FirstOrDefault()?.Role??"main";
    public static void Assign(Package p,string path,string role,GamePlay play,ScanSnapshot scan)
    {
        path=Path.GetFullPath(path);BackupPaths.NoLinks(path);
        if(!GameScanner.Within(path,p.Source)||!Directory.Exists(path)||!scan.Directories.Contains(path,StringComparer.OrdinalIgnoreCase))throw new IOException("현재 게임 안의 폴더를 선택하세요.");
        var rel=Path.GetRelativePath(p.Source,path);
        if(rel=="."){if(role!="main")throw new IOException("최상위 폴더는 본편으로 유지합니다. 하위 폴더를 지정하세요.");return;}
        if(role is not ("main" or "related" or "game" or "exclude"))throw new IOException("구성을 확인하세요.");
        if(p.Contents.Any(c=>c.Path!=rel&&GameScanner.Within(path,Path.Combine(p.Source,c.Path))))throw new IOException("상위 폴더의 지정을 먼저 해제하세요.");
        if(role!="main"&&(GameScanner.Within(p.DetectedRoot,path)||play.Executable.Length>0&&GameScanner.Within(BackupPaths.Child(p.Source,play.Executable),path)||play.WorkingDirectory.Length>0&&GameScanner.Within(play.WorkingDirectory=="."?p.Source:BackupPaths.Child(p.Source,play.WorkingDirectory),path)))throw new IOException("본편 실행 파일이나 작업 폴더가 포함되어 있습니다. 실행 설정을 먼저 확인하세요.");
        if(role=="game"&&!scan.Files.Any(f=>GameScanner.Within(f.Path,path)&&Path.GetExtension(f.Path).Equals(".exe",StringComparison.OrdinalIgnoreCase)))throw new IOException("실행 파일이 없는 폴더입니다. 관련 콘텐츠로 지정할 수 있습니다.");
        var prior=p.Contents.Find(c=>c.Path.Equals(rel,StringComparison.OrdinalIgnoreCase));p.Contents.RemoveAll(c=>GameScanner.Within(Path.Combine(p.Source,c.Path),path));
        if(role!="main")p.Contents.Add(new(){Path=rel,Role=role,Id=prior?.Id??Guid.NewGuid().ToString("N")});
        p.OmittedFolders=p.Contents.Where(c=>c.Role is "game" or "exclude").Select(c=>c.Path).ToList();
    }
    public static List<Package> Apply(Package draft,List<Package> current,ScanSnapshot scan)
    {
        draft.ValidateComposition();var result=Copy(current);var index=result.FindIndex(p=>p.Id==draft.Id);if(index<0)throw new IOException("선택한 게임을 찾지 못했습니다.");
        var old=result[index];
        foreach(var part in draft.Contents.Where(c=>c.Role=="related"))if(result.Any(p=>p.Id!=draft.Id&&GameScanner.Within(p.Source,Path.Combine(draft.Source,part.Path))))throw new IOException("다른 게임으로 등록된 폴더는 관련 콘텐츠로 바꾸지 않습니다.");
        foreach(var removed in old.Contents.Where(c=>c.Role is "game" or "exclude"&&!draft.Contents.Any(n=>n.Id==c.Id&&n.Role==c.Role)))
        {
            var owned=result.Find(p=>p.Id==removed.Id);if(owned==null)continue;
            if(GameRegistration.IsAdded(owned)||File.Exists(Path.Combine(owned.Source,LibraryStore.MarkerName)))throw new IOException("이미 추가한 별도 게임은 자동으로 합치지 않습니다. 현재 구성을 유지하세요.");
            result.Remove(owned);
        }
        foreach(var detached in old.RelatedFolders.Where(r=>!draft.RelatedFolders.Any(n=>n.Id==r.Id)))
        {var item=result.Find(p=>p.Id==detached.Id);if(item!=null){item.IsContent=true;item.ContentOwnerId="";item.CompositionParentId="";}}
        result[result.FindIndex(p=>p.Id==draft.Id)]=Copy(draft);
        foreach(var part in draft.Contents.Where(c=>c.Role is "game" or "exclude"))
        {
            var path=BackupPaths.Child(draft.Source,part.Path);var existing=result.Find(p=>p.Id==part.Id||p.Source.Equals(path,StringComparison.OrdinalIgnoreCase));
            if(result.Any(p=>p.Id!=draft.Id&&p!=existing&&p.Includes(path)))throw new IOException("다른 게임과 폴더 범위가 겹칩니다.");
            if(existing!=null&&existing.Id!=part.Id){part.Id=existing.Id;result.Find(p=>p.Id==draft.Id)!.Contents.Find(c=>c.Path==part.Path)!.Id=existing.Id;}
            var marker=LibraryStore.ReadIdentity(path);if(marker!=null&&marker.GameId!=existing?.Id)throw new IOException("별도 폴더에 기존 식별 정보가 있습니다. 먼저 게임으로 추가해 확인하세요.");
            if(existing==null){existing=New(path,draft,part.Id,scan);result.Add(existing);}
            existing.CompositionParentId=draft.Id;existing.Excluded=part.Role=="exclude";existing.ContentOwnerId="";
        }
        foreach(var related in draft.RelatedFolders)
        {
            BackupPaths.NoLinks(related.Source);if(!Directory.Exists(related.Source)||GameScanner.Within(related.Source,draft.Source)||GameScanner.Within(draft.Source,related.Source))throw new IOException("게임 밖의 별도 자료 폴더를 선택하세요.");
            var existing=result.Find(p=>p.Id==related.Id||p.Source.Equals(related.Source,StringComparison.OrdinalIgnoreCase));
            if(existing!=null&&(existing.RelatedFolders.Count>0||existing.Contents.Count>0))throw new IOException("다른 구성에 연결된 폴더입니다. 기존 연결을 먼저 확인하세요.");
            if(existing!=null&&!existing.IsRelatedContent&&GameRegistration.IsAdded(existing))throw new IOException("이미 추가한 게임은 관련 콘텐츠로 바꾸지 않습니다.");
            if(result.Any(p=>p.Id!=draft.Id&&p!=existing&&(p.Includes(related.Source)||GameScanner.Within(p.Source,related.Source))))throw new IOException("다른 게임이나 관련 콘텐츠와 범위가 겹칩니다.");
            var marker=LibraryStore.ReadIdentity(related.Source);if(marker!=null&&marker.GameId!=existing?.Id)throw new IOException("기존 식별 정보가 있는 폴더입니다. 먼저 등록 상태를 확인하세요.");
            if(existing==null){existing=New(related.Source,draft,related.Id,scan);result.Add(existing);}else{related.Id=existing.Id;result.Find(p=>p.Id==draft.Id)!.RelatedFolders.Find(r=>r.Source==related.Source)!.Id=existing.Id;}
            existing.IsContent=true;existing.ContentOwnerId=draft.Id;existing.CompositionParentId=draft.Id;existing.Excluded=false;
        }
        foreach(var item in result.Where(p=>p.Id==draft.Id||p.CompositionParentId==draft.Id))
        {
            if(!scan.Directories.Contains(item.Source,StringComparer.OrdinalIgnoreCase))continue;
            var bytes=scan.Files.Where(f=>item.Includes(f.Path)).Sum(f=>f.Length);item.Gb=bytes/(1024d*1024*1024);item.Size=bytes>=1024L*1024*1024?$"{item.Gb:0.##} GB":bytes>=1024*1024?$"{bytes/(1024d*1024):0.##} MB":bytes>=1024?$"{bytes/1024d:0.#} KB":$"{bytes} B";
        }
        ContentLinks.Normalize(result);return result;
    }
    static Package New(string path,Package parent,string id,ScanSnapshot scan)
    {
        var exe=scan.Files.FirstOrDefault(f=>GameScanner.Within(f.Path,path)&&Path.GetExtension(f.Path).Equals(".exe",StringComparison.OrdinalIgnoreCase));
        return new(){Id=id,IsReal=true,Registered=false,Source=path,DetectedRoot=exe==null?path:Path.GetDirectoryName(exe.Path)!,SearchRoot=GameScanner.Within(path,parent.SearchRoot)?parent.SearchRoot:Path.GetDirectoryName(path)!,Folder=Path.GetFileName(path),Name=Path.GetFileName(path),Color=parent.Color};
    }
    static Package MoveCopy(Package p){var copy=Copy(p);copy.ReviewState=p.ReviewState;copy.ReviewReason=p.ReviewReason;copy.ReviewDetails=[..p.ReviewDetails];return copy;}
    public static List<Package> Expand(IEnumerable<Package> selected,IEnumerable<Package> all)
    {
        var chosen=selected.DistinctBy(p=>p.Id).ToDictionary(p=>p.Id);var source=all.ToDictionary(p=>p.Id);
        var result=chosen.Values.ToDictionary(p=>p.Id,MoveCopy);var queue=new Queue<string>(chosen.Keys);var visited=new HashSet<string>();
        Package Add(string id)
        {
            if(result.TryGetValue(id,out var found))return found;
            if(!source.TryGetValue(id,out var original))throw new IOException("게임 구성과 연결 위치를 다시 확인하세요.");
            var copy=MoveCopy(original);result.Add(id,copy);queue.Enqueue(id);return copy;
        }
        while(queue.TryDequeue(out var id))
        {
            if(!visited.Add(id))continue;var p=result[id];

            if(p.RelatedFolders.Any(r=>r.Together&&ContentLinks.Owners(source.Values,r.Id).Count<=1)&&p.PlannedContainer.Length==0)p.PlannedContainer=p.Name+"_bundle";
            foreach(var related in p.RelatedFolders)
            {
                if(ContentLinks.Owners(source.Values,related.Id).Count>1||!Directory.Exists(related.Source))continue;
                var child=Add(related.Id);if(!chosen.ContainsKey(child.Id))child.Name=related.Name;
                child.PlannedContainer=related.Together?p.PlannedContainer:"";
            }
        }
        // Main folders omit extracted children, so move children first. Undo runs in reverse.
        return result.Values.OrderByDescending(p=>p.Source.Length).ToList();
    }
    public static List<Package> Moving(List<Package> items,string root)=>items.Where(p=>!items.Any(owner=>owner.Id==p.CompositionParentId)||!p.Source.Equals(Target(p,root),StringComparison.OrdinalIgnoreCase)).ToList();
    public static string Target(Package p,string root)=>p.PlannedContainer.Length==0?Path.Combine(root,p.Name):Path.Combine(root,p.PlannedContainer,p.Name);
    public static void Preflight(BackupEngine engine,List<Package> items,string root)
    {
        foreach(var p in items){p.ValidateComposition();engine.Preflight([(p.Source,Target(p,root))]);}
        for(var i=0;i<items.Count;i++)for(var j=i+1;j<items.Count;j++)
        {
            var a=items[i];var b=items[j];var ad=Target(a,root);var bd=Target(b,root);
            if(a.Includes(b.Source)||b.Includes(a.Source)||GameScanner.Within(ad,bd)||GameScanner.Within(bd,ad)||GameScanner.Within(ad,b.Source)||GameScanner.Within(b.Source,ad)||GameScanner.Within(bd,a.Source)||GameScanner.Within(a.Source,bd))throw new IOException("이동 범위나 도착 위치가 겹칩니다.");
        }
    }
    public static string Summary(Package p)=>$"본편 · 관련 콘텐츠 {p.Contents.Count(c=>c.Role=="related")+p.RelatedFolders.Count}개"+(p.Contents.Any(c=>c.Role=="game")?$" · 별도 게임 {p.Contents.Count(c=>c.Role=="game")}개":"")+(p.Contents.Any(c=>c.Role=="exclude")?$" · 제외 {p.Contents.Count(c=>c.Role=="exclude")}개":"");
}

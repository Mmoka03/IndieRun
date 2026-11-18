using System.Diagnostics;
using System.IO;
namespace IndieRun;

public sealed class GamePlay
{
    public string Id { get; set; }="";
    public bool Hidden {get;set;}
    public bool Favorite { get; set; }
    public DateTime AddedUtc { get; set; }=DateTime.UtcNow;
    public DateTime? LastPlayedUtc { get; set; }
    public string Executable { get; set; }="";
    public string Arguments { get; set; }="";
    public string WorkingDirectory { get; set; }="";
    public string Image { get; set; }="";
    public double CropX { get; set; }=.5;
    public double CropY { get; set; }=.5;
}
public sealed class PlayerDocument
{
    public int Version { get; set; }=1;
    public List<GamePlay> Games { get; set; }=[];
}
public sealed class PlayerView
{
    public string Layout { get; set; }="cards";
    public string Query { get; set; }="";
    public bool IncludeOriginal { get; set; }
    public string Sort { get; set; }="alias";
    public string Group { get; set; }="all";
    public List<string> TagIds { get; set; }=[];
}
public static class PlayerRules
{
    public static string Title(Package p)=>string.IsNullOrWhiteSpace(p.Alias)?p.Folder:p.Alias;
    public static Dictionary<string,List<(string Id,string Title)>> TagUsage(IEnumerable<Package> packages,LibraryDocument library,IEnumerable<TagDefinition> definitions)
    {
        var tags=definitions.ToList();var result=tags.ToDictionary(t=>t.Id,_=>new Dictionary<string,string>());
        var names=tags.ToDictionary(t=>(t.Kind,t.Name),t=>t.Id);var kinds=tags.ToDictionary(t=>t.Id,t=>t.Kind);
        void Add(string id,string title,IEnumerable<string> genres,IEnumerable<string> categories,IEnumerable<string> genreIds,IEnumerable<string> categoryIds)
        {
            foreach(var pair in new[]{("genre",genres,genreIds),("category",categories,categoryIds)})
            {
                foreach(var name in pair.Item2)if(names.TryGetValue((pair.Item1,name),out var tagId))result[tagId].TryAdd(id,title);
                foreach(var tagId in pair.Item3)if(kinds.GetValueOrDefault(tagId)==pair.Item1)result[tagId].TryAdd(id,title);
            }
        }
        foreach(var p in packages)Add(p.Id,Title(p),p.Genres,p.Categories,p.GenreIds,p.CategoryIds);
        foreach(var g in library.Games)Add(g.Id,string.IsNullOrWhiteSpace(g.Alias)?g.Folder:g.Alias,g.Genres,g.Categories,g.GenreIds,g.CategoryIds);
        return result.ToDictionary(pair=>pair.Key,pair=>pair.Value.Select(g=>(Id:g.Key,Title:g.Value)).OrderBy(g=>g.Title,StringComparer.CurrentCultureIgnoreCase).ToList());
    }
    public static bool Matches(Package p,GamePlay settings,PlayerView view,IEnumerable<TagDefinition> tags)
    {
        if(p.Excluded||(view.Group=="hidden"?!settings.Hidden:settings.Hidden))return false;
        var haystack=string.IsNullOrWhiteSpace(p.Alias)||view.IncludeOriginal?Title(p)+" "+p.Folder:Title(p);
        if(!haystack.Contains(view.Query.Trim(),StringComparison.OrdinalIgnoreCase))return false;
        if(view.Group=="favorites"&&!settings.Favorite||view.Group=="recent"&&settings.LastPlayedUtc==null)return false;
        return view.TagIds.All(id=>tags.Any(t=>t.Id==id&&(t.Kind=="genre"?p.Genres:p.Categories).Contains(t.Name)));
    }
    public static void ApplyTags(Package p,IEnumerable<TagDefinition> tags,ISet<string> additions,ISet<string> removals)
    {
        if(additions.Overlaps(removals))throw new InvalidDataException("같은 태그를 추가와 제거에 동시에 지정할 수 없습니다.");
        foreach(var t in tags)
        {
            var list=t.Kind=="genre"?p.Genres:p.Categories;
            if(removals.Contains(t.Id))list.RemoveAll(n=>n==t.Name);
            if(additions.Contains(t.Id)&&!list.Contains(t.Name))list.Add(t.Name);
        }
    }
    public static void ChangeTag(IEnumerable<Package> packages,LibraryDocument library,TagDefinition tag,string? replacement)
    {
        foreach(var p in packages)
        {
            var values=tag.Kind=="genre"?p.Genres:p.Categories;var affected=values.Contains(tag.Name)||(tag.Kind=="genre"?p.GenreIds:p.CategoryIds).Contains(tag.Id);values.RemoveAll(n=>n==tag.Name);
            if(replacement!=null&&affected&&!values.Contains(replacement))values.Add(replacement);
            if(replacement==null)(tag.Kind=="genre"?p.GenreIds:p.CategoryIds).RemoveAll(id=>id==tag.Id);
        }
        foreach(var g in library.Games)
        {
            var values=tag.Kind=="genre"?g.Genres:g.Categories;var ids=tag.Kind=="genre"?g.GenreIds:g.CategoryIds;
            var affected=values.Contains(tag.Name)||ids.Contains(tag.Id);values.RemoveAll(n=>n==tag.Name);
            if(affected&&replacement!=null)values.Add(replacement);if(replacement==null)ids.RemoveAll(id=>id==tag.Id);
        }
    }
}
public sealed class PlayerStore(string root)
{
    public string Root { get; }=Path.GetFullPath(root);
    public string FilePath=>Path.Combine(Root,"indierun-play.json");
    public static void Validate(PlayerDocument doc)
    {
        if(doc.Version!=1||doc.Games==null||doc.Games.Any(g=>g==null||!BackupPaths.Id(g.Id)||g.Arguments==null||g.Arguments.Contains('\0')||g.Executable==null||g.WorkingDirectory==null||g.Image==null||g.Executable.Length>0&&(!BackupPaths.Relative(g.Executable)||!g.Executable.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))||g.WorkingDirectory.Length>0&&g.WorkingDirectory!="."&&!BackupPaths.Relative(g.WorkingDirectory)||g.Image.Length>0&&(!BackupPaths.Relative(g.Image)||!g.Image.StartsWith(".indierun-art/",StringComparison.Ordinal))||!double.IsFinite(g.CropX)||!double.IsFinite(g.CropY)||g.CropX<0||g.CropX>1||g.CropY<0||g.CropY>1)||doc.Games.GroupBy(g=>g.Id).Any(g=>g.Count()>1))throw new InvalidDataException("실행 설정을 확인해 주세요.");
    }
    public PlayerDocument Load()=>File.Exists(FilePath)||File.Exists(FilePath+".bak")?BackupJson.Load<PlayerDocument>(FilePath,Validate):new();
    public void Save(PlayerDocument doc)=>BackupJson.Save(FilePath,doc,Validate);
    public static void ValidateLaunchEdit(Package package,GamePlay original,GamePlay draft,bool requireExecutable=false)
    {
        Validate(new PlayerDocument {Games=[draft]});
        var changed=original.Executable!=draft.Executable||original.WorkingDirectory!=draft.WorkingDirectory||original.Arguments!=draft.Arguments;
        if(!requireExecutable&&!changed)return;
        if(draft.Executable.Length==0)throw new GameLaunchException(LaunchProblem.Setup,"실행 파일을 먼저 선택하세요.");
        StartInfo(package,draft);
    }
    public static ProcessStartInfo StartInfo(Package p,GamePlay game)
    {
        Validate(new PlayerDocument {Games=[game]});
        GameLaunch.RequireFolder(p.Source,LaunchProblem.FolderMissing);
        var marker=GameLaunch.ReadIdentity(p.Source);if(marker!=null&&marker.GameId!=p.Id)throw new GameLaunchException(LaunchProblem.Identity,GameLaunch.State(LaunchProblem.Identity).Message);
        if(game.Executable.Length==0)throw new GameLaunchException(LaunchProblem.Setup,GameLaunch.State(LaunchProblem.Setup).Message);
        var executable=BackupPaths.Child(p.Source,game.Executable);BackupPaths.NoLinks(executable);
        if(!p.Includes(executable))throw new GameLaunchException(LaunchProblem.Settings,"별도 게임 또는 제외한 폴더의 실행 파일입니다. 게임 구성을 확인하세요.");
        GameLaunch.RequireFile(executable);
        var working=game.WorkingDirectory.Length==0?Path.GetDirectoryName(executable)!:game.WorkingDirectory=="."?p.Source:BackupPaths.Child(p.Source,game.WorkingDirectory);
        if(!p.Includes(working))throw new GameLaunchException(LaunchProblem.Settings,"작업 폴더가 본편 범위 밖에 있습니다.");
        GameLaunch.RequireFolder(working,LaunchProblem.WorkingFolderMissing);
        return new ProcessStartInfo(executable){Arguments=game.Arguments,WorkingDirectory=working,UseShellExecute=false};
    }
    public static Process Launch(Package p,GamePlay game)
    {
        var process=Process.Start(StartInfo(p,game))??throw new IOException("게임을 시작하지 못했습니다.");game.LastPlayedUtc=DateTime.UtcNow;return process;
    }
}

public static class TagBatchRules
{
    public static int Present(IEnumerable<Package> games,TagDefinition tag)=>games.Count(p=>(tag.Kind=="genre"?p.Genres:p.Categories).Contains(tag.Name));
    public static int Affected(IReadOnlyCollection<Package> games,TagDefinition tag,bool add)=>add?games.Count-Present(games,tag):Present(games,tag);
}

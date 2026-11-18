using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace IndieRun;
public sealed class DemoManifest
{
    public int Version {get;set;}=1;
    public List<DemoGame> Games {get;set;}=[];
}
public sealed class DemoGame
{
    public string Key {get;set;}="";
    public string Folder {get;set;}="";
    public string Alias {get;set;}="";
    public string Executable {get;set;}="";
    public bool Content {get;set;}
    public bool Added {get;set;}=true;
    public bool Missing {get;set;}
    public List<string> Resources {get;set;}=[];
    public List<string> InternalResources {get;set;}=[];
    public List<string> Separate {get;set;}=[];
    public string Image {get;set;}="";
}
public static class DemoSetup
{
    public static void Initialize(string appFolder)
    {
        appFolder=Path.GetFullPath(appFolder);var kit=Directory.GetParent(Path.TrimEndingDirectorySeparator(appFolder))!.FullName;var mock=Path.Combine(kit,"MockData");var data=Path.Combine(appFolder,"data");var marker=Path.Combine(data,"demo-session.json");var workspace=Path.Combine(data,"workspace.json");
        if(File.Exists(workspace))
        {
            if(!File.Exists(marker))throw new IOException("이 폴더에는 기존 보관 데이터가 있습니다. 테스트 압축파일을 새 폴더에 풀어 실행하세요.");
            var prior=JsonNode.Parse(File.ReadAllText(marker))?["Root"]?.GetValue<string>()??throw new IOException("테스트 기록을 확인하세요.");
            if(prior!=kit)Rebase(kit,data,prior);return;
        }
        BackupPaths.NoLinks(mock);var template=Path.Combine(mock,"demo-manifest.json");var manifest=JsonSerializer.Deserialize<DemoManifest>(File.ReadAllText(template))??throw new IOException("목데이터를 찾지 못했습니다.");if(manifest.Version!=1||manifest.Games.Count>100)throw new IOException("목데이터 형식을 확인하세요.");
        var library=Path.Combine(kit,"Library");var backups=Path.Combine(kit,"Backups");var all=new List<Package>();var keys=new Dictionary<string,Package>();var tags=new TagCatalog{Tags=[new(){Kind="genre",Name="퍼즐"},new(){Kind="genre",Name="탐험"},new(){Kind="category",Name="테스트용"}]};var players=new PlayerDocument();
        foreach(var item in manifest.Games)
        {
            if(!BackupPaths.Relative(item.Folder)||item.Key.Length==0||keys.ContainsKey(item.Key))throw new IOException("목데이터 경로를 확인하세요.");var source=BackupPaths.Child(mock,item.Folder);BackupPaths.NoLinks(source);if(!item.Missing&&!Directory.Exists(source))throw new IOException("목데이터 폴더가 없습니다: "+item.Folder);
            var id=LibraryStore.ReadIdentity(source)?.GameId??Guid.NewGuid().ToString("N");var p=new Package{Id=id,IsReal=true,Registered=item.Added&&!item.Content,IsContent=item.Content,Source=source,DetectedRoot=item.Executable.Length>0?Path.GetDirectoryName(BackupPaths.Child(source,item.Executable))!:source,SearchRoot=mock,Folder=Path.GetFileName(source),Name=Path.GetFileName(source),Alias=item.Alias,Color="#527BDC"};
            if(!item.Content&&item.Added){p.Genres=[tags.Tags[all.Count%2].Name];p.GenreIds=[tags.Tags[all.Count%2].Id];p.Categories=["테스트용"];p.CategoryIds=[tags.Tags[2].Id];}
            foreach(var relative in item.InternalResources){if(!BackupPaths.Relative(relative))throw new IOException("자료 경로를 확인하세요.");p.Contents.Add(new(){Path=relative,Role="related",Alias=relative=="Comics"?"디지털 만화":"가이드"});}
            all.Add(p);keys.Add(item.Key,p);var play=new GamePlay{Id=id,Executable=item.Executable,Favorite=item.Key=="NightTrain"};
            if(item.Image.Length>0){var image=BackupPaths.Child(mock,item.Image);BackupPaths.NoLinks(image);var relative=".indierun-art/"+id+Path.GetExtension(image);var target=BackupPaths.Child(library,relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(image,target,false);play.Image=relative;}players.Games.Add(play);
        }
        foreach(var item in manifest.Games)
        {
            var p=keys[item.Key];foreach(var key in item.Resources){var asset=keys[key];p.RelatedFolders.Add(new(){Id=asset.Id,Source=asset.Source,Name=asset.Folder,Alias=asset.Alias});}
            foreach(var key in item.Separate){var child=keys[key];var relative=Path.GetRelativePath(p.Source,child.Source);if(!BackupPaths.Relative(relative))throw new IOException("별도 게임 범위를 확인하세요.");p.Contents.Add(new(){Path=relative,Role="game",Id=child.Id});p.OmittedFolders.Add(relative);child.CompositionParentId=p.Id;}
        }
        ContentLinks.Normalize(all);var scan=GameScanner.Scan(mock);
        foreach(var p in all.Where(p=>!p.IsRelatedContent&&p.Registered==true))new LibraryStore(library).Register(p,tags.Tags,scan);
        scan=GameScanner.Scan(mock);foreach(var p in all.Where(p=>Directory.Exists(p.Source))){var bytes=scan.Files.Where(f=>p.Includes(f.Path)).Sum(f=>f.Length);p.Gb=bytes/(1024d*1024*1024);p.Size=bytes>=1024?$"{bytes/1024d:0.#} KB":$"{bytes} B";}
        var plan=new WorkspaceDocument{SearchRoot=mock,SearchLocations=[new(){Path=mock}],Destination=library,BackupRoot=backups,SelectedId=keys[manifest.Games.First(g=>!g.Content).Key].Id,IdentityNoticeAccepted=false,Packages=all,Tags=tags.Tags,Checkpoints=all.Where(p=>Directory.Exists(p.Source)).Select(p=>WorkspaceReconciler.Capture(p,scan)).ToList()};
        new PlayerStore(library).Save(players);new TagCatalogStore(Path.Combine(data,"tags.json")).Save(tags);new WorkspaceStore(workspace).Save(plan);BackupJson.Save(marker,new{Root=kit,Version=1});
    }
    static readonly HashSet<string> PathKeys=["Original","Current","Root","Source","DetectedRoot","SearchRoot","Path","Destination","BackupRoot","LastKnownPath","SourceRoot","OldPath","NewPath","Temporary","Anchor","OldRoot","NewRoot","TargetContainer"];
    static void Rebase(string kit,string data,string old)
    {
        void Visit(JsonNode? node)
        {
            if(node is JsonObject obj)foreach(var pair in obj.ToList()){if(PathKeys.Contains(pair.Key)&&pair.Value is JsonValue value&&value.TryGetValue<string>(out var s)&&Path.IsPathFullyQualified(s)&&GameScanner.Within(s,old))obj[pair.Key]=Path.GetFullPath(Path.Combine(kit,Path.GetRelativePath(old,s)));else Visit(pair.Value);}
            else if(node is JsonArray array)foreach(var item in array)Visit(item);
        }
        foreach(var directory in new[]{data,Path.Combine(kit,"Library"),Path.Combine(kit,"Backups")})
        {
            if(!Directory.Exists(directory))continue;BackupPaths.NoLinks(directory);
            foreach(var path in Directory.EnumerateFiles(directory,"*.json",SearchOption.AllDirectories))
            {BackupPaths.NoLinks(path);var json=JsonNode.Parse(File.ReadAllText(path));Visit(json);var temp=path+".relocating";BackupPaths.NoLinks(temp);File.WriteAllText(temp,json!.ToJsonString(new(){WriteIndented=true}));File.Move(temp,path,true);}
        }
    }
}



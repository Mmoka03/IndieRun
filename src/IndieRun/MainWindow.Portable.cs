using System.IO;
using System.Windows;
namespace IndieRun;
public partial class MainWindow
{
    readonly Dictionary<string,string> portableErrors=[];
    void SyncPortableMetadata()
    {
        if(backupBusy||scanning||workspaceBlocked||playerRoot!=destination||playerError.Length>0)return;
        foreach(var p in packages.Where(p=>p.PortableMetadata&&!p.Excluded&&p.ReviewState.Length==0&&Directory.Exists(p.Source)))
        {
            try{if(LibraryStore.ReadIdentity(p.Source)?.GameId!=p.Id)throw new IOException("식별 정보를 확인하세요.");PortableStore.Export(Path.GetDirectoryName(p.Source)!,Path.GetFileName(p.Source),p,PlayFor(p),catalog,destination);portableErrors.Remove(p.Id);}
            catch(Exception ex) when(LibraryFault(ex)){portableErrors[p.Id]=ex.Message;}
        }
        if(portableErrors.Count>0){SaveStatus.Content="정보 파일 저장 확인";SaveStatus.ToolTip="이동 위치의 정보 파일을 갱신하지 못했습니다. 해당 게임을 선택해 다시 시도하세요.";}
    }
    void ImportPortableGames(HashSet<string> known,ScanSnapshot scan)
    {
        if(workspaceBlocked||tagLoadError.Length>0||File.Exists(Edits().Pending)||ScopeChanges().HasPending)return;
        foreach(var p in packages.Where(p=>!known.Contains(p.Id)&&!p.Excluded).ToList())
        {
            try
            {
                var record=PortableStore.Read(p.Source);if(record==null)continue;
                if(libraryDocument.Games.Any(g=>g.Id==p.Id)){p.PortableMetadata=true;continue;}
                var tags=CopyCatalog();var draft=Clone(p);var play=PortableStore.Import(record,draft,tags,destination);var players=new PlayerStore(destination).Load();
                // Existing launch settings belong to the user, even when the folder is newly discovered.
                if(!players.Games.Any(g=>g.Id==p.Id))players.Games.Add(play);
                var plan=Clone(CaptureWorkspace());var index=plan.Packages.FindIndex(q=>q.Id==p.Id);plan.Packages[index]=draft;plan.Tags=tags.Tags;
                Edits().Commit(new(){Plan=plan,Tags=tags,Play=players,EditedIds=[p.Id],ImportedIds=[p.Id]});
                p.ContentOwnerId=draft.ContentOwnerId;p.CompositionParentId=draft.CompositionParentId;p.Contents=draft.Contents;p.RelatedFolders=draft.RelatedFolders;p.Alias=draft.Alias;p.Genres=draft.Genres;p.Categories=draft.Categories;p.GenreIds=draft.GenreIds;p.CategoryIds=draft.CategoryIds;p.Registered=true;p.PortableMetadata=true;catalog=tags;libraryDocument=Library().Load();playerDocument=players;playerRoot=destination;portableErrors.Remove(p.Id);
            }
            catch(Exception ex) when(LibraryFault(ex)){portableErrors[p.Id]=ex.Message;if(File.Exists(Edits().Pending)){workspaceBlocked=true;break;}}
        }
    }
    void RetryPortable_Click(object sender,RoutedEventArgs e)
    {
        if(!HasSelection||backupBusy||running||scanning||workspaceBlocked)return;
        try
        {
            if(selected.PortableMetadata){SyncPortableMetadata();if(portableErrors.ContainsKey(selected.Id))throw new IOException(portableErrors[selected.Id]);}
            else{ImportPortableGames(new HashSet<string>(packages.Where(p=>p.Id!=selected.Id).Select(p=>p.Id)),snapshot!);if(portableErrors.ContainsKey(selected.Id))throw new IOException(portableErrors[selected.Id]);}
            FlushWorkspace();Render();Say("게임 정보 파일을 확인했습니다.");
        }
        catch(Exception ex) when(LibraryFault(ex)){ErrorInfo("게임 정보 파일","위치와 접근 권한을 확인한 뒤 다시 시도하세요. 기존 게임과 설정은 유지됩니다.",ex.Message);}
    }
}

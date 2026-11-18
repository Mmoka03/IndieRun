using System.IO;
namespace IndieRun;
public partial class MainWindow
{
    async Task AssignCopyIdentityAsync(string folder,string expectedId)
    {
        if(scanning||running||backupBusy)return;
        if(workspaceBlocked||workspaceLock==null||tagLoadError.Length>0||playerError.Length>0||File.Exists(Edits().Pending)||ScopeChanges().HasPending||RenamePending){ShowWorkspaceSaveIssue();return;}
        if(!FlushWorkspace())return;
        var known=identityInventory.Locations.GetValueOrDefault(expectedId)?.ToArray()??[];var allKnown=identityInventory.Locations.Values.SelectMany(paths=>paths).Concat(identityInventory.Problems.Select(p=>p.Path)).ToArray();var plan=Clone(CaptureWorkspace());var tags=CopyCatalog();var play=Clone(playerDocument);var library=Clone(libraryDocument);
        scanning=true;IsEnabled=false;saveTimer.Stop();bool committed=false;
        try
        {
            RequireUserGameFolder(folder);var bundle=await Task.Run(()=>CopyIdentity.Prepare(plan,tags,play,library,folder,expectedId,known));
            await Task.Run(()=>Edits().Commit(bundle));committed=true;ApplyWorkspace(bundle.Plan);catalog=bundle.Tags;playerDocument=bundle.Play;playerRoot=destination;libraryDocument=Library().Load();history.Clear();lastSavedSignature=null;
            var previous=snapshot;var refreshed=await Task.Run(()=>
            {
                var fresh=GameScanner.Scan(folder);var merged=new ScanSnapshot{Root=source,IsPartial=previous==null||previous.IsPartial};
                if(previous!=null)
                {
                    merged.SearchRoots.AddRange(previous.SearchRoots);merged.Files.AddRange(previous.Files.Where(f=>!GameScanner.Within(f.Path,folder)));merged.Directories.AddRange(previous.Directories.Where(d=>!GameScanner.Within(d,folder)));merged.UnreadablePaths.AddRange(previous.UnreadablePaths.Where(d=>!GameScanner.Within(d,folder)));merged.InactivePaths.AddRange(previous.InactivePaths.Where(d=>!GameScanner.Within(folder,d)));merged.OfflineRoots.AddRange(previous.OfflineRoots.Where(d=>!GameScanner.Within(folder,d)));merged.Issues.AddRange(previous.Issues);
                }
                if(!merged.SearchRoots.Any(root=>GameScanner.Within(folder,root)))merged.SearchRoots.Add(folder);
                merged.Files.AddRange(fresh.Files);merged.Directories.AddRange(fresh.Directories);merged.UnreadablePaths.AddRange(fresh.UnreadablePaths);merged.Issues.AddRange(fresh.Issues);merged.PrepareIndex();
                var identities=LibraryStore.Inspect(merged);IdentityProbe.RefreshKnown(identities,allKnown.Concat(libraryDocument.Games.Select(g=>g.LastKnownPath)).Where(Path.IsPathFullyQualified),[folder]);return(merged,identities);
            });
            snapshot=refreshed.merged;identityInventory=refreshed.identities;Library().Reconcile(libraryDocument,identityInventory,snapshot);
            foreach(var p in packages.Where(p=>!p.Excluded))
            {if(snapshot.ContainsDirectory(p.Source)){WorkspaceReconciler.Check(p,checkpoints.Find(c=>c.PackageId==p.Id),snapshot);IdentityReconciler.Flag(p,identityInventory,libraryDocument);}else{p.ReviewState="cached";p.ReviewReason="폴더 작업 전에 다시 검색하세요.";}}
            Say("복사본을 별도 게임으로 등록했습니다. 게임 폴더는 이동하지 않았습니다.");
        }
        catch(Exception ex)when(LibraryFault(ex))
        {
            var pending=File.Exists(Edits().Pending);if(pending)workspaceBlocked=true;
            if(pending&&Edits().CanCancelUnappliedCopyIdentity())
            {
                var choice=Choose("복사본 등록 중단","게임 ID와 등록 정보는 아직 바뀌지 않았습니다. 등록을 취소한 뒤 폴더 구성을 다시 확인할 수 있습니다.",[new("close","닫기"),new("cancel","등록 취소",true)],"close",ex.Message,DialogTone.Warning);
                if(choice=="cancel")
                {
                    try{if(Edits().CancelUnappliedCopyIdentity()){workspaceBlocked=false;Say("복사본 등록을 취소했습니다. 게임 ID와 파일은 유지됩니다.");return;}}
                    catch(Exception cancelError)when(LibraryFault(cancelError)){ErrorInfo("등록 취소 확인","작업 기록을 보관하지 못했습니다. 기록과 게임 파일은 유지됩니다.",cancelError.Message);return;}
                }
                Say("복사본 등록 중단 · 작업 기록 확인 필요");return;
            }
            ErrorInfo("복사본 등록",pending?"복사본 등록을 완료하지 못했습니다. 저장된 작업 기록을 복구하면 이어서 완료할 수 있습니다.":committed?"복사본 등록은 저장했지만 화면을 갱신하지 못했습니다. 다시 확인해 주세요.":"복사본을 등록하지 못했습니다. 게임 ID와 파일은 변경하지 않았습니다.",ex.Message);
            Say(pending?"복사본 등록 복구 필요":committed?"복사본 등록됨 · 화면 갱신 필요":"복사본을 등록하지 못했습니다.");
        }
        finally{scanning=false;IsEnabled=true;RenderLibrary();}
    }
}

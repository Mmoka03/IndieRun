using System.IO;
using System.Windows;
namespace IndieRun;
public partial class MainWindow
{
    readonly HashSet<string> recentlyRegisteredIds=[];
    readonly Dictionary<string,FolderCheckpoint> verifiedTransferChecks=[];
    Task<bool> VerifyTransferSelectionAsync(List<Package> requested)=>VerifyCurrentSelectionAsync(requested,true);
    async Task<bool> VerifyCurrentSelectionAsync(List<Package> requested,bool confirmParents)
    {
        if(confirmParents&&!ConfirmParentScopes(requested))return false;
        var targets=confirmParents?new MoveSelection(requested,packages).Available:requested.ToList();
        try{foreach(var p in targets)RequireUserGameFolder(p.Source);}catch(IOException ex){ErrorInfo("폴더 확인","프로그램이나 백업 보관 위치는 게임으로 사용할 수 없습니다.",ex.Message);return false;}
        var roots=SearchLocationRules.MinimalRoots(targets.Select(p=>p.Source));
        var previous=snapshot;var baselines=Clone(checkpoints);var fresh=Clone(targets);var knownIdentityPaths=identityInventory.Locations.Values.SelectMany(paths=>paths).Concat(packages.Where(p=>p.Registered!=false).Select(p=>p.Source)).Concat(libraryDocument.Games.Select(g=>g.LastKnownPath)).Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach(var p in targets.Where(p=>p.IsRelatedContent&&!baselines.Any(c=>c.PackageId==p.Id)))if(previous?.ContainsDirectory(p.Source)==true)baselines.Add(WorkspaceReconciler.Capture(p,previous));
        running=true;scanning=true;scanCancellation=new();var token=scanCancellation.Token;
        Workbench.IsEnabled=FooterActions.IsEnabled=PlayerPanel.IsEnabled=false;SourceButton.IsEnabled=false;RescanButton.Content="확인 중지";
        var navs=new[]{PlayerNav,OrganizeNav,TagsNav,LibraryNav,JobsNav};foreach(var nav in navs)nav.IsEnabled=false;
        Say("선택한 게임 폴더를 확인하고 있습니다…");
        try
        {
            var result=await Task.Run(()=>
            {
                var merged=new ScanSnapshot{Root=source,IsPartial=previous==null||previous.IsPartial};
                merged.SearchRoots.AddRange((previous?.SearchRoots??[]).Concat(roots).Distinct(StringComparer.OrdinalIgnoreCase));
                if(previous!=null)
                {
                    merged.Files.AddRange(previous.Files.Where(f=>!roots.Any(root=>GameScanner.Within(f.Path,root))));
                    merged.Directories.AddRange(previous.Directories.Where(d=>!roots.Any(root=>GameScanner.Within(d,root))));
                    merged.UnreadablePaths.AddRange(previous.UnreadablePaths.Where(p=>!roots.Any(root=>GameScanner.Within(p,root))));
                }
                foreach(var root in roots)
                {
                    token.ThrowIfCancellationRequested();
                    if(!Directory.Exists(root))continue;
                    var part=GameScanner.Scan(root,cancellation:token,budget:new ScanBudget());merged.Files.AddRange(part.Files);merged.Directories.AddRange(part.Directories);merged.UnreadablePaths.AddRange(part.UnreadablePaths);
                }
                merged.PrepareIndex(token);var identities=LibraryStore.Inspect(merged,token);IdentityProbe.RefreshKnown(identities,knownIdentityPaths,roots,token);
                foreach(var p in fresh){token.ThrowIfCancellationRequested();WorkspaceReconciler.Check(p,baselines.Find(c=>c.PackageId==p.Id),merged);IdentityReconciler.Flag(p,identities,libraryDocument);}
                return(merged,identities);
            });
            token.ThrowIfCancellationRequested();snapshot=result.merged;identityInventory=result.identities;
            foreach(var verified in fresh)
            {
                var live=packages.Find(p=>p.Id==verified.Id)??requested.Find(p=>p.Id==verified.Id);if(live==null)continue;
                live.ReviewState=verified.ReviewState;live.ReviewReason=verified.ReviewReason;live.ReviewDetails=verified.ReviewDetails;live.AddedExecutables=verified.AddedExecutables;
                foreach(var selectedCopy in requested.Where(p=>p.Id==verified.Id)){selectedCopy.ReviewState=verified.ReviewState;selectedCopy.ReviewReason=verified.ReviewReason;selectedCopy.ReviewDetails=verified.ReviewDetails;selectedCopy.AddedExecutables=verified.AddedExecutables;}
            }
            var blockedIdentity=fresh.FirstOrDefault(p=>p.ReviewState is "identity" or "relocation");if(blockedIdentity!=null){ShowTransferIdentityIssue(blockedIdentity);return false;}
            var changed=fresh.Where(p=>p.ReviewState=="changed").ToList();
            if(changed.Count>0)
            {
                if(!Confirm("폴더 변경 확인",$"선택한 게임 {changed.Count}개의 파일 구성이 달라졌습니다. 현재 상태를 폴더 이동·백업 대상으로 사용할까요?","현재 상태로 진행",string.Join("\n\n",changed.Select(p=>PlayerRules.Title(p)+"\n"+string.Join("\n",p.ReviewDetails.Take(30))))))return false;
                foreach(var p in changed){if(packages.Any(q=>q.Id==p.Id)){checkpoints.RemoveAll(c=>c.PackageId==p.Id);checkpoints.Add(WorkspaceReconciler.Capture(p,snapshot));}var live=packages.Find(q=>q.Id==p.Id)??requested.Find(q=>q.Id==p.Id)??p;live.ReviewState="";live.ReviewReason="";live.ReviewDetails.Clear();live.AddedExecutables.Clear();foreach(var selectedCopy in requested.Where(q=>q.Id==p.Id)){selectedCopy.ReviewState="";selectedCopy.ReviewReason="";selectedCopy.ReviewDetails=[];selectedCopy.AddedExecutables=[];}}
            }
            foreach(var p in requested.Where(p=>p.ScopeReviewRequired).ToList()){var live=packages.Find(q=>q.Id==p.Id)??p;if(!ConfirmStoredScope(live))return false;p.ScopeReviewRequired=false;p.ReviewState=live.ReviewState;p.ReviewReason=live.ReviewReason;}
            var blocked=requested.FirstOrDefault(p=>p.ReviewState.Length>0);
            if(blocked!=null){Info("폴더 확인",blocked.ReviewReason.Length>0?blocked.ReviewReason:"게임 폴더의 위치와 상태를 먼저 확인하세요.");return false;}
            foreach(var p in fresh)verifiedTransferChecks[p.Id]=WorkspaceReconciler.Capture(p,snapshot);
            return true;
        }
        catch(OperationCanceledException){Say("폴더 확인을 중지했습니다. 파일은 변경하지 않았습니다.");return false;}
        catch(Exception ex) when(LibraryFault(ex)){ErrorInfo("폴더 확인","선택한 게임 폴더를 확인하지 못했습니다. 파일은 변경하지 않았습니다.",ex.Message);return false;}
        finally{running=false;scanning=false;scanCancellation.Dispose();scanCancellation=null;Workbench.IsEnabled=FooterActions.IsEnabled=PlayerPanel.IsEnabled=true;SourceButton.IsEnabled=true;RescanButton.Content="다시 검색";foreach(var nav in navs)nav.IsEnabled=true;RenderPlan();}
    }
    async Task OpenTransferAsync(List<Package> list)
    {
        if(list.Count==0||running||backupBusy||scanning||scopeDraft!=null)return;
        var locationIssue=list.FirstOrDefault(p=>moveLocationIssues.ContainsKey(p.Id));if(locationIssue!=null){ShowMoveLocationIssue(locationIssue);return;}
        if(workspaceBlocked||workspaceLock==null||File.Exists(Edits().Pending)||ScopeChanges().HasPending||RenamePending){ShowWorkspaceSaveIssue();return;}
        if(!await VerifyTransferSelectionAsync(list))return;
        OpenMovePlan(list,destination);
    }
}

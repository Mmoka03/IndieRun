namespace IndieRun;

// Display state only. Durable backup/move records remain the source of recovery actions.
public sealed record OperationItem(string Id,string Title)
{
    public string State {get;set;}="Pending";
    public string? Error {get;set;}
}
public sealed class OperationStatus(string name,IEnumerable<OperationItem> items)
{
    public bool WithoutBackup {get;init;}
    public string Name {get;}=name;
    public List<OperationItem> Items {get;}=items.ToList();
    public string CurrentId {get;private set;}="";
    public string Stage {get;set;}="준비 중";
    public bool Finished {get;private set;}
    public bool Stopped {get;private set;}
    public int Done=>Items.Count(i=>i.State=="Completed");
    public int Failed=>Items.Count(i=>i.State=="Failed");
    public int Restored=>Items.Count(i=>i.State=="RolledBack");
    public int Interrupted=>Items.Count(i=>i.State=="Interrupted");
    public int Pending=>Items.Count(i=>i.State=="Pending");
    public int SavePending=>Items.Count(i=>i.State=="SavePending");
    public int LocationCheck=>Items.Count(i=>i.State=="LocationCheck");
    public bool NeedsAttention=>Failed>0||SavePending>0||LocationCheck>0||Interrupted>0||Pending>0;
    public string Heading=>!Finished?Name+" 진행 중":Failed>0||SavePending>0||LocationCheck>0?$"{Name} · 확인 필요":Stopped||Interrupted>0?$"{Name} 중단":Restored>0?$"{Name} 되돌림":Pending>0||Items.Count==0?$"{Name} · 미처리 항목 확인":$"{Name} 완료";
    public string Counts=>$"완료 {Done}"+(Failed>0?$" · 실패 {Failed}":"")+(SavePending>0?$" · 저장 확인 {SavePending}":"")+(LocationCheck>0?$" · 위치 확인 {LocationCheck}":"")+(Restored>0?$" · 되돌림 {Restored}":"")+(Interrupted>0?$" · 중단 {Interrupted}":"")+(Pending>0?$" · {(Finished?"미처리":"남음")} {Pending}":"");
    public string Current=>Items.Find(i=>i.Id==CurrentId)?.Title??"";
    public string Position=>CurrentId.Length==0?"":$"{Items.FindIndex(i=>i.Id==CurrentId)+1}/{Items.Count}";
    public void Begin(string id){CurrentId=id;}
    public void Mark(string id,string state,string? error=null)
    {var item=Items.Single(i=>i.Id==id);item.State=state;item.Error=error;}
    public void Finish(bool stopped)
    {Stopped=stopped;Finished=true;}
    public static string ItemLabel(string state)=>state switch {"Completed"=>"완료","SavePending"=>"파일 처리 완료 · 설정 저장 확인","LocationCheck"=>"이동 위치 확인","Failed"=>"실패 · 확인 필요","RolledBack"=>"원래 위치로 되돌림","Interrupted"=>"중단됨",_=>"미처리"};
    public static string ProgressLabel(string stage)=>stage switch
    {
        "Copying"=>"백업 중","Verifying"=>"백업 확인 중","Moving"=>"이동 중",
        "MoveChecking"=>"이동 준비 확인 중","MoveVerifying"=>"이동 결과 확인 중",
        "RollingBack"=>"원래 위치로 되돌리는 중","Restoring"=>"별도 위치에 복원 중",_=>"준비 중"
    };
}

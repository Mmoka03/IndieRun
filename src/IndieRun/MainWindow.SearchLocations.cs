using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Microsoft.Win32;

namespace IndieRun;

public partial class MainWindow
{
    List<SearchLocation> searchLocations=[];
    void UpdateSearchLocationLabel()
    {
        ScanSourceLabel.Text=searchLocations.Count==0?"등록된 위치 없음":$"위치 {searchLocations.Count}개 · 검색 {searchLocations.Count(l=>l.Enabled)}개";
        ScanSourceLabel.ToolTip=string.Join('\n',searchLocations.Select(l=>(l.Enabled?"검색 켜짐 · ":"검색 꺼짐 · ")+l.Path));
    }
    async Task ManageSearchLocationsAsync()
    {
        if(backupBusy||scanning)return;
        var draft=Clone(searchLocations);var win=Modal("탐색 위치",820,600);var layout=new DockPanel {Margin=new Thickness(28)};
        var heading=new DockPanel {Margin=new Thickness(0,0,0,20)};
        var rows=new StackPanel();var message=WorkText("",12,"Warning.Text");
        var add=Btn("＋ 위치 추가",()=>
        {
            var picker=new OpenFolderDialog {Title="게임을 찾을 폴더 선택"};if(picker.ShowDialog(win)!=true)return;
            try
            {
                if((File.GetAttributes(picker.FolderName)&FileAttributes.ReparsePoint)!=0)throw new IOException("연결 폴더는 탐색 위치로 사용할 수 없습니다.");
                var blocked=new[]{AppContext.BaseDirectory,backupStorageRoot,Path.Combine(projectRoot,"src")};
                if(blocked.Any(p=>GameScanner.Within(picker.FolderName,p)))throw new IOException("프로그램·소스·백업 폴더는 탐색 위치에서 제외됩니다.");
                var nested=draft.Any(l=>GameScanner.Within(l.Path,picker.FolderName));
                if(!SearchLocationRules.Add(draft,picker.FolderName)){message.Text="이미 등록된 상위 위치에 포함됩니다.";return;}
                message.Text=nested?"겹치는 하위 위치를 이 위치로 합쳤습니다.":"";RenderLocations();
            }
            catch(Exception ex) when(LibraryFault(ex)){message.Text=ex.Message;}
        });DockPanel.SetDock(add,Dock.Right);heading.Children.Add(add);heading.Children.Add(WorkText("탐색 위치",23,"Text.Primary"));DockPanel.SetDock(heading,Dock.Top);layout.Children.Add(heading);
        bool applied=false;
        var footer=new StackPanel {Margin=new Thickness(0,16,0,0)};footer.Children.Add(message);
        footer.Children.Add(WorkText("검색을 끄거나 등록을 해제해도 게임과 분류 정보는 유지됩니다.",12,"Text.Muted"));
        var actions=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
        var cancel=Btn("취소",()=>win.Close());cancel.IsCancel=true;actions.Children.Add(cancel);
        actions.Children.Add(Btn("저장 후 검색",()=>
        {
            if(!FlushWorkspace()){message.Text="현재 작업을 저장하지 못했습니다. 저장 상태를 확인하세요.";return;}
            var old=searchLocations;var oldSource=source;searchLocations=Clone(draft);source=searchLocations.FirstOrDefault(l=>l.Enabled)?.Path??searchLocations.FirstOrDefault()?.Path??projectRoot;
            if(!FlushWorkspace()){searchLocations=old;source=oldSource;message.Text="탐색 위치를 저장하지 못했습니다. 다시 시도하세요.";return;}
            history.Clear();applied=true;win.Close();
        },true));footer.Children.Add(actions);DockPanel.SetDock(footer,Dock.Bottom);layout.Children.Add(footer);
        void RenderLocations()
        {
            rows.Children.Clear();
            if(draft.Count==0){rows.Children.Add(WorkText("게임을 모아 둔 폴더를 추가하세요.",14));return;}
            foreach(var location in draft)
            {
                var row=new Grid();row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
                var info=new StackPanel {Margin=new Thickness(0,0,16,0)};info.Children.Add(WorkText(Path.GetFileName(location.Path.TrimEnd('\\')) is {Length:>0} name?name:location.Path,16,"Text.Primary"));
                var path=WorkText(location.Path,12,"Text.Muted");path.TextTrimming=TextTrimming.CharacterEllipsis;path.TextWrapping=TextWrapping.NoWrap;path.ToolTip=location.Path;info.Children.Add(path);
                info.Children.Add(WorkText(Directory.Exists(location.Path)?"연결됨":"연결 안 됨 · 연결 후 다시 검색하세요",12,Directory.Exists(location.Path)?"Text.Secondary":"Warning.Text"));row.Children.Add(info);
                var tools=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(tools,1);
                var toggle=Btn(location.Enabled?"검색 켜짐":"검색 꺼짐",()=>{location.Enabled=!location.Enabled;RenderLocations();},location.Enabled);AutomationProperties.SetName(toggle,location.Path+" 검색 "+(location.Enabled?"켜짐":"꺼짐"));tools.Children.Add(toggle);
                tools.Children.Add(Btn("등록 해제",()=>{draft.Remove(location);RenderLocations();}));row.Children.Add(tools);rows.Children.Add(WorkCard(row));
            }
        }
        RenderLocations();layout.Children.Add(new ScrollViewer {Content=rows});win.Content=layout;win.ShowDialog();
        if(applied){UpdateSearchLocationLabel();await RefreshWorkspaceAsync(source,false,false);}
    }
}

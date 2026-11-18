using IndieRun;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;

public static class Program
{
    static readonly DialogChoice[] Confirm=[new("cancel","취소"),new("delete","태그 삭제",true,true)];
    static readonly DialogChoice[] Move=[new("continue","실패한 패키지는 건너뛰고 계속"),new("stop","이동 중단 · 완료한 이동 유지",true),new("undo","이동 중단 · 완료한 이동도 되돌리기")];
    static string LongError=>string.Join('\n',Enumerable.Range(1,250).Select(i=>$"{i}: C:\\Games\\VeryLongFolderName_日本語\\Data\\LockedFile.dll · 다른 프로그램에서 사용 중입니다."));
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    [STAThread]
    public static int Main(string[] args)
    {
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var styles=XDocument.Load(Path.Combine(AppContext.BaseDirectory,"styles-source.xaml")).Root!.Elements().Single().Elements().Single();
        app.Resources=(ResourceDictionary)XamlReader.Parse(styles.ToString(),new ParserContext {BaseUri=new Uri(AppContext.BaseDirectory)});
        var results=new List<object>();
        if(args.Length==0)
        {
            foreach(var sample in new[]{"long","delete","move","info"})
            {
                var dialog=sample switch
                {
                    "long"=>new AppDialog(null,"이동을 완료하지 못했습니다","파일을 이동하지 못했습니다. 오류 내용을 확인한 뒤 다시 시도하세요.",[new("close","닫기",true)],"close",LongError,DialogTone.Warning),
                    "delete"=>new AppDialog(null,"태그를 삭제할까요?","‘"+new string('가',40)+"’ 태그가 게임 128개에서 제거됩니다.\n찾을 수 없거나 목록에서 제외한 게임도 포함합니다.",Confirm,"cancel",tone:DialogTone.Destructive),
                    "move"=>new AppDialog(null,"이동을 완료하지 못했습니다","완료한 패키지 3개 · 백업은 유지됩니다.",Move,"stop",LongError,DialogTone.Warning,stacked:true),
                    _=>new AppDialog(null,"저장 확인","변경 내용을 저장하지 못했습니다.",[new("close","확인",true)],"close","테스트용 오류 내용",DialogTone.Warning)
                };
                dialog.ShowInTaskbar=true;app.MainWindow=dialog;var result=dialog.Open();results.Add(new{sample,result});
            }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"dialog-preview-results.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        int failures=0;
        void Test(string name,Action action){try{action();results.Add(new{name,status="PASS"});Console.WriteLine("PASS: "+name);}catch(Exception ex){failures++;results.Add(new{name,status="FAIL",error=ex.ToString()});Console.WriteLine("FAIL: "+name+" · "+ex.Message);}}
        foreach(var width in new[]{440d,600d})foreach(var textScale in new[]{1d,1.5d})
        {
            Test($"long-details-footer-visible-{width}-{textScale}",()=>
            {
                var d=new AppDialog(null,"파일 이동 확인",LongError,Move,"stop",LongError,DialogTone.Warning,stacked:true);d.FontSize=13*textScale;
                var details=((Panel)d.BodyScroll.Content).Children.OfType<Expander>().Single();details.IsExpanded=true;
                d.Layout.Measure(new Size(width,510));d.Layout.Arrange(new Rect(0,0,width,510));d.Layout.UpdateLayout();
                var footer=d.ActionPanel.TranslatePoint(new Point(),d.Layout);Check(footer.Y>=0&&footer.Y+d.ActionPanel.ActualHeight<=d.Layout.ActualHeight+.5,"Footer clipped");Check(d.BodyScroll.ScrollableHeight>0,"Long content cannot scroll");
                Check(d.ActionButtons.Count==3&&d.ActionButtons.Single(b=>b.IsDefault).IsCancel,"Unsafe default");d.Close();
            });
            Test($"long-tag-name-wraps-{width}-{textScale}",()=>
            {
                var d=new AppDialog(null,"태그를 삭제할까요?",new string('가',40)+" 태그가 게임 100개에서 제거됩니다.",Confirm,"cancel",tone:DialogTone.Destructive);d.FontSize=13*textScale;
                d.Layout.Measure(new Size(width,310));d.Layout.Arrange(new Rect(0,0,width,310));d.Layout.UpdateLayout();var footer=d.ActionPanel.TranslatePoint(new Point(),d.Layout);Check(footer.Y+d.ActionPanel.ActualHeight<=d.Layout.ActualHeight+.5,"Delete footer clipped");Check(d.Result=="cancel"&&d.ActionButtons.Single(b=>b.IsDefault).IsCancel,"Delete selected by default");d.Close();
            });
        }
        Test("invalid-dialog-configuration-refused",()=>{try{_ = new AppDialog(null,"test","",Confirm,"unknown");}catch(ArgumentException){return;}throw new Exception("Invalid cancel accepted");});
        Test("readable-details-copy-and-theme-resources",()=>{var d=new AppDialog(null,"상세 정보","",Confirm,"cancel",LongError);var exp=((Panel)d.BodyScroll.Content).Children.OfType<Expander>().Single();Check(exp.Content is TextBox box&&box.IsReadOnly&&box.Text==LongError,"Details not selectable");Check(app.TryFindResource("Danger.Fill")!=null&&app.TryFindResource("Warning.Surface")!=null,"Missing theme roles");d.Close();});
        File.WriteAllText(Path.GetFullPath(args[0]),JsonSerializer.Serialize(new{total=results.Count,passed=results.Count-failures,failed=failures,results},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"RESULT: {results.Count-failures}/{results.Count}");return failures==0?0:1;
    }
}

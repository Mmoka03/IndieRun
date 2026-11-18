using System.Windows;
using System.Windows.Controls;
namespace IndieRun;
public partial class MainWindow
{
    const int ResultsPerPage=60;
    readonly Dictionary<string,(string Query,int Page)> resultPages=[];
    IEnumerable<T> PageItems<T>(List<T> items,string id,string query,Panel host,Action refresh)
    {
        var old=resultPages.GetValueOrDefault(id);var page=old.Query==query?old.Page:0;
        page=Math.Clamp(page,0,Math.Max(0,(items.Count-1)/ResultsPerPage));resultPages[id]=(query,page);
        if(items.Count>ResultsPerPage)
        {
            var row=new WrapPanel{Margin=new Thickness(0,8,0,12)};
            var back=Btn("이전",()=>{resultPages[id]=(query,page-1);refresh();});back.IsEnabled=page>0;row.Children.Add(back);
            row.Children.Add(new TextBlock{Text=$"{page*ResultsPerPage+1}–{Math.Min(items.Count,(page+1)*ResultsPerPage)} / {items.Count}",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,0,12,0),Foreground=ThemeBrush("Text.Secondary")});
            var next=Btn("다음",()=>{resultPages[id]=(query,page+1);refresh();});next.IsEnabled=(page+1)*ResultsPerPage<items.Count;row.Children.Add(next);host.Children.Add(row);
        }
        return items.Skip(page*ResultsPerPage).Take(ResultsPerPage);
    }
}

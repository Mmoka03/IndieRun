using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace IndieRun;

public static class ReadOnlyPreview
{
    public static bool IsText(string path) => new[]{".txt",".md",".ini",".log",".htm",".html",".json",".csv"}.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsImage(string path) => new[]{".png",".jpg",".jpeg",".bmp",".gif"}.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static (string Text,string Encoding,bool Truncated) ReadText(string path,string mode="자동")
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const int limit=256*1024;
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        var data=new byte[(int)Math.Min(stream.Length,limit)];var count=0;
        while(count<data.Length){var read=stream.Read(data,count,data.Length-count);if(read==0)break;count+=read;}
        var truncated=stream.Length>limit;
        Encoding encoding;string text;
        if(mode=="일본어 CP932")encoding=Encoding.GetEncoding(932);
        else if(mode=="한국어 CP949")encoding=Encoding.GetEncoding(949);
        else if(mode=="UTF-16 LE")encoding=Encoding.Unicode;
        else if(mode=="UTF-8")encoding=new UTF8Encoding(false,false);
        else if(count>=4&&data[0]==0xFF&&data[1]==0xFE&&data[2]==0&&data[3]==0)encoding=Encoding.UTF32;
        else if(count>=2&&data[0]==0xFF&&data[1]==0xFE)encoding=Encoding.Unicode;
        else if(count>=2&&data[0]==0xFE&&data[1]==0xFF)encoding=Encoding.BigEndianUnicode;
        else
        {
            try
            {
                var strict=new UTF8Encoding(false,true);var chars=new char[strict.GetMaxCharCount(count)];
                var written=strict.GetDecoder().GetChars(data,0,count,chars,0,!truncated);
                text=new string(chars,0,written).TrimStart('\uFEFF');return (PlainText(path,text),"UTF-8",truncated);
            }
            catch(DecoderFallbackException){encoding=Encoding.GetEncoding(932);}
        }
        text=encoding.GetString(data,0,count).TrimStart('\uFEFF');
        return (PlainText(path,text),encoding.WebName,truncated);
    }
    static string PlainText(string path,string text)
    {
        if(Path.GetExtension(path).ToLowerInvariant() is not (".htm" or ".html"))return text;
        // Display HTML as inert text; never host a browser or run embedded script.
        var timeout=TimeSpan.FromMilliseconds(200);
        try
        {
            text=Regex.Replace(text,@"<(script|style)\b[^>]*>[\s\S]*?</\1\s*>","",RegexOptions.IgnoreCase,timeout);
            text=Regex.Replace(text,@"</?(p|div|br|h[1-6]|li|tr)\b[^>]*>","\n",RegexOptions.IgnoreCase,timeout);
            text=Regex.Replace(text,@"<[^>]*>","",RegexOptions.None,timeout);
            return WebUtility.HtmlDecode(text);
        }
        catch(RegexMatchTimeoutException){return text;}
    }
}

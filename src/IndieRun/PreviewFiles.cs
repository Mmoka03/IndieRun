using System.IO;
using System.Windows.Media.Imaging;
namespace IndieRun;
public static class PreviewFiles
{
    public static string? Recommendation(Package p,string path)
    {
        var related=p.RelatedFolders.Any(r=>GameScanner.Within(path,r.Source))||p.Contents.Any(c=>c.Role=="related"&&GameScanner.Within(path,Path.Combine(p.Source,c.Path)));
        var parts=Path.GetRelativePath(p.Source,path).Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);var leaf=Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if(!related&&parts.SkipLast(1).Any(part=>part.Equals("data",StringComparison.OrdinalIgnoreCase)||part.EndsWith("_data",StringComparison.OrdinalIgnoreCase)||part.Equals("cache",StringComparison.OrdinalIgnoreCase)||part.Equals("streamingassets",StringComparison.OrdinalIgnoreCase)))return null;
        if(related)return "지정한 관련 자료";
        if(ReadOnlyPreview.IsText(path)&&(leaf.Contains("readme")||leaf.Contains("manual")||leaf.Contains("説明")||leaf.Contains("읽어")))return parts.Length==1?"최상위 설명 문서":"설명 문서 이름";
        if(ReadOnlyPreview.IsImage(path)&&(leaf.Contains("cover")||leaf.Contains("title")||leaf.Contains("screenshot")||leaf.Contains("표지")))return "표지·화면 이미지 이름";
        if(ReadOnlyPreview.IsImage(path)&&parts.Length==1)return "최상위 이미지";return null;
    }
    public static List<ScannedFile> Recommended(Package p,IEnumerable<ScannedFile> files)=>files.Where(f=>Recommendation(p,f.Path)!=null).Take(6).ToList();
    public static bool InScope(Package p,string path)=>p.Includes(path)||p.RelatedFolders.Any(r=>GameScanner.Within(path,r.Source));
    public static List<ScannedFile> Candidates(Package p,ScanSnapshot scan)=>scan.Files.Where(f=>!f.Path.Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar).Any(part=>part.StartsWith(".indierun",StringComparison.OrdinalIgnoreCase))&&InScope(p,f.Path)&&(ReadOnlyPreview.IsText(f.Path)||ReadOnlyPreview.IsImage(f.Path))).OrderBy(f=>Rank(p,f.Path)).ThenBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).Take(500).ToList();
    static int Rank(Package p,string path)
    {
        var name=Path.GetFileNameWithoutExtension(path).ToLowerInvariant();var depth=Path.GetRelativePath(p.Source,path).Count(c=>c=='\\'||c=='/');
        var readme=name.Contains("readme")||name.Contains("説明")||name.Contains("읽어")||name.Contains("guide");var art=ReadOnlyPreview.IsImage(path)&&(name.Contains("cover")||name.Contains("title")||name.Contains("screenshot"));
        return readme?Math.Min(depth,9):art?10+Math.Min(depth,9):depth==0&&ReadOnlyPreview.IsImage(path)?20:30+Math.Min(depth,100);
    }
    public static BitmapImage Image(string path,int width)
    {BackupPaths.NoLinks(path);using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);if(stream.Length>50L*1024*1024)throw new IOException("이미지는 50 MB 이하로 선택하세요.");return Decode(stream,width);}
    static BitmapImage Decode(Stream stream,int width)
    {var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=width;image.StreamSource=stream;image.EndInit();image.Freeze();return image;}
    public static string CopyArtwork(string path,string library)
    {
        if(!ReadOnlyPreview.IsImage(path))throw new IOException("이미지 파일을 선택하세요.");BackupPaths.NoLinks(path);using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length>50L*1024*1024)throw new IOException("이미지는 50 MB 이하로 선택하세요.");_=Decode(input,1200);input.Position=0;
        var relative=".indierun-art/"+Guid.NewGuid().ToString("N")+Path.GetExtension(path).ToLowerInvariant();var target=BackupPaths.Child(library,relative);BackupPaths.NoLinks(target);Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try{using var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None);input.CopyTo(output);output.Flush(true);return relative;}catch{if(File.Exists(target))File.Delete(target);throw;}
    }
}


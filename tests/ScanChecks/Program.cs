using IndieRun;
using System.Text;

static void Check(bool passed,string name){if(!passed)throw new Exception("FAIL: "+name);Console.WriteLine("PASS: "+name);}
var fixtures=Path.GetFullPath(args[0]);Directory.CreateDirectory(fixtures);
var game=Path.Combine(fixtures,"wrapper","game");Directory.CreateDirectory(game);
File.WriteAllBytes(Path.Combine(game,"Game.exe"),[]);File.WriteAllBytes(Path.Combine(game,"Config.exe"),[]);
Directory.CreateDirectory(Path.Combine(fixtures,"backups"));File.WriteAllBytes(Path.Combine(fixtures,"backups","Old.exe"),[]);
File.WriteAllText(Path.Combine(fixtures,"wrapper","guide.txt"),"가이드",new UTF8Encoding(true));
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var japanese=Path.Combine(fixtures,"readme.txt");File.WriteAllText(japanese,"ゲームの説明です。",Encoding.GetEncoding(932));
var html=Path.Combine(fixtures,"readme.htm");File.WriteAllText(html,"<p>Hello &amp; world</p><script>doNotRun()</script>",Encoding.UTF8);
var scan=GameScanner.Scan(fixtures,[Path.Combine(fixtures,"backups")]);
Check(GameScanner.CandidateGroups(scan).Count()==1,"same-directory EXEs grouped; backup excluded");
Check(scan.Files.Any(f=>f.Path.EndsWith("guide.txt")),"parent guide indexed for scope expansion");
Check(GameScanner.Auxiliary("UnityCrashHandler32.exe")&&GameScanner.Auxiliary("Config.exe")&&!GameScanner.Auxiliary("GamePro.exe"),"auxiliary EXE labels");
Check(!GameScanner.Within(fixtures+"-other",fixtures),"path boundary avoids sibling-prefix overlap");
Check(ReadOnlyPreview.ReadText(japanese).Text=="ゲームの説明です。","CP932 Japanese preview");
Check(ReadOnlyPreview.ReadText(Path.Combine(fixtures,"wrapper","guide.txt")).Text=="가이드","UTF8 BOM preview");
var plain=ReadOnlyPreview.ReadText(html).Text;Check(plain.Contains("Hello & world")&&!plain.Contains("doNotRun"),"HTML displayed as inert decoded text");
var longText=Path.Combine(fixtures,"long.txt");File.WriteAllText(longText,new string('あ',100000),new UTF8Encoding(false));
var limited=ReadOnlyPreview.ReadText(longText);Check(limited.Truncated&&limited.Encoding=="UTF-8"&&!limited.Text.Contains('�'),"bounded UTF8 preview handles split character");
using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();try{GameScanner.Scan(fixtures,cancellation:cancelled.Token);Check(false,"cancellation");}catch(OperationCanceledException){Check(true,"cancellation");}}
if(args.Length>1)
{
    var actual=GameScanner.Scan(args[1]);var before=actual.Fingerprint();
    foreach(var group in GameScanner.CandidateGroups(actual))
    {
        var files=actual.Files.Where(f=>GameScanner.Within(f.Path,group.Key)).ToList();
        var docs=files.Where(f=>ReadOnlyPreview.IsText(f.Path)).OrderBy(f=>f.Path.Length).Take(1);
        foreach(var doc in docs){var result=ReadOnlyPreview.ReadText(doc.Path);Check(result.Text.Length>0,"actual document readable: "+Path.GetFileName(doc.Path));}
        Console.WriteLine($"CANDIDATE: {group.Key} | EXEs={group.Count()} | auxiliary={group.Count(f=>GameScanner.Auxiliary(f.Path))} | files={files.Count}");
    }
    var after=GameScanner.Scan(args[1]);Check(before==after.Fingerprint(),"actual source file list, sizes and write timestamps unchanged");
    Console.WriteLine($"ACTUAL: candidates={GameScanner.CandidateGroups(actual).Count()} exes={actual.Files.Count(f=>Path.GetExtension(f.Path).Equals(".exe",StringComparison.OrdinalIgnoreCase))} files={actual.Files.Count} directories={actual.Directories.Count} skipped={actual.Issues.Count}");
}

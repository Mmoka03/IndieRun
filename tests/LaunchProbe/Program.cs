using System.Text.Json;
var marker=args.Length>0?args[0]:Path.Combine(AppContext.BaseDirectory,"probe-launches.jsonl");
File.AppendAllText(marker,JsonSerializer.Serialize(new{working=Environment.CurrentDirectory,arguments=args.Skip(args.Length>0?1:0).ToArray()})+Environment.NewLine);

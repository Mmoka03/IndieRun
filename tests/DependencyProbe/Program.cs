var engine=Path.Combine(AppContext.BaseDirectory,"engine");
if(!File.Exists(Path.Combine(engine,"Engine.exe"))||!File.Exists(Path.Combine(engine,"assets","required.dat"))||File.ReadAllText(Path.Combine(engine,"assets","required.dat"))!="generated required data")return 7;
if(args.Length>0)File.WriteAllText(args[0],"ready");
return 0;

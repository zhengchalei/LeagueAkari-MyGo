using LeagueAkari.WinUI.Services;
var fixtures = new[] {
 ("/lol-summoner/v1/summoners/:puuid", "/lol-summoner/v1/summoners/player-1", true),
 ("/lol-chat/v1/conversations/*/messages", "/lol-chat/v1/conversations/conv-1/messages", true),
 ("/lol-champ-select/v1/session/**", "/lol-champ-select/v1/session", true),
 ("/lol-champ-select/v1/session/**", "/lol-champ-select/v1/session/actions/1", true),
 ("/**", "/", true),
 ("/lol/:id", "/lol/1/more", false),
 ("/lol/*", "/lol/", false),
 ("/lol/session", "/lol/session/more", false)
};
foreach(var (route,uri,expected) in fixtures) { DebugRoute.Validate(route); if(DebugRoute.Matches(route,uri)!=expected) throw new Exception($"{route}: {uri}"); }
foreach(var invalid in new[]{"/", "/lol/:", "/lol/**/session", "/lol/**session", "/lol/*session", "/lol/game:flow", "/lol/game*flow"}) {try {DebugRoute.Validate(invalid);}catch(ArgumentException){continue;}throw new Exception("invalid rule accepted: "+invalid);}
if(DebugRoute.Normalize("lol///chat/")!="/lol/chat")throw new Exception("sanitize mismatch");
Console.WriteLine("16 original Radix route behavior fixtures passed.");

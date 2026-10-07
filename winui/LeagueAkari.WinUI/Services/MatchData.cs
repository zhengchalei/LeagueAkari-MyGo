using System.Text.Json;
namespace LeagueAkari.WinUI.Services;

public sealed record MatchParticipant(JsonElement Raw,JsonElement Stats,string Puuid,string Name,string Tag,int Id,int ChampionId,int TeamId,string TeamKey,string Position,string WinResult)
{
 public double Num(string key)=>key switch{"cs"=>Stats.Number("totalMinionsKilled")+Stats.Number("neutralMinionsKilled"),"kda"=>(Kills+Assists)/MatchData.NoZero(Deaths),"level"=>Stats.Number("champLevel"),"totalDamageToTowers"=>Stats.Number("damageDealtToTurrets",Stats.Number("totalDamageToTowers")),"magicDamageTaken"=>IsLcu?Stats.Number("magicalDamageTaken",Stats.Number("magicDamageTaken")):Stats.Number("magicDamageTaken"),_=>Stats.Number(key,Raw.Number(key,Raw.Field("challenges").Number(key)))};
 public bool IsLcu=>Raw.Field("stats").ValueKind==JsonValueKind.Object;
 public double? NullableNum(string key){if(key is "soloKills" or "effectiveHealAndShielding" or "knockEnemyIntoTeamAndKill" or "killsNearEnemyTurret" or "killsUnderOwnTurret" or "earliestDragonTakedown" or "maxCsAdvantageOnLaneOpponent"){if(IsLcu)return null;var value=Raw.Field("challenges").Field(key);return value.ValueKind==JsonValueKind.Number?value.GetDouble():null;}if(key.EndsWith("Pings",StringComparison.Ordinal)||key=="totalDamageShieldedOnTeammates"){var value=Raw.Field(key);return !IsLcu&&value.ValueKind==JsonValueKind.Number?value.GetDouble():null;}return Num(key);}
 public bool IsSurrender=>Flag("gameEndedInEarlySurrender")||Flag("teamEarlySurrendered")||WinResult=="loss"&&Flag("gameEndedInSurrender");
 public int RoleBoundItem=>(int)Num("roleBoundItem");
 public int[] PerkStyles=>!IsLcu?Raw.Field("perks").Field("styles").Items().Select(s=>(int)s.Number("style")).ToArray():[(int)Num("perkPrimaryStyle"),(int)Num("perkSubStyle")];
 public bool Flag(string key)=>Stats.Boolean(key)||Raw.Boolean(key);
 public double Kills=>Num("kills");public double Deaths=>Num("deaths");public double Assists=>Num("assists");
 public int[] Items=>Enumerable.Range(0,7).Select(i=>(int)Num("item"+i)).ToArray();
 public int[] Spells=>[(int)Raw.Number("spell1Id",Raw.Number("summoner1Id")),(int)Raw.Number("spell2Id",Raw.Number("summoner2Id"))];
 public int[] Augments=>Enumerable.Range(1,6).Select(i=>(int)Num("playerAugment"+i)).ToArray();
 public int[] Runes=>Raw.Field("perks").Field("styles").ValueKind==JsonValueKind.Array?Raw.Field("perks").Field("styles").Items().SelectMany(s=>s.Field("selections").Items().Select(p=>(int)p.Number("perk"))).ToArray():Enumerable.Range(0,6).Select(i=>(int)Num("perk"+i)).ToArray();
}

public static class MatchData
{
 public static double NoZero(double value)=>value==0?1:value;
 public static bool IsPveQueue(int queue)=>PveQueues.Contains(queue);
 private static readonly HashSet<int> PveQueues=[31,32,33,34,35,36,52,800,801,810,820,830,831,832,840,841,842,850,851,852,860,870,880,890,2000,2010,2020,90,91,92,950,951,960,961,981,982,990,1030,1031,1032,1040,1041,1050,1051,1060,1061,1070,1071,1800,1810,1820,1830,1840,1850,1860,1870,1880,1890];
 public static JsonElement Game(JsonElement value){for(int i=0;i<4;i++){if(value.Field("json").ValueKind==JsonValueKind.Object){value=value.Field("json");continue;}if(value.Field("data").ValueKind==JsonValueKind.Object&&value.Field("participants").ValueKind!=JsonValueKind.Array){value=value.Field("data");continue;}break;}return value;}
 public static MatchParticipant[] Participants(JsonElement raw){var game=Game(raw);var identities=game.Field("participantIdentities").Items().ToArray();return game.Field("participants").Items().Where(p=>p.Field("stats").ValueKind!=JsonValueKind.Object||identities.Any(i=>i.Number("participantId")==p.Number("participantId"))).Select(p=>{var stats=p.Field("stats");bool lcu=stats.ValueKind==JsonValueKind.Object;if(!lcu)stats=p;var identity=identities.FirstOrDefault(i=>i.Number("participantId")==p.Number("participantId")).Field("player");string puuid=p.Text("puuid",identity.Text("puuid"));var name=p.Text("riotIdGameName",p.Text("gameName",p.Text("summonerName",identity.Text("gameName",identity.Text("summonerName")))));var tag=p.Text("riotIdTagline",p.Text("tagLine",identity.Text("tagLine")));int team=(int)p.Number("teamId");string teamKey=game.Text("gameMode")=="CHERRY"?"CHERRY-"+stats.Number("playerSubteamId"):"TEAM-"+team;string result=game.Text("endOfGameResult").StartsWith("Abort_")?"abort":stats.Boolean("gameEndedInEarlySurrender")?"remake":stats.Boolean("teamEarlySurrendered")?"loss":stats.Boolean("win")?"win":"loss";return new MatchParticipant(p,stats,puuid,name,tag,(int)p.Number("participantId"),(int)p.Number("championId"),team,teamKey,lcu?"":p.Text("teamPosition"),result);}).ToArray();}
 public static MatchParticipant? Self(JsonElement game,string puuid)=>Participants(game).FirstOrDefault(p=>p.Puuid==puuid);
 public static string ResultLabel(string result)=>result switch{"win"=>"胜利","remake"=>"重开","abort"=>"中止",_=>"失败"};
 public static string QueueLabel(int id)=>id switch{420=>"单双排位",440=>"灵活排位",450=>"极地大乱斗",2400 or 2401 or 2403 or 2405 or 2410 or 2450=>"海克斯大乱斗",1700=>"斗魂竞技场",400=>"征召匹配",430=>"匹配",700=>"冠军杯赛",_=>"队列 "+id};
 public static DateTimeOffset Creation(JsonElement raw){var game=Game(raw);var number=game.Number("gameCreation",game.Number("gameStartTimestamp"));return number>0?DateTimeOffset.FromUnixTimeMilliseconds((long)number):DateTimeOffset.TryParse(game.Text("gameCreationDate"),out var date)?date:DateTimeOffset.MinValue;}
 public static double Share(MatchParticipant self,IEnumerable<MatchParticipant> team,string key)=>self.Num(key)/NoZero(team.Sum(p=>p.Num(key)));
 public static HistorySummary Summarize(IEnumerable<JsonElement> games,string puuid)
 {
  var prepared=games.Select(Game).Where(g=>g.Text("gameType")=="MATCHED_GAME"&&!IsPveQueue((int)g.Number("queueId"))).Select(g=>(Game:g,All:Participants(g),Self:Self(g,puuid))).Where(g=>g.Self is not null&&g.Self.WinResult is "win" or "loss").ToArray();
  int count=prepared.Length;if(count==0)return new();
  double kills=prepared.Sum(g=>g.Self!.Kills),deaths=prepared.Sum(g=>g.Self!.Deaths),assists=prepared.Sum(g=>g.Self!.Assists),wins=prepared.Count(g=>g.Self!.WinResult=="win");
  double Avg(Func<(JsonElement Game,MatchParticipant[] All,MatchParticipant? Self),double> selector)=>prepared.Average(selector);
  IEnumerable<MatchParticipant> Team((JsonElement Game,MatchParticipant[] All,MatchParticipant? Self) g)=>g.All.Where(p=>p.TeamKey==g.Self!.TeamKey);
  double Ratio(string key)=>Avg(g=>Share(g.Self!,Team(g),key));
  double Contribution((JsonElement Game,MatchParticipant[] All,MatchParticipant? Self) g,string key)=>Team(g).Count()<=1?0:Share(g.Self!,Team(g),key)*Team(g).Count();
  double Score(double value,double min,double max,double weight)=>Math.Clamp((value-min)/(max-min),0,1)*weight;
  double Participation((JsonElement Game,MatchParticipant[] All,MatchParticipant? Self) g)=>(g.Self!.Kills+g.Self.Assists)/NoZero(Team(g).Sum(p=>p.Kills));
  double Efficiency((JsonElement Game,MatchParticipant[] All,MatchParticipant? Self) g){double totalKills=Team(g).Sum(p=>p.Kills),damage=Team(g).Sum(p=>p.Num("totalDamageDealtToChampions"));return totalKills==0||damage==0?1:g.Self!.Kills/totalKills/(g.Self.Num("totalDamageDealtToChampions")/NoZero(damage));}
  double? NullableAvg(string key){var values=prepared.Select(g=>g.Self!.NullableNum(key)).ToArray();return values.All(v=>v.HasValue)?values.Average(v=>v!.Value):null;}
  double kda=(kills+assists)/NoZero(deaths);
  double akari=Math.Clamp(Math.Sqrt(kda)*3/7,0,1)+Score(wins/count,.5,1,1)+Avg(g=>Score(Contribution(g,"totalDamageDealtToChampions"),1,2,3))+Avg(g=>Score(Contribution(g,"totalDamageTaken"),1,2,2))+Avg(g=>Score(g.Self!.Num("cs")/NoZero(g.Game.Number("gameDuration")/60),5,10,2))+Avg(g=>Score(Contribution(g,"goldEarned"),1,1.5,2))+Avg(g=>Score(Participation(g),.3,1,2))+Avg(g=>Score(Contribution(g,"visionScore"),1,2,2));
  int activeWins=0,activeLosses=0,winStreak=0,lossStreak=0;
  foreach(var g in prepared){if(g.Self!.WinResult=="win"){if(lossStreak>0)break;winStreak++;}else{if(winStreak>0)break;lossStreak++;}}
  var latest=prepared[0];var ended=Creation(latest.Game).AddSeconds(latest.Game.Number("gameDuration"));
  if(DateTimeOffset.UtcNow-ended<TimeSpan.FromHours(4)){for(int i=0;i<prepared.Length;i++){var current=prepared[i];if(i>0&&ended-Creation(current.Game)>TimeSpan.FromHours(8))break;if(current.Self!.WinResult=="win")activeWins++;else activeLosses++;ended=Creation(current.Game).AddSeconds(current.Game.Number("gameDuration"));}}
  var kdas=prepared.Select(g=>g.Self!.Num("kda")).ToArray();double meanKda=kdas.Average();
  return new(){Count=count,Wins=(int)wins,Kills=kills,Deaths=deaths,Assists=assists,Kda=kda,KdaCv=meanKda==0?-1:Math.Sqrt(kdas.Average(k=>(k-meanKda)*(k-meanKda)))/meanKda,KillParticipation=Avg(Participation),DamageShare=Ratio("totalDamageDealtToChampions"),TakenShare=Ratio("totalDamageTaken"),GoldShare=Ratio("goldEarned"),CsPerMinute=Avg(g=>g.Self!.Num("cs")/NoZero(g.Game.Number("gameDuration")/60)),AkariScore=akari,BlueCount=prepared.Count(g=>g.Self!.TeamKey=="TEAM-100"),RedCount=prepared.Count(g=>g.Self!.TeamKey=="TEAM-200"),ChampionCounts=prepared.GroupBy(g=>g.Self!.ChampionId).ToDictionary(g=>g.Key,g=>g.Count()),VisionScore=Avg(g=>g.Self!.Num("visionScore")),SoloKills=NullableAvg("soloKills"),EnemyMissingPings=NullableAvg("enemyMissingPings"),DamageGoldEfficiency=Avg(g=>g.Self!.Num("totalDamageDealtToChampions")/NoZero(g.Self.Num("goldEarned"))),KillDamageEfficiency=Avg(Efficiency),ActiveSessionWins=activeWins,ActiveSessionLosses=activeLosses,WinningStreak=winStreak,LosingStreak=lossStreak};
 }
}
public sealed record HistorySummary
{
 public int Count{get;init;}public int Wins{get;init;}public int Losses=>Count-Wins;
 public double Kills{get;init;}public double Deaths{get;init;}public double Assists{get;init;}
 public double Kda{get;init;}public double KdaCv{get;init;}=-1;
 public double KillParticipation{get;init;}public double DamageShare{get;init;}public double TakenShare{get;init;}public double GoldShare{get;init;}public double CsPerMinute{get;init;}public double AkariScore{get;init;}
 public int BlueCount{get;init;}public int RedCount{get;init;}public Dictionary<int,int> ChampionCounts{get;init;}=new();
 public double VisionScore{get;init;}public double? SoloKills{get;init;}public double? EnemyMissingPings{get;init;}
 public double DamageGoldEfficiency{get;init;}public double KillDamageEfficiency{get;init;}=1;
 public int ActiveSessionWins{get;init;}public int ActiveSessionLosses{get;init;}public int WinningStreak{get;init;}public int LosingStreak{get;init;}
 public bool Outstanding=>AkariScore>=6.5&&Count>=5;public bool Extraordinary=>AkariScore>=8&&Count>=8;
}

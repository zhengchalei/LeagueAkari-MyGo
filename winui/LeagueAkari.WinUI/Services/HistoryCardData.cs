using System.Text.Json;
namespace LeagueAkari.WinUI.Services;

public sealed record HistoryCardTag(string Key, double Count, double Priority, string Color, Dictionary<string, object?> Arguments, bool Times = false);
public static class HistoryCardData
{
    public static HistoryCardTag[] Tags(JsonElement game, MatchParticipant self, MatchParticipant[] players)
    {
        var tags = new List<HistoryCardTag>(); var team = players.Where(p => p.TeamKey == self.TeamKey).ToArray();
        double? Stat(MatchParticipant p,string key) => p.IsLcu && (key is "soloKills" or "knockEnemyIntoTeamAndKill" or "maxCsAdvantageOnLaneOpponent" or "killsNearEnemyTurret" or "killsUnderOwnTurret" or "totalDamageShieldedOnTeammates") ? null : key == "killParticipation" ? MatchDetailsData.KillParticipation(p, players.Where(t => t.TeamKey == p.TeamKey)) : MatchDetailsData.Stat(p,key);
        void Add(string key,double value,double priority,string color,bool times=false,Dictionary<string,object?>? args=null) => tags.Add(new(key,value,priority,color,args ?? new(){["value"]=value.ToString("N0")},times));
        var penta = self.Num("pentaKills"); var quadra = self.Num("quadraKills") - penta; var triple = self.Num("tripleKills") - quadra - penta; var doubles = self.Num("doubleKills") - triple - quadra - penta;
        foreach(var (key,count,priority) in new[]{("penta",penta,20000d),("quadra",quadra,1300d),("triple",triple,300+triple*15),("double",doubles,100+doubles*10)}) if(count>0)Add("multiKill."+key,count,priority,"rose",true);
        foreach(var (key,field,priority,color) in new[]{("damage","totalDamageDealtToChampions",1800d,"red"),("taken","totalDamageTaken",1400d,"slate"),("heal","totalHeal",1600d,"emerald"),("tower","totalDamageToTowers",900d,"stone"),("shield","totalDamageShieldedOnTeammates",1500d,"sky"),("gold","goldEarned",700d,"amber"),("kills","kills",1200d,"violet"),("kp","killParticipation",1100d,"cyan"),("cc","timeCCingOthers",1750d,"fuchsia"),("damageGoldEfficiency","damageGoldEfficiency",1800d,"lime")})
        {
            var value=Stat(self,field); if(value is not >0)continue;
            var global = players.Select(p=>Stat(p,field)).Where(v=>v.HasValue).Select(v=>v!.Value).DefaultIfEmpty(0).Max();
            var teamMax = team.Select(p=>Stat(p,field)).Where(v=>v.HasValue).Select(v=>v!.Value).DefaultIfEmpty(0).Max();
            var best=value==global; if(!best&&value!=teamMax)continue;
            var sum=MatchDetailsData.Sum(team.Select(p=>Stat(p,field)));
            var args=new Dictionary<string,object?>{["value"]=key=="kp"?(value.Value*100).ToString("F2"):value.Value.ToString("N0"),["rate"]=key=="damageGoldEfficiency"?(value.Value*100).ToString("F2"):sum.HasValue?(value.Value/MatchData.NoZero(sum.Value)*100).ToString("F2"):"—"};
            Add(key+"."+(best?"best":"team"),value.Value,best?priority:key=="damageGoldEfficiency"?930:priority-50,color,args:args);
        }
        void Threshold(string key,string field,double minimum,double priority,string color){if(Stat(self,field)is{}v&&v>=minimum)Add(key,v,priority,color,true);}
        Threshold("solo","soloKills",2,1700,"indigo"); Threshold("knockUp","knockEnemyIntoTeamAndKill",6,660,"purple");
        if(Stat(self,"maxCsAdvantageOnLaneOpponent")is{}advantage&&advantage>=40)Add("csAdvantage",advantage,750+advantage*10,"amber",true,new(){["value"]=Math.Round(advantage).ToString("N0")});
        if(Stat(self,"cs")is{}cs&&cs>0&&cs==players.Select(p=>Stat(p,"cs")).Where(v=>v.HasValue).Select(v=>v!.Value).DefaultIfEmpty(0).Max())Add("cs.best",cs,600,"orange");
        var map=(int)game.Number("mapId");var minutes=game.Number("gameDuration")/60;
        if(map is 11 or 12){Threshold("towerKill.dive","killsNearEnemyTurret",minutes/(map==11?5:1.5),1660,"rose");Threshold("towerKill.under","killsUnderOwnTurret",minutes/(map==11?6:1.5),1640,"stone");}
        return tags.OrderByDescending(t=>t.Priority).ToArray();
    }
    public static string MapName(JsonElement resources,JsonElement game)
    {
        string id=game.Number("mapId").ToString("0");var mutators=resources.Field("gameModeMutators").Field(id);
        if(mutators.ValueKind!=JsonValueKind.Object)return resources.Field("maps").Field(id).Text("name",id);
        var active=game.Field("gameModeMutators").Items().Select(m=>m.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected=mutators.Field("Mutators").Items().FirstOrDefault(m=>active.Contains(m.Field("Mutator").Text("ExpandedMutator")));
        return selected.Text("MapNameOverride",mutators.Text("MapNameBase",id));
    }
    public static JsonElement Resource(JsonElement resources,string kind,int id) => (kind == "perkstyles" ? resources.Field(kind).Field("styles") : resources.Field(kind)).Field(id.ToString());
    public static string IconPath(JsonElement resource) => resource.Text("augmentSmallIconPath",resource.Text("iconPath",resource.Text("iconLarge",resource.Text("icon"))));
    public static string Duration(double seconds) => seconds>=3600 ? TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss") : TimeSpan.FromSeconds(Math.Max(0,seconds)).ToString(@"mm\:ss");
    public static int Placement(MatchParticipant player)=>(int)(MatchDetailsData.Stat(player,"subteamPlacement")??0);
    public static bool IsChampion(MatchParticipant player)=>Placement(player)==1;
}

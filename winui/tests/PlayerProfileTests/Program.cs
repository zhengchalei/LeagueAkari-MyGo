using System.Text.Json;
using LeagueAkari.WinUI.Services;

static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
// The historical highest fixture is copied from the original RankedPane.test.ts.
var ranked = Json("""
{"queueMap":{"RANKED_SOLO_5x5":{"queueType":"RANKED_SOLO_5x5","tier":"DIAMOND","division":"II","leaguePoints":64,"wins":138,"losses":119,"previousSeasonHighestTier":"DIAMOND","previousSeasonHighestDivision":"IV","highestTier":"MASTER","highestDivision":"I"}},"queues":[{"queueType":"RANKED_TFT","tier":"GOLD","division":"II","wins":10,"losses":12},{"queueType":"RANKED_FLEX_SR","tier":"NA","wins":0,"losses":0},{"queueType":"RANKED_SOLO_5x5","tier":"GOLD","wins":1,"losses":1}]}
""");
var entries = PlayerRankEntry.All(ranked);
Check(entries.Length == 3 && entries[0].QueueType == "RANKED_SOLO_5x5" && entries[1].QueueType == "RANKED_FLEX_SR" && entries[2].QueueType == "RANKED_TFT", "Original queue sorting and complete table entries");
Check(entries[0].Tier == "DIAMOND", "queueMap takes precedence over queues fallback");
Check(entries[0].HighestTier == "MASTER" && entries[0].HighestDivision == "I" && entries[0].PreviousHighestTier == "DIAMOND", "True historical peak must not use the previous season peak");
Check(Math.Abs(entries[0].WinRate!.Value - 138d / 257) < 1e-10 && entries[1].WinRate is null && !entries[1].IsRanked, "Unranked and rate visibility semantics");
Check(PlayerRankEntry.Cards(ranked).Length == 2, "Header cards show only solo and flex, while table retains TFT");
var noLosses = PlayerRankEntry.Read(Json("""{"tier":"MASTER","wins":6,"losses":0} """));
Check(noLosses.WinRate is null, "Original ranked badge is hidden when losses are zero");
var objectQueues = PlayerRankEntry.All(Json("""{"queues":{"RANKED_FLEX_SR":{"tier":"SILVER","division":"IV"}}} """));
Check(objectQueues.Length == 1 && objectQueues[0].QueueType == "RANKED_FLEX_SR" && objectQueues[0].Wins is null, "LCU object queue contract and missing values");
var summoner = PlayerProfileData.Read(Json("""{"puuid":"player","gameName":"名字","tagLine":"36386","profileIconId":29,"summonerLevel":180} """));
Check(summoner.RiotId == "名字#36386" && summoner.Level == 180 && summoner.IconId == 29, "Real profile header fields");
Check(PlayerProfileData.Read(Json("""{"level":99,"displayName":"fallback"} """)).Name == "fallback", "SGP normalized level/name fallback");
Check(PlayerRankEntry.All(Json("""{"queueMap":{"RANKED_SOLO_5x5":null},"queues":[{"queueType":"RANKED_SOLO_5x5","tier":"GOLD"}]} """))[0].Tier == "GOLD", "Null queueMap entries preserve queues fallback");
Console.WriteLine("10 original profile and ranked behavior fixtures passed.");

using System.Text.Json;
using LeagueAkari.WinUI.Services;

int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-9;
JsonElement Game(long id = 101, string puuid = "historic-player", int participant = 7, int team = 200, int champion = 64, string position = "JUNGLE", int smite = 0, int map = 11, string type = "MATCHED_GAME") => JsonSerializer.SerializeToElement(new
{
    gameId = id, mapId = map, queueId = 420, gameType = type, gameDuration = 1800,
    participants = new[] { new { puuid, participantId = participant, teamId = team, championId = champion, teamPosition = position, summoner1Id = smite, win = true, kills = 1, deaths = 1, assists = 1 } }
});
object Kill(int timestamp, int killer = 7, int[]? assists = null, double x = 2000, double y = 11000) => new { type = "CHAMPION_KILL", timestamp, killerId = killer, assistingParticipantIds = assists ?? [], position = new { x, y } };
object Monster(string monster, int timestamp, int killer = 7, int? team = 200, int[]? assists = null) => new { type = "ELITE_MONSTER_KILL", monsterType = monster, timestamp, killerId = killer, killerTeamId = team, assistingParticipantIds = assists ?? [] };
JsonElement Timeline(bool detailed = true, bool kills = true, bool objectives = true, double thirdDamage = 10, double fourthDamage = 20)
{
    var frames = Enumerable.Range(0, 16).Select(i =>
    {
        var player = new Dictionary<string, object?> { ["position"] = new { x = i == 1 ? 3830 : 2000, y = i == 1 ? 7880 : 11000 }, ["level"] = 3, ["minionsKilled"] = 0, ["jungleMinionsKilled"] = 16 };
        if (detailed) { player["damageStats"] = new { totalDamageDoneToChampions = i == 3 ? thirdDamage : fourthDamage }; player["championStats"] = new { }; }
        var events = new List<object>();
        if (kills && i == 3) { events.Add(Kill(180000)); events.Add(Kill(180001, 1, [7], 7000, 7000)); events.Add(Kill(240001)); events.Add(Kill(840000, 7, null, 11000, 2000)); events.Add(Kill(840001)); events.Add(Kill(120000, 1)); }
        if (objectives && i == 6) { events.Add(Monster("DRAGON", 300000, 1, 100)); events.Add(Monster("DRAGON", 360000)); events.Add(Monster("DRAGON", 700000, 7, 200, [8])); events.Add(Monster("HORDE", 420000)); events.Add(Monster("RIFTHERALD", 800000)); events.Add(Monster("BARON_NASHOR", 1200000)); }
        return new { timestamp = i * 60000, participantFrames = new Dictionary<string, object> { ["7"] = player }, events };
    }).ToArray();
    return JsonSerializer.SerializeToElement(new { frames });
}
var game = Game(); var timeline = Timeline();
var analysis = HistoryJungleData.Analyze([game], "historic-player", new Dictionary<long, JsonElement> { [101] = timeline }); var jungle = analysis.Field("jungle");
Check(HistoryJungleData.Eligible(game, "historic-player") && !HistoryJungleData.Eligible(game, "current-player"), "arbitrary historical identity independent of ongoing roster");
Check(HistoryJungleData.Eligible(Game(position: "", smite: 11), "historic-player"), "LCU-style smite fallback identifies jungler");
Check(!HistoryJungleData.Eligible(Game(map: 12), "historic-player") && !HistoryJungleData.Eligible(Game(position: "TOP"), "historic-player"), "only Rift actual junglers eligible");
Check(!HistoryJungleData.Eligible(Game(type: "PRACTICE_GAME"), "historic-player"), "practice summaries excluded as original analysis");
Check(jungle.Number("gamesAnalyzed") == 1 && analysis.Field("champions").Field("64").Field("jungle").Number("gamesAnalyzed") == 1, "per champion and overall real sample counts");
Check(jungle.Field("minutePositions").Items().Count() == 14, "minute path is first fourteen minutes only");
Check(jungle.Number("topZoneWeightSum") == 24 && jungle.Number("midZoneWeightSum") == 5 && jungle.Number("botZoneWeightSum") == 5, "position and killer or assist weight combine; kill boundary inclusive");
Check(jungle.Number("totalTopGanks") == 2 && jungle.Number("totalMidGanks") == 1 && jungle.Number("totalBotGanks") == 1, "strict gank lanes ignore uninvolved and late kills");
Check(jungle.Field("firstClearCamp").Field("redInvade").Number("blue") == 1 && jungle.Field("firstClearCamp").Number("redGames") == 1, "red-side actual player nearest blue buff is invade start");
Check(jungle.Field("earlyGank").Number("level3GankCount") == 1 && jungle.Field("earlyGank").Number("level4GankCount") == 1, "detailed third/fourth frame damage detection");
Check(jungle.Field("earlyGank").Field("level3KillPositions").Items().Count() == 1 && jungle.Field("earlyGank").Field("level4KillPositions").Items().Count() == 1, "180000 and 240000 early-event boundaries preserved");
var objective = jungle.Field("objectives");
Check(objective.Number("firstDragonRate") == 0 && objective.Number("avgDragons") == 2 && Near(objective.Number("soloDragonRate"), .5), "first dragon includes enemy and solo dragon checks killer and assists");
Check(objective.Number("avgFirstDragonTime") == 360 && objective.Number("avgFirstVoidgrubTime") == 420 && objective.Number("avgFirstHeraldTime") == 800 && objective.Number("avgFirstBaronTime") == 1200, "first own-objective times remain seconds");
var lcu = HistoryJungleData.Analyze([game], "historic-player", new Dictionary<long, JsonElement> { [101] = Timeline(detailed: false) });
Check(lcu.Field("jungle").Field("earlyGank").Number("level3GankCount") == 1, "LCU third-minute detection uses actual kill participation");
var noKillsLcu = HistoryJungleData.Analyze([game], "historic-player", new Dictionary<long, JsonElement> { [101] = Timeline(detailed: false, kills: false, objectives: false) }).Field("jungle");
Check(noKillsLcu.Field("earlyGank").Number("level3GankCount") == 0 && noKillsLcu.Field("earlyGank").Number("level4GankCount") == 0, "LCU unavailable damage does not invent early gank");
Check(noKillsLcu.Field("objectives").Field("avgFirstDragonTime").ValueKind == JsonValueKind.Null, "missing objective times remain null");
var damageOnly = HistoryJungleData.Analyze([game], "historic-player", new Dictionary<long, JsonElement> { [101] = Timeline(kills: false, objectives: false) }).Field("jungle");
Check(damageOnly.Field("earlyGank").Number("level3GankCount") == 1 && damageOnly.Field("earlyGank").Number("level4GankCount") == 1, "SGP damage proves gank even without kill");
var wraps = new[] { JsonSerializer.SerializeToElement(new { json = timeline }), JsonSerializer.SerializeToElement(new { source = "sgp", data = new { json = timeline } }), JsonSerializer.SerializeToElement(new { source = "lcu", data = timeline }) };
Check(wraps.All(t => HistoryJungleData.Frames(t).Length == 16), "raw SGP json and LCU timeline wrappers normalize");
var none = HistoryJungleData.Analyze([game], "historic-player", new Dictionary<long, JsonElement>());
Check(none.Field("jungle").ValueKind == JsonValueKind.Null, "no actual timeline gives no fabricated jungle aggregate");
var combined = HistoryJungleData.Analyze([game, Game(102, champion: 11, team: 100)], "historic-player", new Dictionary<long, JsonElement> { [101] = timeline, [102] = Timeline(kills: false, objectives: false) }).Field("jungle");
Check(combined.Field("earlyGank").Field("byTeam").Number("blueGames") == 1 && combined.Field("earlyGank").Field("byTeam").Number("redGames") == 1, "blue red denominators are independent actual game counts");
Check(combined.Field("objectives").Number("avgFirstDragonTime") == 360 && combined.Field("objectives").Number("avgDragons") == 1, "objective times average observed samples while counts use all samples");

var requests = new List<(string Server, string Source, long Id)>();
var loader = new HistoryJungleLoader((server, source, id, token) => { requests.Add((server, source, id)); return Task.FromResult(timeline); });
var result = await loader.LoadAsync([game, game, Game(102, position: "TOP")], "historic-player", "TENCENT_HN10", "sgp");
Check(result.RequestedGames == 1 && result.LoadedGames == 1 && requests.Single() == ("TENCENT_HN10", "sgp", 101), "loader deduplicates eligible ids with captured source server identity");
await loader.LoadAsync([game], "historic-player", "TENCENT_HN10", "sgp"); Check(requests.Count == 1, "successful same source region timeline reused");
await loader.LoadAsync([game], "historic-player", "TENCENT_HN1", "sgp"); await loader.LoadAsync([game], "historic-player", "TENCENT_HN10", "lcu"); Check(requests.Count == 3, "identical game id cache partitioned by region and source");
int attempts = 0;
var partial = new HistoryJungleLoader((_, _, id, _) => { attempts++; return id == 102 ? Task.FromException<JsonElement>(new IOException("actual server 404")) : Task.FromResult(timeline); });
var failure = await partial.LoadAsync([game, Game(102)], "historic-player", "A", "sgp");
Check(failure.LoadedGames == 1 && failure.Errors.Single().GameId == 102 && failure.Analysis.Field("jungle").Number("gamesAnalyzed") == 1, "partial failure retains successful real sample with game-specific error");
await partial.LoadAsync([game, Game(102)], "historic-player", "A", "sgp"); Check(attempts == 3, "failed timeline is retried not poisoned cache");
var invalid = await new HistoryJungleLoader((_, _, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { }))).LoadAsync([game], "historic-player", "A", "lcu");
Check(invalid.LoadedGames == 0 && invalid.Errors.Count == 1, "missing frames reports failure rather than empty successful sample");
int called = 0; var canceledLoader = new HistoryJungleLoader((_, _, _, _) => { called++; return Task.FromResult(timeline); });
using (var canceled = new CancellationTokenSource()) { canceled.Cancel(); try { await canceledLoader.LoadAsync([game], "historic-player", "A", "lcu", canceled.Token); throw new Exception("did not cancel"); } catch (OperationCanceledException) { } }
Check(called == 0, "cancel before start prevents real requests");
int current = 0, maximum = 0;
var bounded = new HistoryJungleLoader(async (_, _, _, token) => { int active = Interlocked.Increment(ref current); maximum = Math.Max(maximum, active); await Task.Delay(20, token); Interlocked.Decrement(ref current); return timeline; });
await bounded.LoadAsync(Enumerable.Range(0, 12).Select(i => Game(200 + i)), "historic-player", "A", "sgp"); Check(maximum <= 5 && maximum > 1, "real timeline concurrency bounded to original five");

var sgp = JsonSerializer.SerializeToElement(new { isTokenReady = true, availability = new { sgpServerId = "TENCENT_HN1" }, leagueServers = new { servers = new Dictionary<string, object> { ["TENCENT_HN1"] = new { matchHistory = "https://a" }, ["TENCENT_HN10"] = new { matchHistory = "https://b" } } } });
var calls = new List<(string Ns, string Method, object?[] Args)>();
var dataSource = new PlayerDataSource((ns, method, args) => { calls.Add((ns, method, args)); return Task.FromResult(timeline); }, (_, _) => Task.FromResult(sgp));
await dataSource.ConfigureAsync("TENCENT_HN10", "lcu"); await dataSource.TimelineAsync(101);
Check(calls.Last().Method == "sgpRequest" && (string)calls.Last().Args[0]! == "TENCENT_HN10" && (string)calls.Last().Args[3]! == "/match-history-query/v1/products/lol/@akari:sgpServerSubId@_101/DETAILS", "cross region request uses selected SGP server exact DETAILS route");
await dataSource.ConfigureAsync("TENCENT_HN1", "lcu"); await dataSource.TimelineAsync(101);
Check(calls.Last().Method == "lcuRequest" && (string)calls.Last().Args[1]! == "/lol-match-history/v1/game-timelines/101", "local LCU uses exact timeline route");
bool Equal(JsonElement actual, JsonElement expected, string path)
{
    if (actual.ValueKind != expected.ValueKind) throw new Exception(path + " kind mismatch");
    if (expected.ValueKind == JsonValueKind.Number) { if (!Near(actual.GetDouble(), expected.GetDouble())) throw new Exception($"{path}: {actual} != {expected}"); return true; }
    if (expected.ValueKind == JsonValueKind.Object) { foreach (var field in expected.EnumerateObject()) Equal(actual.Field(field.Name), field.Value, path + "." + field.Name); return true; }
    if (expected.ValueKind == JsonValueKind.Array) { var aa = actual.Items().ToArray(); var ee = expected.Items().ToArray(); if (aa.Length != ee.Length) throw new Exception(path + " array length mismatch"); for (int i = 0; i < aa.Length; i++) Equal(aa[i], ee[i], path + "[" + i + "]"); return true; }
    if (actual.ToString() != expected.ToString()) throw new Exception(path + " value mismatch"); return true;
}
var oracle = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "upstream-oracle.json"))).RootElement;
foreach (var fixture in oracle.Items())
{
    string queue = fixture.Text("queue");
    var raw = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "snapshots", "games", queue + ".json"))).RootElement;
    var details = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "snapshots", "timelines", queue + ".json"))).RootElement;
    var actual = HistoryJungleData.Analyze([raw], fixture.Text("puuid"), new Dictionary<long, JsonElement> { [(long)raw.Number("gameId")] = details }).Field("jungle");
    Check(Equal(actual, fixture.Field("jungle"), queue), "all numeric/map/event fields equal upstream TypeScript snapshot " + queue + " player " + MatchData.Self(raw, fixture.Text("puuid"))!.Id);
}
Console.WriteLine($"History jungle: {passed} passed");

namespace LeagueAkari.WinUI.Services { public sealed class BackendClient { public Task<JsonElement> CallAsync(string ns, string method, params object?[] args) => throw new NotSupportedException(); public Task<JsonElement> StateAsync(string ns, string state = "state") => throw new NotSupportedException(); } }

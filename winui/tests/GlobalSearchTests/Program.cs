using System.Text.Json;
using LeagueAkari.WinUI.Services;

int passed = 0; void Check(string label, bool value) { if (!value) throw new Exception(label); ++passed; Console.WriteLine("PASS " + label); }
JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
JsonElement Sgp(bool token = true, bool tencent = true) => J(new { isTokenReady = token, availability = new { region = tencent ? "TENCENT" : "NA", sgpServerId = "TENCENT_HN1" }, leagueServers = new { tencentServerMatchHistoryInteroperability = new[] { "TENCENT_HN1", "TENCENT_HN10" }, servers = new Dictionary<string, object> { ["TENCENT_HN1"] = new { common = "https://fixture.invalid", matchHistory = "" }, ["TENCENT_HN10"] = new { common = "https://fixture.invalid", matchHistory = "" } } } });
string id = "12345678-1234-1234-1234-123456789abc";
Check("Original dashed PUUID syntax", GlobalPlayerSearch.Kind(id) == "puuid");
Check("Undashed GUID treated as original fuzzy name", GlobalPlayerSearch.Kind(id.Replace("-", "")) == "fuzzy");
Check("Whitespace invalid", GlobalPlayerSearch.Kind("  ") == "invalid");
Check("Malformed tagline invalid", GlobalPlayerSearch.Kind("Name#") == "invalid" && GlobalPlayerSearch.Kind("Name#Tag#More") == "invalid");
Check("Invisible chars stripped but ordinary unicode retained", GlobalPlayerSearch.Clean("\u200b 测试\u2066#标签\ufeff ") == "测试#标签");
Check("Tencent server interoperability list is source", GlobalPlayerSearch.Servers(Sgp()).SequenceEqual(new[] { "TENCENT_HN1", "TENCENT_HN10" }));
Check("Other-region history excludes foreign server", !GlobalPlayerSearch.HistoryVisible("TENCENT_HN10", Sgp(tencent: false)));
Check("Foreign single tab requires region label", GlobalPlayerSearch.ShowServer(["TENCENT_HN10"], "TENCENT_HN1"));
Check("Current only tabs hide region label", !GlobalPlayerSearch.ShowServer(["TENCENT_HN1", "TENCENT_HN1"], "TENCENT_HN1"));
Check("Mixed region tabs display labels", GlobalPlayerSearch.ShowServer(["TENCENT_HN1", "TENCENT_HN10"], "TENCENT_HN1"));
Check("Friends game/chat/away/offline priority", new[] { "dnd", "chat", "away", "offline" }.Select(s => GlobalPlayerSearch.FriendPriority(J(new { availability = s }))).SequenceEqual(new[] { 3, 2, 1, 0 }));
Check("Spectator requires all existing original fields", GlobalPlayerSearch.FriendSpectatable(J(new { availability = "dnd", puuid = id, lol = new { gameStatus = "InGame", spectatorKey = "fixture-key" } })) && !GlobalPlayerSearch.FriendSpectatable(J(new { availability = "dnd", puuid = id, lol = new { gameStatus = "InGame" } })));
var calls = new List<(string Method, object?[] Args)>(); int aliasCount = 1; bool lcuMissing = false; bool namesMissing = false;
Task<JsonElement> Call(string ns, string method, object?[] args)
{
    calls.Add((method, args));
    if (method == "riotRequest" && (string)args[0]! == "GET") return Task.FromResult(J(Enumerable.Range(0, aliasCount).Select(i => new { puuid = i == 0 ? id : "fixture-" + i, alias = new { game_name = "Alias" + i, tag_line = "TAG" } })));
    if (method == "lcuRequest") { if (lcuMissing) throw new InvalidOperationException("LCU HTTP 404"); return Task.FromResult(J(new { puuid = ((string)args[1]!).Split('/').Last(), gameName = "Local", tagLine = "TAG", profileIconId = 12, summonerLevel = 50 })); }
    if (method == "sgpRequest") return Task.FromResult(J(((string[])args[4]!).Select(p => new { puuid = p, id = 99, level = 60, profileIconId = 13 })));
    if (method == "riotRequest" && (string)args[0]! == "POST") { var body = J(args[2]!); return Task.FromResult(J(new { namesets = namesMissing ? [] : body.Field("puuids").Items().Select(p => new { puuid = p.GetString(), gnt = new { gameName = "Global", tagLine = "G" } }).ToArray() })); }
    throw new Exception("unexpected action " + method);
}
var search = new GlobalPlayerSearch(Call);
var exact = await search.FindAsync(" Local # TAG ", "TENCENT_HN1", "sgp", Sgp(false));
Check("Exact same-region works without SGP token", exact.Players.Single().Player.Text("gameName") == "Local");
Check("Exact same-region only calls Riot+LCU", calls.All(c => c.Method != "sgpRequest"));
Check("Exact trims name and tagline", ((string)calls[0].Args[1]!).Contains("gameName=Local&tagLine=TAG"));
calls.Clear(); var puuid = await search.FindAsync(id, "TENCENT_HN1", "sgp", Sgp(false));
Check("Same-region PUUID bypasses alias and SGP", calls.Count == 1 && calls[0].Method == "lcuRequest" && puuid.Players.Single().Puuid == id);
calls.Clear(); var cross = await search.FindAsync("Name#TAG", "TENCENT_HN10", "sgp", Sgp());
Check("Cross-region exact calls target SGP common even without match-history endpoint", cross.Players.Single().Server == "TENCENT_HN10" && calls.Any(c => c.Method == "sgpRequest" && (string)c.Args[0]! == "TENCENT_HN10"));
Check("Exact SGP retains alias without extra nameset", cross.Players[0].Player.Text("gameName") == "Alias0" && calls.Count(c => c.Method == "riotRequest") == 1);
calls.Clear(); aliasCount = 33; var fuzzy = await search.FindAsync("SameName", "TENCENT_HN1", "sgp", Sgp());
Check("Fuzzy uses real aliases multiple results", fuzzy.Players.Length == 33);
Check("SGP fuzzy batches sixteen", calls.Where(c => c.Method == "sgpRequest").Select(c => ((string[])c.Args[4]!).Length).SequenceEqual(new[] { 16, 16, 1 }));
Check("Fuzzy uses namesets canonical identities", fuzzy.Players.All(p => p.Player.Text("gameName") == "Global"));
calls.Clear(); aliasCount = 3; var localFuzzy = await search.FindAsync("SameName", "TENCENT_HN1", "lcu", Sgp(false));
Check("LCU fuzzy uses each actual alias puuid", localFuzzy.Players.Length == 3 && calls.Count(c => c.Method == "lcuRequest") == 3 && calls.All(c => c.Method != "sgpRequest"));
calls.Clear(); aliasCount = 1; lcuMissing = true; var fallback = await search.FindAsync("Name#TAG", "TENCENT_HN1", "sgp", Sgp());
Check("Exact no local player checks known Tencent fallback regions", fallback.Players.Single().Server == "TENCENT_HN10");
Check("Unavailable fallback endpoints reported with successful result retained", fallback.Errors.Length == 5);
calls.Clear(); var noFallback = await search.FindAsync("Name#TAG", "TENCENT_HN1", "lcu", Sgp());
Check("LCU preference never attempts regional fallback", noFallback.Players.Length == 0 && calls.All(c => c.Method != "sgpRequest"));
lcuMissing = false; namesMissing = true; calls.Clear();
Check("Missing nameset omits SGP player rather than fake name", (await search.FindAsync("Name", "TENCENT_HN1", "sgp", Sgp())).Players.Length == 0);
using var cancel = new CancellationTokenSource(); cancel.Cancel(); bool canceled = false;
try { await search.FindAsync("Name", "TENCENT_HN1", "sgp", Sgp(), cancel.Token); } catch (OperationCanceledException) { canceled = true; }
Check("Canceled search never returns result", canceled);
Console.WriteLine($"Global player search contracts: {passed} passed");

namespace LeagueAkari.WinUI.Services { public sealed class BackendClient { public Task<JsonElement> CallAsync(string ns, string method, params object?[] args) => throw new NotSupportedException(); public Task<JsonElement> StateAsync(string ns, string state = "state") => throw new NotSupportedException(); } }

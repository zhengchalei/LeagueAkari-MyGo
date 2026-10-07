using System.Text.Json.Nodes;
using LeagueAkari.WinUI.Services;
int count = 0;
void Equal<T>(T expected, T actual, string message) { count++; if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(message + $": expected {expected}, actual {actual}"); }
JsonNode Parse(string json) => JsonNode.Parse(json)!;
var arena = new OpggQuery("kr", "arena", "top", "gold_plus", "15.2").Normalize();
Equal("none", arena.Position, "Arena ignores role"); Equal("?version=15.2", arena.Parameters, "Arena omits rank tier"); Equal("/api/kr/champions/arena/17?version=15.2", arena.ChampionPath(17), "Arena has no role suffix");
var ranked = arena with { Mode = "ranked", Position = "none", Version = "" }; ranked = ranked.Normalize();
Equal("mid", ranked.Position, "Ranked fallback matches upstream"); Equal("?tier=gold_plus", ranked.Parameters, "Ranked retains tier");
var aram = ranked with { Mode = "aram", Position = "top" }; aram = aram.Normalize(); Equal("/api/kr/champions/aram/17/none?tier=gold_plus", aram.ChampionPath(17), "ARAM fixed none suffix");
var champion = Parse("""{"average_stats":{"rank":7,"tier":2,"win":8,"play":10},"positions":[{"name":"MID","stats":{"tier_data":{"rank":3,"tier":1},"win_rate":0.6}}]}""");
Equal(3d, OpggData.Rank(OpggData.Stats(champion, ranked), true), "Ranked position rank"); Equal(1d, OpggData.Tier(OpggData.Stats(champion, ranked), true), "Ranked tier_data"); Equal(2d, OpggData.Tier(OpggData.Stats(champion, aram), false), "ARAM flat tier"); Equal(7d, OpggData.Rank(OpggData.Stats(champion, aram), false), "ARAM flat rank"); Equal(.8d, OpggData.Win(OpggData.Stats(champion, arena)), "Arena win/play");
Equal<JsonNode?>(null, OpggData.Stats(champion, ranked with { Position = "top" }), "Table excludes missing position"); Equal(7d, OpggData.Number(OpggData.Stats(champion, ranked with { Position = "top" }, true), "rank"), "Detail allows average fallback");
var build = Parse("""{"starter_items":[{"ids":[3042],"pick_rate":0.2},{"ids":[1]},{"ids":[2]},{"ids":[3]}],"boots":[{"ids":[4]},{"ids":[5,6]}],"prism_items":[{"ids":[223121]},{"ids":[323042]}],"core_items":[{"ids":[7]},{"ids":[8]},{"ids":[9]},{"ids":[10]},{"ids":[11]}],"last_items":[{"ids":[3040]},{"ids":[2530]}]}""");
Equal(true, OpggData.HasItems(build), "Available item import"); Equal(false, OpggData.HasItems(Parse("{}")), "Absent item import");
var groups = OpggData.ItemGroups(build); Equal(10, groups.Length, "Three starter plus four core plus three flattened groups"); Equal(3, groups.Count(g => g.Field == "starter_items"), "Starter cap3"); Equal(4, groups.Count(g => g.Field == "core_items"), "Core cap4"); Equal("4,5,6", string.Join(',', groups.Single(g => g.Field == "boots").Items), "Boots one flattened block"); Equal("223119,323004", string.Join(',', groups.Single(g => g.Field == "prism_items").Items), "Prism restored recipes"); Equal("3003,2526", string.Join(',', groups.Single(g => g.Field == "last_items").Items), "Last item one restored block"); Equal(3004, groups[0].Items[0], "Starter recipe restored");
Equal("akari1-17-ranked-kr-gold_plus-mid-15.3", OpggData.ItemUid(17, ranked, "15.3"), "UID uses response version"); Equal("akari1-17-ranked-kr-gold_plus-mid-_", OpggData.ItemUid(17, ranked, null), "Missing version placeholder");
var session = Parse("""{"localPlayerCellId":2,"myTeam":[{"cellId":1,"championId":50},{"cellId":2,"championId":12}],"actions":[[{"actorCellId":1,"type":"pick","championId":5},{"actorCellId":2,"type":"ban","championId":6},{"actorCellId":2,"type":"pick","championId":17}]]}"""); Equal(17, OpggData.ActiveChampion(session), "Active pick overrides confirmed"); session["actions"] = Parse("[[{\"actorCellId\":2,\"type\":\"pick\",\"championId\":-3}]]"); Equal(-3, OpggData.ActiveChampion(session), "Bravery does not use old hero"); session["actions"] = Parse("[]"); Equal(12, OpggData.ActiveChampion(session), "Confirmed fallback");
var augments = Parse("""[{"id":1,"tier":2,"performance":80,"popular":0},{"id":2,"tier":1,"performance":30,"popular":50},{"id":3,"tier":0,"performance":50,"popular":10}]""").AsArray();
Equal("3,2,1", string.Join(',', OpggData.SortAugments(augments, "default").Select(x => OpggData.Number(x,"id"))), "Default strength sort"); Equal("3,2,1", string.Join(',', OpggData.SortAugments(augments, "performance").Select(x => OpggData.Number(x,"id"))), "Zero popularity last even high performance"); Equal("2,3,1", string.Join(',', OpggData.SortAugments(augments, "popular").Select(x => OpggData.Number(x,"id"))), "Popularity sort");
Equal(false, OpggData.HasNumber(Parse("{\"ban_rate\":null}"), "ban_rate"), "Null rate is absent, not zero"); Equal(true, OpggData.HasNumber(Parse("{\"ban_rate\":0}"), "ban_rate"), "Explicit zero is present"); Equal(false, OpggData.HasWin(Parse("{\"play\":20}")), "No invented win for missing wins"); Equal(true, OpggData.HasWin(Parse("{\"win\":0,\"play\":20}")), "Zero wins valid");
Equal(5d, OpggData.DisplayRank(Parse("{}"), true, 4), "Missing rank falls back to row index"); Equal(5d, OpggData.DisplayRank(Parse("{\"rank\":0}"), false, 4), "Zero rank falls back to row index");
var calls = new List<string>();
Task<JsonObject> Successful(string path) { calls.Add(path); return Task.FromResult(Parse(path.EndsWith("/versions") ? "{\"data\":[\"16.19\",\"16.18\"]}" : "{\"data\":[]}").AsObject()); }
var loaded = await OpggData.LoadAsync(aram with { Version = "old" }, ranked, [], false, true, 17, Successful);
Equal("16.19", loaded.Query.Version, "Unsupported previous version replaced by newest"); Equal(4, calls.Count, "Versions, list, detail and Kiwi complete"); Equal(true, calls.Last().EndsWith("/aram-augments"), "Kiwi part of same transaction");
calls.Clear(); loaded = await OpggData.LoadAsync(ranked with { Position = "top", Version = "16.19" }, ranked with { Version = "16.19" }, ["16.19"], true, false, 17, Successful); Equal(1, calls.Count, "Position-only skips cached list");
using var cancel = new CancellationTokenSource(); var pending = new TaskCompletionSource<JsonObject>(); calls.Clear();
var canceled = OpggData.LoadAsync(aram, ranked, [], false, true, 17, path => { calls.Add(path); return pending.Task; }, cancel.Token); cancel.Cancel();
try { await canceled; throw new Exception("Cancelled load completed"); } catch (OperationCanceledException) { count++; }
pending.SetResult(Parse("{\"data\":[\"16.19\"]}").AsObject()); await Task.Yield(); Equal(1, calls.Count, "Cancel while versions in-flight sends no subsequent request");
calls.Clear(); bool failed = false;
try { await OpggData.LoadAsync(aram, ranked, ["16.19"], true, false, 17, path => { calls.Add(path); return path.EndsWith("/aram-augments") ? Task.FromException<JsonObject>(new IOException("Fixture augment error")) : Successful(path); }); } catch (IOException) { failed = true; }
Equal(true, failed, "Failed Kiwi cannot return successful partial snapshot");
Console.WriteLine($"OP.GG semantic contracts: {count} passed");

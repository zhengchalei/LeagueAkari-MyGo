using System.Text.Json;
using LeagueAkari.WinUI.Services;

int passed = 0;
void Check(string label, bool condition) { if (!condition) throw new Exception(label); passed++; }
JsonElement Game(bool lcu = false, bool surrender = false)
{
    var stats = new { kills = 8, deaths = 2, assists = 12, win = true, champLevel = 18, goldEarned = 12000, totalDamageDealtToChampions = 20000, totalMinionsKilled = 100, neutralMinionsKilled = 20, item0 = 3031, roleBoundItem = 4643, playerAugment1 = 1250, perk0 = 8005, perkPrimaryStyle = 8000, perkSubStyle = 8100, gameEndedInSurrender = surrender };
    if (lcu) return JsonSerializer.SerializeToElement(new { gameType = "MATCHED_GAME", gameMode = "ARAM", queueId = 450, mapId = 12, gameDuration = 1200, gameCreation = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds(), participantIdentities = new[] { new { participantId = 1, player = new { puuid = "self", gameName = "本人", tagLine = "123" } } }, participants = new[] { new { participantId = 1, teamId = 100, championId = 126, spell1Id = 4, spell2Id = 14, stats } } });
    return JsonSerializer.SerializeToElement(new { gameType = "MATCHED_GAME", gameMode = "CLASSIC", queueId = 2400, mapId = 11, gameDuration = 1200, gameCreation = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds(), participants = new object[] {
        new { puuid="self",participantId=1,teamId=100,championId=1,teamPosition="MIDDLE",kills=8,deaths=2,assists=12,win=true,champLevel=18,goldEarned=12000,totalDamageDealtToChampions=20000,item0=3031,roleBoundItem=4643,spell1Id=4,spell2Id=14,challenges=new{soloKills=2},perks=new{styles=new[]{new{style=8000,selections=new[]{new{perk=8005}}},new{style=8100,selections=new[]{new{perk=8139}}}}}},
        new { puuid="ally",participantId=2,teamId=100,championId=2,kills=12,deaths=5,assists=4,win=true,goldEarned=8000,totalDamageDealtToChampions=10000 },
        new { puuid="enemy",participantId=3,teamId=200,championId=126,kills=5,deaths=4,assists=6,win=false,gameEndedInSurrender=surrender,playerAugment1=1250,totalDamageDealtToChampions=10000,goldEarned=9000 }
    } });
}
HistoryFilterState PlayerCondition(string type,params object?[] args)
{
    var state=new HistoryFilterState();var player=state.Add("player","root",HistoryFilterArg.Param("self"),HistoryFilterArg.Node(null));state.NodeMap["root"].Args[0]=HistoryFilterArg.Node(player.Id);var leaf=state.Add(type,player.Id,args.Select(HistoryFilterArg.Param).ToArray());player.Args[1]=HistoryFilterArg.Node(leaf.Id);return state;
}
bool Advanced(HistoryFilterState state,JsonElement? game=null)=>new HistoryFilter(new(){Mode="advanced",Advanced=state}).Match(game??Game(),"self");
Check("Empty rule accepts game",Advanced(new()));
Check("Clean matched sample preset",Advanced(HistoryFilter.Preset("clean-match-samples","self")));
Check("Self strong performance preset",Advanced(HistoryFilter.Preset("strong-self-performance","self")));
Check("Enemy Jayce augment predicate",Advanced(HistoryFilter.Preset("kiwi-jayce-slow-and-steady","self")));
Check("LCU clean sample",Advanced(HistoryFilter.Preset("clean-match-samples","self"),Game(true)));
Check("LCU champLevel mapping",Advanced(PlayerCondition("levelBetween",18,18),Game(true)));
Check("Team total gold share inclusive",Advanced(PlayerCondition("goldBetween","teamShare",60,60)));
Check("Team maximum gold share",Advanced(PlayerCondition("goldBetween","teamMaxShare",100,100)));
Check("Whole game gold share",Advanced(PlayerCondition("goldBetween","gameShare",41,42)));
Check("Damage gold percentage",Advanced(PlayerCondition("dgrBetween",166,167)));
Check("Kill participation percentage",Advanced(PlayerCondition("killParticipationBetween",100,100)));
Check("SGP solo kills",Advanced(PlayerCondition("soloKillsBetween",2,2)));
Check("LCU solo kills unavailable rejects zero range",!Advanced(PlayerCondition("soloKillsBetween",0,99),Game(true)));
Check("LCU perk list",Advanced(PlayerCondition("hasPerk",8005,-1),Game(true)));
Check("LCU perk styles",Advanced(PlayerCondition("hasPerkStyle",8100,1),Game(true)));
Check("SGP perk list",Advanced(PlayerCondition("hasPerk",8139,1)));
Check("Role bound item regardless of slot",Advanced(PlayerCondition("hasItem",4643,6)));
Check("Missing item slot false",!Advanced(PlayerCondition("hasItem",3031,1)));
var simple=new HistoryFilterSettings{EnablePosition=true,Simple=new(){WinLoss="win",TimeRange="last3Hours",Positions=["MIDDLE"],ChampionIds=[1,126],SummonerPuuids=["self","enemy"]}};
Check("Simple conjunctive players and champions",new HistoryFilter(simple).Match(Game(),"self"));
simple.Simple.ChampionIds.Add(999);Check("Simple heroes AND not OR",!new HistoryFilter(simple).Match(Game(),"self"));
simple.Simple.ChampionIds.Remove(999);simple.Simple.TimeRange="last30Days";Check("Rolling date range",new HistoryFilter(simple).Match(Game(),"self"));
var invalid=PlayerCondition("isQueue",450);try {Advanced(invalid);Check("Scope violation rejects",false);} catch(InvalidOperationException){passed++;}
var cycle=new HistoryFilterState();cycle.NodeMap["root"].Args[0]=HistoryFilterArg.Node("root");try{Advanced(cycle);Check("Cycle rejects",false);}catch(InvalidOperationException){passed++;}
var restored=JsonSerializer.Deserialize<HistoryFilterSettings>(JsonSerializer.Serialize(new HistoryFilterSettings{Mode="advanced",Advanced=HistoryFilter.Preset("strong-self-performance","self")}))!;
Check("Version 1 nodeMap persistence roundtrip",new HistoryFilter(restored).Match(Game(),"self"));
var loss=PlayerCondition("isLoss",true);loss.NodeMap.Values.First(n=>n.Type=="player").Args[0]=HistoryFilterArg.Param("enemy");Check("Surrender loss filter",Advanced(loss,Game(surrender:true)));Check("Ordinary loss excluded from surrender",!Advanced(loss));
Check("Summary matches original aggregate KDA", MatchData.Summarize([Game(), Game()], "self").Kda == 10);
Check("Summary nullable LCU solo kills", MatchData.Summarize([Game(true)], "self").SoloKills is null);
Check("Summary SGP solo kills", MatchData.Summarize([Game()], "self").SoloKills == 2);
Check("Summary mixed source unavailable stays null", MatchData.Summarize([Game(), Game(true)], "self").SoloKills is null);
Check("Original kill damage efficiency", Math.Abs(MatchData.Summarize([Game()], "self").KillDamageEfficiency - .6) < .0001);
Check("Original consecutive wins", MatchData.Summarize([Game(), Game()], "self").WinningStreak == 2);
Check("Akari extraordinary needs minimum sample count", !new HistorySummary { AkariScore = 9, Count = 7 }.Extraordinary);
Check("Akari outstanding needs minimum sample count", new HistorySummary { AkariScore = 6.5, Count = 5 }.Outstanding);
var calls = new List<(string Method, object?[] Args)>();
var available = JsonSerializer.SerializeToElement(new { availability = new { sgpServerId = "TENCENT_HN1" }, isTokenReady = true, leagueServers = new { servers = new Dictionary<string, object> { ["TENCENT_HN1"] = new { matchHistory = "https://history/", common = "https://common/" }, ["TENCENT_HN2"] = new { matchHistory = "https://history2/", common = "https://common2/" } } } });
Task<JsonElement> Request(string ns, string method, object?[] args)
{
    calls.Add((method,args)); string path = args.ElementAtOrDefault(method == "sgpRequest" ? 3 : 1)?.ToString() ?? "";
    if (path.StartsWith("/player-account/lookup")) return Task.FromResult(JsonSerializer.SerializeToElement(new { namesets = new[] { new { puuid = "cross-player", gnt = new { gameName = "跨区名字", tagLine = "HN2" }, error = "" } } }));
    if (path.StartsWith("/player-account/aliases")) return Task.FromResult(JsonSerializer.SerializeToElement(new[] { new { puuid = "cross-player", alias = new { game_name = "跨区名字", tag_line = "HN2" } } }));
    if (path.StartsWith("/summoner-ledge")) return Task.FromResult(JsonSerializer.SerializeToElement(new[] { new { puuid = "cross-player", id = 123, level = 300, profileIconId = 7, expPoints = 250, expToNextLevel = 500, privacy = "PUBLIC" } }));
    if (path.StartsWith("/match-history-query")) return Task.FromResult(JsonSerializer.SerializeToElement(new { games = Array.Empty<object>() }));
    return Task.FromResult(JsonSerializer.SerializeToElement(new { queues = Array.Empty<object>() }));
}
var source = new PlayerDataSource(Request, (_,_) => Task.FromResult(available));
await source.ConfigureAsync("", "sgp"); await source.RankedAsync("self");
Check("Personal ranked always uses LCU like upstream", calls[^1].Method == "lcuRequest");
await source.ConfigureAsync("TENCENT_HN2", "lcu");
Check("Cross region forces SGP", source.Source == "sgp" && source.IsCrossRegion);
Check("Cross region optional panes unavailable", !source.SupportsRanked && !source.SupportsMastery && !source.SupportsSocialProfile);
var cross = await source.ProfileAsync("cross-player");
Check("SGP nameset completes names", cross.Text("gameName") == "跨区名字" && cross.Text("tagLine") == "HN2");
Check("SGP level and id normalized", cross.Number("summonerLevel") == 300 && cross.Number("summonerId") == 123);
var aliasProfile = await source.FindAsync("跨区名字#HN2"); Check("Cross region Riot alias resolves PUUID then SGP", aliasProfile.Text("gameName") == "跨区名字");
await source.ChallengesAsync("cross-player");
Check("Challenges exact trailing slash and token", calls[^1].Args[1]?.ToString() == "league-session" && calls[^1].Args[3]?.ToString() == "/challenges-client/v2/all-player-data/?puuid=cross-player");
var unavailable = new PlayerDataSource(Request, (_,_) => Task.FromResult(JsonSerializer.SerializeToElement(new { availability = new { sgpServerId="TENCENT_HN1" },isTokenReady=true,leagueServers=new {servers=new{}} })));
await unavailable.ConfigureAsync("", "sgp"); Check("Local missing SGP endpoint fallback",unavailable.Source=="lcu"&&unavailable.FallbackReason=="sgp-api-unavailable");
try { await unavailable.ConfigureAsync("TENCENT_HN2","lcu"); Check("Cross missing endpoint never falls back to wrong region",false); } catch(InvalidOperationException){passed++;}
var waiting=new PlayerDataSource(Request,(_,_)=>{var value=System.Text.Json.Nodes.JsonNode.Parse(available.GetRawText())!;value["isTokenReady"]=false;return Task.FromResult(JsonSerializer.SerializeToElement(value));});
try{await waiting.ConfigureAsync("","sgp");Check("Token wait preserves requested source",false);}catch(InvalidOperationException){passed++;}
await source.ConfigureAsync("", "sgp");
await source.HistoryAsync("self", 0, 20, "ranked");
Check("Original ranked tag passes through to SGP", calls[^1].Args[3]?.ToString()?.Contains("&tag=ranked") == true);
await source.HistoryAsync("self", 20, 20, "q_2450");
Check("Exact supported queue tag passes through", calls[^1].Args[3]?.ToString()?.Contains("&tag=q_2450") == true);
await source.HistoryAsync("self", 0, 20, "<akari:all>");
Check("All tag is omitted from query", calls[^1].Args[3]?.ToString()?.Contains("&tag=") == false);
var collection = HistoryCollection.ForPlayer("self", 126, null, 20, true);
Check("Collect by hero excludes games where only the opponent used it", !new HistoryFilter(collection.Filter).Match(Game(), "self"));
collection = HistoryCollection.ForPlayer("self", 1, "MIDDLE", 50, true);
Check("Champion plus position belongs to selected player", new HistoryFilter(collection.Filter).Match(Game(), "self"));
Check("Collection scans ten times target in 20-game batches", collection.BatchSize == 20 && collection.TargetCount == 50 && collection.Iterations == 25);
collection = HistoryCollection.ForPlayer("self", 1, "TOP", 1000, true);
Check("Wrong position is rejected for the selected player", !new HistoryFilter(collection.Filter).Match(Game(), "self"));
Check("Scan cap stays at original thousand games", collection.Iterations == 50);
collection = HistoryCollection.ForPlayer("self", 126, "TOP", 0, false);
Check("Unavailable LCU positions do not reject matching champion", new HistoryFilter(collection.Filter).Match(Game(true), "self"));
Check("Missing target count follows original default", collection.TargetCount == 20);
// Edit saved predicates, not just newly-created defaults: legacy ranges occur in original presets.
var legacyGold = new HistoryFilterNode { Type = "goldBetween", Args = [HistoryFilterArg.Param(12000), HistoryFilterArg.Param(999999)] };
HistoryFilterEditorData.SetMeasure(legacyGold, "teamShare");
Check("Switching a legacy numeric predicate to percentage resets its range", HistoryFilterEditorData.Range(legacyGold) == ("teamShare", 0, 100));
HistoryFilterEditorData.SetRange(legacyGold, 60, 60);
Check("Editing migrated percent range preserves measure at arg zero", legacyGold.Args.Count == 3 && legacyGold.Args[0].StringValue == "teamShare" && legacyGold.Args[1].Value.TryNumber() == 60 && legacyGold.Args[2].Value.TryNumber() == 60);
Check("Edited migrated predicate evaluates exact team share", Advanced(PlayerCondition("goldBetween", legacyGold.Args.Select(a => (object?)a.Value).ToArray())));
HistoryFilterEditorData.SetMeasure(legacyGold, "value");
Check("Returning to value mode keeps currently displayed endpoints", HistoryFilterEditorData.Range(legacyGold) == ("value", 60, 60));
var level = new HistoryFilterNode { Type = "levelBetween", Args = HistoryFilterEditorData.Defaults("levelBetween").ToList() };
Check("Original level minimum is one", HistoryFilterEditorData.Range(level) == ("value", 1, 18));
var dgr = new HistoryFilterNode { Type = "dgrBetween", Args = HistoryFilterEditorData.Defaults("dgrBetween").ToList() };
Check("Original damage gold efficiency defaults to five hundred percent", HistoryFilterEditorData.Range(dgr) == ("value", 0, 500));
foreach (string type in new[] { "killsBetween", "deathsBetween", "assistsBetween" })
    Check("Original count default: " + type, HistoryFilterEditorData.Defaults(type).Length == 3 && HistoryFilterEditorData.Defaults(type)[2].Value.TryNumber() == 999);
foreach (string type in new[] { "soloKillsBetween", "doubleKillsBetween", "tripleKillsBetween", "quadraKillsBetween", "pentaKillsBetween" })
    Check("Original multikill default: " + type, HistoryFilterEditorData.Defaults(type)[2].Value.TryNumber() == 20);
Check("Unselected player predicate remains null until user selects", HistoryFilterEditorData.Defaults("player")[0].Value.ValueKind == JsonValueKind.Null);
Check("Original add menu excludes combinators without renderer factories", HistoryFilterEditorData.Choices("game").All(s => s.Type is not ("all" or "gameCreationInTimeRange")));
Check("Participant menu excludes queue and member-scoped predicates", HistoryFilterEditorData.Choices("participant").All(s => s.Type is not ("isQueue" or "player" or "anyone")));
Check("Original game modes include retired modes for old history", HistoryFilterEditorData.GameModes.Length == 20 && HistoryFilterEditorData.GameModes.Contains("PRACTICETOOL") && HistoryFilterEditorData.GameModes.Contains("NEXUSBLITZ") && HistoryFilterEditorData.GameModes.Contains("BRAWL"));
Check("Augment fifth slot is the last supported original slot", HistoryFilterEditorData.SlotCount("hasAugment") == 5);
Check("Perk, style, spell and item slot counts remain distinct", HistoryFilterEditorData.SlotCount("hasPerk") == 6 && HistoryFilterEditorData.SlotCount("hasPerkStyle") == 2 && HistoryFilterEditorData.SlotCount("hasSpell") == 2 && HistoryFilterEditorData.SlotCount("hasItem") == 7);
var catalog = JsonSerializer.SerializeToElement(new { augments = new Dictionary<string, object>
{
    ["1250"] = new { id = 1250, name = "internal", nameTRA = "慢慢来" }, ["1000"] = new { id = 1000, nameTRA = "斗魂边界" },
    ["3000"] = new { id = 3000, nameTRA = "海克斯边界" }, ["3001"] = new { id = 3001, nameTRA = "其他" }, ["0"] = new { id = 0, nameTRA = "零" }
}});
var augmentation = HistoryFilterEditorData.Catalog(catalog, "augments");
Check("Augment labels use translated metadata rather than internal name", augmentation.Single(a => a.Id == 1250).Name == "慢慢来");
Check("Original augment groups and boundaries", augmentation.Single(a => a.Id == 1000).Group == "cherry" && augmentation.Single(a => a.Id == 3000).Group == "kiwi" && augmentation.Single(a => a.Id == 3001).Group == "other" && augmentation.Single(a => a.Id == 0).Group == "other");
Check("Hextech augment group precedes Arena and Other", augmentation[0].Group == "kiwi" && Array.FindIndex(augmentation, a => a.Group == "cherry") < Array.FindIndex(augmentation, a => a.Group == "other"));
var switchState = PlayerCondition("isChampion", 126);
var selectedNode = switchState.NodeMap.Values.Single(n => n.Type == "isChampion"); var playerScope = switchState.NodeMap.Values.Single(n => n.Type == "player");
var groupNode = switchState.Add("and", playerScope.Id, HistoryFilterArg.Node(selectedNode.Id)); playerScope.Args[1] = HistoryFilterArg.Node(groupNode.Id); selectedNode.ParentId = groupNode.Id;
var extraCondition = switchState.Add("hasItem", groupNode.Id, HistoryFilterArg.Param(3031), HistoryFilterArg.Param(-1)); groupNode.Args.Add(HistoryFilterArg.Node(extraCondition.Id));
Check("Conjunction of false champion and true item rejects game", !Advanced(switchState));
HistoryFilterEditorData.ToggleLogic(groupNode);
Check("In-place switch to OR preserves children and changes matching", groupNode.Args.Count == 2 && selectedNode.ParentId == groupNode.Id && Advanced(switchState));
switchState.Remove(selectedNode.Id);
Check("Deletion after AND to OR removes arg rather than leaving a match-all null", groupNode.Args.Count == 1 && groupNode.ArgDeleteStrategy == "remove-from-array" && Advanced(switchState));
var originalSettings = new HistoryFilterSettings { Mode = "advanced", Advanced = switchState, Simple = new() { Version = 7, ChampionIds = [126] } }; originalSettings.Advanced.CachedSummoners["self"] = JsonSerializer.SerializeToElement(new { puuid = "self", gameName = "本人" });
var draftSettings = HistoryFilterEditorData.Draft(originalSettings); draftSettings.Advanced.Clear(); draftSettings.Simple = HistoryFilterEditorData.Clear(draftSettings.Simple);
Check("Cancelling a draft leaves live advanced graph and cached summoners intact", originalSettings.Advanced.NodeMap.Count > 1 && originalSettings.Advanced.CachedSummoners.ContainsKey("self"));
Check("Cancelling a draft leaves live simple selections intact", originalSettings.Simple.ChampionIds.SequenceEqual([126]));
Check("Clearing simple preserves version and clears selections/cache", draftSettings.Simple.Version == 7 && draftSettings.Simple.ChampionIds.Count == 0 && draftSettings.Simple.CachedSummoners.Count == 0);
Check("Clearing advanced retains root with one empty node ref and clears cache", draftSettings.Advanced.NodeMap.Count == 1 && draftSettings.Advanced.NodeMap["root"].Args.Single().Kind == "node" && draftSettings.Advanced.NodeMap["root"].Args[0].Value.ValueKind == JsonValueKind.Null && draftSettings.Advanced.CachedSummoners.Count == 0);
var persisted = JsonSerializer.SerializeToElement(draftSettings);
Check("Editor draft keeps original version-one graph serialization", persisted.Field("advanced").Number("version") == 1 && persisted.Field("advanced").Text("rootId") == "root" && persisted.Field("advanced").Field("nodeMap").Field("root").Field("args").Items().Single().Text("kind") == "node");
HistoryFilterState MemberCondition(string memberType, string reference, string leafType, params object?[] parameters)
{
    var state = new HistoryFilterState(); var rootNode = state.NodeMap["root"];
    var members = state.Add(memberType, rootNode.Id, HistoryFilterArg.Param(reference), HistoryFilterArg.Node(null)); rootNode.Args[0] = HistoryFilterArg.Node(members.Id);
    var quantifier = state.Add("everyone", members.Id, HistoryFilterArg.Node(null)); members.Args[1] = HistoryFilterArg.Node(quantifier.Id);
    var leaf = state.Add(leafType, quantifier.Id, parameters.Select(HistoryFilterArg.Param).ToArray()); quantifier.Args[0] = HistoryFilterArg.Node(leaf.Id); return state;
}
var alliesFilter = MemberCondition("allies", "self", "isWin");
Check("Everyone in selected allied team tests only allied participants", Advanced(alliesFilter));
var opponentsFilter = MemberCondition("enemies", "self", "isWin");
Check("Enemy grouping remains distinct from allied team", !Advanced(opponentsFilter));
var championFilter = MemberCondition("allies", "self", "isChampion", 1);
Check("Everyone requires each participant to match", !Advanced(championFilter));
championFilter.NodeMap.Values.Single(n => n.Type == "everyone").Type = "anyone";
Check("Anyone requires one member rather than all", Advanced(championFilter));
var incompleteNot = new HistoryFilterState(); var notNode = incompleteNot.Add("not", "root", HistoryFilterArg.Node(null)); incompleteNot.NodeMap["root"].Args[0] = HistoryFilterArg.Node(notNode.Id);
Check("Incomplete NOT is match-all until its child is chosen", Advanced(incompleteNot));
var earlySurrender = System.Text.Json.Nodes.JsonNode.Parse(Game().GetRawText())!; earlySurrender["participants"]![2]!["gameEndedInEarlySurrender"] = true;
Check("Remake is not counted as a surrender loss", !Advanced(loss, JsonSerializer.SerializeToElement(earlySurrender)) && MatchData.Self(JsonSerializer.SerializeToElement(earlySurrender), "enemy")!.IsSurrender);
JsonElement NamedPlayer(string puuid, string gameName, string tagLine = "CN") => JsonSerializer.SerializeToElement(new { puuid, gameName, tagLine, profileIconId = 7 });
var playerOptions = HistoryFilterEditorData.PlayerSuggestions([NamedPlayer("other", "Cached"), NamedPlayer("self", "Updated", "1")], [NamedPlayer("self", "Page", "1"), NamedPlayer("page", "Page Player")], "self", "");
Check("Player choices merge cached and current-page identities without duplicates", playerOptions.Length == 3 && playerOptions[0].Puuid == "self");
Check("Cached identity takes precedence over an older page identity", playerOptions[0].Label == "Updated#1");
Check("Local suggestions filter name and tag case-insensitively", HistoryFilterEditorData.PlayerSuggestions([NamedPlayer("a", "Name", "CN")], [], "", "name#cn").Single().Puuid == "a");
Check("Local suggestions can filter by PUUID", HistoryFilterEditorData.PlayerSuggestions([], [NamedPlayer("puuid-x", "Name")], "", "uuid-x").Single().Puuid == "puuid-x");
Check("Typing an incomplete Riot ID does not request remote lookup", HistoryFilterEditorData.PlayerAlias("name") is null && HistoryFilterEditorData.PlayerAlias("#tag") is null && HistoryFilterEditorData.PlayerAlias("name#") is null);
Check("Remote lookup trims name and tag like the original", HistoryFilterEditorData.PlayerAlias(" Name # CN ") == "Name#CN");
var debounceWaiters = new List<TaskCompletionSource>(); var requestedAliases = new List<string>(); var requestWaiters = new Dictionary<string, TaskCompletionSource<JsonElement>>(); var busyTransitions = new List<bool>();
var remoteSearch = new HistoryFilterPlayerSearch(alias =>
{
    requestedAliases.Add(alias); var waiter = new TaskCompletionSource<JsonElement>(); requestWaiters[alias] = waiter; return waiter.Task;
}, (duration, token) =>
{
    Check("Remote search uses original 750 ms debounce", duration == TimeSpan.FromMilliseconds(750));
    var waiter = new TaskCompletionSource(); debounceWaiters.Add(waiter); return waiter.Task.WaitAsync(token);
});
var beforeLookup = remoteSearch.SearchAsync("old#CN", busyTransitions.Add);
var latestInput = remoteSearch.SearchAsync("new#CN", busyTransitions.Add);
debounceWaiters[0].SetResult();
Check("Superseded debounce returns without issuing a lookup", await beforeLookup is null && requestedAliases.Count == 0);
debounceWaiters[1].SetResult();
Check("Only the latest debounced name reaches remote lookup", requestedAliases.SequenceEqual(["new#CN"]));
var newerInput = remoteSearch.SearchAsync("newer#CN", busyTransitions.Add);
Check("Replacing input cancels an active response immediately", await latestInput is null);
requestWaiters["new#CN"].SetResult(NamedPlayer("old", "New"));
debounceWaiters[2].SetResult(); requestWaiters["newer#CN"].SetResult(NamedPlayer("latest", "Newer"));
var published = await newerInput;
Check("Late older response cannot replace latest suggestions", published?.Player.Text("puuid") == "latest" && published.Error is null && busyTransitions.Last() == false);
var leaveDraft = remoteSearch.SearchAsync("closing#CN", busyTransitions.Add); debounceWaiters[3].SetResult(); remoteSearch.Cancel();
Check("Closing or switching a draft suppresses in-flight suggestions", await leaveDraft is null);
requestWaiters["closing#CN"].SetException(new InvalidOperationException("Old draft failure"));
var failureSearch = new HistoryFilterPlayerSearch(_ => Task.FromException<JsonElement>(new InvalidOperationException("Network unavailable")), (_, _) => Task.CompletedTask);
Check("Current remote failures retain an explicit error message", (await failureSearch.SearchAsync("name#CN"))?.Error == "Network unavailable");
var emptySearch = new HistoryFilterPlayerSearch(_ => Task.FromResult(default(JsonElement)), (_, _) => Task.CompletedTask);
Check("No matching remote player is reported distinctly", (await emptySearch.SearchAsync("missing#CN"))?.Error == "not-found");
Console.WriteLine($"History filter and player data business fixtures: {passed} passed");

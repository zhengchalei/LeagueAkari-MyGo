using System.IO.Pipes;
using System.Text;
using System.Text.Json;

// An isolated protocol fixture, never linked into the production backend.
var pipeName = args.SkipWhile(a => a != "--winui-backend").Skip(1).First();
using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
await pipe.ConnectAsync(10000);
using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 65536, true) { AutoFlush = true };
using var reader = new StreamReader(pipe, Encoding.UTF8, false, 65536, true);
await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "ready" }));
var settings = new Dictionary<string, Dictionary<string, object?>>
{
    ["app-common-main"] = new() { ["preferredLolSource"] = "sgp", ["theme"] = Environment.GetEnvironmentVariable("WINUI_FIXTURE_THEME") ?? "light", ["locale"] = Environment.GetEnvironmentVariable("WINUI_FIXTURE_LOCALE") ?? "zh-CN", ["streamerMode"] = false },
    ["window-manager-main/main-window"] = new() { ["closeAction"] = Environment.GetEnvironmentVariable("WINUI_FIXTURE_CLOSE_ACTION") ?? "close", ["opacity"] = 1, ["pinned"] = false },
    ["window-manager-main/aux-window"] = new() { ["showSkinSelector"] = true, ["opacity"] = 1, ["pinned"] = false },
    ["auto-gameflow-main"] = new() { ["autoAcceptEnabled"] = false, ["autoAcceptDelaySeconds"] = 0, ["autoMatchmakingEnabled"] = false, ["autoMatchmakingDelaySeconds"] = 5, ["autoMatchmakingMinimumMembers"] = 1, ["autoMatchmakingWaitForInvitees"] = true },
    ["ongoing-game-main"] = new() { ["matchHistoryLoadCount"] = 20, ["enabled"] = true }
};
var automation = new FixtureAutomation();
var opgg = new FixtureOpgg();
var connection = new FixtureConnection();
var rewards = new FixtureRewards();
var heroes = new[] { 103, 147, 17, 238, 22, 86, 99, 51, 64, 121 };
var names = new[] { "九尾妖狐", "星籁歌姬", "迅捷斥候", "影流之主", "寒冰射手", "德玛西亚之力", "光辉女郎", "皮城女警", "盲僧", "虚空掠夺者" };
var scheduledFriends = new HashSet<string>();
bool miscSaveFailsOnce = Environment.GetEnvironmentVariable("WINUI_FIXTURE_MISC_SAVE_FAIL_ONCE") == "1";
bool championSaveFailsOnce = Environment.GetEnvironmentVariable("WINUI_FIXTURE_CHAMPION_SAVE_FAIL_ONCE") == "1";
object FixtureFriends() => new[] { ("离线好友", "offline"), ("在线好友", "chat"), ("离开好友", "away"), ("游戏好友", "dnd") }.Select((friend, index) => new { puuid = "fixture-friend-" + index, gameName = friend.Item1, gameTag = "CN" + index, availability = friend.Item2, icon = 29, summonerId = 2000 + index, id = "chat-" + index }).ToArray();
var phase = Environment.GetEnvironmentVariable("WINUI_FIXTURE_PHASE") ?? "ChampSelect";
var lifecycleControl = Environment.GetEnvironmentVariable("WINUI_FIXTURE_LIFECYCLE_CONTROL");
string lifecycleRevision = "";
var notificationControl = Environment.GetEnvironmentVariable("WINUI_FIXTURE_NOTIFICATION_CONTROL");
string notificationRevision = "";
var notificationStates = new Dictionary<string, JsonElement>();
var endpointSubscriptions = new Dictionary<string, string>();
int endpointSubscriptionId = 0;
var fixtureStarted = DateTimeOffset.UtcNow;
var historySource = Environment.GetEnvironmentVariable("WINUI_FIXTURE_SOURCE") == "lcu" ? "lcu" : "sgp";
Set("app-common-main")["preferredLolSource"] = historySource;
Set("player-tabs-renderer")["matchHistoryUseSgpApi"] = historySource == "sgp";
bool fixtureThreeChoices = Environment.GetEnvironmentVariable("WINUI_FIXTURE_MINI_SCENARIO") == "three";
bool fixtureSkinFailsOnce = Environment.GetEnvironmentVariable("WINUI_FIXTURE_SKIN_FAIL_ONCE") == "1";
int fixtureSkinDelay = int.TryParse(Environment.GetEnvironmentVariable("WINUI_FIXTURE_SKIN_DELAY_MS"), out var skinDelay) ? Math.Clamp(skinDelay, 0, 10000) : 0;
int fixtureRerolls = int.TryParse(Environment.GetEnvironmentVariable("WINUI_FIXTURE_REROLLS"), out var rerolls) ? Math.Max(0, rerolls) : 2;
int selectedHero = fixtureThreeChoices ? 0 : 103, selectedSkin = fixtureThreeChoices ? 0 : 103001;
string fixtureReplayState = "download";
bool fixtureEncounters = Environment.GetEnvironmentVariable("WINUI_FIXTURE_ENCOUNTERS") == "1";
bool fixtureJungle = Environment.GetEnvironmentVariable("WINUI_FIXTURE_JUNGLE") == "1";
// Explicitly simulated saved-player rows, scoped to the fixture's own identities.
var encounterRows = fixtureEncounters ? Enumerable.Range(1, 21).SelectMany(match => new[] { 1, 5 }.Select(target => new Dictionary<string, object?> { ["id"] = 9000 + match * 10 + target, ["gameId"] = 500000 + match, ["selfPuuid"] = "fixture-player-0", ["puuid"] = "fixture-player-" + target, ["region"] = "TENCENT", ["rsoPlatformId"] = "HN1", ["queueType"] = "KIWI", ["updateAt"] = fixtureStarted.AddHours(-match).ToString("O") })).ToList() : new List<Dictionary<string, object?>>();
var notifications = new FixtureNotifications(Environment.GetEnvironmentVariable("WINUI_FIXTURE_NOTIFICATIONS") ?? "", int.TryParse(Environment.GetEnvironmentVariable("WINUI_FIXTURE_COUNTDOWN_SECONDS"), out var notificationSeconds) ? notificationSeconds : 120);
Set("app-common-main")["showFreeSoftwareDeclaration"] = Environment.GetEnvironmentVariable("WINUI_FIXTURE_DECLARATION") == "1";
Set("auto-gameflow-main")["autoAcceptEnabled"] = notifications.WillAccept;
Set("auto-gameflow-main")["autoAcceptDelaySeconds"] = notificationSeconds > 0 ? notificationSeconds : 120;
Set("auto-gameflow-main")["autoMatchmakingEnabled"] = notifications.WillSearch;
var fixtureLog = Environment.GetEnvironmentVariable("WINUI_FIXTURE_LOG") ?? Path.Combine(AppContext.BaseDirectory, "actions.jsonl");
void EnsureLogDirectory(string file) { var parent = Path.GetDirectoryName(Path.GetFullPath(file)); if (parent != null) Directory.CreateDirectory(parent); }
EnsureLogDirectory(fixtureLog);
if (Environment.GetEnvironmentVariable("WINUI_FIXTURE_REQUEST_LOG") is { Length: > 0 } initialRequestLog) EnsureLogDirectory(initialRequestLog);
var resource = Environment.GetEnvironmentVariable("WINUI_FIXTURE_ASSET") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../desktop/build/icon.png"));
if (!File.Exists(resource)) resource = Path.Combine(Environment.CurrentDirectory, "desktop/build/icon.png");
byte[] icon = File.Exists(resource) ? File.ReadAllBytes(resource) : Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");
Dictionary<string, object?> Set(string ns) => settings.TryGetValue(ns, out var found) ? found : settings[ns] = new();
object Profile(int i) => new { puuid = "fixture-player-" + i, gameName = "测试玩家" + i, tagLine = "CN" + i, summonerLevel = 180 + i, summonerId = 1000 + i, profileIconId = 29, xpSinceLastLevel = 120, xpUntilNextLevel = 880 };
int ProfileIndex(string puuid) => int.TryParse(puuid.Split('-').Last(), out var index) && index is >= 0 and < 10 ? index : throw new InvalidOperationException("模拟资料未找到玩家: " + puuid);
string[] RequestedPuuids(JsonElement body) => (body.ValueKind == JsonValueKind.Array ? body.EnumerateArray() : body.ValueKind == JsonValueKind.Object && body.TryGetProperty("puuids", out var puuids) && puuids.ValueKind == JsonValueKind.Array ? puuids.EnumerateArray() : Enumerable.Empty<JsonElement>()).Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).ToArray();
object Rank()
{
    var solo = new { queueType = "RANKED_SOLO_5x5", tier = "MASTER", division = "I", leaguePoints = 243, wins = 54, losses = 40, highestTier = "GRANDMASTER", highestDivision = "I", previousSeasonEndTier = "DIAMOND", previousSeasonEndDivision = "II", previousSeasonHighestTier = "MASTER", previousSeasonHighestDivision = "I" };
    var flex = new { queueType = "RANKED_FLEX_SR", tier = "DIAMOND", division = "I", leaguePoints = 57, wins = 65, losses = 54, highestTier = "MASTER", highestDivision = "I", previousSeasonEndTier = "EMERALD", previousSeasonEndDivision = "I", previousSeasonHighestTier = "DIAMOND", previousSeasonHighestDivision = "IV" };
    var tft = new { queueType = "RANKED_TFT", tier = "GOLD", division = "II", leaguePoints = 32, wins = 13, losses = 17, highestTier = "PLATINUM", highestDivision = "I", previousSeasonEndTier = "SILVER", previousSeasonEndDivision = "I", previousSeasonHighestTier = "GOLD", previousSeasonHighestDivision = "IV" };
    return new { queueMap = new { RANKED_SOLO_5x5 = solo, RANKED_FLEX_SR = flex, RANKED_TFT = tft }, queues = new object[] { solo, flex, tft } };
}
Dictionary<string, object?> Stats(int i, int match) => new() { ["kills"] = 5 + i, ["deaths"] = 3 + i % 4, ["assists"] = 10 + i, ["win"] = i < 5 == (match % 2 == 0), ["totalDamageDealtToChampions"] = 12000 + i * 1800, ["physicalDamageDealtToChampions"] = 3000 + i * 800, ["magicDamageDealtToChampions"] = 8000 + i * 500, ["trueDamageDealtToChampions"] = 1000 + i * 500, ["totalDamageTaken"] = 10000 + i * 1500, ["goldEarned"] = 10000 + i * 700, ["totalMinionsKilled"] = 120 + i * 5, ["neutralMinionsKilled"] = i * 3, ["visionScore"] = 15 + i, ["totalHeal"] = 200 + i * 300, ["item0"] = 1001, ["item1"] = 3006, ["item2"] = 3031, ["item3"] = 3085, ["item4"] = 3072, ["item5"] = 0, ["item6"] = 3363, ["perk0"] = 8005, ["perk1"] = 9111, ["perk2"] = 9104, ["perk3"] = 8014, ["perk4"] = 8345, ["perk5"] = 8347, ["perk0Var1"] = 1437, ["perk0Var2"] = 2, ["perk0Var3"] = 3, ["doubleKills"] = i % 3, ["playerAugment1"] = 100, ["enemyMissingPings"] = i, ["soloKills"] = i % 3, ["damageSelfMitigated"] = i * 1000 };
object Game(int match, bool lcu = false)
{
    var players = Enumerable.Range(0, 10).Select(i => lcu ? (object)new { participantId = i + 1, teamId = i < 5 ? 100 : 200, championId = heroes[i], spell1Id = 4, spell2Id = 14, stats = Stats(i, match) } : (object)new { participantId = i + 1, teamId = i < 5 ? 100 : 200, championId = heroes[i], puuid = "fixture-player-" + i, riotIdGameName = "测试玩家" + i, riotIdTagline = "CN" + i, summoner1Id = 4, summoner2Id = 14, teamPosition = new[] { "TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY" }[i % 5], kills = 5 + i, deaths = 3 + i % 4, assists = 10 + i, win = i < 5 == (match % 2 == 0), totalDamageDealtToChampions = 12000 + i * 1800, physicalDamageDealtToChampions = 3000 + i * 800, magicDamageDealtToChampions = 8000 + i * 500, trueDamageDealtToChampions = 1000 + i * 500, totalDamageTaken = 10000 + i * 1500, goldEarned = 10000 + i * 700, totalMinionsKilled = 120 + i * 5, neutralMinionsKilled = i * 3, visionScore = 15 + i, totalHeal = 200 + i * 300, item0 = 1001, item1 = 3006, item2 = 3031, item3 = 3085, item4 = 3072, item5 = 0, item6 = 3363, perks = new { styles = new[] { new { style = 8000, selections = new[] { new { perk = 8005, var1 = 1437, var2 = 2, var3 = 3 }, new { perk = 9111, var1 = 100, var2 = 0, var3 = 0 } } } }, statPerks = new { offense = 5005, flex = 5008, defense = 5002 } }, challenges = new { soloKills = i % 3, enemyMissingPings = i } }).ToArray();
    if (Environment.GetEnvironmentVariable("WINUI_FIXTURE_TEAM_DETAILS") == "1")
        for (var index = 0; index < players.Length; index++)
        {
            var player = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(players[index]))!;
            var stats = lcu ? player["stats"]! : player;
            stats["champLevel"] = 18; stats["physicalDamageTaken"] = 3000 + index * 400;
            stats["magicDamageTaken"] = 6000 + index * 900; stats["trueDamageTaken"] = 1000 + index * 200;
            stats["playerAugment1"] = 9000; stats["playerAugment6"] = index == 0 ? 9000 : 0;
            if (index == 0) stats["roleBoundItem"] = 1001;
            players[index] = player;
        }
    if (fixtureJungle)
    {
        // Only the history fixture changes: eligibility must be independent of the current KIWI roster.
        var first = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(players[0]))!;
        first[lcu ? "spell2Id" : "summoner2Id"] = 11;
        if (!lcu) first["teamPosition"] = "JUNGLE";
        players[0] = first;
    }
    return new { gameId = 500000 + match, gameMode = fixtureJungle ? "CLASSIC" : "KIWI", gameType = "MATCHED_GAME", mapId = fixtureJungle ? 11 : 12, queueId = fixtureJungle ? 420 : 2400, gameDuration = 900, gameCreation = DateTimeOffset.UtcNow.AddHours(-match).ToUnixTimeMilliseconds(), gameVersion = "16.19.1", participants = players, participantIdentities = Enumerable.Range(0, 10).Select(i => new { participantId = i + 1, player = Profile(i) }).ToArray(), teams = new[] { new { teamId = 100, win = match % 2 == 0 ? "Win" : "Fail", towerKills = 4, baronKills = 0, dragonKills = 0 }, new { teamId = 200, win = match % 2 == 1 ? "Win" : "Fail", towerKills = 1, baronKills = 0, dragonKills = 0 } } };
}
object Timeline() => new { frames = Enumerable.Range(0, 16).Select(minute => new { timestamp = minute * 60000, participantFrames = Enumerable.Range(1, 10).ToDictionary(i => i.ToString(), i => (object)new { participantId = i, totalGold = 500 + minute * (600 + i * 12), minionsKilled = minute * 8 + i, jungleMinionsKilled = minute, xp = minute * 800 + i * 20, level = Math.Min(18, 1 + minute), position = new { x = 5000 + i * 300, y = 6000 + i * 150 }, damageStats = new { totalDamageDoneToChampions = minute * (1000 + i * 100), totalDamageTaken = minute * (800 + i * 150) }, championStats = new { health = 1000 + minute * 80, healthMax = 1600 + minute * 80, healthRegen = 10, attackDamage = 120, abilityPower = 80, armor = 75, magicResist = 50, movementSpeed = 360, attackSpeed = 1.3, abilityHaste = 30 } }), events = minute % 3 == 0 ? new object[] { new { type = "CHAMPION_KILL", timestamp = minute * 60000 + 12000, killerId = 3, victimId = 8, assistingParticipantIds = new[] { 1, 2 }, position = new { x = 6700, y = 7500 }, victimDamageReceived = new[] { new { participantId = 3, type = "OTHER", name = "Ahri", spellName = "AhriOrb", spellSlot = 0, basic = false, physicalDamage = 0, magicDamage = 240, trueDamage = 110 } }, victimDamageDealt = new[] { new { participantId = 8, type = "OTHER", name = "Caitlyn", spellName = "basicattack", spellSlot = 64, basic = true, physicalDamage = 350, magicDamage = 0, trueDamage = 0 } } }, new { type = "ITEM_PURCHASED", timestamp = minute * 60000 + 10000, participantId = 1, itemId = 1001 }, new { type = "SKILL_LEVEL_UP", timestamp = minute * 60000 + 15000, participantId = 1, skillSlot = minute % 4 + 1, levelUpType = "NORMAL" } } : Array.Empty<object>() }).ToArray() };
object LcuTimeline()
{
    var data = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Timeline()))!;
    foreach (var frame in data["frames"]!.AsArray())
    {
        foreach (var participant in frame!["participantFrames"]!.AsObject())
        {
            participant.Value!.AsObject().Remove("damageStats");
            participant.Value.AsObject().Remove("championStats");
        }
        foreach (var gameEvent in frame["events"]!.AsArray())
        {
            gameEvent!.AsObject().Remove("victimDamageReceived");
            gameEvent.AsObject().Remove("victimDamageDealt");
        }
    }
    return data;
}
object RawGameData() => new { champions = heroes.Select((id, i) => (id, i)).ToDictionary(pair => pair.id.ToString(), pair => (object)new { name = names[pair.i], squarePortraitPath = $"/lol-game-data/assets/v1/champion-icons/{pair.id}.png" }), items = new[] { 1001, 3006, 3031, 3085, 3072, 3363 }.ToDictionary(id => id.ToString(), id => (object)new { name = "测试装备" + id, iconPath = $"/lol-game-data/assets/v1/items/{id}.png", description = "用于原生图像与弹窗验证" }), summonerSpells = new Dictionary<string, object> { { "4", new { name = "闪现", cooldown = 300, iconPath = "/lol-game-data/assets/v1/spells/flash.png" } }, { "14", new { name = "点燃", cooldown = 180, iconPath = "/lol-game-data/assets/v1/spells/ignite.png" } } }, perks = new[] { 8005, 9111, 9104, 8014, 8345, 8347, 5005, 5008, 5002 }.ToDictionary(id => id.ToString(), id => (object)new { name = "测试符文" + id, iconPath = $"/lol-game-data/assets/v1/perks/{id}.png", endOfGameStatDescriptions = new[] { "造成伤害 @eogvar1@", "触发次数 @eogvar2@" }, longDesc = "符文的原生文本说明" }) };
object GameData()
{
    var data = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(RawGameData()))!;
    if (Environment.GetEnvironmentVariable("WINUI_FIXTURE_TEAM_DETAILS") == "1")
    {
        data["augments"] = JsonSerializer.SerializeToNode(new Dictionary<string, object> { ["100"] = new { nameTRA = "测试强化", augmentSmallIconPath = "/lol-game-data/assets/v1/augments/100.png", description = "用于原生强化验证" } });
        data["perkstyles"] = JsonSerializer.SerializeToNode(new { styles = new Dictionary<string, object> { ["8300"] = new { name = "启迪", iconPath = "/lol-game-data/assets/v1/perkstyles/8300.png" } } });
    }
    return data;
}
object ClientPart(string state) => state switch
{
    "state" => connection.Enabled ? connection.State() : new { connectionState = "connected", auth = new { region = "TENCENT", rsoPlatformId = "HN1" } },
    "summoner" => new { me = Profile(0) },
    "gameData" => FixtureChampionConfig.Enhance(opgg.EnhanceGameData(GameData())),
    "gameflow" => new { phase, session = new { phase, gameData = new { gameId = 500001, queue = new { name = "海克斯大乱斗", gameMode = "KIWI", id = 2400 }, teamOne = Enumerable.Range(0, 5).Select(i => new { puuid = "fixture-player-" + i, selectedPosition = "TOP" }).ToArray(), teamTwo = Enumerable.Range(5, 5).Select(i => new { puuid = "fixture-player-" + i, selectedPosition = "TOP" }).ToArray(), playerChampionSelections = Enumerable.Range(0, 10).Select(i => new { puuid = "fixture-player-" + i, championId = heroes[i], spell1Id = 4, spell2Id = 14 }).ToArray() }, map = new { name = "嚎哭深渊", assets = new Dictionary<string, string>() } } },
    "champSelect" => new { currentPickableChampionIds = heroes.Take(7).ToArray(), currentBannableChampionIds = heroes.Take(9).ToArray(), disabledChampionIds = new[] { 121 }, session = phase == "ChampSelect" ? (object)new { isSpectating = false, localPlayerCellId = 0, allowSubsetChampionPicks = fixtureThreeChoices, allowRerolling = !fixtureThreeChoices, timer = new { phase = "FINALIZATION" }, actions = new[] { new[] { new { type = "pick", actorCellId = 0, championId = selectedHero, completed = selectedHero > 0, isInProgress = selectedHero == 0 } } } } : null },
    "matchmaking" => new { readyCheck = new { playerResponse = "None" }, search = new { timeInQueue = 38, estimatedQueueTime = 60 } },
    "lobby" => new { lobby = phase == "Lobby" ? (object)new { id = "fixture-room", localMember = new { allowedInviteOthers = true } } : null },
    _ => new { }
};
object ClientEnvelope() => new { state = ClientPart("state"), summoner = ClientPart("summoner"), gameData = ClientPart("gameData"), gameflow = ClientPart("gameflow"), champSelect = ClientPart("champSelect"), matchmaking = ClientPart("matchmaking") };
object Ongoing() => new { queryStage = new { phase = "champ-select", gameInfo = new { puuid = "fixture-player-0" } }, teams = new Dictionary<string, string[]> { { "TEAM-100", Enumerable.Range(0, 5).Select(i => "fixture-player-" + i).ToArray() }, { "TEAM-200", Enumerable.Range(5, 5).Select(i => "fixture-player-" + i).ToArray() } }, championSelections = Enumerable.Range(0, 10).ToDictionary(i => "fixture-player-" + i, i => heroes[i]), positionAssignments = new { }, additional = new { }, teamParticipantGroups = new { } };
object All() => new { summoner = Enumerable.Range(0, 10).ToDictionary(i => "fixture-player-" + i, i => Profile(i)), rankedStats = Enumerable.Range(0, 10).ToDictionary(i => "fixture-player-" + i, i => Rank()), matchHistory = Enumerable.Range(0, 10).ToDictionary(i => "fixture-player-" + i, i => (object)new { source = historySource, data = Enumerable.Range(1, 5).Select(n => Game(n, historySource == "lcu")).ToArray() }), savedInfo = new { } };
object Mini() => new { snapshot = new { Connected = true, Phase = phase, ChampionID = selectedHero, ChampionName = selectedHero > 0 ? names[Array.IndexOf(heroes, selectedHero)] : "请选择英雄", IconPath = $"/lol-game-data/assets/v1/champion-icons/{selectedHero}.png", Choices = heroes.Take(fixtureThreeChoices ? 3 : 5).Select((id, i) => new { ID = id, Name = names[i], IconPath = $"/lol-game-data/assets/v1/champion-icons/{id}.png", Selected = id == selectedHero, Enabled = phase == "ChampSelect", Buffs = i % 3, Nerfs = 1 }), Balance = new[] { new { Type = "damage-dealt", Name = "造成伤害", Value = "+5%", Original = "105%", Effect = "buffed" }, new { Type = "damage-taken", Name = "承受伤害", Value = "−10%", Original = "90%", Effect = "buffed" }, new { Type = "healing", Name = "治疗效果", Value = "−20%", Original = "80%", Effect = "nerfed" } }, BalanceKnown = true, Notes = new[] { "测试快照：数据仅用于原生界面验收" }, Source = "Bilibili RESG · 测试", SourceURL = "https://www.bilibili.com/toy/resg/index.html", Version = "16.19", Cached = true, Skins = Enumerable.Range(0, 24).Select(i => new { ID = selectedHero * 1000 + i, Name = i == 0 ? "经典皮肤" : i == 2 ? "测试炫彩 2" : "测试皮肤 " + i, ImagePath = $"/lol-game-data/assets/v1/skins/{selectedHero * 1000 + i}.png", Selected = selectedSkin == selectedHero * 1000 + i, Enabled = phase == "ChampSelect" }), SkinsLoading = false, Status = "测试选人快照", Error = "", CanSelectSkin = true, ShowSkins = true, SelectedSkinID = selectedSkin, ShowReroll = !fixtureThreeChoices, Rerolls = fixtureRerolls, CanReroll = !fixtureThreeChoices && fixtureRerolls > 0 }, controls = new { Theme = Set("app-common-main")["theme"], Locale = Set("app-common-main")["locale"], Pinned = Set("window-manager-main/aux-window")["pinned"], CanDodge = phase == "ChampSelect", TemporarilyDisabled = false, CanAccept = phase == "ReadyCheck", CanCancel = phase == "Matchmaking", CanCancelAutoAccept = notifications.WillAccept, CanCancelAutoMatchmaking = notifications.WillSearch, Plans = Array.Empty<string>() }, state = ClientEnvelope(), automation = new Dictionary<string, object> { ["auto-gameflow-main"] = notifications.Flow() }, settings = new { autoGameflow = Set("auto-gameflow-main") } };
object BaseSnapshot(string ns, string state) => state == "settings" ? Set(ns) : ns == "league-client-ux-main" && connection.Enabled ? connection.Ux() : notifications.Snapshot(ns) ?? (ns switch { "league-client-main" => ClientPart(state), "sgp-main" => new { availability = new { region = "TENCENT", sgpServerId = "TENCENT_HN1" }, leagueServers = new { tencentServerMatchHistoryInteroperability = new[] { "TENCENT_HN1", "TENCENT_HN10" }, servers = new Dictionary<string, object> { { "TENCENT_HN1", new { matchHistory = "https://fixture.invalid", common = "https://fixture.invalid" } }, { "TENCENT_HN10", new { matchHistory = "https://fixture.invalid", common = "https://fixture.invalid" } } } }, isTokenReady = true }, "extra-assets-main" => state == "gtimg" ? automation.ExtraAssets : state == "kiwi" ? opgg.KiwiBalance : new { }, "auto-select-main" => automation.State, "ongoing-game-main" => Ongoing(), "window-manager-main/cd-timer-window" => new { gameTime = 420, supportedGameModes = new[] { new { gameMode = "KIWI", abilityHaste = 70 } } }, "app-common-main" => new { nativeSupport = new { nativeInput = new { available = true } }, isElevated = false }, _ => new { } });
object Snapshot(string ns, string state)
{
    var snapshot = JsonSerializer.SerializeToElement(BaseSnapshot(ns, state));
    if (ns == "auto-gameflow-main" && state != "settings")
    {
        var flow = snapshot.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        flow["friendsToBeInvited"] = JsonSerializer.SerializeToElement(scheduledFriends.ToArray()); snapshot = JsonSerializer.SerializeToElement(flow);
    }
    if (state == "settings") return snapshot;
    if (!notificationStates.TryGetValue(ns + ":" + (state.Length == 0 ? "state" : state), out var patch)) return snapshot;
    var fields = snapshot.ValueKind == JsonValueKind.Object ? snapshot.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) : new Dictionary<string, JsonElement>();
    foreach (var field in patch.EnumerateObject()) fields[field.Name] = field.Value.Clone();
    return JsonSerializer.SerializeToElement(fields);
}
bool detailFailOnce = Environment.GetEnvironmentVariable("WINUI_FIXTURE_DETAILS_FAIL_ONCE") == "1";
bool timelineFailOnce = Environment.GetEnvironmentVariable("WINUI_FIXTURE_TIMELINE_FAIL_ONCE") == "1";
object Api(string path, bool lcu = false, string httpMethod = "GET", JsonElement body = default)
{
    if (path == "/lol-chat/v1/friends") return FixtureFriends();
    if (detailFailOnce && (path.EndsWith("/SUMMARY") || path.Contains("/games/"))) { detailFailOnce = false; throw new InvalidOperationException("模拟读取对局详情失败"); }
    if (timelineFailOnce && (path.EndsWith("/DETAILS") || path.Contains("game-timelines"))) { timelineFailOnce = false; throw new InvalidOperationException("模拟读取时间线失败"); }
    if (rewards.Request(httpMethod, path) is { } rewardResult) return rewardResult;
    if (path == "/lol-settings/v2/account/GamePreferences/game-settings") return new { data = new { HUD = new { HidePlayerNames = false } } };
    if (path == "/lol-game-data/assets/v1/champion-summary.json") return heroes.Select((id, i) => new { id, name = names[i], squarePortraitPath = $"/lol-game-data/assets/v1/champion-icons/{id}.png" }).ToArray();
    if(path == "/lol-perks/v1/recommended-champion-positions") return automation.Recommendations;
    if (fixtureEncounters && (path.EndsWith("/SUMMARY") || path.Contains("/games/")))
    {
        var idText = path.EndsWith("/SUMMARY") ? path.Split('/')[^2].Split('_').Last() : path.Split('/').Last().Split('?')[0];
        if (long.TryParse(idText, out var id) && id is > 500000 and <= 500021)
        {
            int match = (int)(id - 500000); var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Game(match, lcu)))!;
            // On alternate games swap one ally/opponent. Hero and KDA remain
            // distinct, so both relationship branches are visibly testable.
            if (match % 2 == 1) foreach (int i in new[] { 1, 5 }) { int team = i == 1 ? 200 : 100; node["participants"]![i]!["teamId"] = team; var stats = lcu ? node["participants"]![i]!["stats"]! : node["participants"]![i]!; stats["win"] = team == 100 == (match % 2 == 0); }
            node["gameCreation"] = fixtureStarted.AddHours(-match).ToUnixTimeMilliseconds();
            return path.EndsWith("/SUMMARY") ? new { json = (object)node } : node;
        }
    }
    if (path.Contains("/SUMMARY?")) return new { games = Enumerable.Range(1, 8).Select(n => new { json = Game(n) }).ToArray() };
    if (path.EndsWith("/SUMMARY")) return new { json = Game(1) };
    if (path.EndsWith("/DETAILS")) return new { json = Timeline() };
    if (path.Contains("game-timelines")) return LcuTimeline();
    if (path.Contains("/matches?")) return new { games = new { games = Enumerable.Range(1, 8).Select(n => Game(n, true)).ToArray() } };
    if (path.Contains("/games/")) return Game(1, true);
    if (path.Contains("rankedStats") || path.Contains("ranked-stats")) return Rank();
    if (path.Contains("champion-mastery")) return heroes.Select(id => new { championId = id, championLevel = 12, championPoints = 126800 }).ToArray();
    if (path.Contains("aliases")) return new[] { Profile(1) };
    if (path.Contains("namesets-for-puuids")) return new { namesets = RequestedPuuids(body).Select(puuid => { int i = ProfileIndex(puuid); return new { puuid, gnt = new { gameName = "测试玩家" + i, tagLine = "CN" + i }, error = "" }; }).ToArray() };
    if (path.Contains("summoners/puuids")) return RequestedPuuids(body).Select(puuid => { int i = ProfileIndex(puuid); return new { puuid, id = 1000 + i, level = 180 + i, profileIconId = 29, expPoints = 120, expToNextLevel = 880, privacy = "PUBLIC" }; }).ToArray();
    if (path.Contains("/lol-summoner/v2/summoners/puuid/")) return Profile(ProfileIndex(Uri.UnescapeDataString(path.Split('/').Last())));
    if (path.Contains("summoner")) return Profile(0);
    if (path == "/lol-replays/v1/configuration") return new { isReplaysEnabled = true, gameVersion = "16.19.1", isReplaysForMatchHistoryEnabled = true };
    if (path.Contains("/lol-replays/v1/metadata/")) return new { state = fixtureReplayState, gameId = long.TryParse(path.Split('/').Last(), out var replayId) ? replayId : 0, downloadProgress = fixtureReplayState == "watch" ? 100 : 0 };
    if (httpMethod == "POST" && path.Contains("/lol-replays/v1/rofls/") && path.EndsWith("/download")) { fixtureReplayState = "watch"; return new { }; }
    return new { };
}
object FixtureAnalysis(string id) => new {
 count=5, summary=new { avgEnemyMissingPings=1.2,avgKillDamageEfficiency=1.5 }, spells=new{flashOnD=3,flashOnF=2}, detailsCount=4, details=new{avgEarlyDeathsWithEnemyJunglerInvolved=1.6},
 akariScore=new{kdaScore=.65,winRateScore=.2,damageScore=1.2,total=7.1,outstanding=true,extraordinary=false},
 jungle=new{gamesAnalyzed=4,avgTopZonePercentage=.2,avgMidZonePercentage=.3,avgBotZonePercentage=.5,topZoneWeightSum=20,midZoneWeightSum=30,botZoneWeightSum=50,minutePositions=Enumerable.Range(0,14).Select(i=>new{x=3000+i*600,y=9000-i*400,lane="bot",minute=i}),gankPositions=new[]{new{x=9000,y=4500,lane="bot"}},firstClearCamp=new{blue=new{red=2,blue=1,wolves=1,raptors=0},red=new{red=1,blue=2,wolves=0,raptors=1}},earlyGank=new{level3GankRate=.5,level3GankCount=2,level4GankRate=.25,level4GankCount=1},objectives=new{firstDragonRate=.75,soloDragonRate=.5,avgDragons=2.0,avgVoidgrubs=3.0,avgHeralds=1.0,avgBarons=.5,avgFirstDragonTime=360000}}, champions=new{} };
while (await reader.ReadLineAsync() is { } line)
{
    using var document = JsonDocument.Parse(line); var message = document.RootElement; if (message.GetProperty("type").GetString() != "call") continue;
    var ns = message.GetProperty("namespace").GetString()!; var method = message.GetProperty("method").GetString()!; var values = message.GetProperty("args").EnumerateArray().ToArray();
    // Test-only input: dispatch the same incremental events as LCU when a scenario
    // changes phase. Consume on the next UI request, keeping all pipe writes serialized.
    if (!string.IsNullOrEmpty(lifecycleControl) && File.Exists(lifecycleControl))
    {
        using var control = JsonDocument.Parse(await File.ReadAllTextAsync(lifecycleControl));
        string revision = control.RootElement.GetProperty("revision").GetString()!;
        if (revision != lifecycleRevision)
        {
            lifecycleRevision = revision;
            phase = control.RootElement.GetProperty("phase").GetString()!;
            foreach (var (section, key, value) in new[] {
                ("gameflow", "phase", JsonSerializer.SerializeToElement(phase)),
                ("lobby", "lobby", JsonSerializer.SerializeToElement(ClientPart("lobby")).GetProperty("lobby").Clone()),
                ("champSelect", "session", JsonSerializer.SerializeToElement(ClientPart("champSelect")).GetProperty("session").Clone()) })
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "event", @event = new { @namespace = "mobx-utils-main", name = "update-state-prop/league-client-main:" + section, args = new object[] { key, value, new { action = "update", raw = true } } } }));
        }
    }
    // File-controlled notification transitions remain inside this simulator.
    // Consume them on requests, so state and protocol events use one pipe writer.
    if (!string.IsNullOrEmpty(notificationControl) && File.Exists(notificationControl))
    {
        using var control = JsonDocument.Parse(await File.ReadAllTextAsync(notificationControl));
        string revision = control.RootElement.GetProperty("revision").GetString()!;
        if (revision != notificationRevision)
        {
            notificationRevision = revision;
            foreach (var section in control.RootElement.GetProperty("states").EnumerateObject())
            {
                int separator = section.Name.LastIndexOf(':');
                if (separator < 0 || section.Value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Fixture notification section requires namespace:state");
                string owner = section.Name[..separator], kind = section.Name[(separator + 1)..];
                var merged = notificationStates.TryGetValue(section.Name, out var prior) ? prior.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) : new Dictionary<string, JsonElement>();
                foreach (var property in section.Value.EnumerateObject())
                {
                    merged[property.Name] = property.Value.Clone();
                    if (kind == "settings") Set(owner)[property.Name] = property.Value.Clone();
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "event", @event = new { @namespace = "mobx-utils-main", name = "update-state-prop/" + section.Name, args = new object[] { property.Name, property.Value.Clone(), new { action = "update", raw = true } } } }));
                }
                notificationStates[section.Name] = JsonSerializer.SerializeToElement(merged);
            }
            if (control.RootElement.TryGetProperty("events", out var events))
                foreach (var envelope in events.EnumerateArray())
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "event", @event = envelope.Clone() }));
        }
    }
    if (Environment.GetEnvironmentVariable("WINUI_FIXTURE_REQUEST_LOG") is { Length: > 0 } requestLog)
        await File.AppendAllTextAsync(requestLog, JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ns, method, args = values }) + "\n");
    string A(int index) => index < values.Length && values[index].ValueKind == JsonValueKind.String ? values[index].GetString()! : "";
    object? result = null; string? error = null;
    try
    {
        if (ns == "league-client-main" && method == "http-request")
        { var request = values[0]; result = new { data = Api(request.GetProperty("url").GetString()!, true, request.GetProperty("method").GetString()!, request.TryGetProperty("data", out var body) ? body : default) }; }
        if (ns == "league-client-main" && method == "subscribeLcuEndpoint")
        { string id = "fixture-subscription-" + ++endpointSubscriptionId; endpointSubscriptions[id] = A(0); result = id; }
        if (ns == "league-client-main" && method == "unsubscribeLcuEndpoint") result = endpointSubscriptions.Remove(A(0));
        if (ns == "league-client-main" && method == "peekClient" && connection.Enabled) result = connection.Peek(values[0], icon);
        if (ns == "league-client-main" && connection.Action(method, values) is { } connectionState)
        {
            foreach (var property in JsonSerializer.SerializeToElement(connectionState).EnumerateObject())
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "event", @event = new { @namespace = "mobx-utils-main", name = "update-state-prop/league-client-main:state", args = new object[] { property.Name, property.Value.Clone(), new { action = "update", raw = true } } } }));
            result = new { };
        }
        if (ns.StartsWith("window-manager-main/") && method is "toggle" or "show" or "hide" or "resetPosition")
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "host-call", id = "fixture-window-" + message.GetProperty("id").GetString(), @namespace = ns, method, args = values }));
        if (ns == "winui-backend") result = method switch { "snapshot" => Snapshot(A(0), A(1)), "miniSnapshot" => Mini(), "publicRequest" => await opgg.RequestAsync(A(0)), "image" => new { mime = "image/png", base64 = Convert.ToBase64String(icon) }, "lcuRequest" => Api(A(1), true, A(0), values.ElementAtOrDefault(2)), "riotRequest" => Api(A(1), false, A(0), values.ElementAtOrDefault(2)), "sgpRequest" => Api(A(3), false, A(2), values.ElementAtOrDefault(4)), _ => new { } };
        if (ns == "winui-backend" && method == "miniAction")
        {
            var action = values[0]; var kind = action.GetProperty("kind").GetString(); var id = action.TryGetProperty("id", out var actionId) ? actionId.GetInt32() : 0;
            if (kind is "champion" or "champion-preview") { selectedHero = id; selectedSkin = id * 1000; }
            if (kind == "skin") {
                if (fixtureSkinDelay > 0) await Task.Delay(fixtureSkinDelay);
                if (fixtureSkinFailsOnce) { fixtureSkinFailsOnce = false; throw new InvalidOperationException("模拟客户端换肤失败"); }
                selectedSkin = id;
            }
            await File.AppendAllTextAsync(fixtureLog, action.GetRawText() + "\n"); result = new { };
        }
        if (ns == "setting-factory-main")
        {
            var setting = Set(A(0)); result = method switch { "getByPrefix" => setting, "get" => setting.GetValueOrDefault(A(1)), _ => null };
            if (method == "set") {
                if (miscSaveFailsOnce && A(0) == "auto-misc-main" && A(1) == "autoReplyText") { miscSaveFailsOnce = false; throw new InvalidOperationException("模拟回复保存失败"); }
                setting[A(1)] = values[2].Clone();
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "event", @event = new { @namespace = "mobx-utils-main", name = "update-state-prop/" + A(0) + ":settings", args = new object[] { A(1), values[2].Clone(), new { action = "update", raw = true } } } }));
            }
        }
        if (ns == "auto-gameflow-main" && method == "setFriendsToBeInvited")
        {
            if (miscSaveFailsOnce) { miscSaveFailsOnce = false; throw new InvalidOperationException("模拟预约保存失败"); }
            scheduledFriends = values[0].EnumerateArray().Select(value => value.GetString()!).ToHashSet();
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "event", @event = new { @namespace = "mobx-utils-main", name = "update-state-prop/auto-gameflow-main:state", args = new object[] { "friendsToBeInvited", scheduledFriends.ToArray(), new { action = "update", raw = true } } } }));
            result = new { };
        }
        if (ns == "auto-champ-config-main" && method is "updateRunes" or "updateSummonerSpells")
        {
            if (championSaveFailsOnce) { championSaveFailsOnce = false; throw new InvalidOperationException("模拟英雄配置保存失败"); }
            string key = method == "updateRunes" ? "runesV2" : "summonerSpells";
            var config = Set(ns).TryGetValue(key, out var previous) && previous is JsonElement element ? System.Text.Json.Nodes.JsonNode.Parse(element.GetRawText())!.AsObject() : new System.Text.Json.Nodes.JsonObject();
            string hero = values[0].ToString(); config[hero] ??= new System.Text.Json.Nodes.JsonObject(); config[hero]![A(1)] = System.Text.Json.Nodes.JsonNode.Parse(values[2].GetRawText());
            var saved = JsonSerializer.SerializeToElement(config); Set(ns)[key] = saved;
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "event", @event = new { @namespace = "mobx-utils-main", name = "update-state-prop/" + ns + ":settings", args = new object[] { key, saved, new { action = "update", raw = true } } } })); result = new { };
        }
        if (notifications.Action(ns, method) is { } changedState)
        {
            await File.AppendAllTextAsync(fixtureLog, JsonSerializer.Serialize(new { ns, method, args = values }) + "\n");
            foreach (var prop in JsonSerializer.SerializeToElement(changedState).EnumerateObject())
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "event", @event = new { @namespace = "mobx-utils-main", name = "update-state-prop/" + ns + ":state", args = new object[] { prop.Name, prop.Value.Clone(), new { action = "update", raw = true } } } }));
            result = new { };
        }
        if (ns == "winui-backend" && method == "miniSnapshot")
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "event", @event = new { @namespace = "mobx-utils-main", name = "update-state-prop/respawn-timer-main:state", args = new object[] { "info", JsonSerializer.SerializeToElement(notifications.Respawn()).GetProperty("info"), new { action = "update", raw = true } } } }));
        if (automation.Action(ns, method, values) is { } automationState) { result = new { }; await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "event", @event = new { @namespace = "mobx-utils-main", name = "update-state-prop/auto-select-main:state", args = new object[] { "temporarilyDisabled", JsonSerializer.SerializeToElement(automationState).GetProperty("temporarilyDisabled"), new { action = "update", raw = true } } } })); }
        if (ns == "in-game-send-main" && method == "generatePlayerAnalysis") result = FixtureAnalysis(A(0));
        if (ns == "ongoing-game-main") result = method switch { "getAll" => All(), "loadGameDetails" => new { source = "sgp", data = new { json = Timeline() } }, _ => null };
        if (ns == "saved-player-main")
        {
            result = method switch { "getPlayerTags" => Array.Empty<object>(), "getAllPlayerTags" or "queryEncounteredGames" => new { data = Array.Empty<object>(), total = 0 }, _ => null };
            if (fixtureEncounters && method == "queryEncounteredGames")
            {
                var query = values.ElementAtOrDefault(0);
                string Filter(string key) => query.ValueKind == JsonValueKind.Object && query.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                int Number(string key, int fallback) => query.ValueKind == JsonValueKind.Object && query.TryGetProperty(key, out var value) && value.TryGetInt32(out var number) ? number : fallback;
                int page = Math.Max(1, Number("page", 1)), pageSize = Math.Clamp(Number("pageSize", 40), 1, 1000);
                var rows = encounterRows.Where(row => new[] { "puuid", "selfPuuid", "region", "rsoPlatformId", "queueType" }.All(key => Filter(key).Length == 0 || row[key]?.ToString() == Filter(key))).OrderByDescending(row => row["updateAt"]?.ToString()).ThenByDescending(row => Convert.ToInt64(row["id"]));
                var sorted = Filter("timeOrder") == "asc" ? rows.Reverse().ToArray() : rows.ToArray();
                result = new { data = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), total = sorted.Length, page, pageSize };
            }
            if (fixtureEncounters && method == "deleteEncounteredGame")
            {
                long id = values[0].GetInt64(); int count = encounterRows.RemoveAll(row => Convert.ToInt64(row["id"]) == id);
                await File.AppendAllTextAsync(fixtureLog, JsonSerializer.Serialize(new { ns, method, recordId = id, affected = count, simulated = true }) + "\n"); result = new { affected = count };
            }
        }
        if (ns == "app-common-main" && method == "getVersion") result = "fixture-native";
        if (ns == "remote-config-main" && method == "testRepoLatency") result = new { giteeLatency = 24.5, githubLatency = -1 };
        if (ns == "self-update-main" && method == "checkUpdates") result = new { result = JsonSerializer.SerializeToElement(notifications.Remote()).GetProperty("latestRelease").GetProperty("isNew").GetBoolean() ? "new-updates" : "no-updates", reason = "" };
        if (ns == "app-common-main" && method == "getRuntimeInfo")
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            result = new { version = "fixture-native", platform = "win32", arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), pid = process.Id, type = "fixture-backend", uptime = (DateTimeOffset.UtcNow - fixtureStarted).TotalSeconds, memoryUsage = new { rss = process.WorkingSet64, heapUsed = GC.GetTotalMemory(false), heapTotal = GC.GetGCMemoryInfo().HeapSizeBytes }, versions = new { go = "fixture: .NET protocol simulator" }, os = new { type = "Windows_NT", release = Environment.OSVersion.Version.ToString(), totalmem = 0, freemem = 0, cpus = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => new { model = "fixture" }).ToArray() }, argv = Environment.GetCommandLineArgs() };
        }
    }
    catch (Exception exception) { error = exception.Message; }
    await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "response", id = message.GetProperty("id").GetString(), result = new { success = error is null, data = result, error = error is null ? null : new { message = error } } }));
}

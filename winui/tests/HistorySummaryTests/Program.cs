using System.Text.Json;
using LeagueAkari.WinUI.Services;

int passed = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); passed++; }
bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-9;
var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
JsonElement Game(bool win = true, int champion = 1, double hours = 1, double damage = 100, double allyDamage = 100,
    string position = "TOP", string type = "MATCHED_GAME", int queue = 420, bool remake = false, bool lcu = false,
    bool arena = false, int placement = 1, int teams = 8, double duration = 1800)
{
    object Player(string id, int team, int subteam, double output, bool self)
    {
        var stats = new Dictionary<string, object?> { ["kills"] = self ? 8 : 2, ["deaths"] = self ? 2 : 1, ["assists"] = self ? 4 : 0,
            ["totalDamageDealtToChampions"] = output, ["totalDamageTaken"] = self ? 200 : 100, ["goldEarned"] = self ? 150 : 50,
            ["totalMinionsKilled"] = self ? 200 : 100, ["neutralMinionsKilled"] = 0, ["visionScore"] = self ? 20 : 10,
            ["damageDealtToTurrets"] = self ? 100 : 50, ["totalDamageToTowers"] = self ? 100 : 50,
            ["win"] = win, ["gameEndedInEarlySurrender"] = remake, ["playerSubteamId"] = subteam, ["subteamPlacement"] = placement };
        var result = new Dictionary<string, object?> { ["puuid"] = id, ["teamId"] = team, ["championId"] = self ? champion : 10,
            ["teamPosition"] = position, ["participantId"] = self ? 1 : team + subteam };
        if (lcu) result["stats"] = stats;
        else
        {
            foreach (var value in stats) result[value.Key] = value.Value;
            result["challenges"] = new { soloKills = self ? 3 : 0 };
            foreach (string key in new[] { "allInPings", "assistMePings", "basicPings", "commandPings", "dangerPings", "enemyMissingPings", "enemyVisionPings", "getBackPings", "holdPings", "needVisionPings", "onMyWayPings", "pushPings", "retreatPings", "visionClearedPings" }) result[key] = 1;
        }
        return result;
    }
    var players = new List<object> { Player("self", 100, 1, damage, true), Player("ally", 100, 1, allyDamage, false) };
    if (arena) for (int i = 2; i <= teams; i++) players.Add(Player("enemy" + i, 200, i, 1000, false));
    else players.Add(Player("enemy", 200, 0, 1000, false));
    return JsonSerializer.SerializeToElement(new { gameId = 1, gameType = type, queueId = queue, gameMode = arena ? "CHERRY" : "CLASSIC",
        gameCreation = now.AddHours(-hours).ToUnixTimeMilliseconds(), gameDuration = duration, participants = players,
        participantIdentities = players.Select((_, i) => new { participantId = i == 0 ? 1 : i == 1 ? 101 : 200, player = new { puuid = i == 0 ? "self" : "other" + i } }) });
}
var one = HistorySummaryData.Analyze([Game()], "self", now);
Check(one.Summary.Count == 1 && Near(one.WinRate, 1), "wins and denominator use eligible games");
Check(Near(one.Summary.Kda, 6), "aggregate KDA uses total deaths");
Check(Near(one.Summary.KillParticipation, 1.2), "participation is not clamped");
Check(Near(one.Metrics["damageToTeamMax"], 1) && Near(one.Metrics["damageToMatchMax"], .1), "team and match maxima remain distinct");
Check(Near(one.Metrics["towerDamageShare"], 2d / 3) && Near(one.Metrics["visionShare"], 2d / 3), "tower and vision use team share");
Check(Near(one.Metrics["pings"], 14) && one.Summary.SoloKills == 3, "SGP nullable metrics sum all actual ping types");
Check(Near(one.Scores.Sum(p => p.Value), one.Summary.AkariScore), "eight Akari parts equal displayed score");
Check(Near(one.Scores.Sum(p => p.Value), 8) && Near(one.Scores.Single(p => p.Key == "gold").Value, 2), "hand-calculated score fixture preserves full gold bonus and eight-point total");
Check(Near(one.Scores.Single(p => p.Key == "damage").Value, 0), "expected-contribution baseline gives no damage score");
var soloTeam = HistorySummaryData.Analyze([JsonSerializer.SerializeToElement(new { gameType = "MATCHED_GAME", queueId = 420, gameDuration = 1800, participants = new[] { Game().Field("participants").Items().First() } })], "self", now);
Check(soloTeam.Scores.Where(p => p.Key is "damage" or "damageTaken" or "gold" or "vision").All(p => p.Value == 0), "one-player team has no expected-contribution bonus");
var mixedShares = HistorySummaryData.Analyze([Game(damage: 90, allyDamage: 10), Game(damage: 10, allyDamage: 990)], "self", now);
Check(Near(mixedShares.Summary.DamageShare, .455), "shares average individual games instead of pooled totals");
Check(Near(mixedShares.Metrics["damageToTeamMax"], (1 + 10d / 990) / 2), "max ratios also average per game");
var eligible = HistorySummaryData.Analyze([Game(), Game(type: "PRACTICE_GAME"), Game(queue: 830), Game(remake: true)], "self", now);
Check(eligible.Summary.Count == 1, "practice PVE and remakes excluded from summary only");
var unknown = HistorySummaryData.Analyze([Game()], "missing", now);
Check(unknown.Summary.Count == 0 && unknown.Positions is null, "missing identity has empty analysis");
Check(one.Positions!["TOP"] == 1 && one.Positions["JUNGLE"] == 0, "position distribution retains all five lanes");
var noPosition = HistorySummaryData.Analyze([Game(position: ""), Game(position: "TOP")], "self", now);
Check(noPosition.Positions is null, "position availability follows latest sample as upstream");
var mixed = HistorySummaryData.Analyze([Game(), Game(lcu: true)], "self", now);
Check(!mixed.Metrics.ContainsKey("pings") && mixed.Summary.SoloKills is null && mixed.Summary.EnemyMissingPings is null, "mixed LCU source does not invent missing metrics");
var active = HistorySummaryData.Analyze([Game(hours: 1), Game(false, hours: 3), Game(hours: 15)], "self", now);
Check(active.Summary.ActiveSessionWins == 1 && active.Summary.ActiveSessionLosses == 1 && Near(active.ActiveSessionWinRate, .5), "active session stops across long inter-game gap");
Check(active.ShowActiveSession(0) && !active.ShowActiveSession(1), "active session only appears on first page");
var expired = HistorySummaryData.Analyze([Game(hours: 4.5)], "self", now);
Check(!expired.ShowActiveSession(0), "exact four-hour latest ended boundary is inactive");
var atGap = HistorySummaryData.Analyze([Game(hours: 1), Game(false, hours: 8.5)], "self", now);
Check(atGap.Summary.ActiveSessionLosses == 1, "exact eight-hour gap remains in session");
var overGap = HistorySummaryData.Analyze([Game(hours: 1), Game(false, hours: 8.5001)], "self", now);
Check(overGap.Summary.ActiveSessionLosses == 0, "gap greater than eight hours starts another session");
Check(one.VisibleStreak(0) == 0, "single result has no streak badge");
var losing = HistorySummaryData.Analyze([Game(false), Game(false), Game()], "self", now);
Check(losing.VisibleStreak(0) == -2 && losing.VisibleStreak(1) == 0, "consecutive newest losses and page guard");
var winning = HistorySummaryData.Analyze([Game(), Game(), Game(false)], "self", now);
Check(winning.VisibleStreak(0) == 2, "streak stops at first opposite result");
var champions = HistorySummaryData.Champions([Game(false, 1), Game(false, 1), Game(true, 2), Game(false, 2), Game(true, 3)], "self", now);
Check(champions.Select(c => c.ChampionId).SequenceEqual([2, 1, 3]), "champion sort is count descending then wins descending");
Check(champions[0].Analysis.Summary.Count == 2 && champions[0].Analysis.Summary.Wins == 1, "champion metrics use only its own games");
Check(HistorySummaryData.Champions([Game(champion: 0), Game()], "self", now).Count == 1, "unknown champion omitted from usage as upstream");
var arena = HistorySummaryData.Analyze([Game(arena: true, placement: 4), Game(arena: true, placement: 5), Game(arena: true, placement: 1)], "self", now);
Check(arena.Arena.Count == 3 && arena.Arena.TopHalfFinishes == 2 && arena.Arena.Top1s == 1, "eight-team arena uses top half not fixed top two");
Check(Near(arena.Arena.AveragePlacement, 10d / 3), "arena average uses actual placements");
Check(arena.Summary.BlueCount == 0 && arena.Summary.RedCount == 0, "arena does not manufacture blue/red side distribution");
var placements = HistorySummaryData.Analyze([Game(arena: true, placement: 0), Game(arena: true, placement: 2, teams: 4), Game()], "self", now);
Check(placements.Arena.Count == 2 && placements.Arena.TopHalfFinishes == 1 && Near(placements.Arena.AveragePlacement, 2) && placements.NormalCount == 1, "zero placement omitted from mean and mixed modes remain separate");
Console.WriteLine($"History summary: {passed} passed");

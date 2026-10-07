using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record SummaryScore(string Key, double Value, double Maximum);
public sealed record ArenaSummary(int Count, int Top1s, int TopHalfFinishes, double AveragePlacement)
{
    public double Top1Rate => Top1s / MatchData.NoZero(Count);
    public double TopHalfRate => TopHalfFinishes / MatchData.NoZero(Count);
}
public sealed record ChampionSummary(int ChampionId, JsonElement[] Games, SummaryAnalysis Analysis);
public sealed record SummaryAnalysis(HistorySummary Summary, IReadOnlyList<SummaryScore> Scores,
    IReadOnlyDictionary<string, double> Metrics, IReadOnlyDictionary<string, int>? Positions,
    ArenaSummary Arena, int NormalCount)
{
    public double WinRate => Summary.Wins / MatchData.NoZero(Summary.Count);
    public double ActiveSessionWinRate => Summary.ActiveSessionWins / MatchData.NoZero(Summary.ActiveSessionWins + Summary.ActiveSessionLosses);
    public bool ShowActiveSession(int page) => page == 0 && Summary.ActiveSessionWins + Summary.ActiveSessionLosses > 0;
    public int VisibleStreak(int page) => page != 0 ? 0 : Summary.WinningStreak >= 2 ? Summary.WinningStreak : Summary.LosingStreak >= 2 ? -Summary.LosingStreak : 0;
}

/// <summary>SummaryPane and ChampionAnalysisContent projections; ratios are averaged per match.</summary>
public static class HistorySummaryData
{
    private sealed record Sample(JsonElement Game, MatchParticipant Self, MatchParticipant[] All)
    {
        public MatchParticipant[] Team => All.Where(p => p.TeamKey == Self.TeamKey).ToArray();
    }
    private static readonly string[] PositionNames = ["TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY"];
    private static readonly string[] PingNames = ["allInPings", "assistMePings", "basicPings", "commandPings", "dangerPings", "enemyMissingPings", "enemyVisionPings", "getBackPings", "holdPings", "needVisionPings", "onMyWayPings", "pushPings", "retreatPings", "visionClearedPings"];

    private static Sample[] Prepare(IEnumerable<JsonElement> games, string puuid) => games.Select(MatchData.Game)
        .Where(g => g.Text("gameType") == "MATCHED_GAME" && !MatchData.IsPveQueue((int)g.Number("queueId")))
        .Select(g => new { Game = g, All = MatchData.Participants(g), Self = MatchData.Self(g, puuid) })
        .Where(g => g.Self is not null && g.Self.WinResult is "win" or "loss")
        .Select(g => new Sample(g.Game, g.Self!, g.All)).ToArray();

    public static IReadOnlyList<ChampionSummary> Champions(IEnumerable<JsonElement> games, string puuid, DateTimeOffset? now = null) =>
        Prepare(games, puuid).Where(g => g.Self.ChampionId != 0).GroupBy(g => g.Self.ChampionId)
            .Select(group => { var values = group.Select(g => g.Game).ToArray(); return new ChampionSummary(group.Key, values, Analyze(values, puuid, now)); })
            .OrderByDescending(c => c.Analysis.Summary.Count).ThenByDescending(c => c.Analysis.Summary.Wins).ToArray();

    public static SummaryAnalysis Analyze(IEnumerable<JsonElement> games, string puuid, DateTimeOffset? now = null)
    {
        var samples = Prepare(games, puuid);
        var summary = MatchData.Summarize(samples.Select(g => g.Game), puuid);
        double Average(Func<Sample, double> selector) => samples.Length == 0 ? 0 : samples.Average(selector);
        double Ratio(Sample sample, string key) => MatchData.Share(sample.Self, sample.Team, key);
        double Contribution(Sample sample, string key) => sample.Team.Length <= 1 ? 0 : Ratio(sample, key) * sample.Team.Length;
        double Score(double value, double minimum, double maximum, double weight) => Math.Clamp((value - minimum) / (maximum - minimum), 0, 1) * weight;
        var scores = new SummaryScore[]
        {
            new("kda", Math.Clamp(Math.Sqrt(summary.Kda) * 3 / 7, 0, 1), 1),
            new("winRate", Score(summary.Wins / MatchData.NoZero(summary.Count), .5, 1, 1), 1),
            new("damage", Average(g => Score(Contribution(g, "totalDamageDealtToChampions"), 1, 2, 3)), 3),
            new("damageTaken", Average(g => Score(Contribution(g, "totalDamageTaken"), 1, 2, 2)), 2),
            new("cs", Average(g => Score(g.Self.Num("cs") / MatchData.NoZero(g.Game.Number("gameDuration") / 60), 5, 10, 2)), 2),
            new("gold", Average(g => Score(Contribution(g, "goldEarned"), 1, 1.5, 2)), 2),
            new("participation", Average(g => Score((g.Self.Kills + g.Self.Assists) / MatchData.NoZero(g.Team.Sum(p => p.Kills)), .3, 1, 2)), 2),
            new("vision", Average(g => Score(Contribution(g, "visionScore"), 1, 2, 2)), 2)
        };
        var metrics = new Dictionary<string, double>
        {
            ["visionShare"] = Average(g => Ratio(g, "visionScore")),
            ["damagePerMinute"] = Average(g => g.Self.Num("totalDamageDealtToChampions") / MatchData.NoZero(g.Game.Number("gameDuration") / 60))
        };
        foreach (var (key, stat) in new[] { ("damage", "totalDamageDealtToChampions"), ("damageTaken", "totalDamageTaken"), ("gold", "goldEarned"), ("cs", "cs"), ("towerDamage", "totalDamageToTowers") })
        {
            metrics[key + "Share"] = Average(g => Ratio(g, stat));
            metrics[key + "ToTeamMax"] = Average(g => g.Self.Num(stat) / MatchData.NoZero(g.Team.Select(p => p.Num(stat)).DefaultIfEmpty().Max()));
            metrics[key + "ToMatchMax"] = Average(g => g.Self.Num(stat) / MatchData.NoZero(g.All.Select(p => p.Num(stat)).DefaultIfEmpty().Max()));
        }
        // LCU has no ping records. Mixed or incomplete samples must not appear as zero pings.
        if (samples.Length > 0 && samples.All(g => !g.Self.IsLcu && PingNames.All(p => g.Self.Raw.Field(p).ValueKind == JsonValueKind.Number)))
            metrics["pings"] = Average(g => PingNames.Sum(p => g.Self.Raw.Number(p)));
        Dictionary<string, int>? positions = null;
        if (samples.FirstOrDefault()?.Self.Position.Length > 0)
            positions = PositionNames.ToDictionary(p => p, p => samples.Count(g => g.Self.Position == p));

        var arena = samples.Where(g => g.Game.Text("gameMode") == "CHERRY").ToArray();
        double Placement(Sample g) => g.Self.Num("subteamPlacement");
        int TeamCount(Sample g) => g.All.Where(p => p.Num("subteamPlacement") > 0).Select(p => p.TeamKey).Distinct().Count();
        var positive = arena.Where(g => Placement(g) > 0).ToArray();
        var arenaSummary = new ArenaSummary(arena.Length, arena.Count(g => Placement(g) == 1),
            arena.Count(g => Placement(g) > 0 && Placement(g) <= Math.Floor(TeamCount(g) / 2d)),
            positive.Length == 0 ? 0 : positive.Average(Placement));

        int activeWins = 0, activeLosses = 0;
        if (samples.Length > 0)
        {
            var ended = MatchData.Creation(samples[0].Game).AddSeconds(samples[0].Game.Number("gameDuration"));
            if ((now ?? DateTimeOffset.UtcNow) - ended < TimeSpan.FromHours(4))
                for (int i = 0; i < samples.Length; i++)
                {
                    var sample = samples[i];
                    if (i > 0 && ended - MatchData.Creation(sample.Game) > TimeSpan.FromHours(8)) break;
                    if (sample.Self.WinResult == "win") activeWins++; else activeLosses++;
                    ended = MatchData.Creation(sample.Game).AddSeconds(sample.Game.Number("gameDuration"));
                }
        }
        summary = summary with { ActiveSessionWins = activeWins, ActiveSessionLosses = activeLosses };
        return new(summary, scores, metrics, positions, arenaSummary, samples.Length - arena.Length);
    }
}

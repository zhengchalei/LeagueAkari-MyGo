using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record OngoingPositionInfo(string Current, string Reason, string Primary, string Secondary, string[] Recent);
public sealed record OngoingWinStats(int Count, double Rate, double? Top1Rate);
public sealed record OngoingEncounters(EncounterGame[] Games, DateTimeOffset? LastMetAt, string LabelKey);

public static class OngoingCardData
{
    public static double TeamKda(IEnumerable<HistorySummary> players)
    {
        var summaries = players.ToArray();
        return (summaries.Sum(s => s.Kills) + summaries.Sum(s => s.Assists)) / MatchData.NoZero(summaries.Sum(s => s.Deaths));
    }
    public static OngoingPositionInfo? Position(JsonElement assignment, JsonElement analysis)
    {
        string current = assignment.Text("position");
        if (current.Length == 0 || current == "NONE") return null;
        var role = assignment.Field("role"); var positions = analysis.Field("positions");
        string[] recent = positions.ValueKind != JsonValueKind.Object ? [] : positions.EnumerateObject().Where(p => p.Name != "NONE" && p.Value.TryNumber() > 0).OrderByDescending(p => p.Value.TryNumber()).Select(p => p.Name).ToArray();
        return new(current, role.Text("assignmentReason"), role.Text("primary"), role.Text("secondary"), recent);
    }

    public static OngoingWinStats Wins(string queueType, JsonElement analysis, IEnumerable<JsonElement> history, string puuid)
    {
        var data = analysis.Field("winLoss").Field(queueType == "CHERRY" ? "cherry" : "all");
        if (data.ValueKind == JsonValueKind.Object) return new((int)data.Number("count"), data.Number("winRate"), queueType == "CHERRY" ? data.Number("top1Rate") : null);
        var games = history.Select(MatchData.Game).Where(g => queueType != "CHERRY" || g.Text("gameMode") == "CHERRY").ToArray();
        var summary = MatchData.Summarize(games, puuid);
        return new(summary.Count, summary.Wins / (double)Math.Max(1, summary.Count), queueType == "CHERRY" ? games.Count(g => MatchData.Self(g, puuid)?.Num("subteamPlacement") == 1) / (double)Math.Max(1, summary.Count) : null);
    }

    public static Dictionary<string, string> KdaOutliers(IReadOnlyDictionary<string, double> values)
    {
        if (values.Count < 5) return [];
        double[] sorted = values.Values.Order().ToArray();
        double Percentile(double p) { double index = p * (sorted.Length - 1); int lower = (int)index; return sorted[lower] + (index - lower) * (sorted[Math.Min(lower + 1, sorted.Length - 1)] - sorted[lower]); }
        double q1 = Percentile(.25), q3 = Percentile(.75), width = (q3 - q1) * .65;
        return values.Where(p => p.Value < q1 - width || p.Value > q3 + width).ToDictionary(p => p.Key, p => p.Value < q1 - width ? "below" : "over");
    }

    public static OngoingEncounters Encounters(JsonElement saved, JsonElement histories, JsonElement cached, string self, string target, Func<int, string>? queueName = null)
    {
        var games = new Dictionary<long, EncounterGame>();
        var recent = histories.ValueKind == JsonValueKind.Object ? histories.EnumerateObject().SelectMany(p => p.Value.Field("data").Items()).Select(MatchData.Game).ToArray() : [];
        JsonElement Cached(long id, JsonElement fallback = default) => cached.Field(id.ToString()).ValueKind == JsonValueKind.Object ? cached.Field(id.ToString()) : fallback;
        foreach (var raw in saved.Field("encounteredGames").Field("data").Items())
        {
            var record = EncounterRecord.Parse(raw);
            // Saved encounters survive missing participant identities; only their statistics are unavailable.
            games[record.GameId] = EncounterData.Project(record, Cached(record.GameId), queueName) ?? new(record, default, record.QueueType, record.RecordedAt, null, null, null);
        }
        if (self.Length > 0)
            foreach (var raw in recent)
            {
                long id = (long)raw.Number("gameId"); if (games.ContainsKey(id)) continue;
                var game = EncounterData.Project(new(0, id, self, target, "", MatchData.Creation(raw)), Cached(id, raw), queueName);
                if (game?.Self != null && game.Target != null) games[id] = game;
            }
        var merged = games.Values.OrderByDescending(g => g.PlayedAt ?? g.Record.RecordedAt).Take(40).ToArray();
        DateTimeOffset? last = merged.FirstOrDefault()?.PlayedAt ?? merged.FirstOrDefault()?.Record.RecordedAt ?? (DateTimeOffset.TryParse(saved.Text("lastMetAt"), out var at) ? at : null);
        long Latest(string puuid) => (long)histories.Field(puuid).Field("data").Items().Select(MatchData.Game).OrderByDescending(MatchData.Creation).FirstOrDefault().Number("gameId");
        var latest = merged.FirstOrDefault();
        string label = latest?.IsOpponent is bool enemy && latest.Record.GameId == Latest(self) && latest.Record.GameId == Latest(target) ? enemy ? "metLastGameOpponent" : "metLastGameTeammate" : "met";
        return new(merged, last, label);
    }
}

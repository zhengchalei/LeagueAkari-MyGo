namespace LeagueAkari.WinUI.Services;

public sealed record MatchRadarMetric(string Key, string LabelKey, double? Value, double? PlayerRatio, double? TeamRatio);

public static class MatchRadarData
{
    public static MatchRadarMetric[] Metrics(MatchParticipant player, IReadOnlyList<MatchParticipant> players)
    {
        var team = players.Where(p => p.TeamKey == player.TeamKey).ToArray();
        double? Value(MatchParticipant p, string key) => key == "killParticipation"
            ? MatchDetailsData.KillParticipation(p, players.Where(t => t.TeamKey == p.TeamKey)) : MatchDetailsData.Stat(p, key);
        return new[] { ("totalDamageDealtToChampions", "damage"), ("totalDamageTaken", "taken"), ("goldEarned", "gold"), ("cs", "cs"), ("kda", "kda"), ("killParticipation", "kp"), ("totalHeal", "heal") }.Select(metric =>
        {
            var value = Value(player, metric.Item1);
            var available = players.Select(p => Value(p, metric.Item1)).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
            double? maximum = available.Length > 0 ? available.Max() : null;
            var total = MatchDetailsData.Sum(team.Select(p => Value(p, metric.Item1)));
            double? Ratio(double? number) => number.HasValue && maximum.HasValue ? number.Value / MatchData.NoZero(maximum.Value) : null;
            return new MatchRadarMetric(metric.Item1, "matchCard.radar." + metric.Item2, value, Ratio(value), Ratio(team.Length > 0 ? total / team.Length : null));
        }).ToArray();
    }
}

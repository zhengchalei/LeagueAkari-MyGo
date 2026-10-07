using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record MatchDamage(double? Total, double? Physical, double? Magic, double? True, double Baseline)
{
    public double? BaselineRatio => Total is { } total ? total / MatchData.NoZero(Baseline) : null;
    public double? Share(double? value) => value.HasValue && Total.HasValue ? value.Value / MatchData.NoZero(Total.Value) : null;
}

public static class MatchTeamData
{
    public static MatchDamage Damage(MatchParticipant player, IEnumerable<MatchParticipant> players, bool taken)
    {
        var suffix = taken ? "Taken" : "DealtToChampions";
        var total = "totalDamage" + suffix;
        return new(MatchDetailsData.Stat(player, total), MatchDetailsData.Stat(player, "physicalDamage" + suffix),
            MatchDetailsData.Stat(player, "magicDamage" + suffix), MatchDetailsData.Stat(player, "trueDamage" + suffix),
            players.Select(p => MatchDetailsData.Stat(p, total)).Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Max());
    }
    public static double? PerMinute(double? value, double duration) => value.HasValue && duration > 0 ? value.Value / (duration / 60) : null;
    public static double? Objective(JsonElement team, string name, string legacy)
        => MatchDetailsData.Number(team.Field("objectives").Field(name).Field("kills")) ?? MatchDetailsData.Number(team.Field(legacy));
    public static int AugmentSlots(IEnumerable<MatchParticipant> team) => team.Any(p => p.Augments.ElementAtOrDefault(5) > 0) ? 6 : 5;
    public static bool CanOpenPlayer(MatchParticipant player, bool privacy)
        => !privacy && !string.IsNullOrWhiteSpace(player.Puuid) && player.Puuid != "00000000-0000-0000-0000-000000000000";
}

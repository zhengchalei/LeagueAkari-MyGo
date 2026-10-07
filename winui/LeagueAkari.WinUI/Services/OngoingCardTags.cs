using System.Globalization;
using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record OngoingTagText(string Key, IReadOnlyDictionary<string, object?> Arguments);
public sealed record OngoingTag(string Id, OngoingTagText Label, OngoingTagText[] Details, string Color, string DarkColor = "", bool BlackText = false, bool DarkBlackText = false);
public sealed record OngoingScorePart(string Key, string LabelKey, double? Value, double Maximum, double Progress);

/// <summary>Original player-card tag predicates and precision, independent of native controls.</summary>
public static class OngoingCardTags
{
    public static readonly string[] Order = ["self", "tagged", "premade-team", "high-win-rate", "met", "privacy", "winning-streak", "losing-streak", "great-performance", "suspicious-flash-position", "easy-gank", "solo-kills", "average-team-damage", "average-team-damage-taken", "average-team-gold", "average-cs-per-minute", "average-damage-gold-efficiency", "average-enemy-missing-pings", "average-vision-score", "average-kill-damage-efficiency", "akari-score"];
    private static readonly (string Light, string Dark, bool DarkBlack)[] PremadeColors = [("#0f6f68", "#48e5db", true), ("#1f3fa6", "#628aff", true), ("#5c6000", "#d4de17", true), ("#1a7a2a", "#2eda3e", true), ("#8a4400", "#ff9f1c", true), ("#8a2a00", "#da4e2e", false), ("#6a0d6a", "#bc2ebc", false), ("#a2133f", "#fa4e80", true), ("#0b3d91", "#0b3d91", false), ("#7f0000", "#7f0000", false), ("#5a2a0b", "#8b4513", false), ("#333333", "#555555", false)];
    private static readonly HashSet<string> DisabledByDefault = ["showAverageTeamDamageTag", "showAverageTeamDamageTakenTag", "showAverageTeamGoldTag", "showAverageCsPerMinuteTag", "showAverageDamageGoldEfficiencyTag", "showAverageEnemyMissingPingsTag", "showAverageVisionScoreTag", "showAkariScoreTag"];
    public static OngoingTagText Text(string key, params (string Key, object? Value)[] arguments) => new(key, arguments.ToDictionary(a => a.Key, a => a.Value));
    public static bool Enabled(JsonElement settings, string key) => settings.Field(key).ValueKind is JsonValueKind.True or JsonValueKind.False ? settings.Boolean(key) : !DisabledByDefault.Contains(key);
    public static bool IsJungler(string position, JsonElement spells) => position.Equals("JUNGLE", StringComparison.OrdinalIgnoreCase) || spells.Number("spell1Id") == 11 || spells.Number("spell2Id") == 11;
    private static double? Number(JsonElement value, string name) => value.Field(name).ValueKind == JsonValueKind.Number && value.Field(name).TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    private static string Fixed(double value, int precision) => value.ToString("F" + precision, CultureInfo.InvariantCulture);

    public static OngoingTag[] Build(JsonElement settings, JsonElement analysis, bool self, string premade, bool privacy, bool jungler)
    {
        var tags = new List<OngoingTag>();
        OngoingTag Add(string id, string label, string color, string? detail = null, params (string Key, object? Value)[] arguments)
        {
            var tag = new OngoingTag(id, Text(label, arguments), detail == null ? [] : [Text(detail, arguments)], color); tags.Add(tag); return tag;
        }
        if (self && Enabled(settings, "showSelfTag")) Add("self", "self", "#37246c");
        if (premade.Length > 0 && Enabled(settings, "showPremadeTeamTag"))
        {
            var tag = Add("premade-team", "premade", "#40ffffff", "premadePopover", ("team", premade));
            int index = premade[0] - 'A'; if (index >= 0 && index < PremadeColors.Length) { var palette = PremadeColors[index]; tags[^1] = tag with { Color = palette.Light, DarkColor = palette.Dark, DarkBlackText = palette.DarkBlack }; }
        }
        if (privacy && Enabled(settings, "showPrivacyTag")) Add("privacy", "private", "#870808", "privatePopover");
        var summary = analysis.Field("summary"); var score = analysis.Field("akariScore"); var wins = analysis.Field("winLoss").Field("all");
        if (analysis.ValueKind != JsonValueKind.Object) return tags.OrderBy(t => Array.IndexOf(Order, t.Id)).ToArray();
        int count = (int)analysis.Number("count");
        if (Enabled(settings, "showWinRateTeamTag") && wins.Number("count") >= 16 && wins.Number("winRate") >= .85) Add("high-win-rate", "highWinRate", "#7e2c85", "highWinRatePopover", ("count", wins.Number("count")), ("winCount", wins.Number("wins")));
        foreach (var (setting, id, color) in new[] { ("showWinningStreakTag", "winning", "#18571c"), ("showLosingStreakTag", "losing", "#893b3b") })
            if (Enabled(settings, setting) && wins.Number(id + "Streak") >= 3) Add(id + "-streak", id + "Streak", color, id + "StreakPopover", ("count", wins.Number(id + "Streak")));
        if (Enabled(settings, "showGreatPerformanceTag") && (score.Boolean("outstanding") || score.Boolean("extraordinary")))
        {
            string key = "akariLoved." + (score.Boolean("extraordinary") ? "extraordinary" : "outstanding"); Add("great-performance", key, "#b81b86", key + "Popover");
        }
        var spells = analysis.Field("spells"); double flashD = spells.Number("flashOnD"), flashF = spells.Number("flashOnF");
        if (Enabled(settings, "showSuspiciousFlashPositionTag") && flashD > 0 && flashF > 0) Add("suspicious-flash-position", "suspiciousFlashPosition", "#3a1bb8");
        if (Enabled(settings, "showEasyGankTag") && !jungler && Number(analysis.Field("details"), "avgEarlyDeathsWithEnemyJunglerInvolved") is double deaths)
        {
            string key = deaths > 2 ? "veryEasyGank" : deaths >= 1.5 ? "easyGank" : deaths >= 1 ? "gankable" : "hardGank";
            string color = deaths > 2 ? "#a81919" : deaths >= 1.5 ? "#8f541e" : deaths >= 1 ? "#64732a" : "#24606d";
            Add("easy-gank", key, color, "easyGankPopover", ("times", Fixed(deaths, 2)), ("count", analysis.Number("detailsCount")));
        }
        if (Enabled(settings, "showSoloKillsTag") && Number(summary, "avgSoloKills") is double solo && solo != 0)
        {
            var tag = Add("solo-kills", "soloKills", "#9019a8", null, ("times", Fixed(solo, 1)));
            tags[^1] = tag with { Details = [Text("soloKillsPopover", ("times", Fixed(solo, 2)), ("count", count))] };
        }
        foreach (var (setting, id, key, field, color) in new[] { ("showAverageTeamDamageTag", "average-team-damage", "teamDamageShare", "avgChampionDamagePercentageOfTeam", "#692723"), ("showAverageTeamDamageTakenTag", "average-team-damage-taken", "teamDamageTakenShare", "avgDamageTakenPercentageOfTeam", "#135225"), ("showAverageTeamGoldTag", "average-team-gold", "teamGoldShare", "avgGoldPercentageOfTeam", "#a73d2a"), ("showAverageDamageGoldEfficiencyTag", "average-damage-gold-efficiency", "damageGoldEfficiency", "avgDamageGoldEfficiency", "#8f411e") })
            if (Enabled(settings, setting) && Number(summary, field) is double share)
            {
                var tag = Add(id, key, color, null, ("rate", Fixed(share * 100, 0)));
                var details = new List<OngoingTagText> { Text(key + "Popover", ("rate", Fixed(share * 100, 2)), ("count", count)) };
                if (id == "average-damage-gold-efficiency") { details.Add(Text("damageGoldEfficiencyPopoverDefinition")); details.Add(Text("damageGoldEfficiencyPopoverUsage")); }
                tags[^1] = tag with { Details = details.ToArray() };
            }
        if (Enabled(settings, "showAverageCsPerMinuteTag") && Number(summary, "avgCsPerMinute") is double cs)
        {
            var tag = Add("average-cs-per-minute", "csPerMinute", "#5a4a1f", null, ("value", Fixed(cs, 1)));
            var details = new List<OngoingTagText> { Text("csPerMinutePopover", ("value", Fixed(cs, 2)), ("count", count)) };
            if (Number(summary, "avgCsPercentageOfTeam") is double share) details.Add(Text("csTeamSharePopover", ("rate", Fixed(share * 100, 2))));
            tags[^1] = tag with { Details = details.ToArray() };
        }
        foreach (var (setting, id, key, field, color, black) in new[] { ("showAverageEnemyMissingPingsTag", "average-enemy-missing-pings", "enemyMissingPings", "avgEnemyMissingPings", "#e7da30", true), ("showAverageVisionScoreTag", "average-vision-score", "visionScore", "avgVisionScore", "#2451a6", false) })
            if (Enabled(settings, setting) && Number(summary, field) is double value)
            {
                var tag = Add(id, key, color, null, ("count", Fixed(value, 1).TrimEnd('0').TrimEnd('.')));
                tags[^1] = tag with { BlackText = black, DarkBlackText = black, Details = [Text(key + "Popover", ("count", Fixed(value, 3)))] };
            }
        if (Enabled(settings, "showAverageKillDamageEfficiencyTag") && Number(summary, "avgKillDamageEfficiency") is double efficiency && (efficiency > 1.35 || efficiency < .65))
        {
            string key = "killDamageEfficiency" + (efficiency > 1.35 ? "High" : "Low"); Add("average-kill-damage-efficiency", key, "#04614b", key + "Popover", ("rate", Fixed(efficiency * 100, 2)), ("count", count));
        }
        if (Enabled(settings, "showAkariScoreTag") && Number(score, "total") is double total) Add("akari-score", "$Akari " + Fixed(total, 2), "#b81b86", "akariScorePopoverDescription");
        return tags.OrderBy(t => Array.IndexOf(Order, t.Id)).ToArray();
    }

    public static OngoingScorePart[] ScoreParts(JsonElement score) => new[] { ("kdaScore", "kda", 1d), ("winRateScore", "winRate", 1d), ("dmgScore", "damage", 3d), ("dmgTakenScore", "damageTaken", 2d), ("csScore", "cs", 2d), ("goldScore", "gold", 2d), ("participationScore", "participation", 2d), ("visionScore", "vision", 2d) }.Select(p => { var value = Number(score, p.Item1); return new OngoingScorePart(p.Item1, "akariScore.parts." + p.Item2, value, p.Item3, Math.Clamp((value ?? 0) / p.Item3 * 100, 0, 100)); }).ToArray();
}

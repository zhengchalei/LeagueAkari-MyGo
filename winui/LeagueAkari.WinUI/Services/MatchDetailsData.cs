using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

/// <summary>Preserves the difference between a reported zero and a value absent from LCU/SGP.</summary>
public static class MatchDetailsData
{
    public static double? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    public static double? Stat(MatchParticipant player, string key)
    {
        double? Read(string name) => Number(player.Stats.Field(name)) ?? Number(player.Raw.Field(name)) ?? Number(player.Raw.Field("challenges").Field(name));
        return key switch
        {
            "cs" => Sum([Read("totalMinionsKilled"), Read("neutralMinionsKilled")]),
            "kda" => Read("kills") is { } kills && Read("deaths") is { } deaths && Read("assists") is { } assists ? (kills + assists) / MatchData.NoZero(deaths) : null,
            "level" => Read("champLevel"),
            "totalDamageToTowers" => Read("damageDealtToTurrets") ?? Read("totalDamageToTowers"),
            "magicDamageTaken" => Read("magicDamageTaken") ?? Read("magicalDamageTaken"),
            "damageGoldEfficiency" => Read("totalDamageDealtToChampions") is { } damage && Read("goldEarned") is { } gold ? damage / MatchData.NoZero(gold) : null,
            _ => Read(key)
        };
    }
    public static double? Sum(IEnumerable<double?> values)
    {
        var data = values.ToArray();
        return data.Length > 0 && data.All(v => v.HasValue) ? data.Sum(v => v!.Value) : null;
    }
    public static string Format(double? value, string format = "N0") => value?.ToString(format) ?? "—";
    public static JsonElement Timeline(JsonElement raw)
    {
        var timeline = MatchData.Game(raw);
        if (timeline.ValueKind != JsonValueKind.Object || timeline.Field("frames").ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("对局时间线响应格式无效");
        return timeline;
    }
    public static double? TimelineMetric(JsonElement frame, int id, int metric)
    {
        var participant = frame.Field("participantFrames").Field(id.ToString());
        return metric switch
        {
            0 => Number(participant.Field("totalGold")),
            1 => Sum([Number(participant.Field("minionsKilled")), Number(participant.Field("jungleMinionsKilled"))]),
            2 => Number(participant.Field("xp")),
            3 => Number(participant.Field("damageStats").Field("totalDamageDoneToChampions")),
            _ => Number(participant.Field("damageStats").Field("totalDamageTaken"))
        };
    }
    public static double? KillParticipation(MatchParticipant player, IEnumerable<MatchParticipant> teammates)
    {
        var kills = Sum(teammates.Select(p => Stat(p, "kills")));
        var involved = Sum([Stat(player, "kills"), Stat(player, "assists")]);
        return kills.HasValue && involved.HasValue ? involved.Value / MatchData.NoZero(kills.Value) : null;
    }
    public static bool HasDraftIdentities(IEnumerable<MatchParticipant> players)
    {
        var data = players.ToArray();
        return data.Length > 0 && data.All(p => !string.IsNullOrWhiteSpace(p.Puuid)) && data.Select(p => p.Puuid).Distinct(StringComparer.Ordinal).Count() == data.Length;
    }
    public static object CreateDraft(JsonElement raw, string focusedPuuid)
    {
        var game = MatchData.Game(raw); var players = MatchData.Participants(game);
        if (!HasDraftIdentities(players)) throw new InvalidOperationException("Match participants do not have unique identities.");
        bool arena = game.Text("gameMode") == "CHERRY";
        var positions = players.Where(p => p.Position.Length > 0).ToDictionary(p => p.Puuid, p => new { selected = p.Position, primary = p.Position, secondary = "" });
        return new { gameModeKind = arena ? "cherry" : "normal", queueId = game.Number("queueId"), puuid = players.Any(p => p.Puuid == focusedPuuid) ? focusedPuuid : null, teams = players.GroupBy(p => arena ? "TEAM-ALL" : p.TeamKey).ToDictionary(g => g.Key, g => g.Select(p => p.Puuid).ToArray()), championSelections = players.ToDictionary(p => p.Puuid, p => p.ChampionId), positions = positions.Count > 0 ? positions : null };
    }
}

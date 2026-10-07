using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record CDTimerRow(string ID, string FirstKey, string SecondKey, int Hero, int FirstSpell, int SecondSpell, string Type);

public static class CDTimerRoster
{
    public static CDTimerRow[] Defaults() => Enumerable.Range(0, 5).Select(i => new CDTimerRow("default-" + i, "default-" + i + "-spell1", "default-" + i + "-spell2", 0, 0, 0, "countup")).ToArray();
    public static CDTimerRow[] Read(JsonElement flow, JsonElement summoner, JsonElement ongoing, JsonElement platform, string type)
    {
        var game = flow.Field("session").Field("gameData");
        string me = summoner.Field("me").Text("puuid");
        if (flow.Text("phase") != "InProgress" || !Valid(me) || !CDTimerData.AbilityHaste(platform, game.Field("queue").Text("gameMode")).HasValue) return Defaults();
        var additional = ongoing.Field("additional");
        var one = game.Field("teamOne").Items().ToArray(); var two = game.Field("teamTwo").Items().ToArray();
        var selections = new Dictionary<string, (int Hero, int First, int Second)>(); var positions = new Dictionary<string, string>();
        foreach (var player in one.Concat(two)) if (Valid(player.Text("puuid"))) positions[player.Text("puuid")] = player.Text("selectedPosition");
        foreach (var player in game.Field("playerChampionSelections").Items())
            if (Valid(player.Text("puuid"))) selections[player.Text("puuid")] = ((int)player.Number("championId"), (int)player.Number("spell1Id"), (int)player.Number("spell2Id"));
        foreach (var property in Properties(additional.Field("positions"))) if (property.Value.Text("position").Length > 0) positions[property.Name] = property.Value.Text("position");
        foreach (var selection in Properties(additional.Field("selections")))
        {
            var spells = additional.Field("spells").Field(selection.Name);
            if (Valid(selection.Name) && spells.ValueKind == JsonValueKind.Object && selection.Value.ValueKind == JsonValueKind.Number)
                selections[selection.Name] = ((int)selection.Value.TryNumber(), (int)spells.Number("spell1Id"), (int)spells.Number("spell2Id"));
        }
        string[] Team(JsonElement players, JsonElement extra) => players.Items().Select(player => player.Text("puuid")).Concat(extra.Items().Where(id => id.ValueKind == JsonValueKind.String).Select(id => id.GetString()!)).Where(Valid).Distinct().ToArray();
        var first = Team(game.Field("teamOne"), additional.Field("teams").Field("TEAM-100"));
        var second = Team(game.Field("teamTwo"), additional.Field("teams").Field("TEAM-200"));
        var resolvedFirst = ongoing.Field("teams").Field("TEAM-100").Items().Where(id => id.ValueKind == JsonValueKind.String).Select(id => id.GetString()!).Where(Valid).Distinct().ToArray();
        var resolvedSecond = ongoing.Field("teams").Field("TEAM-200").Items().Where(id => id.ValueKind == JsonValueKind.String).Select(id => id.GetString()!).Where(Valid).Distinct().ToArray();
        // Authoritative backend teams supersede stale champion-select memberships.
        if (resolvedFirst.Length > 0 && resolvedSecond.Length > 0) { first = resolvedFirst; second = resolvedSecond; }
        var enemies = first.Contains(me) ? second : first;
        var rows = enemies.Where(selections.ContainsKey).OrderBy(id => Position(positions.GetValueOrDefault(id))).Select(id =>
        {
            var player = selections[id]; string key = $"champion-{id}-{player.Hero}";
            return new CDTimerRow(key + $"-{player.First}-{player.Second}", key + "-" + player.First, key + "-" + player.Second, player.Hero, player.First, player.Second, type);
        }).ToArray();
        return rows.Length > 0 ? rows : Defaults();
    }
    private static IEnumerable<JsonProperty> Properties(JsonElement value) => value.ValueKind == JsonValueKind.Object ? value.EnumerateObject() : Array.Empty<JsonProperty>();
    private static bool Valid(string puuid) => puuid.Length > 0 && puuid != "00000000-0000-0000-0000-000000000000";
    private static int Position(string? position) => position?.ToUpperInvariant() switch { "TOP" => 0, "JUNGLE" => 1, "MIDDLE" or "MID" => 2, "BOTTOM" or "BOT" or "ADC" => 3, "UTILITY" or "SUP" or "SUPPORT" => 4, _ => 5 };
}

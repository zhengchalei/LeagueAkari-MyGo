using System.Globalization;
using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record OpggBalanceRow(string Key, double Delta, bool Buff, bool Percentage);
public static class OpggSessionData
{
    public static int[] Champions(JsonElement flow, JsonElement selection)
    {
        string phase = flow.Text("phase", flow.Field("session").Text("phase"));
        IEnumerable<int> ids = phase is "GameStart" or "InProgress" or "WaitingForStats" or "PreEndOfGame" or "EndOfGame" or "Reconnect"
                ? new[] { "teamOne", "teamTwo" }.SelectMany(team => flow.Field("session").Field("gameData").Field(team).Items()).Select(p => (int)p.Number("championId")) : [];
        // A zero confirmed champion still permits the current hover/pick intent.
        if (phase == "ChampSelect") ids = new[] { "myTeam", "theirTeam" }.SelectMany(team => selection.Field("session").Field(team).Items()).Select(p => (int)(p.Number("championId") > 0 ? p.Number("championId") : p.Number("championPickIntent")));
        return ids.Where(id => id > 0).Distinct().ToArray();
    }
    public static bool CanAutoApplyRunesAndSpells(string mode) => mode != "KIWI";
    public static string[] ConfigKeys(string mode, string queueType, string? position) => mode == "CLASSIC" ? queueType.StartsWith("RANKED_") ? ["ranked-" + (position ?? "undefined"), "ranked-default"] : ["normal"] : mode switch { "ARAM" => ["aram"], "URF" or "ARURF" => ["urf"], "NEXUSBLITZ" => ["nexusblitz"], "ULTBOOK" => ["ultbook"], _ => [] };
    public static OpggBalanceRow[] Balance(JsonElement record)
    {
        var rows = new List<OpggBalanceRow>();
        foreach (var (key, percentage, inverse) in new[] { ("damage_dealt", true, false), ("damage_taken", true, true), ("attack_speed", true, false), ("cooldown_reduction", false, false), ("healing", true, false), ("tenacity", false, false), ("shield_amount", true, false), ("energy_regen", true, false), ("area_of_effect_damage", true, false) })
        {
            var value = record.Field(key); if (value.ValueKind != JsonValueKind.Number) continue;
            double delta = value.GetDouble() - (percentage ? 100 : 0); if (delta == 0) continue;
            rows.Add(new(key, delta, inverse ? delta < 0 : delta > 0, percentage));
        }
        return rows.ToArray();
    }
    public static string DeltaText(OpggBalanceRow row) => row.Delta.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + (row.Percentage ? "%" : "");
}

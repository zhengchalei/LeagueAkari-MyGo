using System.Globalization;
using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record ChampionConfigListEntry(int Id, string Name, bool Runes, bool Spells);

public static class ChampionConfigEditorData
{
    public static readonly string[] Modes = ["ranked", "normal", "aram", "urf", "nexusblitz", "ultbook"];
    public static string GameMode(string mode) => mode switch { "aram" => "ARAM", "urf" => "URF", "nexusblitz" => "NEXUSBLITZ", "ultbook" => "ULTBOOK", _ => "CLASSIC" };
    public static ChampionConfigListEntry[] Sort(IEnumerable<ChampionConfigListEntry> entries) => entries.OrderBy(e => e.Id is >= 3000 and < 4000 ? 1 : 0)
        .ThenByDescending(e => e.Id is >= 3000 and < 4000 ? 0 : (e.Runes ? 1 : 0) + (e.Spells ? 1 : 0))
        .ThenByDescending(e => e.Id is >= 3000 and < 4000 ? false : e.Runes)
        .ThenBy(e => e.Name, StringComparer.Create(CultureInfo.GetCultureInfo("zh-Hans-CN"), false)).ToArray();
}

/// <summary>One champion configuration request at a time; request payloads are frozen before awaiting the backend.</summary>
public sealed class ChampionConfigWriter(Func<string, string, object?[], Task<JsonElement>> call)
{
    public bool IsWriting { get; private set; }
    public async Task<bool> SaveAsync(int championId, string mode, string position, bool runes, ChampionConfigDraft draft, IEnumerable<int> availableSpells)
    {
        if (IsWriting) return false;
        if (championId <= 0 || !ChampionConfigEditorData.Modes.Contains(mode) || (runes ? !draft.ValidRunes() : !draft.ValidSpells(availableSpells))) throw new InvalidOperationException("Invalid champion configuration");
        object? payload = runes ? draft.RunePayload : draft.SpellPayload;
        payload = payload is null ? null : JsonSerializer.SerializeToElement(payload);
        IsWriting = true;
        try
        {
            await call("auto-champ-config-main", runes ? "updateRunes" : "updateSummonerSpells", [championId, ChampionConfigDraft.StorageKey(mode, position), payload]);
            return true;
        }
        finally { IsWriting = false; }
    }
}

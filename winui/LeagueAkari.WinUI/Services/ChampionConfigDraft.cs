using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record RuneConfig(int PrimaryStyleId, int SubStyleId, int[] SelectedPerkIds);
public sealed record SpellConfig(int Spell1Id, int Spell2Id);

/// <summary>Edits remain local until Save; a null draft represents an intentional clear.</summary>
public sealed class ChampionConfigDraft
{
    private readonly JsonElement _catalog;
    private readonly List<int> _secondaryHistory = [];
    public RuneConfig? Runes { get; private set; }
    public SpellConfig? Spells { get; private set; }
    private RuneConfig? _savedRunes;
    private SpellConfig? _savedSpells;
    public bool IsSupported => _catalog.Number("schemaVersion") == 2;
    public bool RunesChanged => !Same(Runes, _savedRunes);
    public bool SpellsChanged => Spells != _savedSpells;
    public IEnumerable<JsonElement> Styles => _catalog.Field("styles").ValueKind == JsonValueKind.Object ? _catalog.Field("styles").EnumerateObject().Select(p => p.Value) : _catalog.Field("styles").Items();
    public JsonElement Style(int id) => Styles.FirstOrDefault(s => s.Number("id") == id);
    public static string StorageKey(string mode, string position) => mode == "ranked" ? mode + "-" + position : mode;
    public ChampionConfigDraft(JsonElement catalog, RuneConfig? runes, SpellConfig? spells) { _catalog = catalog; _savedRunes = Copy(runes); _savedSpells = spells; RestoreRunes(); RestoreSpells(); }
    private static RuneConfig? Copy(RuneConfig? config) => config is null ? null : config with { SelectedPerkIds = [.. config.SelectedPerkIds] };
    private static bool Same(RuneConfig? a, RuneConfig? b) => a is null || b is null ? a == b : a.PrimaryStyleId == b.PrimaryStyleId && a.SubStyleId == b.SubStyleId && a.SelectedPerkIds.SequenceEqual(b.SelectedPerkIds);
    public static RuneConfig? ReadRunes(JsonElement data) => data.ValueKind != JsonValueKind.Object ? null : new((int)data.Number("primaryStyleId"), (int)data.Number("subStyleId"), data.Field("selectedPerkIds").Items().Select(v => (int)v.TryNumber()).ToArray());
    public static SpellConfig? ReadSpells(JsonElement data) => data.ValueKind != JsonValueKind.Object ? null : new((int)data.Number("spell1Id"), (int)data.Number("spell2Id"));
    public void RestoreRunes()
    {
        Runes = Copy(_savedRunes);
        if (IsSupported && Runes is { } current && Style(current.PrimaryStyleId).ValueKind != JsonValueKind.Object) SelectPrimary((int)Styles.FirstOrDefault().Number("id"));
        SeedSecondaryHistory();
    }
    public void RestoreSpells() => Spells = _savedSpells;
    public void ClearRunes() { Runes = null; _secondaryHistory.Clear(); }
    public void ClearSpells() => Spells = null;
    public void CreateRunes() { if (!IsSupported) return; Runes = new(0, 0, new int[9]); SelectPrimary((int)Styles.FirstOrDefault().Number("id")); }
    public void CreateSpells(IEnumerable<int> available) { var ids = available.Distinct().ToArray(); Spells = new(ids.ElementAtOrDefault(0), ids.ElementAtOrDefault(1)); }
    public void NormalizeSpells(IEnumerable<int> available)
    {
        if (Spells is not { } current) return;
        var ids = available.Distinct().ToArray(); int first = current.Spell1Id, second = current.Spell2Id;
        if (!ids.Contains(first)) first = ids.FirstOrDefault(id => id != second);
        if (!ids.Contains(second) || second == first) second = ids.FirstOrDefault(id => id != first);
        Spells = new(first, second);
    }
    private void SeedSecondaryHistory()
    {
        _secondaryHistory.Clear(); if (Runes is not { } current) return;
        var slots = Slots(current.SubStyleId, "kMixedRegularSplashable");
        foreach (int perk in current.SelectedPerkIds.Skip(4).Take(2))
        {
            int row = Array.FindIndex(slots, s => Perks(s).Contains(perk)); if (row >= 0 && !_secondaryHistory.Contains(row)) _secondaryHistory.Add(row);
        }
    }
    public JsonElement[] Slots(int style, string kind) => Style(style).Field("slots").Items().Where(s => s.Text("type") == kind).ToArray();
    public static int[] Perks(JsonElement slot) => slot.Field("perks").Items().Select(p => (int)p.TryNumber()).ToArray();
    public void SelectPrimary(int id)
    {
        if (Runes is not { } current || Style(id).ValueKind != JsonValueKind.Object) return;
        var allowed = Style(id).Field("allowedSubStyles").Items().Select(v => (int)v.TryNumber()).ToArray();
        Runes = current with { PrimaryStyleId = id, SubStyleId = allowed.Contains(current.SubStyleId) ? current.SubStyleId : allowed.FirstOrDefault(), SelectedPerkIds = current.SelectedPerkIds.Length == 9 ? [.. current.SelectedPerkIds] : new int[9] };
        if (Runes.SubStyleId != current.SubStyleId) SeedSecondaryHistory();
    }
    public void SelectSecondary(int id)
    {
        if (Runes is not { } current || !Style(current.PrimaryStyleId).Field("allowedSubStyles").Items().Any(v => v.TryNumber() == id)) return;
        Runes = current with { SubStyleId = id, SelectedPerkIds = current.SelectedPerkIds.Length == 9 ? [.. current.SelectedPerkIds] : new int[9] }; if (id != current.SubStyleId) SeedSecondaryHistory();
    }
    public void SelectPrimaryPerk(int index, int id)
    {
        if (Runes is not { } current || index is < 0 or > 8 || index is 4 or 5) return;
        var slots = index < 4 ? Slots(current.PrimaryStyleId, "kKeyStone").Concat(Slots(current.PrimaryStyleId, "kMixedRegularSplashable")).ToArray() : Slots(current.PrimaryStyleId, "kStatMod");
        int slot = index < 4 ? index : index - 6;
        if (slot >= slots.Length || !Perks(slots[slot]).Contains(id)) return;
        var ids = current.SelectedPerkIds.Length == 9 ? current.SelectedPerkIds.ToArray() : new int[9]; ids[index] = id; Runes = current with { SelectedPerkIds = ids };
    }
    public void SelectSecondaryPerk(int row, int id)
    {
        if (Runes is not { } current) return; var slots = Slots(current.SubStyleId, "kMixedRegularSplashable");
        if (row < 0 || row >= slots.Length || !Perks(slots[row]).Contains(id)) return;
        var selected = new Dictionary<int, int>();
        foreach (int previous in current.SelectedPerkIds.Skip(4).Take(2)) { int position = Array.FindIndex(slots, slot => Perks(slot).Contains(previous)); if (position >= 0) selected[position] = previous; }
        selected[row] = id;
        if (selected.Count > 2) { int oldest = _secondaryHistory.FirstOrDefault(selected.ContainsKey, selected.Keys.First()); selected.Remove(oldest); _secondaryHistory.Remove(oldest); }
        var ids = current.SelectedPerkIds.Length == 9 ? current.SelectedPerkIds.ToArray() : new int[9]; var chosen = selected.OrderBy(p => p.Key).Select(p => p.Value).ToArray();
        if (current.SelectedPerkIds.Skip(4).Take(2).SequenceEqual(chosen)) return;
        _secondaryHistory.Remove(row); _secondaryHistory.Add(row);
        ids[4] = chosen.ElementAtOrDefault(0); ids[5] = chosen.ElementAtOrDefault(1); Runes = current with { SelectedPerkIds = ids };
    }
    public bool ValidRunes()
    {
        if (Runes is null) return true; var r = Runes;
        if (!IsSupported || r.SelectedPerkIds.Length != 9 || !Style(r.PrimaryStyleId).Field("allowedSubStyles").Items().Any(v => v.TryNumber() == r.SubStyleId)) return false;
        var slots = Slots(r.PrimaryStyleId, "kKeyStone").Concat(Slots(r.PrimaryStyleId, "kMixedRegularSplashable")).Concat(Slots(r.PrimaryStyleId, "kStatMod")).ToArray();
        var ids = r.SelectedPerkIds.Take(4).Concat(r.SelectedPerkIds.Skip(6)).ToArray(); if (slots.Length != 7 || slots.Where((s, i) => !Perks(s).Contains(ids[i])).Any()) return false;
        var sub = Slots(r.SubStyleId, "kMixedRegularSplashable"); int a = Array.FindIndex(sub, s => Perks(s).Contains(r.SelectedPerkIds[4])), b = Array.FindIndex(sub, s => Perks(s).Contains(r.SelectedPerkIds[5])); return a >= 0 && b >= 0 && a != b;
    }
    public void SelectSpell(bool first, int id, IEnumerable<int> available)
    {
        var ids = available.Distinct().ToArray(); if (Spells is not { } current || !ids.Contains(id)) return;
        Spells = first ? new(id, id == current.Spell2Id ? current.Spell1Id : current.Spell2Id) : new(id == current.Spell1Id ? current.Spell2Id : current.Spell1Id, id);
    }
    public bool ValidSpells(IEnumerable<int> available) => Spells is null || Spells.Spell1Id != Spells.Spell2Id && available.Contains(Spells.Spell1Id) && available.Contains(Spells.Spell2Id);
    public object? RunePayload => Runes is { } r ? new { primaryStyleId = r.PrimaryStyleId, subStyleId = r.SubStyleId, selectedPerkIds = r.SelectedPerkIds } : null;
    public object? SpellPayload => Spells is { } s ? new { spell1Id = s.Spell1Id, spell2Id = s.Spell2Id } : null;
    public void AcceptRunes() => _savedRunes = Copy(Runes);
    public void AcceptSpells() => _savedSpells = Spells;
}

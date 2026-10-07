using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record AutoSelectGroup(string Id, string[] Positions, int[] AdditionalPicks, int[] AdditionalBans, int[] ExcludedPicks, int[] ExcludedBans);
public sealed record AutoSelectChampion(int Id, string Name, string Keywords);

public static class AutoSelectData
{
    public static AutoSelectGroup[] Groups(JsonElement state) => state.Field("groups").Items().Select(g => new AutoSelectGroup(g.Text("groupId"), g.Field("positions").Items().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).ToArray(), Numbers(g.Field("additionalPicks")), Numbers(g.Field("additionalBans")), Numbers(g.Field("excludedPicks")), Numbers(g.Field("excludedBans")))).Where(g => g.Id.Length > 0).ToArray();
    public static int[] Numbers(JsonElement value) => value.Items().Where(v => v.ValueKind == JsonValueKind.Number).Select(v => (int)v.TryNumber()).ToArray();
    public static string Path(bool ban, string groupId, string position) => (ban ? "banConfig." : "pickConfig.") + groupId + ".champions." + position;
    public static AutoSelectChampion[] Champions(JsonElement catalog, JsonElement extra)
    {
        var heroes = extra.Field("heroListMap");
        var entries = extra.Field("heroList").Field("hero").Items().Where(h => h.Text("heroId").Length > 0).DistinctBy(h => h.Text("heroId")).ToDictionary(h => h.Text("heroId"), h => h);
        return catalog.ValueKind != JsonValueKind.Object ? [] : catalog.EnumerateObject().Select(p => new AutoSelectChampion((int)p.Value.Number("id", int.TryParse(p.Name, out int id) ? id : 0), p.Value.Text("name"), heroes.Field(p.Name).Text("keywords", entries.GetValueOrDefault(p.Name).Text("keywords")))).Where(c => c.Id != 0).ToArray();
    }
    public static AutoSelectChampion[] Candidates(IEnumerable<AutoSelectChampion> champions, AutoSelectGroup group, bool ban, IReadOnlySet<int> disabled, string dummy, string bravery)
    {
        var additional = ban ? group.AdditionalBans : group.AdditionalPicks;
        var excluded = ban ? group.ExcludedBans : group.ExcludedPicks;
        var candidates = champions.Where(c => c.Id != -3 && (c.Id != -1 || !excluded.Contains(-1))).ToList();
        if (!excluded.Contains(-1) && candidates.All(c => c.Id != -1)) candidates.Add(new(-1, dummy, ""));
        if (additional.Contains(-3)) candidates.Add(new(-3, bravery, ""));
        return candidates.Where(c => !disabled.Contains(c.Id)).DistinctBy(c => c.Id).OrderBy(c => c.Id < 0 ? 0 : 1).ThenBy(c => c.Id < 0 ? c.Id : 0).ThenBy(c => c.Name, StringComparer.CurrentCulture).ToArray();
    }
    public static bool Matches(AutoSelectChampion champion, string search, string? position, JsonElement recommendations)
    {
        if (!string.IsNullOrEmpty(position) && recommendations.ValueKind == JsonValueKind.Object)
        {
            var recommended = recommendations.EnumerateObject().SelectMany(p => p.Value.Field("recommendedPositions").Items().Select(v => v.GetString())).ToArray();
            if (recommended.Any(p => p?.Equals(position, StringComparison.OrdinalIgnoreCase) == true) && !recommendations.Field(champion.Id.ToString()).Field("recommendedPositions").Items().Any(p => p.GetString()?.Equals(position, StringComparison.OrdinalIgnoreCase) == true)) return false;
        }
        return champion.Id.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) || (champion.Name + "," + champion.Keywords).Split(',').Any(label => NativePinyin.Matches(search, label));
    }
    public static int[] Add(IEnumerable<int> ids, int id) => ids.Append(id).Distinct().ToArray();
    public static int[] Remove(IEnumerable<int> ids, int id) => ids.Where(value => value != id).ToArray();
    public static int[] Move(IEnumerable<int> ids, int id, int offset)
    {
        var result = ids.ToList(); int index = result.IndexOf(id), next = index + offset;
        if (index < 0 || next < 0 || next >= result.Count) return result.ToArray();
        result.RemoveAt(index); result.Insert(next, id); return result.ToArray();
    }
    public static int[] Drop(IEnumerable<int> ids, int dragged, int target)
    {
        var result = ids.ToList(); int index = result.IndexOf(dragged), next = result.IndexOf(target);
        if (dragged == target || index < 0 || next < 0) return result.ToArray();
        result.RemoveAt(index); result.Insert(next, dragged); return result.ToArray();
    }
}

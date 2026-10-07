using System.Text.Json.Nodes;

namespace LeagueAkari.WinUI.Services;

public sealed record OpggQuery(string Region, string Mode, string Position, string Tier, string Version)
{
    public OpggQuery Normalize() => this with { Position = Mode != "ranked" ? "none" : Position == "none" || Position.Length == 0 ? "mid" : Position };
    public string Root => $"/api/{Uri.EscapeDataString(Region)}/champions/{Uri.EscapeDataString(Mode)}";
    public string Parameters => (Mode == "arena" ? Array.Empty<string>() : new[] { "tier=" + Uri.EscapeDataString(Tier) }).Concat(Version.Length > 0 ? new[] { "version=" + Uri.EscapeDataString(Version) } : []).ToArray() is { Length: > 0 } parts ? "?" + string.Join("&", parts) : "";
    public string ChampionPath(int id) => Root + "/" + id + (Mode == "arena" ? "" : "/" + Position) + Parameters;
}

public sealed record OpggLoadResult(OpggQuery Query, string[] Versions, JsonObject? Champions, JsonObject? Detail, JsonObject? Augments);
public sealed record OpggItemGroup(string Field, int Index, double PickRate, int[] Items);
public static class OpggData
{
    public static async Task<OpggLoadResult> LoadAsync(OpggQuery target, OpggQuery current, string[] versions, bool hasList, bool forceVersions, int hero, Func<string, Task<JsonObject>> request, CancellationToken cancellation = default)
    {
        async Task<JsonObject> Fetch(string path)
        {
            cancellation.ThrowIfCancellationRequested(); var response = await request(path).WaitAsync(cancellation); cancellation.ThrowIfCancellationRequested(); return response;
        }
        target = target.Normalize();
        if (forceVersions || versions.Length == 0)
        {
            var response = await Fetch(target.Root + "/versions"); versions = (response["data"] as JsonArray ?? []).Select(version => version?.ToString() ?? "").Where(version => version.Length > 0).ToArray();
            if (versions.Length == 0) throw new InvalidOperationException("No OP.GG version found");
            target = target with { Version = versions.Contains(target.Version) ? target.Version : versions[0] };
        }
        bool reloadList = forceVersions || !hasList || target.Region != current.Region || target.Mode != current.Mode || target.Tier != current.Tier || target.Version != current.Version;
        var champions = reloadList ? await Fetch(target.Root + target.Parameters) : null;
        var detail = hero > 0 ? await Fetch(target.ChampionPath(hero)) : null;
        var augments = hero > 0 && target.Mode == "aram" ? await Fetch($"/api/contents/stats/champions/{hero}/aram-augments") : null;
        cancellation.ThrowIfCancellationRequested(); return new(target, versions, champions, detail, augments);
    }
    public static double Number(JsonNode? node, string key, double fallback = 0) => double.TryParse(node?[key]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : fallback;
    public static JsonNode? Position(JsonNode? champion, string position) => (champion?["positions"] as JsonArray)?.FirstOrDefault(p => p?["name"]?.ToString().Equals(position, StringComparison.OrdinalIgnoreCase) == true);
    public static JsonNode? Stats(JsonNode? champion, OpggQuery query, bool fallback = false) => query.Mode == "ranked" ? Position(champion, query.Position)?["stats"] ?? (fallback ? champion?["average_stats"] : null) : champion?["average_stats"];
    public static double Rank(JsonNode? stats, bool ranked) => Number(ranked ? stats?["tier_data"] : stats, "rank", double.PositiveInfinity);
    public static double Tier(JsonNode? stats, bool ranked) => Number(ranked ? stats?["tier_data"] : stats, "tier", double.PositiveInfinity);
    public static bool HasNumber(JsonNode? node, string key) => node?[key] is not null && double.TryParse(node[key]!.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
    public static bool HasWin(JsonNode? stats) => HasNumber(stats, "win_rate") || HasNumber(stats, "win") && HasNumber(stats, "play");
    public static double DisplayRank(JsonNode? stats, bool ranked, int index) { double rank = Rank(stats, ranked); return double.IsFinite(rank) && rank != 0 ? rank : index + 1; }
    public static double Win(JsonNode? stats) => stats?["win_rate"] is not null ? Number(stats, "win_rate") : Number(stats, "win") / Math.Max(1, Number(stats, "play"));
    public static bool HasItems(JsonNode? build) => new[] { "starter_items", "boots", "prism_items", "core_items", "last_items" }.Any(key => build?[key] is JsonArray { Count: > 0 });
    public static int[] Ids(JsonNode? node) => node is JsonArray array ? array.Where(x => int.TryParse(x?.ToString(), out _)).Select(x => int.Parse(x!.ToString())).ToArray() : [];
    public static OpggItemGroup[] ItemGroups(JsonNode? build)
    {
        var groups = new List<OpggItemGroup>();
        foreach (string field in new[] { "starter_items", "boots", "prism_items", "core_items", "last_items" })
        {
            if (build?[field] is not JsonArray { Count: > 0 } rows) continue;
            if (field is "starter_items" or "core_items")
                foreach (var (row, index) in rows.Take(field == "starter_items" ? 3 : 4).Select((r, i) => (r, i + 1))) groups.Add(new(field, index, Number(row, "pick_rate"), Ids(row?["ids"]).Select(RestoreRecipe).ToArray()));
            else groups.Add(new(field, 0, 0, rows.SelectMany(row => Ids(row?["ids"])).Select(RestoreRecipe).ToArray()));
        }
        return groups.ToArray();
    }
    public static int RestoreRecipe(int id) => id switch { 3042 => 3004, 223042 => 223004, 323042 => 323004, 3040 => 3003, 223040 => 223003, 323040 => 323003, 3121 => 3119, 223121 => 223119, 323121 => 323119, 2530 => 2526, 222530 => 222526, 322530 => 322526, _ => id };
    public static string ItemUid(int champion, OpggQuery query, string? responseVersion) => string.Join("-", new[] { "akari1", champion.ToString(), query.Mode, query.Region, query.Tier, query.Position, responseVersion ?? "" }.Select(value => value.Length == 0 ? "_" : value));
    public static int ActiveChampion(JsonNode? session)
    {
        int cell = (int)Number(session, "localPlayerCellId", -1);
        var action = (session?["actions"] as JsonArray)?.SelectMany(group => group as JsonArray ?? []).FirstOrDefault(action => Number(action, "actorCellId", -2) == cell && action?["type"]?.ToString() == "pick" && Number(action, "championId") != 0);
        return (int)(action is not null ? Number(action, "championId") : Number((session?["myTeam"] as JsonArray)?.FirstOrDefault(player => Number(player, "cellId", -2) == cell), "championId"));
    }
    public static IEnumerable<JsonNode?> SortAugments(IEnumerable<JsonNode?> items, string sort) => sort switch
    {
        "performance" => items.OrderBy(item => Number(item, "popular") == 0).ThenByDescending(item => Number(item, "performance")),
        "popular" => items.OrderByDescending(item => Number(item, "popular")),
        _ => items.OrderBy(item => Number(item, "tier"))
    };
}

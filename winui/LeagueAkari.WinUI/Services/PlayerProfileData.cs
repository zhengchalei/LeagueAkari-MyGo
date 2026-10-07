using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record PlayerProfileData(string Puuid, string Name, string Tag, int IconId, double? Level)
{
    public string RiotId => Tag.Length == 0 ? Name : Name + "#" + Tag;
    public static PlayerProfileData Read(JsonElement player, JsonElement social = default)
    {
        double? Number(string field) => player.Field(field).ValueKind == JsonValueKind.Number ? player.Number(field) : null;
        return new(player.Text("puuid"), player.Text("gameName", player.Text("displayName", player.Text("name", "—"))), player.Text("tagLine"), (int)player.Number("profileIconId"), Number("summonerLevel") ?? Number("level"));
    }
}

public sealed record PlayerRankEntry(string QueueType, string Tier, string Division, double? LeaguePoints, double? Wins, double? Losses, string HighestTier, string HighestDivision, string PreviousEndTier, string PreviousEndDivision, string PreviousHighestTier, string PreviousHighestDivision)
{
    public bool IsRanked => IsTier(Tier);
    public double? WinRate => IsRanked && Wins is { } wins && Losses is > 0 ? wins / (wins + Losses.Value) : null;
    public static bool IsTier(string? value) => value is not null and not "" and not "NA" and not "NONE" and not "UNRANKED";
    public static PlayerRankEntry Read(JsonElement value, string queue = "")
    {
        double? Number(string field) => value.Field(field).ValueKind == JsonValueKind.Number ? value.Number(field) : null;
        return new(value.Text("queueType", queue), value.Text("tier"), value.Text("division", value.Text("rank")), Number("leaguePoints"), Number("wins"), Number("losses"), value.Text("highestTier"), value.Text("highestDivision"), value.Text("previousSeasonEndTier"), value.Text("previousSeasonEndDivision"), value.Text("previousSeasonHighestTier"), value.Text("previousSeasonHighestDivision"));
    }
    public static PlayerRankEntry[] All(JsonElement ranked)
    {
        var entries = new Dictionary<string, PlayerRankEntry>();
        var queues = ranked.Field("queues");
        if (queues.ValueKind == JsonValueKind.Array)
            foreach (var item in queues.Items()) { if (item.ValueKind != JsonValueKind.Object) continue; var entry = Read(item); if (entry.QueueType.Length > 0) entries.TryAdd(entry.QueueType, entry); }
        else if (queues.ValueKind == JsonValueKind.Object)
            foreach (var item in queues.EnumerateObject()) { if (item.Value.ValueKind != JsonValueKind.Object) continue; var entry = Read(item.Value, item.Name); entries.TryAdd(entry.QueueType, entry); }
        var map = ranked.Field("queueMap");
        if (map.ValueKind == JsonValueKind.Object)
            foreach (var item in map.EnumerateObject()) { if (item.Value.ValueKind != JsonValueKind.Object) continue; var entry = Read(item.Value, item.Name); entries[entry.QueueType] = entry; }
        int Order(string queue) => queue switch { "RANKED_SOLO_5x5" => 1, "RANKED_FLEX_SR" => 2, "RANKED_TFT" => 3, "RANKED_TFT_TURBO" => 4, "RANKED_TFT_DOUBLE_UP" => 5, _ => int.MaxValue };
        return entries.Values.OrderBy(entry => Order(entry.QueueType)).ToArray();
    }
    public static PlayerRankEntry[] Cards(JsonElement ranked) => All(ranked).Where(entry => entry.QueueType is "RANKED_SOLO_5x5" or "RANKED_FLEX_SR").ToArray();
}

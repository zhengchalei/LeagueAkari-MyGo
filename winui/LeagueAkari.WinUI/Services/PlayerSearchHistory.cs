using System.Text.Json;
using System.Text.Json.Serialization;

namespace LeagueAkari.WinUI.Services;

public sealed class PlayerSearchEntry
{
    [JsonPropertyName("puuid")] public string Puuid { get; set; } = "";
    [JsonPropertyName("sgpServerId")] public string Server { get; set; } = "";
    [JsonPropertyName("summoner")] public JsonElement Summoner { get; set; }
    [JsonPropertyName("isPinned")] public bool IsPinned { get; set; }
}

public sealed class PlayerSearchHistory(BackendClient backend)
{
    public async Task<List<PlayerSearchEntry>> ReadAsync()
    {
        var value = await backend.CallAsync("setting-factory-main", "get", "player-tabs-renderer", "searchHistory");
        return value.ValueKind == JsonValueKind.Array ? JsonSerializer.Deserialize<List<PlayerSearchEntry>>(value.GetRawText()) ?? [] : [];
    }
    private Task<JsonElement> WriteAsync(List<PlayerSearchEntry> values) => backend.CallAsync("setting-factory-main", "set", "player-tabs-renderer", "searchHistory", values);
    public async Task SaveAsync(string puuid, string server, JsonElement summoner)
    {
        var list = await ReadAsync();
        int old = list.FindIndex(v => v.Puuid == puuid);
        var item = new PlayerSearchEntry { Puuid = puuid, Server = server, Summoner = summoner, IsPinned = old >= 0 && list[old].IsPinned };
        if (old >= 0 && item.IsPinned) list[old] = item;
        else
        {
            if (old >= 0) list.RemoveAt(old);
            int first = list.FindIndex(v => !v.IsPinned);
            list.Insert(first < 0 ? list.Count : first, item);
            if (list.Count > 20)
            {
                int last = list.FindLastIndex(v => !v.IsPinned);
                list.RemoveAt(last < 0 ? list.Count - 1 : last);
            }
        }
        await WriteAsync(list);
    }
    public async Task PinAsync(string puuid)
    {
        var values = await ReadAsync();
        var entry = values.FirstOrDefault(v => v.Puuid == puuid);
        if (entry is null) return;
        entry.IsPinned = !entry.IsPinned;
        await WriteAsync(values.OrderByDescending(v => v.IsPinned).ToList());
    }
    public async Task DeleteAsync(string puuid)
    {
        var values = await ReadAsync();
        values.RemoveAll(v => v.Puuid == puuid);
        await WriteAsync(values);
    }
}

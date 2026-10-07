using System.Text.Json;
using LeagueAkari.WinUI.Services;

int passed = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
Check(PlayerTabTransitions.SelectedIndexAfterClose(1, 3) == 1, "Closing a middle tab selects its right neighbor");
Check(PlayerTabTransitions.SelectedIndexAfterClose(3, 3) == 2, "Closing the last tab selects its left neighbor");
Check(PlayerTabTransitions.SelectedIndexAfterClose(0, 0) == -1, "Closing the only tab clears selection");
Check(!PlayerTabTransitions.CanCloseOthers(0, 1) && PlayerTabTransitions.CanCloseOthers(0, 2), "Close others disabled for a single tab");
Check(!PlayerTabTransitions.CanCloseRight(2, 3) && PlayerTabTransitions.CanCloseRight(1, 3), "Close right disabled at the final tab");
Check(!PlayerTabTransitions.CanCloseOthers(-1, 3) && !PlayerTabTransitions.CanCloseRight(-1, 3), "Removed tab has no close targets");

var backend = new BackendClient();
var history = new PlayerSearchHistory(backend);
JsonElement Player(string name) => JsonSerializer.SerializeToElement(new { gameName = name, tagLine = "TAG" });
await history.SaveAsync("a", "TENCENT_HN1", Player("Alpha"));
await history.SaveAsync("b", "TENCENT_HN1", Player("Beta"));
await history.SaveAsync("c", "TENCENT_HN10", Player("Gamma"));
Check((await history.ReadAsync()).Select(v => v.Puuid).SequenceEqual(["c", "b", "a"]), "Recent search insertion order is newest first");
await history.PinAsync("b");
await history.PinAsync("a");
Check((await history.ReadAsync()).Select(v => v.Puuid).SequenceEqual(["b", "a", "c"]), "Pin uses stable order before recent entries");
await history.SaveAsync("b", "TENCENT_HN10", Player("Beta updated"));
var pinned = await history.ReadAsync();
Check(pinned.Select(v => v.Puuid).SequenceEqual(["b", "a", "c"]) && pinned[0].IsPinned && pinned[0].Server == "TENCENT_HN10", "Repeated search updates a pinned identity without moving it");
await history.PinAsync("b");
Check((await history.ReadAsync()).Select(v => v.Puuid).SequenceEqual(["a", "b", "c"]), "Unpin preserves relative order of recent entries");
await history.SaveAsync("c", "TENCENT_HN10", Player("Gamma updated"));
Check((await history.ReadAsync()).Select(v => v.Puuid).SequenceEqual(["a", "c", "b"]), "Repeated recent search moves to first unpinned position");
await history.DeleteAsync("a");
var afterDelete = await new PlayerSearchHistory(backend).ReadAsync();
Check(afterDelete.Select(v => v.Puuid).SequenceEqual(["c", "b"]), "Pinned deletion persists and survives reopening history");
Check(backend.LastNamespace == "player-tabs-renderer" && backend.LastKey == "searchHistory", "History writes original persisted setting contract");
await history.PinAsync("b");
for (int i = 0; i < 25; i++) await history.SaveAsync("recent-" + i, "TENCENT_HN1", Player("Recent " + i));
var limited = await history.ReadAsync();
Check(limited.Count == 20 && limited[0].Puuid == "b" && limited[0].IsPinned && limited[1].Puuid == "recent-24" && limited[^1].Puuid == "recent-6", "Twenty-item capacity evicts oldest unpinned entry and retains pinned players");
backend.Values = Enumerable.Range(0, 20).Select(i => new PlayerSearchEntry { Puuid = "pinned-" + i, Server = "TENCENT_HN1", Summoner = Player("Pinned " + i), IsPinned = true }).ToList();
await history.SaveAsync("extra", "TENCENT_HN1", Player("Cannot evict pinned"));
Check((await history.ReadAsync()).All(v => v.IsPinned) && (await history.ReadAsync()).Count == 20, "Full pinned history does not evict a pinned identity");
Console.WriteLine($"Player tabs and search history: {passed} checks passed");

namespace LeagueAkari.WinUI.Services
{
    public sealed class BackendClient
    {
        public List<PlayerSearchEntry> Values { get; set; } = [];
        public string LastNamespace { get; private set; } = "";
        public string LastKey { get; private set; } = "";
        public Task<JsonElement> CallAsync(string ns, string method, params object?[] args)
        {
            if (ns != "setting-factory-main" || args.Length < 2 || (string)args[0]! != "player-tabs-renderer" || (string)args[1]! != "searchHistory") throw new Exception("Unexpected history setting contract");
            if (method == "set")
            {
                Values = JsonSerializer.Deserialize<List<PlayerSearchEntry>>(JsonSerializer.Serialize(args[2]))!;
                LastNamespace = (string)args[0]!; LastKey = (string)args[1]!;
            }
            else if (method != "get") throw new Exception("Unexpected history operation");
            return Task.FromResult(JsonSerializer.SerializeToElement(Values));
        }
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeagueAkari.WinUI.Services;

public sealed record PlayerSearchResult(string Puuid, string Server, JsonElement Player);
public sealed record PlayerSearchOutcome(PlayerSearchResult[] Players, string[] Errors);

public sealed class GlobalPlayerSearch
{
    private readonly Func<string, string, object?[], Task<JsonElement>> _call;
    private static readonly string[] TencentExactFallback = ["TENCENT_HN1", "TENCENT_HN10", "TENCENT_NJ100", "TENCENT_GZ100", "TENCENT_CQ100", "TENCENT_TJ100", "TENCENT_TJ101"];
    public GlobalPlayerSearch(BackendClient backend) : this((ns, method, args) => backend.CallAsync(ns, method, args)) { }
    public GlobalPlayerSearch(Func<string, string, object?[], Task<JsonElement>> call) => _call = call;
    public static string Clean(string text) => Regex.Replace(text, "[\\u0000-\\u001F\\u007F-\\u009F\\u200B-\\u200D\\uFEFF\\u2060-\\u2069\\u202A-\\u202E\\uFFF9-\\uFFFB]", "").Trim();
    public static string Kind(string text) { text = Clean(text); return Guid.TryParseExact(text, "D", out _) ? "puuid" : Regex.IsMatch(text, "^(?!\\s+$)[^#]+(?:#[^#]+)?$") ? text.Contains('#') ? "exact" : "fuzzy" : "invalid"; }
    public static string[] Servers(JsonElement sgp) => sgp.Field("availability").Text("region") == "TENCENT" ? sgp.Field("leagueServers").Field("tencentServerMatchHistoryInteroperability").Items().Select(s => s.GetString() ?? "").Where(s => s.Length > 0).Distinct().ToArray() : [sgp.Field("availability").Text("sgpServerId")];
    public static bool HistoryVisible(string server, JsonElement sgp) => Servers(sgp).Contains(server, StringComparer.Ordinal);
    public static int FriendPriority(JsonElement friend) => friend.Text("availability") switch { "dnd" => 3, "chat" => 2, "away" => 1, _ => 0 };
    public static bool FriendSpectatable(JsonElement friend) => friend.Text("availability") == "dnd" && friend.Field("lol").Text("gameStatus").Equals("ingame", StringComparison.OrdinalIgnoreCase) && (friend.Text("puuid").Length > 0 || friend.Field("lol").Text("puuid").Length > 0) && friend.Field("lol").Text("spectatorKey").Length > 0;
    public static bool ShowServer(IEnumerable<string> servers, string current) { var regions = servers.Distinct().ToArray(); return regions.Length > 1 || regions.Length == 1 && regions[0] != current; }

    public async Task<PlayerSearchOutcome> FindAsync(string input, string selectedServer, string preferred, JsonElement sgp, CancellationToken cancel = default, Action<int, int>? progress = null)
    {
        input = Clean(input); string kind = Kind(input); if (kind == "invalid") throw new ArgumentException("请输入玩家名字、名字#编号或 PUUID");
        string current = sgp.Field("availability").Text("sgpServerId"); string server = selectedServer.Length > 0 ? selectedServer : current;
        var players = new List<PlayerSearchResult>(); var errors = new List<string>();
        Task<JsonElement> Call(string method, object?[] args) { cancel.ThrowIfCancellationRequested(); return _call("winui-backend", method, args).WaitAsync(cancel); }
        async Task<JsonElement[]> Summoners(string[] ids, string target, bool lcu, JsonElement[]? aliases = null)
        {
            cancel.ThrowIfCancellationRequested();
            if (lcu)
            {
                var result = new List<JsonElement>();
                foreach (var id in ids)
                    try { var raw = await Call("lcuRequest", ["GET", "/lol-summoner/v2/summoners/puuid/" + Uri.EscapeDataString(id), null]); result.Add(PlayerDataSource.NormalizeSummoner(raw, false)); }
                    catch (Exception ex) when (!cancel.IsCancellationRequested && ex.Message.Contains("404", StringComparison.Ordinal)) { }
                return result.ToArray();
            }
            if (sgp.Field("leagueServers").Field("servers").Field(target).Text("common").Length == 0) throw new InvalidOperationException("此大区没有可用的 SGP common 接口: " + target);
            if (!sgp.Boolean("isTokenReady")) throw new InvalidOperationException("等待 SGP 登录凭据就绪");
            var summoners = await Call("sgpRequest", [target, "league-session", "POST", "/summoner-ledge/v1/regions/@akari:sgpServerSubId@/summoners/puuids", ids]);
            JsonElement[] names = aliases ?? (await Call("riotRequest", ["POST", "/player-account/lookup/v1/namesets-for-puuids", new { puuids = ids }])).Field("namesets").Items().ToArray();
            return summoners.Items().Where(s => ids.Contains(s.Text("puuid"), StringComparer.Ordinal)).Select(s =>
            {
                var name = names.FirstOrDefault(n => n.Text("puuid") == s.Text("puuid")); if (name.ValueKind != JsonValueKind.Object || name.Text("error").Length > 0) return default;
                return PlayerDataSource.NormalizeSummoner(s, true, aliases == null ? name.Field("gnt").Text("gameName") : name.Field("alias").Text("game_name"), aliases == null ? name.Field("gnt").Text("tagLine") : name.Field("alias").Text("tag_line"));
            }).Where(s => s.ValueKind == JsonValueKind.Object).ToArray();
        }
        void Add(string target, IEnumerable<JsonElement> found) { foreach (var player in found) { string id = player.Text("puuid"); if (id.Length > 0 && !players.Any(p => p.Puuid == id && p.Server == target)) players.Add(new(id, target, player)); } }
        if (kind == "puuid") { Add(server, await Summoners([input], server, server == current)); return new(players.ToArray(), []); }
        string name = input, tag = ""; if (kind == "exact") { int split = input.IndexOf('#'); name = input[..split].Trim(); tag = input[(split + 1)..].Trim(); }
        var aliases = (await Call("riotRequest", ["GET", "/player-account/aliases/v1/lookup?gameName=" + Uri.EscapeDataString(name) + (kind == "exact" ? "&tagLine=" + Uri.EscapeDataString(tag) : ""), null])).Items().ToArray();
        if (kind == "exact")
        {
            if (aliases.Length == 0) return new([], []);
            var alias = aliases[0]; string[] ids = [alias.Text("puuid")];
            Add(server, await Summoners(ids, server, server == current, [alias]));
            if (players.Count == 0 && sgp.Field("availability").Text("region") == "TENCENT" && preferred == "sgp")
                foreach (var target in TencentExactFallback.Where(s => s != server))
                {
                    cancel.ThrowIfCancellationRequested();
                    try { Add(target, await Summoners(ids, target, false, [alias])); }
                    catch (Exception ex) when (!cancel.IsCancellationRequested) { errors.Add(target + ": " + ex.Message); }
                }
        }
        else
        {
            int step = preferred == "lcu" && server == current ? 1 : 16;
            for (int start = 0; start < aliases.Length; start += step)
            {
                cancel.ThrowIfCancellationRequested();
                try { Add(server, await Summoners(aliases.Skip(start).Take(step).Select(a => a.Text("puuid")).Where(p => p.Length > 0).ToArray(), server, step == 1)); }
                catch (Exception ex) when (!cancel.IsCancellationRequested) { errors.Add(ex.Message); }
                progress?.Invoke(Math.Min(start + step, aliases.Length), aliases.Length);
            }
        }
        return new(players.ToArray(), errors.Distinct().ToArray());
    }
}

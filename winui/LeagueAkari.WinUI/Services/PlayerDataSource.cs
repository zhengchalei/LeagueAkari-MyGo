using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeagueAkari.WinUI.Services;

public sealed class PlayerDataSource
{
    private readonly Func<string, string, object?[], Task<JsonElement>> _call;
    private readonly Func<string, string?, Task<JsonElement>> _state;
    private JsonElement _sgp;
    private string _target = "", _preferred = "lcu";
    private bool _preview;
    private readonly SemaphoreSlim _lcuCompletionQueue = new(10);
    public string Server { get; private set; } = "";
    public string Source { get; private set; } = "lcu";
    public string CurrentServer { get; private set; } = "";
    public string? FallbackReason { get; private set; }
    public SourceDecision Decision { get; private set; } = new("load", "lcu");
    public int LastHistoryRawCount { get; private set; }
    public bool IsCrossRegion => Server != CurrentServer;
    public bool SupportsRanked => !IsCrossRegion;
    public bool SupportsMastery => !IsCrossRegion;
    public bool SupportsSocialProfile => !IsCrossRegion;
    public bool SupportsChallenges => SourceAvailability.RequiredSgp(_sgp, Server).Type == "load";

    public PlayerDataSource(BackendClient backend) : this((ns, method, args) => backend.CallAsync(ns, method, args), (ns, state) => backend.StateAsync(ns, state ?? "state")) { }
    public PlayerDataSource(Func<string, string, object?[], Task<JsonElement>> call, Func<string, string?, Task<JsonElement>> state) { _call = call; _state = state; }
    public Task<JsonElement> LcuAsync(string method, string path, object? body = null) => _call("winui-backend", "lcuRequest", [method, path, body]);
    public Task<JsonElement> RiotAsync(string method, string path, object? body = null) => _call("winui-backend", "riotRequest", [method, path, body]);
    public async Task<JsonElement> SgpAsync(string token, string method, string path, object? body = null)
    {
        await RefreshAvailabilityAsync();
        var required = SourceAvailability.RequiredSgp(_sgp, Server);
        if (required.Type != "load") throw new PlayerSourceUnavailableException(required);
        RequireEndpoint(token == "entitlements" ? "matchHistory" : "common");
        return await _call("winui-backend", "sgpRequest", [Server, token, method, path, body]);
    }
    private bool EndpointAvailable(string endpoint) => Server.Length > 0 && _sgp.Field("leagueServers").Field("servers").Field(Server).Text(endpoint).Length > 0;
    private void RequireEndpoint(string endpoint)
    {
        if (!EndpointAvailable(endpoint)) throw new InvalidOperationException($"此大区没有可用的 SGP {endpoint} 接口");
    }
    public async Task ConfigureAsync(string target, string preferred)
    {
        _target = target; _preferred = preferred; _preview = false;
        await EnsureSelectionAsync();
    }
    public async Task ConfigurePreviewAsync(string preferred)
    {
        _target = ""; _preferred = preferred; _preview = true;
        await EnsureSelectionAsync();
    }
    public async Task<SourceDecision> RefreshAvailabilityAsync()
    {
        _sgp = await _state("sgp-main", null);
        CurrentServer = _sgp.Field("availability").Text("sgpServerId"); Server = _target.Length == 0 ? CurrentServer : _target;
        Decision = _preview ? SourceAvailability.Preview(_sgp, Server, _preferred) : SourceAvailability.Resolve(_sgp, Server, CurrentServer, _preferred);
        Source = Decision.Source; FallbackReason = Decision.FallbackReason;
        return Decision;
    }
    private async Task EnsureSelectionAsync()
    {
        if ((await RefreshAvailabilityAsync()).Type != "load") throw new PlayerSourceUnavailableException(Decision);
    }
    private static JsonElement RequireArray(JsonElement value, string label, JsonElement response = default)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            string reason = response.Text("message", response.Text("errorCode"));
            throw new InvalidOperationException(label + "响应格式无效" + (reason.Length > 0 ? ": " + reason : ""));
        }
        return value;
    }
    public async Task<JsonElement> FindAsync(string text)
    {
        await EnsureSelectionAsync();
        text = text.Trim(); if (Guid.TryParse(text, out _) || text.Length > 50) return await ProfileAsync(text);
        int split = text.LastIndexOf('#');
        if (split > 0)
        {
            string name = text[..split], tag = text[(split + 1)..];
            var aliases = await RiotAsync("GET", "/player-account/aliases/v1/lookup?gameName=" + Uri.EscapeDataString(name) + "&tagLine=" + Uri.EscapeDataString(tag));
            var alias = aliases.Items().FirstOrDefault(); string puuid = alias.Text("puuid"); if (puuid.Length == 0) throw new InvalidOperationException("没有找到此玩家");
            return Source == "sgp" ? await SgpSummonerAsync(puuid, alias.Field("alias").Text("game_name", name), alias.Field("alias").Text("tag_line", tag)) : await ProfileAsync(puuid);
        }
        if (IsCrossRegion) throw new InvalidOperationException("跨大区查询请输入完整的玩家名#编号或 PUUID");
        return NormalizeSummoner(await LcuAsync("GET", "/lol-summoner/v1/summoners?name=" + Uri.EscapeDataString(text)), false);
    }
    public async Task<JsonElement> ProfileAsync(string puuid)
    {
        await EnsureSelectionAsync();
        if (Source == "lcu") return NormalizeSummoner(await LcuAsync("GET", "/lol-summoner/v2/summoners/puuid/" + Uri.EscapeDataString(puuid)), false);
        var results = await SummonersAsync([puuid]); var result = results.FirstOrDefault(); if (result.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("此大区未找到玩家"); return result;
    }
    private async Task<JsonElement> SgpSummonerAsync(string puuid, string name, string tag)
    {
        var players = RequireArray(await SgpAsync("league-session", "POST", "/summoner-ledge/v1/regions/@akari:sgpServerSubId@/summoners/puuids", new[] { puuid }), "SGP 玩家资料");
        var player = players.Items().FirstOrDefault(p => p.Text("puuid") == puuid); if (player.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("此大区未找到玩家");
        return NormalizeSummoner(player, true, name, tag);
    }
    public async Task<JsonElement[]> SummonersAsync(string[] puuids)
    {
        await EnsureSelectionAsync();
        if (Source == "lcu") return await Task.WhenAll(puuids.Select(async p => NormalizeSummoner(await LcuAsync("GET", "/lol-summoner/v2/summoners/puuid/" + Uri.EscapeDataString(p)), false)));
        var summonersTask = SgpAsync("league-session", "POST", "/summoner-ledge/v1/regions/@akari:sgpServerSubId@/summoners/puuids", puuids);
        var namesTask = RiotAsync("POST", "/player-account/lookup/v1/namesets-for-puuids", new { puuids });
        await Task.WhenAll(summonersTask, namesTask); var names = RequireArray((await namesTask).Field("namesets"), "Riot 玩家名称").Items().ToArray(); var result = new List<JsonElement>();
        foreach (var player in RequireArray(await summonersTask, "SGP 玩家资料").Items())
        {
            var nameset = names.FirstOrDefault(n => n.Text("puuid") == player.Text("puuid")); if (nameset.ValueKind != JsonValueKind.Object || nameset.Text("error").Length > 0) continue;
            result.Add(NormalizeSummoner(player, true, nameset.Field("gnt").Text("gameName"), nameset.Field("gnt").Text("tagLine")));
        }
        return result.ToArray();
    }
    public static JsonElement NormalizeSummoner(JsonElement value, bool sgp, string name = "", string tag = "")
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("玩家资料格式无效");
        var node = JsonNode.Parse(value.GetRawText())!.AsObject();
        node["gameName"] = sgp ? name : value.Text("gameName", value.Text("displayName")); node["tagLine"] = sgp ? tag : value.Text("tagLine");
        node["summonerId"] = sgp ? value.Number("id") : value.Number("summonerId"); node["summonerLevel"] = sgp ? value.Number("level") : value.Number("summonerLevel");
        node["level"] = node["summonerLevel"]!.DeepClone(); node["expPoints"] = sgp ? value.Number("expPoints") : value.Number("xpSinceLastLevel");
        node["expToNextLevel"] = sgp ? value.Number("expToNextLevel") : value.Number("xpSinceLastLevel") + value.Number("xpUntilNextLevel");
        return JsonSerializer.SerializeToElement(node);
    }
    public Task<JsonElement[]> HistoryAsync(string puuid, int start, int count, int queue) => HistoryAsync(puuid, start, count, queue > 0 ? "q_" + queue : "<akari:all>");
    public async Task<JsonElement[]> HistoryAsync(string puuid, int start, int count, string tag)
    {
        if (start < 0 || count < 1 || count > 200) throw new ArgumentOutOfRangeException(nameof(count));
        await EnsureSelectionAsync();
        JsonElement data;
        if (Source == "sgp")
        {
            string path = $"/match-history-query/v1/products/lol/player/{Uri.EscapeDataString(puuid)}/SUMMARY?startIndex={start}&count={count}";
            if (tag is not "" and not "<akari:all>") path += "&tag=" + Uri.EscapeDataString(tag);
            data = await SgpAsync("entitlements", "GET", path);
            var rows = RequireArray(data.Field("games"), "SGP 战绩", data).Items().ToArray();
            LastHistoryRawCount = rows.Length;
            return rows.Where(g => g.Field("json").ValueKind == JsonValueKind.Object).Select(MatchData.Game).ToArray();
        }
        data = await LcuAsync("GET", $"/lol-match-history/v1/products/lol/{Uri.EscapeDataString(puuid)}/matches?begIndex={start}&endIndex={start + count - 1}");
        var summaries = RequireArray(data.Field("games").Field("games"), "LCU 战绩", data).Items().ToArray();
        var completed = await Task.WhenAll(summaries.Where(g => g.Field("gameId").ValueKind == JsonValueKind.Number).Select(CompleteLcuGameAsync));
        LastHistoryRawCount = summaries.Length;
        return completed;
    }
    private async Task<JsonElement> CompleteLcuGameAsync(JsonElement summary)
    {
        await _lcuCompletionQueue.WaitAsync();
        try
        {
            var complete = await LcuAsync("GET", "/lol-match-history/v1/games/" + summary.Number("gameId"));
            return complete.Field("gameId").ValueKind == JsonValueKind.Number && complete.Number("gameId") == summary.Number("gameId") ? complete : summary;
        }
        catch { return summary; }
        finally { _lcuCompletionQueue.Release(); }
    }
    // 原个人战绩页段位、熟练度与社交资料仅支持本地大区，与战绩摘要来源相互独立。
    public async Task<JsonElement> RankedAsync(string puuid)
    {
        await RefreshAvailabilityAsync();
        if (!SupportsRanked) throw new NotSupportedException("原段位接口不支持跨大区查询");
        return await LcuAsync("GET", "/lol-ranked/v1/ranked-stats/" + Uri.EscapeDataString(puuid));
    }
    public async Task<JsonElement> MasteryAsync(string puuid, int? topCount = null)
    {
        await RefreshAvailabilityAsync();
        if (!SupportsMastery) throw new NotSupportedException("原英雄熟练度接口不支持跨大区查询");
        string path = "/lol-champion-mastery/v1/" + Uri.EscapeDataString(puuid) + "/champion-mastery";
        return await (topCount is { } count ? LcuAsync("POST", path + "/top?count=" + Math.Clamp(count, 1, 200), new { skipCache = true }) : LcuAsync("GET", path));
    }
    public async Task<JsonElement> SocialProfileAsync(string puuid)
    {
        await RefreshAvailabilityAsync();
        if (!SupportsSocialProfile) throw new NotSupportedException("原社交资料接口不支持跨大区查询");
        return await LcuAsync("GET", "/lol-summoner/v1/summoner-profile?puuid=" + Uri.EscapeDataString(puuid));
    }
    public Task<JsonElement> ChallengesAsync(string puuid, string[]? friends = null)
    {
        return SgpAsync("league-session", "POST", "/challenges-client/v2/all-player-data/?puuid=" + Uri.EscapeDataString(puuid), friends ?? Array.Empty<string>());
    }
    public async Task<JsonElement> DetailsAsync(long id) { await EnsureSelectionAsync(); return Source == "sgp" ? await SgpAsync("entitlements", "GET", $"/match-history-query/v1/products/lol/@akari:sgpServerSubId@_{id}/SUMMARY") : await LcuAsync("GET", "/lol-match-history/v1/games/" + id); }
    public async Task<JsonElement> TimelineAsync(long id) { await EnsureSelectionAsync(); return Source == "lcu" ? await LcuAsync("GET", "/lol-match-history/v1/game-timelines/" + id) : await SgpAsync("entitlements", "GET", $"/match-history-query/v1/products/lol/@akari:sgpServerSubId@_{id}/DETAILS"); }
}

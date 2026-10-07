using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using LeagueAkari.WinUI.Services;

int passed = 0;
void Check(string label, bool value) { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
async Task<T> Failure<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T error) { return error; }
    throw new Exception("Expected " + typeof(T).Name);
}
JsonElement State(bool known = true, bool ready = true, bool endpoints = true, string current = "HN1", bool preview = true) => JsonSerializer.SerializeToElement(new
{
    availability = new { sgpServerId = current, serversSupported = new { matchHistory = preview } }, isTokenReady = ready,
    leagueServers = new { servers = known ? new Dictionary<string, object> { ["HN1"] = new { common = "https://mock.invalid", matchHistory = endpoints ? "https://mock.invalid" : "" }, ["HN2"] = new { common = "https://mock.invalid", matchHistory = endpoints ? "https://mock.invalid" : "" } } : [] }
});
await using var protocol = new ProtocolFixture();
protocol.State = State();
PlayerDataSource NewSource() => new(protocol.CallAsync, (_, _) => protocol.StateAsync());

var source = NewSource();
await source.ConfigureAsync("", "sgp");
Check("Ready known local region loads preferred SGP", source.Decision == new SourceDecision("load", "sgp"));
protocol.State = State(preview: false); await source.ConfigurePreviewAsync("sgp");
Check("Connected preview uses serversSupported and falls back even with configured region", source.Source == "lcu" && source.FallbackReason == "sgp-api-unavailable");
await source.ConfigureAsync("", "sgp");
Check("Player history does not inherit preview endpoint fallback", source.Source == "sgp");
protocol.State = State(); await source.ConfigurePreviewAsync("sgp");
Check("Supported preview uses SGP", source.Source == "sgp");
await source.ConfigurePreviewAsync("lcu");
Check("Preview honors explicit LCU preference", source.Source == "lcu");
await source.ConfigureAsync("", "sgp");
protocol.State = State(ready: false); protocol.Calls.Clear();
var wait = await Failure<PlayerSourceUnavailableException>(() => source.HistoryAsync("player", 0, 20, "<akari:all>"));
Check("Lost token after configure waits without LCU fallback or request", wait.Decision.Type == "wait" && wait.Decision.Reason == "sgp-token-not-ready" && protocol.Calls.IsEmpty);
protocol.State = State();
Check("Readiness can be refreshed without reconfigure", (await source.RefreshAvailabilityAsync()).Type == "load");
protocol.State = State(known: false); await source.ConfigureAsync("", "sgp");
Check("Only absent local region configuration permits LCU fallback", source.Source == "lcu" && source.FallbackReason == "sgp-api-unavailable");
await source.ConfigureAsync("", "lcu");
Check("Preferred LCU remains available without SGP", source.Decision == new SourceDecision("load", "lcu"));
var unavailable = await Failure<PlayerSourceUnavailableException>(() => source.ConfigureAsync("HN2", "lcu"));
Check("Cross region cannot use local API even if preferred LCU", unavailable.Decision.Type == "unavailable" && source.Source == "sgp");
protocol.State = State(ready: false);
wait = await Failure<PlayerSourceUnavailableException>(() => source.ConfigureAsync("HN2", "lcu"));
Check("Known cross region waits for token", wait.Decision.Type == "wait");
protocol.State = State(endpoints: false); await source.ConfigureAsync("HN2", "lcu");
protocol.Reply = call => JsonSerializer.SerializeToElement(new[] { new { puuid = "player", id = 12, level = 30 } });
protocol.RiotReply = JsonSerializer.SerializeToElement(new { namesets = new[] { new { puuid = "player", gnt = new { gameName = "跨区", tagLine = "CN" } } } });
var profile = await source.ProfileAsync("player");
Check("Common-only known region can load profile without matchHistory endpoint", profile.Text("gameName") == "跨区" && profile.Number("summonerLevel") == 30);
protocol.Calls.Clear();
var endpoint = await Failure<InvalidOperationException>(() => source.HistoryAsync("player", 0, 20, "<akari:all>"));
Check("Missing matchHistory endpoint surfaces error instead of switching to LCU", endpoint.Message.Contains("matchHistory") && source.Source == "sgp" && protocol.Calls.IsEmpty);
protocol.State = State(); await source.ConfigureAsync("HN2", "lcu");
foreach (var load in new Func<Task>[] { () => source.RankedAsync("player"), () => source.MasteryAsync("player"), () => source.SocialProfileAsync("player") }) await Failure<NotSupportedException>(load);
Check("Local-only ranked mastery social APIs reject cross-region", protocol.Calls.IsEmpty);

protocol.Calls.Clear();
protocol.Reply = _ => JsonSerializer.SerializeToElement(new { games = new object[] { new { json = new { gameId = 101, queueId = 2450 } }, new { metadata = new { gameId = 102 } }, new { json = new { gameId = 103, queueId = 450 } } } });
var games = await source.HistoryAsync("player/name", 20, 3, "q_2450");
var query = protocol.Calls.Single();
Check("Cross-region summary uses target and original pagination/tag protocol", query.Text("method") == "sgpRequest" && query.Field("args").Items().First().GetString() == "HN2" && query.Field("args").Items().ElementAt(3).GetString() == "/match-history-query/v1/products/lol/player/player%2Fname/SUMMARY?startIndex=20&count=3&tag=q_2450");
Check("Malformed SGP row skipped while raw count preserves next-page offset", games.Select(g => (int)g.Number("gameId")).SequenceEqual([101, 103]) && source.LastHistoryRawCount == 3);
protocol.Reply = _ => JsonSerializer.SerializeToElement(new { games = Array.Empty<object>() });
Check("Genuine empty SGP games accepted", (await source.HistoryAsync("player", 0, 20, "<akari:all>")).Length == 0 && source.LastHistoryRawCount == 0);
protocol.Reply = _ => JsonSerializer.SerializeToElement(new { errorCode = "FORBIDDEN", message = "match history is private" });
var malformed = await Failure<InvalidOperationException>(() => source.HistoryAsync("player", 0, 20, "<akari:all>"));
Check("HTTP success error envelope never becomes empty history and retains reason", malformed.Message.Contains("响应格式无效") && malformed.Message.Contains("match history is private"));
protocol.Error = "SGP HTTP 403: match history is private"; protocol.Calls.Clear();
var privacy = await Failure<InvalidOperationException>(() => source.HistoryAsync("player", 0, 20, "<akari:all>"));
Check("HTTP privacy failure retains reason and makes no LCU retry", privacy.Message == protocol.Error && protocol.Calls.Single().Text("method") == "sgpRequest");
protocol.Error = null;
protocol.State = State(ready: false); protocol.Calls.Clear();
wait = await Failure<PlayerSourceUnavailableException>(() => source.ChallengesAsync("player"));
Check("Challenges independently checks latest token", wait.Decision.Type == "wait" && protocol.Calls.IsEmpty);
protocol.State = State();
await source.ConfigureAsync("", "lcu"); protocol.Calls.Clear();
protocol.Reply = call => call.Field("args").Items().ElementAt(1).GetString()!.Contains("/matches?")
    ? JsonSerializer.SerializeToElement(new { games = new { games = Enumerable.Range(1, 24).Select(i => new { gameId = i, participants = new[] { new { participantId = 1 } } }) } })
    : JsonSerializer.SerializeToElement(new { gameId = int.Parse(call.Field("args").Items().ElementAt(1).GetString()!.Split('/').Last()), participants = new[] { new { participantId = 1, stats = new { kills = 7 } } } });
protocol.BlockDetails = true;
var pending = source.HistoryAsync("player", 40, 24, "<akari:all>");
await protocol.TenDetails.Task.WaitAsync(TimeSpan.FromSeconds(10));
Check("LCU completion uses original ten concurrent requests", protocol.MaxDetails == 10 && !pending.IsCompleted);
protocol.ReleaseDetails.TrySetResult(); games = await pending;
Check("LCU summaries completed with full participant stats and stable order", games.Select(g => (int)g.Number("gameId")).SequenceEqual(Enumerable.Range(1, 24)) && games[0].Field("participants").Items().Single().Field("stats").Number("kills") == 7);
var lcuQuery = protocol.Calls.First(c => c.Field("args").Items().ElementAt(1).GetString()!.Contains("/matches?"));
Check("LCU page end remains inclusive", lcuQuery.Field("args").Items().ElementAt(1).GetString()!.EndsWith("begIndex=40&endIndex=63"));
Check("Only failed individual completion retains summary", games[23].Field("participants").Items().Single().Field("stats").ValueKind == JsonValueKind.Undefined && protocol.DetailFailures == 1);
protocol.BlockDetails = false;
protocol.Reply = _ => JsonSerializer.SerializeToElement(new { errorCode = "PRIVATE" });
await Failure<InvalidOperationException>(() => source.HistoryAsync("player", 0, 20, "<akari:all>"));
Check("Malformed LCU list is a failed request not an empty list", true);
protocol.State = State(current: "HN2"); await source.ConfigureAsync("HN1", "lcu");
Check("Changed current server reclassifies previous local player as cross-region", source.IsCrossRegion && source.Source == "sgp");
Console.WriteLine($"Player data source protocol contracts: {passed} passed");

sealed class ProtocolFixture : IAsyncDisposable
{
    readonly HttpListener _listener = new();
    readonly HttpClient _http = new();
    readonly Task _server;
    readonly ConcurrentBag<Task> _handlers = [];
    public JsonElement State { get; set; }
    public JsonElement RiotReply { get; set; }
    public Func<JsonElement, JsonElement> Reply { get; set; } = _ => JsonSerializer.SerializeToElement(new { });
    public string? Error { get; set; }
    public ConcurrentQueue<JsonElement> Calls { get; } = new();
    public bool BlockDetails { get; set; }
    public int MaxDetails, DetailFailures;
    int _activeDetails;
    public TaskCompletionSource TenDetails { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseDetails { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ProtocolFixture()
    {
        var portSocket = new TcpListener(IPAddress.Loopback, 0); portSocket.Start(); int port = ((IPEndPoint)portSocket.LocalEndpoint).Port; portSocket.Stop();
        string url = $"http://127.0.0.1:{port}/"; _listener.Prefixes.Add(url); _listener.Start(); _http.BaseAddress = new Uri(url);
        _server = AcceptAsync();
    }
    async Task AcceptAsync()
    {
        try { while (_listener.IsListening) { var context = await _listener.GetContextAsync(); _handlers.Add(HandleAsync(context)); } }
        catch (HttpListenerException) { } catch (ObjectDisposedException) { }
    }
    async Task HandleAsync(HttpListenerContext context)
    {
        using var json = await JsonDocument.ParseAsync(context.Request.InputStream); var call = json.RootElement.Clone();
        object response;
        if (call.Text("type") == "state") response = new { success = true, data = State };
        else
        {
            Calls.Enqueue(call); bool detail = call.Text("method") == "lcuRequest" && call.Field("args").Items().ElementAt(1).GetString()!.Contains("/games/");
            if (detail && BlockDetails)
            {
                int active = Interlocked.Increment(ref _activeDetails); int max;
                do { max = MaxDetails; if (max >= active) break; } while (Interlocked.CompareExchange(ref MaxDetails, active, max) != max);
                if (active == 10) TenDetails.TrySetResult();
                await ReleaseDetails.Task; Interlocked.Decrement(ref _activeDetails);
            }
            string? error = Error;
            if (detail && call.Field("args").Items().ElementAt(1).GetString()!.EndsWith("/24")) { error = "LCU HTTP 404: unavailable single game"; Interlocked.Increment(ref DetailFailures); }
            response = error is not null ? (object)new { success = false, error = new { message = error } }
                : new { success = true, data = call.Text("method") == "riotRequest" ? RiotReply : Reply(call) };
        }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(response); context.Response.ContentType = "application/json"; await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
    }
    async Task<JsonElement> SendAsync(object envelope)
    {
        using var response = await _http.PostAsJsonAsync("", envelope); using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        if (!json.RootElement.Boolean("success")) throw new InvalidOperationException(json.RootElement.Field("error").Text("message"));
        return json.RootElement.Field("data").Clone();
    }
    public Task<JsonElement> StateAsync() => SendAsync(new { type = "state", ns = "sgp-main" });
    public Task<JsonElement> CallAsync(string ns, string method, object?[] args) => SendAsync(new { type = "call", ns, method, args });
    public async ValueTask DisposeAsync() { _listener.Close(); await _server; await Task.WhenAll(_handlers); _http.Dispose(); }
}

namespace LeagueAkari.WinUI.Services
{
    public sealed class BackendClient
    {
        public Task<JsonElement> CallAsync(string ns, string method, params object?[] args) => throw new NotSupportedException();
        public Task<JsonElement> StateAsync(string ns, string state = "state") => throw new NotSupportedException();
    }
}

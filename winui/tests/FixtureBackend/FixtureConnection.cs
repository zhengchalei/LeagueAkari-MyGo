using System.Text.Json;

// Connection controls use synthetic PIDs and profiles. No discovery or client write occurs.
public sealed class FixtureConnection
{
    public bool Enabled { get; } = Environment.GetEnvironmentVariable("WINUI_FIXTURE_CONNECTION") is "multi" or "connecting";
    private string _state = Environment.GetEnvironmentVariable("WINUI_FIXTURE_CONNECTION") == "connecting" ? "connecting" : "connected";
    private int _pid = 11;
    private bool _failOnce = Environment.GetEnvironmentVariable("WINUI_FIXTURE_CONNECT_FAIL_ONCE") == "1";
    private static object Client(int pid) => new { pid, region = "TENCENT", rsoPlatformId = pid == 22 ? "HN10" : "HN1" };
    public object State() => new { connectionState = _state, auth = _state == "connected" ? Client(_pid) : null, connectingClient = _state == "connecting" ? Client(_pid) : null };
    public object Ux() => new { launchedClients = new[] { Client(11), Client(22) }, hasClientButNoCommandLine = false };
    public object Peek(JsonElement auth, byte[] image) => new { summoner = new { puuid = "fixture-account-" + auth.GetProperty("pid").GetInt32(), gameName = "测试账号" + auth.GetProperty("pid").GetInt32(), tagLine = "CN", displayName = "测试账号", summonerLevel = 180, profileIconId = 1 }, profileIcon = "data:image/png;base64," + Convert.ToBase64String(image) };
    public object? Action(string method, JsonElement[] args)
    {
        if (!Enabled || method is not ("connect" or "disconnect")) return null;
        if (method == "connect")
        {
            if (_failOnce) { _failOnce = false; throw new InvalidOperationException("模拟连接失败，请重试"); }
            _pid = args[0].GetProperty("pid").GetInt32(); _state = "connected";
        }
        else _state = "disconnected";
        return State();
    }
}

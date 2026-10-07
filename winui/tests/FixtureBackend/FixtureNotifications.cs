// Test-only notification scenarios. No request reaches a real client or download service.
public sealed class FixtureNotifications
{
    private readonly HashSet<string> _scenarios;
    private readonly long _started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly int _duration;
    public bool WillAccept { get; private set; }
    public bool WillSearch { get; private set; }
    private string _updatePhase;
    public FixtureNotifications(string scenarios, int duration = 120)
    {
        _scenarios = scenarios.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _duration = Math.Clamp(duration, 5, 600);
        WillAccept = _scenarios.Contains("auto-accept"); WillSearch = _scenarios.Contains("auto-matchmaking");
        _updatePhase = _scenarios.Contains("update-failed") ? "download-failed" : _scenarios.Contains("update-ready") ? "waiting-for-restart" : _scenarios.Contains("update-progress") ? "downloading" : "";
    }
    public object Flow() => new { willAccept = WillAccept, willAcceptAt = _started + _duration * 1000, willSearchMatch = WillSearch, willSearchMatchAt = _started + _duration * 1000, willReconnectAt = _scenarios.Contains("reconnect") ? _started + _duration * 1000 : 0 };
    public object Respawn() { var left = Math.Max(0, _duration - (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _started) / 1000d); return new { info = new { isDead = _scenarios.Contains("respawn") && left > 0, timeLeft = left, totalTime = _duration } }; }
    public object Update() => new { updateProgressInfo = _updatePhase.Length == 0 ? null : (object)new { phase = _updatePhase, downloadingProgress = .65, averageDownloadSpeed = 2097152, downloadTimeLeft = 18 }, lastUpdateResult = new { reason = "模拟断网，仅用于原生错误提示验收" } };
    public object Remote() => new { latestRelease = new { isNew = _scenarios.Contains("new-release"), version = "fixture-0.0.2", currentVersion = "fixture-0.0.1", description = "# 模拟更新\nThis is a native notification fixture.\n\nNo network download occurs.", archiveFile = new { name = "fixture.zip" } }, announcement = _scenarios.Contains("announcement") ? (object)new { uniqueId = "native-fixture-announcement", frontMatter = new { summary = "原生公告测试 / Native announcement fixture", alertLevel = "medium" }, content = "# 测试公告\nNo real external service is contacted." } : new { } };
    public object? Snapshot(string ns) => ns switch { "auto-gameflow-main" => Flow(), "respawn-timer-main" => Respawn(), "self-update-main" => Update(), "remote-config-main" => Remote(), _ => null };
    public object? Action(string ns, string method)
    {
        if (ns == "auto-gameflow-main" && method == "cancelAutoAccept") { WillAccept = false; return Flow(); }
        if (ns == "auto-gameflow-main" && method == "cancelAutoMatchmaking") { WillSearch = false; return Flow(); }
        if (ns == "self-update-main" && method == "cancelUpdate") { _updatePhase = ""; return Update(); }
        if (ns == "self-update-main" && method == "startUpdate") { _updatePhase = "downloading"; return Update(); }
        return null;
    }
}

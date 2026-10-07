using System.Text.Json;

// Business fixtures are explicit synthetic inputs; no real client calls or settings writes.
internal sealed class FixtureAutomation
{
    private readonly JsonElement _groups = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "select-groups.json"))).RootElement.Clone();
    private bool _paused;
    public object State => new { groups = _groups, temporarilyDisabled = _paused };
    public object Recommendations => new Dictionary<string, object> { ["103"] = new { recommendedPositions = new[] { "MIDDLE" } }, ["147"] = new { recommendedPositions = new[] { "UTILITY", "BOTTOM" } }, ["17"] = new { recommendedPositions = new[] { "TOP" } }, ["64"] = new { recommendedPositions = new[] { "JUNGLE" } } };
    public object ExtraAssets => new { heroList = new { hero = new[] { new { heroId = "103", keywords = "ahri,ali,狐狸,阿狸,jiuweiyaohu" }, new { heroId = "147", keywords = "seraphine,salefenni,萨勒芬妮,星籁歌姬" }, new { heroId = "17", keywords = "teemo,timo,提莫" }, new { heroId = "64", keywords = "leesin,mangseng,盲僧" } } } };
    public object? Action(string ns, string method, JsonElement[] args)
    {
        if (ns != "auto-select-main" || method != "setTemporarilyDisabled") return null;
        _paused = args.FirstOrDefault().ValueKind == JsonValueKind.True; return State;
    }
}

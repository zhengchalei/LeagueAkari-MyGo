using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed class OngoingPlayerActions
{
    private readonly Func<string, string, object?[], Task<JsonElement>> _call;
    public OngoingPlayerActions(BackendClient backend) : this((ns, method, args) => backend.CallAsync(ns, method, args)) { }
    public OngoingPlayerActions(Func<string, string, object?[], Task<JsonElement>> call) => _call = call;
    public static object QueryParameters(string tag) => tag == "<akari:all>" ? new { } : (object)new { tag, tagsQueryType = "AND" };

    public async Task<string> LoadTagAsync(string puuid, string selfPuuid)
    {
        var tags = await _call("saved-player-main", "getPlayerTags", [new { puuid, selfPuuid }]);
        return tags.Items().FirstOrDefault(t => t.Boolean("markedBySelf")).Text("tag");
    }

    public async Task SaveTagAsync(string puuid, string selfPuuid, string text, JsonElement auth)
    {
        var dto = new Dictionary<string, object?> { ["puuid"] = puuid, ["selfPuuid"] = selfPuuid, ["tag"] = string.IsNullOrWhiteSpace(text) ? null : text };
        if (dto["tag"] != null) { dto["region"] = auth.Text("region"); dto["rsoPlatformId"] = auth.Text("rsoPlatformId"); }
        await _call("saved-player-main", "updatePlayerTag", [dto]);
        await _call("ongoing-game-main", "reloadPlayer", [puuid, new { includes = new[] { "savedInfo" } }]);
    }
}

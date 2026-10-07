using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record SourceDecision(string Type, string Source, string? Reason = null, string? FallbackReason = null);

public sealed class PlayerSourceUnavailableException(SourceDecision decision) : InvalidOperationException(
    decision.Type == "wait" ? "等待 SGP 登录凭据就绪" : "此大区没有可用的 SGP 接口")
{
    public SourceDecision Decision { get; } = decision;
}

public static class SourceAvailability
{
    public static SourceDecision Preview(JsonElement state, string server, string preferred) => preferred == "sgp" &&
        !state.Field("availability").Field("serversSupported").Boolean("matchHistory")
        ? new("load", "lcu", FallbackReason: "sgp-api-unavailable") : Resolve(state, server, server, preferred);

    // 原玩家页的 canUse 指服务器配置存在；具体接口的请求错误不能成为 LCU 回退条件。
    public static bool CanUse(JsonElement state, string server) => server.Length > 0 &&
        state.Field("leagueServers").Field("servers").Field(server).ValueKind == JsonValueKind.Object;

    public static SourceDecision RequiredSgp(JsonElement state, string server) => !CanUse(state, server)
        ? new("unavailable", "sgp", "sgp-api-unavailable")
        : !state.Boolean("isTokenReady") ? new("wait", "sgp", "sgp-token-not-ready") : new("load", "sgp");

    public static SourceDecision Resolve(JsonElement state, string server, string currentServer, string preferred)
    {
        if (server != currentServer) return RequiredSgp(state, server);
        if (preferred == "lcu") return new("load", "lcu");
        var sgp = RequiredSgp(state, server);
        return sgp.Type == "unavailable" ? new("load", "lcu", FallbackReason: "sgp-api-unavailable") : sgp;
    }
}

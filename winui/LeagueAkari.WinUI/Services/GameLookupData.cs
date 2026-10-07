using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record GameLookupResult(JsonElement Summary, string Source, string Server, string? FallbackReason);

public sealed class GameLookupData
{
    private readonly PlayerDataSource _source;
    private readonly Func<Task<string>> _preference;
    public GameLookupData(BackendClient backend) : this(new(backend), async () => (await backend.CallAsync("setting-factory-main", "get", "app-common-main", "preferredLolSource")).GetString() ?? "sgp") { }
    public GameLookupData(PlayerDataSource source, Func<Task<string>> preference) { _source = source; _preference = preference; }
    public static bool ValidGameId(double id) => double.IsFinite(id) && id > 0 && id == Math.Truncate(id) && id <= 9007199254740991;
    public async Task<GameLookupResult> LoadAsync(long gameId)
    {
        if (!ValidGameId(gameId)) throw new ArgumentOutOfRangeException(nameof(gameId));
        await _source.ConfigurePreviewAsync(await _preference());
        var summary = await _source.DetailsAsync(gameId);
        if (MatchData.Game(summary).ValueKind != JsonValueKind.Object || MatchData.Game(summary).Number("gameId") != gameId) throw new InvalidOperationException("对局详情未返回请求的对局");
        return new(summary.Clone(), _source.Source, _source.Server, _source.FallbackReason);
    }
}

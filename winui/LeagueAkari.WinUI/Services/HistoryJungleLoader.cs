using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record HistoryTimelineError(long GameId, string Message);
public sealed record HistoryJungleResult(JsonElement Analysis, int RequestedGames, int LoadedGames, IReadOnlyList<HistoryTimelineError> Errors);

/// <summary>History detail loading is separate from ongoing-game prefetch limits and identities.</summary>
public sealed class HistoryJungleLoader
{
    private readonly Dictionary<(string Server, string Source, long GameId), JsonElement> _cache = [];
    private readonly Queue<(string Server, string Source, long GameId)> _order = [];
    private readonly object _gate = new();
    private readonly Func<string, string, long, CancellationToken, Task<JsonElement>> _timeline;
    public HistoryJungleLoader(Func<string, string, long, CancellationToken, Task<JsonElement>> timeline) => _timeline = timeline;
    public async Task<HistoryJungleResult> LoadAsync(IEnumerable<JsonElement> games, string puuid, string server, string source, CancellationToken token = default)
    {
        var selected = games.Select(MatchData.Game).Where(g => HistoryJungleData.Eligible(g, puuid) && g.Number("gameId") > 0).DistinctBy(g => (long)g.Number("gameId")).ToArray();
        var loaded = new Dictionary<long, JsonElement>(); var errors = new List<HistoryTimelineError>(); using var concurrency = new SemaphoreSlim(5);
        await Task.WhenAll(selected.Select(async game =>
        {
            await concurrency.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested(); long id = (long)game.Number("gameId"); var key = (server, source, id); JsonElement timeline;
                lock (_gate) timeline = _cache.GetValueOrDefault(key);
                if (timeline.ValueKind != JsonValueKind.Object)
                {
                    try
                    {
                        timeline = await _timeline(server, source, id, token).WaitAsync(token);
                        token.ThrowIfCancellationRequested();
                        if (HistoryJungleData.Frames(timeline).Length == 0) throw new InvalidOperationException("时间线未包含 frames 数据");
                        lock (_gate)
                        {
                            if (!_cache.ContainsKey(key)) _order.Enqueue(key); _cache[key] = timeline;
                            while (_order.Count > 200) _cache.Remove(_order.Dequeue());
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { lock (errors) errors.Add(new(id, ex.Message)); return; }
                }
                lock (loaded) loaded[id] = timeline;
            }
            finally { concurrency.Release(); }
        }));
        token.ThrowIfCancellationRequested();
        return new(HistoryJungleData.Analyze(selected, puuid, loaded), selected.Length, loaded.Count, errors.OrderBy(e => e.GameId).ToArray());
    }
}

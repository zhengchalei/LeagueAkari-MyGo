using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record HistoryQuery(string Puuid, string Server, string PreferredSource, int Page, int Count, string Queue)
{
    public int StartIndex => Page * Count;
}
public sealed record HistoryBatch(JsonElement[] Games, int RawCount);
public enum HistoryCollectionStatus { Collecting, Completed, Stopped, Failed }
public sealed record HistoryLoadedPage<T>(HistoryQuery Query, T Data, JsonElement[] Games, int RawCount,
    HistoryCollectionStatus? Collection = null, int Scanned = 0, int Iterations = 0);
public sealed record HistoryCollectOptions(int BatchSize, int Target, int MaxIterations);

/// <summary>Only the active request may commit a page; committed query parameters survive a failed query.</summary>
public sealed class HistoryLoadController<T>
{
    private readonly Func<HistoryQuery, CancellationToken, Task<T>> _prepare;
    private readonly Func<T, HistoryQuery, int, int, CancellationToken, Task<HistoryBatch>> _read;
    private CancellationTokenSource? _request;
    private Task _running = Task.CompletedTask;
    private Func<Task>? _retry;
    private bool _active;
    private int _revision;
    public HistoryLoadedPage<T>? Page { get; private set; }
    public HistoryQuery? RequestedQuery { get; private set; }
    public Exception? Error { get; private set; }
    public bool Busy { get; private set; }
    public bool Collecting { get; private set; }
    public bool CanRetry => !Busy && _retry is not null;
    public bool CanPrevious => !Busy && Page is { Collection: null, Query.Page: > 0 };
    // Original pagination permits querying beyond a short or empty page.
    public bool CanNext => !Busy && Page is { Collection: null };
    public event Action? Changed;
    public HistoryLoadController(Func<HistoryQuery, CancellationToken, Task<T>> prepare,
        Func<T, HistoryQuery, int, int, CancellationToken, Task<HistoryBatch>> read) { _prepare = prepare; _read = read; }
    public void Activate() => _active = true;
    public void Deactivate() { _active = false; ++_revision; _request?.Cancel(); _request = null; StopPartialPage(); Busy = Collecting = false; }
    public Task WaitAsync() => _running;
    public void Cancel() => _request?.Cancel();
    public Task RetryAsync() => _active && CanRetry ? _retry!() : Task.CompletedTask;
    private bool Owns(int revision) => _active && revision == _revision;
    private (int Revision, CancellationTokenSource Stop) Begin(HistoryQuery query, bool collecting)
    {
        _request?.Cancel(); StopPartialPage(); var stop = _request = new CancellationTokenSource();
        RequestedQuery = query; Busy = true; Collecting = collecting; Error = null; _retry = null;
        int revision = ++_revision; Changed?.Invoke(); return (revision, stop);
    }
    private void StopPartialPage() { if (Page?.Collection == HistoryCollectionStatus.Collecting) Page = Page with { Collection = HistoryCollectionStatus.Stopped }; }
    public Task LoadAsync(HistoryQuery query, bool force = false)
    {
        if (!_active) return Task.CompletedTask;
        if (Busy && RequestedQuery == query && !Collecting && !force) return _running;
        var request = Begin(query, false);
        return _running = LoadCoreAsync(query, request.Revision, request.Stop);
    }
    private async Task LoadCoreAsync(HistoryQuery query, int revision, CancellationTokenSource stop)
    {
        try
        {
            var data = await _prepare(query, stop.Token).WaitAsync(stop.Token); stop.Token.ThrowIfCancellationRequested();
            var batch = await _read(data, query, query.StartIndex, query.Count, stop.Token).WaitAsync(stop.Token);
            stop.Token.ThrowIfCancellationRequested(); if (!Owns(revision)) return;
            Page = new(query, data, batch.Games, batch.RawCount); Changed?.Invoke();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { if (Owns(revision)) { Error = error; _retry = () => LoadAsync(query); } }
        finally { Finish(revision, stop); }
    }
    public Task CollectAsync(HistoryQuery query, HistoryCollectOptions options, Func<T, JsonElement, bool> predicate)
    {
        if (!_active || Busy) return Task.CompletedTask;
        var request = Begin(query, true);
        return _running = CollectCoreAsync(query, options, predicate, request.Revision, request.Stop);
    }
    private async Task CollectCoreAsync(HistoryQuery query, HistoryCollectOptions options, Func<T, JsonElement, bool> predicate, int revision, CancellationTokenSource stop)
    {
        try
        {
            var data = await _prepare(query, stop.Token).WaitAsync(stop.Token); stop.Token.ThrowIfCancellationRequested();
            if (!Owns(revision)) return;
            var games = new List<JsonElement>(); var seen = new HashSet<long>(); int scanned = 0;
            Page = new(query, data, [], 0, HistoryCollectionStatus.Collecting); Changed?.Invoke();
            for (int round = 0; round < options.MaxIterations && games.Count < options.Target; round++)
            {
                var batch = await _read(data, query, round * options.BatchSize, options.BatchSize, stop.Token).WaitAsync(stop.Token);
                stop.Token.ThrowIfCancellationRequested(); if (!Owns(revision)) return;
                if (batch.RawCount == 0) break;
                scanned += batch.RawCount;
                foreach (var game in batch.Games)
                {
                    long id = (long)game.Number("gameId");
                    if (id > 0 && !seen.Contains(id) && predicate(data, game)) { seen.Add(id); games.Add(game); }
                    if (games.Count >= options.Target) break;
                }
                Page = new(query, data, games.ToArray(), batch.RawCount, HistoryCollectionStatus.Collecting, scanned, round + 1); Changed?.Invoke();
            }
            if (Owns(revision)) Page = Page! with { Collection = HistoryCollectionStatus.Completed };
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            if (Owns(revision) && Page?.Collection == HistoryCollectionStatus.Collecting) Page = Page with { Collection = HistoryCollectionStatus.Stopped };
        }
        catch (Exception error)
        {
            if (Owns(revision))
            {
                Error = error; _retry = () => CollectAsync(query, options, predicate);
                if (Page?.Collection == HistoryCollectionStatus.Collecting) Page = Page with { Collection = HistoryCollectionStatus.Failed };
            }
        }
        finally { Finish(revision, stop); }
    }
    private void Finish(int revision, CancellationTokenSource stop)
    {
        if (Owns(revision)) { _request = null; Busy = Collecting = false; Changed?.Invoke(); }
        stop.Dispose();
    }
}

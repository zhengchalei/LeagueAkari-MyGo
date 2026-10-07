using System.Globalization;
using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record FriendToolGroup(int Id, string Name, int Priority, JsonElement[] Friends);
public sealed record FriendDeleteOutcome(int Requested, int Deleted, bool Cancelled);

public static class FriendToolsDate
{
    public static string Relative(DateTimeOffset date, DateTimeOffset now, bool english)
    {
        double seconds = (now - date).TotalSeconds; bool future = seconds < 0; seconds = Math.Abs(seconds);
        string zh, en;
        if (seconds < 45) { zh = "几秒"; en = "a few seconds"; }
        else if (seconds < 90) { zh = "1 分钟"; en = "a minute"; }
        else if (seconds < 2700) { zh = $"{Math.Round(seconds / 60)} 分钟"; en = $"{Math.Round(seconds / 60)} minutes"; }
        else if (seconds < 5400) { zh = "1 小时"; en = "an hour"; }
        else if (seconds < 79200) { zh = $"{Math.Round(seconds / 3600)} 小时"; en = $"{Math.Round(seconds / 3600)} hours"; }
        else if (seconds < 129600) { zh = "1 天"; en = "a day"; }
        else if (seconds < 2246400) { zh = $"{Math.Round(seconds / 86400)} 天"; en = $"{Math.Round(seconds / 86400)} days"; }
        else if (seconds < 3888000) { zh = "1 个月"; en = "a month"; }
        else if (seconds < 27648000) { zh = $"{Math.Round(seconds / 2592000)} 个月"; en = $"{Math.Round(seconds / 2592000)} months"; }
        else if (seconds < 47088000) { zh = "1 年"; en = "a year"; }
        else { zh = $"{Math.Round(seconds / 31536000)} 年"; en = $"{Math.Round(seconds / 31536000)} years"; }
        return english ? future ? "in " + en : en + " ago" : zh + (future ? "后" : "前");
    }
}

public sealed class FriendToolsController(Func<string, string, object?[], Task<JsonElement>> call)
{
    private bool _active, _cancelDelete, _refreshing;
    private int _generation, _revision;
    private string _identity = "";
    private JsonElement[] _groups = [];
    private readonly List<(string Id, string Type, JsonElement Friend)> _eventsDuringRefresh = [];
    public JsonElement[] Friends { get; private set; } = [];
    public Dictionary<string, DateTimeOffset> Since { get; } = [];
    public Dictionary<string, DateTimeOffset> LastGames { get; } = [];
    public HashSet<string> Selected { get; } = [];
    public bool Connected { get; private set; }
    public bool Busy { get; private set; }
    public bool Deleting { get; private set; }
    public bool CancelRequested => _cancelDelete;
    public Exception? Error { get; private set; }
    public FriendDeleteOutcome? DeleteOutcome { get; private set; }
    public Task Enrichment { get; private set; } = Task.CompletedTask;
    public event Action? Changed;
    public void Activate() => _active = true;
    public void Deactivate() { _active = false; ++_generation; ++_revision; Busy = Deleting = _refreshing = false; _eventsDuringRefresh.Clear(); _cancelDelete = true; }
    private static string Identity(JsonElement state) => state.Text("connectionState") != "connected" ? "" : state.Field("auth").ToString();
    public bool UpdateConnection(JsonElement state)
    {
        if (!_active) return false;
        string identity = Identity(state); bool connected = state.Text("connectionState") == "connected";
        bool changed = identity != _identity || connected != Connected;
        if (changed)
        {
            _identity = identity; Connected = connected; ++_generation; ++_revision;
            Busy = Deleting = _refreshing = false; _eventsDuringRefresh.Clear(); _cancelDelete = true; _groups = []; Friends = []; Selected.Clear(); Since.Clear(); LastGames.Clear(); Error = null; DeleteOutcome = null;
            Changed?.Invoke();
        }
        return changed;
    }
    private bool Current(int generation, int revision) => _active && Connected && generation == _generation && revision == _revision;
    private async Task<bool> CheckConnectionAsync(int generation, int revision)
    {
        var state = await call("winui-backend", "snapshot", ["league-client-main", "state"]);
        if (!Current(generation, revision)) return false;
        if (Identity(state) == _identity && state.Text("connectionState") == "connected") return true;
        UpdateConnection(state); return false;
    }
    public FriendToolGroup[] Groups(string query = "")
    {
        query = query.Trim();
        return _groups.Select(group => new FriendToolGroup((int)group.Number("id"), group.Text("name"), (int)group.Number("priority"),
            Friends.Where(friend => friend.Number("groupId") == group.Number("id") && (friend.Text("gameName") + "#" + friend.Text("gameTag")).Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(friend => Since.GetValueOrDefault(friend.Text("puuid"), DateTimeOffset.MaxValue)).ToArray()))
            .Where(group => group.Friends.Length > 0).OrderByDescending(group => group.Priority).ToArray();
    }
    public void Select(IEnumerable<string> ids)
    {
        string[] next = ids.ToArray(); Selected.Clear(); foreach (string id in next) if (Friends.Any(friend => friend.Text("id") == id)) Selected.Add(id); Changed?.Invoke();
    }
    public void CancelDelete() { if (Deleting) { _cancelDelete = true; Changed?.Invoke(); } }
    public void ApplyFriendEvent(string id, string type, JsonElement friend)
    {
        if (!_active || !Connected) return;
        if (type is not ("Delete" or "Update")) return;
        if (_refreshing) _eventsDuringRefresh.Add((id, type, friend.ValueKind == JsonValueKind.Undefined ? default : friend.Clone()));
        ApplyEvent(id, type, friend); Changed?.Invoke();
    }
    private void ApplyEvent(string id, string type, JsonElement friend)
    {
        if (type == "Delete") { Friends = Friends.Where(item => item.Text("id") != id).ToArray(); if (Deleting) Selected.Remove(id); else Selected.Clear(); }
        else if (type == "Update") Friends = Friends.Select(item => item.Text("id") == id ? friend.Clone() : item).ToArray();
    }
    public async Task<bool> RefreshAsync()
    {
        if (!_active || !Connected || Busy) return false;
        int generation = _generation, revision = ++_revision; Busy = _refreshing = true; _eventsDuringRefresh.Clear(); Error = null; DeleteOutcome = null; Changed?.Invoke();
        try
        {
            if (!await CheckConnectionAsync(generation, revision)) return false;
            var groups = await call("winui-backend", "lcuRequest", ["GET", "/lol-chat/v1/friend-groups", null]);
            if (!Current(generation, revision)) return false;
            var friends = await call("winui-backend", "lcuRequest", ["GET", "/lol-chat/v1/friends", null]);
            if (!Current(generation, revision) || !await CheckConnectionAsync(generation, revision)) return false;
            _groups = groups.Items().Select(group => group.Clone()).ToArray(); Friends = friends.Items().Where(friend => friend.Text("id").Length > 0).DistinctBy(friend => friend.Text("id")).Select(friend => friend.Clone()).ToArray();
            foreach (var update in _eventsDuringRefresh) ApplyEvent(update.Id, update.Type, update.Friend);
            Selected.Clear(); Changed?.Invoke();
            Enrichment = Task.WhenAll(LoadRecentGamesAsync(Friends, generation, revision), LoadSinceAsync(generation, revision));
            return true;
        }
        catch (Exception error) { if (Current(generation, revision)) Error = error; return false; }
        finally { if (Current(generation, revision)) { Busy = _refreshing = false; _eventsDuringRefresh.Clear(); Changed?.Invoke(); } }
    }
    private async Task LoadRecentGamesAsync(JsonElement[] friends, int generation, int revision)
    {
        foreach (var friend in friends)
        {
            if (!Current(generation, revision)) return;
            try
            {
                var sgp = await call("winui-backend", "snapshot", ["sgp-main", "state"]);
                if (!Current(generation, revision)) return;
                string puuid = friend.Text("puuid"); JsonElement game;
                if (sgp.Boolean("isTokenReady") && sgp.Field("availability").Field("serversSupported").Boolean("matchHistory"))
                {
                    var history = await call("winui-backend", "sgpRequest", ["", "entitlements", "GET", "/match-history-query/v1/products/lol/player/" + Uri.EscapeDataString(puuid) + "/SUMMARY?startIndex=0&count=1", null]);
                    game = history.Field("games").Items().FirstOrDefault().Field("json");
                }
                else
                {
                    var history = await call("winui-backend", "lcuRequest", ["GET", "/lol-match-history/v1/products/lol/" + Uri.EscapeDataString(puuid) + "/matches?begIndex=0&endIndex=0", null]);
                    game = history.Field("games").Field("games").Items().FirstOrDefault();
                }
                if (!Current(generation, revision)) return;
                if (Friends.Any(item => item.Text("id") == friend.Text("id") && item.Text("puuid") == puuid) && game.Number("gameCreation") > 0) LastGames[puuid] = DateTimeOffset.FromUnixTimeMilliseconds((long)game.Number("gameCreation"));
                Changed?.Invoke();
            }
            catch { /* One unavailable player's history must not stop the remaining friends. */ }
        }
    }
    private async Task LoadSinceAsync(int generation, int revision)
    {
        try
        {
            var giftable = await call("winui-backend", "lcuRequest", ["GET", "/lol-store/v1/giftablefriends", null]);
            if (!Current(generation, revision)) return;
            foreach (var item in giftable.Items())
            {
                var friend = Friends.FirstOrDefault(friend => friend.Number("summonerId") == item.Number("summonerId"));
                if (friend.Text("puuid").Length > 0 && DateTimeOffset.TryParse(item.Text("friendsSince"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) Since[friend.Text("puuid")] = date;
            }
            Changed?.Invoke();
        }
        catch { /* Gift metadata is optional; the main friend list remains usable. */ }
    }
    public async Task<bool> DeleteAsync(Func<int, Task<bool>> confirm)
    {
        if (!_active || !Connected || Busy || Selected.Count == 0) return false;
        string[] ids = Friends.Select(friend => friend.Text("id")).Where(Selected.Contains).ToArray();
        int generation = _generation, revision = _revision, deleted = 0; Busy = true; Error = null; DeleteOutcome = null; _cancelDelete = false; Changed?.Invoke();
        try
        {
            if (!await confirm(ids.Length) || !Current(generation, revision) || !await CheckConnectionAsync(generation, revision)) return false;
            Deleting = true; Changed?.Invoke();
            foreach (string id in ids)
            {
                if (_cancelDelete || !Current(generation, revision)) break;
                if (!Friends.Any(friend => friend.Text("id") == id)) continue;
                if (!await CheckConnectionAsync(generation, revision) || _cancelDelete) break;
                await call("winui-backend", "lcuRequest", ["DELETE", "/lol-chat/v1/friends/" + Uri.EscapeDataString(id), null]);
                if (!Current(generation, revision)) return false;
                ++deleted; Friends = Friends.Where(friend => friend.Text("id") != id).ToArray(); Selected.Remove(id); Changed?.Invoke();
            }
            if (Current(generation, revision)) DeleteOutcome = new(ids.Length, deleted, _cancelDelete);
            return Current(generation, revision);
        }
        catch (Exception error) { if (Current(generation, revision)) { Error = error; DeleteOutcome = new(ids.Length, deleted, _cancelDelete); } return false; }
        finally { if (Current(generation, revision)) { Busy = Deleting = false; Changed?.Invoke(); } }
    }
}

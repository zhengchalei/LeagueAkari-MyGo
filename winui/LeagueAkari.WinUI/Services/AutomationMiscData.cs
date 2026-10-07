using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public static class AutomationMiscData
{
    public static JsonElement[] Friends(JsonElement friends, string search)
    {
        int Priority(string availability) => availability switch { "dnd" => 3, "away" => 2, "chat" => 1, _ => 0 };
        string query = search.Trim();
        return friends.Items().Where(friend => (friend.Text("gameName") + "#" + friend.Text("gameTag")).Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(friend => Priority(friend.Text("availability"))).ToArray();
    }
}

public sealed class AutomationInvitationsController
{
    private readonly Func<string, string, object?[], Task<JsonElement>> _call;
    private bool _active;
    private int _revision, _refresh, _planRevision;
    private string _identity = "";
    private JsonElement _client, _lobby;
    public JsonElement Friends { get; private set; }
    public HashSet<string> Scheduled { get; private set; } = [];
    public bool Connected { get; private set; }
    public bool InLobby { get; private set; }
    public bool Busy { get; private set; }
    public Exception? Error { get; private set; }
    public event Action? Changed;
    public AutomationInvitationsController(Func<string, string, object?[], Task<JsonElement>> call) => _call = call;
    public void Activate() { _active = true; ++_revision; }
    public void Deactivate() { _active = false; ++_revision; ++_refresh; Busy = false; }
    public bool CanSchedule(string puuid) => _active && Connected && InLobby && !Busy && Friends.Items().Any(f => f.Text("puuid") == puuid);
    private Task<JsonElement> State(string ns, string part = "state") => _call("winui-backend", "snapshot", [ns, part]);
    private static string Identity(JsonElement client) => client.Field("auth").Text("pid") + ":" + client.Field("auth").Text("port");
    public void ConnectionChanged(JsonElement client)
    {
        string identity = Identity(client); bool connected = client.Text("connectionState") == "connected";
        if (identity != _identity || connected != Connected) { ++_revision; ++_refresh; Busy = false; Friends = default; Scheduled = []; }
        _client = client; _identity = identity; Connected = connected; if (!connected) InLobby = false; Changed?.Invoke();
    }
    public void LobbyChanged(JsonElement lobby) { _lobby = lobby; InLobby = lobby.Field("lobby").ValueKind == JsonValueKind.Object; if (!InLobby) { ++_revision; ++_refresh; Busy = false; } Changed?.Invoke(); }
    public bool ApplyEvent(JsonElement update)
    {
        if (!_active) return false;
        string name = update.Text("name"); var args = update.Field("args").Items().ToArray();
        if (name == "update-state-prop/auto-gameflow-main:state" && args.Length >= 2 && args[0].ValueKind == JsonValueKind.String && args[0].GetString() == "friendsToBeInvited")
        { ++_planRevision; Scheduled = args[1].Items().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).ToHashSet(); Changed?.Invoke(); return false; }
        if (name is "update-state-prop/league-client-main:state" or "update-state-prop/league-client-main:lobby" && args.Length >= 2 && args[0].ValueKind == JsonValueKind.String)
        {
            bool client = name.EndsWith(":state"); var value = client ? _client : _lobby;
            var fields = value.ValueKind == JsonValueKind.Object ? value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) : [];
            fields[args[0].GetString()!] = args[1].Clone();
            if (client) ConnectionChanged(JsonSerializer.SerializeToElement(fields)); else LobbyChanged(JsonSerializer.SerializeToElement(fields));
            return true;
        }
        return name == "update-state-prop/league-client-main:chat" || name.Contains("lol-chat/v1/friends", StringComparison.Ordinal);
    }
    public async Task RefreshAsync()
    {
        if (!_active) return; int revision = _revision, refresh = ++_refresh, planRevision = _planRevision;
        try
        {
            var client = await State("league-client-main"); var lobby = await State("league-client-main", "lobby");
            var flow = await State("auto-gameflow-main");
            JsonElement friends = default;
            if (client.Text("connectionState") == "connected" && lobby.Field("lobby").ValueKind == JsonValueKind.Object)
                friends = (await _call("league-client-main", "http-request", [new { method = "GET", url = "/lol-chat/v1/friends" }])).Field("data");
            if (!_active || revision != _revision || refresh != _refresh) return;
            ConnectionChanged(client); _lobby = lobby; InLobby = lobby.Field("lobby").ValueKind == JsonValueKind.Object;
            Friends = friends; if (planRevision == _planRevision) Scheduled = flow.Field("friendsToBeInvited").Items().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).ToHashSet(); Error = null; Changed?.Invoke();
        }
        catch (Exception error) { if (_active && revision == _revision && refresh == _refresh) { Error = error; Changed?.Invoke(); } }
    }
    public async Task ToggleAsync(string puuid)
    {
        if (!CanSchedule(puuid)) return; Busy = true; Error = null; int revision = _revision; string identity = _identity; Changed?.Invoke();
        try
        {
            var client = await State("league-client-main"); var lobby = await State("league-client-main", "lobby"); var flow = await State("auto-gameflow-main");
            if (!_active || revision != _revision) return;
            if (client.Text("connectionState") != "connected" || Identity(client) != identity || lobby.Field("lobby").ValueKind != JsonValueKind.Object)
            { ConnectionChanged(client); LobbyChanged(lobby); return; }
            var scheduled = flow.Field("friendsToBeInvited").Items().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).ToHashSet();
            if (!scheduled.Remove(puuid)) scheduled.Add(puuid);
            int planRevision = _planRevision; await _call("auto-gameflow-main", "setFriendsToBeInvited", [scheduled.ToArray()]);
            if (_active && revision == _revision && planRevision == _planRevision) Scheduled = scheduled;
        }
        catch (Exception error) { if (_active && revision == _revision) Error = error; }
        finally { if (_active && revision == _revision) { Busy = false; Changed?.Invoke(); } }
    }
}

public sealed class AutoReplyDraft
{
    private readonly Func<string, Task> _save;
    private bool _active;
    private int _revision, _settingsRevision;
    public string Text { get; set; } = "";
    public string Saved { get; private set; } = "";
    public bool Busy { get; private set; }
    public bool Dirty => Text != Saved;
    public Exception? Error { get; private set; }
    public event Action? Changed;
    public AutoReplyDraft(Func<string, Task> save) => _save = save;
    public void Activate(string text) { _active = true; ++_revision; Update(text); }
    public void Deactivate() { _active = false; ++_revision; Busy = false; }
    public void Update(string text) { ++_settingsRevision; Saved = Text = text; Changed?.Invoke(); }
    public async Task SaveAsync()
    {
        if (!_active || Busy || !Dirty) return;
        int revision = _revision, settingsRevision = _settingsRevision; string text = Text; Busy = true; Error = null; Changed?.Invoke();
        try { await _save(text); if (_active && revision == _revision && settingsRevision == _settingsRevision) Saved = text; }
        catch (Exception error) { if (_active && revision == _revision) Error = error; }
        finally { if (_active && revision == _revision) { Busy = false; Changed?.Invoke(); } }
    }
}

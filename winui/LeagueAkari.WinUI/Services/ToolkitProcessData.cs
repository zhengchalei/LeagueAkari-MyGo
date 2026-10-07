using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public enum ToolkitProcessOperation
{
    PlayAgain, LeaveLobby, StartMatchmaking, Accept, Decline, CancelMatchmaking,
    ExitChampSelect, StartDodgeLoop, CancelDodgeLoop, Reconnect, TerminateGame
}

public sealed record ToolkitProcessSnapshot(JsonElement Client, JsonElement Gameflow, JsonElement Lobby,
    JsonElement ChampSelect, JsonElement Matchmaking, JsonElement Controls)
{
    public bool Connected => Client.Text("connectionState") == "connected";
    public string Phase => Gameflow.Text("phase");
    public bool DodgeLooping => Controls.Boolean("DodgeLooping");
    public int DodgeIterations => (int)Controls.Number("DodgeIterations");
    public string ConnectionIdentity => Client.Field("auth").Text("pid") + ":" + Client.Field("auth").Text("port");
    public bool CanExecute(ToolkitProcessOperation operation)
    {
        if (operation == ToolkitProcessOperation.CancelDodgeLoop) return DodgeLooping;
        if (!Connected || DodgeLooping) return false;
        var lobby = Lobby.Field("lobby");
        var session = ChampSelect.Field("session");
        string response = Matchmaking.Field("readyCheck").Text("playerResponse");
        return operation switch
        {
            ToolkitProcessOperation.PlayAgain => Phase is "WaitingForStats" or "PreEndOfGame" or "EndOfGame",
            ToolkitProcessOperation.LeaveLobby => Phase == "Lobby",
            ToolkitProcessOperation.StartMatchmaking => Phase == "Lobby" && lobby.Boolean("canStartActivity")
                && lobby.Field("localMember").Boolean("isLeader") && lobby.Field("localMember").Boolean("allowedStartActivity")
                && !lobby.Field("gameConfig").Boolean("isCustom"),
            // 原版允许已经接受的玩家拒绝，以及已经拒绝的玩家重新接受。
            ToolkitProcessOperation.Accept => Phase == "ReadyCheck" && response != "Accepted",
            ToolkitProcessOperation.Decline => Phase == "ReadyCheck" && response != "Declined",
            ToolkitProcessOperation.CancelMatchmaking => Phase == "Matchmaking",
            ToolkitProcessOperation.ExitChampSelect or ToolkitProcessOperation.StartDodgeLoop => Phase == "ChampSelect"
                && session.ValueKind == JsonValueKind.Object && !session.Boolean("isSpectating")
                && !session.Boolean("isCustomGame") && !Gameflow.Field("session").Field("gameData").Boolean("isCustomGame"),
            ToolkitProcessOperation.Reconnect => Phase == "Reconnect",
            ToolkitProcessOperation.TerminateGame => Phase is "GameStart" or "InProgress" or "Reconnect" or "WatchInProgress",
            _ => false
        };
    }

    public string ConfirmationScope(ToolkitProcessOperation operation) => operation switch
    {
        ToolkitProcessOperation.ExitChampSelect => ConnectionIdentity + ":" + Phase + ":" + ChampSelect.Field("session").Text("id") + ":" + ChampSelect.Field("session").Text("gameId"),
        ToolkitProcessOperation.TerminateGame => ConnectionIdentity + ":" + Phase + ":" + Gameflow.Field("session").Field("gameData").Text("gameId"),
        _ => ConnectionIdentity
    };
}

public static class ToolkitProcessData
{
    public static readonly string[] StateParts = ["state", "gameflow", "lobby", "champSelect", "matchmaking"];
    public static bool RequiresConfirmation(ToolkitProcessOperation operation) => operation is ToolkitProcessOperation.ExitChampSelect or ToolkitProcessOperation.TerminateGame;
    public static bool IsStateEvent(JsonElement envelope) => envelope.Text("namespace") == "mobx-utils-main"
        && StateParts.Any(part => envelope.Text("name") == "update-state-prop/league-client-main:" + part);

    public static (string Namespace, string Method, object?[] Arguments) Command(ToolkitProcessOperation operation)
    {
        (string, string, object?[]) Lcu(string method, string path, object? body = null) => ("winui-backend", "lcuRequest", [method, path, body]);
        (string, string, object?[]) Mini(string kind) => ("winui-backend", "miniAction", [new { kind }]);
        return operation switch
        {
            ToolkitProcessOperation.PlayAgain => Lcu("POST", "/lol-lobby/v2/play-again"),
            ToolkitProcessOperation.LeaveLobby => Lcu("DELETE", "/lol-lobby/v2/lobby"),
            ToolkitProcessOperation.StartMatchmaking => Lcu("POST", "/lol-lobby/v2/lobby/matchmaking/search"),
            ToolkitProcessOperation.Accept => Lcu("POST", "/lol-matchmaking/v1/ready-check/accept"),
            ToolkitProcessOperation.Decline => Lcu("POST", "/lol-matchmaking/v1/ready-check/decline"),
            // Mini 既有实现会在取消队列成功后关闭自动匹配，避免再次自动排队。
            ToolkitProcessOperation.CancelMatchmaking => Mini("cancel-queue"),
            ToolkitProcessOperation.ExitChampSelect => Lcu("POST", "/lol-login/v1/session/invoke?destination=lcdsServiceProxy&method=call&args="
                + Uri.EscapeDataString("[\"\", \"teambuilder-draft\", \"quitV2\", \"\"]"), new { data = new[] { "", "teambuilder-draft", "quitV2", "" } }),
            ToolkitProcessOperation.StartDodgeLoop => Mini("start-dodge-loop"),
            ToolkitProcessOperation.CancelDodgeLoop => Mini("cancel-dodge-loop"),
            ToolkitProcessOperation.Reconnect => Lcu("POST", "/lol-gameflow/v1/reconnect"),
            ToolkitProcessOperation.TerminateGame => ("game-client-main", "terminateGameClient", []),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }
}

public sealed class ToolkitProcessController
{
    private readonly Func<string, string, object?[], Task<JsonElement>> _call;
    private readonly Dictionary<string, JsonElement> _states = [];
    private JsonElement _controls;
    private int _stateRevision, _refreshSequence, _controlSequence;
    private bool _active;
    public bool Busy { get; private set; }
    public Exception? Error { get; private set; }
    public Exception? RefreshError { get; private set; }
    public ToolkitProcessOperation? Completed { get; private set; }
    public event Action? Changed;
    public ToolkitProcessSnapshot Snapshot => new(_states.GetValueOrDefault("state"), _states.GetValueOrDefault("gameflow"),
        _states.GetValueOrDefault("lobby"), _states.GetValueOrDefault("champSelect"), _states.GetValueOrDefault("matchmaking"), _controls);

    public ToolkitProcessController(Func<string, string, object?[], Task<JsonElement>> call) => _call = call;
    public void Activate() { _active = true; _stateRevision++; }
    public void Deactivate() { _active = false; _stateRevision++; _controlSequence++; }
    public bool CanExecute(ToolkitProcessOperation operation) => _active && !Busy && Snapshot.CanExecute(operation);

    public async Task<bool> RefreshAsync()
    {
        int sequence = ++_refreshSequence, revision = _stateRevision;
        try
        {
            var snapshots = await Task.WhenAll(ToolkitProcessData.StateParts.Select(part => _call("winui-backend", "snapshot", ["league-client-main", part])));
            if (!_active || revision != _stateRevision || sequence != _refreshSequence) return false;
            for (int i = 0; i < ToolkitProcessData.StateParts.Length; i++) _states[ToolkitProcessData.StateParts[i]] = snapshots[i];
            RefreshError = null; Changed?.Invoke(); return true;
        }
        catch (Exception error) { if (_active && sequence == _refreshSequence) { RefreshError = error; Changed?.Invoke(); } throw; }
    }

    public async Task RefreshControlsAsync()
    {
        int sequence = ++_controlSequence;
        try
        {
            var snapshot = await _call("winui-backend", "miniSnapshot", []);
            if (!_active || sequence != _controlSequence) return;
            _controls = snapshot.Field("controls"); Changed?.Invoke();
        }
        catch (Exception error) { if (_active && sequence == _controlSequence) { RefreshError = error; Changed?.Invoke(); } }
    }

    public void ApplyStateEvent(JsonElement envelope)
    {
        if (!_active || !ToolkitProcessData.IsStateEvent(envelope)) return;
        var args = envelope.Field("args").Items().ToArray();
        if (args.Length < 2 || args[0].ValueKind != JsonValueKind.String) return;
        string part = envelope.Text("name")[("update-state-prop/league-client-main:").Length..];
        var properties = _states.GetValueOrDefault(part).ValueKind == JsonValueKind.Object
            ? _states[part].EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) : new Dictionary<string, JsonElement>();
        properties[args[0].GetString()!] = args[1].Clone();
        _states[part] = JsonSerializer.SerializeToElement(properties); _stateRevision++;
        if (!Snapshot.Connected) _controls = default;
        Changed?.Invoke();
    }

    public async Task<bool> ExecuteAsync(ToolkitProcessOperation operation, Func<Task<bool>>? confirm = null)
    {
        if (!_active || Busy) return false;
        Busy = true; Error = null; Completed = null; Changed?.Invoke();
        try
        {
            if (!await RefreshAsync() || !Snapshot.CanExecute(operation)) throw new InvalidOperationException("process-action-unavailable");
            string identity = Snapshot.ConnectionIdentity;
            if (ToolkitProcessData.RequiresConfirmation(operation))
            {
                string scope = Snapshot.ConfirmationScope(operation);
                if (confirm is null) throw new InvalidOperationException("process-confirmation-required");
                if (!await confirm()) return false;
                if (!await RefreshAsync() || !Snapshot.CanExecute(operation) || Snapshot.ConfirmationScope(operation) != scope)
                    throw new InvalidOperationException("process-action-unavailable");
            }
            if (!_active || Snapshot.ConnectionIdentity != identity) throw new InvalidOperationException("process-action-unavailable");
            var command = ToolkitProcessData.Command(operation);
            await _call(command.Namespace, command.Method, command.Arguments);
            Completed = operation;
            try { await RefreshAsync(); await RefreshControlsAsync(); } catch { }
            return true;
        }
        catch (Exception error) { Error = error; return false; }
        finally { Busy = false; if (_active) Changed?.Invoke(); }
    }
}

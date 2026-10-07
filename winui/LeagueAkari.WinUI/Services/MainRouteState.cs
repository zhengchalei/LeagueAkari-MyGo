using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public readonly record struct MainRouteTransition(bool EnteredMatch, bool LeftMatch, bool GameEnded);

// Mirrors auto-route-watcher: a live non-spectating selection session can precede
// the gameflow phase. Phase changes inside one match must preserve manual navigation.
public sealed class MainRouteState
{
    private string _connection = "", _phase = "";
    private JsonElement _session;
    private bool _inMatch, _atGameEnd;
    public bool InMatch => _inMatch;

    public MainRouteTransition Initialize(JsonElement client, JsonElement gameflow, JsonElement champSelect)
    {
        _connection = client.Text("connectionState"); _phase = gameflow.Text("phase"); _session = champSelect.Field("session");
        return Evaluate();
    }

    public MainRouteTransition? Apply(JsonElement update)
    {
        if (update.Text("namespace") != "mobx-utils-main") return null;
        var args = update.Field("args").Items().ToArray();
        if (args.Length < 2 || args[0].ValueKind != JsonValueKind.String) return null;
        string name = update.Text("name"), key = args[0].GetString()!;
        if (name == "update-state-prop/league-client-main:state" && key == "connectionState") _connection = args[1].ValueKind == JsonValueKind.String ? args[1].GetString()! : "";
        else if (name == "update-state-prop/league-client-main:gameflow" && key == "phase") _phase = args[1].ValueKind == JsonValueKind.String ? args[1].GetString()! : "";
        else if (name == "update-state-prop/league-client-main:champSelect" && key == "session") _session = args[1].Clone();
        else return null;
        return Evaluate();
    }

    private MainRouteTransition Evaluate()
    {
        bool next = _connection == "connected" && (_session.ValueKind == JsonValueKind.Object && !_session.Boolean("isSpectating") || _phase is "ChampSelect" or "GameStart" or "InProgress" or "Reconnect" or "WaitingForStats" or "PreEndOfGame");
        bool atEnd = _phase is "EndOfGame" or "PreEndOfGame";
        var transition = new MainRouteTransition(next && !_inMatch, !next && _inMatch, atEnd && !_atGameEnd);
        _inMatch = next; _atGameEnd = atEnd;
        return transition;
    }
}

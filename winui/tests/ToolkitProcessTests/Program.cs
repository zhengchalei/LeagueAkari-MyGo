using System.Text.Json;
using LeagueAkari.WinUI.Services;

int passed = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);
var states = new Dictionary<string, JsonElement>();
void SetPhase(string phase, bool connected = true, string response = "None", bool leader = true, bool canStart = true, bool custom = false, bool spectating = false, int pid = 10, string selectionId = "selection-one")
{
    states["state"] = J(new { connectionState = connected ? "connected" : "disconnected", auth = new { pid, port = 57830 } });
    states["gameflow"] = J(new { phase, session = new { gameData = new { isCustomGame = custom, gameId = 42 } } });
    states["lobby"] = J(new { lobby = new { canStartActivity = canStart, localMember = new { isLeader = leader, allowedStartActivity = canStart }, gameConfig = new { isCustom = custom } } });
    states["champSelect"] = J(new { session = new { id = selectionId, gameId = 42, isSpectating = spectating, isCustomGame = custom } });
    states["matchmaking"] = J(new { readyCheck = new { playerResponse = response } });
}
ToolkitProcessSnapshot Current(bool loop = false) => new(states["state"], states["gameflow"], states["lobby"], states["champSelect"], states["matchmaking"], J(new { DodgeLooping = loop, DodgeIterations = 21 }));
SetPhase("Lobby");
Check(Current().CanExecute(ToolkitProcessOperation.StartMatchmaking) && Current().CanExecute(ToolkitProcessOperation.LeaveLobby), "Lobby leader can matchmake and leave");
SetPhase("Lobby", leader: false); Check(!Current().CanExecute(ToolkitProcessOperation.StartMatchmaking) && Current().CanExecute(ToolkitProcessOperation.LeaveLobby), "Non-leader cannot start matchmaking but can leave");
SetPhase("Lobby", canStart: false); Check(!Current().CanExecute(ToolkitProcessOperation.StartMatchmaking), "Unavailable lobby activity disables starting");
SetPhase("Lobby", custom: true); Check(!Current().CanExecute(ToolkitProcessOperation.StartMatchmaking), "Custom lobby is not sent to matchmaking search");
foreach (string phase in new[] { "WaitingForStats", "PreEndOfGame", "EndOfGame" }) { SetPhase(phase); Check(Current().CanExecute(ToolkitProcessOperation.PlayAgain), "Original postgame return phase " + phase); }
SetPhase("InProgress"); Check(!Current().CanExecute(ToolkitProcessOperation.PlayAgain) && !Current().CanExecute(ToolkitProcessOperation.LeaveLobby), "In-progress disables postgame return and leave lobby");
SetPhase("ReadyCheck"); Check(Current().CanExecute(ToolkitProcessOperation.Accept) && Current().CanExecute(ToolkitProcessOperation.Decline), "Pending ready check permits either response");
SetPhase("ReadyCheck", response: "Accepted"); Check(!Current().CanExecute(ToolkitProcessOperation.Accept) && Current().CanExecute(ToolkitProcessOperation.Decline), "Original accepted player may decline");
SetPhase("ReadyCheck", response: "Declined"); Check(Current().CanExecute(ToolkitProcessOperation.Accept) && !Current().CanExecute(ToolkitProcessOperation.Decline), "Original declined player may accept again");
SetPhase("Matchmaking"); Check(Current().CanExecute(ToolkitProcessOperation.CancelMatchmaking) && !Current().CanExecute(ToolkitProcessOperation.Accept), "Searching only allows cancel, not ready-check response");
SetPhase("ChampSelect"); Check(Current().CanExecute(ToolkitProcessOperation.ExitChampSelect) && Current().CanExecute(ToolkitProcessOperation.StartDodgeLoop), "Normal selection permits one-shot and original dodge loop");
SetPhase("ChampSelect", custom: true); Check(!Current().CanExecute(ToolkitProcessOperation.ExitChampSelect) && !Current().CanExecute(ToolkitProcessOperation.StartDodgeLoop), "Custom selection cannot use draft quitV2");
SetPhase("ChampSelect", spectating: true); Check(!Current().CanExecute(ToolkitProcessOperation.ExitChampSelect), "Spectator selection cannot dodge");
SetPhase("Reconnect"); Check(Current().CanExecute(ToolkitProcessOperation.Reconnect), "Reconnect action requires reconnect phase");
SetPhase("GameStart"); Check(Current().CanExecute(ToolkitProcessOperation.TerminateGame) && !Current().CanExecute(ToolkitProcessOperation.Reconnect), "Loading phase can terminate but cannot reconnect");
SetPhase("Lobby", connected: false); Check(Enum.GetValues<ToolkitProcessOperation>().All(op => !Current().CanExecute(op)), "Disconnected state disables all idle actions");
SetPhase("ChampSelect"); Check(Current(true).DodgeIterations == 21 && Current(true).CanExecute(ToolkitProcessOperation.CancelDodgeLoop) && !Current(true).CanExecute(ToolkitProcessOperation.ExitChampSelect), "Loop retains count, prevents conflicting action, permits cancel");

var commands = new List<(string Namespace, string Method, object?[] Args)>();
bool throwCommand = false, loopActive = false, throwPostWriteRefresh = false;
TaskCompletionSource<JsonElement>? pendingCommand = null, pendingSnapshot = null;
async Task<JsonElement> Call(string ns, string method, object?[] args)
{
    if (method == "snapshot")
    {
        var value = states[(string)args[1]!];
        if (pendingSnapshot is not null && (string)args[1]! == "gameflow") return await pendingSnapshot.Task;
        if (throwPostWriteRefresh && commands.Count > 0) throw new IOException("snapshot failed after write");
        return value;
    }
    if (method == "miniSnapshot") return J(new { controls = new { DodgeLooping = loopActive, DodgeIterations = 21 } });
    commands.Add((ns, method, args));
    if (throwCommand) throw new IOException("fixture request failed");
    if (pendingCommand is not null) return await pendingCommand.Task;
    return J(null);
}
var controller = new ToolkitProcessController(Call); controller.Activate();
SetPhase("Lobby"); await controller.RefreshAsync();
Check(await controller.ExecuteAsync(ToolkitProcessOperation.StartMatchmaking) && commands.Single().Method == "lcuRequest" && (string)commands[0].Args[0]! == "POST" && (string)commands[0].Args[1]! == "/lol-lobby/v2/lobby/matchmaking/search", "Manual start sends original POST search endpoint");
commands.Clear(); SetPhase("Matchmaking"); await controller.ExecuteAsync(ToolkitProcessOperation.CancelMatchmaking);
Check(commands.Count == 1 && commands[0].Method == "miniAction" && J(commands[0].Args[0]).Text("kind") == "cancel-queue", "Cancel reuses backend path that also disables automatic matchmaking");
commands.Clear(); SetPhase("EndOfGame"); await controller.ExecuteAsync(ToolkitProcessOperation.PlayAgain);
Check(commands.Count == 1 && (string)commands[0].Args[1]! == "/lol-lobby/v2/play-again", "Postgame uses original play-again endpoint");
commands.Clear(); SetPhase("ReadyCheck"); await controller.ExecuteAsync(ToolkitProcessOperation.Accept);
Check(commands.Count == 1 && (string)commands[0].Args[1]! == "/lol-matchmaking/v1/ready-check/accept", "Accept uses original ready-check endpoint");
commands.Clear(); SetPhase("ReadyCheck", response: "Accepted"); await controller.ExecuteAsync(ToolkitProcessOperation.Decline);
Check(commands.Count == 1 && (string)commands[0].Args[1]! == "/lol-matchmaking/v1/ready-check/decline", "Decline after accept uses original inverse response endpoint");
commands.Clear(); SetPhase("ChampSelect");
Check(!await controller.ExecuteAsync(ToolkitProcessOperation.ExitChampSelect) && commands.Count == 0, "One-shot dodge cannot bypass confirmation");
Check(!await controller.ExecuteAsync(ToolkitProcessOperation.ExitChampSelect, () => Task.FromResult(false)) && commands.Count == 0, "Canceled confirmation sends no write");
await controller.ExecuteAsync(ToolkitProcessOperation.ExitChampSelect, () => Task.FromResult(true));
var dodge = commands.Single();
Check(dodge.Namespace == "winui-backend" && dodge.Method == "lcuRequest" && Uri.UnescapeDataString((string)dodge.Args[1]!).Contains("args=[\"\", \"teambuilder-draft\", \"quitV2\", \"\"]") && J(dodge.Args[2]).Field("data").Items().Select(v => v.GetString()).SequenceEqual(["", "teambuilder-draft", "quitV2", ""]), "Confirmed one-shot uses original invoke query and exact quitV2 body");
commands.Clear(); SetPhase("ChampSelect");
Check(!await controller.ExecuteAsync(ToolkitProcessOperation.ExitChampSelect, () => { SetPhase("InProgress"); return Task.FromResult(true); }) && commands.Count == 0, "Selection ending while confirmation is open prevents late dodge");
SetPhase("ChampSelect"); Check(!await controller.ExecuteAsync(ToolkitProcessOperation.ExitChampSelect, () => { SetPhase("ChampSelect", selectionId: "new-selection"); return Task.FromResult(true); }) && commands.Count == 0, "A new selection identity cannot inherit old confirmation");
SetPhase("InProgress"); Check(!await controller.ExecuteAsync(ToolkitProcessOperation.TerminateGame, () => { SetPhase("InProgress", pid: 11); return Task.FromResult(true); }) && commands.Count == 0, "Switching client during confirmation prevents target change");

SetPhase("ReadyCheck"); pendingCommand = new(TaskCreationOptions.RunContinuationsAsynchronously);
var first = controller.ExecuteAsync(ToolkitProcessOperation.Accept);
Check(controller.Busy && !controller.CanExecute(ToolkitProcessOperation.Decline), "Busy state disables conflicting operation");
Check(!await controller.ExecuteAsync(ToolkitProcessOperation.Accept) && commands.Count == 1, "Duplicate click does not submit a second request");
pendingCommand.SetResult(J(null)); await first; pendingCommand = null;
Check(!controller.Busy && controller.Error is null && controller.Completed == ToolkitProcessOperation.Accept, "Successful completion releases busy state");
commands.Clear(); throwCommand = true;
Check(!await controller.ExecuteAsync(ToolkitProcessOperation.Accept) && !controller.Busy && controller.Error?.Message == "fixture request failed", "Failed command retains original error and clears busy state");
await controller.RefreshAsync(); Check(controller.Error?.Message == "fixture request failed" && controller.CanExecute(ToolkitProcessOperation.Accept), "State refresh preserves command failure for retry");
throwCommand = false; Check(await controller.ExecuteAsync(ToolkitProcessOperation.Accept) && controller.Error is null, "Retry succeeds and replaces previous failure");

JsonElement Event(string part, string property, object? value) => J(new { @namespace = "mobx-utils-main", name = "update-state-prop/league-client-main:" + part, args = new object?[] { property, value } });
controller.ApplyStateEvent(Event("gameflow", "phase", "Lobby"));
Check(controller.Snapshot.Phase == "Lobby" && !controller.CanExecute(ToolkitProcessOperation.Accept), "External phase event immediately updates command availability");
controller.ApplyStateEvent(Event("state", "connectionState", "disconnected"));
Check(!controller.CanExecute(ToolkitProcessOperation.LeaveLobby), "External disconnect immediately disables actions");
SetPhase("Lobby"); await controller.RefreshAsync(); pendingSnapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
var oldRefresh = controller.RefreshAsync(); controller.ApplyStateEvent(Event("gameflow", "phase", "Matchmaking")); pendingSnapshot.SetResult(J(new { phase = "Lobby" }));
Check(!await oldRefresh && controller.Snapshot.Phase == "Matchmaking", "Late snapshot cannot overwrite newer external phase event");
pendingSnapshot = null; commands.Clear(); SetPhase("InProgress");
Check(!await controller.ExecuteAsync(ToolkitProcessOperation.Accept) && commands.Count == 0, "Fresh preflight state rejects stale ready-check click");

SetPhase("ChampSelect"); await controller.ExecuteAsync(ToolkitProcessOperation.StartDodgeLoop);
Check(commands.Count == 1 && J(commands[0].Args[0]).Text("kind") == "start-dodge-loop", "Loop uses backend existing five-worker confirmed operation");
loopActive = true; await controller.RefreshControlsAsync(); commands.Clear(); await controller.ExecuteAsync(ToolkitProcessOperation.CancelDodgeLoop);
Check(commands.Count == 1 && J(commands[0].Args[0]).Text("kind") == "cancel-dodge-loop", "Cancel sends existing loop cancellation operation");
loopActive = false; await controller.RefreshControlsAsync(); commands.Clear(); SetPhase("EndOfGame"); throwPostWriteRefresh = true;
Check(await controller.ExecuteAsync(ToolkitProcessOperation.PlayAgain) && controller.Completed == ToolkitProcessOperation.PlayAgain && controller.RefreshError is not null, "Completed command stays successful if follow-up refresh fails");
throwPostWriteRefresh = false; controller.Deactivate(); commands.Clear();
Check(!await controller.ExecuteAsync(ToolkitProcessOperation.PlayAgain) && commands.Count == 0, "Unloaded page cannot submit process operations");
Console.WriteLine($"Toolkit process: {passed} mocked command and state contracts passed; no League client writes");

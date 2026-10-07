using System.Text.Json;
using LeagueAkari.WinUI.Services;

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); ++passed; Console.WriteLine("PASS " + name); }
JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);
JsonElement Event(string name, params object?[] args) => J(new { name, args });
var snapshots = new Dictionary<string, JsonElement>();
JsonElement Teams(object? teams, object? groups = null, string phase = "champ-select", object? champions = null, bool draft = false) => J(new { teams, mergedPremadeTeamMap = groups ?? new { }, championSelections = champions ?? new { }, queryStage = new { phase, gameInfo = new { gameId = 42 } }, draft = draft ? new { title = "Simulation" } : (object?)null });
void Reset()
{
    snapshots["settings"] = J(new { ratingPresetOptions = new { nameDisplayStrategy = "preferChampionName", winRate = true, kda = false, targetShortcuts = new { friendly = "F5", enemy = "F6", all = (string?)null } }, junglePresetOptions = new { nameDisplayStrategy = "preferName", activityPreference = true, showCurrentChampion = true, targetShortcuts = new { friendly = (string?)null, enemy = "F7", all = "F8" } }, premadePresetOptions = new { nameDisplayStrategy = "championNameWithName", targetShortcuts = new { friendly = "F9", enemy = (string?)null, all = "F10" } } });
    snapshots["selection"] = J(new { ratingPuuids = new[] { "self", "enemy-a" }, junglePuuids = new[] { "ally-a" }, premadeIndices = new[] { 1, 2, 99 } });
    snapshots["ongoing"] = Teams(new Dictionary<string, string[]> { ["TEAM-100"] = ["enemy-a", "enemy-b", "enemy-solo"], ["CHERRY-4"] = ["cherry-a"], ["TEAM-200"] = ["self", "ally-a", "ally-b"] }, new Dictionary<string, int> { ["self"] = 1, ["ally-a"] = 1, ["ally-b"] = 3, ["enemy-a"] = 2, ["enemy-b"] = 2, ["enemy-solo"] = 3, ["cherry-a"] = 1 }, champions: new Dictionary<string, int> { ["self"] = 1, ["ally-a"] = 2 });
    snapshots["app"] = J(new { isElevated = false, nativeSupport = new { nativeInput = new { available = true, availableOnCurrentPlatform = true, requiresElevation = true } } });
    snapshots["me"] = J(new { me = new { puuid = "self" } });
    snapshots["data"] = J(new { summoner = new Dictionary<string, object> { ["self"] = new { gameName = "Self", tagLine = "CN", profileIconId = 10 }, ["ally-a"] = new { displayName = "Ally A", profileIconId = 20 } } });
}
Reset();
var writes = new List<(string Method, object?[] Args)>();
var reads = new List<string>();
bool failWrite = false, failRead = false, sendResult = true, emitWrites = false;
Func<string, string, object?[], Task<JsonElement>?>? intercept = null;
ToolkitPresetController? controller = null;
async Task<JsonElement> Call(string ns, string method, object?[] args)
{
    if (method == "snapshot" || method == "getAll")
    {
        string key = method == "getAll" ? "data" : (string)args[0]! switch { "in-game-send-main" => (string)args[1]! == "settings" ? "settings" : "selection", "ongoing-game-main" => "ongoing", "app-common-main" => "app", "league-client-main" => "me", _ => throw new Exception("Unexpected snapshot") };
        reads.Add(key); if (failRead) throw new IOException("Fixture read failed");
        if (intercept?.Invoke(ns, method, args) is { } read) return await read;
        return snapshots[key];
    }
    writes.Add((method, args)); if (failWrite) throw new IOException("Fixture write failed");
    if (intercept?.Invoke(ns, method, args) is { } write) return await write;
    if (method.StartsWith("generate")) return J(new[] { method + ":" + args[0], "Second line" });
    if (method.StartsWith("send")) return J(sendResult);
    if (method.StartsWith("set"))
    {
        string key = method == "setPremadeIndices" ? "premadeIndices" : method == "setJunglePuuids" ? "junglePuuids" : "ratingPuuids";
        var values = snapshots["selection"].EnumerateObject().ToDictionary(property => property.Name, property => property.Value); values[key] = J(args[0]); snapshots["selection"] = J(values);
        if (emitWrites) controller!.ApplyEvent(Event("update-state-prop/in-game-send-main:state", key, args[0]));
    }
    if (method.StartsWith("update"))
    {
        string key = method.Contains("Jungle") ? "junglePresetOptions" : method.Contains("Premade") ? "premadePresetOptions" : "ratingPresetOptions";
        var all = snapshots["settings"].EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        var options = all[key].EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        foreach (var field in J(args[0]).EnumerateObject())
            if (field.Name == "targetShortcuts")
            {
                var shortcuts = options["targetShortcuts"].EnumerateObject().ToDictionary(property => property.Name, property => property.Value); foreach (var shortcut in field.Value.EnumerateObject()) shortcuts[shortcut.Name] = shortcut.Value; options[field.Name] = J(shortcuts);
            }
            else options[field.Name] = field.Value;
        all[key] = J(options); snapshots["settings"] = J(all);
        if (emitWrites) controller!.ApplyEvent(Event("update-state-prop/in-game-send-main:settings", key, all[key]));
    }
    return J(null);
}
controller = new ToolkitPresetController("rating", Call); controller.Activate(); await controller.RefreshAsync();
Check(controller.Ready && writes.Count == 0 && reads.Count == 6, "Loading reads real settings, selection and teams without writing defaults");
Check(controller.SelectedPlayers.SequenceEqual(["self", "enemy-a"]) && controller.SelectedCount == 2 && controller.TotalCount == 7, "Refresh preserves authoritative subset rather than selecting everyone");
Check(controller.Teams.Select(team => team.Id).SequenceEqual(["TEAM-200", "TEAM-100", "CHERRY-4"]) && controller.Teams[0].Friendly == true && controller.Teams[1].Friendly == false, "Own team first, then original TEAM and CHERRY order");
Check(controller.Teams[0].Players[0] is { GameName: "Self", TagLine: "CN", ChampionId: 1, ProfileIconId: 10 } && controller.Teams[0].Players[1].GameName == "Ally A", "Player rows use champion selection and summoner fallback fields");
Check(controller.Teams.Last().Players[0].GameName == "cherry" && controller.Teams.Last().Players[0].ProfileIconId == 29, "Unloaded summoner uses original short PUUID and icon 29 fallback");
controller.ApplyEvent(Event("update-state-prop/in-game-send-main:state", "ratingPuuids", new[] { "ally-a" })); Check(controller.SelectedPlayers.SequenceEqual(["ally-a"]) && controller.SelectedCount == 1, "External selection changes update the visible subset");
writes.Clear(); await controller.SetTeamAsync("TEAM-100", true);
Check(writes.Single().Method == "setRatingPuuids" && J(writes[0].Args[0]).Items().Select(item => item.GetString()).SequenceEqual(["ally-a", "enemy-a", "enemy-b", "enemy-solo"]), "Team selection commits normalized roster order and retains other team selections");
await controller.SetPlayerAsync("enemy-b", false); Check(controller.SelectedPlayers.SequenceEqual(["ally-a", "enemy-a", "enemy-solo"]), "Individual deselect keeps every unrelated selected player");
await controller.SetSelectionAsync(["unknown", "self", "self", "enemy-a"]); Check(controller.SelectedPlayers.SequenceEqual(["self", "enemy-a"]), "Player writes remove stale IDs and duplicate selection");
await controller.SetAllAsync(false); Check(controller.SelectedCount == 0 && controller.SelectedPlayers.Length == 0, "Clear really persists an empty selection");
await controller.RefreshAsync(); Check(controller.SelectedCount == 0, "Refresh does not undo a manually cleared selection");
await controller.SetAllAsync(true); Check(controller.SelectedCount == controller.TotalCount, "Select all uses the current roster");
writes.Clear(); await controller.UpdateOptionsAsync(new Dictionary<string, object?> { ["winRate"] = false });
Check(writes.Single().Method == "updateRatingPresetOptions" && J(writes[0].Args[0]).EnumerateObject().Count() == 1 && !controller.Options.Boolean("winRate") && controller.Options.Text("nameDisplayStrategy") == "preferChampionName", "Display option is an immediate sparse patch, preserving unrelated settings");
await controller.SetShortcutAsync("enemy", "Control+F12"); Check(controller.Options.Field("targetShortcuts").Text("enemy") == "Control+F12" && controller.Options.Field("targetShortcuts").Text("friendly") == "F5", "A shortcut patch cannot erase another target's shortcut");
await controller.SetShortcutAsync("enemy", null); Check(controller.Options.Field("targetShortcuts").Field("enemy").ValueKind == JsonValueKind.Null, "Unset shortcut sends and keeps explicit null");
emitWrites = true; await controller.UpdateOptionsAsync(new Dictionary<string, object?> { ["nameDisplayStrategy"] = "preferName" }); Check(controller.Options.Text("nameDisplayStrategy") == "preferName", "Authoritative option event and command response agree"); emitWrites = false;
failWrite = true; var before = controller.Options; Check(!await controller.UpdateOptionsAsync(new Dictionary<string, object?> { ["kda"] = true }) && controller.Options.ToString() == before.ToString() && controller.Error is not null, "Failed option write rolls UI back to saved options and retains error"); failWrite = false;
var lateRefresh = new TaskCompletionSource<JsonElement>(); var oldSelection = snapshots["selection"];
intercept = (ns, method, args) => method == "snapshot" && (string)args[0]! == "in-game-send-main" && (string)args[1]! == "state" ? lateRefresh.Task : null;
var refreshing = controller.RefreshAsync(); controller.ApplyEvent(Event("update-state-prop/in-game-send-main:state", "ratingPuuids", new[] { "self" })); lateRefresh.SetResult(oldSelection); await refreshing; intercept = null;
Check(controller.SelectedPlayers.SequenceEqual(["self"]), "State event during an older refresh cannot be replaced by its late selection snapshot");
failRead = true; await controller.RefreshAsync(); Check(controller.SelectedPlayers.SequenceEqual(["self"]) && controller.RefreshError is not null && !controller.Refreshing, "Failed refresh preserves selection and exposes retryable failure"); failRead = false;
await controller.RefreshAsync(); Check(controller.RefreshError is null, "Successful retry clears load failure");
writes.Clear(); snapshots["ongoing"] = Teams(new { ALL = new[] { "draft-player" } }, phase: "draft", draft: true); await controller.RefreshAsync();
Check(controller.SendDisabledReason == "draftOnly" && await controller.DryRunAsync("friendly") && writes.Single().Method == "generateRatingPresetLines", "Simulation supports dry run while disabling real writes");
Check(controller.Preview is { Target: "friendly", Lines.Length: 2 }, "Dry run records requested target and real returned lines");
controller.ClosePreview(); Check(controller.Preview is null, "Closing preview removes rendered output");
writes.Clear(); Check(!await controller.SendAsync("friendly") && writes.Count == 0, "Fresh simulated context prevents actual sending");
Reset(); await controller.RefreshAsync(); writes.Clear(); await controller.DryRunAsync("enemy"); await controller.SendAsync("enemy");
Check(writes.Select(write => write.Method).SequenceEqual(["generateRatingPresetLines", "sendRatingPreset"]) && writes.All(write => (string)write.Args[0]! == "enemy"), "Preview and send use the same target and authoritative configuration without hidden saves");
sendResult = false; Check(!await controller.SendAsync("all") && controller.Error?.Message == "preset-send-rejected" && controller.SentTarget is null, "Backend false is a failure, not a completed send"); sendResult = true;
foreach (string phase in new[] { "lobby", "champ-select", "in-game" }) { snapshots["ongoing"] = Teams(new { ALL = new[] { "self" } }, phase: phase); Check(await controller.SendAsync("all") && controller.SentTarget == "all", "Original real sending phase " + phase); }
snapshots["app"] = J(new { isElevated = false, nativeSupport = new { nativeInput = new { available = false, availableOnCurrentPlatform = true, requiresElevation = true } } }); writes.Clear();
Check(!await controller.SendAsync("all") && writes.Count == 0 && controller.NativeUnavailableReason == "needAdmin", "Unavailable in-game keyboard input disables write and explains elevation");
snapshots["ongoing"] = Teams(new { ALL = new[] { "self" } }, phase: "lobby"); Check(await controller.SendAsync("all"), "Lobby chat does not require native keyboard input");
Reset(); await controller.RefreshAsync();
var previewOne = new TaskCompletionSource<JsonElement>(); var previewTwo = new TaskCompletionSource<JsonElement>();
intercept = (ns, method, args) => method.StartsWith("generate") ? (string)args[0]! == "friendly" ? previewOne.Task : previewTwo.Task : null;
var firstPreview = controller.DryRunAsync("friendly"); var secondPreview = controller.DryRunAsync("enemy"); previewTwo.SetResult(J(new[] { "Latest enemy" })); await secondPreview; previewOne.SetResult(J(new[] { "Old friendly" })); await firstPreview;
Check(controller.Preview is { Target: "enemy" } && controller.Preview.Lines.Single() == "Latest enemy", "Out-of-order dry runs keep the most recently requested target");
previewOne = new(); var closedPreview = controller.DryRunAsync("friendly"); controller.ClosePreview(); previewOne.SetResult(J(new[] { "Too late" })); await closedPreview;
Check(controller.Preview is null && !controller.PreviewLoading, "Close cancels pending preview completion");
previewOne = new(); var changedPreview = controller.DryRunAsync("friendly"); controller.ApplyEvent(Event("update-state-prop/in-game-send-main:settings", "ratingPresetOptions.nameDisplayStrategy", "preferName")); previewOne.SetResult(J(new[] { "Wrong old options" })); await changedPreview;
Check(controller.Preview is null, "Changed options invalidate pending preview generated for old parameters");
intercept = null;
var pendingWrite = new TaskCompletionSource<JsonElement>(); intercept = (ns, method, args) => method == "updateRatingPresetOptions" ? pendingWrite.Task : null;
writes.Clear(); var writing = controller.UpdateOptionsAsync(new Dictionary<string, object?> { ["kda"] = true });
Check(controller.Busy && !await controller.SendAsync("all") && !await controller.SetAllAsync(false) && !await controller.DryRunAsync("all") && writes.Count == 1, "Pending options exclude sends, selections and previews using unsaved parameters");
controller.Deactivate(); Check(!await writing && !controller.Busy, "Unload cancels UI wait for pending write immediately"); pendingWrite.SetResult(J(null)); intercept = null;
controller.Activate(); await controller.RefreshAsync(); Check(!controller.Options.Boolean("kda"), "Late unloaded write response cannot alter reactivated saved state");
var lateStatus = new TaskCompletionSource<JsonElement>(); var oldOngoing = snapshots["ongoing"];
intercept = (ns, method, args) => method == "snapshot" && (string)args[0]! == "ongoing-game-main" ? lateStatus.Task : null;
writes.Clear(); var sending = controller.SendAsync("all"); controller.ApplyEvent(Event("update-state-prop/ongoing-game-main:state", "queryStage", new { phase = "draft", gameInfo = new { gameId = 42 } })); lateStatus.SetResult(oldOngoing); await sending; intercept = null;
Check(writes.Count == 0 && controller.Phase == "draft", "Fresh phase event beats an older send-status response and prevents write");
Reset(); controller.Deactivate(); controller = new ToolkitPresetController("premade", Call); controller.Activate(); await controller.RefreshAsync();
Check(controller.TotalGroupCount == 2 && controller.SelectedCount == 2 && controller.Teams[0].Buckets.Single().Index == 1 && controller.Teams[1].Buckets.Single().Index == 2, "Premade buckets require two members inside one team; cross-team singletons cannot be merged");
writes.Clear(); await controller.SetBucketAsync(1, false); Check(writes.Single().Method == "setPremadeIndices" && controller.SelectedGroups.SequenceEqual([2]), "Premade choice persists valid numeric indices and filters obsolete groups");
await controller.SetTeamAsync("TEAM-200", true); Check(controller.SelectedGroups.SequenceEqual([1, 2]), "Premade team selection retains another team's buckets");
await controller.SetAllAsync(false); await controller.RefreshAsync(); Check(controller.SelectedCount == 0, "Premade refresh preserves cleared group selection");
writes.Clear(); await controller.DryRunAsync("all"); await controller.SendAsync("all"); Check(writes.Select(write => write.Method).SequenceEqual(["generatePremadePresetLines", "sendPremadePreset"]), "Premade calls original generator and sender without player selection writes");
controller.ApplyEvent(Event("in-game-send-main/shortcut-error", "in-game-send-main/preset/premade/all", "Occupied shortcut")); Check(controller.ShortcutError == "Occupied shortcut", "Shortcut errors identify this preset's actual target namespace");
Check(!controller.ApplyEvent(Event("in-game-send-main/shortcut-error", "in-game-send-main/preset/rating/all", "Other preset")) && controller.ShortcutError == "Occupied shortcut", "Other preset shortcut errors do not contaminate this panel");
controller.Deactivate(); controller = new ToolkitPresetController("jungle", Call); controller.Activate(); await controller.RefreshAsync();
Check(controller.SelectedPlayers.SequenceEqual(["ally-a"]) && controller.Options.Boolean("showCurrentChampion"), "Jungle uses backend's own jungler-only selection and current champion option");
writes.Clear(); await controller.SetPlayerAsync("self", true); await controller.DryRunAsync("friendly"); await controller.SendAsync("friendly");
Check(writes.Select(write => write.Method).SequenceEqual(["setJunglePuuids", "generateJunglePresetLines", "sendJunglePreset"]), "Jungle selection, dry run and send use their separate original methods");
Check(ToolkitPresetController.DisplayFields("rating").Length == 12 && ToolkitPresetController.DisplayFields("jungle").Length == 6 && ToolkitPresetController.NameStrategies.SequenceEqual(["preferChampionName", "preferName", "championNameWithName"]), "Original display choices and name ordering are exposed to native controls");
controller.Deactivate(); Reset(); controller = new ToolkitPresetController("rating", Call); controller.Activate();
lateRefresh = new();
intercept = (ns, method, args) => method == "snapshot" && (string)args[0]! == "in-game-send-main" && (string)args[1]! == "settings" ? lateRefresh.Task : null;
var firstLoad = controller.RefreshAsync(); controller.ApplyEvent(Event("update-state-prop/in-game-send-main:settings", "ratingPresetOptions.kda", true));
Check(!controller.Ready && !await controller.SetAllAsync(true), "An early partial event cannot mark an incomplete initial snapshot ready");
lateRefresh.SetResult(snapshots["settings"]); await firstLoad; intercept = null;
Check(controller.Ready && controller.Options.Boolean("kda") && controller.Options.Boolean("winRate") && controller.Options.Field("targetShortcuts").Text("friendly") == "F5", "Initial read merges newer field event while restoring untouched saved options");
writes.Clear(); var selectionBefore = controller.SelectedPlayers; failWrite = true;
Check(!await controller.SetPlayerAsync("ally-a", true) && controller.SelectedPlayers.SequenceEqual(selectionBefore) && controller.Error is not null, "Failed player-selection write preserves saved subset for retry"); failWrite = false;
pendingWrite = new(); intercept = (ns, method, args) => method == "setRatingPuuids" ? pendingWrite.Task : null;
var staleSelection = controller.SetPlayerAsync("ally-a", true); controller.ApplyEvent(Event("update-state-prop/in-game-send-main:state", "ratingPuuids", new[] { "enemy-b" })); pendingWrite.SetResult(J(null)); await staleSelection; intercept = null;
Check(controller.SelectedPlayers.SequenceEqual(["enemy-b"]), "New authoritative selection event beats an older user-write completion");
previewOne = new(); intercept = (ns, method, args) => method.StartsWith("generate") ? previewOne.Task : null;
var cancelledPreview = controller.DryRunAsync("all"); controller.Deactivate();
Check(!await cancelledPreview && !controller.PreviewLoading, "Unload cancels a pending dry run wait without waiting for its backend reply"); previewOne.SetResult(J(new[] { "Late unloaded" })); intercept = null;
controller.Activate(); await controller.RefreshAsync(); controller.ApplyEvent(Event("ongoing-game-main/summoner-loaded", "ally-a", new { gameName = "New Ally", tagLine = "ONE", profileIconId = 80 }));
Check(controller.Teams.SelectMany(team => team.Players).Single(player => player.Puuid == "ally-a").GameName == "New Ally", "Loaded summoner events update real participant identity without a manual refresh");
controller.ApplyEvent(Event("update-state-prop/ongoing-game-main:state", "teams", new { }));
Check(controller.TotalCount == 0 && controller.TotalGroupCount == 0 && controller.SelectedCount == 0, "Empty ongoing teams hide player and premade selections without writing defaults");
controller.ApplyEvent(Event("update-state-prop/league-client-main:summoner", "me", (object?)null));
controller.ApplyEvent(Event("update-state-prop/ongoing-game-main:state", "teams", new Dictionary<string, string[]> { ["TEAM-200"] = ["self"], ["TEAM-100"] = ["enemy-a"] }));
Check(controller.Teams[0].Id == "TEAM-100" && controller.Teams.All(team => team.Friendly is null), "Without current summoner use neutral original team order and labels");
intercept = (ns, method, args) => method == "getRegistration" ? Task.FromResult(J(new { targetId = "some-other-feature", type = "normal" })) : null;
writes.Clear(); string shortcutBefore = controller.Options.Field("targetShortcuts").Text("friendly");
Check(!await controller.SetShortcutAsync("friendly", "F12") && controller.Error?.Message == "preset-shortcut-occupied" && writes.All(write => write.Method != "updateRatingPresetOptions") && controller.Options.Field("targetShortcuts").Text("friendly") == shortcutBefore, "Occupied shortcut is rejected before saving and retains the configured shortcut");
intercept = (ns, method, args) => method == "getRegistration" ? Task.FromResult(J(new { targetId = "akari-disabled-keys", type = "normal" })) : null;
Check(!await controller.SetShortcutAsync("friendly", "Enter") && controller.Error?.Message == "preset-shortcut-reserved", "Reserved registration explains the original unavailable shortcut reason");
intercept = (ns, method, args) => method == "getRegistration" ? Task.FromResult(J(new { targetId = "in-game-send-main/preset/rating/friendly", type = "normal" })) : null;
Check(await controller.SetShortcutAsync("friendly", "F5") && controller.Options.Field("targetShortcuts").Text("friendly") == "F5", "Own existing shortcut target may retain its current registration");
intercept = null; controller.ApplyEvent(Event("update-state-prop/app-common-main:state", "nativeSupport.nativeInput.available", false)); writes.Clear();
Check(!await controller.SetShortcutAsync("enemy", "F3") && writes.Count == 0 && controller.Error?.Message == "preset-shortcut-unavailable", "Unavailable native input prevents both shortcut lookup and setting write");
controller.ApplyEvent(Event("update-state-prop/app-common-main:state", "nativeSupport.nativeInput.available", true));
var pendingRegistration = new TaskCompletionSource<JsonElement>(); intercept = (ns, method, args) => method == "getRegistration" ? pendingRegistration.Task : null;
writes.Clear(); var shortcutRace = controller.SetShortcutAsync("enemy", "F3"); controller.ApplyEvent(Event("update-state-prop/app-common-main:state", "nativeSupport.nativeInput.available", false)); pendingRegistration.SetResult(J(null)); await shortcutRace;
Check(writes.All(write => write.Method != "updateRatingPresetOptions"), "Native availability changing during registration lookup prevents a stale shortcut write");
controller.ApplyEvent(Event("update-state-prop/app-common-main:state", "nativeSupport.nativeInput.available", true));
intercept = (ns, method, args) =>
{
    if (method == "updateRatingPresetOptions") { controller.ApplyEvent(Event("in-game-send-main/shortcut-error", "in-game-send-main/preset/rating/enemy", "Native registration failed")); return Task.FromResult(J(null)); }
    return null;
};
await controller.SetShortcutAsync("enemy", "F3");
Check(controller.ShortcutError == "Native registration failed", "Backend shortcut error arriving before successful settings response remains visible");
intercept = null;
Console.WriteLine($"Toolkit preset contracts: {passed} passed");

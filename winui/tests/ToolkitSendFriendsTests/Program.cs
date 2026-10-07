using System.Text.Json;
using LeagueAkari.WinUI.Services;

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); ++passed; Console.WriteLine("PASS " + name); }
JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);
JsonElement Item(string id, string title = "", string content = "", string? shortcut = null) => J(new { id, title, content, shortcut });
var writes = new List<(string Method, object?[] Args)>();
JsonElement[] saved = [Item("a", "Alpha", "A"), Item("b", "Beta", "B"), Item("c", "Gamma", "C")];
bool fail = false, sendResult = true, emit = false;
TaskCompletionSource<JsonElement>? pending = null, pendingRead = null;
FixedTextPresetController? presets = null;
async Task<JsonElement> FixedCall(string ns, string method, object?[] args)
{
    if (method == "snapshot") return pendingRead is not null ? await pendingRead.Task : J(new { fixedTextPresetItems = saved });
    writes.Add((method, args)); if (fail) throw new IOException("fixture failed"); if (pending is not null) return await pending.Task;
    int index = Array.FindIndex(saved, item => item.Text("id") == (args.FirstOrDefault() as string));
    JsonElement response;
    switch (method)
    {
        case "updateFixedTextPresetItem":
            var patch = J(args[1]); var old = saved[index];
            saved[index] = Item(old.Text("id"), patch.Field("title").ValueKind != JsonValueKind.Undefined ? patch.Text("title") : old.Text("title"), patch.Field("content").ValueKind != JsonValueKind.Undefined ? patch.Text("content") : old.Text("content"), patch.Field("shortcut").ValueKind != JsonValueKind.Undefined ? patch.Text("shortcut") : old.Text("shortcut")); response = saved[index]; break;
        case "createFixedTextPresetItem": response = Item("new"); saved = saved.Append(response).ToArray(); break;
        case "moveFixedTextPresetItem":
            int target = (string)args[1]! == "up" ? index - 1 : index + 1; if (target < 0 || target >= saved.Length) return J(false);
            (saved[index], saved[target]) = (saved[target], saved[index]); response = J(true); break;
        case "deleteFixedTextPresetItem": saved = saved.Where(item => item.Text("id") != (string)args[0]!).ToArray(); response = J(true); break;
        case "sendFixedTextPreset": return J(sendResult);
        default: throw new Exception(method);
    }
    if (emit) presets!.ApplyItems(J(saved)); return response;
}
presets = new FixedTextPresetController(FixedCall); presets.Activate(); await presets.RefreshAsync();
Check(presets.SelectedId == "a" && presets.Title == "Alpha" && !presets.Dirty, "Refresh initializes first preset and saved draft");
await presets.SelectAsync("b"); presets.Edit("Draft", "Unsaved"); presets.ApplyItems(J(saved));
Check(presets.SelectedId == "b" && presets.Title == "Draft" && presets.Content == "Unsaved" && presets.Dirty, "External settings preserve current selection and dirty draft");
fail = true; writes.Clear(); Check(!await presets.SelectAsync("c") && presets.SelectedId == "b" && presets.Dirty && presets.Error is not null, "Failed autosave keeps draft and blocks navigation");
Check(writes.Count == 1 && writes[0].Method == "updateFixedTextPresetItem", "Failed switch submits only current save");
fail = false; writes.Clear(); await presets.SelectAsync("c");
Check(presets.SelectedId == "c" && !presets.Dirty && saved.Single(item => item.Text("id") == "b").Text("title") == "Draft", "Switch waits for successful saved edits");
Check(J(writes[0].Args[1]).Field("shortcut").ValueKind == JsonValueKind.Undefined, "Saving text does not overwrite an independently updated shortcut");
presets.Edit("Gamma draft", "Different"); writes.Clear(); await presets.SetShortcutAsync("Control+F9");
Check(presets.Dirty && presets.Title == "Gamma draft" && presets.Selected!.Shortcut == "Control+F9", "Shortcut save retains unrelated text draft");
Check(J(writes.Single().Args[1]).Field("content").ValueKind == JsonValueKind.Undefined, "Shortcut submits only shortcut patch");
writes.Clear(); await presets.CreateAsync();
Check(presets.SelectedId == "new" && presets.Title == "" && presets.Content == "" && writes.Select(write => write.Method).SequenceEqual(["updateFixedTextPresetItem", "createFixedTextPresetItem"]), "Create saves current draft before selecting returned new identity");
emit = true; await presets.MoveAsync("up");
Check(presets.Items.Select(item => item.Id).SequenceEqual(saved.Select(item => item.Text("id"))) && presets.SelectedId == "new" && presets.Items[2].Id == "new", "State event plus move response cannot reorder twice");
writes.Clear(); Check(!await presets.MoveAsync("invalid") && writes.Count == 0, "Unknown move cannot submit a mutation");
// Restore a deterministic list for neighbour and boundary contracts.
saved = [Item("a", "Alpha", "A"), Item("b", "Beta", "B"), Item("c", "Gamma", "C")]; presets.ApplyItems(J(saved)); await presets.SelectAsync("b"); writes.Clear();
Check(!await presets.DeleteAsync(_ => Task.FromResult(false)) && writes.Count == 0 && presets.SelectedId == "b", "Cancelled preset confirmation does not delete");
await presets.DeleteAsync(_ => Task.FromResult(true));
Check(presets.SelectedId == "c" && presets.Items.Select(item => item.Id).SequenceEqual(["a", "c"]), "Delete selects adjacent right preset even when event arrives first");
await presets.DeleteAsync(_ => Task.FromResult(true)); Check(presets.SelectedId == "a", "Delete last preset selects left neighbour");
writes.Clear(); Check(!await presets.MoveAsync("up") && !await presets.MoveAsync("down") && writes.Count == 0, "Boundary reorder sends no mutation");
presets.Edit(new string('名', 70), new string('x', 66000)); Check(presets.Title.Length == 64 && presets.Content.Length == 65536, "Original title and content limits apply before save");
await presets.SaveAsync(); writes.Clear();
foreach (var phase in new[] { "draft", "none", "unavailable" }) Check(!await presets.SendAsync(() => Task.FromResult((phase, true))) && writes.Count == 0, "Fresh phase prevents fixed sending in " + phase);
Check(!await presets.SendAsync(() => Task.FromResult(("in-game", false))) && writes.Count == 0, "In-game native input unavailable prevents write");
foreach (string phase in new[] { "lobby", "champ-select", "in-game" }) { writes.Clear(); Check(await presets.SendAsync(() => Task.FromResult((phase, true))) && writes.Single().Method == "sendFixedTextPreset", "Original send phase " + phase); }
sendResult = false; Check(!await presets.SendAsync(() => Task.FromResult(("lobby", true))) && presets.Error?.Message == "send-unavailable", "Backend false produces send failure instead of success"); sendResult = true;
presets.Edit("Unsaved", "Changed"); writes.Clear(); Check(!await presets.SendAsync(() => Task.FromResult(("lobby", true))) && writes.Count == 0, "Dirty preset cannot submit old saved text");
pending = new(); var saveTask = presets.SaveAsync();
Check(presets.Busy && !await presets.CreateAsync() && !await presets.SetShortcutAsync("F2") && writes.Count == 1, "Pending save excludes conflicting create and shortcut write");
presets.Deactivate(); pending.SetResult(Item("a", "Late", "Late")); await saveTask;
Check(presets.Title == "Unsaved" && !presets.Busy && presets.Selected!.Title != "Late", "Unloaded save response cannot replace the retained draft"); pending = null; presets.Activate();
pendingRead = new(); var refreshTask = presets.RefreshAsync(); presets.ApplyItems(J(new[] { Item("live", "Live", "Latest") })); pendingRead.SetResult(J(new { fixedTextPresetItems = new[] { Item("old", "Old") } })); await refreshTask;
Check(presets.SelectedId == "live", "Late refresh cannot overwrite newer settings event"); pendingRead = null;
presets.ApplyItems(J(Enumerable.Range(0, 100).Select(index => Item(index.ToString())).ToArray())); writes.Clear();
Check(!presets.CanCreate && !await presets.CreateAsync() && writes.Count == 0, "Original maximum 100 presets disables create");

var calls = new List<(string Method, object?[] Args)>();
JsonElement connection = J(new { connectionState = "connected", auth = new { pid = 1, port = 5001 } });
JsonElement groups = J(new[] { new { id = 1, name = "**Default", priority = 1 }, new { id = 2, name = "Close", priority = 10 }, new { id = 3, name = "Empty", priority = 100 } });
JsonElement Friend(string id, string name, string puuid, int group, int summoner) => J(new { id, gameName = name, gameTag = "CN", puuid, groupId = group, summonerId = summoner });
JsonElement friendList = J(new[] { Friend("1", "Alpha", "p1", 1, 11), Friend("2", "Beta", "p2", 1, 22), Friend("3", "Gamma", "p3", 2, 33), Friend("4", "Orphan", "p4", 99, 44) });
bool sgpReady = false, sgpFail = false, refreshFail = false, deleteFail = false;
TaskCompletionSource<JsonElement>? pendingFriends = null, pendingGift = null, pendingHistory = null, pendingDelete = null;
async Task<JsonElement> FriendCall(string ns, string method, object?[] args)
{
    calls.Add((method, args));
    if (method == "snapshot") return (string)args[0]! == "league-client-main" ? connection : J(new { isTokenReady = sgpReady, availability = new { serversSupported = new { matchHistory = true } } });
    if (method == "sgpRequest") { if (sgpFail) throw new IOException("Private SGP history"); return J(new { games = new[] { new { json = new { gameCreation = 1700000000000L } } } }); }
    string verb = (string)args[0]!, path = (string)args[1]!;
    if (verb == "DELETE") { if (deleteFail && path.EndsWith("/2")) throw new IOException("Deletion failed"); return pendingDelete is not null ? await pendingDelete.Task : J(null); }
    if (path == "/lol-chat/v1/friend-groups") { if (refreshFail) throw new IOException("Groups unavailable"); return groups; }
    if (path == "/lol-chat/v1/friends") return pendingFriends is not null ? await pendingFriends.Task : friendList;
    if (path == "/lol-store/v1/giftablefriends") return pendingGift is not null ? await pendingGift.Task : J(new[] { new { summonerId = 22, friendsSince = "2021-01-01T00:00:00Z" }, new { summonerId = 33, friendsSince = "2020-01-01T00:00:00Z" }, new { summonerId = 99, friendsSince = "2019-01-01T00:00:00Z" } });
    if (path.Contains("/matches?")) return pendingHistory is not null ? await pendingHistory.Task : J(new { games = new { games = new[] { new { gameCreation = 1600000000000L } } } });
    throw new Exception(path);
}
var friends = new FriendToolsController(FriendCall); friends.Activate(); friends.UpdateConnection(connection); Check(await friends.RefreshAsync(), "Connected friend refresh loads grouped rows"); await friends.Enrichment;
Check(friends.Groups().Select(group => group.Id).SequenceEqual([2, 1]), "Group priority descending, empty and unknown groups excluded");
Check(friends.Groups().Single(group => group.Id == 1).Friends.Select(friend => friend.Text("id")).SequenceEqual(["2", "1"]), "Known friendship dates precede unknown while respecting oldest first");
Check(friends.Groups("  alpha#cn  ").Single().Friends.Single().Text("id") == "1", "Riot ID filter trims, ignores case and accepts name plus tag");
Check(friends.Groups("Close").Length == 0 && friends.Groups("no player").Length == 0, "Original filter searches friend identity rather than group label");
Check(friends.Since.Count == 2 && friends.LastGames.Count == 4 && friends.Since.ContainsKey("p2") && !friends.Since.ContainsKey(""), "Refresh automatically loads gift dates and latest matches with puuid mapping");
Check(calls.Any(call => call.Method == "lcuRequest" && ((string)call.Args[1]!).EndsWith("?begIndex=0&endIndex=0")), "Unready SGP uses original first LCU game request");
friends.Select(["1", "2", "group-id", "missing"]); Check(friends.Selected.SetEquals(["1", "2"]), "Selection contains friend IDs only and deduplicates");
friends.Select(friends.Selected.Concat(["3"])); Check(friends.Selected.SetEquals(["1", "2", "3"]), "Adding a visible group retains existing selection");
calls.Clear(); Check(!await friends.DeleteAsync(_ => Task.FromResult(false)) && !calls.Any(call => call.Method == "lcuRequest" && (string)call.Args[0]! == "DELETE") && friends.Selected.Count == 3, "Cancelled friend confirmation preserves selection and sends no delete");
pendingDelete = new(); calls.Clear(); var deleteTask = friends.DeleteAsync(_ => Task.FromResult(true));
Check(friends.Busy && friends.Deleting && !await friends.RefreshAsync(), "Deletion excludes concurrent refresh and exposes cancellable busy state");
friends.ApplyFriendEvent("1", "Delete", default); Check(friends.Selected.SetEquals(["2", "3"]), "In-flight own deletion event retains remaining batch selection");
friends.CancelDelete(); pendingDelete.SetResult(J(null)); await deleteTask; pendingDelete = null;
Check(calls.Count(call => call.Method == "lcuRequest" && (string)call.Args[0]! == "DELETE") == 1 && friends.DeleteOutcome is { Deleted: 1, Cancelled: true }, "Cancel allows the in-flight deletion to finish and skips remaining requests");
Check(!friends.Friends.Any(friend => friend.Text("id") == "1") && friends.Selected.SetEquals(["2", "3"]), "Completed deletion removed locally; remaining selection is retained");
await friends.RefreshAsync(); await friends.Enrichment; friends.Select(["1", "2", "3"]); deleteFail = true; await friends.DeleteAsync(_ => Task.FromResult(true));
Check(friends.Error is not null && friends.DeleteOutcome is { Deleted: 1 } && friends.Selected.SetEquals(["2", "3"]), "Partial deletion failure preserves completed count, error and remaining retry selection"); deleteFail = false;
await friends.DeleteAsync(_ => Task.FromResult(true)); Check(friends.DeleteOutcome is { Deleted: 2 } && friends.Selected.Count == 0, "Retry operates on remaining friends only");
await friends.RefreshAsync(); await friends.Enrichment; friends.Select(["1"]); refreshFail = true; Check(!await friends.RefreshAsync() && friends.Friends.Length == 4 && friends.Selected.Contains("1") && friends.Error is not null, "Failed refresh retains previous rows and selection"); refreshFail = false;
await friends.RefreshAsync(); await friends.Enrichment; Check(friends.Selected.Count == 0 && friends.Error is null, "Successful manual refresh clears selection and prior error");
pendingFriends = new(); var eventRefresh = friends.RefreshAsync(); friends.ApplyFriendEvent("1", "Delete", default); friends.ApplyFriendEvent("2", "Update", Friend("2", "Fresh Beta", "p2", 2, 22)); pendingFriends.SetResult(friendList); await eventRefresh; await friends.Enrichment; pendingFriends = null;
Check(friends.Friends.All(friend => friend.Text("id") != "1") && friends.Groups("Fresh Beta").Single().Id == 2, "Friend events arriving during refresh are replayed over its older snapshot");
await friends.RefreshAsync(); await friends.Enrichment;
friends.Select(["1", "2"]); friends.ApplyFriendEvent("1", "Update", Friend("1", "Changed", "p1", 2, 11)); Check(friends.Groups("Changed").Single().Id == 2, "LCU update moves friend to current group and updates identity");
friends.ApplyFriendEvent("1", "Delete", default); Check(friends.Selected.Count == 0 && friends.Friends.All(friend => friend.Text("id") != "1"), "LCU deletion removes friend and clears checked rows");
sgpReady = true; calls.Clear(); await friends.RefreshAsync(); await friends.Enrichment;
Check(friends.LastGames["p1"].ToUnixTimeMilliseconds() == 1700000000000L && calls.Any(call => call.Method == "sgpRequest"), "Supported ready SGP supplies recent game metadata");
sgpFail = true; calls.Clear(); await friends.RefreshAsync(); await friends.Enrichment;
Check(calls.Count(call => call.Method == "sgpRequest") == 4 && !calls.Any(call => call.Method == "lcuRequest" && ((string)call.Args[1]!).Contains("/matches?")), "SGP failure is isolated per friend without changing source to LCU"); sgpFail = false; sgpReady = false;
pendingFriends = new(); var refreshLate = friends.RefreshAsync(); connection = J(new { connectionState = "connected", auth = new { pid = 2, port = 5002 } }); friends.UpdateConnection(connection); pendingFriends.SetResult(friendList); await refreshLate; pendingFriends = null;
Check(friends.Friends.Length == 0 && !friends.Busy, "Previous client refresh cannot populate new client identity");
await friends.RefreshAsync(); await friends.Enrichment; friends.Select(["1"]); calls.Clear(); connection = J(new { connectionState = "disconnected", auth = (object?)null });
Check(!await friends.DeleteAsync(_ => Task.FromResult(true)) && !calls.Any(call => call.Method == "lcuRequest" && (string)call.Args[0]! == "DELETE") && !friends.Connected, "Fresh connection check after confirmation prevents disconnected delete");
connection = J(new { connectionState = "connected", auth = new { pid = 3, port = 5003 } }); friends.UpdateConnection(connection);
pendingGift = new(); pendingHistory = new(); await friends.RefreshAsync(); var enrichmentLate = friends.Enrichment; friends.Deactivate(); pendingGift.SetResult(J(new[] { new { summonerId = 11, friendsSince = "2022-01-01T00:00:00Z" } })); pendingHistory.SetResult(J(new { games = new { games = new[] { new { gameCreation = 1800000000000L } } } })); await enrichmentLate;
Check(friends.Since.Count == 0 && friends.LastGames.Count == 0, "Unloaded gift metadata and history replies cannot update old UI"); pendingGift = pendingHistory = null;
friends.Activate(); await friends.RefreshAsync(); await friends.Enrichment; friends.Select(["1", "2"]); pendingDelete = new(); calls.Clear(); var unloadedDelete = friends.DeleteAsync(_ => Task.FromResult(true)); friends.Deactivate(); pendingDelete.SetResult(J(null)); await unloadedDelete; pendingDelete = null;
Check(calls.Count(call => call.Method == "lcuRequest" && (string)call.Args[0]! == "DELETE") == 1 && friends.DeleteOutcome is null, "Unloaded deletion stops the sequence and does not publish completion");
var now = DateTimeOffset.Parse("2026-10-07T00:00:00Z");
Check(FriendToolsDate.Relative(now.AddMinutes(-5), now, false) == "5 分钟前" && FriendToolsDate.Relative(now.AddDays(2), now, true) == "in 2 days", "Original date presentation supports relative Chinese and future English");
Console.WriteLine($"Toolkit send and friends contracts: {passed} passed");

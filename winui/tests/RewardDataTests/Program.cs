using System.Text.Json;
using LeagueAkari.WinUI.Services;
int passed = 0;
void Equal<T>(T expected, T actual, string message) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(message + ": " + actual); passed++; }
JsonElement Json(string source) => JsonDocument.Parse(source).RootElement.Clone();
var missions = RewardData.Missions(Json("""[{"id":"mission/a","internalName":"Internal mission","status":"SELECT_REWARDS","rewardStrategy":{"selectMaxGroupCount":2},"rewards":[{"rewardGroup":"g1","description":"One","iconUrl":"/one.png"},{"rewardGroup":"g2","description":"Two"},{"rewardGroup":"g3","description":"Three"}]},{"id":"running","status":"IN_PROGRESS","rewards":[]}]"""));
Equal(1, missions.Length, "Only SELECT_REWARDS mission claimable"); Equal("Internal mission", missions[0].Title, "Mission internalName matches original card"); Equal("/one.png", missions[0].Rewards[0].Icon, "Mission reward icon mapping");
var missionRequest = RewardData.Request(RewardClaimKind.Missions, missions[0], count => count - 1); var missionBody = JsonSerializer.SerializeToElement(missionRequest.Body);
Equal("PUT", missionRequest.Method, "Mission claim method"); Equal("/lol-missions/v1/player/mission%2Fa", missionRequest.Path, "Mission path escapes identity"); Equal("g2,g3", string.Join(',', missionBody.Field("rewardGroups").Items().Select(value => value.GetString())), "Random draw without replacement sorted back to source order"); Equal("Two, Three", missionRequest.Claimed, "Chosen reward description in success status");
var grants = RewardData.Grants(Json("""[{"info":{"id":"grant-1","status":"PENDING_SELECTION"},"rewardGroup":{"id":"group","localizations":{"title":"Gift title"},"selectionStrategyConfig":{"maxSelectionsAllowed":1},"rewards":[{"id":"r1","localizations":{"title":"Reward one"},"media":{"iconUrl":"/gift.png"}},{"id":"r2","localizations":{"title":"Reward two"}}]}},{"info":{"id":"completed","status":"CLAIMED"},"rewardGroup":{}}]"""));
Equal(1, grants.Length, "Live grant events exclude completed grant"); Equal("Gift title", grants[0].Title, "Grant group localized title"); Equal("Reward one", grants[0].Rewards[0].Name, "Grant item localization"); Equal("/gift.png", grants[0].Rewards[0].Icon, "Grant media icon");
var grantRequest = RewardData.Request(RewardClaimKind.Grants, grants[0], _ => 1); var grantBody = JsonSerializer.SerializeToElement(grantRequest.Body); Equal("group", grantBody.Text("rewardGroupId"), "Grant body group ID"); Equal("r2", grantBody.Field("selections").Items().Single().GetString(), "Grant selected reward ID"); Equal("grant-1", grantBody.Text("grantId"), "Grant body grant ID");
var events = RewardData.Events(Json("""[{"eventId":"evt","eventInfo":{"eventName":"Actual pass name","name":"Wrong field","unclaimedRewardCount":2}},{"eventId":"empty","eventInfo":{"unclaimedRewardCount":0}}]""")); Equal("Actual pass name", events.Single().Title, "Event name uses actual eventName");
var eventRewards = RewardData.EventRewards(Json("""[{"rewardOptions":[{"state":"Unselected","rewardGroupId":"normal","rewardName":"Normal","thumbIconPath":"/normal.png"},{"state":"UNSELECTED","rewardName":"Wrong casing"},{"state":"Selected","rewardName":"Taken"}]}]"""), Json("""[{"rewardOptions":[{"state":"Unselected","rewardGroupId":"bonus","rewardName":"Bonus"}]}]""")); Equal("normal,bonus", string.Join(',', eventRewards.Select(reward => reward.Id)), "Actual Unselected casing and track+bonus merge");
Equal("/lol-event-hub/v1/events/evt/reward-track/claim-all", RewardData.Request(RewardClaimKind.EventHub, events[0]).Path, "Event claims all endpoint");
Equal("grant-1", string.Join(',', RewardData.PreserveSelected(new[] { "grant-1", "completed" }, grants)), "Live refresh preserves only still-claimable checked IDs");
var rewards = Enumerable.Range(0, 5).Select(i => new ClaimableReward(i.ToString(), i.ToString(), "")).ToArray(); Equal("0,1,2,3,4", string.Join(',', RewardData.Choose(rewards, 5).Select(reward => reward.Id)), "All options request preserves original order");
bool invalid = false; try { RewardData.Choose(rewards, 6); } catch (InvalidOperationException) { invalid = true; } Equal(true, invalid, "Impossible count fails rather than silently choosing fewer");
var source = new Dictionary<string, ClaimableRewardEntry> { ["a"] = events[0] with { Id = "a" }, ["b"] = events[0] with { Id = "b" }, ["c"] = events[0] with { Id = "c" } };
bool active = true; var sent = new List<string>(); var succeeded = new List<string>();
await RewardClaimBatch.RunAsync(new[] { "a", "b", "c" }, id => source.GetValueOrDefault(id), RewardClaimKind.EventHub, () => active, async request => { sent.Add(request.Path); active = false; await Task.CompletedTask; }, request => succeeded.Add(request.Path));
Equal(1, sent.Count, "Cancellation stops subsequent writes, without claiming rollback of in-flight result"); Equal(1, succeeded.Count, "Successful in-flight result remains acknowledged");
sent.Clear(); succeeded.Clear(); bool failure = false;
try { await RewardClaimBatch.RunAsync(new[] { "a", "b", "c" }, id => source.GetValueOrDefault(id), RewardClaimKind.EventHub, () => true, request => { sent.Add(request.Path); if (sent.Count == 2) throw new InvalidOperationException("simulated failure"); return Task.CompletedTask; }, request => succeeded.Add(request.Path)); } catch (InvalidOperationException) { failure = true; }
Equal(true, failure, "Batch retains failure for UI"); Equal(2, sent.Count, "Failure stops before third claim"); Equal(1, succeeded.Count, "Failure never counts as successful claim"); Equal(3, source.Count, "Batch does not invent server-side removal");
sent.Clear(); await RewardClaimBatch.RunAsync(new[] { "missing", "a" }, id => source.GetValueOrDefault(id), RewardClaimKind.EventHub, () => true, request => { sent.Add(request.Path); return Task.CompletedTask; }, _ => { }); Equal(1, sent.Count, "LCU event-removed checked row skipped");
Console.WriteLine($"Reward sources, claim contracts, selection and cancellation: {passed} passed");
var operations = new RewardOperationState(); var oldRead = operations.Begin(false)!; var oldResponse = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); string displayed = "old-client", errorStatus = "";
async Task Observe(RewardOperation operation, Task<string> response)
{
    try { string value = await response; if (operations.IsCurrent(operation)) displayed = value; }
    catch (Exception ex) { if (operations.IsCurrent(operation)) errorStatus = ex.Message; }
    finally { operations.Complete(operation); }
}
var oldPending = Observe(oldRead, oldResponse.Task); operations.Reset(); displayed = "new-client";
var newRead = operations.Begin(false)!; var newResponse = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); var newPending = Observe(newRead, newResponse.Task);
Equal(true, oldRead.Cancelled, "Changing client invalidates old in-flight request"); Equal(true, operations.IsCurrent(newRead), "New client can refresh before old read finishes");
oldResponse.SetException(new InvalidOperationException("Old client failed")); await oldPending;
Equal("", errorStatus, "Late old failure cannot replace new client's status"); Equal("new-client", displayed, "Late old completion retains new client's contents"); Equal(true, operations.Loading, "Old finally cannot release new client's active refresh");
Equal<RewardOperation?>(null, operations.Begin(true), "Busy new refresh still prevents concurrent claim");
newResponse.SetResult("new-client-refreshed"); await newPending; Equal("new-client-refreshed", displayed, "New response publishes normally"); Equal(false, operations.Loading, "Current finally releases only its own busy state");
var firstClaim = operations.Begin(true)!; operations.Reset(); operations.Reset(); var returnToSamePid = operations.Begin(true)!;
Equal(false, operations.IsCurrent(firstClaim), "A-to-B-to-A does not revive first A claim"); Equal(false, operations.Complete(firstClaim), "Old A finally cannot clear later A claim"); Equal(true, operations.Claiming, "Later A claim remains busy after stale completion"); Equal(true, firstClaim.Generation != returnToSamePid.Generation, "Delayed post-claim refresh cannot attach to a new session with same PID");
operations.Cancel(); Equal(true, returnToSamePid.Cancelled, "User cancellation belongs to current claim"); Equal(true, operations.IsCurrent(returnToSamePid), "Cancellation retains ownership until in-flight request settles"); operations.Complete(returnToSamePid);
var unloadRead = operations.Begin(false)!; operations.Reset(); Equal(false, operations.IsCurrent(unloadRead), "Unloading drops pending request ownership"); Equal(false, operations.Loading, "Unloading releases busy flag immediately for next activation"); Equal(false, operations.Complete(unloadRead), "Unloaded completion cannot mutate a reactivated section");
Console.WriteLine($"Reward contract and asynchronous ownership checks: {passed} passed");

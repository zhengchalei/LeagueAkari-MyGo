using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public enum RewardClaimKind { Grants, Missions, EventHub }
public sealed record ClaimableReward(string Id, string Name, string Icon);
public sealed record ClaimableRewardEntry(string Id, string Title, ClaimableReward[] Rewards, int Selections, string GroupId = "");
public sealed record RewardClaimRequest(string Method, string Path, object? Body, string Claimed);

public static class RewardData
{
    public static ClaimableRewardEntry[] Missions(JsonElement data) => data.Items().Where(item => item.Text("status") == "SELECT_REWARDS").Select(item => new ClaimableRewardEntry(item.Text("id"), item.Text("internalName"), item.Field("rewards").Items().Select(reward => new ClaimableReward(reward.Text("rewardGroup"), reward.Text("description"), reward.Text("iconUrl"))).ToArray(), DefaultCount(item.Field("rewardStrategy").Number("selectMaxGroupCount")))).ToArray();
    public static ClaimableRewardEntry[] Grants(JsonElement data, bool filteredEndpoint = false) => data.Items().Where(item => item.Field("info").Text("status") == "PENDING_SELECTION" || filteredEndpoint && item.Field("info").Text("status").Length == 0).Select(item => new ClaimableRewardEntry(item.Field("info").Text("id"), item.Field("rewardGroup").Field("localizations").Text("title"), item.Field("rewardGroup").Field("rewards").Items().Select(reward => new ClaimableReward(reward.Text("id"), reward.Field("localizations").Text("title"), reward.Field("media").Text("iconUrl"))).ToArray(), DefaultCount(item.Field("rewardGroup").Field("selectionStrategyConfig").Number("maxSelectionsAllowed")), item.Field("rewardGroup").Text("id"))).ToArray();
    public static ClaimableRewardEntry[] Events(JsonElement data) => data.Items().Where(item => item.Field("eventInfo").Number("unclaimedRewardCount") > 0).Select(item => new ClaimableRewardEntry(item.Text("eventId"), item.Field("eventInfo").Text("eventName"), [], 0)).ToArray();
    public static ClaimableReward[] EventRewards(JsonElement track, JsonElement bonus) => track.Items().Concat(bonus.Items()).SelectMany(item => item.Field("rewardOptions").Items()).Where(reward => reward.Text("state") == "Unselected").Select(reward => new ClaimableReward(reward.Text("rewardGroupId"), reward.Text("rewardName"), reward.Text("thumbIconPath"))).ToArray();
    private static int DefaultCount(double count) => count == 0 ? 1 : (int)count;
    public static string[] PreserveSelected(IEnumerable<string> selected, IEnumerable<ClaimableRewardEntry> entries)
    {
        var valid = entries.Select(entry => entry.Id).ToHashSet(); return selected.Where(valid.Contains).ToArray();
    }
    public static RewardClaimRequest Request(RewardClaimKind kind, ClaimableRewardEntry entry, Func<int, int>? chooseIndex = null)
    {
        var id = Uri.EscapeDataString(entry.Id);
        if (kind == RewardClaimKind.EventHub) return new("POST", $"/lol-event-hub/v1/events/{id}/reward-track/claim-all", null, entry.Title);
        var chosen = Choose(entry.Rewards, entry.Selections, chooseIndex);
        return kind == RewardClaimKind.Missions
            ? new("PUT", "/lol-missions/v1/player/" + id, new { rewardGroups = chosen.Select(reward => reward.Id).ToArray() }, string.Join(", ", chosen.Select(reward => reward.Name)))
            : new("POST", "/lol-rewards/v1/grants/" + id + "/select", new { grantId = entry.Id, rewardGroupId = entry.GroupId, selections = chosen.Select(reward => reward.Id).ToArray() }, string.Join(", ", chosen.Select(reward => reward.Name)));
    }
    public static ClaimableReward[] Choose(ClaimableReward[] rewards, int count, Func<int, int>? chooseIndex = null)
    {
        // Original ChoiceMaker uses equal weights, no replacement and original-order output.
        if (rewards.Length == 0 || count > rewards.Length) throw new InvalidOperationException("Count cannot exceed the number of choices");
        if (count <= 0) return [];
        if (count == rewards.Length) return rewards.ToArray();
        var remaining = Enumerable.Range(0, rewards.Length).ToList(); var selected = new List<int>();
        for (int i = 0; i < count; i++) { int index = chooseIndex?.Invoke(remaining.Count) ?? Random.Shared.Next(remaining.Count); selected.Add(remaining[index]); remaining.RemoveAt(index); }
        return selected.Order().Select(index => rewards[index]).ToArray();
    }
}

public static class RewardClaimBatch
{
    public static async Task RunAsync(IEnumerable<string> selected, Func<string, ClaimableRewardEntry?> current, RewardClaimKind kind, Func<bool> canContinue, Func<RewardClaimRequest, Task> send, Action<RewardClaimRequest> claimed, Func<int, int>? chooseIndex = null)
    {
        foreach (string id in selected.ToArray())
        {
            if (!canContinue()) break;
            if (current(id) is not { } entry) continue;
            var request = RewardData.Request(kind, entry, chooseIndex);
            if (!canContinue()) break;
            await send(request);
            claimed(request);
        }
    }
}

/// <summary>Each reward section releases only the request that owns its current client session.</summary>
public sealed class RewardOperationState
{
    private RewardOperation? _current;
    public int Generation { get; private set; }
    public bool Loading => _current != null;
    public bool Claiming => _current?.Claiming == true;
    public bool Cancelled => _current?.Cancelled == true;
    public RewardOperation? Begin(bool claiming)
    {
        if (_current != null) return null;
        return _current = new RewardOperation(Generation, claiming);
    }
    public bool IsCurrent(RewardOperation operation) => ReferenceEquals(_current, operation);
    public void Cancel() => _current?.Cancel();
    public void Reset()
    {
        _current?.Cancel(); _current = null; Generation++;
    }
    public bool Complete(RewardOperation operation)
    {
        if (!IsCurrent(operation)) return false;
        _current = null; return true;
    }
}

public sealed class RewardOperation(int generation, bool claiming)
{
    public int Generation { get; } = generation;
    public bool Claiming { get; } = claiming;
    public bool Cancelled { get; private set; }
    public void Cancel() => Cancelled = true;
}

namespace LeagueAkari.WinUI.Services;

public sealed record HistoryCollection(HistoryFilterSettings Filter, int BatchSize, int TargetCount, int Iterations)
{
    // Match the original init-parameter collection: champion and position apply to this player.
    public static HistoryCollection ForPlayer(string puuid, int? champion, string? position, int expectedCount, bool supportsPosition)
    {
        int target = Math.Clamp(expectedCount > 0 ? expectedCount : 20, 1, 1000);
        int iterations = Math.Max(1, (int)Math.Ceiling(Math.Min(target * 10, 1000) / 20d));
        var state = new HistoryFilterState();
        var player = state.Add("player", state.RootId, HistoryFilterArg.Param(puuid), HistoryFilterArg.Node(null));
        state.NodeMap[state.RootId].Args[0] = HistoryFilterArg.Node(player.Id);
        var conditions = new List<HistoryFilterNode>();
        if (champion is > 0) conditions.Add(state.Add("isChampion", player.Id, HistoryFilterArg.Param(champion.Value)));
        if (supportsPosition && !string.IsNullOrEmpty(position)) conditions.Add(state.Add("isPosition", player.Id, HistoryFilterArg.Param(position)));
        if (conditions.Count == 1) player.Args[1] = HistoryFilterArg.Node(conditions[0].Id);
        else if (conditions.Count > 1)
        {
            var and = state.Add("and", player.Id, conditions.Select(c => HistoryFilterArg.Node(c.Id)).ToArray());
            foreach (var condition in conditions) condition.ParentId = and.Id;
            player.Args[1] = HistoryFilterArg.Node(and.Id);
        }
        return new(new() { Mode = "advanced", Advanced = state, CollectEnabled = true, EnablePosition = supportsPosition }, 20, target, iterations);
    }
}

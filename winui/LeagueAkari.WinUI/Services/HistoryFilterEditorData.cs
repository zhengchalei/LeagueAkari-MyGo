using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record HistoryFilterCatalogEntry(int Id, string Name, string Group, JsonElement Data);
public sealed record HistoryFilterPlayerSuggestion(string Puuid, string Label, int ProfileIconId)
{
    public override string ToString() => Label;
}
public sealed record HistoryFilterPlayerSearchResult(JsonElement Player, string? Error);

/// <summary>Only the latest input may publish a remote suggestion, even when an older HTTP request cannot be aborted.</summary>
public sealed class HistoryFilterPlayerSearch(Func<string, Task<JsonElement>> lookup, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private CancellationTokenSource? _pending;
    private long _generation;
    private Action<bool>? _loading;
    public void Cancel()
    {
        _generation++; _pending?.Cancel(); _pending = null; _loading?.Invoke(false); _loading = null;
    }
    public async Task<HistoryFilterPlayerSearchResult?> SearchAsync(string input, Action<bool>? loading = null)
    {
        Cancel(); long generation = _generation;
        var alias = HistoryFilterEditorData.PlayerAlias(input); if (alias is null) return null;
        using var pending = new CancellationTokenSource(); _pending = pending; _loading = loading;
        bool Current() => generation == _generation && !pending.IsCancellationRequested;
        try
        {
            await (delay ?? ((duration, token) => Task.Delay(duration, token)))(TimeSpan.FromMilliseconds(750), pending.Token);
            if (!Current()) return null; loading?.Invoke(true);
            var player = await lookup(alias).WaitAsync(pending.Token);
            if (!Current()) return null;
            return new(player, player.Text("puuid").Length > 0 ? null : "not-found");
        }
        catch (OperationCanceledException) when (!Current()) { return null; }
        catch (Exception ex) { return Current() ? new(default, ex.Message) : null; }
        finally { if (Current()) { _pending = null; _loading = null; loading?.Invoke(false); } }
    }
}

/// <summary>Editing operations retain the original version-one DSL, including its legacy two-number ranges.</summary>
public static class HistoryFilterEditorData
{
    public static string? PlayerAlias(string input)
    {
        var pieces = input.Split('#');
        return pieces.Length < 2 || pieces[0].Trim().Length == 0 || pieces[1].Trim().Length == 0 ? null : pieces[0].Trim() + "#" + pieces[1].Trim();
    }
    public static HistoryFilterPlayerSuggestion[] PlayerSuggestions(IEnumerable<JsonElement> cached, IEnumerable<JsonElement> page, string currentPuuid, string input)
    {
        return cached.Concat(page).Where(p => p.Text("puuid").Length > 0).DistinctBy(p => p.Text("puuid"))
            .Select(p => new HistoryFilterPlayerSuggestion(p.Text("puuid"), p.Text("gameName") + "#" + p.Text("tagLine"), (int)p.Number("profileIconId")))
            .Where(p => p.Label.Contains(input, StringComparison.OrdinalIgnoreCase) || p.Puuid.Contains(input, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Puuid == currentPuuid).ToArray();
    }
    public static readonly string[] GameModes = ["CLASSIC", "ARAM", "URF", "CHERRY", "KIWI", "STRAWBERRY", "PRACTICETOOL", "TUTORIAL", "NEXUSBLITZ", "ULTBOOK", "ONEFORALL", "SNOWURF", "DOOMBOTSTEEMO", "RUBY", "ARSR", "ASSASSINATE", "FIRSTBLOOD", "PROJECT", "STARGUARDIAN", "BRAWL"];
    public static bool SupportsMeasure(string type) => HistoryFilter.NumberStats.ContainsKey(type) && type is not ("kdaBetween" or "levelBetween" or "killParticipationBetween" or "dgrBetween");
    public static HistoryFilterSettings Draft(HistoryFilterSettings settings) => JsonSerializer.Deserialize<HistoryFilterSettings>(JsonSerializer.Serialize(settings))!;
    public static SimpleHistoryFilterState Clear(SimpleHistoryFilterState state) => new() { Version = state.Version };
    public static HistoryFilterArg[] Defaults(string type)
    {
        if (HistoryFilter.NumberStats.ContainsKey(type))
        {
            double min = type == "levelBetween" ? 1 : 0;
            double max = type switch
            {
                "levelBetween" => 18, "killParticipationBetween" => 100, "dgrBetween" => 500,
                "soloKillsBetween" or "doubleKillsBetween" or "tripleKillsBetween" or "quadraKillsBetween" or "pentaKillsBetween" => 20,
                "kdaBetween" or "killsBetween" or "deathsBetween" or "assistsBetween" => 999,
                _ => 999999
            };
            return SupportsMeasure(type) ? [HistoryFilterArg.Param("value"), HistoryFilterArg.Param(min), HistoryFilterArg.Param(max)] : [HistoryFilterArg.Param(min), HistoryFilterArg.Param(max)];
        }
        return type switch
        {
            "not" or "all" or "anyone" or "everyone" => [HistoryFilterArg.Node(null)],
            "player" or "allies" or "enemies" => [HistoryFilterArg.Param(null), HistoryFilterArg.Node(null)],
            "hasPlayer" => [HistoryFilterArg.Param(null)], "isLoss" => [HistoryFilterArg.Param(false)],
            "durationBetween" => [HistoryFilterArg.Param(0), HistoryFilterArg.Param(999999)],
            "isPosition" => [HistoryFilterArg.Param("TOP")], "gameCreationInTimeRange" => [HistoryFilterArg.Param("all")], "isGameMode" => [HistoryFilterArg.Param("CLASSIC")],
            "hasItem" => [HistoryFilterArg.Param(3031), HistoryFilterArg.Param(-1)], "hasSpell" => [HistoryFilterArg.Param(4), HistoryFilterArg.Param(-1)], "hasPerk" => [HistoryFilterArg.Param(8005), HistoryFilterArg.Param(-1)], "hasPerkStyle" => [HistoryFilterArg.Param(8000), HistoryFilterArg.Param(-1)], "hasAugment" => [HistoryFilterArg.Param(null), HistoryFilterArg.Param(-1)],
            "isChampion" => [HistoryFilterArg.Param(893)], "isQueue" => [HistoryFilterArg.Param(450)], "isMap" => [HistoryFilterArg.Param(11)], _ => []
        };
    }
    public static (string Mode, double Min, double Max) Range(HistoryFilterNode node)
    {
        bool measured = node.Args.FirstOrDefault()?.Value.ValueKind == JsonValueKind.String; int offset = measured ? 1 : 0;
        return (measured ? node.Args[0].StringValue! : "value", node.Args.ElementAtOrDefault(offset)?.Value.TryNumber() ?? 0, node.Args.ElementAtOrDefault(offset + 1)?.Value.TryNumber() ?? 0);
    }
    public static void SetRange(HistoryFilterNode node, double min, double max)
    {
        string mode = Range(node).Mode;
        node.Args = SupportsMeasure(node.Type) ? [HistoryFilterArg.Param(mode), HistoryFilterArg.Param(min), HistoryFilterArg.Param(max)] : [HistoryFilterArg.Param(min), HistoryFilterArg.Param(max)];
    }
    public static void SetMeasure(HistoryFilterNode node, string mode)
    {
        if (!SupportsMeasure(node.Type)) return;
        var range = Range(node);
        node.Args = [HistoryFilterArg.Param(mode), HistoryFilterArg.Param(mode == "value" ? range.Min : 0), HistoryFilterArg.Param(mode == "value" ? range.Max : 100)];
    }
    public static void ToggleLogic(HistoryFilterNode node)
    {
        if (node.Type is not ("and" or "or")) return;
        node.Type = node.Type == "and" ? "or" : "and";
        node.ArgDeleteStrategy = "remove-from-array";
    }
    public static int SlotCount(string type) => type switch { "hasItem" => 7, "hasSpell" or "hasPerkStyle" => 2, "hasAugment" => 5, "hasPerk" => 6, _ => 0 };
    public static string Category(string type) => type switch
    {
        "and" or "or" or "not" => "logicGroups",
        "isQueue" or "isGameMode" or "isMap" or "isMatchedGame" or "isPveGame" or "isAbort" or "isRemake" or "durationBetween" => "gameConditions",
        "allies" or "enemies" or "anyone" or "everyone" or "player" or "hasPlayer" => "matchConditions",
        "isWin" or "isLoss" => "resultConditions",
        _ => HistoryFilter.NumberStats.ContainsKey(type) ? "statConditions" : "participantConditions"
    };
    public static HistoryFilterSpec[] Choices(string scope)
    {
        string[] categories = ["logicGroups", "gameConditions", "matchConditions", "resultConditions", "participantConditions", "statConditions"];
        return HistoryFilter.Specs.Where(s => s.Require.Contains(scope) && s.Type is not ("all" or "gameCreationInTimeRange")).OrderBy(s => Array.IndexOf(categories, Category(s.Type))).ToArray();
    }
    public static HistoryFilterCatalogEntry[] Catalog(JsonElement catalog, string name)
    {
        var source = name == "perkStyles" ? catalog.Field("perkstyles").Field("styles") : catalog.Field(name);
        var entries = source.ValueKind == JsonValueKind.Object ? source.EnumerateObject().Select(p => p.Value) : source.Items();
        return entries.Select(value =>
        {
            int id = (int)value.Number("id", -1);
            string label = name == "augments" ? value.Text("nameTRA", value.Text("name")) : value.Text("name", value.Text("displayName", value.Text("description")));
            string group = name != "augments" ? "" : id is >= 1001 and <= 3000 ? "kiwi" : id is > 0 and <= 1000 ? "cherry" : "other";
            return new HistoryFilterCatalogEntry(id, label, group, value);
        }).Where(v => v.Id >= 0).OrderBy(v => v.Group switch { "kiwi" => 0, "cherry" => 1, _ => 2 }).ThenBy(v => v.Name, StringComparer.CurrentCulture).ToArray();
    }
}

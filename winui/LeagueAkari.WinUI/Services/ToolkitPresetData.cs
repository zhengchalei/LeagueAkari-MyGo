using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record ToolkitPresetPlayer(string Puuid, string GameName, string TagLine, int ProfileIconId, int? ChampionId, int? PremadeGroup);
public sealed record ToolkitPresetBucket(int Index, ToolkitPresetPlayer[] Players);
public sealed record ToolkitPresetTeam(string Id, bool? Friendly, ToolkitPresetPlayer[] Players, ToolkitPresetBucket[] Buckets);
public sealed record ToolkitPresetPreview(string Target, string[] Lines);

/// <summary>Uses the backend's current options and selection for both dry run and real sending.</summary>
public sealed class ToolkitPresetController
{
    public static readonly string[] Targets = ["friendly", "enemy", "all"];
    public static readonly string[] NameStrategies = ["preferChampionName", "preferName", "championNameWithName"];
    public static string[] DisplayFields(string kind) => kind switch
    {
        "rating" => ["winRate", "kda", "avgSoloKills", "avgVisionScore", "avgChampionDamage", "avgDamageTaken", "avgGold", "avgCsPerMinute", "avgKillParticipation", "avgDamageGoldEfficiency", "mainChampions", "mainPositions"],
        "jungle" => ["activityPreference", "firstClearDistribution", "earlyGank", "dragonControl", "monsterControl", "mainChampions"],
        "premade" => [],
        _ => throw new ArgumentException("Unknown preset kind", nameof(kind))
    };
    private readonly Func<string, string, object?[], Task<JsonElement>> _call;
    private readonly Dictionary<string, JsonElement> _states = [];
    private readonly Dictionary<string, int> _versions = [];
    private readonly Dictionary<string, Dictionary<string, (int Version, JsonElement Value)>> _eventPatches = [];
    private readonly HashSet<string> _loaded = [];
    private CancellationTokenSource _lifetime = new();
    private bool _active;
    private int _generation, _refresh, _preview;
    private string _context = "";
    public string Kind { get; }
    private string Suffix => char.ToUpperInvariant(Kind[0]) + Kind[1..] + "Preset";
    public string SelectionKey => Kind == "premade" ? "premadeIndices" : Kind + "Puuids";
    public JsonElement Options => State("settings").Field(Kind + "PresetOptions");
    public string[] SelectedPlayers => State("selection").Field(SelectionKey).Items().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).Distinct().ToArray();
    public int[] SelectedGroups => State("selection").Field(SelectionKey).Items().Where(item => item.ValueKind == JsonValueKind.Number).Select(item => (int)item.TryNumber()).Distinct().ToArray();
    public ToolkitPresetTeam[] Teams { get; private set; } = [];
    public bool Ready => new[] { "settings", "selection", "ongoing", "app", "me", "data" }.All(_loaded.Contains);
    public bool Refreshing { get; private set; }
    public bool Busy { get; private set; }
    public Exception? Error { get; private set; }
    public Exception? RefreshError { get; private set; }
    public string? ShortcutError { get; private set; }
    public ToolkitPresetPreview? Preview { get; private set; }
    public bool PreviewLoading { get; private set; }
    public string? SentTarget { get; private set; }
    public string Phase => State("ongoing").Field("draft").ValueKind == JsonValueKind.Object ? "draft" : State("ongoing").Field("queryStage").Text("phase", "none");
    public bool NativeAvailable => State("app").Field("nativeSupport").Field("nativeInput").Boolean("available");
    public string NativeUnavailableReason
    {
        get
        {
            var native = State("app").Field("nativeSupport").Field("nativeInput");
            return !native.Boolean("availableOnCurrentPlatform") ? "unsupported" : native.Boolean("requiresElevation") && !State("app").Boolean("isElevated") ? "needAdmin" : "unavailable";
        }
    }
    public string? SendDisabledReason => !Ready || Refreshing ? "loading" : Busy ? "busy" : Phase == "draft" ? "draftOnly" : Phase == "in-game" && !NativeAvailable ? "nativeInput" : Phase is not ("in-game" or "champ-select" or "lobby") ? "unavailable" : null;
    public int TotalCount => Teams.Sum(team => team.Players.Length);
    public int TotalGroupCount => Teams.Sum(team => team.Buckets.Length);
    public int SelectedCount => Kind == "premade" ? Teams.Sum(team => team.Buckets.Count(bucket => SelectedGroups.Contains(bucket.Index))) : Teams.Sum(team => team.Players.Count(player => SelectedPlayers.Contains(player.Puuid)));
    public event Action? Changed;
    public ToolkitPresetController(string kind, Func<string, string, object?[], Task<JsonElement>> call) { _ = DisplayFields(kind); Kind = kind; _call = call; }
    private JsonElement State(string key) => _states.GetValueOrDefault(key);
    public void Activate() { if (_active) return; _active = true; _lifetime.Dispose(); _lifetime = new(); ++_generation; _loaded.Clear(); }
    public void Deactivate() { _active = false; _lifetime.Cancel(); ++_generation; ++_preview; Busy = Refreshing = PreviewLoading = false; SentTarget = null; }
    private bool Current(int generation) => _active && generation == _generation;
    private Task<JsonElement> Call(string ns, string method, object?[] args) => _call(ns, method, args).WaitAsync(_lifetime.Token);
    private void Store(string resource, JsonElement state)
    {
        _states[resource] = state.Clone(); _versions[resource] = _versions.GetValueOrDefault(resource) + 1;
        Rebuild();
    }
    private static int TeamSort(string id) => id == "TEAM-100" ? 100 : id == "TEAM-200" ? 200 : id.StartsWith("CHERRY-") && int.TryParse(id[7..], out int number) ? 1000 + number : int.MaxValue;
    private void Rebuild()
    {
        var ongoing = State("ongoing"); string self = State("me").Field("me").Text("puuid"); var rawTeams = ongoing.Field("teams");
        string? selfTeam = self.Length > 0 && rawTeams.ValueKind == JsonValueKind.Object ? rawTeams.EnumerateObject().Where(team => team.Value.Items().Any(id => id.ValueKind == JsonValueKind.String && id.GetString() == self)).Select(team => team.Name).FirstOrDefault() : null;
        var summoners = State("data").Field("summoner"); var champions = ongoing.Field("championSelections"); var premades = ongoing.Field("mergedPremadeTeamMap");
        Teams = rawTeams.ValueKind != JsonValueKind.Object ? [] : rawTeams.EnumerateObject().Select(team =>
        {
            var players = team.Value.Items().Where(id => id.ValueKind == JsonValueKind.String).Select(id =>
            {
                string puuid = id.GetString()!; var summoner = summoners.Field(puuid); var champion = champions.Field(puuid); var group = premades.Field(puuid);
                return new ToolkitPresetPlayer(puuid, summoner.Text("gameName", summoner.Text("displayName", puuid[..Math.Min(puuid.Length, 6)])), summoner.Text("tagLine"), (int)summoner.Number("profileIconId", 29), champion.ValueKind == JsonValueKind.Number ? (int)champion.TryNumber() : null, group.ValueKind == JsonValueKind.Number && group.TryNumber() > 0 ? (int)group.TryNumber() : null);
            }).ToArray();
            var buckets = players.Where(player => player.PremadeGroup.HasValue).GroupBy(player => player.PremadeGroup!.Value).OrderBy(group => group.Key).Where(group => group.Count() > 1).Select(group => new ToolkitPresetBucket(group.Key, group.ToArray())).ToArray();
            return new ToolkitPresetTeam(team.Name, string.IsNullOrEmpty(selfTeam) ? null : team.Name == selfTeam, players, buckets);
        }).Where(team => team.Players.Length > 0).OrderBy(team => team.Id == selfTeam ? 0 : 1).ThenBy(team => TeamSort(team.Id)).ThenBy(team => team.Id, StringComparer.Ordinal).ToArray();
        string context = Phase + "|" + ongoing.Field("queryStage").Field("gameInfo").ToString() + "|" + string.Join(";", Teams.Select(team => team.Id + ":" + string.Join(",", team.Players.Select(player => player.Puuid))));
        if (_context != context) { _context = context; InvalidatePreviewRequest(); SentTarget = null; }
    }
    public async Task RefreshAsync()
    {
        if (!_active) return;
        int generation = _generation, refresh = ++_refresh; Refreshing = true; RefreshError = null; Changed?.Invoke();
        var resources = new (string Key, string Namespace, string State)[] { ("settings", "in-game-send-main", "settings"), ("selection", "in-game-send-main", "state"), ("ongoing", "ongoing-game-main", "state"), ("app", "app-common-main", "state"), ("me", "league-client-main", "summoner") };
        var versions = resources.Select(resource => _versions.GetValueOrDefault(resource.Key)).Append(_versions.GetValueOrDefault("data")).ToArray();
        try
        {
            var reads = resources.Select(resource => Call("winui-backend", "snapshot", [resource.Namespace, resource.State])).Append(Call("ongoing-game-main", "getAll", [])).ToArray();
            var values = await Task.WhenAll(reads);
            if (!Current(generation) || refresh != _refresh) return;
            for (int index = 0; index < values.Length; ++index)
            {
                string key = index == resources.Length ? "data" : resources[index].Key;
                _states[key] = WithNewerEvents(key, values[index], versions[index]); _loaded.Add(key);
            }
            Rebuild();
        }
        catch (OperationCanceledException) { }
        catch (Exception failure) { if (Current(generation) && refresh == _refresh) RefreshError = failure; }
        finally { if (Current(generation) && refresh == _refresh) { Refreshing = false; Changed?.Invoke(); } }
    }
    private static JsonElement SetProperty(JsonElement state, string path, JsonElement value)
    {
        string[] keys = path.Split('.', 2); var values = state.ValueKind == JsonValueKind.Object ? state.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone()) : new Dictionary<string, JsonElement>();
        values[keys[0]] = keys.Length == 1 ? value.Clone() : SetProperty(values.GetValueOrDefault(keys[0]), keys[1], value);
        return JsonSerializer.SerializeToElement(values);
    }
    private JsonElement WithNewerEvents(string resource, JsonElement value, int version)
    {
        if (_eventPatches.TryGetValue(resource, out var patches))
            foreach (var patch in patches.Where(patch => patch.Value.Version > version).OrderBy(patch => patch.Value.Version)) value = SetProperty(value, patch.Key, patch.Value.Value);
        return value.Clone();
    }
    private void EventProperty(string resource, string path, JsonElement value)
    {
        Store(resource, SetProperty(State(resource), path, value));
        if (!_eventPatches.TryGetValue(resource, out var patches)) _eventPatches[resource] = patches = [];
        patches[path] = (_versions[resource], value.Clone());
    }
    public bool ApplyEvent(JsonElement envelope)
    {
        if (!_active) return false;
        string name = envelope.Text("name"); var args = envelope.Field("args").Items().ToArray();
        string? resource = name switch
        {
            "update-state-prop/in-game-send-main:settings" => "settings", "update-state-prop/in-game-send-main:state" => "selection",
            "update-state-prop/ongoing-game-main:state" => "ongoing", "update-state-prop/app-common-main:state" => "app", "update-state-prop/league-client-main:summoner" => "me", _ => null
        };
        if (resource is not null && args.Length >= 2 && args[0].ValueKind == JsonValueKind.String)
        {
            string path = args[0].GetString()!;
            string root = path.Split('.')[0];
            if (resource == "settings" && root != Kind + "PresetOptions" || resource == "selection" && root != SelectionKey
                || resource == "ongoing" && root is not ("teams" or "championSelections" or "mergedPremadeTeamMap" or "queryStage" or "draft")
                || resource == "app" && root is not ("nativeSupport" or "isElevated") || resource == "me" && root != "me") return false;
            EventProperty(resource, path, args[1]);
            if (resource == "settings" && path.StartsWith(Kind + "PresetOptions") || resource == "selection" && path == SelectionKey) InvalidatePreviewRequest();
            Changed?.Invoke(); return true;
        }
        if (name is "ongoing-game-main/summoner-loaded" or "ongoing-game-main/summoner-updated" && args.Length >= 2 && args[0].ValueKind == JsonValueKind.String)
        { EventProperty("data", "summoner." + args[0].GetString(), args[1]); Changed?.Invoke(); return true; }
        if (name == "ongoing-game-main/summoner-removed" && args.Length >= 1 && args[0].ValueKind == JsonValueKind.String)
        { EventProperty("data", "summoner." + args[0].GetString(), JsonSerializer.SerializeToElement<object?>(null)); Changed?.Invoke(); return true; }
        if (name == "ongoing-game-main/clear") { EventProperty("data", "summoner", JsonSerializer.SerializeToElement(new { })); ClosePreview(); return true; }
        if (name == "in-game-send-main/shortcut-error" && args.Length >= 2 && args[0].ValueKind == JsonValueKind.String && args[0].GetString()!.StartsWith("in-game-send-main/preset/" + Kind + "/"))
        { ShortcutError = args[1].ToString(); Changed?.Invoke(); return true; }
        return false;
    }
    private async Task<bool> Execute(Func<int, Task<bool>> operation)
    {
        if (!_active || Busy || Refreshing || !Ready) return false;
        int generation = _generation; Busy = true; Error = null; SentTarget = null; Changed?.Invoke();
        try { return await operation(generation); }
        catch (OperationCanceledException) { return false; }
        catch (Exception failure) { if (Current(generation)) Error = failure; return false; }
        finally { if (Current(generation)) { Busy = false; Changed?.Invoke(); } }
    }
    public Task<bool> UpdateOptionsAsync(IReadOnlyDictionary<string, object?> patch) => Execute(generation => UpdateOptionsCoreAsync(patch, generation));
    private async Task<bool> UpdateOptionsCoreAsync(IReadOnlyDictionary<string, object?> patch, int generation)
    {
        int version = _versions.GetValueOrDefault("settings");
        await Call("in-game-send-main", "update" + Suffix + "Options", [patch]);
        if (!Current(generation)) return false;
        if (_versions.GetValueOrDefault("settings") == version)
        {
            var options = Options;
            foreach (var entry in patch)
                if (entry.Key == "targetShortcuts")
                {
                    var shortcuts = JsonSerializer.SerializeToElement(entry.Value);
                    foreach (var shortcut in shortcuts.EnumerateObject()) options = SetProperty(options, "targetShortcuts." + shortcut.Name, shortcut.Value);
                }
                else options = SetProperty(options, entry.Key, JsonSerializer.SerializeToElement(entry.Value));
            EventProperty("settings", Kind + "PresetOptions", options);
        }
        InvalidatePreviewRequest(); return true;
    }
    public Task<bool> SetShortcutAsync(string target, string? shortcut) => !Targets.Contains(target) ? Task.FromResult(false) : Execute(async generation =>
    {
        if (!NativeAvailable) throw new InvalidOperationException("preset-shortcut-unavailable");
        ShortcutError = null;
        if (!string.IsNullOrEmpty(shortcut))
        {
            var registration = await Call("keyboard-shortcuts-main", "getRegistration", [shortcut]);
            if (!Current(generation)) return false;
            if (!NativeAvailable) throw new InvalidOperationException("preset-shortcut-unavailable");
            string occupied = registration.Text("targetId");
            if (occupied.Length > 0 && occupied != "in-game-send-main/preset/" + Kind + "/" + target)
                throw new InvalidOperationException(occupied == "akari-disabled-keys" ? "preset-shortcut-reserved" : "preset-shortcut-occupied");
        }
        return await UpdateOptionsCoreAsync(new Dictionary<string, object?> { ["targetShortcuts"] = new Dictionary<string, string?> { [target] = shortcut } }, generation);
    });
    public Task<bool> SetSelectionAsync(IEnumerable<string> players, IEnumerable<int>? groups = null) => Execute(async generation =>
    {
        var wantedPlayers = players.ToHashSet(); var wantedGroups = (groups ?? []).ToHashSet();
        object values = Kind == "premade" ? Teams.SelectMany(team => team.Buckets).Select(bucket => bucket.Index).Where(wantedGroups.Contains).Distinct().ToArray() : Teams.SelectMany(team => team.Players).Select(player => player.Puuid).Where(wantedPlayers.Contains).Distinct().ToArray();
        int version = _versions.GetValueOrDefault("selection");
        await Call("in-game-send-main", Kind == "premade" ? "setPremadeIndices" : "set" + char.ToUpperInvariant(Kind[0]) + Kind[1..] + "Puuids", [values]);
        if (!Current(generation)) return false;
        if (_versions.GetValueOrDefault("selection") == version) EventProperty("selection", SelectionKey, JsonSerializer.SerializeToElement(values));
        InvalidatePreviewRequest(); return true;
    });
    public Task<bool> SetAllAsync(bool selected) => SetSelectionAsync(selected ? Teams.SelectMany(team => team.Players).Select(player => player.Puuid) : [], selected ? Teams.SelectMany(team => team.Buckets).Select(bucket => bucket.Index) : []);
    public Task<bool> SetTeamAsync(string id, bool selected)
    {
        var team = Teams.FirstOrDefault(team => team.Id == id); if (team is null) return Task.FromResult(false);
        return SetSelectionAsync(selected ? SelectedPlayers.Concat(team.Players.Select(player => player.Puuid)) : SelectedPlayers.Except(team.Players.Select(player => player.Puuid)),
            selected ? SelectedGroups.Concat(team.Buckets.Select(bucket => bucket.Index)) : SelectedGroups.Except(team.Buckets.Select(bucket => bucket.Index)));
    }
    public Task<bool> SetPlayerAsync(string puuid, bool selected) => SetSelectionAsync(selected ? SelectedPlayers.Append(puuid) : SelectedPlayers.Where(id => id != puuid));
    public Task<bool> SetBucketAsync(int index, bool selected) => SetSelectionAsync([], selected ? SelectedGroups.Append(index) : SelectedGroups.Where(group => group != index));
    public async Task<bool> DryRunAsync(string target)
    {
        if (!_active || !Ready || Busy || Refreshing || !Targets.Contains(target)) return false;
        int generation = _generation, preview = ++_preview; PreviewLoading = true; Error = null; Changed?.Invoke();
        try
        {
            var lines = await Call("in-game-send-main", "generate" + Suffix + "Lines", [target]);
            if (!Current(generation) || preview != _preview) return false;
            Preview = new(target, lines.Items().Where(line => line.ValueKind == JsonValueKind.String).Select(line => line.GetString()!).ToArray()); Changed?.Invoke(); return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception failure) { if (Current(generation) && preview == _preview) { Error = failure; Changed?.Invoke(); } return false; }
        finally { if (Current(generation) && preview == _preview) { PreviewLoading = false; Changed?.Invoke(); } }
    }
    private void InvalidatePreviewRequest() { ++_preview; PreviewLoading = false; }
    public void ClosePreview() { InvalidatePreviewRequest(); Preview = null; Changed?.Invoke(); }
    public Task<bool> SendAsync(string target) => !Targets.Contains(target) ? Task.FromResult(false) : Execute(async generation =>
    {
        int ongoingVersion = _versions.GetValueOrDefault("ongoing");
        var ongoing = await Call("winui-backend", "snapshot", ["ongoing-game-main", "state"]); if (!Current(generation)) return false;
        Store("ongoing", WithNewerEvents("ongoing", ongoing, ongoingVersion));
        int appVersion = _versions.GetValueOrDefault("app");
        var app = await Call("winui-backend", "snapshot", ["app-common-main", "state"]); if (!Current(generation)) return false;
        Store("app", WithNewerEvents("app", app, appVersion));
        if (Phase == "draft" || Phase is not ("in-game" or "champ-select" or "lobby") || Phase == "in-game" && !NativeAvailable) throw new InvalidOperationException("preset-send-unavailable");
        var sent = await Call("in-game-send-main", "send" + Suffix, [target]); if (!Current(generation)) return false;
        if (sent.ValueKind != JsonValueKind.True) throw new InvalidOperationException("preset-send-rejected"); SentTarget = target; return true;
    });
}

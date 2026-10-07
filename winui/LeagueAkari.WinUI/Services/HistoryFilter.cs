using System.Text.Json;
using System.Text.Json.Serialization;

namespace LeagueAkari.WinUI.Services;

public sealed class HistoryFilterSettings
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = "simple";
    [JsonPropertyName("simple")] public SimpleHistoryFilterState Simple { get; set; } = new();
    [JsonPropertyName("advanced")] public HistoryFilterState Advanced { get; set; } = new();
    [JsonPropertyName("collectEnabled")] public bool CollectEnabled { get; set; }
    [JsonPropertyName("enablePosition")] public bool EnablePosition { get; set; }
}
public sealed class SimpleHistoryFilterState
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("winLoss")] public string WinLoss { get; set; } = "all";
    [JsonPropertyName("timeRange")] public string TimeRange { get; set; } = "all";
    [JsonPropertyName("positions")] public List<string> Positions { get; set; } = new();
    [JsonPropertyName("championIds")] public List<int> ChampionIds { get; set; } = new();
    [JsonPropertyName("summonerPuuids")] public List<string> SummonerPuuids { get; set; } = new();
    [JsonPropertyName("cachedSummoners")] public Dictionary<string, JsonElement> CachedSummoners { get; set; } = new();
}
public sealed class HistoryFilterState
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("rootId")] public string RootId { get; set; } = "root";
    [JsonPropertyName("nodeMap")] public Dictionary<string, HistoryFilterNode> NodeMap { get; set; } = new() { ["root"] = new() { Id = "root", Type = "game", Args = [HistoryFilterArg.Node(null)] } };
    [JsonPropertyName("cachedSummoners")] public Dictionary<string, JsonElement> CachedSummoners { get; set; } = new();
    public HistoryFilterNode Add(string type, string parent, params HistoryFilterArg[] args)
    {
        var node = new HistoryFilterNode { Id = type + "-" + Guid.NewGuid().ToString("N"), Type = type, ParentId = parent, Args = args.ToList(), ArgDeleteStrategy = type is "and" or "or" ? "remove-from-array" : null };
        NodeMap[node.Id] = node; return node;
    }
    public void Remove(string id)
    {
        if (id == RootId) { Clear(); return; }
        if (!NodeMap.TryGetValue(id, out var node)) return;
        foreach (var child in node.Args.Where(a => a.Kind == "node").ToArray()) if (child.StringValue is { } childId) Remove(childId);
        if (node.ParentId is { } parentId && NodeMap.TryGetValue(parentId, out var parent))
            if (parent.ArgDeleteStrategy == "remove-from-array") parent.Args.RemoveAll(a => a.Kind == "node" && a.StringValue == id);
            else foreach (var arg in parent.Args.Where(a => a.Kind == "node" && a.StringValue == id)) arg.Value = JsonSerializer.SerializeToElement<string?>(null);
        NodeMap.Remove(id);
    }
    public void Clear()
    {
        if (NodeMap.TryGetValue(RootId, out var root))
        {
            foreach (var child in root.Args.Where(a => a.Kind == "node").ToArray())
                if (child.StringValue is { } id) Remove(id);
            root.Args = [HistoryFilterArg.Node(null)];
        }
        else NodeMap[RootId] = new() { Id = RootId, Type = "game", Args = [HistoryFilterArg.Node(null)] };
        CachedSummoners.Clear();
    }
}
public sealed class HistoryFilterNode
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("args")] public List<HistoryFilterArg> Args { get; set; } = new();
    [JsonPropertyName("parentId")] public string? ParentId { get; set; }
    [JsonPropertyName("argDeleteStrategy")] public string? ArgDeleteStrategy { get; set; }
}
public sealed class HistoryFilterArg
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "param";
    [JsonPropertyName("value")] public JsonElement Value { get; set; }
    [JsonIgnore] public string? StringValue => Value.ValueKind == JsonValueKind.String ? Value.GetString() : null;
    public static HistoryFilterArg Param(object? value) => new() { Value = JsonSerializer.SerializeToElement(value) };
    public static HistoryFilterArg Node(string? id) => new() { Kind = "node", Value = JsonSerializer.SerializeToElement(id) };
}
public sealed record HistoryFilterSpec(string Type, string Label, string[] Require, string? Provide = null);

public sealed class HistoryFilter
{
    private readonly HistoryFilterSettings _settings;
    public HistoryFilter(HistoryFilterSettings settings) => _settings = settings;
    public static readonly Dictionary<string, string> NumberStats = new()
    {
        ["kdaBetween"] = "kda", ["levelBetween"] = "champLevel", ["killsBetween"] = "kills", ["deathsBetween"] = "deaths", ["assistsBetween"] = "assists", ["csBetween"] = "cs", ["goldBetween"] = "goldEarned", ["goldSpentBetween"] = "goldSpent", ["killParticipationBetween"] = "killParticipation", ["dgrBetween"] = "damageGoldEfficiency", ["damageDealtToChampionsBetween"] = "totalDamageDealtToChampions", ["physicalDamageDealtToChampionsBetween"] = "physicalDamageDealtToChampions", ["magicDamageDealtToChampionsBetween"] = "magicDamageDealtToChampions", ["trueDamageDealtToChampionsBetween"] = "trueDamageDealtToChampions", ["damageTakenBetween"] = "totalDamageTaken", ["physicalDamageTakenBetween"] = "physicalDamageTaken", ["magicDamageTakenBetween"] = "magicDamageTaken", ["trueDamageTakenBetween"] = "trueDamageTaken", ["damageToTowersBetween"] = "totalDamageToTowers", ["healBetween"] = "totalHeal", ["visionScoreBetween"] = "visionScore", ["timeCCingOthersBetween"] = "timeCCingOthers", ["soloKillsBetween"] = "soloKills", ["doubleKillsBetween"] = "doubleKills", ["tripleKillsBetween"] = "tripleKills", ["quadraKillsBetween"] = "quadraKills", ["pentaKillsBetween"] = "pentaKills"
    };
    public static readonly HistoryFilterSpec[] Specs = BuildSpecs();
    private static HistoryFilterSpec[] BuildSpecs()
    {
        string[] any = ["game", "participant", "participants"], game = ["game"], person = ["participant"], members = ["game", "participants"];
        var list = new List<HistoryFilterSpec> { new("and", "同时满足 AND", any), new("or", "任一满足 OR", any), new("not", "不满足 NOT", any), new("player", "指定玩家", members, "participant"), new("anyone", "任意玩家", members, "participant"), new("everyone", "所有玩家", members, "participant"), new("allies", "指定玩家的队伍", game, "participants"), new("enemies", "指定玩家的对手", game, "participants"), new("all", "全体玩家", game, "participants"), new("hasPlayer", "包含玩家", members), new("isQueue", "队列", game), new("isGameMode", "游戏模式", game), new("isMap", "地图", game), new("isMatchedGame", "匹配对局", game), new("isPveGame", "PVE 对局", game), new("gameCreationInTimeRange", "对局时间", game), new("durationBetween", "时长（秒）", game), new("isAbort", "中止", any), new("isRemake", "重开", any), new("isWin", "胜利", ["participant", "participants"]), new("isLoss", "失败", ["participant", "participants"]), new("isChampion", "英雄", person), new("isPosition", "位置（SGP）", person), new("hasItem", "装备", person), new("hasSpell", "召唤师技能", person), new("hasPerk", "符文", person), new("hasPerkStyle", "符文系", person), new("hasAugment", "强化符文", person) };
        string[] labels = ["KDA", "等级", "击杀", "死亡", "助攻", "补刀", "获得金币", "花费金币", "参团率 %", "伤害金币效率 %", "英雄伤害", "物理英雄伤害", "魔法英雄伤害", "真实英雄伤害", "承受伤害", "承受物理伤害", "承受魔法伤害", "承受真实伤害", "防御塔伤害", "治疗", "视野得分", "控制时长", "单杀（SGP）", "双杀", "三杀", "四杀", "五杀"];
        int i = 0; foreach (var key in NumberStats.Keys) list.Add(new(key, labels[i++], person)); return list.ToArray();
    }
    private sealed record Scope(JsonElement Game, MatchParticipant[] All, MatchParticipant[] Members, MatchParticipant? Participant, string Kind);
    public bool Match(JsonElement game, string puuid)
    {
        game = MatchData.Game(game); var all = MatchData.Participants(game); var scope = new Scope(game, all, all, null, "game");
        if (_settings.Mode == "advanced") return Evaluate(_settings.Advanced.RootId, _settings.Advanced, scope, new HashSet<string>());
        var simple = _settings.Simple; var self = all.FirstOrDefault(p => p.Puuid == puuid);
        if (!TimeMatches(game, simple.TimeRange)) return false;
        if (simple.WinLoss != "all" && (self is null || self.WinResult != (simple.WinLoss == "win" ? "win" : "loss"))) return false;
        if (_settings.EnablePosition && simple.Positions.Count > 0 && (self is null || !simple.Positions.Contains(Position(self)))) return false;
        return simple.ChampionIds.All(id => all.Any(p => p.ChampionId == id)) && simple.SummonerPuuids.All(id => all.Any(p => p.Puuid == id));
    }
    private static bool Evaluate(string id, HistoryFilterState state, Scope scope, HashSet<string> path)
    {
        if (!path.Add(id)) throw new InvalidOperationException("筛选规则存在循环引用");
        try
        {
            if (!state.NodeMap.TryGetValue(id, out var node)) throw new InvalidOperationException("筛选节点不存在：" + id);
            if (node.Type != "game" && !Specs.Any(s => s.Type == node.Type && s.Require.Contains(scope.Kind))) throw new InvalidOperationException("筛选规则不适用于当前范围：" + node.Type);
            if (node.Args.Any(a => a.Kind == "node" && a.StringValue is null)) return true; // 未完成的条件与原版一致，暂时视为真。
            JsonElement Param(int n) => node.Args.Count > n ? node.Args[n].Value : default;
            string? Text(int n) => Param(n).ValueKind == JsonValueKind.String ? Param(n).GetString() : null;
            double Number(int n, double fallback = 0) => Param(n).TryNumber(fallback);
            bool Child(Scope target, int index) => Evaluate(node.Args[index].StringValue!, state, target, path);
            MatchParticipant? Selected() => scope.Members.FirstOrDefault(p => p.Puuid == Text(0));
            switch (node.Type)
            {
                case "game": return Child(scope with { Kind = "game" }, 0);
                case "and": return node.Args.All(a => Evaluate(a.StringValue!, state, scope, path));
                case "or": return node.Args.Count == 0 || node.Args.Any(a => Evaluate(a.StringValue!, state, scope, path));
                case "not": return !Child(scope, 0);
                case "all": return Child(scope with { Kind = "participants", Members = scope.All }, 0);
                case "player": return Text(0) is null || Selected() is { } selected && Child(scope with { Kind = "participant", Participant = selected }, 1);
                case "hasPlayer": return Text(0) is null || Selected() is not null;
                case "allies": case "enemies":
                    if (Text(0) is null) return true;
                    var reference = Selected(); if (reference is null) return false;
                    return Child(scope with { Kind = "participants", Members = scope.All.Where(p => (p.TeamKey == reference.TeamKey) == (node.Type == "allies")).ToArray() }, 1);
                case "anyone": return scope.Members.Any(p => Child(scope with { Kind = "participant", Participant = p }, 0));
                case "everyone": return scope.Members.All(p => Child(scope with { Kind = "participant", Participant = p }, 0));
                case "isQueue": return scope.Game.Number("queueId") == Number(0);
                case "isMap": return scope.Game.Number("mapId") == Number(0);
                case "isGameMode": return scope.Game.Text("gameMode") == Text(0);
                case "isMatchedGame": return scope.Game.Text("gameType") == "MATCHED_GAME";
                case "isPveGame": return MatchData.IsPveQueue((int)scope.Game.Number("queueId"));
                case "gameCreationInTimeRange": return TimeMatches(scope.Game, Text(0));
                case "durationBetween": return Between(scope.Game.Number("gameDuration"), Number(0), Number(1, double.PositiveInfinity));
                case "isAbort": case "isRemake": case "isWin": case "isLoss":
                    var resultPlayer = scope.Participant ?? scope.Members.FirstOrDefault(); if (resultPlayer is null) return false;
                    string result = node.Type switch { "isAbort" => "abort", "isRemake" => "remake", "isWin" => "win", _ => "loss" };
                    return resultPlayer.WinResult == result && (node.Type != "isLoss" || resultPlayer.IsSurrender == (Param(0).ValueKind == JsonValueKind.True));
                case "isChampion": return scope.Participant!.ChampionId == Number(0);
                case "isPosition": return Position(scope.Participant!) == Text(0);
                case "hasItem": case "hasSpell": case "hasAugment": case "hasPerk": case "hasPerkStyle":
                    if (node.Type == "hasAugment" && Param(0).ValueKind == JsonValueKind.Null) return true;
                    var p = scope.Participant!; int value = (int)Number(0), order = (int)Number(1, -1);
                    int[] values = node.Type switch { "hasItem" => p.Items, "hasSpell" => p.Spells, "hasAugment" => p.Augments, "hasPerk" => p.Runes, _ => PerkStyles(p) };
                    return node.Type == "hasItem" && p.Num("roleBoundItem") == value || (order == -1 ? values.Contains(value) : order >= 0 && order < values.Length && values[order] == value);
            }
            if (!NumberStats.TryGetValue(node.Type, out string? stat)) throw new InvalidOperationException("未知筛选条件：" + node.Type);
            double? metric = Metric(scope.Participant!, scope.All, stat); if (metric is null) return false;
            bool measure = Param(0).ValueKind == JsonValueKind.String; string mode = measure ? Text(0)! : "value"; int offset = measure ? 1 : 0;
            if (node.Type is "killParticipationBetween" or "dgrBetween") metric *= 100;
            else if (mode != "value")
            {
                var related = mode.StartsWith("team", StringComparison.Ordinal) ? scope.All.Where(p => p.TeamKey == scope.Participant!.TeamKey) : scope.All;
                var metrics = related.Select(p => Metric(p, scope.All, stat)).Where(n => n.HasValue).Select(n => n!.Value).ToArray();
                double baseline = mode is "teamShare" or "gameShare" ? metrics.Sum() : metrics.Length == 0 ? 0 : metrics.Max(); metric = baseline == 0 ? 0 : metric / baseline * 100;
            }
            return Between(metric.Value, Number(offset), Number(offset + 1, double.PositiveInfinity));
        }
        finally { path.Remove(id); }
    }
    private static double? Metric(MatchParticipant p, MatchParticipant[] all, string key) => key switch
    {
        "killParticipation" => (p.Kills + p.Assists) / Math.Max(1, all.Where(a => a.TeamKey == p.TeamKey).Sum(a => a.Kills)),
        "damageGoldEfficiency" => p.Num("totalDamageDealtToChampions") / Math.Max(1, p.Num("goldEarned")),
        "soloKills" => p.Raw.Field("challenges").Field("soloKills").ValueKind == JsonValueKind.Number ? p.Raw.Field("challenges").Number("soloKills") : null,
        "champLevel" => p.Stats.Number("champLevel", p.Raw.Number("champLevel")), _ => p.Num(key)
    };
    private static string Position(MatchParticipant p) => p.Raw.Field("stats").ValueKind == JsonValueKind.Object ? "" : p.Raw.Text("teamPosition");
    private static int[] PerkStyles(MatchParticipant p) => p.Raw.Field("perks").Field("styles").ValueKind == JsonValueKind.Array ? p.Raw.Field("perks").Field("styles").Items().Select(s => (int)s.Number("style")).ToArray() : [(int)p.Num("perkPrimaryStyle"), (int)p.Num("perkSubStyle")];
    private static bool Between(double value, double min, double max) => value >= min && value <= max;
    private static bool TimeMatches(JsonElement game, string? range)
    {
        double hours = range switch { "last3Hours" => 3, "last12Hours" => 12, "last24Hours" => 24, "last3Days" => 72, "last7Days" => 168, "last30Days" => 720, _ => 0 };
        if (hours == 0) return true; var now = DateTimeOffset.UtcNow; var creation = MatchData.Creation(game); return creation >= now.AddHours(-hours) && creation <= now;
    }
    public static HistoryFilterState Preset(string name, string puuid)
    {
        var state = new HistoryFilterState(); var root = state.NodeMap["root"];
        HistoryFilterNode Add(HistoryFilterNode parent, string type, params object?[] args) { var child = state.Add(type, parent.Id, args.Select(HistoryFilterArg.Param).ToArray()); parent.Args.Add(HistoryFilterArg.Node(child.Id)); return child; }
        root.Args.Clear(); var and = Add(root, "and");
        if (name == "kiwi-jayce-slow-and-steady")
        {
            Add(and, "isQueue", 2400); var enemies = Add(and, "enemies", puuid); var anyone = Add(enemies, "anyone"); var person = Add(anyone, "and"); Add(person, "isChampion", 126); Add(person, "hasAugment", 1250, -1); return state;
        }
        Add(and, "isMatchedGame"); Add(and, "durationBetween", 600, 999999); var not = Add(and, "not"); var dirty = Add(not, "or"); Add(dirty, "isAbort"); Add(dirty, "isRemake"); Add(dirty, "isPveGame");
        if (name == "strong-self-performance")
        {
            var player = Add(and, "player", puuid); var conditions = Add(player, "and"); Add(conditions, "kdaBetween", 4, 999); Add(conditions, "deathsBetween", 0, 4); var contribution = Add(conditions, "or"); Add(contribution, "isWin"); Add(contribution, "killsBetween", 8, 999); Add(contribution, "assistsBetween", 12, 999); Add(contribution, "goldBetween", 12000, 999999);
        }
        return state;
    }
}


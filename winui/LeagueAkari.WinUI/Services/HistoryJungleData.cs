using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

/// <summary>Original single/jungle.ts, single/objectives.ts and aggregate/jungle.ts without UI or current-game state.</summary>
public static class HistoryJungleData
{
    private sealed class Sample
    {
        public required MatchParticipant Player;
        public double[] Weights = new double[3];
        public string? Camp, CampSide;
        public bool Level3, Level4;
        public bool? FirstDragon;
        public double? DragonTime, GrubTime, HeraldTime, BaronTime;
        public int Dragons, SoloDragons, Grubs, Heralds, Barons;
        public List<object> Minutes = [], Ganks = [], Level3Positions = [], Level4Positions = [];
        public Dictionary<string, int> GankCounts = new() { ["top"] = 0, ["mid"] = 0, ["bot"] = 0 };
    }
    private static readonly string[] Zones = ["top", "mid", "bot"];
    public static bool Eligible(JsonElement raw, string puuid)
    {
        var game = MatchData.Game(raw); var player = MatchData.Self(game, puuid);
        return game.Text("gameType") == "MATCHED_GAME" && !MatchData.IsPveQueue((int)game.Number("queueId"))
            && game.Number("mapId") == 11 && player is not null && player.WinResult is "win" or "loss"
            && (player.Position == "JUNGLE" || player.Spells.Contains(11));
    }
    public static JsonElement[] Frames(JsonElement timeline)
    {
        for (int i = 0; i < 5; i++)
        {
            if (timeline.Field("frames").ValueKind == JsonValueKind.Array) return timeline.Field("frames").Items().ToArray();
            if (timeline.Field("json").ValueKind == JsonValueKind.Object) { timeline = timeline.Field("json"); continue; }
            if (timeline.Field("data").ValueKind == JsonValueKind.Object) { timeline = timeline.Field("data"); continue; }
            break;
        }
        return [];
    }
    public static JsonElement Analyze(IEnumerable<JsonElement> games, string puuid, IReadOnlyDictionary<long, JsonElement> timelines)
    {
        var samples = new List<Sample>();
        foreach (var raw in games)
        {
            var game = MatchData.Game(raw); if (!Eligible(game, puuid)) continue;
            if (!timelines.TryGetValue((long)game.Number("gameId"), out var timeline)) continue;
            var frames = Frames(timeline); if (frames.Length == 0) continue;
            var player = MatchData.Self(game, puuid)!; if (player.Id <= 0) continue;
            samples.Add(AnalyzeSample(player, frames));
        }
        var champions = samples.Where(s => s.Player.ChampionId > 0).GroupBy(s => s.Player.ChampionId)
            .ToDictionary(g => g.Key.ToString(), g => new { jungle = Aggregate(g.ToArray()) });
        return JsonSerializer.SerializeToElement(new { jungle = Aggregate(samples.ToArray()), champions });
    }
    private static int Zone(double x, double y) => x < 5000 && y > 9000 ? 0 : x > 9000 && y < 5000 ? 2 : Math.Abs(y - x) <= 3500 ? 1 : y > x ? 0 : 2;
    private static string? GankLane(double x, double y) => x < 5000 && y > 9000 ? "top" : x > 9000 && y < 5000 ? "bot" : Math.Abs(y - x) < 4000 && (x + y) / 2 > 3000 && (x + y) / 2 < 12000 ? "mid" : null;
    private static JsonElement PlayerFrame(JsonElement[] frames, int minute, int player) => minute < frames.Length ? frames[minute].Field("participantFrames").Field(player.ToString()) : default;
    private static bool Detailed(JsonElement frame) => frame.Field("damageStats").ValueKind == JsonValueKind.Object && frame.Field("championStats").ValueKind == JsonValueKind.Object;
    private static bool Involved(JsonElement ev, int id) => ev.Number("killerId") == id || ev.Field("assistingParticipantIds").Items().Any(p => p.TryNumber() == id);
    private static object Point(JsonElement pos, string lane) => new { x = pos.Number("x"), y = pos.Number("y"), lane };
    private static Sample AnalyzeSample(MatchParticipant player, JsonElement[] frames)
    {
        var sample = new Sample { Player = player };
        for (int minute = 1; minute < Math.Min(frames.Length, 15); minute++)
        {
            var pos = PlayerFrame(frames, minute, player.Id).Field("position"); if (pos.ValueKind != JsonValueKind.Object) continue;
            int zone = Zone(pos.Number("x"), pos.Number("y")); sample.Weights[zone]++;
            sample.Minutes.Add(new { x = pos.Number("x"), y = pos.Number("y"), lane = Zones[zone], minute });
        }
        var start = PlayerFrame(frames, 1, player.Id).Field("position");
        if (start.ValueKind == JsonValueKind.Object)
        {
            var camps = new (double X, double Y, string Camp, string Side)[] { (3830, 7880, "blue", "blue"), (3800, 6440, "wolves", "blue"), (7760, 4010, "red", "blue"), (6970, 5460, "raptors", "blue"), (10990, 7000, "blue", "red"), (11020, 8440, "wolves", "red"), (7060, 10870, "red", "red"), (7850, 9420, "raptors", "red") };
            var camp = camps.MinBy(c => Math.Pow(start.Number("x") - c.X, 2) + Math.Pow(start.Number("y") - c.Y, 2));
            sample.Camp = camp.Camp; sample.CampSide = camp.Side;
        }
        foreach (var frame in frames)
            foreach (var ev in frame.Field("events").Items())
            {
                double time = ev.Number("timestamp");
                if (ev.Text("type") == "CHAMPION_KILL" && Involved(ev, player.Id))
                {
                    var pos = ev.Field("position"); double x = pos.Number("x"), y = pos.Number("y"); var lane = GankLane(x, y);
                    if (time <= 840000)
                    {
                        sample.Weights[Zone(x, y)] += 5;
                        if (lane is not null) { sample.GankCounts[lane]++; sample.Ganks.Add(Point(pos, lane)); }
                    }
                    if (time <= 180000) sample.Level3Positions.Add(Point(pos, lane ?? Zones[Zone(x, y)]));
                    else if (time <= 240000) sample.Level4Positions.Add(Point(pos, lane ?? Zones[Zone(x, y)]));
                }
                if (ev.Text("type") != "ELITE_MONSTER_KILL") continue;
                int killer = (int)ev.Number("killerId");
                int team = ev.Field("killerTeamId").ValueKind == JsonValueKind.Number ? (int)ev.Number("killerTeamId") : killer is >= 1 and <= 5 ? 100 : 200;
                bool ours = team == player.TeamId; double seconds = time / 1000;
                switch (ev.Text("monsterType"))
                {
                    case "DRAGON":
                        sample.FirstDragon ??= ours;
                        if (ours) { sample.Dragons++; if (killer == player.Id && !ev.Field("assistingParticipantIds").Items().Any()) sample.SoloDragons++; sample.DragonTime ??= seconds; } break;
                    case "HORDE": if (ours) { sample.Grubs++; sample.GrubTime ??= seconds; } break;
                    case "RIFTHERALD": if (ours) { sample.Heralds++; sample.HeraldTime ??= seconds; } break;
                    case "BARON_NASHOR": if (ours) { sample.Barons++; sample.BaronTime ??= seconds; } break;
                }
            }
        var third = PlayerFrame(frames, 3, player.Id);
        if (third.ValueKind == JsonValueKind.Object)
        {
            double cs = third.Number("minionsKilled") + third.Number("jungleMinionsKilled");
            double thirdDamage = Detailed(third) ? third.Field("damageStats").Number("totalDamageDoneToChampions") : 0;
            sample.Level3 = cs >= 12 && cs < 20 && third.Number("level") == 3 && (Detailed(third) ? thirdDamage > 0 : sample.Level3Positions.Count > 0);
            var fourth = PlayerFrame(frames, 4, player.Id);
            if (fourth.ValueKind == JsonValueKind.Object) sample.Level4 = Detailed(fourth)
                ? fourth.Field("damageStats").Number("totalDamageDoneToChampions") > thirdDamage || sample.Level4Positions.Count > 0
                : sample.Level4Positions.Count > 0;
        }
        return sample;
    }
    private static object? Aggregate(Sample[] samples)
    {
        if (samples.Length == 0) return null;
        double count = samples.Length; double Share(double numerator, double denominator) => denominator == 0 ? 0 : numerator / denominator;
        double? AverageTime(Func<Sample, double?> get) { var values = samples.Select(get).Where(v => v.HasValue).Select(v => v!.Value).ToArray(); return values.Length == 0 ? null : values.Average(); }
        var camps = new Dictionary<string, object>();
        foreach (string side in new[] { "blue", "red" })
        {
            var sideSamples = samples.Where(s => (s.Player.TeamId == 100 ? "blue" : "red") == side && s.Camp is not null).ToArray(); camps[side + "Games"] = sideSamples.Length;
            foreach (bool invade in new[] { false, true }) camps[side + (invade ? "Invade" : "")] = new[] { "blue", "red", "wolves", "raptors" }.ToDictionary(c => c, c => sideSamples.Count(s => s.Camp == c && (s.CampSide != side) == invade));
        }
        var byTeam = new Dictionary<string, object>();
        foreach (string side in new[] { "blue", "red" })
        {
            var sideSamples = samples.Where(s => (s.Player.TeamId == 100 ? "blue" : "red") == side).ToArray(); byTeam[side + "Games"] = sideSamples.Length;
            foreach (int level in new[] { 3, 4 })
            {
                int ganks = sideSamples.Count(s => level == 3 ? s.Level3 : s.Level4); string prefix = side + "Level" + level;
                byTeam[prefix + "GankCount"] = ganks; byTeam[prefix + "GankRate"] = Share(ganks, sideSamples.Length);
                byTeam[prefix + "KillPositions"] = sideSamples.SelectMany(s => level == 3 ? s.Level3Positions : s.Level4Positions).ToArray();
            }
        }
        double top = samples.Sum(s => s.Weights[0]), mid = samples.Sum(s => s.Weights[1]), bot = samples.Sum(s => s.Weights[2]), total = top + mid + bot;
        int dragons = samples.Sum(s => s.Dragons), firsts = samples.Count(s => s.FirstDragon.HasValue);
        var result = new Dictionary<string, object?>
        {
            ["gamesAnalyzed"] = count, ["topZoneWeightSum"] = top, ["midZoneWeightSum"] = mid, ["botZoneWeightSum"] = bot, ["totalZoneWeightSum"] = total,
            ["avgTopZonePercentage"] = Share(top, total), ["avgMidZonePercentage"] = Share(mid, total), ["avgBotZonePercentage"] = Share(bot, total), ["firstClearCamp"] = camps,
            ["minutePositions"] = samples.SelectMany(s => s.Minutes).ToArray(), ["gankPositions"] = samples.SelectMany(s => s.Ganks).ToArray(),
            ["earlyGank"] = new { byTeam, level3GankCount = samples.Count(s => s.Level3), level4GankCount = samples.Count(s => s.Level4), level3GankRate = samples.Count(s => s.Level3) / count, level4GankRate = samples.Count(s => s.Level4) / count, level3KillPositions = samples.SelectMany(s => s.Level3Positions).ToArray(), level4KillPositions = samples.SelectMany(s => s.Level4Positions).ToArray() },
            ["objectives"] = new { firstDragonRate = Share(samples.Count(s => s.FirstDragon == true), firsts), soloDragonRate = Share(samples.Sum(s => s.SoloDragons), dragons), avgDragons = dragons / count, avgVoidgrubs = samples.Sum(s => s.Grubs) / count, avgHeralds = samples.Sum(s => s.Heralds) / count, avgBarons = samples.Sum(s => s.Barons) / count, avgFirstDragonTime = AverageTime(s => s.DragonTime), avgFirstVoidgrubTime = AverageTime(s => s.GrubTime), avgFirstHeraldTime = AverageTime(s => s.HeraldTime), avgFirstBaronTime = AverageTime(s => s.BaronTime) }
        };
        foreach (string lane in Zones) { string title = char.ToUpperInvariant(lane[0]) + lane[1..]; int ganks = samples.Sum(s => s.GankCounts[lane]); result["total" + title + "Ganks"] = ganks; result["avg" + title + "Ganks"] = ganks / count; }
        return result;
    }
}

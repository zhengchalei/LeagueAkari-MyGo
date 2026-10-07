using System.Text.Json;
using System.Text.Json.Nodes;

// Isolated OP.GG protocol data for native GUI acceptance, never a production fallback.
internal sealed class FixtureOpgg
{
    private static readonly int[] Heroes = [103, 147, 17, 238, 22, 86, 99, 51, 64, 121];
    private static readonly int[] RuneIds = [8005, 9111, 9104, 8014, 8345, 8347, 5005, 5008, 5002];
    private static readonly int[] Items = [1001, 3006, 3031, 3085, 3072, 3363];
    private string _controlRevision = "";
    private readonly string? _controlFile;
    public FixtureOpgg(string? controlFile = null) => _controlFile = controlFile ?? Environment.GetEnvironmentVariable("WINUI_FIXTURE_OPGG_FAIL_CONTROL");

    public async Task<object> RequestAsync(string location)
    {
        var uri = new Uri(location);
        if (uri.Scheme != "https" || uri.Host != "lol-api-champion.op.gg") throw new NotSupportedException("Fixture supports only the OP.GG public host");
        await InjectAsync(uri.AbsolutePath);
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (uri.AbsolutePath == "/api/contents/aram-balance") return AramBalance;
        if (parts.Length == 6 && parts[0] == "api" && parts[1] == "contents" && parts[2] == "stats" && parts[3] == "champions" && parts[5] == "aram-augments")
        {
            int id = Hero(parts[4]);
            return new { data = Enumerable.Range(0, 24).Select(index => new { id = 9000 + index, tier = index % 7, performance = Math.Round(90 - index * 1.3 + Array.IndexOf(Heroes, id) * .1, 2), popular = index % 9 == 0 ? 0d : Math.Round(18 - index * .5, 2) }).ToArray() };
        }
        if (parts.Length < 4 || parts[0] != "api" || parts[2] != "champions") throw new NotSupportedException("Unsupported fixture OP.GG path: " + uri.AbsolutePath);
        string mode = parts[3];
        if (mode is not ("ranked" or "aram" or "arena" or "nexus_blitz" or "urf")) throw new NotSupportedException("Unsupported fixture OP.GG mode: " + mode);
        if (parts.Length == 5 && parts[4] == "versions") return new { data = new[] { "16.19", "16.18" } };
        string version = Query(uri, "version") ?? "16.19";
        var meta = new { version, cached_at = "2026-10-07 10:07:36" };
        if (mode == "arena" && Query(uri, "tier") is not null) throw new InvalidOperationException("Arena requests must omit tier");
        if (parts.Length == 4) return new { data = Heroes.Select((hero, index) => Summary(hero, index, mode)).ToArray(), meta };
        if (parts.Length is not (5 or 6)) throw new NotSupportedException("Unsupported fixture OP.GG detail path: " + uri.AbsolutePath);
        int champion = Hero(parts[4]);
        if (mode == "arena" && parts.Length != 5 || mode != "arena" && parts.Length != 6) throw new InvalidOperationException("Fixture role suffix differs from OP.GG contract");
        string position = parts.Length == 6 ? parts[5] : "none";
        if (mode == "aram" && position != "none") throw new InvalidOperationException("ARAM requires none role");
        return new
        {
            data = new
            {
                summary = Summary(champion, Array.IndexOf(Heroes, champion), mode),
                counters = Heroes.Where(hero => hero != champion).Select((hero, index) => new { champion_id = hero, play = 1000 + index * 100, win = 400 + index * 80 }).ToArray(),
                synergies = mode == "arena" ? Heroes.Where(hero => hero != champion).Select((hero, index) => new { champion_id = hero, play = 1000, win = 600 - index * 10, total_place = 2500 + index * 100, first_place = 250 - index * 10, pick_rate = .2 - index * .01 }).ToArray() : [],
                summoner_spells = Enumerable.Range(0, 4).Select(index => BuildRow([4, 14], index)).ToArray(),
                runes = mode == "arena" ? Array.Empty<object>() : Enumerable.Range(0, 4).Select(index => (object)new { primary_page_id = 8000, secondary_page_id = 8300, primary_rune_ids = RuneIds.Take(4).ToArray(), secondary_rune_ids = RuneIds.Skip(4).Take(2).ToArray(), stat_mod_ids = RuneIds.Skip(6).ToArray(), play = 1000 + index * 100, win = 500 + index * 40, pick_rate = .45 - index * .1 }).ToArray(),
                skill_masteries = Enumerable.Range(0, 4).Select(index => new { ids = index % 2 == 0 ? new[] { "Q", "W", "E" } : new[] { "Q", "E", "W" }, play = 1000 + index * 100, win = 550 + index * 40, pick_rate = .45 - index * .1, builds = new[] { new { order = new[] { "Q", "W", "E", "Q", "Q", "R", "Q", "W", "Q", "W", "R", "W", "W", "E", "E", "R", "E", "E" }, play = 1000, win = 550, pick_rate = .6 } } }).ToArray(),
                starter_items = Enumerable.Range(0, 6).Select(index => BuildRow([Items[index % Items.Length], 1001], index)).ToArray(),
                boots = Enumerable.Range(0, 6).Select(index => BuildRow([Items[index % Items.Length]], index)).ToArray(),
                prism_items = mode == "arena" ? Enumerable.Range(0, 6).Select(index => BuildRow([Items[index % Items.Length]], index)).ToArray() : [],
                core_items = Enumerable.Range(0, 6).Select(index => BuildRow([3031, Items[index % Items.Length], 3072], index)).ToArray(),
                last_items = Enumerable.Range(0, 10).Select(index => BuildRow([Items[index % Items.Length]], index)).ToArray(),
                augment_group = mode == "arena" ? new[] { 1, 4, 8 }.Select((rarity, group) => new { rarity, augments = Enumerable.Range(0, 7).Select(index => new { id = 9000 + group * 8 + index, play = 1000, win = 600 - index * 15, pick_rate = .3 - index * .03 }).ToArray() }).ToArray() : []
            }, meta
        };
    }
    private async Task InjectAsync(string path)
    {
        if (string.IsNullOrEmpty(_controlFile) || !File.Exists(_controlFile)) return;
        string revision = await File.ReadAllTextAsync(_controlFile); if (revision == _controlRevision) return;
        string mode = revision.Trim(), target = ""; int delay = 8000;
        if (mode.StartsWith('{'))
        {
            using var document = JsonDocument.Parse(revision); var value = document.RootElement;
            mode = value.TryGetProperty("mode", out var configured) ? configured.GetString() ?? "" : "";
            target = value.TryGetProperty("pathContains", out var filter) ? filter.GetString() ?? "" : "";
            if (value.TryGetProperty("delayMs", out var milliseconds)) delay = Math.Clamp(milliseconds.GetInt32(), 1, 30000);
        }
        else if (mode.StartsWith("mode=", StringComparison.Ordinal)) mode = mode[5..].Trim();
        if (target.Length > 0 && !path.Contains(target, StringComparison.Ordinal)) return;
        _controlRevision = revision; // One-shot: change the content/revision to arm the next request.
        if (mode == "fail") throw new IOException("OP.GG fixture: deliberately injected query failure");
        if (mode == "slow") await Task.Delay(delay);
    }
    private static int Hero(string value) => int.TryParse(value, out int id) && Heroes.Contains(id) ? id : throw new InvalidOperationException("Unknown fixture champion: " + value);
    private static string? Query(Uri uri, string key) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2)).Where(parts => Uri.UnescapeDataString(parts[0]) == key).Select(parts => parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "").FirstOrDefault();
    private static object BuildRow(int[] ids, int index) => new { ids, play = 1000 + index * 100, win = 550 + index * 40, pick_rate = Math.Max(0, .45 - index * .04) };
    private static Dictionary<string, object?> Average(int index, string mode) => new()
    {
        ["play"] = 1000 + index * 100, ["win_rate"] = mode == "arena" || index == 9 ? null : index == 8 ? 0d : .5 + index * .01,
        ["win"] = mode == "arena" ? 550 + index * 10 : null, ["total_place"] = mode == "arena" ? 2600 + index * 100 : null, ["first_place"] = mode == "arena" ? 120 + index * 10 : null,
        ["pick_rate"] = index == 8 ? 0d : .2 - index * .01, ["ban_rate"] = mode == "aram" || index == 9 ? null : index == 8 ? 0d : .04 + index * .001,
        ["tier"] = index == 9 ? null : index % 5, ["rank"] = index == 9 ? null : index + 1,
        ["tier_data"] = new { tier = index % 5, rank = index + 1, rank_prev = index + 2, rank_prev_patch = index + 3 }
    };
    private static object Summary(int hero, int index, string mode) => new
    {
        id = hero, is_rotation = false, is_rip = false, average_stats = Average(index, mode),
        positions = mode == "ranked" ? new[] { "TOP", "JUNGLE", "MID", "ADC", "SUPPORT" }.Where(position => index != 1 || position == "SUPPORT").Select((position, role) => new { name = position, stats = new { play = 1000 + index * 100, win_rate = index == 8 ? 0d : .48 + index * .01, pick_rate = .15, ban_rate = index == 8 ? 0d : .04, kda = 3.1, tier_data = new { tier = (index + role) % 5, rank = index == 9 ? 0 : index + role + 1, rank_prev = index + role + 2, rank_prev_patch = index + role + 3 } }, counters = Heroes.Where(id => id != hero).Take(3).Select((id, counter) => new { champion_id = id, play = 1000, win = 400 + counter * 10 }).ToArray(), roles = Array.Empty<object>() }).ToArray() : [],
        roles = Array.Empty<object>()
    };
    public object AramBalance => new { data = Heroes.Select((hero, index) => new { champion_id = hero, damage_dealt = index % 2 == 0 ? 105 : 95, damage_taken = 90, attack_speed = 100, cooldown_reduction = -5, healing = 100, tenacity = 0, shield_amount = 100, energy_regen = 100, area_of_effect_damage = 100, @default = false }).ToArray() };
    public object KiwiBalance => new { version = "16.19", cached = true, balance = Heroes.ToDictionary(hero => hero.ToString(), hero => (object)new { adjustments = new[] { new { type = "damage-dealt", value = 1.05, display = "percentage", formattedValue = "105%", effect = "buffed" }, new { type = "damage-taken", value = .9, display = "percentage", formattedValue = "90%", effect = "buffed" }, new { type = "healing", value = .8, display = "percentage", formattedValue = "80%", effect = "nerfed" } } }) };
    public object EnhanceGameData(object original)
    {
        var catalog = JsonSerializer.SerializeToNode(original)!.AsObject();
        catalog["augments"] = JsonSerializer.SerializeToNode(Enumerable.Range(0, 24).ToDictionary(index => (9000 + index).ToString(), index => (object)new { id = 9000 + index, name = new[] { "银色", "金色", "棱彩" }[index / 8] + "测试强化 " + (index % 8 + 1), rarity = new[] { "kSilver", "kGold", "kPrismatic" }[index / 8], iconPath = $"/lol-game-data/assets/v1/augments/{9000 + index}.png", description = "隔离的OP.GG强化说明，测试稀有度分组、展开、性能和热度排序" }));
        catalog["perkstyles"] = JsonSerializer.SerializeToNode(new { schemaVersion = 0, styles = new Dictionary<string, object> { ["8000"] = new { id = 8000, name = "精密", iconPath = "/lol-game-data/assets/v1/perkstyles/8000.png" }, ["8300"] = new { id = 8300, name = "启迪", iconPath = "/lol-game-data/assets/v1/perkstyles/8300.png" } } });
        return catalog;
    }
}

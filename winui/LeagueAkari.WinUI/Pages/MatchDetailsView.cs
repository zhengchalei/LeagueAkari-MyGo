using System.Text.Json;
using System.Text.RegularExpressions;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class MatchDetailsView : UserControl
{
    public event Action? SimulationConfigured;
    private readonly BackendClient _backend;
    private readonly NativeImages _images;
    private readonly JsonElement _game;
    private readonly MatchParticipant[] _players;
    private readonly string _server, _source, _puuid;
    private readonly Action<string>? _openPlayer;
    private readonly Action<string>? _openPlayerInBackground;
    private readonly StackPanel _root = new() { Spacing = 10 };
    private readonly Grid _host = new();
    private readonly TextBlock _status = new() { FontSize = 11 };
    private readonly Button _retryTimeline = new() { FontSize = 11, Visibility = Visibility.Collapsed };
    private readonly ComboBox _tabs = new() { SelectedIndex = 0, Width = 160 };
    private JsonElement _timeline, _resources;
    private Task<JsonElement>? _timelineLoading;
    private int _renderVersion;
    private int _lifecycleVersion;
    private bool _attached;
    private bool _updatingTabs;
    private bool _streamer;
    private readonly List<int> _tabIds = [];
    private readonly Dictionary<int, FrameworkElement> _anchors = [];
    private ScrollViewer? _detailScroll;
    private TextBlock? _metadata;
    private Button? _replayButton, _simulateButton;

    public MatchDetailsView(BackendClient backend, JsonElement game, string puuid, string server, string source, Action<string>? openPlayer = null, bool showReplay = true, Action<string>? openPlayerInBackground = null)
    {
        _backend = backend; _images = new NativeImages(backend); _game = MatchData.Game(game); _players = MatchData.Participants(game); _server = server; _source = source; _puuid = puuid; _openPlayer = openPlayer; _openPlayerInBackground = openPlayerInBackground;
        foreach (var (id, label) in new[] { (0, L("概览", "Overview")), (1, L("详细指标", "Detailed stats")), (2, L("符文", "Runes")), (3, L("事件", "Events")), (4, L("构筑", "Builds")), (5, L("时间线", "Timeline")) })
        {
            if (id == 4 && source != "sgp" || id == 2 && !_players.Any(p => p.Runes.Any(r => r > 0))) continue;
            _tabIds.Add(id); _tabs.Items.Add(label);
        }
        _tabs.SelectedIndex = 0;
        var heading = new WrapPanel { Spacing = 8 };
        heading.Children.Add(_tabs);
        var replay = new Button { Content = L("下载 / 播放回放", "Download / watch replay"), FontSize = 11 };
        _replayButton = replay;
        replay.Click += async (_, _) => await ReplayAsync();
        if (showReplay) heading.Children.Add(replay);
        var simulate = new Button { Content = L("模拟对局查询", "Simulate game lookup"), FontSize = 11 };
        _simulateButton = simulate;
        simulate.IsEnabled = MatchDetailsData.HasDraftIdentities(_players);
        simulate.Click += async (_, _) => { try { await _backend.CallAsync("ongoing-game-main", "setDraft", MatchDetailsData.CreateDraft(_game, _puuid)); _status.Text = L("已设置模拟对局", "Simulation configured"); SimulationConfigured?.Invoke(); } catch (Exception error) { _status.Text = error.Message; } };
        heading.Children.Add(simulate);
        _root.Children.Add(heading);
        _metadata = new TextBlock { Text = GameHeading(), FontSize = 11, IsTextSelectionEnabled = true };
        _root.Children.Add(_metadata);
        _retryTimeline.Click += async (_, _) => await RenderAsync();
        _root.Children.Add(_status); _root.Children.Add(_retryTimeline); _root.Children.Add(_host); Content = _root;
        _tabs.SelectionChanged += async (_, _) => { if (!_updatingTabs) await RenderAsync(); };
        Loaded += async (_, _) =>
        {
            _attached = true; int lifecycle = ++_lifecycleVersion;
            Localization.Changed -= AppearanceChanged; Localization.Changed += AppearanceChanged;
            if (NativeAppearance.Current is { } appearance) { appearance.Changed -= AppearanceChanged; appearance.Changed += AppearanceChanged; }
            try
            {
                var resources = await _backend.StateAsync("league-client-main", "gameData");
                var streamer = (await _backend.CallAsync("setting-factory-main", "get", "app-common-main", "streamerMode")).ValueKind == JsonValueKind.True;
                if (!_attached || lifecycle != _lifecycleVersion) return; _resources = resources; _streamer = streamer;
            }
            catch { }
            if (_attached && lifecycle == _lifecycleVersion) AppearanceChanged();
        };
        Unloaded += (_, _) => { _attached = false; ++_lifecycleVersion; ++_renderVersion; _timelineLoading = null; Localization.Changed -= AppearanceChanged; if (NativeAppearance.Current is { } appearance) appearance.Changed -= AppearanceChanged; };
    }

    private void AppearanceChanged()
    {
        _streamer = NativeAppearance.Current?.StreamerMode ?? _streamer;
        var selection = _tabs.SelectedIndex;
        var labels = new[] { L("概览", "Overview"), L("详细指标", "Detailed stats"), L("符文", "Runes"), L("事件", "Events"), L("构筑", "Builds"), L("时间线", "Timeline") };
        try { _updatingTabs = true; for (var i = 0; i < _tabIds.Count; i++) _tabs.Items[i] = labels[_tabIds[i]]; _tabs.SelectedIndex = selection; } finally { _updatingTabs = false; }
        if (_replayButton != null) _replayButton.Content = L("下载 / 播放回放", "Download / watch replay");
        if (_simulateButton != null) _simulateButton.Content = L("模拟对局查询", "Simulate game lookup");
        _retryTimeline.Content = L("重试读取时间线", "Retry loading timeline");
        if (_metadata != null) _metadata.Text = GameHeading();
        _ = RenderAsync();
    }

    private async Task RenderAsync()
    {
        if (!_attached) return;
        var version = ++_renderVersion;
        try
        {
            var tab = _tabIds[Math.Clamp(_tabs.SelectedIndex, 0, _tabIds.Count - 1)];
            _retryTimeline.Visibility = Visibility.Collapsed;
            if (tab >= 3) { _status.Text = L("正在读取对局时间线…", "Loading game timeline…"); await LoadTimelineAsync(); }
            if (!_attached || version != _renderVersion) return;
            _status.Text = "";
            _host.Children.Clear();
            _anchors.Clear();
            var child = tab switch { 1 => StatsTable(), 2 => Runes(), 3 => Events(), 4 => Builds(), 5 => Timeline(), _ => Summary() };
            _detailScroll = new ScrollViewer { Content = child, MaxHeight = 620, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            _host.Children.Add(_detailScroll);
            if (tab is 2 or 4)
            {
                var navigator = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
                foreach (var participant in _players)
                {
                    var button = new Button { Content = Icon($"/lol-game-data/assets/v1/champion-icons/{participant.ChampionId}.png", 23), Padding = new Thickness(2) };
                    ToolTipService.SetToolTip(button, PrivacyName(participant)); button.Click += (_, _) => { if (_anchors.TryGetValue(participant.Id, out var anchor)) anchor.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0 }); }; navigator.Children.Add(button);
                }
                _host.Children.Add(navigator);
            }
        }
        catch (Exception error) { if (_attached && version == _renderVersion) { _status.Text = error.Message; _retryTimeline.Visibility = Visibility.Visible; } }
    }
    private async Task LoadTimelineAsync()
    {
        if (_timeline.ValueKind == JsonValueKind.Object) return;
        int lifecycle = _lifecycleVersion;
        var loading = _timelineLoading ??= FetchTimelineAsync();
        try { var timeline = await loading; if (_attached && lifecycle == _lifecycleVersion) _timeline = timeline; }
        finally { if (ReferenceEquals(_timelineLoading, loading)) _timelineLoading = null; }
    }
    private async Task<JsonElement> FetchTimelineAsync()
    {
        var source = new PlayerDataSource(_backend);
        await source.ConfigureAsync(_server, _source);
        return MatchDetailsData.Timeline(await source.TimelineAsync((long)_game.Number("gameId")));
    }
    private JsonElement[] Frames => _timeline.Field("frames").Items().ToArray();
    private JsonElement[] AllEvents => Frames.SelectMany(frame => frame.Field("events").Items()).OrderBy(e => e.Number("timestamp")).ToArray();

    private UIElement Summary()
    {
        var body = new StackPanel { Spacing = 12 };
        foreach (var team in _players.GroupBy(p => p.TeamKey)) body.Children.Add(TeamTable(team.ToArray()));
        var metric = new ComboBox { ItemsSource = new[] { L("英雄伤害", "Champion damage"), L("承受伤害", "Damage taken"), L("经济", "Gold"), L("视野", "Vision"), L("治疗", "Healing"), L("补刀", "CS") }, SelectedIndex = 0, Width = 160 };
        var chart = new ContentControl();
        void DrawBars() { var key = new[] { "totalDamageDealtToChampions", "totalDamageTaken", "goldEarned", "visionScore", "totalHeal", "cs" }[metric.SelectedIndex]; chart.Content = MatchCharts.Bars(_players.Select(p => (PrivacyName(p), MatchDetailsData.Stat(p, key) ?? double.NaN, Color(p.TeamId))), 650); }
        metric.SelectionChanged += (_, _) => DrawBars(); DrawBars(); body.Children.Add(metric); body.Children.Add(chart);
        if (_players.FirstOrDefault(p => p.Puuid == _puuid) is { } self)
        {
            body.Children.Add(PlayerRadar(self));
        }
        return body;
    }

    private UIElement StatsTable()
    {
        var body = new StackPanel { Spacing = 6 };
        var filter = new TextBox { PlaceholderText = L("筛选指标名称或字段", "Filter stat names or fields"), Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        var table = new StackPanel { Spacing = 1 };
        void Populate()
        {
            table.Children.Clear();
            var header = Row(_players.Select(PrivacyName).Prepend(L("指标", "Stat")).ToArray(), true); table.Children.Add(header);
            var raw = _players.Select(Flatten).ToArray();
            for (var index = 0; index < _players.Length; index++)
            {
                var score = MatchData.Summarize(new[] { _game }, _players[index].Puuid).AkariScore;
                if (_players[index].Stats.ValueKind == JsonValueKind.Object && MatchDetailsData.Stat(_players[index], "kills").HasValue && MatchDetailsData.Stat(_players[index], "totalDamageDealtToChampions").HasValue) raw[index]["akariScore"] = JsonSerializer.SerializeToElement(score);
                if (MatchDetailsData.Stat(_players[index], "damageGoldEfficiency") is { } efficiency) raw[index]["damageGoldEfficiency"] = JsonSerializer.SerializeToElement(efficiency);
            }
            var known = new HashSet<string>();
            string prior = "";
            foreach (var metric in MatchStatsMetadata.Rows.Where(r => r.Group is not ("ignored" or "undocumented")))
            {
                known.Add(metric.Key);
                if (!raw.Any(p => p.ContainsKey(metric.Key))) continue;
                if (filter.Text != "" && !Localization.Key("matchCard.statKeys." + metric.Key, Localization.Translate(metric.Label)).Contains(filter.Text, StringComparison.OrdinalIgnoreCase) && !metric.Key.Contains(filter.Text, StringComparison.OrdinalIgnoreCase)) continue;
                if (prior != metric.Group) { table.Children.Add(Label(GroupLabel(metric.Group), 13, true)); prior = metric.Group; }
                var cells = raw.Select((p, i) => p.TryGetValue(metric.Key, out var value) ? Format(value, metric.Format) : "—").Prepend(Localization.Key("matchCard.statKeys." + metric.Key, Localization.Translate(metric.Label))).ToArray();
                var row = Row(cells); var label = (TextBlock)row.Children[0];
                if (raw.Any(p => p.GetValueOrDefault(metric.Key).ValueKind == JsonValueKind.Number))
                {
                    var values = raw.Select((p, i) => (PrivacyName(_players[i]), MatchDetailsData.Number(p.GetValueOrDefault(metric.Key)) ?? double.NaN, Color(_players[i].TeamId)));
                    label.ContextFlyout = new Flyout { Content = MatchCharts.Bars(values) };
                    label.Tapped += (_, _) => label.ContextFlyout.ShowAt(label);
                }
                table.Children.Add(row);
            }
            var extra = raw.SelectMany(p => p.Keys).Where(k => !known.Contains(k) && raw.Any(p => p.GetValueOrDefault(k).ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)).Distinct().Order().ToArray();
            foreach (var key in extra.Where(k => filter.Text == "" || k.Contains(filter.Text, StringComparison.OrdinalIgnoreCase))) table.Children.Add(Row(raw.Select(p => p.TryGetValue(key, out var value) ? Format(value, "float") : "—").Prepend(key).ToArray()));
        }
        filter.TextChanged += (_, _) => Populate(); Populate(); body.Children.Add(filter); body.Children.Add(table); return body;
    }

    private UIElement Runes()
    {
        var body = new StackPanel { Spacing = 12 };
        foreach (var player in _players)
        {
            var block = new StackPanel { Spacing = 6 }; block.Children.Add(PlayerHeader(player));
            _anchors[player.Id] = block;
            var perks = player.Raw.Field("perks");
            foreach (var style in perks.Field("styles").Items())
                foreach (var selection in style.Field("selections").Items()) block.Children.Add(PerkLine((int)selection.Number("perk"), selection.Number("var1"), selection.Number("var2"), selection.Number("var3")));
            if (perks.Field("styles").ValueKind != JsonValueKind.Array)
                for (var i = 0; i < 6; i++) { var id = (int)player.Num("perk" + i); if (id > 0) block.Children.Add(PerkLine(id, player.Num($"perk{i}Var1"), player.Num($"perk{i}Var2"), player.Num($"perk{i}Var3"))); }
            var stat = perks.Field("statPerks");
            foreach (var key in new[] { "offense", "flex", "defense" }) if (stat.Number(key) > 0) block.Children.Add(PerkLine((int)stat.Number(key), 0, 0, 0));
            foreach (var id in player.Augments.Where(id => id > 0)) block.Children.Add(ResourceLine("augments", id, L("海克斯强化", "Augment")));
            body.Children.Add(Card(block));
        }
        return body;
    }
    private UIElement PerkLine(int id, double first, double second, double third)
    {
        var resource = _resources.Field("perks").Field(id.ToString()); var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(ResourceIcon("perks", id, 24));
        var endDescriptions = resource.Field("endOfGameStatDescriptions").Items().Select(description => Regex.Replace(description.ToString(), "@eogvar([123])@", match => match.Groups[1].Value switch { "1" => first.ToString("0.##"), "2" => second.ToString("0.##"), _ => third.ToString("0.##") })).ToArray();
        row.Children.Add(Label(resource.Text("name", L("符文 ", "Rune ") + id) + "\n" + (endDescriptions.Length > 0 ? string.Join("\n", endDescriptions.Select(Strip)) : Strip(resource.Text("longDesc", resource.Text("shortDesc")))), 11));
        return row;
    }

    private UIElement Events()
    {
        var body = new StackPanel { Spacing = 8 }; var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; var list = new StackPanel { Spacing = 7 };
        var enabled = new HashSet<string>(["CHAMPION_KILL", "BUILDING_KILL"]);
        var championIds = new HashSet<int>();
        var player = new ComboBox { ItemsSource = _players.Select(p => PrivacyName(p)).Prepend(L("所有玩家", "All players")).ToArray(), SelectedIndex = 0, Width = 180 };
        void Populate()
        {
            list.Children.Clear(); list.Children.Add(Label(L("开始 · 0:00", "Start · 0:00"), 11));
            var selected = player.SelectedIndex > 0 ? _players[player.SelectedIndex - 1].Id : 0;
            bool ChampionMatches(JsonElement ev)
            {
                if (championIds.Count == 0) return true;
                var involved = new[] { (int)ev.Number("killerId"), (int)ev.Number("victimId") }.Concat(ev.Field("assistingParticipantIds").Items().Select(id => (int)id.TryNumber()));
                return involved.Any(id => _players.FirstOrDefault(p => p.Id == id) is { } p && championIds.Contains(p.ChampionId));
            }
            foreach (var ev in AllEvents.Where(ev => enabled.Contains(ev.Text("type")) && ChampionMatches(ev) && (selected == 0 || ev.Number("participantId") == selected || ev.Number("killerId") == selected || ev.Number("victimId") == selected || ev.Field("assistingParticipantIds").Items().Any(id => id.TryNumber() == selected))))
            {
                var row = new StackPanel { Spacing = 4 };
                row.Children.Add(Label(MatchCharts.Time(ev.Number("timestamp")) + " · " + EventLabel(ev), 12, true));
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                if (ev.Field("position").ValueKind == JsonValueKind.Object) { var map = new Button { Content = L("位置", "Position"), FontSize = 10 }; map.Flyout = new Flyout { Content = MapPosition(ev.Field("position")) }; buttons.Children.Add(map); }
                if (ev.Field("victimDamageReceived").ValueKind == JsonValueKind.Array) { var damage = new Button { Content = L("被害伤害明细", "Victim damage details"), FontSize = 10 }; damage.Flyout = new Flyout { Content = VictimDamage(ev) }; buttons.Children.Add(damage); }
                row.Children.Add(buttons); list.Children.Add(Card(row));
            }
            list.Children.Add(Label(L("结束 · ", "End · ") + MatchCharts.Time(_game.Number("gameDuration") * 1000), 11));
        }
        foreach (var (key, label) in new[] { ("CHAMPION_KILL", L("击杀", "Kills")), ("CHAMPION_SPECIAL_KILL", L("特殊击杀", "Special kills")), ("BUILDING_KILL", L("建筑", "Buildings")), ("TURRET_PLATE_DESTROYED", L("镀层", "Turret plates")), ("ELITE_MONSTER_KILL", L("野怪", "Monsters")), ("WARD_PLACED", L("插眼", "Wards placed")), ("WARD_KILL", L("排眼", "Wards cleared")), ("ITEM_PURCHASED", L("购买装备", "Items purchased")), ("SKILL_LEVEL_UP", L("技能升级", "Skill upgrades")) })
        {
            var check = new CheckBox { Content = label, IsChecked = enabled.Contains(key), FontSize = 11 };
            check.Checked += (_, _) => { enabled.Add(key); Populate(); }; check.Unchecked += (_, _) => { enabled.Remove(key); Populate(); }; filters.Children.Add(check);
        }
        var heroes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        foreach (var champion in _players.GroupBy(p => p.ChampionId).Select(group => group.First()))
        {
            var select = new CheckBox { Content = _resources.Field("champions").Field(champion.ChampionId.ToString()).Text("name", champion.ChampionId.ToString()), FontSize = 11 };
            select.Checked += (_, _) => { championIds.Add(champion.ChampionId); Populate(); }; select.Unchecked += (_, _) => { championIds.Remove(champion.ChampionId); Populate(); }; heroes.Children.Add(select);
        }
        player.SelectionChanged += (_, _) => Populate(); body.Children.Add(player); body.Children.Add(filters); body.Children.Add(heroes);
        var plates = AllEvents.Where(ev => ev.Text("type") == "TURRET_PLATE_DESTROYED" && ev.Number("killerId") > 0).GroupBy(ev => ev.Number("killerId")).OrderByDescending(group => group.Count()).Select(group => ((_players.FirstOrDefault(p => p.Id == group.Key) is { } p) ? PrivacyName(p) : group.Key.ToString()) + " · " + group.Count());
        if (plates.Any()) body.Children.Add(Label(L("镀层统计 · ", "Turret plate totals · ") + string.Join(" / ", plates), 11));
        body.Children.Add(list); Populate(); return body;
    }
    private string EventLabel(JsonElement ev)
    {
        string Player(string key) => _players.FirstOrDefault(p => p.Id == ev.Number(key)) is { } p ? PrivacyName(p) : L("环境", "Environment");
        return ev.Text("type") switch { "CHAMPION_KILL" => Player("killerId") + L(" 击杀 ", " killed ") + Player("victimId") + L(" · 助攻 ", " · assists ") + string.Join(" / ", ev.Field("assistingParticipantIds").Items().Select(id => _players.FirstOrDefault(p => p.Id == id.TryNumber()) is { } assistant ? PrivacyName(assistant) : "")), "BUILDING_KILL" => Player("killerId") + L(" 摧毁 ", " destroyed ") + EventTerm(ev.Text("buildingType")) + " · " + EventTerm(ev.Text("laneType")), "ELITE_MONSTER_KILL" => Player("killerId") + L(" 击杀 ", " killed ") + EventTerm(ev.Text("monsterSubType", ev.Text("monsterType"))), "ITEM_PURCHASED" => Player("participantId") + L(" 购买 ", " purchased ") + _resources.Field("items").Field(ev.Number("itemId").ToString()).Text("name", L("装备 ", "Item ") + ev.Number("itemId")), "SKILL_LEVEL_UP" => Player("participantId") + L(" 升级 ", " upgraded ") + Skill((int)ev.Number("skillSlot")), "WARD_PLACED" => Player("creatorId") + L(" 放置 ", " placed ") + EventTerm(ev.Text("wardType")), "WARD_KILL" => Player("killerId") + L(" 排除 ", " cleared ") + EventTerm(ev.Text("wardType")), _ => EventTerm(ev.Text("killType", ev.Text("type"))) };
    }
    private UIElement VictimDamage(JsonElement ev)
    {
        var body = new StackPanel { Spacing = 8, MaxWidth = 600 };
        foreach (var (key, label) in new[] { ("victimDamageReceived", L("受到伤害", "Damage received")), ("victimDamageDealt", L("造成伤害", "Damage dealt")) })
        {
            var hits = ev.Field(key).Items().ToArray(); body.Children.Add(Label(label + " · " + hits.Sum(hit => hit.Number("physicalDamage") + hit.Number("magicDamage") + hit.Number("trueDamage")).ToString("N0"), 13, true));
            var grouped = hits.GroupBy(hit => hit.Number("participantId") > 0 ? "champion:" + hit.Number("participantId") : hit.Text("type", "OTHER")).OrderByDescending(group => group.Sum(hit => hit.Number("physicalDamage") + hit.Number("magicDamage") + hit.Number("trueDamage"))).ToArray();
            var maximum = Math.Max(1, grouped.Select(group => group.Sum(hit => hit.Number("physicalDamage") + hit.Number("magicDamage") + hit.Number("trueDamage"))).DefaultIfEmpty(1).Max());
            foreach (var group in grouped)
            {
                var source = group.First(); var participant = _players.FirstOrDefault(p => p.Id == source.Number("participantId"));
                var block = new StackPanel { Spacing = 4 }; var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                if (participant is not null) header.Children.Add(Icon($"/lol-game-data/assets/v1/champion-icons/{participant.ChampionId}.png", 28));
                var physical = group.Sum(hit => hit.Number("physicalDamage")); var magic = group.Sum(hit => hit.Number("magicDamage")); var truth = group.Sum(hit => hit.Number("trueDamage"));
                header.Children.Add(Label((participant is not null ? PrivacyName(participant) : source.Text("name", group.Key)) + $" · {physical + magic + truth:N0} ({physical:N0} / {magic:N0} / {truth:N0})", 11, true)); block.Children.Add(header);
                var bar = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var (amount, color) in new[] { (physical, Microsoft.UI.Colors.Orange), (magic, Microsoft.UI.Colors.RoyalBlue), (truth, Microsoft.UI.Colors.Gray) }) bar.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 350 * amount / maximum, Height = 12, Fill = new SolidColorBrush(color) });
                block.Children.Add(bar);
                var spells = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                foreach (var spell in group.GroupBy(hit => hit.Boolean("basic") ? "A" : (int)hit.Number("spellSlot") switch { 63 => "P", 0 => "Q", 1 => "W", 2 => "E", 3 => "R", _ => "?" }))
                {
                    var cell = Label(spell.Key + " · " + spell.Sum(hit => hit.Number("physicalDamage") + hit.Number("magicDamage") + hit.Number("trueDamage")).ToString("N0"), 11);
                    var details = string.Join("\n", spell.Select(hit => hit.Text("spellName") + $" · {hit.Number("physicalDamage"):N0} / {hit.Number("magicDamage"):N0} / {hit.Number("trueDamage"):N0}")); ToolTipService.SetToolTip(cell, details); spells.Children.Add(cell);
                }
                block.Children.Add(spells); body.Children.Add(block);
            }
        }
        return body;
    }
    private UIElement MapPosition(JsonElement position)
    {
        var canvas = new Canvas { Width = 240, Height = 240, Background = new SolidColorBrush(Microsoft.UI.Colors.DarkSlateGray) };
        var mapId = (int)_game.Number("mapId", 11);
        var domain = mapId switch { 12 or 14 => (-28d, -19d, 12849d, 12858d), 11 or 90 => (0d, 0d, 14820d, 14881d), 1 => (-650d, -83d, 14076d, 14522d), 3 => (-500d, -500d, 15000d, 15000d), 8 => (0d, 0d, 13987d, 13987d), 10 => (0d, 0d, 15398d, 15398d), _ => (0d, 0d, 15000d, 15000d) };
        var file = Path.Combine(AppContext.BaseDirectory, "map-images", mapId + ".png");
        if (File.Exists(file)) canvas.Children.Add(new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(file)), Width = 240, Height = 240, Stretch = Stretch.Fill });
        var mark = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed) };
        Canvas.SetLeft(mark, Math.Clamp((position.Number("x") - domain.Item1) / (domain.Item3 - domain.Item1) * 240, 0, 240) - 4);
        Canvas.SetTop(mark, Math.Clamp((domain.Item4 - position.Number("y")) / (domain.Item4 - domain.Item2) * 240, 0, 240) - 4);
        canvas.Children.Add(mark); ToolTipService.SetToolTip(mark, $"{position.Number("x"):0}, {position.Number("y"):0}"); return canvas;
    }

    private UIElement Builds()
    {
        var body = new StackPanel { Spacing = 12 };
        foreach (var player in _players)
        {
            var block = new StackPanel { Spacing = 8 }; block.Children.Add(PlayerHeader(player));
            _anchors[player.Id] = block;
            var skills = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            var level = 0;
            foreach (var ev in AllEvents.Where(e => e.Text("type") == "SKILL_LEVEL_UP" && e.Number("participantId") == player.Id)) { var evolved = ev.Text("levelUpType") == "EVOLVE"; var label = Label(Skill((int)ev.Number("skillSlot")) + (evolved ? "↑" : " " + (++level)), 13, true); ToolTipService.SetToolTip(label, MatchCharts.Time(ev.Number("timestamp"))); skills.Children.Add(label); }
            block.Children.Add(Label(L("技能升级", "Skill upgrades"), 12, true)); block.Children.Add(skills.Children.Count > 0 ? skills : Label(L("数据源未提供技能升级事件", "No skill upgrade events supplied by this source"), 11));
            var purchased = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            double lastPurchase = 0; var anvils = 0;
            foreach (var ev in AllEvents.Where(e => e.Text("type") == "ITEM_PURCHASED" && e.Number("participantId") == player.Id)) { if (lastPurchase > 0 && ev.Number("timestamp") - lastPurchase > 30000) purchased.Children.Add(Label("→")); lastPurchase = ev.Number("timestamp"); if ((int)ev.Number("itemId") is 6032 or 220000) anvils++; var item = new StackPanel { Spacing = 2 }; item.Children.Add(ResourceIcon("items", (int)ev.Number("itemId"), 28)); item.Children.Add(Label(MatchCharts.Time(ev.Number("timestamp")), 9)); purchased.Children.Add(item); }
            if (anvils > 0) block.Children.Add(Label(L("铁砧 · ", "Anvils · ") + anvils, 11));
            block.Children.Add(Label(L("装备购买", "Item purchases"), 12, true)); block.Children.Add(purchased.Children.Count > 0 ? purchased : Label(L("数据源未提供装备购买事件", "No item purchase events supplied by this source"), 11));
            body.Children.Add(Card(block));
        }
        return body;
    }

    private UIElement Timeline()
    {
        var body = new StackPanel { Spacing = 8 };
        if (Frames.Length == 0) { body.Children.Add(Label(L("数据源未提供时间线帧", "No timeline frames supplied by this source"))); return body; }
        if (_players.Length == 0) { body.Children.Add(Label(L("数据源未提供玩家身份，无法对应时间线", "No participant identities supplied to associate timeline frames"))); return body; }
        var metric = new ComboBox { ItemsSource = new[] { L("经济", "Gold"), L("补刀", "CS"), L("经验", "XP"), L("对英雄伤害", "Champion damage"), L("承受伤害", "Damage taken") }, SelectedIndex = 0, Width = 180 };
        var diff = new CheckBox { Content = L("显示蓝队与红队差值", "Show blue vs red team difference"), IsChecked = true };
        var playerFilters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var selected = _players.Select(p => p.Id).ToHashSet(); var chart = new ContentControl(); var legend = new TextBlock { FontSize = 11 };
        void Draw()
        {
            double Value(JsonElement frame, int id) => MatchDetailsData.TimelineMetric(frame, id, Math.Max(0, metric.SelectedIndex)) ?? double.NaN;
            List<ChartSeries> series = [];
            if (diff.IsChecked == true)
            {
                var blue = _players.Where(p => selected.Contains(p.Id) && p.TeamId == 100).ToArray();
                var red = _players.Where(p => selected.Contains(p.Id) && p.TeamId != 100).ToArray();
                series.Add(new ChartSeries(L("蓝队 − 红队", "Blue − red"), Frames.Select(f => blue.Sum(p => Value(f, p.Id)) - red.Sum(p => Value(f, p.Id))).ToArray(), Color(100)));
            }
            else foreach (var player in _players.Where(p => selected.Contains(p.Id))) series.Add(new ChartSeries(PrivacyName(player), Frames.Select(f => Value(f, player.Id)).ToArray(), Palette(player.Id)));
            chart.Content = MatchCharts.Lines(series, Frames.Select(f => f.Number("timestamp")).ToArray()); legend.Text = series.Any(s => s.Values.Any(double.IsFinite)) ? string.Join(" · ", series.Select(s => s.Name)) : L("数据源未提供此指标", "This metric is unavailable from this source");
        }
        foreach (var player in _players) { var check = new CheckBox { Content = PrivacyName(player), IsChecked = true, FontSize = 10 }; check.Checked += (_, _) => { selected.Add(player.Id); Draw(); }; check.Unchecked += (_, _) => { selected.Remove(player.Id); Draw(); }; playerFilters.Children.Add(check); }
        metric.SelectionChanged += (_, _) => Draw(); diff.Checked += (_, _) => Draw(); diff.Unchecked += (_, _) => Draw();
        body.Children.Add(metric); body.Children.Add(diff); body.Children.Add(playerFilters); body.Children.Add(legend); body.Children.Add(chart); Draw();
        if (_source == "sgp" && _players.Length > 0)
        {
            body.Children.Add(Label(L("时点属性", "Stats at selected time"), 14, true)); var player = new ComboBox { ItemsSource = _players.Select(PrivacyName).ToArray(), SelectedIndex = 0, Width = 180 }; var frameIndex = new Slider { Minimum = 0, Maximum = Frames.Length - 1, StepFrequency = 1, Width = 650 }; var properties = new StackPanel { Spacing = 4 };
            void AtFrame() { properties.Children.Clear(); var frame = Frames[Math.Clamp((int)frameIndex.Value, 0, Frames.Length - 1)]; properties.Children.Add(Label(MatchCharts.Time(frame.Number("timestamp")), 12, true)); var stats = frame.Field("participantFrames").Field(_players[Math.Clamp(player.SelectedIndex, 0, _players.Length - 1)].Id.ToString()).Field("championStats"); foreach (var field in stats.ValueKind == JsonValueKind.Object ? stats.EnumerateObject() : Enumerable.Empty<JsonProperty>()) properties.Children.Add(Label(StatLabel(field.Name) + " · " + field.Value, 11)); }
            player.SelectionChanged += (_, _) => AtFrame(); frameIndex.ValueChanged += (_, _) => AtFrame(); body.Children.Add(player); body.Children.Add(frameIndex); body.Children.Add(properties); AtFrame();
        }
        return body;
    }
    private async Task ReplayAsync()
    {
        try { var id = (long)_game.Number("gameId"); var metadata = await _backend.CallAsync("winui-backend", "lcuRequest", "GET", $"/lol-replays/v1/metadata/{id}", null); var state = metadata.Text("state"); if (state is "incompatible" or "checking" or "downloading") { _status.Text = state == "downloading" ? L("正在下载回放", "Downloading replay") : state == "checking" ? L("正在检查回放", "Checking replay") : L("此版本回放不兼容", "Replay is incompatible with this version"); return; } await _backend.CallAsync("winui-backend", "lcuRequest", "POST", state == "watch" ? $"/lol-replays/v1/rofls/{id}/watch" : $"/lol-replays/v1/rofls/{id}/download", new { componentType = "replay-button_match-history" }); _status.Text = state == "watch" ? L("正在播放回放", "Starting replay") : L("已请求下载回放", "Replay download requested"); } catch (Exception error) { _status.Text = error.Message; }
    }

    private UIElement PlayerHeader(MatchParticipant player) { var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; row.Children.Add(Icon($"/lol-game-data/assets/v1/champion-icons/{player.ChampionId}.png", 28)); row.Children.Add(Label(PrivacyName(player) + " · " + Localization.Key("matchCard.position." + player.Position, player.Position), 13, true)); return row; }
    private string PrivacyName(MatchParticipant player) => _streamer ? _resources.Field("champions").Field(player.ChampionId.ToString()).Text("name", L("英雄 ", "Champion ") + player.ChampionId) : player.Name + (player.Tag == "" ? "" : "#" + player.Tag);
    private Image Icon(string path, int size) { var image = new Image { Width = size, Height = size }; _ = _images.SetAsync(image, path); return image; }
    private UIElement ResourceIcon(string kind, int id, int size) { var data = HistoryCardData.Resource(_resources, kind, id); var image = Icon(HistoryCardData.IconPath(data), size); ToolTipService.SetToolTip(image, data.Text("name", id.ToString()) + "\n" + Strip(data.Text("description", data.Text("longDesc")))); return image; }
    private UIElement ResourceLine(string kind, int id, string fallback) { var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; row.Children.Add(ResourceIcon(kind, id, 24)); row.Children.Add(Label(_resources.Field(kind).Field(id.ToString()).Text("name", fallback + " " + id), 11)); return row; }
    private static Dictionary<string, JsonElement> Flatten(MatchParticipant player) { var values = new Dictionary<string, JsonElement>(); foreach (var field in player.Stats.ValueKind == JsonValueKind.Object ? player.Stats.EnumerateObject() : Enumerable.Empty<JsonProperty>()) values[field.Name] = field.Value; foreach (var kind in new[] { "challenges", "PlayerBehavior" }) if (player.Raw.Field(kind).ValueKind == JsonValueKind.Object) foreach (var field in player.Raw.Field(kind).EnumerateObject()) values[field.Name] = field.Value; return values; }
    private static string Format(JsonElement value, string format) => value.ValueKind switch { JsonValueKind.True => "✓", JsonValueKind.False => "—", JsonValueKind.Number => format == "percentage" ? value.GetDouble().ToString("P0") : format == "game-time" ? MatchCharts.Time(value.GetDouble() * 1000) : value.GetDouble().ToString(format == "integer" ? "N0" : "0.##"), JsonValueKind.String => value.GetString() ?? "", _ => "—" };
    private static Grid Row(string[] cells, bool bold = false) { var row = new Grid { ColumnSpacing = 4 }; for (var index = 0; index < cells.Length; index++) { row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(index == 0 ? 170 : 100) }); var label = Label(cells[index], 11, bold); label.Margin = new Thickness(4); Grid.SetColumn(label, index); row.Children.Add(label); } return row; }
    private static string EventTerm(string value) => Localization.Key("matchCard.frameEventType." + value, Localization.Key("matchCard.buildingType." + value, Localization.Key("matchCard.laneType." + value, Localization.Key("matchCard.towerType." + value, value))));
    private static string L(string zh, string en) => Localization.Text(zh, en);
    private string GameHeading() => (_streamer ? L("对局详情", "Match details") : L("对局", "Game") + " " + _game.Number("gameId").ToString("0")) + $" · {_source} · {MatchData.Creation(_game).ToLocalTime():yyyy-MM-dd HH:mm} · {L("版本", "Version")} {_game.Text("gameVersion")}";
    private static string GroupLabel(string key) => key switch { "combat-stats" => L("战斗", "Combat"), "damage" => L("伤害", "Damage"), "cc" => L("控制", "Crowd control"), "vision" => L("视野", "Vision"), "buildings" => L("建筑", "Buildings"), "economy" => L("经济", "Gold"), "healing" => L("治疗", "Healing"), "pings" => L("信号", "Pings"), "combat-advanced" => L("进阶战斗", "Advanced combat"), "objectives" => L("战略目标", "Objectives"), "statistics" => L("统计", "Statistics"), "game-state" => L("对局状态", "Game state"), "abilities" => L("技能", "Abilities"), "survival-skills" => L("生存", "Survival"), "teamfight" => L("团战", "Teamfights"), _ => key };
    private static string StatLabel(string key) => Localization.Key("matchCard.statKeys." + key, MatchStatsMetadata.Rows.FirstOrDefault(row => row.Key == key).Label ?? key);
    private static string Strip(string value) => Regex.Replace(value, "<[^>]+>", " ").Replace("&nbsp;", " ").Replace("&amp;", "&");
    private static string Skill(int slot) => slot switch { 1 => "Q", 2 => "W", 3 => "E", 4 => "R", _ => "U" };
    private static global::Windows.UI.Color Color(int team) => team == 100 ? Microsoft.UI.Colors.CornflowerBlue : Microsoft.UI.Colors.IndianRed;
    private static global::Windows.UI.Color Palette(int id) => new[] { Microsoft.UI.Colors.CornflowerBlue, Microsoft.UI.Colors.Orange, Microsoft.UI.Colors.MediumPurple, Microsoft.UI.Colors.SeaGreen, Microsoft.UI.Colors.Goldenrod, Microsoft.UI.Colors.IndianRed, Microsoft.UI.Colors.Teal, Microsoft.UI.Colors.SlateBlue, Microsoft.UI.Colors.Magenta, Microsoft.UI.Colors.Olive }[Math.Clamp(id - 1, 0, 9)];
    private static TextBlock Label(string value, int size = 12, bool bold = false) => new() { Text = value, FontSize = size, FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private static Border Card(UIElement content) => new() { Child = content, Padding = new Thickness(8), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray) };
}

using System.Runtime.CompilerServices;
using System.Text.Json;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LeagueAkari.WinUI.Pages;

/// <summary>Realized history rows own their native header and lazy detail view; recycled rows release subscriptions.</summary>
public sealed class HistoryMatchCard : UserControl
{
    private static readonly ConditionalWeakTable<BackendClient, ResourceSnapshot> ResourceCache = new();
    private sealed class ResourceSnapshot { public Task<JsonElement>? Task, ExtraTask; public DateTimeOffset ReadAt; }
    private readonly BackendClient _backend;
    private readonly GameAssets _assets;
    private readonly NativeImages _images;
    private readonly JsonElement _game;
    private readonly MatchParticipant[] _players;
    private readonly MatchParticipant? _self;
    private readonly string _puuid, _server, _source;
    private readonly Action<string, string>? _openPlayer;
    private readonly Action<string, string>? _openPlayerInBackground;
    private readonly Func<Task<JsonElement>>? _loadDetails;
    private readonly Expander _expander = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private SolidColorBrush _headerBackground = new(), _cardBorder = new(), _resultForeground = new(), _headerForeground = new();
    private readonly Grid _heading = new() { ColumnSpacing = 10 };
    private readonly StackPanel _details = new() { Spacing = 6 };
    private readonly StackPanel _detailsToolbar = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly ProgressRing _detailsBusy = new() { Width = 18, Height = 18, Visibility = Visibility.Collapsed };
    private readonly Button _retryDetails = new() { FontSize = 11, Visibility = Visibility.Collapsed };
    private readonly Button _replayButton = new() { FontSize = 11, Visibility = Visibility.Collapsed };
    private readonly TextBlock _error = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Width = 120, Visibility = Visibility.Collapsed };
    private readonly ReplayService _replays;
    private readonly DispatcherTimer _minute = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _replayTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private JsonElement _resources, _extra, _replay;
    private bool _attached, _loadingDetails, _detailsLoaded, _replayInitialized, _loadingReplay, _replayRequest;
    private int _lifecycleVersion, _detailsVersion;
    private double _headerWidth;
    private static string L(string zh, string en) => Localization.Text(zh, en);
    private static TextBlock Text(string value, int size = 11, bool bold = false) => new() { Text = value, FontSize = size, FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, TextWrapping = TextWrapping.Wrap };

    public HistoryMatchCard(BackendClient backend, JsonElement game, string puuid, string server, string source, Action<string, string>? openPlayer = null, Func<Task<JsonElement>>? loadDetails = null, Action<string, string>? openPlayerInBackground = null)
    {
        _backend = backend; _assets = new(backend); _images = new(backend); _replays = new(backend); _game = MatchData.Game(game); _players = MatchData.Participants(_game); _self = _players.FirstOrDefault(p => p.Puuid == puuid); _puuid = puuid; _server = server; _source = source; _openPlayer = openPlayer; _loadDetails = loadDetails; _openPlayerInBackground = openPlayerInBackground;
        _heading.ColumnDefinitions.Add(new()); _heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _expander.Header = _heading; Content = _expander;
        ActualThemeChanged += (_, _) => RenderHeader();
        _expander.Expanding += async (_, _) => await ExpandAsync();
        _expander.Collapsed += (_, _) => _replayTimer.Stop();
        _replayButton.Click += async (_, _) => await ExecuteReplayAsync();
        _retryDetails.Click += async (_, _) => await ExpandAsync();
        _detailsToolbar.Children.Add(_replayButton); _detailsToolbar.Children.Add(_progress); _detailsToolbar.Children.Add(_detailsBusy); _detailsToolbar.Children.Add(_retryDetails);
        _details.Children.Add(_detailsToolbar); _details.Children.Add(_error); _expander.Content = _details;
        _minute.Tick += (_, _) => RenderHeader();
        _replayTimer.Tick += async (_, _) => await RefreshReplayAsync();
        SizeChanged += (_, _) => { var width = ActualWidth < 680 ? 0 : ActualWidth < 700 ? 1 : 2; if (_headerWidth != width) { _headerWidth = width; RenderHeader(); } };
        Loaded += async (_, _) =>
        {
            if (_attached) return; _attached = true; int lifecycle = ++_lifecycleVersion; Localization.Changed += AppearanceChanged; if (NativeAppearance.Current is { } appearance) appearance.Changed += AppearanceChanged;
            _minute.Start();
            try { var cache = ResourceCache.GetValue(_backend, static _ => new ResourceSnapshot()); if (cache.Task == null || DateTimeOffset.UtcNow - cache.ReadAt > TimeSpan.FromSeconds(15)) { cache.ReadAt = DateTimeOffset.UtcNow; cache.Task = _backend.StateAsync("league-client-main", "gameData"); cache.ExtraTask = _backend.StateAsync("extra-assets-main", "gtimg"); } _resources = await cache.Task; if (cache.ExtraTask != null) _extra = await cache.ExtraTask; }
            catch { ResourceCache.GetValue(_backend, static _ => new ResourceSnapshot()).Task = null; }
            if (!_attached || lifecycle != _lifecycleVersion) return;
            RenderHeader();
            if (_expander.IsExpanded) await ExpandAsync();
        };
        Unloaded += (_, _) => { _attached = false; ++_lifecycleVersion; ++_detailsVersion; _loadingDetails = false; _loadingReplay = false; _detailsBusy.IsActive = false; _detailsBusy.Visibility = Visibility.Collapsed; _minute.Stop(); _replayTimer.Stop(); Localization.Changed -= AppearanceChanged; if (NativeAppearance.Current is { } appearance) appearance.Changed -= AppearanceChanged; };
        RenderHeader();
    }
    private void AppearanceChanged() { _retryDetails.Content = L("重试读取详情", "Retry loading details"); RenderHeader(); RenderReplay(); }
    private string PlayerName(MatchParticipant player) => NativeAppearance.Current?.StreamerMode == true ? _resources.Field("champions").Field(player.ChampionId.ToString()).Text("name", L("英雄 ", "Champion ") + player.ChampionId) : player.Name;
    private string PlayerTooltip(MatchParticipant player) => PlayerName(player) + (NativeAppearance.Current?.StreamerMode == true || player.Tag.Length == 0 ? "" : "#" + player.Tag);
    private bool Arena => _game.Text("gameMode") == "CHERRY";
    private double? Value(string key) => _self is null ? null : MatchDetailsData.Stat(_self, key);
    private string Formatted(string key, string format = "N0") => MatchDetailsData.Format(Value(key), format);
    private void RenderHeader()
    {
        _heading.Children.Clear(); if (_self is not { } self) { _heading.Children.Add(Text(L("数据源未提供玩家身份", "No player identity supplied"))); return; }
        var team = _players.Where(p => p.TeamKey == self.TeamKey).ToArray();
        var result = HistoryCardData.Placement(self) is > 0 and var placement ? L("第 " + placement + " 名", placement + (placement % 100 is 11 or 12 or 13 ? "th" : (placement % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" })) : Localization.Key("matchCard.result." + (self.IsSurrender && self.WinResult != "remake" ? "surrender" : self.WinResult), self.WinResult);
        bool dark = NativeAppearance.Current?.IsDark ?? ActualTheme == ElementTheme.Dark;
        var colors = NativeMatchColors.History(_game.Text("gameMode") == "PRACTICETOOL" ? "neutral" : self.WinResult, dark, NativeAppearance.Current?.ThemeId ?? (dark ? "dark" : "light"));
        static global::Windows.UI.Color Color(NativeRgb color, byte alpha = 255) => global::Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);
        _headerBackground = new(Color(colors.Background)); _cardBorder = new(Color(colors.Border, 51)); _resultForeground = new(Color(colors.Result)); _headerForeground = new(dark ? Microsoft.UI.Colors.WhiteSmoke : Microsoft.UI.Colors.Black);
        _expander.Resources["ExpanderHeaderBackground"] = _headerBackground;
        _expander.Resources["ExpanderHeaderBorderBrush"] = _cardBorder;
        _expander.Resources["ExpanderHeaderBorderPointerOverBrush"] = _cardBorder;
        _expander.Resources["ExpanderHeaderBorderPressedBrush"] = _cardBorder;
        foreach (var key in new[] { "ExpanderHeaderForeground", "ExpanderHeaderForegroundPointerOver", "ExpanderHeaderForegroundPressed" }) _expander.Resources[key] = _headerForeground;
        // A realized template can retain its previously resolved resource. Replace its header brush too.
        ApplyHeaderTheme(_expander);
        _expander.Background = NativeAppearance.Current?.CardBrush() ?? new SolidColorBrush(dark ? global::Windows.UI.Color.FromArgb(255,23,23,23) : global::Windows.UI.Color.FromArgb(255,245,245,245));
        _expander.BorderBrush = _cardBorder; _expander.BorderThickness = new Thickness(1);
        var body = new StackPanel { Spacing = 6 }; var upper = new WrapPanel { Spacing = 8 };
        var champion = new Grid { Width = 60 }; champion.Children.Add(_assets.Icon("champion-summary", self.ChampionId, 44));
        if (HistoryCardData.IsChampion(self)) champion.Children.Add(new TextBlock { Text = "♛", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Goldenrod), FontSize = 18, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -10, 0, 0) });
        if (self.Position.Length > 0) { var label = Text(Localization.Key("matchCard.position." + self.Position, self.Position), 9); ToolTipService.SetToolTip(label, label.Text); label.VerticalAlignment = VerticalAlignment.Bottom; label.HorizontalAlignment = HorizontalAlignment.Right; champion.Children.Add(label); }
        upper.Children.Add(champion);
        var spell = new StackPanel { Spacing = 2 }; foreach (var id in self.Spells) spell.Children.Add(ResourceIcon("summonerSpells", "summoner-spells", id, 20)); upper.Children.Add(spell);
        var mode = _game.Text("gameMode");
        if (mode is not ("KIWI" or "CHERRY") && self.Runes.FirstOrDefault() > 0 && self.PerkStyles.ElementAtOrDefault(1) > 0) { var rune = new StackPanel { Spacing = 2 }; rune.Children.Add(ResourceIcon("perks", "perks", self.Runes[0], 20)); rune.Children.Add(ResourceIcon("perkstyles", "perkstyles", self.PerkStyles[1], 20)); upper.Children.Add(rune); }
        if ((mode is "CHERRY" or "KIWI") && _headerWidth >= 1) { var augments = new Grid { ColumnSpacing = 2, RowSpacing = 2 }; for (int i = 0; i < 3; i++) augments.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); for (int i = 0; i < 2; i++) augments.RowDefinitions.Add(new() { Height = GridLength.Auto }); int iAugment = 0; foreach (var id in self.Augments) { var icon = ResourceIcon("augments", "augments", id, 20); Grid.SetColumn(icon, iAugment % 3); Grid.SetRow(icon, iAugment++ / 3); augments.Children.Add(icon); } upper.Children.Add(augments); }
        var stats = new StackPanel { Spacing = 3 }; var kda = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        kda.Children.Add(Text(Formatted("kills"), 16, true)); kda.Children.Add(Text("/")); var deaths = Text(Formatted("deaths"), 16, true); deaths.Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed); kda.Children.Add(deaths); kda.Children.Add(Text("/")); kda.Children.Add(Text(Formatted("assists"), 16, true)); stats.Children.Add(kda);
        var perfect = Value("deaths") == 0 && (Value("kills") > 0 || Value("assists") > 0); stats.Children.Add(Text((perfect ? Localization.Key("matchCard.overview.perfect") : Formatted("kda", "F2")) + " (" + MatchDetailsData.Format(MatchDetailsData.KillParticipation(self, team), "P0") + ")"));
        upper.Children.Add(stats);
        var damage = new StackPanel { Spacing = 3 }; var total = MatchDetailsData.Sum(team.Select(p => MatchDetailsData.Stat(p, "totalDamageDealtToChampions"))); damage.Children.Add(Text(MatchDetailsData.Format(Value("totalDamageDealtToChampions") is { } own && total.HasValue ? own / MatchData.NoZero(total.Value) : null, "P0"), 16, true)); damage.Children.Add(Text(Formatted("totalDamageDealtToChampions") + " " + Localization.Key("matchCard.overview.damage"))); upper.Children.Add(damage);
        var radarTrigger = new Button { Content = Text("◉", 15), Padding = new Thickness(2), Flyout = new Flyout { Content = Radar(team) } }; ToolTipService.SetToolTip(radarTrigger, L("表现雷达", "Performance radar")); upper.Children.Add(radarTrigger);
        if (mode == "CLASSIC" && _headerWidth >= 2) { var cs = new StackPanel { Spacing = 3 }; cs.Children.Add(Text(Formatted("cs") + " CS", 16, true)); cs.Children.Add(Text(MatchDetailsData.Format(Value("cs") is { } ownCs && _game.Number("gameDuration") > 0 ? ownCs / (_game.Number("gameDuration") / 60) : null, "F1") + " " + Localization.Key("matchCard.overview.csPerMin"))); upper.Children.Add(cs); }
        body.Children.Add(upper);
        var lower = new Grid { ColumnSpacing = 5 }; lower.ColumnDefinitions.Add(new() { Width = new GridLength(64) }); lower.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); lower.ColumnDefinitions.Add(new());
        var outcome = Text(result, 13, true); outcome.Foreground = _resultForeground; lower.Children.Add(outcome);
        var items = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 }; foreach (var id in self.Items) items.Children.Add(ResourceIcon("items", "items", id, 20)); if (self.RoleBoundItem > 0) items.Children.Add(ResourceIcon("items", "items", self.RoleBoundItem, 20)); Grid.SetColumn(items, 1); lower.Children.Add(items);
        var tags = TagBar(HistoryCardData.Tags(_game, self, _players)); Grid.SetColumn(tags, 2); lower.Children.Add(tags); body.Children.Add(lower);
        var info = Text(_resources.Field("queues").Field(_game.Number("queueId").ToString("0")).Text("name", Localization.Translate(MatchData.QueueLabel((int)_game.Number("queueId")))) + " · " + HistoryCardData.Duration(_game.Number("gameDuration")) + " · " + RelativeTime() + " · " + HistoryCardData.MapName(_resources, _game)); ToolTipService.SetToolTip(info, MatchData.Creation(_game).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff")); body.Children.Add(info);
        _heading.Children.Add(body);
        if (_headerWidth >= 1) { var roster = Roster(); Grid.SetColumn(roster, 1); _heading.Children.Add(roster); }
    }
    private void ApplyHeaderTheme(DependencyObject parent)
    {
        if (parent is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Name: "ExpanderHeader" } header)
        {
            header.Background = _headerBackground; header.BorderBrush = _cardBorder; header.Foreground = _headerForeground;
            return;
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) ApplyHeaderTheme(VisualTreeHelper.GetChild(parent, i));
    }
    private Grid Roster()
    {
        var grid = new Grid { ColumnSpacing = 6, MaxWidth = 210 }; grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new());
        var teams = _players.GroupBy(p => p.TeamKey).OrderBy(g => Arena ? HistoryCardData.Placement(g.First()) : g.First().TeamId).ToArray();
        var three = Arena && teams.Any(g => g.Count() == 3);
        var shown = Arena && !three ? teams.Take(Math.Max(1, teams.Length / 2)) : teams;
        int index = 0;
        foreach (var team in shown)
        {
            var column = new StackPanel { Spacing = 2 };
            if (Arena) column.Children.Add(Text(HistoryCardData.Placement(team.First()).ToString(), 10, true));
            foreach (var player in team.Take(Arena ? 3 : 5))
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 }; line.Children.Add(_assets.Icon("champion-summary", player.ChampionId, 16));
                if (!three) line.Children.Add(new TextBlock { Text = PlayerName(player), FontSize = 10, FontWeight = player.Puuid == _puuid ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, MaxWidth = 78, TextTrimming = TextTrimming.CharacterEllipsis });
                if (player.Puuid is "" or "00000000-0000-0000-0000-000000000000") line.Children.Add(Text("♟", 10));
                var button = new Button { Content = line, Padding = new Thickness(0), MinHeight = 16, BorderThickness = new Thickness(0), IsEnabled = _openPlayer != null && player.Puuid.Length > 0 }; ToolTipService.SetToolTip(button, PlayerTooltip(player)); button.Click += (_, _) => _openPlayer?.Invoke(player.Puuid, _server); column.Children.Add(button);
                NativePlayerNavigation.AttachBackgroundOpen(button, _openPlayerInBackground == null ? null : () => _openPlayerInBackground(player.Puuid, _server));
            }
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); Grid.SetColumn(column, index % 2); Grid.SetRow(column, index++ / 2); grid.Children.Add(column);
        }
        return grid;
    }
    private FrameworkElement ResourceIcon(string kind, string catalog, int id, int size)
    {
        var data = HistoryCardData.Resource(_resources, kind, id);
        if (id <= 0) return new Border { Width = size, Height = size, Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(60, 100, 120, 145)), CornerRadius = new CornerRadius(2) };
        if (data.ValueKind != JsonValueKind.Object) return _assets.Icon(catalog, id, size);
        var image = new Image { Width = size, Height = size }; _ = _images.SetAsync(image, HistoryCardData.IconPath(data));
        var description = data.Text("description", data.Text("longDesc"));
        var name = data.Text("nameTRA", data.Text("name", id.ToString()));
        if (kind == "augments")
        {
            var rarity = data.Text("rarity"); var key = rarity switch { "kBronze" => "bronze", "kSilver" => "silver", "kGold" => "gold", "kPrismatic" => "prismatic", "kEventChoice" => "eventChoice", _ => "rarity" };
            name += " · " + Localization.Key("gameAssets.augment." + key, rarity, new Dictionary<string, object?> { ["rarity"] = rarity });
            if (!Localization.IsEnglish) description = _extra.Field("kiwiAugments").Items().FirstOrDefault(a => a.Number("augmentID") == id).Text("tooltip", description);
        }
        ToolTipService.SetToolTip(image, name + "\n" + System.Text.RegularExpressions.Regex.Replace(description, "<[^>]+>", " ")); return image;
    }
    private FrameworkElement CreateTag(HistoryCardTag tag)
    {
        string labelKey = "matchCard.tags." + tag.Key + (tag.Key.StartsWith("multiKill.") ? "" : tag.Key.StartsWith("towerKill.") ? "Label" : tag.Key.EndsWith(".best") || tag.Key.EndsWith(".team") ? "Label" : ".label");
        string label = Localization.Key(labelKey); if (tag.Times && tag.Count != 1) label = Localization.Key("matchCard.tags.times", null, new Dictionary<string, object?> { ["label"] = label, ["count"] = Math.Round(tag.Count) });
        var color = tag.Color switch { "red" => Microsoft.UI.Colors.DarkRed, "slate" => Microsoft.UI.Colors.DarkSlateGray, "emerald" => Microsoft.UI.Colors.SeaGreen, "stone" => Microsoft.UI.Colors.DimGray, "sky" => Microsoft.UI.Colors.SteelBlue, "amber" => Microsoft.UI.Colors.DarkGoldenrod, "violet" => Microsoft.UI.Colors.DarkViolet, "cyan" => Microsoft.UI.Colors.DarkCyan, "fuchsia" => Microsoft.UI.Colors.DarkMagenta, "lime" => Microsoft.UI.Colors.OliveDrab, "indigo" => Microsoft.UI.Colors.Indigo, "purple" => Microsoft.UI.Colors.Purple, "orange" => Microsoft.UI.Colors.DarkOrange, _ => Microsoft.UI.Colors.Crimson };
        var chip = new Border { Child = new TextBlock { Text = label, FontSize = 10, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) }, CornerRadius = new CornerRadius(8), Padding = new Thickness(5, 2, 5, 2), Background = new SolidColorBrush(color) };
        if (!tag.Key.StartsWith("multiKill.")) ToolTipService.SetToolTip(chip, Localization.Key("matchCard.tags." + tag.Key + (tag.Key.StartsWith("towerKill.") || tag.Key.EndsWith(".best") || tag.Key.EndsWith(".team") ? "Content" : ".content"), null, tag.Arguments)); return chip;
    }
    private Grid TagBar(HistoryCardTag[] tags)
    {
        var host = new Grid(); var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 }; host.Children.Add(row);
        var chips = tags.Select(CreateTag).ToArray();
        void Fit()
        {
            row.Children.Clear(); if (host.ActualWidth <= 0) return; double used = 0; int shown = 0;
            foreach (var chip in chips) { chip.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, 30)); if (used + chip.DesiredSize.Width + (shown < chips.Length - 1 ? 44 : 0) > host.ActualWidth) break; row.Children.Add(chip); used += chip.DesiredSize.Width + 3; shown++; }
            if (shown < chips.Length)
            {
                var more = new Button { Content = "+" + Math.Min(99, chips.Length - shown), FontSize = 10, Padding = new Thickness(4, 1, 4, 1), MinHeight = 20 };
                var overflow = new WrapPanel { Spacing = 3, MaxWidth = 520 }; foreach (var tag in tags.Skip(shown)) overflow.Children.Add(CreateTag(tag)); more.Flyout = new Flyout { Content = overflow }; row.Children.Add(more);
            }
        }
        host.SizeChanged += (_, _) => Fit(); host.Loaded += (_, _) => Fit(); return host;
    }
    private UIElement Radar(MatchParticipant[] team)
    {
        var keys = new[] { "totalDamageDealtToChampions", "totalDamageTaken", "goldEarned", "cs", "kda", "killParticipation", "totalHeal" };
        double Value(MatchParticipant p, string key) => (key == "killParticipation" ? MatchDetailsData.KillParticipation(p, _players.Where(t => t.TeamKey == p.TeamKey)) : MatchDetailsData.Stat(p, key)) ?? double.NaN;
        double Max(string key) => Math.Max(1, _players.Select(p => Value(p, key)).Where(double.IsFinite).DefaultIfEmpty(1).Max());
        return MatchCharts.Radar([L("伤害", "Damage"), L("承伤", "Taken"), L("经济", "Gold"), "CS", "KDA", L("参团", "KP"), L("治疗", "Healing")], [new(PlayerName(_self!), keys.Select(k => Value(_self!, k) / Max(k)).ToArray(), Microsoft.UI.Colors.RoyalBlue), new(L("队伍平均", "Team average"), keys.Select(k => team.Average(p => Value(p, k)) / Max(k)).ToArray(), Microsoft.UI.Colors.Gray)], 260);
    }
    private string RelativeTime()
    {
        var date = MatchData.Creation(_game).ToLocalTime(); var elapsed = DateTimeOffset.Now - date;
        if (elapsed.TotalDays > 3) return date.ToString("yyyy-MM-dd HH:mm");
        if (elapsed.TotalDays >= 1) return L($"{(int)elapsed.TotalDays} 天前", $"{(int)elapsed.TotalDays} days ago");
        if (elapsed.TotalHours >= 1) return L($"{(int)elapsed.TotalHours} 小时前", $"{(int)elapsed.TotalHours} hours ago");
        return L($"{Math.Max(0, (int)elapsed.TotalMinutes)} 分钟前", $"{Math.Max(0, (int)elapsed.TotalMinutes)} minutes ago");
    }
    private async Task ExpandAsync()
    {
        if (!_attached || _loadingDetails) return; _loadingDetails = true; int version = ++_detailsVersion;
        _error.Text = ""; _retryDetails.Visibility = Visibility.Collapsed; _detailsBusy.IsActive = true; _detailsBusy.Visibility = Visibility.Visible;
        try
        {
            if (!_detailsLoaded)
            {
                JsonElement game;
                if (_loadDetails != null) game = await _loadDetails(); else { var source = new PlayerDataSource(_backend); await source.ConfigureAsync(_server, _source); game = await source.DetailsAsync((long)_game.Number("gameId")); }
                if (!_attached || version != _detailsVersion) return;
                _details.Children.Add(new MatchDetailsView(_backend, game, _puuid, _server, _source, id => _openPlayer?.Invoke(id, _server), showReplay: false, openPlayerInBackground: _openPlayerInBackground == null ? null : id => _openPlayerInBackground(id, _server))); _detailsLoaded = true;
            }
            if (!_replayInitialized) { var replay = await _replays.InitializeAsync(_game, _source); if (!_attached || version != _detailsVersion) return; _replay = replay; _replayInitialized = true; RenderReplay(); }
            if (_attached && _expander.IsExpanded && (_replay.Text("state") is "downloading" or "checking")) _replayTimer.Start();
        }
        catch (Exception error) { if (_attached && version == _detailsVersion) { _error.Text = error.Message; _retryDetails.Content = L("重试读取详情", "Retry loading details"); _retryDetails.Visibility = Visibility.Visible; } }
        finally { if (version == _detailsVersion) { _loadingDetails = false; _detailsBusy.IsActive = false; _detailsBusy.Visibility = Visibility.Collapsed; } }
    }
    public void UpdateReplayState(JsonElement metadata) { if (metadata.Number("gameId") == _game.Number("gameId")) { _replay = metadata; RenderReplay(); } }
    private void RenderReplay()
    {
        var state = _replay.Text("state"); _replayButton.Visibility = _replay.ValueKind == JsonValueKind.Object ? Visibility.Visible : Visibility.Collapsed;
        var key = state switch { "download" => "download", "watch" => "watch", "incompatible" => "unavailable", "downloading" => "downloading", "checking" => "checking", _ => "label" };
        _replayButton.Content = Localization.Key("matchCard.replay." + key, null, new Dictionary<string, object?> { ["progress"] = _replay.Number("downloadProgress") });
        _replayButton.IsEnabled = !_replayRequest && (state is "download" or "watch"); _progress.Visibility = state == "downloading" ? Visibility.Visible : Visibility.Collapsed; _progress.Value = _replay.Number("downloadProgress");
    }
    private async Task ExecuteReplayAsync()
    {
        if (_replayRequest) return; _replayRequest = true; RenderReplay();
        try { await _replays.ExecuteAsync((long)_game.Number("gameId"), _replay.Text("state")); await RefreshReplayAsync(); }
        catch (Exception error) { _error.Text = error.Message; }
        finally { _replayRequest = false; RenderReplay(); }
    }
    private async Task RefreshReplayAsync()
    {
        if (!_attached || !_expander.IsExpanded || _loadingReplay) return; _loadingReplay = true; int lifecycle = _lifecycleVersion;
        try { var replay = await _replays.MetadataAsync((long)_game.Number("gameId")); if (!_attached || lifecycle != _lifecycleVersion) return; _replay = replay; RenderReplay(); if (_replay.Text("state") is not ("downloading" or "checking")) _replayTimer.Stop(); else _replayTimer.Start(); }
        catch (Exception error) { if (_attached && lifecycle == _lifecycleVersion) { _error.Text = error.Message; _replayTimer.Stop(); } }
        finally { if (lifecycle == _lifecycleVersion) _loadingReplay = false; }
    }
}

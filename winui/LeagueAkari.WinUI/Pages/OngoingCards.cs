using System.Text.Json;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class OngoingPage
{
    private static SolidColorBrush MatchBrush(NativeRgb color) => new(global::Windows.UI.Color.FromArgb(255, color.R, color.G, color.B));
    private bool Private => NativeAppearance.Current?.StreamerMode == true;
    private string SelfPuuid => _client.Field("summoner").Field("me").Text("puuid");
    private string Position(string id) => _state.Field("positionAssignments").Field(id).Text("position", _state.Field("positionAssignments").Field(id).Text("selected"));
    private int Champion(string id) => (int)_state.Field("championSelections").Field(id).TryNumber();
    private string ChampionName(int champion) => _resources.Field("champions").Field(champion.ToString()).Text("name", L("英雄 ", "Champion ") + champion);
    private string PrivacyName(string id, string name, string tag = "") => Private ? ChampionName(Champion(id)) : name + (tag.Length == 0 ? "" : " #" + tag);
    private bool Setting(string key, bool fallback) => _settings.Field(key).ValueKind is JsonValueKind.True or JsonValueKind.False ? _settings.Boolean(key) : fallback;
    private bool TagEnabled(string key, bool fallback = true) => _settings.Field("playerCardTags").Field(key).ValueKind is JsonValueKind.True or JsonValueKind.False ? _settings.Field("playerCardTags").Boolean(key) : fallback;
    private static string K(string key, params (string Key, object? Value)[] args) => Localization.Key("ongoingGame." + key, null, args.ToDictionary(a => a.Key, a => a.Value));
    private IEnumerable<string> AllPlayers() => _state.Field("teams").ValueKind == JsonValueKind.Object ? _state.Field("teams").EnumerateObject().SelectMany(t => t.Value.Items().Select(p => p.GetString() ?? "")).Where(p => p.Length > 0).Distinct() : [];
    private bool IsJungler(string id)
    {
        var spells = _state.Field("additional").Field("spells").Field(id);
        return OngoingCardTags.IsJungler(Position(id), spells);
    }
    private async Task LoadAnalysesAsync()
    {
        var players = AllPlayers().ToHashSet(StringComparer.Ordinal);
        foreach (var removed in _analysis.Keys.Concat(_analysisKeys.Keys).Distinct().Where(id => !players.Contains(id)).ToArray()) { _analysis.Remove(removed); _analysisKeys.Remove(removed); }
        foreach (var id in players)
        {
            var raw = _state.Field("analysis").Field("players").Field(id);
            if (raw.ValueKind == JsonValueKind.Object) { _analysis[id] = raw; continue; }
            var history = _all.Field("matchHistory").Field(id);
            var jungle = Setting("showJunglePathing", true) && (IsJungler(id) || Setting("showJunglePathingForAllPlayers", false));
            if (!IsRefreshEnabled) break;
            var details = _all.Field("gameDetails");
            var key = (history.ValueKind == JsonValueKind.Object ? history.GetRawText() : "") + (details.ValueKind == JsonValueKind.Object ? details.GetRawText() : "") + jungle + TagEnabled("showEasyGankTag") + _settings.Number("gameDetailsLoadCount");
            if (_analysisKeys.GetValueOrDefault(id) == key) continue;
            try { _analysis[id] = await _backend.CallAsync("in-game-send-main", "generatePlayerAnalysis", id, new { includeJungle = jungle, includeDetails = TagEnabled("showEasyGankTag") }); _analysisKeys[id] = key; }
            catch { /* Native summaries remain available if timeline loading failed. */ }
        }
    }
    private string[] OrderPlayers(string[] players)
    {
        HistorySummary Summary(string id) => MatchData.Summarize(History(id), id);
        return _settings.Text("orderPlayerBy", "default") switch
        {
            "win-rate" => players.OrderByDescending(id => Summary(id).Wins / (double)Math.Max(1, Summary(id).Count)).ToArray(),
            "kda" => players.OrderByDescending(id => Summary(id).Kda).ToArray(),
            "akari-score" => players.OrderByDescending(id => Summary(id).AkariScore).ToArray(),
            "position" => players.OrderBy(id => Array.IndexOf(new[] { "TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY", "NONE" }, Position(id)) is var index && index >= 0 ? index : 6).ToArray(),
            "premade-team" => players.OrderByDescending(id => Premades().FirstOrDefault(g => g.Value.Contains(id)).Value?.Length ?? 0).ThenBy(id => Premade(id)).ToArray(),
            _ => players
        };
    }
    private Dictionary<string, string[]> Premades()
    {
        var map = _state.Field("mergedPremadeTeamMap");
        if (map.ValueKind != JsonValueKind.Object || _state.Field("queryStage").Text("phase") is "lobby" or "unavailable") return [];
        return map.EnumerateObject().Where(p => p.Value.TryNumber() > 0).GroupBy(p => (int)p.Value.TryNumber()).ToDictionary(g => ((char)('A' + g.Key - 1)).ToString(), g => g.Select(p => p.Name).ToArray());
    }
    private string Premade(string id) => Premades().FirstOrDefault(g => g.Value.Contains(id)).Key ?? "";
    private static global::Windows.UI.Color PremadeColor(string id) => id.Length == 0 ? Microsoft.UI.Colors.SlateGray : new[] { Microsoft.UI.Colors.Teal, Microsoft.UI.Colors.RoyalBlue, Microsoft.UI.Colors.Olive, Microsoft.UI.Colors.ForestGreen, Microsoft.UI.Colors.Sienna, Microsoft.UI.Colors.IndianRed }[Math.Abs(id[0] - 'A') % 6];
    private void BuildOrder()
    {
        foreach (var (key, label) in new[] { ("default", L("默认排序", "Default order")), ("position", L("按位置", "Position")), ("premade-team", L("按小队", "Premade team")), ("win-rate", L("按胜率", "Win rate")), ("kda", "KDA"), ("akari-score", "Akari Score") }) _order.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        _order.SelectedIndex = 0;
        _order.SelectionChanged += async (_, _) => { if (_applyingToolbar || !_subscribed) return; await RunAsync(async () => { await _backend.CallAsync("setting-factory-main", "set", "ongoing-game-main", "orderPlayerBy", (_order.SelectedItem as ComboBoxItem)?.Tag); await RefreshAsync(); }); };
    }
    private void SyncToolbar()
    {
        _applyingToolbar = true;
        for (int index = 0; index < _order.Items.Count; index++)
        {
            var item = (ComboBoxItem)_order.Items[index]; var labels = new[] { L("默认排序", "Default order"), L("按位置", "Position"), L("按小队", "Premade team"), L("按胜率", "Win rate"), "KDA", "Akari Score" }; item.Content = labels[index];
            if ((string)item.Tag == _settings.Text("orderPlayerBy", "default")) _order.SelectedIndex = index;
        }
        SyncQueue();
        if (_clearDraft != null) _clearDraft.Visibility = _state.Field("draft").ValueKind == JsonValueKind.Object ? Visibility.Visible : Visibility.Collapsed;
        _applyingToolbar = false;
    }
    private UIElement TeamChips(string[] players)
    {
        var row = Row();
        foreach (var group in Premades().Where(g => g.Value.All(players.Contains)).OrderBy(g => g.Value.Length))
        {
            var summaries = group.Value.Select(id => MatchData.Summarize(History(id), id)).ToArray(); var found = false; var streak = 0;
            foreach (var player in summaries) { if (!found && player.Count >= 13 && player.Wins / (double)player.Count >= .9) found = true; else streak += player.WinningStreak; }
            var win = found && group.Value.Length >= 3 && streak / (double)(group.Value.Length - 1) >= 4;
            var loss = group.Value.Length >= 2 && summaries.All(s => s.Count >= 2 && s.Wins / (double)s.Count <= .25);
            var label = K("teamTags.premade", ("size", group.Value.Length)) + (win ? " · " + K("teamTags.winRateTeam") : loss ? " · " + K("teamTags.loseRateTeam") : "");
            row.Children.Add(TagChip(new(label, string.Join("\n", group.Value.Select(id => PrivacyName(id, _all.Field("summoner").Field(id).Text("gameName")))), 0)));
        }
        return row;
    }
    private UIElement RankSummary(string id)
    {
        var ranked = _all.Field("rankedStats").Field(id); var queues = ranked.Field("queueMap"); if (queues.ValueKind != JsonValueKind.Object) queues = ranked.Field("queues");
        var row = Row();
        foreach (var kind in new[] { "RANKED_SOLO_5x5", "RANKED_FLEX_SR" })
        {
            var rank = queues.Field(kind); if (rank.ValueKind == JsonValueKind.Undefined) rank = queues.Items().FirstOrDefault(q => q.Text("queueType") == kind);
            var tier = rank.Text("tier", "UNRANKED"); if (!PlayerRankEntry.IsTier(tier)) tier = "UNRANKED"; var text = Localization.Key("common.shortTiers." + tier, tier) + (tier == "UNRANKED" ? "" : $" {(rank.Text("division", rank.Text("rank")) == "NA" ? "" : rank.Text("division", rank.Text("rank")))} {rank.Number("leaguePoints"):0}");
            var label = new TextBlock { Text = text, FontSize = 11 };
            var games = rank.Number("wins") + rank.Number("losses"); ToolTipService.SetToolTip(label, $"{kind}\n{rank.Number("wins"):0} / {rank.Number("losses"):0} ({rank.Number("wins") / Math.Max(1, games):P0})\n{L("最高段位", "Peak tier")} {Localization.Key("common.shortTiers." + rank.Text("highestTier"), rank.Text("highestTier"))}"); row.Children.Add(label);
        }
        return new Button { Content = row, Padding = new Thickness(0), BorderThickness = new Thickness(0), Flyout = new Flyout { Content = RankTable(ranked) } };
    }
    private sealed record CardTag(string Label, string Description, int Color, string Hex = "", bool BlackText = false);
    private IEnumerable<OngoingTag> BuildTags(string id) => OngoingCardTags.Build(_settings.Field("playerCardTags"), _analysis.GetValueOrDefault(id), id == SelfPuuid, Premade(id), _all.Field("summoner").Field(id).Text("privacy") == "PRIVATE", IsJungler(id));
    private static string TagText(OngoingTagText text) => text.Key.StartsWith('$') ? text.Key[1..] : Localization.Key("ongoingGame.playerCard." + text.Key, null, text.Arguments.ToDictionary());
    private static SolidColorBrush TagBrush(string hex)
    {
        var value = hex.TrimStart('#'); uint number = Convert.ToUInt32(value, 16);
        return new SolidColorBrush(global::Windows.UI.Color.FromArgb(value.Length == 8 ? (byte)(number >> 24) : (byte)255, (byte)(number >> 16), (byte)(number >> 8), (byte)number));
    }
    private static Button TagButton(string label, string color, bool black = false)
    {
        var background = TagBrush(color); var foreground = new SolidColorBrush(black ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
        var chip = new Button { Content = new TextBlock { Text = label, FontSize = 11, Foreground = foreground }, Padding = new Thickness(4, 2, 4, 2), BorderThickness = new Thickness(0), MinWidth = 0, MinHeight = 15, CornerRadius = new(2), Background = background, Foreground = foreground };
        // Keep the tag's semantic color in native pointer/focus states. The default
        // light button hover brush otherwise makes its white label disappear.
        foreach (var key in new[] { "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonBackgroundDisabled" }) chip.Resources[key] = background;
        foreach (var key in new[] { "ButtonForegroundPointerOver", "ButtonForegroundPressed", "ButtonForegroundDisabled" }) chip.Resources[key] = foreground;
        return chip;
    }
    private static FrameworkElement TagChip(CardTag tag)
    {
        var colors = new[] { "#0f6f68", "#b81b86", "#870808", "#18571c", "#8f411e", "#e7da30" };
        var chip = TagButton(tag.Label, tag.Hex.Length > 0 ? tag.Hex : colors[tag.Color % colors.Length], tag.BlackText);
        if (tag.Description.Length > 0) AttachTagDetails(chip, new TextBlock { Text = tag.Description, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 });
        return chip;
    }
    private Button TagChip(OngoingTag tag, JsonElement analysis)
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        var chip = TagButton(TagText(tag.Label), dark && tag.DarkColor.Length > 0 ? tag.DarkColor : tag.Color, dark ? tag.DarkBlackText : tag.BlackText);
        chip.Tag = tag.Id;
        FrameworkElement? content = null;
        if (tag.Id == "suspicious-flash-position") content = FlashDetails(analysis.Field("spells"));
        else if (tag.Id is "great-performance" or "akari-score")
        {
            var panel = new StackPanel { Spacing = 8, MaxWidth = 270 };
            if (tag.Id == "great-performance") panel.Children.Add(TagParagraph(TagText(tag.Details[0])));
            panel.Children.Add(ScoreDetails(analysis.Field("akariScore"), tag.Id == "great-performance" ? 1 : 3));
            if (tag.Id == "akari-score") panel.Children.Add(TagParagraph(TagText(tag.Details[0])));
            content = panel;
        }
        else if (tag.Details.Length > 0)
        {
            var panel = new StackPanel { Spacing = 5, MaxWidth = tag.Id == "average-damage-gold-efficiency" ? 288 : 240 };
            foreach (var line in tag.Details) panel.Children.Add(TagParagraph(TagText(line)));
            content = panel;
        }
        if (content != null) AttachTagDetails(chip, content);
        return chip;
    }
    private static TextBlock TagParagraph(string text) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private static Flyout AttachTagDetails(Button chip, FrameworkElement content, bool clickOpen = true)
    {
        var flyout = new Flyout { Content = content }; if (clickOpen) chip.Flyout = flyout;
        bool overChip = false, overContent = false; int revision = 0;
        async void CloseLater() { int current = ++revision; await Task.Delay(100); if (current == revision && !overChip && !overContent) flyout.Hide(); }
        chip.PointerEntered += async (_, _) => { overChip = true; int current = ++revision; await Task.Delay(50); if (current != revision || !overChip || !chip.IsLoaded || chip.XamlRoot == null) return; flyout.ShowAt(chip, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { ShowMode = Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.Transient }); };
        chip.PointerExited += (_, _) => { overChip = false; CloseLater(); };
        content.PointerEntered += (_, _) => { overContent = true; ++revision; };
        content.PointerExited += (_, _) => { overContent = false; CloseLater(); };
        chip.Unloaded += (_, _) => { ++revision; overChip = overContent = false; flyout.Hide(); };
        return flyout;
    }
    private static FrameworkElement ScoreDetails(JsonElement score, int precision)
    {
        var panel = new StackPanel { Spacing = 8, Width = 256 };
        var total = Row(); total.Children.Add(new TextBlock { Text = Localization.Key("akariScore.title"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }); total.Children.Add(new TextBlock { Text = score.Field("total").ValueKind == JsonValueKind.Number ? score.Number("total").ToString("F" + precision, System.Globalization.CultureInfo.InvariantCulture) : Localization.Key("akariScore.na") }); panel.Children.Add(total);
        foreach (var part in OngoingCardTags.ScoreParts(score))
        {
            var row = new StackPanel { Spacing = 3 }; var values = new Grid(); values.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); values.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            values.Children.Add(TagParagraph(Localization.Key(part.LabelKey))); var value = new TextBlock { FontSize = 12, Text = (part.Value?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? Localization.Key("akariScore.na")) + " / " + part.Maximum.ToString("0", System.Globalization.CultureInfo.InvariantCulture) }; Grid.SetColumn(value, 1); values.Children.Add(value); row.Children.Add(values);
            row.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = part.Progress, Height = 5 }); panel.Children.Add(row);
        }
        return panel;
    }
    private static FrameworkElement FlashDetails(JsonElement spells)
    {
        double d = spells.Number("flashOnD"), f = spells.Number("flashOnF"), total = d + f;
        var panel = new StackPanel { Spacing = 8, MaxWidth = 320 }; panel.Children.Add(new TextBlock { Text = K("playerCard.suspiciousFlashPositionPopoverTitle"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var body = Row(); var chart = new Canvas { Width = 112, Height = 112 }; double angle = d / total * 2 * Math.PI;
        foreach (var (start, end, color, key, count) in new[] { (-Math.PI / 2, angle - Math.PI / 2, "#2563eb", "D", d), (angle - Math.PI / 2, Math.PI * 1.5, "#f97316", "F", f) })
        {
            var figure = new PathFigure { StartPoint = new global::Windows.Foundation.Point(56, 56), IsClosed = true, IsFilled = true }; figure.Segments.Add(new LineSegment { Point = new global::Windows.Foundation.Point(56 + 55 * Math.Cos(start), 56 + 55 * Math.Sin(start)) }); figure.Segments.Add(new ArcSegment { Point = new global::Windows.Foundation.Point(56 + 55 * Math.Cos(end), 56 + 55 * Math.Sin(end)), Size = new global::Windows.Foundation.Size(55, 55), IsLargeArc = end - start > Math.PI, SweepDirection = SweepDirection.Clockwise });
            var geometry = new PathGeometry(); geometry.Figures.Add(figure); var slice = new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, Fill = TagBrush(color), Stroke = new SolidColorBrush(Microsoft.UI.Colors.White), StrokeThickness = 1 }; ToolTipService.SetToolTip(slice, K("playerCard.suspiciousFlashPositionPopover" + key + "Label") + $": {count:0} ({count / total * 100:F1}%)"); chart.Children.Add(slice);
        }
        body.Children.Add(chart); var description = new StackPanel { Spacing = 6, MaxWidth = 190 }; description.Children.Add(TagParagraph(K("playerCard.suspiciousFlashPositionPopoverDescription")));
        foreach (var (key, count, color) in new[] { ("D", d, "#2563eb"), ("F", f, "#f97316") }) { var row = Row(); row.Children.Add(new Border { Width = 10, Height = 10, Background = TagBrush(color) }); row.Children.Add(TagParagraph(K("playerCard.suspiciousFlashPositionPopover" + key + "Count", ("count", count), ("rate", (count / total * 100).ToString("F1", System.Globalization.CultureInfo.InvariantCulture))))); description.Children.Add(row); }
        body.Children.Add(description); panel.Children.Add(body); panel.Children.Add(TagParagraph(K("playerCard.suspiciousFlashPositionPopoverTotal", ("count", total)))); return panel;
    }
    private UIElement Usage(string id, HistorySummary summary)
    {
        var row = Row(); var mastery = _all.Field("championMastery").Field(id); var entries = mastery.ValueKind == JsonValueKind.Object ? mastery.EnumerateObject().Select(p => p.Value) : mastery.Items();
        var champions = _settings.Text("showChampionUsage", "recent") == "mastery" ? entries.OrderByDescending(m => m.Number("championPoints")).Take(9).Select(m => (int)m.Number("championId")) : summary.ChampionCounts.OrderByDescending(c => c.Value).Take(9).Select(c => c.Key);
        foreach (var champion in champions)
        {
            var m = mastery.Field(champion.ToString()); var played = History(id).Where(g => MatchData.Self(g, id)?.ChampionId == champion).ToArray(); var stats = MatchData.Summarize(played, id);
            var button = new Button { Content = _assets.Icon("champion-summary", champion, 20), Padding = new Thickness(0), MinWidth = 20, MinHeight = 20 };
            ToolTipService.SetToolTip(button, $"{ChampionName(champion)}\n{L("场次", "Games")} {stats.Count} · {stats.Wins / (double)Math.Max(1, stats.Count):P0} · KDA {stats.Kda:F2}\n{L("熟练度", "Mastery")} {m.Number("championLevel"):0} · {m.Number("championPoints"):N0}" + (m.Number("championLevel") >= 60 ? " ★" : ""));
            button.Click += async (_, _) => { if (!_standalone) await CollectAsync(id, champion, null); }; row.Children.Add(button);
        }
        return row;
    }
    private MenuFlyout CardActions(string id)
    {
        var menu = new MenuFlyout(); void Add(string text, Func<Task> action) { var item = new MenuFlyoutItem { Text = text }; item.Click += async (_, _) => await RunAsync(action); menu.Items.Add(item); }
        if (!_standalone)
        {
            if (id != SelfPuuid && SelfPuuid.Length > 0) Add(K("playerCard.editTag"), () => EditTagAsync(id, PrivacyName(id, _all.Field("summoner").Field(id).Text("gameName"))));
            if (Champion(id) > 0) Add(K("playerCard.collectByChampion", ("champion", ChampionName(Champion(id)))), () => CollectAsync(id, Champion(id), null));
            if (Position(id) is { Length: > 0 } position && position != "NONE") Add(K("playerCard.collectByPosition", ("position", Localization.Key("common.positions." + position, position))), () => CollectAsync(id, null, position));
        }
        return menu;
    }
    private async Task CollectAsync(string id, int? champion, string? position)
    {
        if (_collectMatches != null) { _collectMatches(id, _server, champion, position); return; }
        var history = new HistoryPage(_backend, id, _server); history.Loaded += async (_, _) => await history.ConfigureCollectionAsync(champion, position);
        await LeagueAkari.WinUI.Services.NativeDialogs.ShowAsync(new ContentDialog { Title = PrivacyName(id, _all.Field("summoner").Field(id).Text("gameName")), Content = history, CloseButtonText = L("关闭", "Close"), XamlRoot = XamlRoot });
    }
}

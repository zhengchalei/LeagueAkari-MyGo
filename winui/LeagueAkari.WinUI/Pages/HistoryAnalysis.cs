using System.Text.Json;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class HistoryPage
{
    private static string SummaryLabel(string key) => Localization.Key("playerTabs.summary." + key);
    private static string ChampionLabel(string key) => Localization.Key("common.championAnalysis." + key);
    private static TextBlock SummaryText(string text) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static Grid SummaryRow(string label, FrameworkElement value)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(SummaryText(label)); Grid.SetColumn(value, 1); row.Children.Add(value); return row;
    }
    private static Grid SummaryRow(string label, string value) => SummaryRow(label, SummaryText(value));
    private static string WinsLosses(HistorySummary summary) => $"{summary.Wins} {SummaryLabel("winShort")} {summary.Losses} {SummaryLabel("lossShort")} ({summary.Wins / MatchData.NoZero(summary.Count):P0})";
    private static StackPanel ScoreBreakdown(SummaryAnalysis analysis)
    {
        var panel = new StackPanel { Spacing = 5, Width = 260 };
        panel.Children.Add(SummaryRow("Akari Score", analysis.Summary.AkariScore.ToString("F2")));
        foreach (var part in analysis.Scores)
        {
            panel.Children.Add(SummaryRow(Localization.Key("akariScore.parts." + part.Key), $"{part.Value:F2} / {part.Maximum:0}"));
            panel.Children.Add(new ProgressBar { Minimum = 0, Maximum = part.Maximum, Value = part.Value, Height = 5 });
        }
        return panel;
    }
    private static Button ScoreButton(SummaryAnalysis analysis) => new()
    {
        Content = analysis.Summary.AkariScore.ToString("F2"), Padding = new Thickness(3, 0, 3, 0),
        Flyout = new Flyout { Content = ScoreBreakdown(analysis) }
    };
    private void RenderSummary(JsonElement[] games)
    {
        _summary.Children.Clear();
        var analysis = HistorySummaryData.Analyze(games, _puuid); var value = analysis.Summary;
        if (games.Length == 0) { _summary.Children.Add(SummaryText(Localization.Text("当前条件下没有战绩", "No matches meet these conditions"))); return; }
        _summary.Children.Add(new TextBlock { Text = SummaryLabel("title"), FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 16 });
        _summary.Children.Add(SummaryRow(SummaryLabel("akariScore"), ScoreButton(analysis)));
        var kda = SummaryText(value.Kda.ToString("F2")); ToolTipService.SetToolTip(kda, $"{value.Kills:0} / {value.Deaths:0} / {value.Assists:0}");
        _summary.Children.Add(SummaryRow(SummaryLabel("avgKda"), kda));
        foreach (var (key, metric) in new[] { ("avgKp", value.KillParticipation), ("avgDmg", value.DamageShare), ("avgDmgTaken", value.TakenShare), ("avgGold", value.GoldShare) }) _summary.Children.Add(SummaryRow(SummaryLabel(key), metric.ToString("P0")));
        _summary.Children.Add(SummaryRow(SummaryLabel("avgCsPerMinute"), value.CsPerMinute.ToString("F1")));
        if (analysis.ShowActiveSession(_page)) _summary.Children.Add(SummaryRow(SummaryLabel("activeSession"), $"{value.ActiveSessionWins} {SummaryLabel("winShort")} {value.ActiveSessionLosses} {SummaryLabel("lossShort")} ({analysis.ActiveSessionWinRate:P0})"));
        var results = new WrapPanel { MaxWidth = 180 }; results.Children.Add(SummaryText(WinsLosses(value)));
        int streak = analysis.VisibleStreak(_page);
        if (streak != 0) results.Children.Add(new Border { Padding = new Thickness(3, 1, 3, 1), CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(streak > 0 ? Microsoft.UI.Colors.ForestGreen : Microsoft.UI.Colors.Firebrick), Child = new TextBlock { Text = Localization.Text($"{Math.Abs(streak)} 连{(streak > 0 ? "胜" : "败")}", $"{Math.Abs(streak)} {(streak > 0 ? "win" : "loss")} streak"), Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), FontSize = 11 } });
        _summary.Children.Add(SummaryRow(SummaryLabel("winLose"), results));
        if (value.BlueCount + value.RedCount > 0)
        {
            var sides = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            sides.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue) }); sides.Children.Add(SummaryText(value.BlueCount.ToString())); sides.Children.Add(SummaryText("/"));
            sides.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Microsoft.UI.Colors.Coral) }); sides.Children.Add(SummaryText(value.RedCount.ToString())); _summary.Children.Add(SummaryRow(SummaryLabel("teamSides"), sides));
        }
        var champions = new WrapPanel { MaxWidth = 125 };
        foreach (var champion in HistorySummaryData.Champions(games, _puuid))
        {
            var icon = new Grid(); icon.Children.Add(_assets.Icon("champion-summary", champion.ChampionId, 25));
            icon.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(Microsoft.UI.Colors.Black), Opacity = .85, Padding = new Thickness(2, 0, 2, 0), Child = new TextBlock { Text = champion.Analysis.Summary.Count.ToString(), Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), FontSize = 10 } });
            var body = new StackPanel();
            var presenter = new Style(typeof(FlyoutPresenter));
            presenter.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 760d));
            var flyout = new Flyout { FlyoutPresenterStyle = presenter, Content = new ScrollViewer { Content = body, MaxHeight = 520, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
            var button = new Button { Content = icon, Padding = new Thickness(1), Flyout = flyout };
            flyout.Opening += async (_, _) => { try { await RenderChampionAnalysisAsync(body, champion); } catch (Exception ex) { body.Children.Clear(); body.Children.Add(SummaryText(ex.Message)); } };
            ToolTipService.SetToolTip(button, ChampionLabel("overviewTab")); champions.Children.Add(button);
        }
        if (champions.Children.Count > 0) _summary.Children.Add(SummaryRow(SummaryLabel("champions"), champions));
    }
    private async Task RenderChampionAnalysisAsync(StackPanel body, ChampionSummary champion)
    {
        body.Children.Clear(); body.Spacing = 8; body.MaxWidth = 720;
        var analysis = champion.Analysis; var summary = analysis.Summary;
        var catalog = await _assets.EntryAsync("champion-summary", champion.ChampionId);
        var title = new TextBlock { Text = catalog.Text("name", champion.ChampionId.ToString()), FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.Bold };
        var header = new WrapPanel(); header.Children.Add(_assets.Icon("champion-summary", champion.ChampionId, 32)); header.Children.Add(title);
        var collect = new Button { Content = ChampionLabel("collectShort"), IsEnabled = !_loading && _collectStop is null };
        collect.Click += async (_, _) => { collect.IsEnabled = false; try { await ConfigureCollectionAsync(champion.ChampionId, null); } catch (Exception ex) { Error(ex.Message); } finally { collect.IsEnabled = !_loading && _collectStop is null; } };
        header.Children.Add(collect); body.Children.Add(header); body.Children.Add(SummaryText($"{summary.Count} · {WinsLosses(summary)}"));
        if (analysis.VisibleStreak(0) is int streak && streak != 0) body.Children.Add(SummaryText(Localization.Text($"{Math.Abs(streak)} 连{(streak > 0 ? "胜" : "败")}", $"{Math.Abs(streak)} {(streak > 0 ? "win" : "loss")} streak")));
        if (analysis.ShowActiveSession(0)) body.Children.Add(SummaryText(SummaryLabel("activeSession") + $" · {summary.ActiveSessionWins} / {summary.ActiveSessionLosses}"));
        var tabs = new TabView { IsAddTabButtonVisible = false, TabWidthMode = TabViewWidthMode.SizeToContent, Width = Math.Clamp(ActualWidth - 48, 320, 680) }; body.Children.Add(tabs);
        var overview = new WrapPanel();
        StackPanel Section(string name, int column) { var panel = new StackPanel { Spacing = 4, Width = 205, Margin = new Thickness(0, 0, 12, 8) }; panel.Children.Add(new TextBlock { Text = ChampionLabel(name), FontWeight = Microsoft.UI.Text.FontWeights.Bold }); overview.Children.Add(panel); return panel; }
        var performance = Section("performance", 0); var combat = Section("damage", 1); var economy = Section("economy", 2);
        void Metric(Panel panel, string key, double value, string format = "P0") => panel.Children.Add(SummaryRow(ChampionLabel(key), value.ToString(format)));
        if (analysis.Arena.Count > 0)
        {
            performance.Children.Add(SummaryText(ChampionLabel("arena") + " · " + analysis.Arena.Count));
            Metric(performance, "top1Rate", analysis.Arena.Top1Rate); Metric(performance, "topHalfRate", analysis.Arena.TopHalfRate); Metric(performance, "averagePlacement", analysis.Arena.AveragePlacement, "F1");
            if (analysis.NormalCount > 0) performance.Children.Add(SummaryText(ChampionLabel("standardModes") + " · " + analysis.NormalCount + " · " + WinsLosses(MatchData.Summarize(champion.Games.Where(g => g.Text("gameMode") != "CHERRY"), _puuid))));
        }
        performance.Children.Add(SummaryRow("Akari Score", ScoreButton(analysis))); Metric(performance, "winRate", analysis.WinRate); Metric(performance, "averageKda", summary.Kda, "F2");
        performance.Children.Add(SummaryText($"{summary.Kills / MatchData.NoZero(summary.Count):F1} / {summary.Deaths / MatchData.NoZero(summary.Count):F1} / {summary.Assists / MatchData.NoZero(summary.Count):F1}"));
        Metric(performance, "killParticipation", summary.KillParticipation); Metric(performance, "kdaVariation", summary.KdaCv, "F2"); Metric(performance, "visionScore", summary.VisionScore, "F1"); Metric(performance, "visionShare", analysis.Metrics["visionShare"]);
        if (analysis.Metrics.TryGetValue("pings", out double pings)) Metric(performance, "pings", pings, "F1"); if (summary.EnemyMissingPings is double missing) Metric(performance, "missingPings", missing, "F1");
        Metric(combat, "damagePerMinute", analysis.Metrics["damagePerMinute"], "F0");
        foreach (string key in new[] { "damageShare", "damageToTeamMax", "damageToMatchMax", "damageTakenShare", "damageTakenToTeamMax", "damageTakenToMatchMax" }) Metric(combat, key, analysis.Metrics[key]);
        Metric(combat, "damageGoldEfficiency", summary.DamageGoldEfficiency); Metric(combat, "killDamageEfficiency", summary.KillDamageEfficiency, "F2"); if (summary.SoloKills is double solo) Metric(combat, "soloKills", solo, "F1");
        foreach (string key in new[] { "goldShare", "goldToTeamMax", "goldToMatchMax" }) Metric(economy, key, analysis.Metrics[key]); Metric(economy, "csPerMinute", summary.CsPerMinute, "F1");
        foreach (string key in new[] { "csShare", "csToTeamMax", "csToMatchMax", "towerDamageShare", "towerDamageToTeamMax", "towerDamageToMatchMax" }) Metric(economy, key, analysis.Metrics[key]);
        void Tab(string key, UIElement content) => tabs.TabItems.Add(new TabViewItem { Header = ChampionLabel(key), IsClosable = false, Content = new ScrollViewer { Content = content, MaxHeight = 310 } });
        Tab("overviewTab", overview);
        if (analysis.Positions is { } positions && positions.Values.Any(v => v > 0))
        {
            Tab("positionTab", SummaryPositions(positions));
        }
        AttachHistoryJungleTab(tabs, champion);
        tabs.SelectedIndex = 0;
        int revision = _revision;
        var entry = await _assets.EntryAsync("champion-summary", champion.ChampionId); title.Text = entry.Text("name", champion.ChampionId.ToString());
        if (_source.SupportsMastery && revision == _revision)
        {
            try
            {
                var values = await _source.MasteryAsync(_puuid); if (revision != _revision) return;
                var raw = values.Field("masteries").ValueKind == JsonValueKind.Array ? values.Field("masteries") : values;
                var mastery = raw.Items().FirstOrDefault(m => m.Number("championId") == champion.ChampionId);
                if (mastery.ValueKind == JsonValueKind.Object) Tab("masteryTab", SummaryMastery(mastery));
            }
            catch (Exception ex) { body.Children.Add(SummaryText(Localization.Text("熟练度：", "Mastery: ") + ex.Message)); }
        }
    }
    private static UIElement SummaryPositions(IReadOnlyDictionary<string, int> positions)
    {
        var panel = new WrapPanel(); var chart = new Canvas { Width = 192, Height = 192 };
        var legend = new StackPanel { Spacing = 8, MinWidth = 170 }; panel.Children.Add(chart); panel.Children.Add(legend);
        var colors = new Dictionary<string, global::Windows.UI.Color> { ["TOP"] = global::Windows.UI.Color.FromArgb(255, 224, 107, 107), ["JUNGLE"] = global::Windows.UI.Color.FromArgb(255, 85, 168, 121), ["MIDDLE"] = global::Windows.UI.Color.FromArgb(255, 215, 168, 77), ["BOTTOM"] = global::Windows.UI.Color.FromArgb(255, 93, 142, 214), ["UTILITY"] = global::Windows.UI.Color.FromArgb(255, 155, 121, 198) };
        double total = positions.Values.Sum(), angle = -Math.PI / 2;
        foreach (var entry in positions.Where(p => p.Value > 0).OrderByDescending(p => p.Value))
        {
            double end = angle + entry.Value / total * 2 * Math.PI;
            var polygon = new Polygon { Fill = new SolidColorBrush(colors[entry.Key]), Points = new PointCollection() };
            int steps = Math.Max(2, (int)Math.Ceiling((end - angle) * 30));
            for (int i = 0; i <= steps; i++) { double a = angle + (end - angle) * i / steps; polygon.Points.Add(new global::Windows.Foundation.Point(96 + Math.Cos(a) * 88, 96 + Math.Sin(a) * 88)); }
            for (int i = steps; i >= 0; i--) { double a = angle + (end - angle) * i / steps; polygon.Points.Add(new global::Windows.Foundation.Point(96 + Math.Cos(a) * 51, 96 + Math.Sin(a) * 51)); }
            string label = Localization.Key("common.positions." + entry.Key, entry.Key);
            ToolTipService.SetToolTip(polygon, $"{label} · {entry.Value} · {entry.Value / total:P0}"); chart.Children.Add(polygon);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; row.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(colors[entry.Key]) }); row.Children.Add(SummaryText($"{label} · {entry.Value} · {entry.Value / total:P0}")); legend.Children.Add(row); angle = end;
        }
        return panel;
    }
    private static StackPanel SummaryMastery(JsonElement mastery)
    {
        var panel = new StackPanel { Spacing = 8 };
        void Metric(string label, string value) => panel.Children.Add(SummaryRow(Localization.Key("playerTabs.championMastery." + label), value));
        Metric("levelLabel", mastery.Number("championLevel").ToString("0"));
        Metric("pointsLabel", mastery.Number("championPoints").ToString("N0"));
        if (mastery.Text("highestGrade").Length > 0) Metric("highestGradeLabel", mastery.Text("highestGrade"));
        if (mastery.Number("championSeasonMilestone") > 0) Metric("seasonMilestoneLabel", mastery.Number("championSeasonMilestone").ToString("0"));
        if (mastery.Number("tokensEarned") > 0) Metric("tokensEarnedLabel", mastery.Number("tokensEarned").ToString("0"));
        if (mastery.Number("lastPlayTime") > 0) Metric("lastPlayTimeLabel", DateTimeOffset.FromUnixTimeMilliseconds((long)mastery.Number("lastPlayTime")).ToLocalTime().ToString("yyyy-MM-dd"));
        if (mastery.Field("championPointsSinceLastLevel").ValueKind == JsonValueKind.Number && mastery.Field("championPointsUntilNextLevel").ValueKind == JsonValueKind.Number)
            Metric("progressLabel", $"{mastery.Number("championPointsSinceLastLevel"):N0} / {mastery.Number("championPointsUntilNextLevel"):N0}");
        return panel;
    }
}

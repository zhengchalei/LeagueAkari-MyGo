using System.Text.Json;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace LeagueAkari.WinUI.Pages;

/// <summary>Native projections of the original first-clear, early-gank and jungle map analysis.</summary>
public sealed class JunglePathingView : UserControl
{
    private readonly JsonElement _analysis;
    private readonly GameAssets _assets;
    private readonly StackPanel _body = new() { Spacing = 8, MaxWidth = 760 };
    private readonly ComboBox _champions = new() { MinWidth = 160 };
    private readonly int _currentChampion;
    private static string L(string zh, string en) => Localization.Text(zh, en);
    private static string K(string key) => Localization.Key("ongoingGame.junglePathing." + key, key);
    public JunglePathingView(GameAssets assets, JsonElement analysis, int champion, bool inline = false)
    {
        _assets = assets; _analysis = analysis; _currentChampion = champion;
        var jungle = analysis.Field("jungle");
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        content.Children.Add(Map(jungle, 46));
        content.Children.Add(new TextBlock { Text = $"{jungle.Number("gamesAnalyzed"):0} · {Preference(jungle)}\n{L("上", "Top")} {jungle.Number("topZoneWeightSum"):0} / {L("中", "Mid")} {jungle.Number("midZoneWeightSum"):0} / {L("下", "Bot")} {jungle.Number("botZoneWeightSum"):0}", FontSize = 10, TextWrapping = TextWrapping.Wrap });
        if (inline) Content = _body;
        else
        {
            var flyout = new Flyout { Content = new ScrollViewer { Content = _body, MaxHeight = 650 } };
            Content = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, Flyout = flyout, Padding = new Thickness(4) };
        }
        _champions.Items.Add(new ComboBoxItem { Content = K("overall"), Tag = 0 });
        var data = analysis.Field("champions");
        if (data.ValueKind == JsonValueKind.Object)
            foreach (var item in data.EnumerateObject().Where(p => int.TryParse(p.Name, out _) && p.Value.Field("jungle").ValueKind == JsonValueKind.Object).OrderByDescending(p => p.Value.Field("jungle").Number("gamesAnalyzed")))
                _champions.Items.Add(new ComboBoxItem { Content = _assets.Icon("champion-summary", int.Parse(item.Name), 23), Tag = int.Parse(item.Name) });
        _champions.SelectedIndex = 0;
        _champions.SelectionChanged += (_, _) => Render();
        Render();
    }
    private void Render()
    {
        _body.Children.Clear(); _body.Children.Add(_champions);
        int champion = _champions.SelectedItem is ComboBoxItem { Tag: int selected } ? selected : 0;
        var stats = champion == 0 ? _analysis.Field("jungle") : _analysis.Field("champions").Field(champion.ToString()).Field("jungle");
        _body.Children.Add(new TextBlock { Text = $"{K("gamesAnalyzed").Replace("{{count}}", stats.Number("gamesAnalyzed").ToString("0"))} · {Preference(stats)}", FontSize = 14 });
        _body.Children.Add(Map(stats, 260));
        _body.Children.Add(MatchCharts.Bars(new[] { (K("zoneTiny.top"), stats.Number("avgTopZonePercentage") * 100, Microsoft.UI.Colors.Crimson), (K("zoneTiny.mid"), stats.Number("avgMidZonePercentage") * 100, Microsoft.UI.Colors.Goldenrod), (K("zoneTiny.bot"), stats.Number("avgBotZonePercentage") * 100, Microsoft.UI.Colors.RoyalBlue) }, 280));
        _body.Children.Add(new TextBlock { Text = L("首次清野", "First clear"), FontSize = 14 });
        _body.Children.Add(FirstClearMap(stats, 260));
        _body.Children.Add(new TextBlock { Text = L("营地圆点：己方首清，金色边框：入侵首清；橙色叉：3分钟内击杀参与，紫色叉：3–4分钟击杀参与", "Camp dots: own start; gold border: invade start. Orange crosses: takedowns by 3 minutes; purple crosses: takedowns during minutes 3–4."), FontSize = 10, TextWrapping = TextWrapping.Wrap });
        var clear = stats.Field("firstClearCamp");
        foreach (var side in new[] { "blue", "red", "blueInvade", "redInvade" })
        {
            var camps = clear.Field(side); if (camps.ValueKind != JsonValueKind.Object) continue;
            double starts = camps.EnumerateObject().Sum(c => c.Value.TryNumber()), games = clear.Number(side.StartsWith("blue", StringComparison.Ordinal) ? "blueGames" : "redGames");
            string label = side switch { "blue" => L("蓝色方", "Blue side"), "red" => L("红色方", "Red side"), "blueInvade" => L("蓝色方入侵", "Blue side invade"), _ => L("红色方入侵", "Red side invade") };
            _body.Children.Add(new TextBlock { Text = label + $" · {starts:0}/{games:0} ({(games > 0 ? starts / games : 0):P0})", FontSize = 11 });
            foreach (var camp in camps.EnumerateObject().Where(c => c.Value.TryNumber() > 0).OrderByDescending(c => c.Value.TryNumber()))
                _body.Children.Add(new TextBlock { Text = CampName(camp.Name) + $" · {camp.Value.TryNumber():0} ({camp.Value.TryNumber() / starts:P0})", FontSize = 11 });
            if (starts == 0) _body.Children.Add(new TextBlock { Text = L("暂无首清记录", "No start-camp samples"), FontSize = 11 });
        }
        var early = stats.Field("earlyGank"); _body.Children.Add(new TextBlock { Text = L("早期抓人", "Early ganks"), FontSize = 14 });
        foreach (var level in new[] { 3, 4 }) _body.Children.Add(new TextBlock { Text = $"Lv {level} · {MatchDetailsData.Format(MatchDetailsData.Number(early.Field("level" + level + "GankRate")), "P1")} ({MatchDetailsData.Format(MatchDetailsData.Number(early.Field("level" + level + "GankCount")))})", FontSize = 11 });
        var byTeam = early.Field("byTeam");
        foreach (var (side, title) in new[] { ("blue", L("蓝色方", "Blue side")), ("red", L("红色方", "Red side")) })
        {
            if (byTeam.Field(side + "Games").ValueKind != JsonValueKind.Number) continue;
            _body.Children.Add(new TextBlock { Text = title + " · " + byTeam.Number(side + "Games").ToString("N0") + L(" 场", " games"), FontSize = 11 });
            foreach (var level in new[] { 3, 4 })
            {
                var prefix = side + "Level" + level;
                _body.Children.Add(new TextBlock { Text = $"Lv {level} · {MatchDetailsData.Format(MatchDetailsData.Number(byTeam.Field(prefix + "GankRate")), "P1")} ({MatchDetailsData.Format(MatchDetailsData.Number(byTeam.Field(prefix + "GankCount")))})", FontSize = 11 });
            }
        }
        foreach (var (lane, title) in new[] { ("Top", L("上路抓人", "Top ganks")), ("Mid", L("中路抓人", "Mid ganks")), ("Bot", L("下路抓人", "Bot ganks")) })
            if (stats.Field("total" + lane + "Ganks").ValueKind == JsonValueKind.Number)
                _body.Children.Add(new TextBlock { Text = title + " · " + stats.Number("total" + lane + "Ganks").ToString("N0") + " / " + L("平均", "average") + " " + MatchDetailsData.Format(MatchDetailsData.Number(stats.Field("avg" + lane + "Ganks")), "F2"), FontSize = 11 });
        _body.Children.Add(new TextBlock { Text = L("战略目标", "Objectives"), FontSize = 14 });
        var objectives = stats.Field("objectives");
        foreach (var (field, label, format) in new[] { ("firstDragonRate", L("首条小龙率", "First dragon rate"), "P1"), ("soloDragonRate", L("单人小龙率", "Solo dragon rate"), "P1"), ("avgDragons", L("平均小龙", "Average dragons"), "F2"), ("avgVoidgrubs", L("平均巢虫", "Average void grubs"), "F2"), ("avgHeralds", L("平均先锋", "Average heralds"), "F2"), ("avgBarons", L("平均大龙", "Average barons"), "F2") })
            if (objectives.Field(field).ValueKind == JsonValueKind.Number) _body.Children.Add(new TextBlock { Text = label + " · " + objectives.Number(field).ToString(format), FontSize = 11 });
        foreach (var (field, label) in new[] { ("avgFirstDragonTime", L("首条小龙时间", "First dragon time")), ("avgFirstVoidgrubTime", L("首次巢虫时间", "First void grub time")), ("avgFirstHeraldTime", L("首个先锋时间", "First herald time")), ("avgFirstBaronTime", L("首个大龙时间", "First baron time")) })
            if (objectives.Field(field).ValueKind == JsonValueKind.Number) _body.Children.Add(new TextBlock { Text = label + " · " + MatchCharts.Time(objectives.Number(field) * 1000), FontSize = 11 });
        _body.Children.Add(new TextBlock { Text = Localization.Key("ongoingGame.junglePathing.algorithmDetails", L("基于前14分钟的位置与击杀参与计算", "Computed from positions and takedown participation during the first 14 minutes")), FontSize = 10, TextWrapping = TextWrapping.Wrap });
    }
    private static string CampName(string key) => K(key switch { "red" => "campRed", "blue" => "campBlue", "wolves" => "campWolves", "raptors" => "campRaptors", _ => key });
    private static Canvas FirstClearMap(JsonElement stats, int size)
    {
        var map = BaseMap(size); var clear = stats.Field("firstClearCamp");
        var camps = new (double X, double Y, string Camp, string Side)[] { (3830, 7880, "blue", "blue"), (3800, 6440, "wolves", "blue"), (7760, 4010, "red", "blue"), (6970, 5460, "raptors", "blue"), (10990, 7000, "blue", "red"), (11020, 8440, "wolves", "red"), (7060, 10870, "red", "red"), (7850, 9420, "raptors", "red") };
        foreach (var camp in camps)
        {
            string invader = camp.Side == "blue" ? "red" : "blue";
            double own = clear.Field(camp.Side).Number(camp.Camp), invade = clear.Field(invader + "Invade").Number(camp.Camp);
            if (own + invade <= 0) continue;
            var color = camp.Camp switch { "blue" => Microsoft.UI.Colors.DodgerBlue, "red" => Microsoft.UI.Colors.Coral, "wolves" => Microsoft.UI.Colors.Gray, _ => Microsoft.UI.Colors.MediumPurple };
            var marker = new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(color), Stroke = new SolidColorBrush(invade > 0 ? Microsoft.UI.Colors.Gold : Microsoft.UI.Colors.White), StrokeThickness = 2 };
            ToolTipService.SetToolTip(marker, CampName(camp.Camp) + "\n" + L("己方首清", "Own start") + $" {own:0} / {clear.Number(camp.Side + "Games"):0}" + "\n" + L("入侵首清", "Invade start") + $" {invade:0} / {clear.Number(invader + "Games"):0}");
            Canvas.SetLeft(marker, camp.X / 14820 * size - 6); Canvas.SetTop(marker, (1 - camp.Y / 14881) * size - 6); map.Children.Add(marker);
        }
        foreach (int level in new[] { 3, 4 })
            foreach (var point in stats.Field("earlyGank").Field("level" + level + "KillPositions").Items())
            {
                var marker = new Grid { Width = 9, Height = 9 }; var brush = new SolidColorBrush(level == 3 ? Microsoft.UI.Colors.DarkOrange : Microsoft.UI.Colors.MediumPurple);
                marker.Children.Add(new Line { X1 = 0, Y1 = 0, X2 = 9, Y2 = 9, Stroke = brush, StrokeThickness = 2 }); marker.Children.Add(new Line { X1 = 9, Y1 = 0, X2 = 0, Y2 = 9, Stroke = brush, StrokeThickness = 2 });
                ToolTipService.SetToolTip(marker, L("早期击杀参与", "Early takedown participation") + " · Lv " + level);
                Canvas.SetLeft(marker, point.Number("x") / 14820 * size - 4); Canvas.SetTop(marker, (1 - point.Number("y") / 14881) * size - 4); map.Children.Add(marker);
            }
        return map;
    }
    private static string Preference(JsonElement stats)
    {
        double top = stats.Number("avgTopZonePercentage"), mid = stats.Number("avgMidZonePercentage"), bot = stats.Number("avgBotZonePercentage");
        if (mid + bot >= .7 && top <= .28) return K("midBotPref") + $" {(mid + bot):P0}";
        if (top + mid >= .7 && bot <= .28) return K("topMidPref") + $" {(top + mid):P0}";
        var zones = new[] { ("topsidePref", top), ("midPref", mid), ("botsidePref", bot) }.OrderByDescending(z => z.Item2).ToArray();
        return zones[0].Item2 >= .4 && zones[0].Item2 - zones[1].Item2 >= .08 ? K(zones[0].Item1) + $" {zones[0].Item2:P0}" : K("balanced");
    }
    private static Canvas Map(JsonElement stats, int size)
    {
        var map = BaseMap(size);
        var points = stats.Field("minutePositions").Items().ToArray();
        foreach (var cell in points.GroupBy(p => ((int)Math.Clamp(p.Number("x") / 14820 * 7, 0, 6), (int)Math.Clamp(p.Number("y") / 14881 * 7, 0, 6))))
        {
            var square = new Rectangle { Width = size / 7d, Height = size / 7d, Fill = new SolidColorBrush(global::Windows.UI.Color.FromArgb((byte)Math.Min(180, cell.Count() * 15), 255, 150, 40)) };
            Canvas.SetLeft(square, cell.Key.Item1 * size / 7d); Canvas.SetTop(square, (6 - cell.Key.Item2) * size / 7d); map.Children.Add(square);
        }
        foreach (var kill in stats.Field("gankPositions").Items())
        {
            var marker = new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(Microsoft.UI.Colors.Red) };
            Canvas.SetLeft(marker, Math.Clamp(kill.Number("x") / 14820 * size, 0, size) - 2); Canvas.SetTop(marker, Math.Clamp((1 - kill.Number("y") / 14881) * size, 0, size) - 2); map.Children.Add(marker);
        }
        return map;
    }
    private static Canvas BaseMap(int size)
    {
        var map = new Canvas { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Microsoft.UI.Colors.DarkSlateGray) };
        var file = System.IO.Path.Combine(AppContext.BaseDirectory, "map-images", "11.png");
        if (File.Exists(file)) map.Children.Add(new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(file)), Width = size, Height = size, Stretch = Stretch.Fill });
        return map;
    }
}


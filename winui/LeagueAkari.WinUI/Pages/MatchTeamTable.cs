using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class MatchDetailsView
{
    private UIElement TeamTable(MatchParticipant[] team)
    {
        var body = new StackPanel { Spacing = 6 };
        var first = team[0];
        var placement = HistoryCardData.Placement(first);
        var result = placement > 0 ? L($"第 {placement} 名", $"Place {placement}")
            : first.IsSurrender && first.WinResult != "remake" ? L("投降", "Surrender") : Localization.Translate(MatchData.ResultLabel(first.WinResult));
        string Total(string key) => MatchDetailsData.Format(MatchDetailsData.Sum(team.Select(p => MatchDetailsData.Stat(p, key))));
        var heading = Label($"{result} · {Localization.Key("common.teams." + first.TeamKey, first.TeamKey)} · {Total("kills")}/{Total("deaths")}/{Total("assists")} · {L("经济", "Gold")} {Total("goldEarned")}", 12, true);
        heading.Foreground = new SolidColorBrush(first.WinResult is "remake" or "abort" ? Microsoft.UI.Colors.Gray : first.WinResult == "win" ? Microsoft.UI.Colors.DodgerBlue : Microsoft.UI.Colors.IndianRed);
        body.Children.Add(heading);
        // Arena subteams have no corresponding blue/red objective row.
        var info = _game.Field("teams").Items().FirstOrDefault(t => t.Number("teamId") == first.TeamId);
        if (!first.TeamKey.StartsWith("CHERRY-", StringComparison.Ordinal))
        {
            var objectives = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 8 };
            foreach (var (key, legacy, label) in new[] { ("tower", "towerKills", L("防御塔", "Turrets")), ("inhibitor", "inhibitorKills", L("水晶", "Inhibitors")), ("dragon", "dragonKills", L("小龙", "Dragons")), ("baron", "baronKills", L("大龙", "Barons")), ("horde", "", L("虚空巢虫", "Void grubs")), ("riftHerald", "riftHeraldKills", L("先锋", "Rift Heralds")), ("atakhan", "", L("厄塔汗", "Atakhan")) })
            {
                if (key == "atakhan" && !_game.Field("teams").Items().Any(t => MatchTeamData.Objective(t, key, legacy) > 0)) continue;
                if (MatchTeamData.Objective(info, key, legacy) is { } count) { var item = Label(label + " " + count.ToString("N0"), 10); item.Margin = new Thickness(0, 0, 10, 4); objectives.Children.Add(item); }
            }
            if (objectives.Children.Count > 0) body.Children.Add(objectives);
            var bans = info.Field("bans").Items().Where(b => b.Number("championId") > 0).ToArray();
            if (bans.Length > 0)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 }; row.Children.Add(Label(L("禁用", "Bans"), 10));
                foreach (var ban in bans.Take(5)) row.Children.Add(Icon($"/lol-game-data/assets/v1/champion-icons/{ban.Number("championId")}.png", 18));
                if (bans.Length > 5)
                {
                    var more = new Button { Content = "+" + (bans.Length - 5), FontSize = 10, Padding = new Thickness(3) };
                    var rest = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
                    foreach (var ban in bans.Skip(5)) rest.Children.Add(Icon($"/lol-game-data/assets/v1/champion-icons/{ban.Number("championId")}.png", 24));
                    more.Flyout = new Flyout { Content = rest }; row.Children.Add(more);
                }
                body.Children.Add(row);
            }
        }
        foreach (var player in team) body.Children.Add(TeamPlayerRow(player, team));
        return Card(body);
    }

    private UIElement TeamPlayerRow(MatchParticipant player, MatchParticipant[] team)
    {
        var row = new Grid { ColumnSpacing = 6, Padding = new Thickness(3, 5, 3, 5), MinWidth = 530 };
        foreach (var width in new[] { 1d, 94, 120, 70, 70, 178 }) row.ColumnDefinitions.Add(new ColumnDefinition { Width = width == 1 ? new GridLength(1, GridUnitType.Star) : new GridLength(width) });
        void Add(UIElement cell, int index) { Grid.SetColumn((FrameworkElement)cell, index); row.Children.Add(cell); }
        var identity = new Grid { ColumnSpacing = 3, MinWidth = 125 };
        foreach (var width in new[] { 34d, 17, 17, 1 }) identity.ColumnDefinitions.Add(new ColumnDefinition { Width = width == 1 ? new GridLength(1, GridUnitType.Star) : new GridLength(width) });
        var champion = new Grid(); champion.Children.Add(Icon($"/lol-game-data/assets/v1/champion-icons/{player.ChampionId}.png", 32));
        var level = Label(MatchDetailsData.Format(MatchDetailsData.Stat(player, "level")), 9, true); level.HorizontalAlignment = HorizontalAlignment.Right; level.VerticalAlignment = VerticalAlignment.Bottom;
        level.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        champion.Children.Add(new Border { Child = level, Background = new SolidColorBrush(Microsoft.UI.Colors.Black), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom });
        var championButton = new Button { Content = champion, Padding = new Thickness(0), BorderThickness = new Thickness(0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(championButton, PrivacyName(player) + " · " + L("表现雷达图", "Performance radar"));
        NativeHoverDetails.Attach(championButton, () => PlayerRadar(player)); identity.Children.Add(championButton);
        var spells = new StackPanel { Spacing = 2 }; foreach (var id in player.Spells) spells.Children.Add(ResourceSlot("summonerSpells", id, 16)); Grid.SetColumn(spells, 1); identity.Children.Add(spells);
        var perks = new StackPanel { Spacing = 2 };
        perks.Children.Add(ResourceSlot("perks", player.Runes.FirstOrDefault(), 16)); perks.Children.Add(ResourceSlot("perkstyles", player.PerkStyles.ElementAtOrDefault(1), 16)); Grid.SetColumn(perks, 2); identity.Children.Add(perks);
        var names = new StackPanel { Spacing = 1 };
        var profile = new Button { Content = new TextBlock { Text = PrivacyName(player), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = player.Puuid == _puuid ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal }, Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, IsEnabled = _openPlayer != null && MatchTeamData.CanOpenPlayer(player, _streamer) };
        ToolTipService.SetToolTip(profile, PrivacyName(player)); profile.Click += (_, _) => _openPlayer?.Invoke(player.Puuid); names.Children.Add(profile);
        NativePlayerNavigation.AttachBackgroundOpen(profile, _openPlayerInBackground == null ? null : () => _openPlayerInBackground(player.Puuid));
        if (player.Position.Length > 0 && !player.Position.Equals("invalid", StringComparison.OrdinalIgnoreCase)) names.Children.Add(Label(Localization.Key("matchCard.position." + player.Position, player.Position), 9));
        Grid.SetColumn(names, 3); identity.Children.Add(names); Add(identity, 0);
        Add(Label($"{MatchDetailsData.Format(MatchDetailsData.Stat(player, "kills"))}/{MatchDetailsData.Format(MatchDetailsData.Stat(player, "deaths"))}/{MatchDetailsData.Format(MatchDetailsData.Stat(player, "assists"))} ({MatchDetailsData.Format(MatchDetailsData.KillParticipation(player, team), "P0")})\n{MatchDetailsData.Format(MatchDetailsData.Stat(player, "kda"), "0.00")} KDA", 10), 1);
        var damage = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        damage.Children.Add(DamageCell(player, false)); damage.Children.Add(DamageCell(player, true)); Add(damage, 2);
        var duration = _game.Number("gameDuration"); var cs = MatchDetailsData.Stat(player, "cs"); var gold = MatchDetailsData.Stat(player, "goldEarned");
        var csCell = Label($"{MatchDetailsData.Format(cs)} {L("补刀", "CS")}\n{MatchDetailsData.Format(MatchTeamData.PerMinute(cs, duration), "0.0")}/{L("分", "min")}", 10); Add(csCell, 3);
        var goldCell = Label($"{MatchDetailsData.Format(gold / 1000, "0.00")}k\n{MatchDetailsData.Format(MatchTeamData.PerMinute(gold, duration), "0.0")}/{L("分", "min")}", 10); Add(goldCell, 4);
        var equipment = new StackPanel { Spacing = 3 };
        if (_game.Text("gameMode") is "KIWI" or "CHERRY")
        {
            var augments = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            foreach (var id in player.Augments.Take(MatchTeamData.AugmentSlots(team))) augments.Children.Add(ResourceSlot("augments", id, 20)); equipment.Children.Add(augments);
        }
        var items = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var id in player.Items) items.Children.Add(ResourceSlot("items", id, 20));
        if (team.Any(p => p.RoleBoundItem > 0)) items.Children.Add(ResourceSlot("items", player.RoleBoundItem, 20)); equipment.Children.Add(items); Add(equipment, 5);
        void Resize(double width)
        {
            var showCs = width >= (_game.Text("gameMode") is "KIWI" or "CHERRY" ? 740 : 700);
            var showGold = _game.Text("gameMode") is not ("KIWI" or "CHERRY") || width >= 700;
            csCell.Visibility = showCs ? Visibility.Visible : Visibility.Collapsed; goldCell.Visibility = showGold ? Visibility.Visible : Visibility.Collapsed;
            row.ColumnDefinitions[3].Width = new GridLength(showCs ? 70 : 0); row.ColumnDefinitions[4].Width = new GridLength(showGold ? 70 : 0);
        }
        row.SizeChanged += (_, e) => Resize(e.NewSize.Width); Resize(530);
        if (player.Puuid == _puuid) row.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(25, 128, 128, 128));
        return row;
    }

    private UIElement ResourceSlot(string kind, int id, int size) => id > 0 ? ResourceIcon(kind, id, size)
        : new Border { Width = size, Height = size, Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(30, 128, 128, 128)), CornerRadius = new CornerRadius(2) };

    private UIElement DamageCell(MatchParticipant player, bool taken)
    {
        var data = MatchTeamData.Damage(player, _players, taken);
        var label = taken ? L("承受伤害", "Damage taken") : L("对英雄伤害", "Champion damage");
        var body = new StackPanel { Spacing = 2 };
        var amount = Label(MatchDetailsData.Format(data.Total), 10); amount.IsTextSelectionEnabled = false;
        body.Children.Add(amount); body.Children.Add(DamageBar(data, 52));
        var cell = new Button { Content = body, Padding = new Thickness(1), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cell, PrivacyName(player) + " · " + label + " " + MatchDetailsData.Format(data.Total));
        FrameworkElement Details()
        {
            var panel = new StackPanel { Spacing = 5, MinWidth = 180 }; panel.Children.Add(Label(label, 12, true));
            panel.Children.Add(Label(L("全场最高值占比", "Share of match maximum") + " " + MatchDetailsData.Format(data.BaselineRatio, "P2"), 10)); panel.Children.Add(DamageBar(data, 160));
            panel.Children.Add(Label(L("总计", "Total") + " " + MatchDetailsData.Format(data.Total), 11));
            foreach (var (name, value) in new[] { (L("物理", "Physical"), data.Physical), (L("魔法", "Magic"), data.Magic), (L("真实", "True"), data.True) }) panel.Children.Add(Label($"{name} {MatchDetailsData.Format(value)} ({MatchDetailsData.Format(data.Share(value), "P0")})", 11));
            return panel;
        }
        NativeHoverDetails.Attach(cell, Details);
        return cell;
    }

    private static UIElement DamageBar(MatchDamage data, double width)
    {
        var track = new Grid { Width = width, Height = 6, Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(30, 128, 128, 128)) };
        var segments = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (value, color) in new[] { (data.Physical, Microsoft.UI.Colors.IndianRed), (data.Magic, Microsoft.UI.Colors.DodgerBlue), (data.True, Microsoft.UI.Colors.Gray) })
            if (value is > 0) segments.Children.Add(new Border { Width = width * Math.Clamp(value.Value / MatchData.NoZero(data.Baseline), 0, 1), Height = 6, Background = new SolidColorBrush(color) });
        track.Children.Add(segments); return track;
    }

    private FrameworkElement PlayerRadar(MatchParticipant player)
    {
        var metrics = MatchRadarData.Metrics(player, _players);
        var labels = metrics.Select(metric => Localization.Key(metric.LabelKey, metric.Key, new Dictionary<string, object?> { ["value"] = MatchDetailsData.Format(metric.Key == "killParticipation" ? metric.Value * 100 : metric.Value, metric.Key == "kda" ? "0.00" : "N0") })).ToArray();
        var dark = ActualTheme == ElementTheme.Dark;
        var playerColor = dark ? Microsoft.UI.Colors.LightCoral : Microsoft.UI.Colors.Crimson;
        var teamColor = dark ? Microsoft.UI.Colors.LightSlateGray : Microsoft.UI.Colors.SlateGray;
        var series = new[] { new ChartSeries(PrivacyName(player), metrics.Select(m => m.PlayerRatio ?? double.NaN).ToArray(), playerColor), new ChartSeries(Localization.Key("matchCard.radar.teamAvg"), metrics.Select(m => m.TeamRatio ?? double.NaN).ToArray(), teamColor) };
        var content = new StackPanel { Spacing = 3, Width = 320 };
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var entry in series) { var name = Label(entry.Name, 10); name.Foreground = new SolidColorBrush(entry.Color); legend.Children.Add(name); }
        content.Children.Add(legend); content.Children.Add(MatchCharts.Radar(labels, series, 320)); return content;
    }
}

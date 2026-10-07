using System.Text.Json.Nodes;
using LeagueAkari.WinUI.Pages;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LeagueAkari.WinUI.Windows;

public sealed partial class OpggWindow
{
    private readonly List<(string Key, CheckBox Control)> _expandedControls = [];
    private readonly List<(CheckBox Advanced, CheckBox Expanded, ComboBox Sort)> _kiwiControls = [];
    private bool _syncingAugments;
    private void SetStatus(QueryStatus status, string payload = "")
    {
        _queryStatus = status; _statusPayload = payload; RenderStatus();
    }
    private void RenderStatus() => _status.Text = _queryStatus switch
    {
        QueryStatus.Loading => Localization.Text("加载 OP.GG 数据…", "Loading OP.GG data…"),
        QueryStatus.Loaded => $"OP.GG · {Localization.Key("opgg.filters.version", "版本")} {_query.Version} · {_champions.Count} {Localization.Text("个英雄", "champions")} · {_cachedAt}",
        QueryStatus.Canceled => Localization.Text("已取消加载", "Loading canceled"),
        QueryStatus.InitializationFailed => Localization.Text("加载失败：", "Load failed: ") + _statusPayload,
        QueryStatus.QueryFailed => Localization.Text("OP.GG 查询失败，保留上次完整数据：", "OP.GG query failed; previous complete data retained: ") + _statusPayload,
        QueryStatus.BalanceFailed => Localization.Text("海斗增减益暂不可用：", "ARAM Mayhem balance unavailable: ") + _statusPayload,
        QueryStatus.Applied => Localization.Text("已应用到客户端", "Applied to client"),
        QueryStatus.ApplyFailed => Localization.Text("应用失败：", "Apply failed: ") + _statusPayload,
        QueryStatus.RawError => _statusPayload,
        _ => ""
    };
    private void LocalizeFormStatus()
    {
        // Only SettingsForms' known UI messages are translated; backend payload text stays intact.
        string text = _forms.Status.Text;
        if (text is "已保存" or "Saved") _forms.Status.Text = Localization.Text("已保存", "Saved");
        else if (text is "操作完成" or "Done") _forms.Status.Text = Localization.Text("操作完成", "Done");
        else foreach (string prefix in new[] { "保存失败：", "Save failed: " })
            if (text.StartsWith(prefix, StringComparison.Ordinal)) { _forms.Status.Text = Localization.Text("保存失败：", "Save failed: ") + text[prefix.Length..]; break; }
    }
    private void RenderDetails()
    {
        _expandedControls.Clear(); _kiwiControls.Clear(); RenderBuild(); RenderKiwiAugments(); if (_hero > 0 && Mode == "aram") { AppendAramBalance(); AppendKiwiBalance(); }
    }
    private async Task ReloadCatalog()
    {
        try
        {
            var catalog = await _backend.StateAsync("league-client-main", "gameData"); var extra = await _backend.StateAsync("extra-assets-main", "gtimg"); if (_shutdown) return;
            _catalog = JsonNode.Parse(catalog.GetRawText())?.AsObject() ?? new(); _searchChampions = AutoSelectData.Champions(catalog.Field("champions"), extra);
            RenderTable(); if (_build.Count > 0) RenderDetails(); _sessionChampionSignature = "";
        }
        catch (Exception ex) { if (!_shutdown) SetStatus(QueryStatus.RawError, ex.Message); }
    }
    private static StackPanel Section(string title, params UIElement[] controls)
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var control in controls) panel.Children.Add(control); return panel;
    }
    private void CreateOpggSettings()
    {
        var panel = new StackPanel { Spacing = 12 };
        var enabled = _forms.Toggle("window-manager-main/opgg-window", "enabled", ""); _settingLabels.Add((enabled, "enabled")); panel.Children.Add(enabled);
        var flash = _forms.Choice("opgg-renderer", "savedPreferences.flashPosition", "", ("auto", ""), ("d", "D"), ("f", "F")); _settingLabels.Add((flash, "flashPosition")); panel.Children.Add(flash);
        foreach (string key in new[] { "autoApplyRunes", "autoApplySpells", "autoApplyItems" }) { var toggle = _forms.Toggle("opgg-renderer", key, ""); _settingLabels.Add((toggle, key)); panel.Children.Add(toggle); }
        _settingsContent.Content = panel; LocalizeSettings();
    }
    private void LocalizeSettings()
    {
        _settingsButton.Content = Localization.Key("opgg.filters.settings.button", "设置");
        foreach (var (control, key) in _settingLabels)
        {
            string title = Localization.Key("opgg.view.settings." + key + ".label", key);
            if (control is ToggleSwitch toggle) { toggle.Header = title; toggle.OnContent = Localization.Text("开", "On"); toggle.OffContent = Localization.Text("关", "Off"); }
            if (control is ComboBox combo) { combo.Header = title; foreach (ComboBoxItem item in combo.Items) if (item.Tag is "auto") item.Content = Localization.Key("opgg.view.settings.flashPosition.auto", "保留当前位置"); }
            ToolTipService.SetToolTip(control, new TextBlock { Text = Localization.Key("opgg.view.settings." + key + ".description", ""), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        }
    }
    private async Task ShowOpggSettings()
    {
        if (_settingsContent.Content is null || _body.XamlRoot is null) return;
        var dialog = new ContentDialog { XamlRoot = _body.XamlRoot, Title = Localization.Key("opgg.view.settings.title", "OP.GG 设置"), Content = _settingsContent, CloseButtonText = Localization.Text("关闭", "Close") };
        await NativeDialogs.TryShowAsync(dialog, () => _visible && !_shutdown); dialog.Content = null;
    }
    private void SyncExpandedWidgets(string key)
    {
        foreach (var (widget, control) in _expandedControls.Where(item => item.Key == key)) control.IsChecked = _expandedWidgets.GetValueOrDefault(widget);
    }
    private void SyncKiwiWidgets()
    {
        if (_syncingAugments) return; _syncingAugments = true;
        try { foreach (var (advanced, expanded, sort) in _kiwiControls) { advanced.IsChecked = _kiwiAdvanced; expanded.IsChecked = _kiwiExpanded; Choose(sort, _kiwiSort); } }
        finally { _syncingAugments = false; }
    }
    private void LocalizeFilters()
    {
        _tierTab.Header = Localization.Key("opgg.filters.champions", "英雄榜单"); _heroTab.Header = Localization.Key("opgg.filters.champion", "英雄详情");
        _refresh.Content = Localization.Key("opgg.filters.refresh", "刷新"); _cancel.Content = Localization.Key("opgg.championTable.cancel", "取消");
        _search.PlaceholderText = Localization.Key("opgg.championTable.searchPlaceholder", "搜索英雄");
        foreach (var (combo, prefix, header) in new[] { (_mode, "modes", "mode"), (_region, "regions", "region"), (_position, "positions", "position"), (_tier, "tiers", "rankTier") })
        {
            combo.Header = Localization.Key("opgg.filters." + header, header);
            foreach (ComboBoxItem item in combo.Items) { string id = (string)item.Tag; item.Content = Localization.Key("opgg.filters." + prefix + "." + id, id); if (combo == _position) item.IsEnabled = Mode != "ranked" || id != "none"; }
        }
        _direction.Header = Localization.Text("顺序", "Order"); foreach (ComboBoxItem item in _direction.Items) item.Content = item.Tag is "asc" ? Localization.Text("升序", "Ascending") : Localization.Text("降序", "Descending");
        _version.Header = Localization.Key("opgg.filters.version", "版本"); _sort.Header = Localization.Text("排序", "Sort");
        foreach (ComboBoxItem item in _sort.Items) item.Content = (string)item.Tag switch { "rank" => Localization.Text("排名", "Rank"), "name" => Localization.Key("opgg.championTable.columns.champion", "名称"), "tier" => Localization.Key("opgg.championTable.columns.tier", "强度"), "win" => Localization.Key("opgg.championTable.columns.winRate", "胜率"), "pick" => Localization.Key("opgg.championTable.columns.pickRate", "选取率"), _ => Localization.Key("opgg.championTable.columns.banRate", "禁用率") };
    }
    // Each original widget keeps its own expanded state; expanding one list does not alter the others.
    private StackPanel Expandable(string title, IReadOnlyList<UIElement> rows, int limit, string key)
    {
        var section = Section(title); var list = new StackPanel { Spacing = 8 }; var toggle = new CheckBox { Content = Localization.Key("opgg.champion.showAll", "显示全部"), IsChecked = _expandedWidgets.GetValueOrDefault(key) };
        _expandedControls.Add((key, toggle));
        void Render() { list.Children.Clear(); foreach (var row in toggle.IsChecked == true ? rows : rows.Take(limit)) list.Children.Add(row); }
        if (rows.Count > limit) { section.Children.Add(toggle); toggle.Checked += (_, _) => { _expandedWidgets[key] = true; Render(); SyncExpandedWidgets(key); }; toggle.Unchecked += (_, _) => { _expandedWidgets[key] = false; Render(); SyncExpandedWidgets(key); }; }
        section.Children.Add(list); Render(); return section;
    }
    private void RenderKiwiAugments()
    {
        if (Mode != "aram" || _kiwiAugments.Count == 0) return;
        var section = Section(Localization.Text("海斗海克斯强化 · OP.GG", "ARAM Mayhem augments · OP.GG"));
        var tabs = new TabView { IsAddTabButtonVisible = false };
        foreach (var (rarity, key) in new[] { ("", "augmentAll"), ("kSilver", "augmentSilver"), ("kGold", "augmentGold"), ("kPrismatic", "augmentPrism") })
        {
            var items = _kiwiAugments.Where(item => rarity == "" || _catalog["augments"]?[ID(item, "id").ToString()]?["rarity"]?.ToString() == rarity).ToArray();
            if (items.Length == 0) continue;
            var panel = new StackPanel { Spacing = 8 }; var options = new WrapPanel();
            var advanced = new CheckBox { Content = Localization.Key("opgg.champion.showAdvancedStats", "高级统计"), IsChecked = _kiwiAdvanced }; var expanded = new CheckBox { Content = Localization.Key("opgg.champion.showAll", "显示全部"), IsChecked = _kiwiExpanded };
            var sort = SettingsForms.Select("", new[] { "default", "performance", "popular" }.Select(value => (value, Localization.Key("opgg.champion.augmentSort." + value, value))), _kiwiSort);
            _kiwiControls.Add((advanced, expanded, sort));
            options.Children.Add(advanced); options.Children.Add(expanded); options.Children.Add(sort); panel.Children.Add(options);
            var list = new StackPanel { Spacing = 6 }; panel.Children.Add(list);
            void Render()
            {
                list.Children.Clear(); int index = 0;
                foreach (var item in OpggData.SortAugments(items, SettingsForms.Selected(sort)).Take(expanded.IsChecked == true ? int.MaxValue : 16))
                {
                    int id = ID(item, "id"), tier = ID(item, "tier"); var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    row.Children.Add(new TextBlock { Text = "#" + (++index), Width = 32 });
                    row.Children.Add(new Border { Background = new SolidColorBrush(tier switch { 0 => Microsoft.UI.Colors.BlueViolet, 1 => Microsoft.UI.Colors.DodgerBlue, 2 => Microsoft.UI.Colors.SeaGreen, 3 => Microsoft.UI.Colors.DarkGoldenrod, _ => Microsoft.UI.Colors.Gray }), Padding = new Thickness(4, 0, 4, 0), Child = new TextBlock { Text = tier is >= 0 and <= 6 ? new[] { "S", "A", "B", "C", "D", "E", "F" }[tier] : "-", Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) } });
                    row.Children.Add(Picture("augments", id, 24)); row.Children.Add(new TextBlock { Text = Name("augments", id), TextWrapping = TextWrapping.Wrap, Width = 220 });
                    if (advanced.IsChecked == true) row.Children.Add(new TextBlock { Text = $"{Localization.Key("opgg.champion.augmentPerformance", "表现")} {Num(item, "performance")} · {Localization.Key("opgg.champion.augmentPopular", "热度")} {Num(item, "popular")}" });
                    list.Children.Add(row);
                }
            }
            advanced.Checked += (_, _) => { _kiwiAdvanced = true; Render(); SyncKiwiWidgets(); }; advanced.Unchecked += (_, _) => { _kiwiAdvanced = false; Render(); SyncKiwiWidgets(); }; expanded.Checked += (_, _) => { _kiwiExpanded = true; Render(); SyncKiwiWidgets(); }; expanded.Unchecked += (_, _) => { _kiwiExpanded = false; Render(); SyncKiwiWidgets(); }; sort.SelectionChanged += (_, _) => { _kiwiSort = SettingsForms.Selected(sort); Render(); SyncKiwiWidgets(); }; Render();
            tabs.TabItems.Add(new TabViewItem { Header = Localization.Key("opgg.champion." + key, key), Tag = rarity, IsClosable = false, Content = panel });
        }
        tabs.SelectedIndex = 0; foreach (TabViewItem tab in tabs.TabItems) if ((string)tab.Tag == _kiwiTab) tabs.SelectedItem = tab; tabs.SelectionChanged += (_, _) => { _kiwiTab = (tabs.SelectedItem as TabViewItem)?.Tag?.ToString() ?? ""; SyncKiwiWidgets(); }; section.Children.Add(tabs); _details.Children.Add(section);
    }
}

using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
namespace LeagueAkari.WinUI.Pages;

public sealed class HistoryTabsPage : UserControl
{
    private readonly BackendClient _backend;
    private readonly TabView _tabs = new() { IsAddTabButtonVisible = false, CanDragTabs = true, CanReorderTabs = true };
    private readonly Dictionary<string, TabViewItem> _players = new();
    private readonly TextBlock _empty = new() { Text = "等待 LOL 客户端连接", Margin = new Thickness(24) };
    private readonly Button _selfButton = new() { Content = "我的战绩" };
    private readonly AutoSuggestBox _search = new() { PlaceholderText = "玩家昵称#编号 / PUUID", Width = 260 };
    private readonly InfoBar _error = new() { IsClosable = true };
    private readonly ComboBox _region = new() { Width = 150 };
    private readonly Button _quick = new();
    private readonly Button _query = new();
    private readonly Button _cancelSearch = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _sourceLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<string, string> _names = [];
    private readonly Dictionary<string, JsonElement> _profiles = [];
    private readonly GameAssets _assets;
    private JsonElement _sgp;
    private string _preferred = "sgp";
    private CancellationTokenSource? _searchStop;
    private bool _loaded, _applyingRegions;
    private string _selfPuuid = "", _server = "";
    private JsonElement _selfProfile;
    private bool _synchronizing, _syncPending;
    public HistoryTabsPage(BackendClient backend)
    {
        _backend = backend; _assets = new(backend);
        var tools = new WrapPanel { Spacing = 8, Margin = new Thickness(12, 8, 12, 0) };
        _query.Click += async (_, _) => await SearchAsync();
        _search.QuerySubmitted += async (_, _) => await SearchAsync();
        _cancelSearch.Click += (_, _) => _searchStop?.Cancel();
        _quick.Click += async (_, _) => await ShowQuickSearchAsync();
        _selfButton.Click += (_, _) => OpenSelf();
        tools.Children.Add(_region); tools.Children.Add(_search); tools.Children.Add(_query); tools.Children.Add(_cancelSearch); tools.Children.Add(_quick); tools.Children.Add(_selfButton); tools.Children.Add(_sourceLabel);
        var body = new Grid(); body.Children.Add(_empty); body.Children.Add(_tabs);
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        root.Children.Add(tools); Grid.SetRow(_error, 1); root.Children.Add(_error); Grid.SetRow(body, 2); root.Children.Add(body); Content = root;
        _tabs.TabCloseRequested += (_, e) => CloseTab(e.Tab);
        Loaded += async (_, _) => { _loaded = true; _backend.EventReceived -= BackendEvent; _backend.EventReceived += BackendEvent; Localization.Changed += RefreshText; if (NativeAppearance.Current is { } appearance) appearance.Changed += RefreshText; RefreshText(); await SyncAccountAsync(); };
        Unloaded += (_, _) => { _loaded = false; _searchStop?.Cancel(); _backend.EventReceived -= BackendEvent; Localization.Changed -= RefreshText; if (NativeAppearance.Current is { } appearance) appearance.Changed -= RefreshText; };
    }
    private void BackendEvent(JsonElement update)
    {
        string name = update.Text("name");
        if (name is "update-state-prop/league-client-main:summoner" or "update-state-prop/league-client-main:state" or "update-state-prop/sgp-main:state" or "update-state-prop/app-common-main:settings")
            DispatcherQueue.TryEnqueue(async () => await SyncAccountAsync());
    }
    private async Task SyncAccountAsync()
    {
        if (_synchronizing) { _syncPending = true; return; }
        _synchronizing = true;
        try
        {
            var client = await _backend.StateAsync("league-client-main");
            _sgp = await _backend.StateAsync("sgp-main");
            var sourceSetting = await _backend.CallAsync("setting-factory-main", "get", "app-common-main", "preferredLolSource");
            _preferred = sourceSetting.ValueKind == JsonValueKind.String ? sourceSetting.GetString() ?? "sgp" : "sgp";
            RefreshRegions();
            if (client.Text("connectionState") != "connected")
            {
                _searchStop?.Cancel(); _selfPuuid = _server = ""; _selfProfile = default; CloseAll();
                _empty.Text = Localization.Text("等待 LOL 客户端连接", "Waiting for League client");
                return;
            }
            var me = (await _backend.StateAsync("league-client-main", "summoner")).Field("me");
            string puuid = me.Text("puuid");
            string server = _sgp.Field("availability").Text("sgpServerId");
            if (puuid.Length == 0 || server.Length == 0) return;
            _selfProfile = me;
            _profiles[server + ":" + puuid] = me;
            RefreshHeaders();
            if (_selfPuuid == puuid && _server == server) return;
            _searchStop?.Cancel(); _selfPuuid = puuid; _server = server; _profiles[server + ":" + puuid] = me; RefreshRegions();
            OpenSelf();
        }
        catch (Exception ex) { _empty.Text = ex.Message; }
        finally
        {
            _synchronizing = false;
            if (_syncPending) { _syncPending = false; await SyncAccountAsync(); }
        }
    }
    private void OpenSelf() { if (_selfPuuid.Length > 0) { _profiles[_server + ":" + _selfPuuid] = _selfProfile; OpenPlayer(_selfPuuid, _server); } }
    private async Task SearchAsync()
    {
        if (_search.Text.Trim().Length == 0 || _searchStop != null) return;
        var stop = _searchStop = new CancellationTokenSource();
        _search.IsEnabled = _query.IsEnabled = _region.IsEnabled = false; _cancelSearch.Visibility = Visibility.Visible;
        try
        {
            var outcome = await new GlobalPlayerSearch(_backend).FindAsync(_search.Text, (_region.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? _server, _preferred, _sgp, stop.Token, (done, total) => DispatcherQueue.TryEnqueue(() => _sourceLabel.Text = $"{done} / {total}"));
            stop.Token.ThrowIfCancellationRequested(); if (!_loaded) return;
            if (outcome.Players.Length == 0) throw new InvalidOperationException(outcome.Errors.Length > 0 ? string.Join("\n", outcome.Errors) : Localization.Text("没有找到玩家", "No players found"));
            _error.IsOpen = false; await ShowSearchResultsAsync(outcome);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _error.Title = ex.Message; _error.Severity = InfoBarSeverity.Error; _error.IsOpen = true; }
        finally { _searchStop = null; stop.Dispose(); _search.IsEnabled = _query.IsEnabled = _region.IsEnabled = true; _cancelSearch.Visibility = Visibility.Collapsed; RefreshText(); }
    }
    private void UpdateEmpty()
    {
        _selfButton.IsEnabled = _selfPuuid.Length > 0;
        _tabs.Visibility = _tabs.TabItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _empty.Visibility = _tabs.TabItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_selfPuuid.Length > 0) _empty.Text = Localization.Text("所有玩家页面已关闭", "All player tabs are closed");
    }
    private void CloseTab(TabViewItem tab)
    {
        int index = _tabs.TabItems.IndexOf(tab);
        if (index < 0) return;
        string key = tab.Tag?.ToString() ?? ""; _players.Remove(key); _names.Remove(key); _tabs.TabItems.RemoveAt(index);
        _tabs.SelectedIndex = PlayerTabTransitions.SelectedIndexAfterClose(index, _tabs.TabItems.Count);
        UpdateEmpty(); RefreshHeaders();
    }
    private void CloseAll() { _players.Clear(); _names.Clear(); _profiles.Clear(); _tabs.TabItems.Clear(); UpdateEmpty(); }
    private MenuFlyout TabMenu(TabViewItem tab)
    {
        var menu = new MenuFlyout();
        var refresh = new MenuFlyoutItem { Text = Localization.Text("刷新", "Refresh") };
        refresh.Click += async (_, _) => { refresh.IsEnabled = false; try { if (tab.Content is HistoryPage page) await page.LoadAsync(); } catch (Exception ex) { _error.Title = ex.Message; _error.IsOpen = true; } finally { refresh.IsEnabled = true; } }; menu.Items.Add(refresh);
        void Add(string chinese, string english, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = Localization.Text(chinese, english), IsEnabled = enabled };
            item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        Add("关闭此页面", "Close tab", () => CloseTab(tab));
        Add("关闭其他页面", "Close other tabs", () => { foreach (var other in _tabs.TabItems.OfType<TabViewItem>().Where(t => !ReferenceEquals(t, tab)).ToArray()) CloseTab(other); _tabs.SelectedItem = tab; }, PlayerTabTransitions.CanCloseOthers(_tabs.TabItems.IndexOf(tab), _tabs.TabItems.Count));
        Add("关闭右侧页面", "Close tabs to the right", () => { var selected = _tabs.SelectedItem; foreach (var other in _tabs.TabItems.OfType<TabViewItem>().Skip(_tabs.TabItems.IndexOf(tab) + 1).ToArray()) CloseTab(other); _tabs.SelectedItem = selected is TabViewItem existing && _tabs.TabItems.Contains(existing) ? existing : tab; }, PlayerTabTransitions.CanCloseRight(_tabs.TabItems.IndexOf(tab), _tabs.TabItems.Count));
        Add("关闭所有页面", "Close all tabs", CloseAll);
        Add("我的战绩", "My history", OpenSelf);
        return menu;
    }
    public async Task RefreshAsync()
    {
        await SyncAccountAsync();
        foreach (var tab in _players.Values.ToArray()) if (tab.Content is HistoryPage page) await page.LoadAsync();
    }
    public async Task RefreshAfterGameEndAsync(JsonElement teams)
    {
        if (teams.ValueKind != JsonValueKind.Object) return;
        var settings = await _backend.CallAsync("setting-factory-main", "get", "player-tabs-renderer", "refreshTabsAfterGameEnds");
        if (settings.ValueKind == JsonValueKind.False) return;
        var participants = teams.EnumerateObject().SelectMany(t => t.Value.Items()).Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString() ?? "").ToHashSet(StringComparer.Ordinal);
        foreach (var entry in _players.ToArray())
        {
            if (entry.Key.StartsWith(_server + ":", StringComparison.Ordinal) && participants.Contains(entry.Key[(_server.Length + 1)..]) && entry.Value.Content is HistoryPage page && page.CanRefreshAfterGameEnd)
                await page.LoadAsync();
        }
    }
    public async Task CollectAsync(string puuid, string server, int? champion, string? position)
    {
        OpenPlayer(puuid, server);
        if ((_tabs.SelectedItem as TabViewItem)?.Content is HistoryPage page) await page.ConfigureCollectionAsync(champion, position);
    }
    public void OpenPlayer(string puuid, string server) => OpenPlayer(puuid, server, true);
    public void OpenPlayer(string puuid, string server, bool select)
    {
        if (puuid.Length == 0) { OpenSelf(); return; }
        if (puuid == "00000000-0000-0000-0000-000000000000") return;
        if (server.Length == 0) server = _server;
        string key = server + ":" + puuid;
        if (!_players.TryGetValue(key, out var tab))
        {
            var page = new HistoryPage(_backend, puuid, server, OpenPlayer, OpenSelf, (id, region) => OpenPlayer(id, region, false));
            tab = new TabViewItem { Header = Localization.Text("玩家战绩", "Player history"), Tag = key, Content = page, IsClosable = true };
            var playerTab = tab;
            page.PlayerLoaded += name => { if (_players.TryGetValue(key, out var current) && ReferenceEquals(current, playerTab)) { _names[key] = name; RefreshHeaders(); } };
            tab.ContextFlyout = TabMenu(tab);
            _players[key] = tab; _tabs.TabItems.Add(tab);
        }
        if (select || _tabs.SelectedItem is null) _tabs.SelectedItem = tab;
        UpdateEmpty(); RefreshHeaders();
    }

    private string ServerName(string server) => _sgp.Field("leagueServers").Field("serverNames").Field(Localization.Locale).Text(server, Localization.Key("common.sgpServers." + server, server));
    private void RefreshRegions()
    {
        if (_applyingRegions) return; _applyingRegions = true;
        try
        {
            string previous = (_region.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            string current = _sgp.Field("availability").Text("sgpServerId");
            string[] servers = _preferred == "sgp" ? GlobalPlayerSearch.Servers(_sgp) : [current];
            _region.Items.Clear();
            foreach (var server in servers.Where(s => s.Length > 0)) _region.Items.Add(new ComboBoxItem { Tag = server, Content = ServerName(server) + (server == current ? Localization.Text(" (当前)", " (Current)") : "") });
            _region.SelectedItem = _region.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == previous) ?? _region.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == current);
            if (_region.SelectedItem is null && _region.Items.Count > 0) _region.SelectedIndex = 0;
            _region.Visibility = _region.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _applyingRegions = false; }
        RefreshText();
    }
    private void RefreshText()
    {
        _query.Content = Localization.Text("查询", "Search"); _quick.Content = Localization.Text("搜索历史 / 好友", "Search history / friends");
        _cancelSearch.Content = Localization.Text("取消", "Cancel"); _selfButton.Content = Localization.Text("我的战绩", "My history");
        _search.PlaceholderText = Localization.Text("玩家昵称#编号 / PUUID", "Player name#tag / PUUID");
        foreach (var item in _region.Items.OfType<ComboBoxItem>()) { string server = item.Tag?.ToString() ?? ""; item.Content = ServerName(server) + (server == _server ? Localization.Text(" (当前)", " (Current)") : ""); }
        _sourceLabel.Text = _preferred.ToUpperInvariant(); RefreshHeaders(); UpdateEmpty();
    }
    private void RefreshHeaders()
    {
        bool showRegion = GlobalPlayerSearch.ShowServer(_players.Keys.Select(k => k[..k.IndexOf(':')]), _server);
        foreach (var entry in _players)
        {
            string server = entry.Key[..entry.Key.IndexOf(':')]; string name = _names.GetValueOrDefault(entry.Key, Localization.Text("玩家战绩", "Player history"));
            if (NativeAppearance.Current?.StreamerMode == true) name = NativeAppearance.Current.SummonerPlaceholder(entry.Key[(entry.Key.IndexOf(':') + 1)..], _tabs.TabItems.IndexOf(entry.Value));
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            if (_profiles.TryGetValue(entry.Key, out var profile)) header.Children.Add(_assets.Icon("profile-icons", (int)profile.Number("profileIconId"), 16));
            header.Children.Add(new TextBlock { Text = (showRegion ? ServerName(server) + " · " : "") + name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 250 });
            var tab = entry.Value; header.DoubleTapped += (_, args) => { CloseTab(tab); args.Handled = true; };
            bool middle = false; header.PointerPressed += (_, args) => { middle = args.GetCurrentPoint(header).Properties.IsMiddleButtonPressed; if (middle) args.Handled = true; };
            header.PointerReleased += (_, args) => { if (middle) { middle = false; CloseTab(tab); args.Handled = true; } };
            entry.Value.Header = header;
            entry.Value.ContextFlyout = TabMenu(entry.Value);
        }
    }
    private Button PlayerButton(JsonElement player, string puuid, string server, int index, Func<bool, Task> open)
    {
        bool hidden = NativeAppearance.Current?.StreamerMode == true;
        string name = hidden ? NativeAppearance.Current!.SummonerPlaceholder(puuid, index) : player.Text("gameName", player.Text("name", "—")) + " #" + player.Text("tagLine", player.Text("gameTag"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        row.Children.Add(_assets.Icon("profile-icons", (int)player.Number("profileIconId", player.Number("icon", 29)), 24));
        var text = new StackPanel(); text.Children.Add(new TextBlock { Text = name + (server != _server ? " · " + ServerName(server) : ""), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 440 });
        string details = player.Field("summonerLevel").ValueKind == JsonValueKind.Number ? "Lv. " + player.Number("summonerLevel") : "";
        if (player.Text("privacy") == "PRIVATE") details += " · " + Localization.Text("隐藏战绩", "Private history");
        if (details.Length > 0) text.Children.Add(new TextBlock { Text = details, FontSize = 11 }); row.Children.Add(text);
        var button = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        button.Click += async (_, _) => { button.IsEnabled = false; try { await open(true); } catch (Exception ex) { _error.Title = ex.Message; _error.IsOpen = true; } finally { button.IsEnabled = true; } };
        bool middle = false; button.PointerPressed += (_, args) => { middle = args.GetCurrentPoint(button).Properties.IsMiddleButtonPressed; if (middle) args.Handled = true; };
        button.PointerReleased += async (_, args) => { if (!middle) return; middle = false; args.Handled = true; try { await open(false); } catch (Exception ex) { _error.Title = ex.Message; _error.IsOpen = true; } };
        return button;
    }
    private async Task ShowSearchResultsAsync(PlayerSearchOutcome outcome)
    {
        var rows = new StackPanel { Spacing = 7 };
        var dialog = new ContentDialog { Title = Localization.Text($"查询结果 ({outcome.Players.Length})", $"Search results ({outcome.Players.Length})"), Content = new ScrollViewer { Content = rows, MaxHeight = 500 }, CloseButtonText = Localization.Text("关闭", "Close"), XamlRoot = XamlRoot };
        void Render()
        {
        rows.Children.Clear();
        for (int i = 0; i < outcome.Players.Length; i++)
        {
            var result = outcome.Players[i];
            rows.Children.Add(PlayerButton(result.Player, result.Puuid, result.Server, i, async select => { await new PlayerSearchHistory(_backend).SaveAsync(result.Puuid, result.Server, result.Player); _profiles[result.Server + ":" + result.Puuid] = result.Player; OpenPlayer(result.Puuid, result.Server, select); if (select) dialog.Hide(); }));
        }
        if (outcome.Errors.Length > 0) rows.Children.Add(new TextBlock { Text = string.Join("\n", outcome.Errors), TextWrapping = TextWrapping.Wrap, MaxWidth = 500 });
        }
        void Changed() => DispatcherQueue.TryEnqueue(() => Render());
        Localization.Changed += Changed; if (NativeAppearance.Current is { } appearance) appearance.Changed += Changed;
        try { Render(); await NativeDialogs.TryShowAsync(dialog, () => _loaded); }
        finally { Localization.Changed -= Changed; if (NativeAppearance.Current is { } appearanceAfter) appearanceAfter.Changed -= Changed; }
    }
    private async Task ShowQuickSearchAsync()
    {
        _quick.IsEnabled = false;
        try
        {
            var store = new PlayerSearchHistory(_backend); var history = await store.ReadAsync(); var source = new PlayerDataSource(_backend);
            JsonElement[] friends = []; string friendsError = "";
            try { friends = (await source.LcuAsync("GET", "/lol-chat/v1/friends")).Items().OrderByDescending(GlobalPlayerSearch.FriendPriority).ToArray(); }
            catch (Exception ex) { friendsError = ex.Message; }
            var rows = new StackPanel { Spacing = 6 }; var filter = new TextBox { PlaceholderText = Localization.Text("筛选玩家", "Filter players") }; var body = new StackPanel { Spacing = 8 }; body.Children.Add(filter); body.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 500 });
            var dialog = new ContentDialog { Title = Localization.Text("搜索历史与好友", "Search history and friends"), Content = body, CloseButtonText = Localization.Text("关闭", "Close"), XamlRoot = XamlRoot };
            bool Matches(JsonElement player) => (player.Text("gameName", player.Text("name")) + "#" + player.Text("tagLine", player.Text("gameTag"))).Contains(filter.Text, StringComparison.OrdinalIgnoreCase);
            void Render()
            {
                rows.Children.Clear(); var visible = history.Where(h => GlobalPlayerSearch.HistoryVisible(h.Server, _sgp)).Where(h => Matches(h.Summoner)).ToArray();
                foreach (bool pinned in new[] { true, false })
                {
                    var values = visible.Where(h => h.IsPinned == pinned).ToArray(); if (values.Length == 0) continue;
                    rows.Children.Add(new TextBlock { Text = pinned ? Localization.Text("置顶", "Pinned") : Localization.Text("最近搜索", "Recent searches"), FontWeight = Microsoft.UI.Text.FontWeights.Bold });
                    foreach (var item in values)
                    {
                        var row = new WrapPanel { Spacing = 5 }; row.Children.Add(PlayerButton(item.Summoner, item.Puuid, item.Server, Array.IndexOf(visible, item), select => { _profiles[item.Server + ":" + item.Puuid] = item.Summoner; OpenPlayer(item.Puuid, item.Server, select); if (select) dialog.Hide(); return Task.CompletedTask; }));
                        var pin = new Button { Content = item.IsPinned ? Localization.Text("取消置顶", "Unpin") : Localization.Text("置顶", "Pin") }; pin.Click += async (_, _) => { pin.IsEnabled = false; try { await store.PinAsync(item.Puuid); history = await store.ReadAsync(); Render(); } catch (Exception ex) { _error.Title = ex.Message; _error.IsOpen = true; pin.IsEnabled = true; } }; row.Children.Add(pin);
                        var delete = new Button { Content = Localization.Text("删除", "Delete") }; delete.Click += async (_, _) => { delete.IsEnabled = false; try { await store.DeleteAsync(item.Puuid); history = await store.ReadAsync(); Render(); } catch (Exception ex) { _error.Title = ex.Message; _error.IsOpen = true; delete.IsEnabled = true; } }; row.Children.Add(delete); rows.Children.Add(row);
                    }
                }
                rows.Children.Add(new TextBlock { Text = Localization.Text("好友", "Friends"), FontWeight = Microsoft.UI.Text.FontWeights.Bold });
                if (friendsError.Length > 0) rows.Children.Add(new TextBlock { Text = friendsError, TextWrapping = TextWrapping.Wrap });
                foreach (var friend in friends.Where(Matches))
                {
                    string puuid = friend.Text("puuid"); if (puuid.Length == 0) puuid = friend.Field("lol").Text("puuid"); if (puuid.Length == 0) continue;
                    var row = new WrapPanel { Spacing = 5 }; row.Children.Add(PlayerButton(friend, puuid, _server, Array.IndexOf(friends, friend), select => { OpenPlayer(puuid, _server, select); if (select) dialog.Hide(); return Task.CompletedTask; }));
                    if (GlobalPlayerSearch.FriendSpectatable(friend))
                    {
                        var spectate = new Button { Content = Localization.Key("playerSearch.spectate") }; spectate.Click += async (_, _) => { spectate.IsEnabled = false; try { await source.LcuAsync("POST", "/lol-spectator/v1/spectate/launch", new { puuid, spectatorKey = friend.Field("lol").Text("spectatorKey") }); _error.Title = Localization.Key("playerSearch.spectateStarted"); _error.Severity = InfoBarSeverity.Success; _error.IsOpen = true; } catch (Exception ex) { _error.Title = ex.Message; _error.Severity = InfoBarSeverity.Error; _error.IsOpen = true; } finally { spectate.IsEnabled = true; } }; row.Children.Add(spectate);
                    }
                    rows.Children.Add(row);
                }
            }
            void Changed() => DispatcherQueue.TryEnqueue(() => Render());
            filter.TextChanged += (_, _) => Render(); Localization.Changed += Changed; if (NativeAppearance.Current is { } appearance) appearance.Changed += Changed;
            try { Render(); await NativeDialogs.TryShowAsync(dialog, () => _loaded); }
            finally { Localization.Changed -= Changed; if (NativeAppearance.Current is { } appearanceAfter) appearanceAfter.Changed -= Changed; }
        }
        catch (Exception ex) { _error.Title = ex.Message; _error.Severity = InfoBarSeverity.Error; _error.IsOpen = true; }
        finally { _quick.IsEnabled = true; }
    }
}

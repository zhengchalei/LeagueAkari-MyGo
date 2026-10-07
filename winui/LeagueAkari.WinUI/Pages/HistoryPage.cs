using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;
namespace LeagueAkari.WinUI.Pages;

public sealed partial class HistoryPage : UserControl
{
    private readonly ProfileBackground _background;
    private readonly BackendClient _backend; private PlayerDataSource _source; private readonly GameAssets _assets; private readonly Action<string, string>? _openPlayer, _openPlayerInBackground; private readonly Action? _openSelf;
    private readonly HistoryLoadController<HistoryPlayerContext> _history;
    private readonly List<Action> _localized = [];
    private readonly List<(Button Button, Func<bool> Enabled)> _actions = [];
    private readonly Button _retry = new(), _cancelLoad = new(), _normalPage = new();
    private bool _loaded;
    private string _requestedPuuid;
    private HistoryLoadedPage<HistoryPlayerContext>? _shownPage;
    private readonly PlayerProfileView _profile;
    private readonly RecentlyPlayersView _recent;
    private readonly StackPanel _summary = new() { Spacing = 4 }; private readonly StackPanel _assetsSummary = new() { Spacing = 4 }; private readonly StackPanel _mastery = new() { Spacing = 4 };
    private readonly ListView _games = new() { SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private HistoryFilterSettings _filterSettings = new();
    private object? _collectStop => _history.Collecting ? _history : null;
    private readonly NumberBox _collectBatch = new() { Value = 20, Minimum = 1, Maximum = 100, Width = 100 };
    private readonly NumberBox _collectTarget = new() { Value = 20, Minimum = 1, Maximum = 1000, Width = 100 };
    private readonly NumberBox _collectIterations = new() { Value = 20, Minimum = 1, Maximum = 100, Width = 100 };
    private bool _settingsLoaded;
    private bool _applyingDefaults;
    private Task? _initialization;
    private readonly InfoBar _message = new() { IsClosable = true }; private readonly ProgressBar _progress = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly AutoSuggestBox _search = new() { PlaceholderText = "玩家昵称#编号 / PUUID", Width = 260 };
    private readonly ComboBox _queue = new() { Width = 170 }; private readonly ComboBox _region = new() { Width = 170 }; private readonly ComboBox _preferred = new() { Width = 100 };
    private readonly ComboBox _date = new() { Width = 120 }; private readonly ComboBox _result = new() { Width = 100 }; private readonly CheckBox _showPractice = new() { Content = "显示训练模式" }; private readonly CheckBox _excludeRemakes = new() { Content = "排除重开" };
    private readonly NumberBox _count = new() { Value = 20, Minimum = 1, Maximum = 200, Width = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly NumberBox _jump = new() { Value = 1, Minimum = 1, Maximum = 10000, Width = 100 };
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center }; private readonly Button _previous = new() { Content = "上一页" }; private readonly Button _next = new() { Content = "下一页" };
    private readonly TextBox _tag = new() { PlaceholderText = "给此玩家添加个人标记", Width = 230 };
    private string _puuid; private string _targetServer; private int _page; private bool _loading; private bool _initialized; private int _revision;
    private sealed record HistoryPlayerContext(PlayerDataSource Source, string Puuid, JsonElement Self, JsonElement Player,
        JsonElement Auth, JsonElement Ranked, string Tag, JsonElement Mastery, JsonElement Challenges, string? MasteryError, string? ChallengesError);
    private sealed record HistoryRow(JsonElement Game, string Label) { public override string ToString() => Label; }
    private JsonElement[] _matches = [], _visibleMatches = []; private JsonElement _self; private JsonElement _player; private JsonElement _auth;
    public bool CanRefreshAfterGameEnd => _page == 0;
    public event Action<string>? PlayerLoaded;
    public HistoryPage(BackendClient backend, string puuid = "", string server = "", Action<string, string>? openPlayer = null, Action? openSelf = null, Action<string, string>? openPlayerInBackground = null)
    {
        _backend = backend; _background = new(backend); _source = new(backend); _assets = new(backend); _openPlayer = openPlayer; _openPlayerInBackground = openPlayerInBackground; _openSelf = openSelf; _puuid = _requestedPuuid = puuid; _targetServer = server;
        _history = new(PrepareHistoryAsync, async (data, query, start, count, token) =>
        {
            var games = await data.Source.HistoryAsync(data.Puuid, start, count, query.Queue).WaitAsync(token);
            return new HistoryBatch(games, data.Source.LastHistoryRawCount);
        });
        _history.Changed += HistoryChanged;
        _profile = new(_backend, LoadAsync, EditTagAsync);
        _recent = new(_backend, id => _openPlayer?.Invoke(id, _source.Server));
        if (openPlayerInBackground is not null) _recent.OpenPlayerInBackground += id => openPlayerInBackground(id, _source.Server);
        _recent.OpenEncounter += async encounter =>
        {
            var source = _source; string puuid = _puuid, server = source.Server; int revision = _revision;
            try
            {
                var game = encounter.Summary.ValueKind == JsonValueKind.Object ? encounter.Summary : await source.DetailsAsync(encounter.Record.GameId);
                if (!_loaded || revision != _revision) return;
                await NativeDialogs.ShowAsync(new ContentDialog { Title = Localization.Text("对局详情", "Match details"), Content = new ScrollViewer { Content = new MatchDetailsView(_backend, game, puuid, server, source.Source, id => _openPlayer?.Invoke(id, server), openPlayerInBackground: id => _openPlayerInBackground?.Invoke(id, server)), MaxHeight = 600 }, CloseButtonText = Localization.Text("关闭", "Close"), XamlRoot = XamlRoot });
            }
            catch (Exception ex) { if (_loaded && revision == _revision) Error(ex.Message); }
        };
        var bar = new WrapPanel(); bar.Children.Add(_search); AddButton(bar, "查询", SearchAsync, "Search", () => !_history.Busy); AddButton(bar, "搜索历史 / 好友", SearchSavedAsync, "Search history / friends"); AddButton(bar, "我的战绩", async () => { if (_openSelf is not null) { _openSelf(); return; } _requestedPuuid = ""; _targetServer = ""; await LoadPageAsync(0); }, "My history"); AddButton(bar, "刷新", LoadAsync, "Refresh", () => !_history.Busy);
        foreach (int id in new[] { 0, 420, 440, 450, 2400, 1700, 430 }) _queue.Items.Add(new ComboBoxItem { Content = id == 0 ? Localization.Text("所有模式", "All modes") : QueueName(id), Tag = id > 0 ? "q_" + id : "<akari:all>" }); _queue.Items.Insert(1, new ComboBoxItem { Content = Localization.Text("所有排位", "All ranked games"), Tag = "ranked" }); _queue.Items.Insert(2, new ComboBoxItem { Content = Localization.Text("所有普通对局", "All normal games"), Tag = "normal" }); _queue.SelectedIndex = 0;
        foreach (string option in new[] { "sgp", "lcu" }) _preferred.Items.Add(new ComboBoxItem { Content = option.ToUpperInvariant(), Tag = option }); _preferred.SelectedIndex = 0;
        _region.Items.Add(new ComboBoxItem { Content = Localization.Text("当前客户端大区", "Current client server"), Tag = "" }); _region.SelectedIndex = 0;
        foreach (int days in new[] { 0, 1, 3, 7, 30 }) _date.Items.Add(new ComboBoxItem { Tag = days }); _date.SelectedIndex = 0;
        foreach (string value in new[] { "", "win", "loss", "remake" }) _result.Items.Add(new ComboBoxItem { Tag = value }); _result.SelectedIndex = 0;
        var filters = new WrapPanel(); foreach (var control in new UIElement[] { _region, _preferred, _queue, _date, _result, _excludeRemakes, _showPractice }) filters.Children.Add(control);
        foreach (var combo in new[] { _region, _preferred, _queue }) combo.SelectionChanged += async (_, _) =>
        {
            if (!_initialized || _applyingDefaults || !_loaded) return;
            _targetServer = (_region.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            var loading = LoadPageAsync(0);
            try { if (ReferenceEquals(combo, _queue)) await _backend.CallAsync("setting-factory-main", "set", "player-tabs-renderer", "defaultMatchHistoryTag", SelectedQueueTag()); await loading; }
            catch (Exception ex) { if (_loaded) Error(ex.Message); }
        };
        _date.SelectionChanged += (_, _) => RenderMatches(); _result.SelectionChanged += (_, _) => RenderMatches(); _excludeRemakes.Checked += (_, _) => RenderMatches(); _excludeRemakes.Unchecked += (_, _) => RenderMatches(); _showPractice.Checked += (_, _) => RenderMatches(); _showPractice.Unchecked += (_, _) => RenderMatches();
        var page = new WrapPanel(); page.Children.Add(TextLabel("每页", "Per page")); page.Children.Add(_count); page.Children.Add(_previous); page.Children.Add(_pageLabel); page.Children.Add(_next); page.Children.Add(_jump);
        AddButton(page, "跳转", () => LoadPageAsync((int)Math.Max(0, NumberValue(_jump, 1) - 1)), "Go", () => !_history.Busy && _history.Page?.Collection is null);
        _previous.Click += async (_, _) => { if (_history.CanPrevious) await LoadPageAsync(_page - 1); }; _next.Click += async (_, _) => { if (_history.CanNext) await LoadPageAsync(_page + 1); };
        _count.ValueChanged += async (_, _) => { if (_initialized && !_applyingDefaults && _loaded) await LoadPageAsync(0); };
        _retry.Click += async (_, _) => await _history.RetryAsync(); _cancelLoad.Click += (_, _) => _history.Cancel();
        _normalPage.Click += async (_, _) => { if (_history.Busy) return; _filterSettings = new(); await LoadPageAsync(_history.Page?.Query.Page ?? 0); };
        page.Children.Add(_retry); page.Children.Add(_cancelLoad); page.Children.Add(_normalPage);
        var collect = new WrapPanel(); AddButton(collect, "筛选条件", EditFilterAsync, "Filters", () => !_history.Busy); AddButton(collect, "收集符合条件的战绩", CollectAsync, "Collect matching games", () => !_history.Busy); AddButton(collect, "停止收集", () => { _history.Cancel(); return Task.CompletedTask; }, "Stop collecting", () => _history.Collecting);
        foreach (var (zh, en, control) in new[] { ("每次查询", "Games per request", _collectBatch), ("目标数量", "Target count", _collectTarget), ("最大次数", "Maximum requests", _collectIterations) }) { collect.Children.Add(TextLabel(zh, en)); collect.Children.Add(control); }
        var tag = new WrapPanel(); tag.Children.Add(_tag); AddButton(tag, "保存标记", SaveTagAsync, "Save tag", () => !_history.Busy && _puuid.Length > 0); AddButton(tag, "删除标记", async () => { await _backend.CallAsync("saved-player-main", "updatePlayerTag", new { puuid = _puuid, selfPuuid = _self.Text("puuid"), region = _auth.Text("region"), rsoPlatformId = _auth.Text("rsoPlatformId"), tag = (string?)null }); await LoadTagAsync(); }, "Delete tag", () => !_history.Busy && _puuid.Length > 0); var mastery = new Expander { Content = _mastery, IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch }; BindText(() => mastery.Header = Localization.Text("英雄熟练度", "Champion mastery"));
        _games.ItemsPanel = (ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsStackPanel CacheLength='0.5'/></ItemsPanelTemplate>");
        _games.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ContentControl HorizontalContentAlignment='Stretch'/></DataTemplate>");
        _games.ContainerContentChanging += (_, e) => { if (e.ItemContainer.ContentTemplateRoot is ContentControl content) { if (e.InRecycleQueue) content.Content = null; else if (e.Item is HistoryRow item && e.Phase == 0) { content.Content = GameCard(item.Game); e.Handled = true; } } };
        InitializeHistoryLayout(bar, filters, page, collect, tag, mastery);
        Loaded += LoadedAsync; Loaded += (_, _) => { Localization.Changed -= LocaleChanged; Localization.Changed += LocaleChanged; if (NativeAppearance.Current is { } appearance) { appearance.Changed -= AppearanceChanged; appearance.Changed += AppearanceChanged; } RefreshText(); };
        Unloaded += (_, _) => { _loaded = false; ++_revision; _history.Deactivate(); _loading = false; _backend.EventReceived -= BackendEvent; Localization.Changed -= LocaleChanged; if (NativeAppearance.Current is { } appearance) appearance.Changed -= AppearanceChanged; };
        RefreshText(); UpdateBusy();
    }
    private void AppearanceChanged() { UpdateHistoryLayout(ActualWidth, ActualHeight); UpdateName(); RenderMatches(); if (_loaded && _puuid.Length > 0) _ = _background.RefreshAsync(_source, _puuid); }
    private void LocaleChanged() => DispatcherQueue.TryEnqueue(() => { if (_loaded) { RefreshText(); HistoryChanged(); RenderMatches(); if (_shownPage is { } page) RenderMastery(page.Data); } });
    private void BindText(Action apply) { _localized.Add(apply); apply(); }
    private TextBlock TextLabel(string chinese, string english) { var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center }; BindText(() => text.Text = Localization.Text(chinese, english)); return text; }
    private static double NumberValue(NumberBox input, double fallback) => double.IsFinite(input.Value) ? input.Value : fallback;
    private static string QueueName(int id) => Localization.Key("common.queueTypes." + (id switch { 420 => "RANKED_SOLO_5x5", 440 => "RANKED_FLEX_SR", 450 => "ARAM_UNRANKED_5x5", 2400 or 2401 or 2403 or 2405 or 2410 or 2450 => "KIWI", 1700 => "CHERRY", 430 => "NORMAL", _ => "" }), Localization.Text(MatchData.QueueLabel(id), "Queue " + id));
    private void RefreshText()
    {
        foreach (var apply in _localized) apply();
        _search.PlaceholderText = Localization.Text("玩家昵称#编号 / PUUID", "Player name#tag / PUUID"); _tag.PlaceholderText = Localization.Text("给此玩家添加个人标记", "Add a personal tag for this player");
        _showPractice.Content = Localization.Text("显示训练模式", "Show practice games"); _excludeRemakes.Content = Localization.Text("排除重开", "Exclude remakes");
        _previous.Content = Localization.Key("playerTabs.profile.prevPage"); _next.Content = Localization.Key("playerTabs.profile.nextPage");
        _retry.Content = Localization.Text("重试查询", "Retry query"); _cancelLoad.Content = Localization.Text("取消加载", "Cancel loading"); _normalPage.Content = Localization.Key("playerTabs.matchHistory.collectMode.reloadNormalPage");
        foreach (var section in _pagination.Children.OfType<Expander>()) section.Header = Localization.Text("收集战绩", "Collect games");
        foreach (ComboBoxItem item in _queue.Items)
        {
            string tag = item.Tag?.ToString() ?? "";
            item.Content = tag == "ranked" ? Localization.Text("所有排位", "All ranked games") : tag == "normal" ? Localization.Text("所有普通对局", "All normal games")
                : tag.StartsWith("q_") && int.TryParse(tag[2..], out int id) ? QueueName(id) : Localization.Text("所有模式", "All modes");
        }
        foreach (ComboBoxItem item in _region.Items) { string server = item.Tag?.ToString() ?? ""; item.Content = server.Length == 0 ? Localization.Text("当前客户端大区", "Current client server") : Localization.Key("common.sgpServers." + server, server); }
        foreach (ComboBoxItem item in _date.Items) { int days = (int)item.Tag; item.Content = days == 0 ? Localization.Text("不限时间", "All time") : Localization.Text($"最近 {days} 天", $"Last {days} days"); }
        foreach (ComboBoxItem item in _result.Items) item.Content = item.Tag?.ToString() switch { "win" => Localization.Text("胜利", "Victory"), "loss" => Localization.Text("失败", "Defeat"), "remake" => Localization.Text("重开", "Remake"), _ => Localization.Text("全部结果", "All results") };
        UpdatePageLabel();
    }
    private void UpdateName() { if (_player.ValueKind != JsonValueKind.Object) return; string name = _player.Text("gameName", _player.Text("displayName", _player.Text("name", _puuid))); string visible = NativeAppearance.Current?.StreamerMode == true ? NativeAppearance.Current.SummonerPlaceholder(_puuid, 0) : name + (_player.Text("tagLine").Length > 0 ? " #" + _player.Text("tagLine") : ""); PlayerLoaded?.Invoke(visible); }
    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private void AddButton(Panel parent, string label, Func<Task> action, string? english = null, Func<bool>? enabled = null)
    {
        var button = new Button { Content = label }; if (english is not null) BindText(() => button.Content = Localization.Text(label, english));
        if (enabled is not null) _actions.Add((button, enabled));
        button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); } catch (Exception ex) { if (_loaded) Error(ex.Message); } finally { button.IsEnabled = enabled?.Invoke() ?? true; } }; parent.Children.Add(button);
    }
    private void Error(string text) { _message.Title = text; _message.Message = ""; _message.Severity = InfoBarSeverity.Error; _message.IsOpen = true; }
    private async void LoadedAsync(object sender, RoutedEventArgs args)
    {
        _loaded = true; _history.Activate();
        _backend.EventReceived -= BackendEvent;
        _backend.EventReceived += BackendEvent;
        await EnsureInitializedAsync();
        if (!_loaded) return;
        if (_history.Page?.Collection is not null) HistoryChanged();
        else await LoadAsync();
    }
    private Task EnsureInitializedAsync() => _initialization ??= InitializePageAsync();
    private async Task ApplyDefaultsAsync()
    {
        _applyingDefaults = true;
        try
        {
            var settings = await _backend.CallAsync("setting-factory-main", "getByPrefix", "player-tabs-renderer", "");
            _count.Value = settings.Number("loadCount", 20);
            _preferred.SelectedIndex = settings.Field("matchHistoryUseSgpApi").ValueKind != JsonValueKind.False ? 0 : 1;
            _excludeRemakes.IsChecked = !settings.Boolean("defaultShowIrregularGames");
            _showPractice.IsChecked = settings.Boolean("defaultShowPractice");
            string tag = settings.Text("defaultMatchHistoryTag", "<akari:all>");
            foreach (ComboBoxItem item in _queue.Items) if (item.Tag?.ToString() == tag) _queue.SelectedItem = item;
            int days = settings.Text("defaultMatchHistoryTimeRange") switch { "24h" => 1, "3d" => 3, "7d" => 7, "30d" => 30, _ => 0 };
            foreach (ComboBoxItem item in _date.Items) if ((int)item.Tag == days) _date.SelectedItem = item;
            _settingsLoaded = true;
        }
        finally { _applyingDefaults = false; }
    }
    private async Task InitializePageAsync()
    {
        try
        {
            if (!_settingsLoaded) await ApplyDefaultsAsync();
            var saved = await _backend.CallAsync("setting-factory-main", "get", "winui-history", "filter");
            if (saved.ValueKind == JsonValueKind.Object) _filterSettings = JsonSerializer.Deserialize<HistoryFilterSettings>(saved.GetRawText()) ?? new();
            var gameData = await _backend.StateAsync("league-client-main", "gameData");
            var queues = gameData.Field("queues");
            var queueList = queues.ValueKind == JsonValueKind.Object ? queues.EnumerateObject().Select(q => q.Value) : queues.Items();
            var catalog = queueList.Where(q => q.Number("id") > 0).ToDictionary(q => (int)q.Number("id"));
            _recent.QueueName = id => catalog.GetValueOrDefault(id).Text("name", MatchData.QueueLabel(id));
            var sgp = await _backend.StateAsync("sgp-main");
            int[] supported = sgp.Field("supportedQueues").Items().Where(q => q.ValueKind == JsonValueKind.Number).Select(q => q.GetInt32()).ToArray();
            _queue.Items.Clear();
            _queue.Items.Add(new ComboBoxItem { Content = Localization.Text("所有模式", "All modes"), Tag = "<akari:all>" });
            _queue.Items.Add(new ComboBoxItem { Content = Localization.Text("所有排位", "All ranked games"), Tag = "ranked" });
            _queue.Items.Add(new ComboBoxItem { Content = Localization.Text("所有普通对局", "All normal games"), Tag = "normal" });
            foreach (int id in supported.Length > 0 ? supported : catalog.Keys.ToArray())
                _queue.Items.Add(new ComboBoxItem { Content = catalog.GetValueOrDefault(id).Text("name", MatchData.QueueLabel(id)), Tag = "q_" + id });
            _queue.SelectedIndex = 0;
            await ApplyDefaultsAsync();
            var servers = sgp.Field("leagueServers").Field("servers");
            if (servers.ValueKind == JsonValueKind.Object)
                foreach (var server in servers.EnumerateObject())
                    if (server.Value.Text("matchHistory").Length > 0)
                    {
                        var item = new ComboBoxItem { Content = server.Name, Tag = server.Name };
                        _region.Items.Add(item);
                        if (server.Name == _targetServer) _region.SelectedItem = item;
                    }
        }
        catch (Exception ex) { Error(ex.Message); }
        _initialized = true; RefreshText();
    }
    private void BackendEvent(JsonElement ev)
    {
        if (!_loaded) return;
        string name = ev.Text("name");
        var arguments = ev.Field("args").Items().ToArray(); var argument = arguments.FirstOrDefault(); var value = arguments.ElementAtOrDefault(1);
        string key = argument.ValueKind == JsonValueKind.String ? argument.GetString()! : "";
        if (name == "update-state-prop/league-client-main:summoner" && key == "me"
            || name == "update-state-prop/sgp-main:state" && key is "availability" or "isTokenReady" or "leagueServers"
            || name == "update-state-prop/league-client-main:state" && key is "connectionState" or "auth")
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (!_loaded) return;
                bool identityChanged = name == "update-state-prop/league-client-main:summoner" && value.Text("puuid") != _self.Text("puuid")
                    || name == "update-state-prop/sgp-main:state" && key == "availability" && value.Text("sgpServerId") != _source.CurrentServer
                    || name == "update-state-prop/league-client-main:state" && (key == "auth" || key == "connectionState" && value.ValueKind == JsonValueKind.String && value.GetString() != "connected");
                bool tokenLost = name == "update-state-prop/sgp-main:state" && key == "isTokenReady" && value.ValueKind == JsonValueKind.False;
                if (_history.Collecting && !identityChanged && !tokenLost) return;
                await LoadPageAsync(identityChanged ? 0 : _page, true);
            });
        else if (name == "update-state-prop/player-tabs-renderer:settings" && key is "loadCount" or "matchHistoryUseSgpApi" or "defaultShowIrregularGames" or "defaultShowPractice" or "defaultMatchHistoryTimeRange" or "defaultMatchHistoryTag")
            DispatcherQueue.TryEnqueue(async () => { if (!_loaded) return; try { await ApplyDefaultsAsync(); if (_loaded) await LoadPageAsync(0, true); } catch (Exception ex) { if (_loaded) Error(ex.Message); } });
        else if (name == "update-state-prop/main-window-ui-renderer:settings" && key == "useProfileSkinAsBackground" && _puuid.Length > 0)
            DispatcherQueue.TryEnqueue(async () => { if (_loaded) await _background.RefreshAsync(_source, _puuid); });
    }
    private async Task SearchSavedAsync()
    {
        var store = new PlayerSearchHistory(_backend); var rows = new StackPanel { Spacing = 6 }; var query = new TextBox { PlaceholderText = Localization.Text("筛选玩家", "Filter players") }; var body = new StackPanel { Spacing = 8 }; body.Children.Add(query); body.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 500 }); var dialog = new ContentDialog { Title = Localization.Text("搜索历史与好友", "Search history and friends"), Content = body, CloseButtonText = Localization.Text("关闭", "Close"), XamlRoot = XamlRoot };
        var history = await store.ReadAsync(); JsonElement[] friends = [];
        try { friends = (await _source.LcuAsync("GET", "/lol-chat/v1/friends")).Items().ToArray(); }
        catch (Exception ex) { body.Children.Add(new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap }); }
        void Render() { rows.Children.Clear(); foreach (var item in history) { string label = (NativeAppearance.Current?.StreamerMode == true ? NativeAppearance.Current.SummonerPlaceholder(item.Puuid, history.IndexOf(item)) : item.Summoner.Text("gameName") + "#" + item.Summoner.Text("tagLine")) + " · " + item.Server; if (!label.Contains(query.Text, StringComparison.OrdinalIgnoreCase)) continue; var row = new WrapPanel(); AddButton(row, (item.IsPinned ? "★ " : "") + label, () => { dialog.Hide(); _openPlayer?.Invoke(item.Puuid, item.Server); return Task.CompletedTask; }); AddButton(row, item.IsPinned ? Localization.Text("取消置顶", "Unpin") : Localization.Text("置顶", "Pin"), async () => { await store.PinAsync(item.Puuid); history = await store.ReadAsync(); Render(); }); AddButton(row, Localization.Text("删除", "Delete"), async () => { await store.DeleteAsync(item.Puuid); history = await store.ReadAsync(); Render(); }); rows.Children.Add(row); } rows.Children.Add(new TextBlock { Text = Localization.Text("好友", "Friends"), FontWeight = Microsoft.UI.Text.FontWeights.Bold }); foreach (var friend in friends) { string label = NativeAppearance.Current?.StreamerMode == true ? NativeAppearance.Current.SummonerPlaceholder(friend.Text("puuid"), Array.IndexOf(friends, friend)) : friend.Text("gameName", friend.Text("name")) + "#" + friend.Text("gameTag", friend.Text("tagLine")); if (!label.Contains(query.Text, StringComparison.OrdinalIgnoreCase)) continue; AddButton(rows, label, () => { dialog.Hide(); _openPlayer?.Invoke(friend.Text("puuid"), _source.CurrentServer); return Task.CompletedTask; }); } }
        query.TextChanged += (_, _) => Render(); Render(); await NativeDialogs.ShowAsync(dialog);
    }
    private async Task SearchAsync()
    {
        int revision = _revision;
        var source = new PlayerDataSource(_backend);
        await source.ConfigureAsync(_targetServer, SelectedSource());
        var player = await source.FindAsync(_search.Text);
        string puuid = player.Text("puuid");
        if (puuid.Length == 0) throw new InvalidOperationException(Localization.Text("查询结果不含玩家身份", "No player identity in result"));
        if (!_loaded || revision != _revision) return;
        await new PlayerSearchHistory(_backend).SaveAsync(puuid, source.Server, player);
        if (!_loaded || revision != _revision) return;
        if (_openPlayer is not null) { _openPlayer(puuid, source.Server); return; }
        _requestedPuuid = puuid; _targetServer = source.Server; await LoadPageAsync(0);
    }
    public async Task ConfigureCollectionAsync(int? champion, string? position)
    {
        if (_history.Collecting) return;
        await EnsureInitializedAsync();
        await _history.WaitAsync();
        if (!_loaded) return;
        if (_player.ValueKind != JsonValueKind.Object) await LoadAsync();
        if (!_loaded || _player.ValueKind != JsonValueKind.Object) return;
        string puuid = _puuid; int revision = _revision;
        int expected = (int)(await _backend.CallAsync("setting-factory-main", "get", "ongoing-game-main", "matchHistoryLoadCount")).TryNumber(20);
        if (!_loaded || revision != _revision || _puuid != puuid) return;
        var collection = HistoryCollection.ForPlayer(_puuid, champion, position, expected, _source.Source == "sgp");
        _filterSettings = collection.Filter;
        _collectBatch.Value = collection.BatchSize; _collectTarget.Value = collection.TargetCount; _collectIterations.Value = collection.Iterations;
        await CollectAsync();
    }
    public Task LoadAsync() => _history.Collecting ? Task.CompletedTask : LoadPageAsync(_page);
    private Task LoadPageAsync(int page, bool force = false)
    {
        if (!_loaded) return Task.CompletedTask;
        ++_revision;
        return _history.LoadAsync(new(_requestedPuuid, _targetServer, SelectedSource(), Math.Max(0, page), (int)Math.Clamp(NumberValue(_count, 20), 1, 200), SelectedQueueTag()), force);
    }
    private async Task<HistoryPlayerContext> PrepareHistoryAsync(HistoryQuery query, CancellationToken token)
    {
        var summoner = await _backend.StateAsync("league-client-main", "summoner").WaitAsync(token); var self = summoner.Field("me");
        string puuid = query.Puuid.Length > 0 ? query.Puuid : self.Text("puuid");
        if (puuid.Length == 0) throw new InvalidOperationException(Localization.Text("等待 LOL 客户端连接", "Waiting for League client"));
        var source = new PlayerDataSource(_backend); await source.ConfigureAsync(query.Server, query.PreferredSource).WaitAsync(token);
        var decision = await source.RefreshAvailabilityAsync().WaitAsync(token); if (decision.Type != "load") throw new PlayerSourceUnavailableException(decision);
        var player = source.Server == source.CurrentServer && self.Text("puuid") == puuid ? self : await source.ProfileAsync(puuid).WaitAsync(token);
        var auth = (await _backend.StateAsync("league-client-main").WaitAsync(token)).Field("auth");
        JsonElement ranked = default, mastery = default, challenges = default; string tag = ""; string? masteryError = null, challengesError = null;
        if (_shownPage?.Data is { } previous && previous.Puuid == puuid && previous.Source.Server == source.Server)
        {
            ranked = previous.Ranked;
            if (query.Page != 0) { mastery = previous.Mastery; challenges = previous.Challenges; masteryError = previous.MasteryError; challengesError = previous.ChallengesError; }
        }
        if (source.SupportsRanked) try { ranked = await source.RankedAsync(puuid).WaitAsync(token); } catch (Exception) when (!token.IsCancellationRequested) { }
        if (self.Text("puuid").Length > 0)
            try { var tags = await _backend.CallAsync("saved-player-main", "getPlayerTags", new { puuid, selfPuuid = self.Text("puuid"), region = auth.Text("region"), rsoPlatformId = auth.Text("rsoPlatformId") }).WaitAsync(token); tag = tags.Items().FirstOrDefault(t => t.Text("selfPuuid") == self.Text("puuid")).Text("tag"); } catch (Exception) when (!token.IsCancellationRequested) { }
        if (query.Page == 0 && source.SupportsMastery) try { mastery = await source.MasteryAsync(puuid, 8).WaitAsync(token); } catch (Exception ex) when (!token.IsCancellationRequested) { masteryError = ex.Message; }
        if (query.Page == 0 && source.SupportsChallenges) try { challenges = await source.ChallengesAsync(puuid).WaitAsync(token); } catch (Exception ex) when (!token.IsCancellationRequested) { challengesError = ex.Message; }
        token.ThrowIfCancellationRequested();
        return new(source, puuid, self, player, auth, ranked, tag, mastery, challenges, masteryError, challengesError);
    }
    private void HistoryChanged()
    {
        if (!_loaded) return;
        bool busyChanged = _loading != _history.Busy;
        _loading = _history.Busy;
        if (_history.Page is { } page && !ReferenceEquals(page, _shownPage))
        {
            bool newPlayerContext = !ReferenceEquals(page.Data, _shownPage?.Data); _shownPage = page;
            _source = page.Data.Source; _puuid = page.Data.Puuid; _self = page.Data.Self; _player = page.Data.Player; _auth = page.Data.Auth; _page = page.Query.Page; _matches = page.Games;
            if (newPlayerContext)
            {
                _tag.Text = page.Data.Tag; _profile.SetData(_player, page.Data.Ranked, crossRegion: _source.IsCrossRegion, isSelf: _self.Text("puuid") == _puuid, source: _source); UpdateName();
                _ = _recent.ConfigureEncountersAsync(_source, _puuid, _self.Text("puuid"), _auth.Text("region"), _auth.Text("rsoPlatformId"));
                if (_page == 0) { RenderMastery(page.Data); _ = _background.RefreshAsync(_source, _puuid); }
            }
            RenderMatches();
        }
        else if (busyChanged && _shownPage is not null) RenderSummary(_visibleMatches);
        if (_history.Error is PlayerSourceUnavailableException unavailable)
        {
            _message.Title = unavailable.Decision.Type == "wait" ? Localization.Text("等待 SGP 登录凭据就绪", "Waiting for SGP login credentials") : Localization.Text("此大区没有可用的 SGP 接口", "SGP is unavailable for this server");
            _message.Message = ""; _message.Severity = unavailable.Decision.Type == "wait" ? InfoBarSeverity.Informational : InfoBarSeverity.Error; _message.IsOpen = true;
        }
        else if (_history.Error is { } error)
        {
            _message.Title = Localization.Key("playerTabs.profile.failedToLoadMatchHistoryTitle"); _message.Message = Localization.Key("playerTabs.profile.failedToLoadMatchHistoryContent", arguments: new Dictionary<string, object?> { ["reason"] = error.Message }); _message.Severity = InfoBarSeverity.Error; _message.IsOpen = true;
        }
        else if (_history.Busy || _shownPage is not null) _message.IsOpen = false;
        if (!_history.Busy && _history.Page is { } committed)
        {
            _applyingDefaults = true;
            try { _count.Value = committed.Query.Count; _jump.Value = committed.Query.Page + 1; foreach (ComboBoxItem item in _queue.Items) if (item.Tag?.ToString() == committed.Query.Queue) _queue.SelectedItem = item; }
            finally { _applyingDefaults = false; }
        }
        UpdateBusy(); UpdatePageLabel();
    }
    private void UpdateBusy()
    {
        _loading = _history.Busy; _progress.Visibility = _loading ? Visibility.Visible : Visibility.Collapsed;
        _previous.IsEnabled = _history.CanPrevious; _next.IsEnabled = _history.CanNext;
        _count.IsEnabled = _jump.IsEnabled = !_loading && _history.Page?.Collection is null;
        _queue.IsEnabled = _date.IsEnabled = _result.IsEnabled = _showPractice.IsEnabled = _excludeRemakes.IsEnabled = !_loading;
        _collectBatch.IsEnabled = _collectTarget.IsEnabled = _collectIterations.IsEnabled = !_loading;
        _retry.IsEnabled = _history.CanRetry; _retry.Visibility = _history.CanRetry ? Visibility.Visible : Visibility.Collapsed;
        _cancelLoad.Visibility = _loading && !_history.Collecting ? Visibility.Visible : Visibility.Collapsed;
        _normalPage.IsEnabled = !_loading; _normalPage.Visibility = _history.Page?.Collection is not null ? Visibility.Visible : Visibility.Collapsed;
        foreach (var action in _actions) action.Button.IsEnabled = action.Enabled();
    }
    private void UpdatePageLabel()
    {
        if (_history.Page is not { } page) { _pageLabel.Text = ""; return; }
        if (page.Collection is { } status)
        {
            string title = status switch { HistoryCollectionStatus.Collecting => Localization.Text("收集中", "Collecting"), HistoryCollectionStatus.Stopped => Localization.Text("已停止", "Stopped"), HistoryCollectionStatus.Failed => Localization.Text("收集中断", "Collection failed"), _ => Localization.Text("收集完成", "Collection complete") };
            _pageLabel.Text = title + Localization.Text($"：扫描 {page.Scanned} 场 · 保留 {page.Games.Length} 场", $": {page.Scanned} scanned · {page.Games.Length} retained");
        }
        else _pageLabel.Text = Localization.Text($"第 {page.Query.Page + 1} 页", $"Page {page.Query.Page + 1}") + $" · {page.Data.Source.Server} · {page.Data.Source.Source.ToUpperInvariant()}";
    }
    private string SelectedSource() => (_preferred.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "sgp";
    private string SelectedQueueTag() => (_queue.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "<akari:all>";
    private static bool MatchesQueueTag(JsonElement game, string tag, string source)
    {
        if (tag is "" or "<akari:all>") return true;
        int queue = (int)game.Number("queueId");
        if (tag.StartsWith("q_", StringComparison.Ordinal)) return int.TryParse(tag[2..], out int id) && queue == id;
        // The SGP service has already applied its own ranked/normal grouping.
        return source == "sgp" || (tag == "ranked" ? queue is 420 or 440 : queue is not 420 and not 440);
    }
    private void RenderMatches()
    {
        if (!_initialized) return;
        JsonElement[] visible = _matches;
        if (_shownPage?.Collection is null)
        {
            string queue = _shownPage?.Query.Queue ?? SelectedQueueTag(); int days = (int?)((_date.SelectedItem as ComboBoxItem)?.Tag) ?? 0; string result = (_result.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            _filterSettings.EnablePosition = _source.Source == "sgp"; var filter = new HistoryFilter(_filterSettings);
            visible = _matches.Where(g => (_showPractice.IsChecked == true || g.Text("gameType") != "PRACTICE_GAME") && MatchesQueueTag(g, queue, _source.Source) && (days == 0 || MatchData.Creation(g) >= DateTimeOffset.Now.AddDays(-days)))
                .Where(g => MatchData.Self(g, _puuid) is { } p && (result.Length == 0 || p.WinResult == result) && (_excludeRemakes.IsChecked != true || p.WinResult is "win" or "loss") && filter.Match(g, _puuid)).ToArray();
        }
        _visibleMatches = visible; _games.ItemsSource = visible.Select(g => new HistoryRow(g, $"{QueueName((int)g.Number("queueId"))} · {MatchData.Creation(g).ToLocalTime():MM-dd HH:mm}")).ToArray(); _recent.SetHistory(visible, _puuid); RenderSummary(visible);
    }
    private UIElement GameCard(JsonElement game) => new HistoryMatchCard(_backend, game, _puuid, _source.Server, _source.Source, _openPlayer, openPlayerInBackground: _openPlayerInBackground);
    private object TagQuery() => new { puuid = _puuid, selfPuuid = _self.Text("puuid"), region = _auth.Text("region"), rsoPlatformId = _auth.Text("rsoPlatformId") };
    private async Task EditFilterAsync()
    {
        var draft = JsonSerializer.Deserialize<HistoryFilterSettings>(JsonSerializer.Serialize(_filterSettings)) ?? new(); draft.EnablePosition = _source.Source == "sgp";
        var source = _source; string puuid = _puuid; int revision = _revision;
        var summoners = _matches.SelectMany(MatchData.Participants).Select(p => JsonSerializer.SerializeToElement(new { puuid = p.Puuid, gameName = p.Name, tagLine = p.Tag, profileIconId = (int)p.Raw.Number("profileIconId") })).Append(_player);
        var editor = new HistoryFilterEditor(_backend, puuid, draft, summoners, source.FindAsync); var dialog = new ContentDialog { Title = Localization.Text("战绩筛选", "History filters"), Content = new ScrollViewer { Content = editor, MaxHeight = 600 }, PrimaryButtonText = Localization.Text("应用", "Apply"), CloseButtonText = Localization.Text("取消", "Cancel"), XamlRoot = XamlRoot };
        if (await NativeDialogs.ShowAsync(dialog) == ContentDialogResult.Primary && _loaded && revision == _revision && puuid == _puuid) { _filterSettings = editor.Settings; await _backend.CallAsync("setting-factory-main", "set", "winui-history", "filter", _filterSettings); RenderMatches(); if (_filterSettings.CollectEnabled) await CollectAsync(); }
    }
    private async Task CollectAsync()
    {
        if (!_loaded || _history.Busy) return;
        ++_revision;
        var query = new HistoryQuery(_requestedPuuid, _targetServer, SelectedSource(), _page, (int)Math.Clamp(NumberValue(_count, 20), 1, 200), SelectedQueueTag());
        var filter = JsonSerializer.Deserialize<HistoryFilterSettings>(JsonSerializer.Serialize(_filterSettings))!;
        int days = (int?)((_date.SelectedItem as ComboBoxItem)?.Tag) ?? 0; string result = (_result.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        bool showPractice = _showPractice.IsChecked == true, excludeRemakes = _excludeRemakes.IsChecked == true;
        DateTimeOffset cutoff = days == 0 ? DateTimeOffset.MinValue : DateTimeOffset.Now.AddDays(-days);
        bool MeetsFilters(HistoryPlayerContext data, JsonElement game)
        {
            filter.EnablePosition = data.Source.Source == "sgp";
            return new HistoryFilter(filter).Match(game, data.Puuid) && (showPractice || game.Text("gameType") != "PRACTICE_GAME")
                && MatchesQueueTag(game, query.Queue, data.Source.Source) && MatchData.Creation(game) >= cutoff && MatchData.Self(game, data.Puuid) is { } player
                && (result.Length == 0 || player.WinResult == result) && (!excludeRemakes || player.WinResult is "win" or "loss");
        }
        await _history.CollectAsync(query, new((int)Math.Clamp(NumberValue(_collectBatch, 20), 1, 100), (int)Math.Clamp(NumberValue(_collectTarget, 20), 1, 1000), (int)Math.Clamp(NumberValue(_collectIterations, 20), 1, 100)), MeetsFilters);
    }
    private async Task LoadTagAsync() { if (_self.Text("puuid").Length == 0) return; int revision = _revision; string self = _self.Text("puuid"); var tags = await _backend.CallAsync("saved-player-main", "getPlayerTags", TagQuery()); if (_loaded && revision == _revision) _tag.Text = tags.Items().FirstOrDefault(t => t.Text("selfPuuid") == self).Text("tag"); }
    private async Task EditTagAsync()
    {
        var text = new TextBox { Text = _tag.Text, TextWrapping = TextWrapping.Wrap };
        var dialog = new ContentDialog { Title = Localization.Text("玩家标记", "Player tag"), Content = text, PrimaryButtonText = Localization.Text("保存", "Save"), CloseButtonText = Localization.Text("取消", "Cancel"), XamlRoot = XamlRoot };
        if (await NativeDialogs.ShowAsync(dialog) == ContentDialogResult.Primary) { _tag.Text = text.Text; await SaveTagAsync(); }
    }
    private async Task SaveTagAsync() { await _backend.CallAsync("saved-player-main", "updatePlayerTag", new { puuid = _puuid, selfPuuid = _self.Text("puuid"), region = _auth.Text("region"), rsoPlatformId = _auth.Text("rsoPlatformId"), tag = _tag.Text }); _message.Title = Localization.Text("标记已保存", "Tag saved"); _message.Severity = InfoBarSeverity.Success; _message.IsOpen = true; }
    private void RenderMastery(HistoryPlayerContext data)
    {
        _mastery.Children.Clear(); _assetsSummary.Children.Clear();
        if (data.Source.SupportsMastery)
        {
            if (data.MasteryError is { } masteryError) _mastery.Children.Add(new TextBlock { Text = Localization.Text("熟练度：", "Mastery: ") + masteryError });
            else if (data.Mastery.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                var values = data.Mastery; var raw = values.Field("masteries").ValueKind == JsonValueKind.Array ? values.Field("masteries") : values; var entries = raw.Items().OrderByDescending(m => m.Number("championPoints")).ToArray();
                AddButton(_mastery, Localization.Text("查看全部英雄熟练度", "View all champion mastery"), async () => { int revision = _revision; var all = await data.Source.MasteryAsync(data.Puuid); if (_loaded && revision == _revision) await ShowMasteryAsync(all.Items().ToArray()); });
                if (values.Field("score").ValueKind == JsonValueKind.Number) _mastery.Children.Add(new TextBlock { Text = Localization.Text("总分", "Total score") + " · " + values.Number("score") }); foreach (var mastery in entries.Take(8)) _mastery.Children.Add(MasteryRow(mastery));
            }
        }
        if (data.Source.SupportsChallenges)
        {
            if (data.ChallengesError is { } challengesError) _assetsSummary.Children.Add(new TextBlock { Text = Localization.Text("藏品数据：", "Collection: ") + challengesError, TextWrapping = TextWrapping.Wrap });
            else
            {
                var list = data.Challenges.Field("playerChallenges").Items().ToArray(); var summary = new WrapPanel();
                foreach (var (id, key) in new[] { (505001, "champions"), (510001, "championSkins"), (510011, "chromaSkins"), (504003, "wardSkins"), (504002, "summonerIcons"), (504004, "emotes") }) { var entry = list.FirstOrDefault(e => e.Number("id") == id); if (entry.ValueKind == JsonValueKind.Object) summary.Children.Add(new TextBlock { Text = Localization.Key("playerTabs.challenges." + key) + " · " + entry.Number("currentValue").ToString("N0") }); }
                if (summary.Children.Count > 0) { _assetsSummary.Children.Add(new TextBlock { Text = Localization.Key("playerTabs.challenges.titleAssets"), FontWeight = Microsoft.UI.Text.FontWeights.Bold }); _assetsSummary.Children.Add(summary); }
            }
        }
    }
    private UIElement MasteryRow(JsonElement mastery) { var row = Row(); int champion = (int)mastery.Number("championId"); row.Children.Add(_assets.Icon("champion-summary", champion, 32)); var title = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 230, Text = $"{champion} · {mastery.Number("championLevel")} · {mastery.Number("championPoints"):N0}", VerticalAlignment = VerticalAlignment.Center }; row.Children.Add(title); _ = SetChampionTitleAsync(title, mastery); var progress = new StackPanel { Spacing = 2 }; progress.Children.Add(new TextBlock { Text = $"{Localization.Text("下一级还需", "Points to next level")} {mastery.Number("championPointsUntilNextLevel"):N0}", FontSize = 11 }); progress.Children.Add(new TextBlock { Text = $"{Localization.Text("最后游玩", "Last played")} {(mastery.Number("lastPlayTime") > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)mastery.Number("lastPlayTime")).ToLocalTime().ToString("yyyy-MM-dd") : "—")}", FontSize = 11 }); ToolTipService.SetToolTip(row, progress); return row; }
    private async Task SetChampionTitleAsync(TextBlock text, JsonElement mastery) { var entry = await _assets.EntryAsync("champion-summary", (int)mastery.Number("championId")); text.Text = $"{entry.Text("name", mastery.Number("championId").ToString())} · {mastery.Number("championLevel")} {Localization.Text("级", "level")} · {mastery.Number("championPoints"):N0}"; }
    private async Task ShowMasteryAsync(JsonElement[] entries) { var search = new TextBox { PlaceholderText = Localization.Text("搜索英雄名称或编号", "Search champion name or ID") }; var list = new ListView { SelectionMode = ListViewSelectionMode.None, MaxHeight = 450 }; var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(search); panel.Children.Add(list); var names = new Dictionary<int, string>(); foreach (var entry in entries) { int id = (int)entry.Number("championId"); names[id] = (await _assets.EntryAsync("champion-summary", id)).Text("name", id.ToString()); } void Filter() { list.Items.Clear(); foreach (var entry in entries.Where(m => (names[(int)m.Number("championId")] + m.Number("championId")).Contains(search.Text, StringComparison.OrdinalIgnoreCase))) list.Items.Add(MasteryRow(entry)); } search.TextChanged += (_, _) => Filter(); Filter(); await NativeDialogs.ShowAsync(new ContentDialog { Title = Localization.Key("playerTabs.championMastery.modalTitle"), Content = panel, CloseButtonText = Localization.Text("关闭", "Close"), XamlRoot = XamlRoot }); }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using LeagueAkari.WinUI.Pages;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace LeagueAkari.WinUI.Windows;

public sealed partial class OpggWindow : Window
{
    private readonly BackendClient _backend;
    private readonly SettingsForms _forms;
    private readonly WindowLayout _layout;
    private readonly StackPanel _body = new() { Spacing = 12, Padding = new Thickness(18) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private QueryStatus _queryStatus;
    private string _statusPayload = "";
    private enum QueryStatus { Empty, Loading, Loaded, Canceled, InitializationFailed, QueryFailed, BalanceFailed, Applied, ApplyFailed, RawError }
    private readonly StackPanel _details = new() { Spacing = 12 };
    private readonly ContentControl _heroContent = new();
    private readonly ContentControl _settingsContent = new();
    private readonly Button _settingsButton = new();
    private readonly List<(FrameworkElement Control, string Key)> _settingLabels = [];
    private JsonObject _kiwiBalance = new();
    private readonly WrapPanel _sessionChampions = new();
    private string _sessionChampionSignature = "";
    private JsonObject _aramBalance = new();
    private DateTimeOffset _balanceFetched;
    private bool _itemsImported;
    private string _followSignature = "";
    private readonly ListView _table = new() { MaxHeight = 450, IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None };
    private readonly TextBox _search = new() { PlaceholderText = "搜索英雄" };
    private readonly ComboBox _mode = SettingsForms.Select("模式", [("ranked", "排位"), ("aram", "大乱斗 / 海斗"), ("arena", "斗魂竞技场"), ("nexus_blitz", "极限闪击"), ("urf", "无限火力")]);
    private readonly ComboBox _region = SettingsForms.Select("数据地区", new[] { "global", "na", "euw", "kr", "br", "eune", "jp", "lan", "las", "oce", "tr", "ru", "sg", "vn", "tw", "me" }.Select(x => (x, x == "global" ? "全球" : x.ToUpperInvariant())));
    private readonly ComboBox _position = SettingsForms.Select("位置", [("top", "上单"), ("jungle", "打野"), ("mid", "中单"), ("adc", "下路"), ("support", "辅助"), ("none", "无位置")]);
    private readonly ComboBox _tier = SettingsForms.Select("段位", new[] { "all", "ibsg", "gold_plus", "platinum_plus", "emerald_plus", "diamond_plus", "master", "master_plus", "grandmaster", "challenger" }.Select(x => (x, x)));
    private readonly ComboBox _version = SettingsForms.Select("版本", [("", "最新版本")]);
    private readonly ComboBox _direction = SettingsForms.Select("顺序", [("asc", "升序"), ("desc", "降序")]);
    private readonly ComboBox _sort = SettingsForms.Select("排序", [("rank", "排名"), ("tier", "强度"), ("win", "胜率"), ("pick", "选取率"), ("ban", "禁用率"), ("name", "英雄名称")]);
    private JsonObject _catalog = new(), _preferences = new(), _build = new();
    private JsonArray _champions = new();
    private int _hero, _generation;
    private bool _ready, _shutdown, _visible, _initializing, _loading, _writing;
    private OpggQuery _query = new("global", "ranked", "top", "all", "");
    private string[] _versions = [];
    private JsonArray _kiwiAugments = new();
    private string _responseVersion = "", _cachedAt = "";
    private bool _tableWide, _tableMedium, _allCounters;
    private CancellationTokenSource? _requestCancellation;
    private readonly Dictionary<string, bool> _expandedWidgets = [];
    private bool _kiwiAdvanced, _kiwiExpanded;
    private string _kiwiSort = "default", _kiwiTab = "", _arenaTab = "";
    private AutoSelectChampion[] _searchChampions = [];
    private readonly TabView _tabs = new() { IsAddTabButtonVisible = false };
    private readonly TabViewItem _tierTab = new() { IsClosable = false };
    private readonly TabViewItem _heroTab = new() { IsClosable = false, IsEnabled = false };
    private readonly Button _refresh = new();
    private readonly Button _cancel = new();
    private readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromSeconds(2) };
    private string[] _sessionConfigKeys = [];
    private string _sessionMode = "";
    private bool _following;
    private IDisposable? _appearance;

    public OpggWindow(BackendClient backend)
    {
        _backend = backend; _forms = new(backend); Title = "LeagueAkari · OP.GG"; AppWindow.Resize(new global::Windows.Graphics.SizeInt32(1060, 780)); _layout = new(this, backend, "opgg-window");
        string icon = System.IO.Path.Combine(AppContext.BaseDirectory, "LA_ICON.ico");
        if (System.IO.File.Exists(icon)) AppWindow.SetIcon(icon);
        var filters = new WrapPanel();
        foreach (var combo in new[] { _mode, _region, _position, _tier, _version }) { combo.MinWidth = 120; filters.Children.Add(combo); }
        _body.Children.Add(filters); _body.Children.Add(_status);
        var actions = new WrapPanel();
        _refresh.Click += async (_, _) => await Refresh(true);
        _cancel.Click += (_, _) => { _requestCancellation?.Cancel(); _generation++; RestoreFilters(); SetLoading(false); SetStatus(QueryStatus.Canceled); };
        actions.Children.Add(_refresh); actions.Children.Add(_cancel); actions.Children.Add(_sort); actions.Children.Add(_direction);
        var website = new Button { Content = "OP.GG" }; website.Click += async (_, _) => await global::Windows.System.Launcher.LaunchUriAsync(new Uri("https://www.op.gg/")); actions.Children.Add(website);
        _settingsButton.Click += async (_, _) => await ShowOpggSettings(); actions.Children.Add(_settingsButton);
        _body.Children.Add(actions); _body.Children.Add(_sessionChampions);
        var tierContent = new StackPanel { Spacing = 8 }; tierContent.Children.Add(_search); tierContent.Children.Add(_table);
        _tierTab.Content = tierContent; _heroContent.Content = _details; _heroTab.Content = _heroContent; _tabs.TabItems.Add(_tierTab); _tabs.TabItems.Add(_heroTab); _tabs.SelectedIndex = 0;
        _body.Children.Add(_tabs); _body.Children.Add(_forms.Status);
        Content = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Localization.Changed += LanguageChanged;
        _forms.Observe(); _backend.EventReceived += SettingsEvent;
        _table.ItemClick += async (_, e) => { if (e.ClickedItem is FrameworkElement { Tag: int hero }) await SelectHero(hero); };
        _search.TextChanged += (_, _) => RenderTable(); _sort.SelectionChanged += (_, _) => RenderTable(); _direction.SelectionChanged += (_, _) => RenderTable();
        _table.SizeChanged += (_, _) => { bool wide = _table.ActualWidth >= 600, medium = _table.ActualWidth >= 520; if (wide != _tableWide || medium != _tableMedium) { _tableWide = wide; _tableMedium = medium; RenderTable(); } };
        foreach (var combo in new[] { _mode, _region, _position, _tier }) combo.SelectionChanged += async (_, _) => { if (_ready && !_loading && SettingsForms.TrySelected(combo, out _)) await Refresh(combo == _mode || combo == _region); };
        _version.SelectionChanged += async (_, _) => { if (_ready && !_loading && SettingsForms.TrySelected(_version, out _)) await Refresh(false); };
        AppWindow.Closing += (_, e) => { if (!_shutdown) { e.Cancel = true; SetVisible(false); } };
        _follow.Tick += async (_, _) => await FollowClient();
    }
    public bool IsVisible => _visible;
    public Task PersistAsync() => _layout.PersistAsync();
    public async Task ApplySettingsAsync()
    {
        var values = await _backend.StateAsync("window-manager-main/opgg-window", "settings");
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.IsAlwaysOnTop = values.TryGetProperty("pinned", out var pinned) && pinned.GetBoolean();
        _layout.SetOpacity(values.TryGetProperty("opacity", out var opacity) ? opacity.GetDouble() : 1);
    }
    public void ResetPosition() => _layout.Center();
    public async void Show() { SetVisible(true); if (!_ready) await Initialize(); }
    public void SetVisible(bool visible) { _appearance ??= NativeAppearance.Current?.Watch(this, (FrameworkElement)Content, "window-manager-main/opgg-window"); _visible = visible; if (visible) { AppWindow.Show(); Activate(); _follow.Start(); LanguageChanged(); } else { _follow.Stop(); AppWindow.Hide(); } }
    public void Shutdown() { _shutdown = true; _follow.Stop(); _requestCancellation?.Cancel(); _generation++; _forms.Release(); _backend.EventReceived -= SettingsEvent; Localization.Changed -= LanguageChanged; _appearance?.Dispose(); _layout.Dispose(); Close(); }
    private void LanguageChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        LocalizeFilters(); LocalizeSettings(); RenderStatus(); LocalizeFormStatus(); RenderTable();
        if (_build.Count > 0) RenderDetails();
        _sessionChampionSignature = "";
    });
    private void SettingsEvent(JsonElement envelope)
    {
        if (envelope.Text("name") is "update-state-prop/league-client-main:gameData" or "update-state-prop/extra-assets-main:gtimg") DispatcherQueue.TryEnqueue(async () => { if (_visible && !_shutdown) await ReloadCatalog(); });
        if (envelope.Text("name") == "update-state-prop/window-manager-main/opgg-window:settings") DispatcherQueue.TryEnqueue(async () => { if (!_shutdown) try { await ApplySettingsAsync(); } catch (Exception ex) { SetStatus(QueryStatus.RawError, ex.Message); } });
        if (envelope.Text("name") == "update-state-prop/league-client-main:gameflow") { var args = envelope.Field("args").Items().ToArray(); if (args.Length >= 2 && args[0].ValueKind == JsonValueKind.String && args[0].GetString() == "phase" && args[1].ValueKind == JsonValueKind.String && args[1].GetString() == "EndOfGame") DispatcherQueue.TryEnqueue(async () => { if (_itemsImported) try { await _backend.CallAsync("league-client-main", "writeItemSetsToDisk", (object?)null); _itemsImported = false; } catch (Exception ex) { SetStatus(QueryStatus.RawError, ex.Message); } }); }
    }
    public async Task<object?> HandleAsync(string method, JsonElement[] args)
    {
        var presenter = AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
        switch (method)
        {
            case "ensure": return null;
            case "show": Show(); return null;
            case "hide": case "close": SetVisible(false); return null;
            case "toggle": if (_visible) SetVisible(false); else Show(); return null;
            case "setFakeShow": if (args[0].GetBoolean()) Show(); else SetVisible(false); return null;
            case "restore": presenter?.Restore(); Show(); return null;
            case "minimize": presenter?.Minimize(); return null;
            case "maximize": presenter?.Maximize(); return null;
            case "unmaximize": presenter?.Restore(); return null;
            case "getSize": return new[] { AppWindow.Size.Width, AppWindow.Size.Height };
            case "getPosition": return new[] { AppWindow.Position.X, AppWindow.Position.Y };
            case "setTitle": Title = args[0].GetString() ?? ""; return null;
            case "setSize": AppWindow.Resize(new global::Windows.Graphics.SizeInt32((int)args[0].GetDouble(), (int)args[1].GetDouble())); return null;
            case "setPosition": AppWindow.Move(new global::Windows.Graphics.PointInt32((int)args[0].GetDouble(), (int)args[1].GetDouble())); return null;
            case "resetPosition": ResetPosition(); return null;
            case "repositionWindowIfInvisible": _layout.RepositionIfInvisible(); return null;
            case "setPinned": if (presenter is not null) presenter.IsAlwaysOnTop = args[0].GetBoolean(); await _backend.CallAsync("setting-factory-main", "set", "window-manager-main/opgg-window", "pinned", args[0]); return null;
            case "setOpacity": _layout.SetOpacity(args[0].GetDouble()); await _backend.CallAsync("setting-factory-main", "set", "window-manager-main/opgg-window", "opacity", args[0]); return null;
            case "setIgnoreMouseEvents": _layout.SetClickThrough(args[0].GetBoolean()); return null;
            case "applySettings": await ApplySettingsAsync(); return null;
            case "toggleDevtools": return null;
            default: throw new NotSupportedException("OP.GG window: " + method);
        }
    }
    private async Task Initialize()
    {
        if (_initializing || _ready) return;
        _initializing = true;
        try
        {
            await _forms.Load("opgg-renderer", "auto-champ-config-main", "window-manager-main/opgg-window");
            await _layout.RestoreAsync();
            _catalog = JsonNode.Parse((await _backend.StateAsync("league-client-main", "gameData")).GetRawText())?.AsObject() ?? new();
            var extra = await _backend.StateAsync("extra-assets-main", "gtimg");
            _searchChampions = AutoSelectData.Champions(JsonSerializer.SerializeToElement(_catalog["champions"]), extra);
            _preferences = (_forms.Value("opgg-renderer", "savedPreferences") as JsonObject)?.DeepClone().AsObject() ?? new();
            foreach (var (combo, key) in new[] { (_mode, "mode"), (_region, "region"), (_position, "position"), (_tier, "tier") }) Choose(combo, _preferences[key]?.ToString());
            _query = new OpggQuery(SettingsForms.Selected(_region), SettingsForms.Selected(_mode), SettingsForms.Selected(_position), SettingsForms.Selected(_tier), "").Normalize(); CreateOpggSettings();
            _ready = true; await Refresh(true); LanguageChanged();
        }
        catch (Exception ex) { SetStatus(QueryStatus.InitializationFailed, ex.Message); }
        finally { _initializing = false; }
    }
    private static void Choose(ComboBox combo, string? value) { foreach (ComboBoxItem item in combo.Items) if ((string)item.Tag == value) { combo.SelectedItem = item; return; } }
    private string Mode => _query.Mode;
    private string Position => _query.Position;
    private async Task<JsonObject> Request(string path, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var response = await _backend.CallAsync("winui-backend", "publicRequest", "https://lol-api-champion.op.gg" + path).WaitAsync(cancellation);
        cancellation.ThrowIfCancellationRequested();
        return JsonNode.Parse(response.GetRawText())?.AsObject() ?? throw new InvalidOperationException("OP.GG 返回格式错误");
    }
    private Task Refresh(bool versions) => LoadData(versions, _hero, false);
    private Task<bool> SelectHero(int id) => LoadData(false, id, true);
    private async Task<bool> LoadData(bool fetchVersions, int hero, bool showHero)
    {
        if (!_ready || _shutdown || _writing || _loading || new[] { _mode, _region, _tier, _position, _version }.Any(combo => !SettingsForms.TrySelected(combo, out _))) return false;
        var target = new OpggQuery(SettingsForms.Selected(_region), SettingsForms.Selected(_mode), SettingsForms.Selected(_position), SettingsForms.Selected(_tier), SettingsForms.Selected(_version)).Normalize();
        using var cancellation = new CancellationTokenSource(); _requestCancellation = cancellation; var token = cancellation.Token;
        int generation = ++_generation; SetLoading(true); SetStatus(QueryStatus.Loading);
        try
        {
            // Retain the complete successful snapshot until every requested dataset has arrived.
            var loaded = await OpggData.LoadAsync(target, _query, _versions, _champions.Count > 0, fetchVersions, hero, path => Request(path, token), token);
            target = loaded.Query; string[] versions = loaded.Versions;
            var championResponse = loaded.Champions; var detailResponse = loaded.Detail; var augmentResponse = loaded.Augments;
            if (generation != _generation || _shutdown) return false;
            _query = target; _versions = versions; _hero = hero;
            if (championResponse is not null) _champions = championResponse["data"] as JsonArray ?? [];
            _build = detailResponse?["data"] as JsonObject ?? new(); _responseVersion = detailResponse?["meta"]?["version"]?.ToString() ?? target.Version;
            _kiwiAugments = augmentResponse?["data"] as JsonArray ?? []; _cachedAt = (detailResponse ?? championResponse)?["meta"]?["cached_at"]?.ToString() ?? _cachedAt;
            RestoreFilters(); LocalizeFilters(); _heroTab.IsEnabled = hero > 0; if (showHero && hero > 0) _tabs.SelectedItem = _heroTab;
            RenderTable(); RenderDetails();
            SetStatus(QueryStatus.Loaded);
            foreach (var (key, value) in new[] { ("mode", target.Mode), ("region", target.Region), ("position", target.Position), ("tier", target.Tier) }) _preferences[key] = value;
            _preferences["flashPosition"] = _forms.Value("opgg-renderer", "savedPreferences.flashPosition")?.DeepClone() ?? JsonValue.Create("auto");
            await _forms.Save("opgg-renderer", "savedPreferences", _preferences);
            if (generation != _generation) return true;
            if (hero > 0 && target.Mode == "aram") { await RenderAramBalance(generation, token); if (!token.IsCancellationRequested && generation == _generation) await RenderBalance(generation); }
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            if (generation == _generation) { RestoreFilters(); SetStatus(QueryStatus.QueryFailed, ex.Message); }
            return false;
        }
        finally { if (ReferenceEquals(_requestCancellation, cancellation)) _requestCancellation = null; if (generation == _generation) SetLoading(false); }
    }
    private void SetLoading(bool loading)
    {
        _loading = loading; foreach (var combo in new[] { _mode, _region, _tier, _position, _version }) combo.IsEnabled = !loading;
        _position.IsEnabled = !loading && Mode == "ranked"; _tier.IsEnabled = !loading && Mode != "arena";
        _table.IsEnabled = !loading; foreach (var button in _sessionChampions.Children.OfType<Button>()) button.IsEnabled = !loading; _heroContent.IsEnabled = !loading; _refresh.IsEnabled = !loading; _cancel.IsEnabled = loading;
    }
    private void RestoreFilters()
    {
        bool ready = _ready; _ready = false;
        Choose(_mode, _query.Mode); Choose(_region, _query.Region); Choose(_position, _query.Position); Choose(_tier, _query.Tier);
        _version.Items.Clear(); foreach (string version in _versions.Length > 0 ? _versions : [""]) _version.Items.Add(new ComboBoxItem { Content = version.Length > 0 ? version : Localization.Text("最新版本", "Latest version"), Tag = version });
        _version.SelectedIndex = 0; Choose(_version, _query.Version); _ready = ready;
    }
    private JsonNode? Stats(JsonNode? hero) => OpggData.Stats(hero, _query, true);
    private void RenderTable()
    {
        if (!_ready) return; _table.Items.Clear();
        var rows = _champions.Where(hero => Mode != "ranked" || OpggData.Position(hero, Position) is not null).Where(hero =>
        {
            int id = ID(hero, "id"); var champion = _searchChampions.FirstOrDefault(c => c.Id == id) ?? new(id, Name("champions", id), "");
            return AutoSelectData.Matches(champion, _search.Text, null, default);
        });
        JsonNode? RowStats(JsonNode? hero) => OpggData.Stats(hero, _query);
        rows = SettingsForms.Selected(_sort) switch
        {
            "win" => rows.OrderBy(h => Win(RowStats(h))), "pick" => rows.OrderBy(h => Num(RowStats(h), "pick_rate")), "ban" => rows.OrderBy(h => Num(RowStats(h), "ban_rate")),
            "name" => rows.OrderBy(h => Name("champions", ID(h, "id"))), "tier" => rows.OrderBy(h => OpggData.Tier(RowStats(h), Mode == "ranked")).ThenBy(h => OpggData.Rank(RowStats(h), Mode == "ranked")),
            _ => rows.OrderBy(h => OpggData.Rank(RowStats(h), Mode == "ranked"))
        };
        if (SettingsForms.Selected(_direction) == "desc") rows = rows.Reverse();
        int rowIndex = 0;
        foreach (var hero in rows)
        {
            int id = ID(hero, "id"); var stats = RowStats(hero); double tier = OpggData.Tier(stats, Mode == "ranked");
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Tag = id };
            row.Children.Add(new TextBlock { Text = OpggData.DisplayRank(stats, Mode == "ranked", rowIndex++).ToString("0"), Width = 32, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(Picture("champions", id, 32)); row.Children.Add(new TextBlock { Text = Name("champions", id), Width = 140, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = $"{(double.IsFinite(tier) ? tier == 0 ? "OP" : "T" + tier.ToString("0") : "-")}    {Localization.Key("opgg.champion.winRate", "胜率")} {(OpggData.HasWin(stats) ? Win(stats).ToString("P2") : "-")}    {Localization.Key("opgg.champion.pickRate", "选取率")} {(OpggData.HasNumber(stats, "pick_rate") ? Num(stats, "pick_rate").ToString("P2") : "-")}{(_tableWide ? $"    {Localization.Key("opgg.champion.banRate", "禁用率")} {(OpggData.HasNumber(stats, "ban_rate") ? Num(stats, "ban_rate").ToString("P2") : "-")}" : "")}", VerticalAlignment = VerticalAlignment.Center });
            foreach (var counter in (OpggData.Position(hero, Position)?["counters"] as JsonArray ?? []).Take(_tableMedium ? 3 : 0)) row.Children.Add(Picture("champions", ID(counter, "champion_id"), 24));
            _table.Items.Add(row);
        }
    }
    private async Task RenderAramBalance(int generation, CancellationToken cancellation = default)
    {
        if (DateTimeOffset.UtcNow - _balanceFetched > TimeSpan.FromMinutes(30))
        {
            try { var response = await Request("/api/contents/aram-balance", cancellation); var rows = response["data"]?.AsArray() ?? new(); if (rows.Count > 0) { _aramBalance = new(); foreach (var row in rows) _aramBalance[ID(row, "champion_id").ToString()] = row?.DeepClone(); _balanceFetched = DateTimeOffset.UtcNow; } }
            catch (OperationCanceledException) { return; }
            catch { if (_aramBalance.Count == 0) { try { var cached = await _backend.StateAsync("extra-assets-main", "opgg"); if (cached.Field("balance").ValueKind == JsonValueKind.Object) _aramBalance = JsonNode.Parse(cached.Field("balance").GetRawText())?.AsObject() ?? new(); } catch { } } }
        }
        if (generation != _generation) return;
        RenderDetails();
    }
    private void AppendAramBalance()
    {
        var section = Section(Localization.Text("普通大乱斗增减益 · OP.GG（海斗不适用）", "ARAM balance · OP.GG (not ARAM Mayhem)"));
        var record = _aramBalance[_hero.ToString()];
        if (record is null) section.Children.Add(new TextBlock { Text = Localization.Text("暂无该英雄的普通大乱斗数据", "No ARAM balance data for this champion") });
        else foreach (var row in OpggSessionData.Balance(JsonSerializer.SerializeToElement(record)))
            section.Children.Add(new TextBlock { Text = Localization.Key("opgg.champion.balance." + row.Key, row.Key) + " " + OpggSessionData.DeltaText(row), Foreground = new SolidColorBrush(row.Buff ? Microsoft.UI.Colors.SeaGreen : Microsoft.UI.Colors.IndianRed) });
        if (record is not null && section.Children.Count == 1) section.Children.Add(new TextBlock { Text = Localization.Text("没有调整", "No adjustments") });
        _details.Children.Add(section);
    }
    private void RenderSessionChampions(JsonElement flow, JsonElement selection)
    {
        var ids = OpggSessionData.Champions(flow, selection); string signature = string.Join(",", ids); if (signature == _sessionChampionSignature) return; _sessionChampionSignature = signature; _sessionChampions.Children.Clear();
        if (ids.Length > 0) _sessionChampions.Children.Add(new TextBlock { Text = Localization.Text("当前对局英雄", "Current game champions"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        foreach (int id in ids) { var button = new Button { Content = Picture("champions", id, 32), Padding = new Thickness(3), Margin = new Thickness(0, 0, 5, 0) }; ToolTipService.SetToolTip(button, Name("champions", id)); button.Click += async (_, _) => await SelectHero(id); _sessionChampions.Children.Add(button); }
    }
    private async Task RenderBalance(int generation)
    {
        try
        {
            var source = JsonNode.Parse((await _backend.StateAsync("extra-assets-main", "kiwi")).GetRawText())?.AsObject() ?? new();
            if (generation != _generation) return; _kiwiBalance = source; RenderDetails();
        }
        catch (Exception ex) { if (generation == _generation) SetStatus(QueryStatus.BalanceFailed, ex.Message); }
    }
    private void AppendKiwiBalance()
    {
        var source = _kiwiBalance;
        var section = Section(Localization.Text("海克斯大乱斗专属增减益（普通大乱斗不适用）", "ARAM Mayhem balance (not ARAM)") + " · RESG " + source["version"] + (source["cached"]?.GetValue<bool>() == true ? " · " + Localization.Text("离线缓存", "offline cache") : ""));
        var record = source["balance"]?[_hero.ToString()];
        if (record is null) section.Children.Add(new TextBlock { Text = Localization.Text("暂无该英雄的海斗独立数据", "No separate ARAM Mayhem data for this champion") });
        else if (record["adjustments"] is not JsonArray { Count: > 0 }) section.Children.Add(new TextBlock { Text = Localization.Text("数据源未列出该英雄调整", "The source lists no adjustments for this champion") });
        else foreach (var adjustment in record["adjustments"]!.AsArray())
        {
            string type = adjustment?["type"]?.ToString() ?? "", labelKey = type switch { "damage-dealt" => "damage_dealt", "damage-taken" => "damage_taken", "healing" => "healing", "shielding" => "shield_amount", "ability-haste" => "cooldown_reduction", "attack-speed" => "attack_speed", "tenacity" => "tenacity", "energy-regen" => "energy_regen", _ => type };
            string label = type == "ability-haste" ? Localization.Text("技能急速", "Ability haste") : Localization.Key("opgg.champion.balance." + labelKey, type);
            double value = Num(adjustment, "value"); string text = adjustment?["display"]?.ToString() == "percentage" ? $"{(value - 1) * 100:+0.##;-0.##;0}%" : adjustment?["formattedValue"]?.ToString() ?? value.ToString();
            bool buff = adjustment?["effect"]?.ToString() == "buffed";
            var control = new TextBlock { Text = label + "  " + text, Foreground = new SolidColorBrush(buff ? Microsoft.UI.Colors.SeaGreen : Microsoft.UI.Colors.IndianRed) }; ToolTipService.SetToolTip(control, Localization.Text("来源原值 ", "Source value ") + adjustment?["formattedValue"]); section.Children.Add(control);
        }
        _details.Children.Add(section);
    }
    private void RenderBuild()
    {
        _details.Children.Clear(); if (_hero <= 0 || _build.Count == 0) return;
        var stats = Stats(_build["summary"]); var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        heading.Children.Add(Picture("champions", _hero, 56));
        double tier = OpggData.Tier(stats, Mode == "ranked"); heading.Children.Add(new TextBlock { Text = double.IsFinite(tier) ? tier == 0 ? "OP" : "T" + tier.ToString("0") : "-", FontSize = 24, VerticalAlignment = VerticalAlignment.Center }); heading.Children.Add(new TextBlock { Text = SummaryRate(stats), TextWrapping = TextWrapping.Wrap });
        _details.Children.Add(Section(Name("champions", _hero), heading));
        AddCounters(); AddSynergies();
        AddBuildRows(Localization.Key("opgg.champion.spells", "召唤师技能"), "summoner_spells", "summonerSpells", row => ApplySpells(row), 2);
        var runeRows = new List<UIElement>();
        foreach (var raw in _build["runes"] as JsonArray ?? [])
        {
            var row = raw!.AsObject(); var content = new StackPanel { Spacing = 6 };
            foreach (var (style, field, shards) in new[] { ("primary_page_id", "primary_rune_ids", false), ("secondary_page_id", "secondary_rune_ids", true) })
            {
                var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                icons.Children.Add(Picture("perkstyles", ID(row, style), 24)); foreach (int id in Array(row[field])) icons.Children.Add(Picture("perks", id, 24));
                if (shards) foreach (int id in Array(row["stat_mod_ids"])) icons.Children.Add(Picture("perks", id, 24)); content.Children.Add(icons);
            }
            content.Children.Add(new TextBlock { Text = Rate(row) }); var apply = new Button { Content = Localization.Text("应用方案", "Apply build") }; apply.Click += async (_, _) => await Write(() => ApplyRunes(row)); content.Children.Add(apply); runeRows.Add(content);
        }
        if (runeRows.Count > 0) _details.Children.Add(Expandable(Localization.Key("opgg.champion.runes", "符文"), runeRows, 2, "runes"));
        var skillRows = new List<UIElement>();
        foreach (var raw in _build["skill_masteries"] as JsonArray ?? [])
        {
            var row = new StackPanel { Spacing = 5 }; row.Children.Add(new TextBlock { Text = string.Join(" → ", raw?["ids"]?.AsArray().Select(x => x!.ToString()) ?? []) + "    " + Rate(raw), TextWrapping = TextWrapping.Wrap });
            var first = (raw?["builds"] as JsonArray)?.FirstOrDefault(); row.Children.Add(new TextBlock { Text = string.Join(" ", first?["order"]?.AsArray().Select(x => x!.ToString()) ?? []), TextWrapping = TextWrapping.Wrap }); skillRows.Add(row);
        }
        if (skillRows.Count > 0) _details.Children.Add(Expandable(Localization.Key("opgg.champion.abilityBuild", "技能加点"), skillRows, 2, "skills"));
        foreach (var (key, label, limit) in new[] { ("starter_items", "starterItemText", 4), ("boots", "boots", 4), ("prism_items", "prismItemText", 4), ("core_items", "coreItemText", 4), ("last_items", "itemText", 8) }) AddBuildRows(Localization.Key("opgg.champion." + label, label), key, "items", limit: limit);
        if (OpggData.HasItems(_build)) { var import = new Button { Content = Localization.Key("opgg.champion.applyRunes", "导入完整装备方案") }; import.Click += async (_, _) => await Write(ApplyItems); _details.Children.Add(import); }
        if (_build["augment_group"] is JsonArray { Count: > 0 } groups)
        {
            var tabs = new TabView { IsAddTabButtonVisible = false };
            foreach (var group in groups)
            {
                int rarity = ID(group, "rarity"); if (rarity is not (1 or 4 or 8)) continue;
                var rows = new List<UIElement>(); foreach (var raw in group?["augments"] as JsonArray ?? []) { var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; int id = ID(raw, "id"); row.Children.Add(Picture("augments", id, 24)); row.Children.Add(new TextBlock { Text = Name("augments", id) + "    " + Rate(raw) }); rows.Add(row); }
                string label = Localization.Key("opgg.champion." + (rarity == 1 ? "augmentSilver" : rarity == 4 ? "augmentGold" : "augmentPrism"));
                tabs.TabItems.Add(new TabViewItem { Header = label, Tag = rarity.ToString(), IsClosable = false, Content = Expandable(label, rows, 4, "arena") });
            }
            if (tabs.TabItems.Count > 0) { tabs.SelectedIndex = 0; foreach (TabViewItem tab in tabs.TabItems) if ((string)tab.Tag == _arenaTab) tabs.SelectedItem = tab; tabs.SelectionChanged += (_, _) => { _arenaTab = (tabs.SelectedItem as TabViewItem)?.Tag?.ToString() ?? ""; SyncExpandedWidgets("arena"); }; _details.Children.Add(tabs); }
        }
    }
    private void AddCounters()
    {
        var position = OpggData.Position(_build["summary"], Position);
        if (position?["counters"] is not JsonArray { Count: > 0 } counters) return;
        var section = Section(Localization.Key("opgg.champion.counter", "克制英雄")); var toggle = new ToggleSwitch { Header = Localization.Key("opgg.champion.allCounters", "全部对线"), IsOn = _allCounters, OffContent = Localization.Key("opgg.champion.counterC", "克制"), OnContent = Localization.Key("opgg.champion.allC", "全部") }; section.Children.Add(toggle);
        var list = new WrapPanel(); section.Children.Add(list);
        void Render()
        {
            list.Children.Clear(); IEnumerable<JsonNode?> rows = toggle.IsOn ? (_build["counters"] as JsonArray ?? []).OrderByDescending(Win) : counters;
            foreach (var entry in rows)
            {
                int id = ID(entry, "champion_id"); var content = new StackPanel { Spacing = 4 }; content.Children.Add(Picture("champions", id, 32)); content.Children.Add(new TextBlock { Text = $"{Win(entry):P2}", Foreground = new SolidColorBrush(Win(entry) > .5 && toggle.IsOn ? Microsoft.UI.Colors.DodgerBlue : Microsoft.UI.Colors.IndianRed) }); content.Children.Add(new TextBlock { Text = $"{Num(entry, "play"):N0}", FontSize = 10 });
                var button = new Button { Content = content, Margin = new Thickness(0, 0, 5, 5) }; ToolTipService.SetToolTip(button, Name("champions", id)); button.Click += async (_, _) => await SelectHero(id); list.Children.Add(button);
            }
        }
        toggle.Toggled += (_, _) => { _allCounters = toggle.IsOn; Render(); }; Render(); _details.Children.Add(section);
    }
    private void AddSynergies()
    {
        if (_build["synergies"] is not JsonArray { Count: > 0 } synergies) return;
        var rows = new List<UIElement>(); foreach (var entry in synergies) { int id = ID(entry, "champion_id"); var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 }; row.Children.Add(Picture("champions", id, 28)); var link = new Button { Content = Name("champions", id) + "    " + Rate(entry) }; link.Click += async (_, _) => await SelectHero(id); row.Children.Add(link); rows.Add(row); }
        _details.Children.Add(Expandable(Localization.Key("opgg.champion.synergies", "协同英雄"), rows, 4, "synergies"));
    }
    private void AddBuildRows(string label, string field, string catalog, Func<JsonObject, Task>? apply = null, int limit = 4)
    {
        var rows = new List<UIElement>(); foreach (var raw in _build[field] as JsonArray ?? []) { var data = raw!.AsObject(); var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; foreach (int id in Array(raw["ids"])) row.Children.Add(Picture(catalog, id, 30)); row.Children.Add(new TextBlock { Text = Rate(raw), VerticalAlignment = VerticalAlignment.Center }); if (apply is not null) { var button = new Button { Content = Localization.Text("应用方案", "Apply build") }; button.Click += async (_, _) => await Write(() => apply(data)); row.Children.Add(button); } rows.Add(row); }
        if (rows.Count > 0) _details.Children.Add(Expandable(label, rows, limit, field));
    }
    private Image Picture(string catalog, int id, int size)
    {
        var picture = new Image { Width = size, Height = size, Stretch = Stretch.UniformToFill };
        var item = catalog == "perkstyles" ? _catalog[catalog]?["styles"]?[id.ToString()] : _catalog[catalog]?[id.ToString()]; string path = item?["squarePortraitPath"]?.ToString() ?? item?["iconPath"]?.ToString() ?? "";
        if (catalog == "champions" && path == "") path = $"/lol-game-data/assets/v1/champion-icons/{id}.png";
        string description = item?["longDesc"]?.ToString() ?? item?["description"]?.ToString() ?? item?["shortDesc"]?.ToString() ?? "";
        ToolTipService.SetToolTip(picture, new TextBlock { Text = Name(catalog, id) + (description.Length > 0 ? "\n" + System.Text.RegularExpressions.Regex.Replace(description, "<[^>]+>", "") : ""), TextWrapping = TextWrapping.Wrap, MaxWidth = 320 });
        picture.Loaded += async (_, _) => { try { byte[]? bytes = await _backend.ImageAsync(path); if (bytes is null) return; using var stream = new InMemoryRandomAccessStream(); using var writer = new DataWriter(stream); writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); stream.Seek(0); var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); picture.Source = bitmap; } catch { } }; return picture;
    }
    private string Name(string catalog, int id) => (catalog == "perkstyles" ? _catalog[catalog]?["styles"]?[id.ToString()] : _catalog[catalog]?[id.ToString()])?["name"]?.ToString() ?? id.ToString();
    private static double Num(JsonNode? obj, string key) => double.TryParse(obj?[key]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number) ? number : 0;
    private static int ID(JsonNode? obj, string key) => (int)Num(obj, key);
    private static IEnumerable<int> Array(JsonNode? data) => data is JsonArray values ? values.Select(x => x!.GetValue<int>()) : [];
    private static double Win(JsonNode? row) => OpggData.Win(row);
    private static string Rate(JsonNode? row)
    {
        var parts = new List<string>();
        if (OpggData.HasWin(row)) parts.Add($"{Localization.Key("opgg.champion.winRate", "胜率")} {Win(row):P2}");
        if (OpggData.HasNumber(row, "pick_rate")) parts.Add($"{Localization.Key("opgg.champion.pickRate", "选取率")} {Num(row, "pick_rate"):P2}");
        if (OpggData.HasNumber(row, "play")) parts.Add($"{Num(row, "play"):N0} {Localization.Text("场", "games")}");
        if (Num(row, "total_place") > 0 && Num(row, "play") > 0) parts.Add($"{Localization.Key("opgg.champion.avgPlace", "均名次")} {Num(row, "total_place") / Num(row, "play"):F2}");
        if (Num(row, "first_place") > 0 && Num(row, "play") > 0) parts.Add($"{Localization.Key("opgg.champion.1st", "第一")} {Num(row, "first_place") / Num(row, "play"):P2}");
        return string.Join(" · ", parts);
    }
    private static string SummaryRate(JsonNode? stats)
    {
        var parts = new List<string>();
        if (Num(stats, "total_place") > 0 && Num(stats, "play") > 0) parts.Add($"{Localization.Key("opgg.champion.avgPlace", "均名次")} {Num(stats, "total_place") / Num(stats, "play"):F2}");
        if (Num(stats, "first_place") > 0 && Num(stats, "play") > 0) parts.Add($"{Localization.Key("opgg.champion.1st", "第一")} {Num(stats, "first_place") / Num(stats, "play"):P2}");
        foreach (var (field, label) in new[] { ("win_rate", "winRate"), ("pick_rate", "pickRate"), ("ban_rate", "banRate") }) if (Num(stats, field) != 0) parts.Add($"{Localization.Key("opgg.champion." + label)} {Num(stats, field):P2}");
        return string.Join(" · ", parts);
    }
    private Task<JsonElement> Lcu(string method, string path, object? data = null) => _backend.CallAsync("winui-backend", "lcuRequest", method, path, data);
    private async Task Write(Func<Task> operation)
    {
        if (_writing || _loading || !_visible) return; _writing = true; _heroContent.IsEnabled = false;
        try { await operation(); SetStatus(QueryStatus.Applied); }
        catch (Exception ex) { SetStatus(QueryStatus.ApplyFailed, ex.Message); }
        finally { _writing = false; _heroContent.IsEnabled = !_loading; }
    }
    private async Task ApplySpells(JsonObject row)
    {
        int hero = _hero; var ids = Array(row["ids"]).ToArray(); if (ids.Length != 2) throw new InvalidOperationException("技能方案不完整");
        await GuardChampion(hero);
        var old = await Lcu("GET", "/lol-champ-select/v1/session/my-selection"); string flash = _forms.Value("opgg-renderer", "savedPreferences.flashPosition")?.ToString() ?? "auto";
        if (ids.Contains(4) && flash != "auto") { if ((flash == "d" && ids[1] == 4) || (flash == "f" && ids[0] == 4)) (ids[0], ids[1]) = (ids[1], ids[0]); }
        else if (old.TryGetProperty("spell2Id", out var spell2) && ids[0] == spell2.GetInt32() || old.TryGetProperty("spell1Id", out var spell1) && ids[1] == spell1.GetInt32()) (ids[0], ids[1]) = (ids[1], ids[0]);
        await GuardChampion(hero); await Lcu("PATCH", "/lol-champ-select/v1/session/my-selection", new { spell1Id = ids[0], spell2Id = ids[1] });
        await Announce(Localization.Key("opgg.view.spellsSet", "[LeagueAkari] 召唤师技能已设置：[OP.GG] {{spell1}} | {{spell2}}", new Dictionary<string, object?> { ["spell1"] = Name("summonerSpells", ids[0]), ["spell2"] = Name("summonerSpells", ids[1]) }));
    }
    private async Task ApplyRunes(JsonObject row)
    {
        int hero = _hero; string pageName = RunePageName();
        await GuardChampion(hero);
        var inventory = await Lcu("GET", "/lol-perks/v1/inventory"); int page; bool created = false;
        if (inventory.TryGetProperty("canAddCustomPage", out var can) && can.GetBoolean()) { var added = await Lcu("POST", "/lol-perks/v1/pages", new { name = pageName, isEditable = true, primaryStyleId = ID(row, "primary_page_id").ToString() }); page = added.GetProperty("id").GetInt32(); created = true; }
        else { var pages = await Lcu("GET", "/lol-perks/v1/pages"); if (pages.GetArrayLength() == 0) throw new InvalidOperationException("客户端没有可替换的符文页"); page = pages[0].GetProperty("id").GetInt32(); }
        await GuardChampion(hero); await Lcu("PUT", "/lol-perks/v1/pages/" + page, new { id = page, isRecommendationOverride = false, isTemporary = false, name = pageName, primaryStyleId = ID(row, "primary_page_id"), subStyleId = ID(row, "secondary_page_id"), selectedPerkIds = Array(row["primary_rune_ids"]).Concat(Array(row["secondary_rune_ids"])).Concat(Array(row["stat_mod_ids"])).ToArray() });
        await GuardChampion(hero); await Lcu("PUT", "/lol-perks/v1/currentpage", page);
        await Announce(Localization.Key("opgg.view.runesSet", "[LeagueAkari] {{action}}符文方案：{{name}}", new Dictionary<string, object?> { ["name"] = pageName, ["action"] = Localization.Key("opgg.view." + (created ? "create" : "replace")) }));
    }
    private string RunePageName() => "[OP.GG] " + Name("champions", _hero) + (Position == "none" ? "" : " - " + Localization.Key("opgg.filters.positions." + Position, Position));
    private async Task ApplyItems()
    {
        if (!OpggData.HasItems(_build)) throw new InvalidOperationException(Localization.Key("opgg.champion.empty", "没有装备方案"));
        int hero = _hero; var query = _query; string name = Name("champions", hero);
        var blocks = OpggData.ItemGroups(_build).Select(group => new { type = ItemGroupTitle(group), items = group.Items.Select(id => new { id = id.ToString(), count = 1 }).ToArray() }).ToArray();
        string title = "[OP.GG] " + name + " - " + Localization.Key("opgg.filters.modes." + query.Mode, query.Mode) + (query.Position == "none" ? "" : " - " + Localization.Key("opgg.filters.positions." + query.Position, query.Position));
        object itemSet = new { uid = OpggData.ItemUid(hero, query, _responseVersion), title, sortrank = 0, type = "global", map = "any", mode = "any", blocks, associatedChampions = System.Array.Empty<int>(), associatedMaps = System.Array.Empty<int>(), preferredItemSlots = System.Array.Empty<object>() };
        await _backend.CallAsync("league-client-main", "writeItemSetsToDisk", (object)new[] { itemSet }); _itemsImported = true;
        await Announce(Localization.Key("opgg.champion.writeToDisk", "已导入 OP.GG 装备方案：{{name}}", new Dictionary<string, object?> { ["name"] = "[OP.GG] " + name }));
    }
    private static string ItemGroupTitle(OpggItemGroup group)
    {
        string key = group.Field switch { "starter_items" => "starterItem", "core_items" => "coreItem", "boots" => "bootsDesc", "prism_items" => "prismItemsDesc", _ => "itemsDesc" };
        return Localization.Key("opgg.champion." + key, group.Field, new Dictionary<string, object?> { ["index"] = group.Index, ["pickRate"] = (group.PickRate * 100).ToString("F2") });
    }
    private async Task GuardChampion(int? expected = null)
    {
        var phase = await Lcu("GET", "/lol-gameflow/v1/gameflow-phase"); if (phase.GetString() != "ChampSelect") throw new InvalidOperationException("请在英雄选择中应用配置");
        var session = await Lcu("GET", "/lol-champ-select/v1/session"); if (!_visible || _shutdown || OpggData.ActiveChampion(JsonNode.Parse(session.GetRawText())) != (expected ?? _hero)) throw new InvalidOperationException("客户端当前英雄已切换，请重新选择方案");
    }
    private async Task Announce(string message)
    {
        try { var chat = await _backend.StateAsync("league-client-main", "chat"); var select = chat.GetProperty("conversations").GetProperty("championSelect"); if (select.ValueKind != JsonValueKind.Object) return; await Lcu("POST", "/lol-chat/v1/conversations/" + select.GetProperty("id").GetString() + "/messages", new { body = message, type = "celebration" }); } catch { }
    }
    private async Task FollowClient()
    {
        if (!_ready || !_visible || _following || _loading || _writing) return; _following = true;
        try
        {
            var selection = await _backend.StateAsync("league-client-main", "champSelect");
            var flow = await _backend.StateAsync("league-client-main", "gameflow");
            RenderSessionChampions(flow, selection);
            if (!flow.TryGetProperty("phase", out var phase) || phase.GetString() != "ChampSelect") { _followSignature = ""; return; }
            if (!selection.TryGetProperty("session", out var session) || session.ValueKind != JsonValueKind.Object) return;
            int cell = session.GetProperty("localPlayerCellId").GetInt32(); var self = session.GetProperty("myTeam").EnumerateArray().FirstOrDefault(x => x.GetProperty("cellId").GetInt32() == cell);
            int hero = OpggData.ActiveChampion(JsonNode.Parse(session.GetRawText()));
            if (hero <= 0 || selection.Field("disabledChampionIds").Items().Any(id => id.TryNumber() == hero)) return;
            var queue = flow.GetProperty("session").GetProperty("gameData").GetProperty("queue"); _sessionMode = queue.GetProperty("gameMode").GetString() ?? ""; string queueType = queue.GetProperty("type").GetString() ?? "";
            string newMode = _sessionMode switch { "CLASSIC" => "ranked", "ARAM" or "KIWI" => "aram", "CHERRY" => "arena", "NEXUSBLITZ" => "nexus_blitz", "URF" or "ARURF" => "urf", _ => "" }; if (newMode == "") return;
            string assigned = self.TryGetProperty("assignedPosition", out var assignedValue) ? (assignedValue.GetString() ?? "default").ToLowerInvariant() : "default";
            await _forms.Load("auto-champ-config-main"); var configuration = _forms.Values("auto-champ-config-main");
            string followSignature = hero + ":" + _sessionMode + ":" + queueType + ":" + assigned + ":" + Enabled("autoApplyRunes") + Enabled("autoApplySpells") + Enabled("autoApplyItems") + ":" + configuration.ToJsonString(); if (followSignature == _followSignature) return;
            _sessionConfigKeys = OpggSessionData.ConfigKeys(_sessionMode, queueType, assigned);
            bool changedMode = Mode != newMode; _ready = false; Choose(_mode, newMode); if (assigned != "default") Choose(_position, assigned switch { "middle" => "mid", "bottom" => "adc", "utility" => "support", _ => assigned }); _ready = true;
            if (!await LoadData(changedMode, hero, true)) return; _followSignature = followSignature;
            var current = await _backend.StateAsync("league-client-main", "champSelect"); if (!current.TryGetProperty("session", out var currentSession) || currentSession.ValueKind != JsonValueKind.Object || OpggData.ActiveChampion(JsonNode.Parse(currentSession.GetRawText())) != hero) return;
            if (OpggSessionData.CanAutoApplyRunesAndSpells(_sessionMode) && Enabled("autoApplyRunes") && !HasConfig(configuration["runesV2"], hero) && _build["runes"] is JsonArray { Count: > 0 } runes) await Write(() => ApplyRunes(runes[0]!.AsObject()));
            if (OpggSessionData.CanAutoApplyRunesAndSpells(_sessionMode) && Enabled("autoApplySpells") && !HasConfig(configuration["summonerSpells"], hero) && _build["summoner_spells"] is JsonArray { Count: > 0 } spells) await Write(() => ApplySpells(spells[0]!.AsObject()));
            if (Enabled("autoApplyItems") && OpggData.HasItems(_build)) await Write(ApplyItems);
        }
        catch (Exception ex) { SetStatus(QueryStatus.RawError, ex.Message); }
        finally { _following = false; }
    }
    private bool Enabled(string key) => _forms.Value("opgg-renderer", key)?.GetValue<bool>() == true;
    private bool HasConfig(JsonNode? configs, int hero) => configs?[hero.ToString()] is JsonObject entries && _sessionConfigKeys.Any(key => entries[key] is not null);
}


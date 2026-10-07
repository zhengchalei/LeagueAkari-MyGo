using System.Text.Json;
using System.Text.Json.Nodes;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class SettingsPage : UserControl
{
    private readonly BackendClient _backend;
    private readonly SettingsForms _forms;
    private readonly TabView _tabs = new() { IsAddTabButtonVisible = false };
    private bool _loaded;
    private readonly Dictionary<string, bool> _debugRules = new();
    private readonly TextBox _eventLog = new() { Header = "匹配的 LCU 事件", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 220 };
    private bool _debugSubscribed;
    private Func<Task>? _refreshTags;
    private readonly DispatcherTimer _debugTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Func<Task>? _refreshDebug;
    private bool _refreshingDebug;
    private readonly Func<Task>? _showRelease;
    public SettingsPage(BackendClient backend, Func<Task>? showRelease = null)
    {
        _backend = backend; _forms = new(backend); _showRelease = showRelease;
        _debugTimer.Tick += async (_, _) => { if (_refreshDebug is not null && _tabs.SelectedIndex == 6) await _refreshDebug(); };
        var layout = new Grid(); layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.Children.Add(_tabs); Grid.SetRow(_forms.Status, 1); layout.Children.Add(_forms.Status); Content = layout;
        Loaded += (_, _) => { _debugTimer.Start(); _forms.Observe(); Localization.Changed += LanguageChanged; if (NativeAppearance.Current is { } appearance) appearance.Changed += AppearanceChanged; if (!_debugSubscribed) { _backend.EventReceived += DebugEvent; _debugSubscribed = true; } LanguageChanged(); };
        Unloaded += (_, _) => { _debugTimer.Stop(); _forms.Release(); Localization.Changed -= LanguageChanged; if (NativeAppearance.Current is { } appearance) appearance.Changed -= AppearanceChanged; _backend.EventReceived -= DebugEvent; _debugSubscribed = false; };
        Loaded += async (_, _) =>
        {
            if (_loaded) { try { await _forms.Reload(); } catch (Exception ex) { _forms.Status.Text = ex.Message; } return; }
            try
            {
                await _forms.Load("app-common-main", "window-manager-main", "window-manager-main/main-window", "remote-config-main", "self-update-main", "logger-factory-main", "player-tabs-renderer", "main-window-ui-renderer", "ongoing-game-main", "respawn-timer-main", "league-client-ux-main", "league-client-main", "renderer-debug-renderer");
                Add("应用", Application()); Add("战绩", await History()); Add("对局", Ongoing()); Add("多窗口", await Windows()); Add("存储", Storage()); Add("其他", Misc()); Add("调试", await Debug()); Add("关于", await About()); _loaded = true; LanguageChanged();
            }
            catch (Exception ex) { _forms.Status.Text = "设置加载失败：" + ex.Message; }
        };
    }
    private void LanguageChanged() => DispatcherQueue.TryEnqueue(() => NativeFormText.Apply(this));
    private void AppearanceChanged() => DispatcherQueue.TryEnqueue(async () => { if (_refreshTags is not null) await _refreshTags(); LanguageChanged(); });
    private void Add(string title, UIElement panel) { _tabs.TabItems.Add(new TabViewItem { Header = title, IsClosable = false, Content = SettingsForms.Scroll(panel) }); if (_tabs.SelectedIndex < 0) _tabs.SelectedIndex = 0; }
    private UIElement Application()
    {
        const string ns = "app-common-main";
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(SettingsForms.Section("基本设置", _forms.Choice("window-manager-main/main-window", "closeAction", "关闭主窗口", ("ask", "每次询问"), ("minimize-to-tray", "最小化到托盘"), ("quit", "退出")), _forms.Choice(ns, "locale", "语言 / Language", ("zh-CN", "简体中文"), ("en", "English")), _forms.Choice(ns, "preferredLolSource", "首选战绩来源", ("sgp", "SGP（支持大区与队列查询）"), ("lcu", "LCU（本地客户端）")), _forms.Choice(ns, "theme", "主题", ("default", "跟随系统"), ("light", "浅色"), ("sakura", "樱花"), ("butter", "奶油"), ("mint", "薄荷"), ("dark", "深色"), ("graphite", "石墨"), ("cyber", "赛博"), ("aurora", "极光")), BackgroundChoice(), _forms.Number("window-manager-main/main-window", "opacity", "主窗口透明度", .2, 1, 1, .05), _forms.Toggle("window-manager-main/main-window", "pinned", "主窗口置顶"), _forms.Toggle(ns, "showFreeSoftwareDeclaration", "显示免费软件声明")));
        panel.Children.Add(UpdateSettings());
        panel.Children.Add(ConnectionSettings());
        var proxy = _forms.Choice(ns, "httpProxy.strategy", "代理策略", ("disable", "禁用"), ("force", "强制代理"));
        var host = _forms.Text(ns, "httpProxy.host", "代理主机", "127.0.0.1"); var port = _forms.Number(ns, "httpProxy.port", "代理端口", 1, 65535, 7890);
        void ApplyProxy() { if (SettingsForms.TrySelected(proxy, out string strategy)) host.IsEnabled = port.IsEnabled = strategy == "force"; } proxy.SelectionChanged += (_, _) => ApplyProxy(); ApplyProxy();
        panel.Children.Add(SettingsForms.Section("日志与网络", _forms.Choice("logger-factory-main", "logLevel", "日志等级", ("debug", "调试"), ("info", "信息"), ("warn", "警告"), ("error", "错误"), ("silent", "关闭")), proxy, host, port));
        panel.Children.Add(SettingsForms.Section("应用管理", _forms.Action("打开数据目录", ns, "openUserDataDir"), ConfirmAction("卸载应用", "删除本机应用及配置，无法撤销。是否继续？", "self-update-main", "uninstallApp")));
        return panel;
    }
    private async Task<UIElement> History()
    {
        const string ns = "player-tabs-renderer";
        JsonElement queues = default;
        int[] supported = [420, 440, 430, 450, 2400];
        try
        {
            var sgp = await _backend.StateAsync("sgp-main");
            var ids = sgp.Field("supportedQueues").Items().Where(id => id.ValueKind == JsonValueKind.Number).Select(id => id.GetInt32()).Distinct().ToArray();
            if (ids.Length > 0) supported = ids;
            queues = (await _backend.StateAsync("league-client-main", "gameData")).Field("queues");
        }
        catch (Exception ex) { _forms.Status.Text = ex.Message; }
        var options = new List<(string Value, string Label)> { ("<akari:all>", "所有模式"), ("ranked", "排位"), ("normal", "匹配") };
        foreach (int id in supported)
        {
            string fallback = id switch { 420 => "单双排", 440 => "灵活排位", 430 => "匹配", 450 => "大乱斗", 2400 => "海斗", _ => id.ToString() };
            options.Add(("q_" + id, queues.Field(id.ToString()).Text("name", fallback)));
        }
        string? selected = _forms.Value(ns, "defaultMatchHistoryTag")?.ToString();
        if (selected is not null && selected.StartsWith("q_") && int.TryParse(selected[2..], out int selectedID) && !options.Any(option => option.Value == selected)) options.Add((selected, queues.Field(selectedID.ToString()).Text("name", selectedID.ToString())));
        return SettingsForms.Section("战绩默认设置", _forms.Toggle(ns, "refreshTabsAfterGameEnds", "对局结束后刷新战绩", true), _forms.Toggle(ns, "matchHistoryUseSgpApi", "战绩优先使用 SGP", true), _forms.NumericChoice(ns, "loadCount", "每页战绩数量", 10, 20, 30, 40, 50, 100, 200), _forms.Choice(ns, "defaultMatchHistoryTag", "默认队列", options.ToArray()), _forms.Choice(ns, "defaultMatchHistoryTimeRange", "默认时间范围", ("all", "全部"), ("24h", "24 小时"), ("3d", "3 天"), ("7d", "7 天"), ("30d", "30 天")), _forms.Toggle(ns, "defaultShowPractice", "显示训练模式"), _forms.Toggle(ns, "defaultShowIrregularGames", "显示重开等非正常对局"));
    }
    private UIElement Ongoing()
    {
        const string ns = "ongoing-game-main";
        var panel = new StackPanel { Spacing = 12 };
        double historyCount = Math.Clamp(_forms.Value(ns, "matchHistoryLoadCount")?.GetValue<double>() ?? 50, 2, 200);
        var details = _forms.Number(ns, "gameDetailsLoadCount", "预加载详细对局数量", 0, historyCount, 20);
        _forms.ObserveValue(details, ns, "matchHistoryLoadCount", value => details.Maximum = Math.Clamp(value?.GetValue<double>() ?? 50, 2, 200));
        var tagPreference = _forms.Choice(ns, "matchHistoryTagPreference", "查询模式", ("current", "当前模式"), ("all", "所有模式"));
        _forms.ObserveValue(tagPreference, "app-common-main", "preferredLolSource", value => tagPreference.IsEnabled = value?.ToString() != "lcu");
        tagPreference.IsEnabled = _forms.Value("app-common-main", "preferredLolSource")?.ToString() != "lcu";
        panel.Children.Add(SettingsForms.Section("查询", _forms.Toggle(ns, "enabled", "启用对局查询", true), _forms.Toggle(ns, "autoRouteWhenGameStarts", "进入对局时切换页面", true), _forms.Number(ns, "matchHistoryLoadCount", "每位玩家查询数量", 2, 200, 50, 5), _forms.Number(ns, "concurrency", "查询并发数", 1, double.MaxValue, 4), details, tagPreference, _forms.Toggle(ns, "queryInLobbyPhase", "大厅阶段查询", true), _forms.Number(ns, "premadeTeamInferMatchCountThreshold", "组队推断共同对局阈值", 2, double.MaxValue, 5)));
        panel.Children.Add(SettingsForms.Section("玩家卡片", _forms.Choice(ns, "orderPlayerBy", "排序", ("default", "默认"), ("akari-score", "Akari Score"), ("win-rate", "胜率"), ("kda", "KDA"), ("position", "位置"), ("premade-team", "组队")), _forms.Choice(ns, "showChampionUsage", "英雄使用列表", ("none", "不显示"), ("recent", "最近使用"), ("mastery", "英雄点数")), _forms.Toggle(ns, "showMatchHistoryItemBorder", "显示战绩边框"), _forms.Toggle(ns, "showJunglePathing", "显示打野路径", true), _forms.Toggle(ns, "showJunglePathingForAllPlayers", "所有玩家显示打野路径")));
        var tags = SettingsForms.Section("玩家标签");
        foreach (var (key, label) in new (string, string)[] { ("showPremadeTeamTag", "组队"), ("showSuspiciousFlashPositionTag", "可疑闪现位置"), ("showWinningStreakTag", "连胜"), ("showLosingStreakTag", "连败"), ("showSoloKillsTag", "单杀"), ("showEasyGankTag", "容易被抓"), ("showGreatPerformanceTag", "亮眼表现"), ("showAverageTeamDamageTag", "平均伤害"), ("showAverageTeamDamageTakenTag", "平均承伤"), ("showAverageTeamGoldTag", "平均经济"), ("showAverageCsPerMinuteTag", "补刀每分钟"), ("showAverageDamageGoldEfficiencyTag", "经济伤害效率"), ("showAverageEnemyMissingPingsTag", "问号次数"), ("showAverageVisionScoreTag", "视野得分"), ("showAverageKillDamageEfficiencyTag", "击杀伤害效率"), ("showSelfTag", "自己"), ("showMetTag", "遇见过"), ("showTaggedTag", "已标记"), ("showWinRateTeamTag", "队伍胜率"), ("showPrivacyTag", "隐私"), ("showAkariScoreTag", "Akari Score") }) tags.Children.Add(_forms.Toggle(ns, "playerCardTags." + key, label));
        panel.Children.Add(tags); return panel;
    }
    private async Task<UIElement> Windows()
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var (id, label) in new[] { ("aux-window", "Mini"), ("opgg-window", "OP.GG"), ("ongoing-game-window", "对局悬浮窗口"), ("cd-timer-window", "冷却计时") })
        {
            string ns = "window-manager-main/" + id; await _forms.Load(ns);
            var section = SettingsForms.Section(label, _forms.Toggle(ns, "enabled", "启用"));
            if (id is "aux-window" or "opgg-window") section.Children.Add(_forms.Toggle(ns, "pinned", "置顶"));
            section.Children.Add(_forms.Number(ns, "opacity", "透明度", .2, 1, 1, .05));
            section.Children.Add(_forms.Action("显示 / 隐藏", ns, "toggle")); section.Children.Add(_forms.Action("重置位置", ns, "resetPosition"));
            if (id is "aux-window" or "opgg-window") section.Children.Add(_forms.Toggle(ns, "autoShow", "选人时自动显示"));
            if (id == "aux-window") section.Children.Add(_forms.Toggle(ns, "showSkinSelector", "显示已有皮肤列表", true));
            if (id != "aux-window") section.Children.Add(Shortcut(ns, "showShortcut", "显示快捷键"));
            if (id == "cd-timer-window") { section.Children.Add(_forms.Choice(ns, "timerType", "计时方式", ("countdown", "倒计时"), ("countup", "正计时"))); section.Children.Add(_forms.Toggle(ns, "reverseAdjustmentDirection", "滚轮调整方向反转")); section.Children.Add(new TextBlock { Text = "左击开始或清除计时，右键双击将时间发送到游戏，滚轮调整秒数。", TextWrapping = TextWrapping.Wrap }); }
            panel.Children.Add(section);
        }
        return panel;
    }
    private UIElement Shortcut(string ns, string key, string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = _forms.Value(ns, key)?.ToString() ?? "未设置" };
        _forms.ObserveValue(button, ns, key, value => button.Content = value?.ToString() ?? NativeFormText.Text("未设置"));
        bool recording = false; button.Click += (_, _) => { recording = true; button.Content = "按快捷键（Esc 取消）"; button.Focus(FocusState.Programmatic); };
        button.KeyDown += async (_, e) =>
        {
            if (!recording) return; e.Handled = true;
            if (e.Key == global::Windows.System.VirtualKey.Escape) { recording = false; button.Content = _forms.Value(ns, key)?.ToString() ?? NativeFormText.Text("未设置"); return; }
            if (e.Key is global::Windows.System.VirtualKey.Control or global::Windows.System.VirtualKey.Shift or global::Windows.System.VirtualKey.Menu or global::Windows.System.VirtualKey.LeftWindows or global::Windows.System.VirtualKey.RightWindows) return;
            string combo = "";
            foreach (var (keyCode, name) in new[] { (global::Windows.System.VirtualKey.Control, "Control"), (global::Windows.System.VirtualKey.Shift, "Shift"), (global::Windows.System.VirtualKey.Menu, "Alt"), (global::Windows.System.VirtualKey.LeftWindows, "LeftMeta") }) if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(keyCode).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down)) combo += name + "+";
            int code = (int)e.Key;
            combo += code is >= 48 and <= 57 ? ((char)code).ToString() : code is >= 96 and <= 105 ? "Numpad" + (code - 96) : e.Key switch { global::Windows.System.VirtualKey.Left => "LeftArrow", global::Windows.System.VirtualKey.Right => "RightArrow", global::Windows.System.VirtualKey.Up => "UpArrow", global::Windows.System.VirtualKey.Down => "DownArrow", global::Windows.System.VirtualKey.CapitalLock => "CapsLock", global::Windows.System.VirtualKey.NumberKeyLock => "NumLock", global::Windows.System.VirtualKey.Scroll => "ScrollLock", _ => e.Key.ToString() }; recording = false;
            try { await _forms.Save(ns, key, JsonValue.Create(combo)); button.Content = combo; } catch { button.Content = NativeFormText.Text("保存失败"); }
        };
        var clear = new Button { Content = "清除快捷键" }; clear.Click += async (_, _) => { try { await _forms.Save(ns, key, null); button.Content = NativeFormText.Text("未设置"); } catch { } };
        row.Children.Add(button); row.Children.Add(clear); return row;
    }
    private Button ConfirmAction(string label, string prompt, string ns, string method, params object?[] args)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) =>
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = label, Content = prompt, PrimaryButtonText = "确认", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            NativeFormText.Apply(dialog); if (await LeagueAkari.WinUI.Services.NativeDialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
            button.IsEnabled = false; try { await _backend.CallAsync(ns, method, args); _forms.Status.Text = "操作完成"; } catch (Exception ex) { _forms.Status.Text = ex.Message; } finally { button.IsEnabled = true; }
        };
        return button;
    }
    private UIElement Misc() => SettingsForms.Section("计时与隐私", _forms.Toggle("respawn-timer-main", "enabled", "复活计时"), _forms.Toggle("app-common-main", "streamerMode", "主播模式"), _forms.Toggle("app-common-main", "streamerModeUseAkariStyledName", "使用匿名风格名称"), _forms.Toggle("window-manager-main", "contentProtection", "禁止窗口截屏"));
    private async Task<UIElement> Debug()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(SettingsForms.Section("文件", _forms.Action("打开日志目录", "logger-factory-main", "openLogsDir"), _forms.Action("打开用户目录", "app-common-main", "openUserDataDir")));
        var info = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var connectionFields = new StackPanel { Spacing = 6 };
        var elevated = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var gameflow = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var runtime = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        _refreshDebug = async () =>
        {
            if (_refreshingDebug) return;
            _refreshingDebug = true;
            try
            {
                var connection = await _backend.StateAsync("league-client-main");
                var auth = connection.Field("auth");
                info.Text = auth.ValueKind == JsonValueKind.Object ? string.Join("\n", auth.EnumerateObject().Where(field => field.Name != "password").Select(field => field.Name + ": " + (NativeAppearance.Current?.StreamerMode == true && field.Name is "authToken" or "username" ? "••••" : field.Value.ToString()))) : Localization.Text("未连接", "Disconnected");
                connectionFields.Children.Clear();
                foreach (string key in new[] { "port", "pid", "authToken", "rsoPlatformId", "region" })
                {
                    string value = auth.Text(key, "-"); bool sensitive = key == "authToken" && NativeAppearance.Current?.StreamerMode == true;
                    string labelKey = key switch { "authToken" => "auth", "rsoPlatformId" => "rsoPlatform", _ => key };
                    string label = Localization.Key("settings.debug.lcuConnection." + labelKey, key);
                    string display = key == "rsoPlatformId" && auth.Text("region") == "TENCENT" ? Localization.Key("sgpServers.TENCENT_" + value, value) : value;
                    var copy = new Button { Content = label + ": " + (sensitive ? "••••" : display), IsEnabled = !sensitive && value != "-" };
                    copy.Click += (_, _) => { var package = new global::Windows.ApplicationModel.DataTransfer.DataPackage(); package.SetText(value); global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package); _forms.Status.Text = Localization.Text("已复制", "Copied"); }; connectionFields.Children.Add(copy);
                }
                string phase = (await _backend.StateAsync("league-client-main", "gameflow")).Text("phase", "None");
                gameflow.Text = connection.Text("connectionState") == "connected" ? Localization.Key("settings.debug.gameflow." + phase, phase) + " (" + phase + ")" : Localization.Key("settings.debug.gameflow.unavailable", "Unavailable");
                bool isElevated = (await _backend.StateAsync("app-common-main")).Boolean("isElevated");
                elevated.Visibility = isElevated ? Visibility.Visible : Visibility.Collapsed; elevated.Text = Localization.Key("settings.debug.inAdministrator.description", "Running as administrator");
                var data = await _backend.CallAsync("app-common-main", "getRuntimeInfo");
                var os = data.Field("os"); var memory = data.Field("memoryUsage");
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                runtime.Text = $"Version: {data.Text("version")}\nGo PID: {data.Number("pid")} · {data.Text("platform")} / {data.Text("arch")}\nUptime: {data.Number("uptime"):F2} s\nCPUs: {os.Field("cpus").Items().Count()}\nOS: {os.Text("type")} {os.Text("release")}\nSystem memory: {os.Number("totalmem"):N0} bytes ({os.Number("totalmem") / 1073741824:F2} GB)\nGo working set: {memory.Number("rss") / 1048576:F2} MB · heap: {memory.Number("heapUsed") / 1048576:F2} MB\nWinUI PID: {process.Id} · working set: {process.WorkingSet64 / 1048576d:F2} MB\n.NET: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} · Go: {data.Field("versions").Text("go")}\nArgv: {string.Join(" ", data.Field("argv").Items().Select(value => value.ToString().Contains(' ') ? '\"' + value.ToString() + '\"' : value.ToString()))}";
            }
            catch (Exception ex) { _forms.Status.Text = ex.Message; }
            finally { _refreshingDebug = false; }
        };
        await _refreshDebug();
        panel.Children.Add(SettingsForms.Section("LCU 连接", info, connectionFields, _forms.Action("刷新客户端列表", "league-client-ux-main", "update")));
        panel.Children.Add(SettingsForms.Section("对局阶段", gameflow));
        panel.Children.Add(elevated);
        panel.Children.Add(SettingsForms.Section("Runtime Info", runtime));
        var logAll = new CheckBox { Content = Localization.Key("settings.debug.lcuEvent.logAll", "Log all events to file"), IsChecked = (await _backend.StateAsync("renderer-debug-main")).Boolean("logAllLcuEvents") };
        logAll.Click += async (_, _) => { logAll.IsEnabled = false; try { await _backend.CallAsync("renderer-debug-main", "setLogAllLcuEvents", logAll.IsChecked == true); } catch (Exception ex) { _forms.Status.Text = ex.Message; } finally { logAll.IsEnabled = true; } };
        void LogState(JsonElement envelope)
        {
            if (envelope.Text("name") != "update-state-prop/renderer-debug-main:state") return;
            var args = envelope.Field("args").Items().ToArray(); if (args.Length < 2 || args[0].ValueKind != JsonValueKind.String || args[0].GetString() != "logAllLcuEvents") return;
            logAll.DispatcherQueue.TryEnqueue(() => logAll.IsChecked = args[1].ValueKind == JsonValueKind.True);
        }
        void TranslateDebug() => logAll.Content = Localization.Key("settings.debug.lcuEvent.logAll", "Log all events to file");
        logAll.Loaded += (_, _) => { _backend.EventReceived -= LogState; _backend.EventReceived += LogState; Localization.Changed += TranslateDebug; TranslateDebug(); };
        logAll.Unloaded += (_, _) => { _backend.EventReceived -= LogState; Localization.Changed -= TranslateDebug; };
        panel.Children.Add(SettingsForms.Section("事件与远程配置", logAll, _forms.Toggle("main-window-ui-renderer", "showTestPage", "显示测试页"), _forms.Toggle("main-window-ui-renderer", "sidebarCollapsed", "折叠导航")));
        var section = SettingsForms.Section("LCU 事件记录规则"); var pattern = new AutoSuggestBox { Header = "端点规则", PlaceholderText = "/lol-champ-select/v1/**" }; var list = new StackPanel { Spacing = 8 };
        pattern.TextChanged += (_, args) => { if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) pattern.ItemsSource = DebugEndpoints.Suggest(pattern.Text).ToArray(); };
        pattern.SuggestionChosen += (_, args) => pattern.Text = args.SelectedItem?.ToString() ?? pattern.Text;
        foreach (var saved in _forms.Value("renderer-debug-renderer", "savedRules") as JsonArray ?? new()) _debugRules[saved!.ToString()] = false;
        async Task SaveRules() { await _forms.Save("renderer-debug-renderer", "savedRules", new JsonArray(_debugRules.Keys.Select(key => JsonValue.Create(key)).ToArray<JsonNode?>())); await _backend.CallAsync("renderer-debug-main", "setSendAllNativeLcuEvents", _debugRules.Values.Any(x => x)); }
        void Draw()
        {
            list.Children.Clear(); foreach (string key in _debugRules.Keys.ToArray()) { var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; var enabled = new CheckBox { Content = key, IsChecked = _debugRules[key] }; enabled.Click += async (_, _) => { _debugRules[key] = enabled.IsChecked == true; try { await SaveRules(); } catch (Exception ex) { _forms.Status.Text = ex.Message; } }; var remove = new Button { Content = "删除" }; remove.Click += async (_, _) => { _debugRules.Remove(key); try { await SaveRules(); Draw(); } catch (Exception ex) { _forms.Status.Text = ex.Message; } }; row.Children.Add(enabled); row.Children.Add(remove); list.Children.Add(row); }
        }
        _forms.ObserveValue(list, "renderer-debug-renderer", "savedRules", value =>
        {
            var rules = (value as JsonArray ?? new()).Where(item => item is not null).Select(item => DebugRoute.Normalize(item!.ToString())).Distinct().ToArray();
            var existing = _debugRules.ToDictionary(pair => pair.Key, pair => pair.Value); _debugRules.Clear();
            foreach (string rule in rules) { try { DebugRoute.Validate(rule); _debugRules[rule] = existing.GetValueOrDefault(rule); } catch (ArgumentException) { } }
            Draw();
        });
        var add = new Button { Content = "添加规则", IsEnabled = false };
        void ValidatePattern() { try { DebugRoute.Validate(DebugRoute.Normalize(pattern.Text)); add.IsEnabled = true; } catch (ArgumentException) { add.IsEnabled = false; } }
        pattern.TextChanged += (_, _) => ValidatePattern();
        add.Click += async (_, _) => { string key = DebugRoute.Normalize(pattern.Text); add.IsEnabled = false; try { DebugRoute.Validate(key); if (_debugRules.ContainsKey(key)) return; _debugRules[key] = true; await SaveRules(); Draw(); } catch (Exception ex) { _forms.Status.Text = ex.Message; } finally { ValidatePattern(); } };
        section.Children.Add(pattern); section.Children.Add(add); section.Children.Add(list); section.Children.Add(_eventLog); Draw(); panel.Children.Add(section);
        return panel;
    }
    private void DebugEvent(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("namespace", out var ns) || ns.GetString() != "renderer-debug-main" || !envelope.TryGetProperty("name", out var name) || name.GetString() != "lc-event") return;
        var data = envelope.Field("args").Items().FirstOrDefault(); if (data.ValueKind != JsonValueKind.Object) return; string uri = data.Text("uri");
        DispatcherQueue.TryEnqueue(async () =>
        {
            bool matched = _debugRules.Any(rule => rule.Value && DebugRoute.Matches(rule.Key, uri)); if (!matched) return;
            string message = data.ToString(); _eventLog.Text += message + Environment.NewLine; if (_eventLog.Text.Length > 32000) _eventLog.Text = _eventLog.Text[^24000..];
            try { var state = await _backend.StateAsync("renderer-debug-main"); if (!state.Boolean("logAllLcuEvents")) await _backend.CallAsync("logger-factory-main", "log", "WinUI LCU event", "info", message); } catch { }
        });
    }
    private async Task<UIElement> About()
    {
        string version = (await _backend.CallAsync("app-common-main", "getVersion")).ToString();
        var panel = SettingsForms.Section("LeagueAkari · Windows Native", new TextBlock { Text = "版本 " + version }, new TextBlock { Text = "WinUI 3 原生界面 + Go 后端。MIT 许可证。\n感谢 LeagueAkari、Hanxven 和贡献者；感谢 egoist / MyGo。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new HyperlinkButton { Content = "GitHub 仓库", NavigateUri = new Uri("https://github.com/zhengchalei/LeagueAkari-MyGo") });
        panel.Children.Add(new HyperlinkButton { Content = "LeagueAkari", NavigateUri = new Uri("https://github.com/Hanxven/LeagueAkari") }); return panel;
    }
}


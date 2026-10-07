using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace LeagueAkari.WinUI.Services;

/// <summary>The notification center belongs to the main window, never to a floating helper window.</summary>
public sealed partial class HostNotifications : IDisposable
{
    private readonly BackendClient _backend;
    private readonly Func<XamlRoot?> _xamlRoot;
    private readonly Func<Task>? _quit;
    private readonly Action? _openSettings;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly InfoBar _respawn = new() { IsClosable = false };
    private readonly InfoBar _accept = new() { IsClosable = false };
    private readonly InfoBar _matchmaking = new() { IsClosable = false };
    private readonly InfoBar _update = new() { IsClosable = true };
    private readonly InfoBar _announcement = new() { IsClosable = true };
    private readonly InfoBar _login = new() { IsClosable = false };
    private readonly InfoBar _reconnect = new() { IsClosable = false, Severity = InfoBarSeverity.Success };
    private readonly InfoBar _streaming = new() { IsClosable = true, Severity = InfoBarSeverity.Warning };
    private readonly InfoBar _sgp = new() { IsClosable = true, Severity = InfoBarSeverity.Warning };
    private readonly InfoBar _failure = new() { IsClosable = true, Severity = InfoBarSeverity.Error };
    private readonly ProgressBar _respawnProgress = new() { Maximum = 1, Width = 150 };
    private readonly ProgressBar _updateProgress = new() { Maximum = 1, Width = 220 };
    private readonly ProgressBar _loginProgress = new() { Maximum = 1, Width = 220 };
    private readonly Dictionary<string, JsonElement> _states = [];
    private readonly Dictionary<string, long> _stateRevisions = [];
    private JsonElement _respawnState, _flow, _updateState, _remote, _loginState, _app, _updateSettings, _notificationSettings;
    private bool _visible = true, _disposed, _refreshing, _refreshPending, _started, _declarationShown;
    private string _announcementDialog = "", _dismissedRelease = "", _lastUpdatePhase = "", _streamerPid = "";
    private bool _clientStreamerMode, _checkingStreamer;
    private string _streamerSubscription = "";
    private NativeAppearance? _appearance;
    private ContentDialog? _releaseDialog;
    private ContentDialog? _activeDialog;
    private CheckBox? _releaseIgnore;
    private string _releaseDialogVersion = "";
    public StackPanel Panel { get; } = new() { Spacing = 4 };

    public HostNotifications(BackendClient backend, Func<XamlRoot?> xamlRoot, Func<Task>? quit = null, Action? openSettings = null)
    {
        _backend = backend; _xamlRoot = xamlRoot; _quit = quit; _openSettings = openSettings;
        foreach (var bar in new[] { _respawn, _accept, _matchmaking, _reconnect, _update, _announcement, _login, _streaming, _sgp, _failure }) Panel.Children.Add(bar);
        _respawn.Content = _respawnProgress; _login.Content = _loginProgress;
        _update.CloseButtonClick += (_, _) => _dismissedRelease = _remote.Field("latestRelease").Text("version");
        _streaming.CloseButtonClick += async (_, _) => await DismissStreamingAsync(false);
        _sgp.CloseButtonClick += async (_, _) => await SafeAsync(DismissBadSgpAsync);
        InitializeEasterEggs();
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await RefreshAsync(); };
        _clock.Tick += (_, _) => UpdateCountdowns();
    }
    public async Task StartAsync()
    {
        if (_started) return; _started = true;
        _backend.EventReceived += OnEvent; Localization.Changed += Render;
        _appearance = NativeAppearance.Current; if (_appearance != null) _appearance.Changed += Render;
        await SafeAsync(async () => { var id = await _backend.CallAsync("league-client-main", "subscribeLcuEndpoint", "/lol-settings/v2/account/GamePreferences/game-settings"); _streamerSubscription = id.ValueKind == JsonValueKind.String ? id.GetString()! : id.ToString(); });
        await RefreshAsync();
    }
    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (!visible) { _clock.Stop(); _debounce.Stop(); }
        else if (_started) _ = RefreshAsync();
    }
    private static string L(string zh, string en) => Localization.Text(zh, en);
    private static string K(string key, params (string Name, object? Value)[] args) => Localization.Key(key, null, args.ToDictionary(a => a.Name, a => a.Value));
    private void OnEvent(JsonElement update)
    {
        if (!_visible || _disposed) return;
        Panel.DispatcherQueue.TryEnqueue(() => OnUiEvent(update));
    }
    private void OnUiEvent(JsonElement update)
    {
        if (!_visible || _disposed) return;
        var name = update.Text("name");
        var args = update.Field("args").Items().ToArray();
        if (update.Text("namespace") == "self-update-main" && name == "error-download-update")
        { ShowDownloadFailure(args.FirstOrDefault().Text("message")); return; }
        if (update.Text("namespace") == "league-client-main" && name == "lcu-event" && args.Length >= 3 && args[0].GetString() == "/lol-settings/v2/account/GamePreferences/game-settings")
        { _clientStreamerMode = args[2].Field("data").Field("HUD").Boolean("HidePlayerNames"); Render(); return; }
        if (update.Text("namespace") == "league-client-main" && name == "extra-lcu-event" && args.Length >= 2 && args[0].ToString() == _streamerSubscription)
        { _clientStreamerMode = args[1].Field("data").Field("data").Field("HUD").Boolean("HidePlayerNames"); Render(); return; }
        if (name.StartsWith("update-state-prop/") && args.Length > 1)
        {
            var ns = name["update-state-prop/".Length..];
            if (ns.EndsWith(":state")) ns = ns[..^":state".Length];
            if (_states.TryGetValue(ns, out var previous) && args[0].ValueKind == JsonValueKind.String)
            {
                var copy = previous.ValueKind == JsonValueKind.Object ? previous.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) : [];
                copy[args[0].GetString()!] = args[1].Clone();
                var state = JsonSerializer.SerializeToElement(copy); _states[ns] = state;
                _stateRevisions[ns] = _stateRevisions.GetValueOrDefault(ns) + 1;
                AssignStates(); Render();
                if (_refreshing) _refreshPending = true;
                return;
            }
        }
        if (new[] { "respawn-timer-main", "auto-gameflow-main", "self-update-main", "remote-config-main", "league-client-main", "app-common-main", "simple-notifications-renderer", "client-installation-main", "league-client-ux-main", "sgp-main", "storage-main" }.Any(name.Contains))
            if (!_debounce.IsEnabled) _debounce.Start();
    }
    private async Task RefreshAsync()
    {
        if (_disposed || !_visible) return;
        if (_refreshing) { _refreshPending = true; return; } _refreshing = true;
        try
        {
            var revisions = new Dictionary<string, long>(_stateRevisions);
            var values = await Task.WhenAll(_backend.StateAsync("respawn-timer-main"), _backend.StateAsync("auto-gameflow-main"), _backend.StateAsync("self-update-main"), _backend.StateAsync("remote-config-main"), _backend.StateAsync("league-client-main", "login"), _backend.CallAsync("setting-factory-main", "getByPrefix", "app-common-main", ""), _backend.CallAsync("setting-factory-main", "getByPrefix", "self-update-main", ""), _backend.CallAsync("setting-factory-main", "getByPrefix", "simple-notifications-renderer", ""), _backend.StateAsync("app-common-main"), _backend.StateAsync("client-installation-main"), _backend.StateAsync("league-client-ux-main"), _backend.StateAsync("sgp-main"), _backend.StateAsync("storage-main"), _backend.StateAsync("league-client-main"), _backend.CallAsync("setting-factory-main", "getByPrefix", "league-client-ux-main", ""));
            if (_disposed || !_visible) return;
            foreach (var (ns, index) in new[] { ("respawn-timer-main", 0), ("auto-gameflow-main", 1), ("self-update-main", 2), ("remote-config-main", 3), ("league-client-main:login", 4), ("app-common-main:settings", 5), ("self-update-main:settings", 6), ("simple-notifications-renderer:settings", 7), ("app-common-main", 8), ("client-installation-main", 9), ("league-client-ux-main", 10), ("sgp-main", 11), ("storage-main", 12), ("league-client-main", 13), ("league-client-ux-main:settings", 14) })
                if (_stateRevisions.GetValueOrDefault(ns) == revisions.GetValueOrDefault(ns)) _states[ns] = values[index];
            AssignStates();
            Render();
            await CheckClientStreamingAsync();
        }
        catch (Exception error) { _failure.Title = L("读取通知失败", "Failed to load notifications"); _failure.Message = error.Message; _failure.IsOpen = true; }
        finally { _refreshing = false; if (_refreshPending) { _refreshPending = false; await RefreshAsync(); } }
    }
    private void AssignStates()
    {
        _respawnState = _states.GetValueOrDefault("respawn-timer-main"); _flow = _states.GetValueOrDefault("auto-gameflow-main"); _updateState = _states.GetValueOrDefault("self-update-main"); _remote = _states.GetValueOrDefault("remote-config-main");
        _loginState = _states.GetValueOrDefault("league-client-main:login"); _app = _states.GetValueOrDefault("app-common-main:settings"); _updateSettings = _states.GetValueOrDefault("self-update-main:settings"); _notificationSettings = _states.GetValueOrDefault("simple-notifications-renderer:settings");
    }
    private void Render()
    {
        if (!_visible || _disposed) return;
        _respawn.Title = L("复活计时", "Respawn timer");
        _accept.Title = L("自动接受对局", "Automatic match acceptance");
        _matchmaking.Title = L("自动匹配", "Automatic matchmaking");
        _accept.ActionButton = Action(L("取消自动接受", "Cancel automatic accept"), () => _backend.CallAsync("auto-gameflow-main", "cancelAutoAccept"));
        _matchmaking.ActionButton = Action(L("取消自动匹配", "Cancel automatic matchmaking"), () => _backend.CallAsync("auto-gameflow-main", "cancelAutoMatchmaking"));
        UpdateCountdowns();
        if (_accept.IsOpen || _matchmaking.IsOpen || _reconnect.IsOpen) _clock.Start(); else _clock.Stop();
        var detected = _states.GetValueOrDefault("client-installation-main").Field("detectedLiveStreamingClients").Items().Any();
        _streaming.IsOpen = HostNotificationRules.ShowStreamingHint(detected, _clientStreamerMode, _app.Boolean("streamerMode"), _notificationSettings.Boolean("neverShowLiveStreamingStreamerMode"), _notificationSettings.Number("lastDismissLiveStreamingStreamerMode"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (_streaming.IsOpen)
        {
            _streaming.Title = K("notifications.simple.liveStreamingHints.detected.title"); _streaming.Message = K("notifications.simple.liveStreamingHints.detected." + (detected ? "liveTools" : "bySettings"));
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            controls.Children.Add(Action(K("notifications.simple.liveStreamingHints.dismiss"), () => DismissStreamingAsync(false)));
            controls.Children.Add(Action(K("notifications.simple.liveStreamingHints.neverShowAgain"), () => DismissStreamingAsync(true)));
            controls.Children.Add(Action(K("notifications.simple.liveStreamingHints.toSettings"), async () => { await DismissStreamingAsync(true); _openSettings?.Invoke(); }));
            _streaming.Content = controls;
        }
        RenderSystemWarnings();
        var sgp = _states.GetValueOrDefault("sgp-main"); var attempts = sgp.Number("connectionSuccessesCounted") + sgp.Number("connectionFailuresCounted");
        bool badSgp = _app.Text("preferredLolSource") != "lcu" && attempts >= 5 && sgp.Number("connectionFailuresCounted") / attempts >= .5;
        if (!badSgp) _dismissedBadSgp = false;
        bool showSgp = badSgp && !_dismissedBadSgp && !_notificationSettings.Boolean("neverShowBadSgpConnection"); _sgp.IsOpen = false;
        if (showSgp)
        {
            _sgp.Title = K("notifications.simple.badSgpConnection.title");
            _sgp.Message = K("notifications.simple.badSgpConnection.content.usingSgp") + "\n" + K("notifications.simple.badSgpConnection.content.recommendation") + "\n" + K("notifications.simple.badSgpConnection.content.vpnReason");
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            var never = new CheckBox { Content = K("notifications.simple.badSgpConnection.neverShowAgain"), IsChecked = _neverBadSgp }; never.Checked += (_, _) => _neverBadSgp = true; never.Unchecked += (_, _) => _neverBadSgp = false; actions.Children.Add(never);
            actions.Children.Add(Action(K("notifications.simple.badSgpConnection.negativeText"), DismissBadSgpAsync));
            actions.Children.Add(Action(K("notifications.simple.badSgpConnection.positiveText"), async () => { await DismissBadSgpAsync(); _openSettings?.Invoke(); }));
            RenderSgpDialog(actions);
        }
        else _badSgpDialog?.Hide();
        var queue = _loginState.Field("loginQueueState"); _login.IsOpen = queue.ValueKind == JsonValueKind.Object;
        if (_login.IsOpen)
        {
            _login.Title = K("notifications.simple.login-queue-task.name");
            _login.Message = K("notifications.simple.login-queue-task.description", ("position", queue.Number("estimatedPositionInQueue")), ("maxPosition", queue.Number("maxDisplayedPosition")), ("waitTime", HostNotificationRules.FormatSeconds(queue.Number("approximateWaitTimeSeconds"))));
            _loginProgress.Value = Math.Clamp(1 - queue.Number("estimatedPositionInQueue") / Math.Max(1, queue.Number("maxDisplayedPosition")), 0, 1);
        }
        RenderUpdate();
        var announcement = _remote.Field("announcement");
        _announcement.IsOpen = announcement.ValueKind == JsonValueKind.Object && announcement.Text("uniqueId").Length > 0;
        if (_announcement.IsOpen)
        {
            _announcement.Title = K("announcements.modal.title");
            _announcement.Message = announcement.Field("frontMatter").Text("summary", L("查看最新公告", "Read the latest announcement"));
            _announcement.ActionButton = Action(L("查看", "Read"), ShowAnnouncementAsync);
            if (announcement.Field("frontMatter").Text("alertLevel") == "high" && announcement.Text("uniqueId") != _notificationSettings.Text("lastAnnouncementUniqueId") && announcement.Text("uniqueId") != _announcementDialog)
            { _announcementDialog = announcement.Text("uniqueId"); _ = ShowAnnouncementAsync(); }
        }
        if (_app.Boolean("showFreeSoftwareDeclaration") && !_declarationShown) { _declarationShown = true; _ = ShowDeclarationAsync(); }
        UpdateReleaseDialog();
    }
    private void UpdateCountdowns()
    {
        var info = _respawnState.Field("info"); _respawn.IsOpen = info.Boolean("isDead");
        _respawn.Message = K("navigation.sidebar.status.respawnTimer.timeLeft", ("seconds", info.Number("timeLeft").ToString("0"))) + $" ({info.Number("totalTime"):0}s)";
        _respawnProgress.Value = info.Number("timeLeft") / Math.Max(1, info.Number("totalTime"));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _accept.IsOpen = _flow.Boolean("willAccept") && _flow.Number("willAcceptAt") > 0;
        _accept.Message = $"{Math.Max(0, (_flow.Number("willAcceptAt") - now) / 1000):0.0}s";
        _matchmaking.IsOpen = _flow.Boolean("willSearchMatch") && _flow.Number("willSearchMatchAt") > 0;
        _matchmaking.Message = $"{Math.Max(0, (_flow.Number("willSearchMatchAt") - now) / 1000):0.0}s";
        _reconnect.IsOpen = _flow.Number("willReconnectAt") > now;
        _reconnect.Title = K("notifications.simple.autoReconnect.title");
        _reconnect.Message = K("notifications.simple.autoReconnect.content", ("timeLeft", $"{Math.Max(0, (_flow.Number("willReconnectAt") - now) / 1000):0.0}s"));
    }
    private void RenderUpdate()
    {
        var release = _remote.Field("latestRelease"); var progress = _updateState.Field("updateProgressInfo"); var phase = progress.Text("phase");
        _update.IsOpen = phase.Length > 0 || release.Boolean("isNew") && release.Text("version") != _updateSettings.Text("ignoreVersion") && release.Text("version") != _dismissedRelease;
        if (!_update.IsOpen) return;
        _update.Title = K("notifications.simple.newReleaseHints.title");
        var content = new StackPanel { Spacing = 3 };
        if (_update.Content is StackPanel previousContent) previousContent.Children.Clear();
        if (phase == "downloading")
        {
            _updateProgress.Value = progress.Number("downloadingProgress"); content.Children.Add(_updateProgress);
            _update.Message = K("selfUpdate.tasks.self-update-task.downloading", ("progress", (progress.Number("downloadingProgress") * 100).ToString("F1")), ("avgSpeed", Bytes(progress.Number("averageDownloadSpeed"))), ("eta", TimeSpan.FromSeconds(Math.Max(0, progress.Number("downloadTimeLeft"))).ToString("mm\\:ss")));
            _update.ActionButton = Action(K("selfUpdate.tasks.self-update-task.cancelButton"), () => _backend.CallAsync("self-update-main", "cancelUpdate"));
        }
        else if (phase == "waiting-for-restart")
        {
            _update.Message = K("selfUpdate.tasks.self-update-task.waiting-for-restart");
            _update.ActionButton = Action(K("selfUpdate.tasks.self-update-task.closeButton"), _quit ?? (() => _backend.CallAsync("app-common-main", "exit")));
        }
        else if (phase == "download-failed")
        {
            _update.Severity = InfoBarSeverity.Error; _update.Message = K("notifications.updateModal.downloadFailed");
            _update.ActionButton = Action(L("重新下载", "Retry download"), () => _backend.CallAsync("self-update-main", "startUpdate"));
        }
        else
        {
            _update.Message = K("notifications.simple.newReleaseHints.content", ("version", release.Text("version")));
            _update.ActionButton = Action(K("notifications.simple.newReleaseHints.takeALook"), ShowReleaseAsync);
        }
        if (phase != "download-failed") _update.Severity = InfoBarSeverity.Informational;
        _update.Content = content;
        if (_lastUpdatePhase != phase && phase == "download-failed") ShowDownloadFailure(_updateState.Field("lastUpdateResult").Text("reason"));
        _lastUpdatePhase = phase;
    }
    public async Task ShowReleaseAsync()
    {
        if (_releaseDialog != null) return;
        var release = _remote.Field("latestRelease");
        var content = new StackPanel { Spacing = 10 }; content.Children.Add(Markdown(release.Text("description", K("notifications.updateModal.noUpdateMd"))));
        var ignore = new CheckBox { Content = K("notifications.updateModal.ignoreThisVersion"), IsChecked = _updateSettings.Text("ignoreVersion") == release.Text("version"), Visibility = release.Boolean("isNew") ? Visibility.Visible : Visibility.Collapsed }; content.Children.Add(ignore);
        if (Uri.TryCreate(release.Field("archiveFile").Text("downloadUrl"), UriKind.Absolute, out var url) && url.Scheme is "https" or "http") content.Children.Add(new HyperlinkButton { Content = K("notifications.updateModal.externalDownload"), NavigateUri = url });
        ignore.Checked += async (_, _) => await SafeAsync(() => _backend.CallAsync("setting-factory-main", "set", "self-update-main", "ignoreVersion", release.Text("version")));
        ignore.Unchecked += async (_, _) => await SafeAsync(() => _backend.CallAsync("setting-factory-main", "set", "self-update-main", "ignoreVersion", null));
        _releaseDialogVersion = release.Text("version"); _releaseIgnore = ignore;
        _releaseDialog = new ContentDialog { Title = release.ValueKind != JsonValueKind.Object ? K("notifications.updateModal.noUpdate") : K(release.Boolean("isNew") ? "notifications.updateModal.newVersion" : "notifications.updateModal.versionFeatures") + " " + release.Text("version"), Content = new ScrollViewer { Content = content, MaxHeight = 500 }, CloseButtonText = L("关闭", "Close") };
        UpdateReleaseDialog();
        try { await DialogAsync(_releaseDialog, async result => { if (result == ContentDialogResult.Primary) await SafeAsync(() => _backend.CallAsync("self-update-main", "startUpdate")); }); }
        finally { _releaseDialog = null; _releaseIgnore = null; }
    }
    public async Task ShowAnnouncementAsync()
    {
        var announcement = _remote.Field("announcement");
        var read = new AnnouncementReadState(announcement.Text("uniqueId"), announcement.Field("frontMatter").Text("alertLevel"), _notificationSettings.Text("lastAnnouncementUniqueId"));
        var dialog = new ContentDialog { Title = K("announcements.modal.title"), Content = new ScrollViewer { Content = Markdown(announcement.Text("content", K("announcements.modal.noAnnouncementMd"))), MaxHeight = 500 }, PrimaryButtonText = announcement.Text("uniqueId").Length == 0 ? "" : K(read.PrimaryButtonKey), CloseButtonText = L("关闭", "Close") };
        dialog.Opened += async (_, _) =>
        {
            if (!read.Open()) return;
            dialog.PrimaryButtonText = K(read.PrimaryButtonKey);
            await SafeAsync(() => MarkAnnouncementReadAsync(announcement.Text("uniqueId")));
        };
        if (!await DialogAsync(dialog, async result =>
        {
            if (result == ContentDialogResult.Primary && read.MarkRead()) await SafeAsync(() => MarkAnnouncementReadAsync(announcement.Text("uniqueId")));
        })) _announcementDialog = "";
    }
    public async Task ShowDeclarationAsync()
    {
        var confirmation = K("legal.declaration.ok").Replace("{isFree}", K("legal.declaration.isFree"));
        var seconds = DeclarationDelay();
        // Escape maps to ContentDialog's close action. Keep Quit on the secondary
        // action so dismissing the popup can never be mistaken for choosing Quit.
        var dialog = new ContentDialog { Title = K("legal.declaration.title"), Content = new ScrollViewer { Content = Markdown(K("legal.declaration.newContent")), MaxHeight = 500 }, PrimaryButtonText = confirmation + $" ({seconds})", IsPrimaryButtonEnabled = false, SecondaryButtonText = K("legal.declaration.quit") };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => { seconds = Math.Max(0, seconds - 1); dialog.PrimaryButtonText = confirmation + (seconds > 0 ? $" ({seconds})" : ""); dialog.IsPrimaryButtonEnabled = seconds == 0; if (seconds == 0) timer.Stop(); };
        dialog.Opened += (_, _) => { timer.Start(); AttachDeclarationBypass(dialog); dialog.Focus(FocusState.Programmatic); };
        dialog.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(OnComboKey), true);
        dialog.Closed += (_, _) => timer.Stop();
        _declarationDialog = dialog; _declarationBypass = false;
        dialog.Closing += (_, args) => { if (args.Result == ContentDialogResult.None && !_disposed && !_declarationBypass && !_declarationComboClosed) args.Cancel = true; };
        var blockEscape = new Microsoft.UI.Xaml.Input.KeyEventHandler((_, e) => { if (e.Key == global::Windows.System.VirtualKey.Escape) e.Handled = true; });
        dialog.AddHandler(UIElement.PreviewKeyDownEvent, blockEscape, true);
        dialog.AddHandler(UIElement.KeyDownEvent, blockEscape, true);
        if (!await DialogAsync(dialog, async result =>
        {
            if (result == ContentDialogResult.Primary || _declarationBypass) await SafeAsync(() => _backend.CallAsync("setting-factory-main", "set", "app-common-main", "showFreeSoftwareDeclaration", false));
            else if (result == ContentDialogResult.Secondary) { if (_quit != null) await _quit(); else await SafeAsync(() => _backend.CallAsync("app-common-main", "exit")); }
        })) _declarationShown = false;
        _declarationDialog = null; _declarationComboClosed = false;
    }
    private static int DeclarationDelay()
    {
        double value;
        do { value = 22.5 + 25 / 6d * Math.Sqrt(-2 * Math.Log(Math.Max(double.Epsilon, Random.Shared.NextDouble()))) * Math.Cos(2 * Math.PI * Random.Shared.NextDouble()); } while (value is < 10 or > 35);
        return (int)value;
    }
    private async Task<bool> DialogAsync(ContentDialog dialog, Func<ContentDialogResult, Task> completed, Func<bool>? canShow = null)
    {
        try
        {
            if (_disposed || _xamlRoot() is not { } root) return false;
            dialog.XamlRoot = root;
            dialog.Opened += (_, _) => _activeDialog = dialog;
            dialog.Closed += (_, _) => { if (_activeDialog == dialog) _activeDialog = null; };
            var result = await NativeDialogs.TryShowAsync(dialog, () => _visible && !_disposed && canShow?.Invoke() != false);
            if (!result.HasValue) return false;
            // The shared root slot is released before an action can open another dialog.
            await completed(result.Value);
            return true;
        }
        catch (Exception error) { _failure.Title = error.Message; _failure.IsOpen = true; return false; }
    }
    private Button Action(string label, Func<Task> action)
    {
        var button = new Button { Content = label, FontSize = 11 }; button.Click += async (_, _) => { button.IsEnabled = false; await SafeAsync(action); button.IsEnabled = true; }; return button;
    }
    private async Task SafeAsync(Func<Task> action) { try { await action(); } catch (Exception error) { _failure.Title = error.Message; _failure.IsOpen = true; } }
    private static string Bytes(double count) => count >= 1024 * 1024 ? $"{count / (1024 * 1024):F1} MB" : count >= 1024 ? $"{count / 1024:F1} KB" : $"{count:F0} B";
    private FrameworkElement Markdown(string markdown) => NativeNoticeMarkdown.Create(markdown, async uri =>
    {
        // This is the renderer-link route consumed by registerNewReleaseModal.
        if (uri.Host == "renderer-link" && uri.AbsolutePath == "/overlays/release-modal") { _activeDialog?.Hide(); await ShowReleaseAsync(); }
    });
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _clock.Stop(); _debounce.Stop();
        _backend.EventReceived -= OnEvent; Localization.Changed -= Render; if (_appearance != null) _appearance.Changed -= Render;
        DisposeNotificationExtras();
        if (_streamerSubscription.Length > 0 && _backend.IsReady) _ = UnsubscribeStreamingAsync();
    }
    private async Task UnsubscribeStreamingAsync() { try { await _backend.CallAsync("league-client-main", "unsubscribeLcuEndpoint", _streamerSubscription); } catch { } }
}


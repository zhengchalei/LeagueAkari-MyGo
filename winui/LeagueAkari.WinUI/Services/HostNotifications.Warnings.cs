using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeagueAkari.WinUI.Services;

public sealed partial class HostNotifications
{
    private readonly Dictionary<string, InfoBar> _systemWarnings = [];
    private readonly HashSet<string> _dismissedWarnings = [];
    private readonly Dictionary<string, ContentDialog> _warningDialogs = [];
    private readonly Dictionary<string, bool> _warningConditions = [];
    private bool _dismissedBadSgp, _neverBadSgp, _elevatedShown;
    private ContentDialog? _badSgpDialog;
    private readonly DispatcherTimer _elevatedTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private void RenderSystemWarnings()
    {
        var app = _states.GetValueOrDefault("app-common-main"); var ux = _states.GetValueOrDefault("league-client-ux-main");
        bool elevated = app.Boolean("isElevated"), wmi = _states.GetValueOrDefault("league-client-ux-main:settings").Boolean("useWmi");
        void Warning(string id, bool active, string message, string actionLabel = "", Func<Task>? action = null, bool actionEnabled = true)
        {
            _warningConditions[id] = active;
            if (!active)
            {
                _dismissedWarnings.Remove(id); if (_warningDialogs.TryGetValue(id, out var obsolete)) obsolete.Hide(); return;
            }
            if (_dismissedWarnings.Contains(id)) return;
            var content = new StackPanel { Spacing = 8 }; content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 580 });
            if (action != null) { var button = Action(actionLabel, async () => { if (_warningDialogs.TryGetValue(id, out var dialog)) dialog.Hide(); await action(); }); button.IsEnabled = actionEnabled; content.Children.Add(button); }
            if (_warningDialogs.TryGetValue(id, out var existing)) { existing.Title = K("notifications.simple." + id + ".title"); existing.Content = content; existing.CloseButtonText = L("关闭", "Close"); return; }
            var warning = new ContentDialog { Title = K("notifications.simple." + id + ".title"), Content = content, CloseButtonText = L("关闭", "Close") };
            _warningDialogs[id] = warning; _ = ShowWarningAsync(id, warning);
        }
        Warning("wmiRequiresAdministrator", wmi && !elevated, K("notifications.simple.wmiRequiresAdministrator.content"), K("notifications.simple.wmiRequiresAdministrator.positiveText"), () => _backend.CallAsync("app-common-main", "relaunchAsAdministrator"));
        var cannot = ux.Boolean("hasClientButNoCommandLine") && _states.GetValueOrDefault("league-client-main").Text("connectionState") == "disconnected";
        Func<Task> reconnectAction = wmi || !elevated ? () => Task.CompletedTask : async () => { await _backend.CallAsync("setting-factory-main", "set", "league-client-ux-main", "useWmi", true); await _backend.CallAsync("app-common-main", "relaunchAsAdministrator"); };
        Warning("cannotGetUxCommandLine", cannot, K("notifications.simple.cannotGetUxCommandLine." + (wmi ? "alreadyUseWmi" : elevated ? "withAdminContent" : "noAdminContent")) + (wmi ? "" : "\n" + K("notifications.simple.cannotGetUxCommandLine.extraContent")), K("notifications.simple.cannotGetUxCommandLine." + (wmi || elevated ? "withAdminPositiveText" : "noAdminPositiveText")), reconnectAction);
        Warning("higherVersionDb", _states.GetValueOrDefault("storage-main").Boolean("usingHigherVersionDb"), K("notifications.simple.higherVersionDb.content"), K("notifications.simple.higherVersionDb.positiveText"), ShowReleaseAsync, _remote.Field("latestRelease").Boolean("isNew"));
        Warning("runInTempDirWarning", app.Boolean("isRunInTempDir"), K("notifications.simple.runInTempDirWarning.content"));
        if (elevated && !_elevatedShown)
        {
            _elevatedShown = true;
            var bar = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Informational, IsOpen = true }; _systemWarnings["elevatedStartup"] = bar; Panel.Children.Add(bar);
            _elevatedTimer.Tick += (_, _) => { _elevatedTimer.Stop(); bar.IsOpen = false; };
            _elevatedTimer.Start();
        }
        if (_systemWarnings.TryGetValue("elevatedStartup", out var startup)) { startup.Title = K("notifications.simple.elevatedStartup.title"); startup.Message = K("notifications.simple.elevatedStartup.content"); }
    }
    private async Task ShowWarningAsync(string id, ContentDialog dialog)
    {
        try
        {
            if (_xamlRoot() is not { } root) return; dialog.XamlRoot = root;
            var result = await NativeDialogs.TryShowAsync(dialog, () => _visible && !_disposed && _warningConditions.GetValueOrDefault(id));
            if (result.HasValue && _warningConditions.GetValueOrDefault(id)) _dismissedWarnings.Add(id);
        }
        catch (Exception error) { _failure.Title = error.Message; _failure.IsOpen = true; }
        finally { if (_warningDialogs.GetValueOrDefault(id) == dialog) _warningDialogs.Remove(id); }
    }
    private async Task DismissStreamingAsync(bool never)
    {
        string key = never ? "neverShowLiveStreamingStreamerMode" : "lastDismissLiveStreamingStreamerMode";
        object value = never ? true : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _backend.CallAsync("setting-factory-main", "set", "simple-notifications-renderer", key, value);
        SetLocalNotificationSetting(key, value); Render();
    }
    private async Task DismissBadSgpAsync()
    {
        if (_dismissedBadSgp) return;
        _dismissedBadSgp = true;
        _badSgpDialog?.Hide();
        if (_neverBadSgp) { await _backend.CallAsync("setting-factory-main", "set", "simple-notifications-renderer", "neverShowBadSgpConnection", true); SetLocalNotificationSetting("neverShowBadSgpConnection", true); }
        Render();
    }
    private void RenderSgpDialog(StackPanel actions)
    {
        var content = new StackPanel { Spacing = 12 }; content.Children.Add(new TextBlock { Text = _sgp.Message, MaxWidth = 580, TextWrapping = TextWrapping.Wrap }); content.Children.Add(actions);
        if (_badSgpDialog != null) { _badSgpDialog.Title = _sgp.Title; _badSgpDialog.Content = content; _badSgpDialog.CloseButtonText = K("notifications.simple.badSgpConnection.negativeText"); return; }
        _neverBadSgp = false;
        _badSgpDialog = new ContentDialog { Title = _sgp.Title, Content = content, CloseButtonText = K("notifications.simple.badSgpConnection.negativeText") }; _ = ShowSgpDialogAsync(_badSgpDialog);
    }
    private async Task ShowSgpDialogAsync(ContentDialog dialog)
    {
        try { await DialogAsync(dialog, _ => DismissBadSgpAsync(), () => !_dismissedBadSgp && !_notificationSettings.Boolean("neverShowBadSgpConnection") && _app.Text("preferredLolSource") != "lcu" && _states.GetValueOrDefault("sgp-main").Number("connectionSuccessesCounted") + _states.GetValueOrDefault("sgp-main").Number("connectionFailuresCounted") >= 5 && _states.GetValueOrDefault("sgp-main").Number("connectionFailuresCounted") / Math.Max(1, _states.GetValueOrDefault("sgp-main").Number("connectionSuccessesCounted") + _states.GetValueOrDefault("sgp-main").Number("connectionFailuresCounted")) >= .5); }
        finally { if (_badSgpDialog == dialog) _badSgpDialog = null; }
    }
    private void SetLocalNotificationSetting(string key, object value)
    {
        var copy = _notificationSettings.ValueKind == JsonValueKind.Object ? _notificationSettings.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone()) : [];
        copy[key] = JsonSerializer.SerializeToElement(value); _notificationSettings = JsonSerializer.SerializeToElement(copy); _states["simple-notifications-renderer:settings"] = _notificationSettings;
        _stateRevisions["simple-notifications-renderer:settings"] = _stateRevisions.GetValueOrDefault("simple-notifications-renderer:settings") + 1;
    }
    private async Task CheckClientStreamingAsync()
    {
        var client = _states.GetValueOrDefault("league-client-main"); string pid = client.Field("auth").Number("pid").ToString();
        if (client.Text("connectionState") != "connected") { _streamerPid = ""; _clientStreamerMode = false; return; }
        if (_checkingStreamer || _streamerPid == pid) return; _checkingStreamer = true;
        try
        {
            var response = await _backend.CallAsync("winui-backend", "lcuRequest", "GET", "/lol-settings/v2/account/GamePreferences/game-settings", null);
            if (_disposed || _states.GetValueOrDefault("league-client-main").Field("auth").Number("pid").ToString() != pid) return;
            _streamerPid = pid; _clientStreamerMode = response.Field("data").Field("HUD").Boolean("HidePlayerNames"); Render();
        }
        catch { /* An unavailable optional game preference does not replace an unrelated notification. */ }
        finally { _checkingStreamer = false; }
    }
    private void ShowDownloadFailure(string reason)
    {
        _failure.Title = K("notifications.simple.updateDownloadFailed.title"); _failure.Message = K("notifications.simple.updateDownloadFailed.content", ("error", reason));
        _failure.ActionButton = Action(K("notifications.simple.updateDownloadFailed.positiveText"), async () => { _failure.IsOpen = false; await ShowAnnouncementAsync(); }); _failure.IsOpen = true;
    }
    private async Task MarkAnnouncementReadAsync(string id)
    {
        if (id.Length == 0) return;
        await _backend.CallAsync("setting-factory-main", "set", "simple-notifications-renderer", "lastAnnouncementUniqueId", id); SetLocalNotificationSetting("lastAnnouncementUniqueId", id);
    }
    private void UpdateReleaseDialog()
    {
        if (_releaseDialog == null) return;
        var release = _remote.Field("latestRelease"); var progress = _updateState.Field("updateProgressInfo"); bool updating = progress.ValueKind == JsonValueKind.Object;
        if (_releaseIgnore != null) { _releaseIgnore.IsEnabled = !updating; _releaseIgnore.Content = K("notifications.updateModal.ignoreThisVersion"); }
        _releaseDialog.PrimaryButtonText = !release.Boolean("isNew") || release.Field("archiveFile").ValueKind != JsonValueKind.Object ? "" : progress.Text("phase") switch { "downloading" => K("notifications.updateModal.downloading", ("progress", (progress.Number("downloadingProgress") * 100).ToString("F0"))), "waiting-for-restart" => K("notifications.updateModal.waitingForRestart"), "download-failed" => K("notifications.updateModal.downloadFailed"), _ => K("notifications.updateModal.startUpdate") };
        _releaseDialog.IsPrimaryButtonEnabled = !updating && release.Text("version") == _releaseDialogVersion && release.Boolean("isNew") && release.Field("archiveFile").ValueKind == JsonValueKind.Object;
        _releaseDialog.CloseButtonText = L("关闭", "Close");
    }
}

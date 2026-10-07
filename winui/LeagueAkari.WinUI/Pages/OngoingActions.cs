using System.Text.Json;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class OngoingPage
{
    private string _queueSignature = "";
    private void InitializeQueue()
    {
        _queue.SelectionChanged += async (_, _) =>
        {
            if (!_subscribed || _applyingToolbar || _queue.SelectedItem is not ComboBoxItem choice) return;
            await RunAsync(async () =>
            {
                string tag = choice.Tag?.ToString() ?? "<akari:all>";
                await _backend.CallAsync("setting-factory-main", "set", "ongoing-game-main", "matchHistoryTagPreference", tag == "current" ? "current" : "all");
                if (tag != "current") await _backend.CallAsync("ongoing-game-main", "setMatchHistoryTagParams", OngoingPlayerActions.QueryParameters(tag));
                await _backend.CallAsync("ongoing-game-main", "reload"); await RefreshAsync();
            });
        };
    }
    private void SyncQueue()
    {
        _queue.Visibility = _preferredSource == "sgp" && _sgp.Field("availability").Field("serversSupported").Boolean("matchHistory") ? Visibility.Visible : Visibility.Collapsed;
        var options = new List<(string Tag, string Label)> { ("current", L("当前对局模式", "Current game mode")), ("<akari:all>", Localization.Key("common.sgpMatchHistoryTags.all", L("所有模式", "All modes"))), ("ranked", Localization.Key("common.sgpMatchHistoryTags.ranked", L("所有排位", "All ranked games"))), ("normal", Localization.Key("common.sgpMatchHistoryTags.normal", L("所有普通对局", "All normal games"))) };
        foreach (var queue in _sgp.Field("supportedQueues").Items().Where(q => q.ValueKind == JsonValueKind.Number).Select(q => q.GetInt32()).Distinct()) options.Add(("q_" + queue, _resources.Field("queues").Field(queue.ToString()).Text("name", Localization.Translate(MatchData.QueueLabel(queue)))));
        string selected = _settings.Text("matchHistoryTagPreference", "current") == "current" ? "current" : _state.Field("matchHistoryTagParams").Text("tag", "<akari:all>");
        string signature = JsonSerializer.Serialize(options.Select(o => new { o.Tag, o.Label }));
        if (signature != _queueSignature)
        {
            _queueSignature = signature; _queue.Items.Clear();
            foreach (var option in options) _queue.Items.Add(new ComboBoxItem { Tag = option.Tag, Content = option.Label });
        }
        _queue.SelectedItem = _queue.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == selected);
    }
    private async Task EditTagAsync(string id, string name)
    {
        var actions = new OngoingPlayerActions(_backend);
        var text = new TextBox { PlaceholderText = L("标记内容，留空删除", "Tag text; leave empty to remove"), TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 90, IsEnabled = false };
        var error = new InfoBar { IsClosable = true, Severity = InfoBarSeverity.Error };
        var loading = new ProgressRing { IsActive = true, Width = 24, Height = 24 };
        var retry = new Button { Content = L("重试", "Retry"), Visibility = Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 8, MinWidth = 360 }; panel.Children.Add(text); panel.Children.Add(loading); panel.Children.Add(error); panel.Children.Add(retry);
        var dialog = new ContentDialog { Title = Localization.Key("playerTags.editModal.title") + " · " + name, Content = panel, PrimaryButtonText = L("保存", "Save"), CloseButtonText = L("取消", "Cancel"), IsPrimaryButtonEnabled = false, XamlRoot = XamlRoot };
        JsonElement auth = default; string selfPuuid = ""; bool ready = false, saving = false, opened = false, saved = false;
        async Task LoadAsync()
        {
            ready = false; text.IsEnabled = dialog.IsPrimaryButtonEnabled = false; retry.Visibility = Visibility.Collapsed; loading.IsActive = true; loading.Visibility = Visibility.Visible; error.IsOpen = false;
            try
            {
                var me = (await _backend.StateAsync("league-client-main", "summoner")).Field("me");
                selfPuuid = me.Text("puuid"); auth = (await _backend.StateAsync("league-client-main")).Field("auth");
                if (selfPuuid.Length == 0) throw new InvalidOperationException(L("等待 LOL 客户端登录", "Waiting for League client sign-in"));
                var tag = await actions.LoadTagAsync(id, selfPuuid);
                if (!opened) return;
                text.Text = tag; ready = true; text.IsEnabled = dialog.IsPrimaryButtonEnabled = true; text.Focus(FocusState.Programmatic); text.SelectionStart = text.Text.Length;
            }
            catch (Exception failure) { if (opened) { error.Title = failure.Message; error.IsOpen = true; retry.Visibility = Visibility.Visible; } }
            finally { loading.IsActive = false; loading.Visibility = Visibility.Collapsed; }
        }
        async Task SaveAsync()
        {
            if (!ready || saving) return; saving = true; text.IsEnabled = dialog.IsPrimaryButtonEnabled = false; error.IsOpen = false; loading.IsActive = true; loading.Visibility = Visibility.Visible;
            try
            {
                await actions.SaveTagAsync(id, selfPuuid, text.Text, auth);
                saved = true; saving = false; dialog.Hide();
            }
            catch (Exception failure) { error.Title = failure.Message; error.IsOpen = true; }
            finally { saving = false; text.IsEnabled = dialog.IsPrimaryButtonEnabled = true; loading.IsActive = false; loading.Visibility = Visibility.Collapsed; }
        }
        dialog.Opened += async (_, _) => { opened = true; await LoadAsync(); };
        dialog.Closed += (_, _) => opened = false;
        dialog.Closing += (_, args) => args.Cancel = saving;
        dialog.PrimaryButtonClick += async (_, args) => { args.Cancel = true; await SaveAsync(); };
        retry.Click += async (_, _) => await LoadAsync();
        text.KeyDown += async (_, args) =>
        {
            bool Down(global::Windows.System.VirtualKey key) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (args.Key != global::Windows.System.VirtualKey.Enter || !Down(global::Windows.System.VirtualKey.Shift) || Down(global::Windows.System.VirtualKey.Control) || Down(global::Windows.System.VirtualKey.Menu) || Down(global::Windows.System.VirtualKey.LeftWindows) || Down(global::Windows.System.VirtualKey.RightWindows)) return;
            args.Handled = true; await SaveAsync();
        };
        await NativeDialogs.ShowAsync(dialog);
        if (saved) await RefreshAsync();
    }
}

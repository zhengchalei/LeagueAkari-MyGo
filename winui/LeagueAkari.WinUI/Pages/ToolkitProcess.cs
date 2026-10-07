using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LeagueAkari.WinUI.Services;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class ToolkitPage
{
    private StackPanel ProcessTools()
    {
        var panel = Panel();
        var controller = new ToolkitProcessController((ns, method, args) => _backend.CallAsync(ns, method, args));
        var stateLabel = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var errorBar = new InfoBar { IsClosable = true, Severity = InfoBarSeverity.Error };
        var progress = new ProgressRing { Width = 18, Height = 18, IsActive = false, Visibility = Visibility.Collapsed };
        var actions = new Dictionary<ToolkitProcessOperation, (Button Button, Func<string> Title)>();
        var translated = new List<(TextBlock Block, Func<string> Text)>();
        ToolkitProcessOperation? attempted = null;
        bool loaded = false, controlsDirty = false, readingControls = false;
        var controlsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        string Key(string key) => Localization.Key(key);
        void Section(Func<string> title, Func<string>? description = null)
        {
            var heading = Label(title()); panel.Children.Add(heading); translated.Add((heading, title));
            if (description is null) return;
            var details = new TextBlock { Text = description(), TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
            panel.Children.Add(details); translated.Add((details, description));
        }
        Button Add(ToolkitProcessOperation operation, Func<string> title)
        {
            var button = new Button { Content = title(), IsEnabled = false };
            actions[operation] = (button, title);
            button.Click += async (_, _) =>
            {
                attempted = operation;
                Func<Task<bool>>? confirm = operation switch
                {
                    ToolkitProcessOperation.ExitChampSelect => async () =>
                    {
                        var dialog = new ContentDialog
                        {
                            Title = Key("auxWindow.champSelect.operations.dodge.label"),
                            Content = Key("auxWindow.champSelect.operations.dodge.popconfirm"),
                            PrimaryButtonText = Key("auxWindow.champSelect.operations.dodge.positiveText"),
                            CloseButtonText = Key("auxWindow.champSelect.operations.dodge.negativeText"),
                            DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot
                        };
                        return await NativeDialogs.TryShowAsync(dialog, () => loaded) == ContentDialogResult.Primary;
                    },
                    ToolkitProcessOperation.TerminateGame => () => Confirm(Localization.Text("结束游戏进程", "Terminate game process"), Localization.Text("强制结束游戏客户端？正在进行的对局会断开。", "Terminate the game client? The current match will disconnect.")),
                    _ => null
                };
                await controller.ExecuteAsync(operation, confirm);
            };
            return button;
        }
        Section(() => Key("toolkit.gameflowInProgress.title"));
        panel.Children.Add(Row(stateLabel, progress)); panel.Children.Add(errorBar);
        Section(() => Key("toolkit.gameflowInProgress.playAgain.label"), () => Key("toolkit.gameflowInProgress.playAgain.description"));
        panel.Children.Add(Add(ToolkitProcessOperation.PlayAgain, () => Key("toolkit.gameflowInProgress.playAgain.button")));
        Section(() => Key("toolkit.gameflowInProgress.leaveLobby.label"), () => Key("toolkit.gameflowInProgress.leaveLobby.description"));
        panel.Children.Add(Row(Add(ToolkitProcessOperation.LeaveLobby, () => Key("toolkit.gameflowInProgress.leaveLobby.button")),
            Add(ToolkitProcessOperation.StartMatchmaking, () => Localization.Text("开始匹配", "Start matchmaking"))));
        Section(() => Localization.Text("匹配确认", "Match confirmation"));
        panel.Children.Add(Row(Add(ToolkitProcessOperation.Accept, () => Key("auxWindow.lounge.panel.autoAccept.acceptButton")),
            Add(ToolkitProcessOperation.Decline, () => Key("auxWindow.lounge.panel.autoAccept.declineButton")),
            Add(ToolkitProcessOperation.CancelMatchmaking, () => Key("auxWindow.lounge.panel.matchmaking.stopAndDisable"))));
        Section(() => Key("toolkit.gameflowInProgress.dodge.label"), () => Key("toolkit.gameflowInProgress.dodge.description"));
        panel.Children.Add(Row(Add(ToolkitProcessOperation.ExitChampSelect, () => Key("auxWindow.champSelect.operations.dodge.positiveText")),
            Add(ToolkitProcessOperation.StartDodgeLoop, () => Key("auxWindow.champSelect.operations.dodge.button")),
            Add(ToolkitProcessOperation.CancelDodgeLoop, () => Key("auxWindow.champSelect.operations.dodge.cancel"))));
        Section(() => Localization.Text("游戏客户端", "Game client"));
        panel.Children.Add(Row(Add(ToolkitProcessOperation.Reconnect, () => Localization.Text("重新连接对局", "Reconnect to match")),
            Add(ToolkitProcessOperation.TerminateGame, () => Localization.Text("结束游戏客户端进程", "Terminate game client"))));

        void Apply()
        {
            foreach (var item in translated) item.Block.Text = item.Text();
            var state = controller.Snapshot;
            stateLabel.Text = state.Connected ? Localization.Key("settings.debug.gameflow." + state.Phase, state.Phase)
                : Localization.Text("客户端未连接", "Client disconnected");
            progress.IsActive = controller.Busy; progress.Visibility = controller.Busy ? Visibility.Visible : Visibility.Collapsed;
            foreach (var entry in actions)
            {
                entry.Value.Button.IsEnabled = controller.CanExecute(entry.Key);
                entry.Value.Button.Content = entry.Key == ToolkitProcessOperation.StartDodgeLoop && state.DodgeLooping
                    ? entry.Value.Title() + " (" + (state.DodgeIterations >= 1000 ? "999+" : state.DodgeIterations.ToString()) + ")" : entry.Value.Title();
            }
            if (controller.Error is { } error)
            {
                string reason = error.Message == "process-action-unavailable" ? Localization.Text("当前客户端或对局阶段不支持此操作", "This action is unavailable in the current client or match phase")
                    : error.Message == "process-confirmation-required" ? Localization.Text("此操作需要确认", "This action requires confirmation") : error.Message;
                string? prefix = attempted switch
                {
                    ToolkitProcessOperation.PlayAgain => "toolkit.gameflowInProgress.playAgain.failedNotification",
                    ToolkitProcessOperation.ExitChampSelect or ToolkitProcessOperation.StartDodgeLoop => "toolkit.gameflowInProgress.dodge.failedNotification",
                    _ => null
                };
                errorBar.Title = prefix is not null ? Key(prefix + ".title") : attempted is { } operation ? actions[operation].Title() : reason;
                errorBar.Message = prefix is not null ? Localization.Key(prefix + ".description", arguments: new Dictionary<string, object?> { ["reason"] = reason }) : reason;
                errorBar.IsOpen = true;
            }
            else if (controller.RefreshError is { } refreshError) { errorBar.Title = Localization.Text("读取对局状态失败", "Failed to read match state"); errorBar.Message = refreshError.Message; errorBar.IsOpen = true; }
            else if (controller.Busy) errorBar.IsOpen = false;
            else if (controller.Completed is { } completed && (completed != ToolkitProcessOperation.StartDodgeLoop || state.DodgeLooping))
                _status.Text = actions[completed].Title() + " · " + Localization.Text("操作完成", "Completed");
        }
        controller.Changed += Apply;
        void Changed(JsonElement envelope)
        {
            if (ToolkitProcessData.IsStateEvent(envelope)) panel.DispatcherQueue.TryEnqueue(() => { if (loaded) controller.ApplyStateEvent(envelope); });
            else if (envelope.Text("namespace") == "winui-backend" && envelope.Text("name") == "miniChanged")
                panel.DispatcherQueue.TryEnqueue(() => { if (loaded) controlsDirty = true; });
        }
        void LocaleChanged() => panel.DispatcherQueue.TryEnqueue(Apply);
        controlsTimer.Tick += async (_, _) =>
        {
            if (!loaded || readingControls || !controlsDirty) return;
            readingControls = true; controlsDirty = false;
            try { await controller.RefreshControlsAsync(); }
            finally { readingControls = false; }
        };
        panel.Loaded += async (_, _) =>
        {
            loaded = true; controller.Activate(); _backend.EventReceived -= Changed; _backend.EventReceived += Changed;
            Localization.Changed += LocaleChanged; controlsTimer.Start();
            try { await controller.RefreshAsync(); await controller.RefreshControlsAsync(); } catch { }
            Apply();
        };
        panel.Unloaded += (_, _) =>
        {
            loaded = false; controller.Deactivate(); controlsTimer.Stop(); _backend.EventReceived -= Changed; Localization.Changed -= LocaleChanged;
        };
        Apply(); return panel;
    }
}

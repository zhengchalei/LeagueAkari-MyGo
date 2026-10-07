using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LeagueAkari.WinUI.Services;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class ToolkitPage
{
    private StackPanel Clients()
    {
        var p = Panel(); p.Children.Add(Label("启动器"));
        foreach (var (label, method) in new[] { ("启动 TCLS", "launchTencentTcls"), ("启动 WeGame", "launchWeGame"), ("从 WeGame 启动 LOL", "launchWeGameLeagueOfLegends"), ("启动 Riot 客户端", "launchDefaultRiotClient") })
            p.Children.Add(Action(label, async () => await _backend.CallAsync("client-installation-main", method)));
        var detected = new TextBlock { TextWrapping = TextWrapping.Wrap };
        p.Children.Add(Action("刷新安装与客户端进程", async () => { await _backend.CallAsync("client-installation-main", "update"); var installations = await _backend.StateAsync("client-installation-main"); var clients = await _backend.StateAsync("league-client-ux-main"); detected.Text = string.Join("\n", new[] { installations.Text("tclsExecutablePath"), installations.Text("weGameExecutablePath"), installations.Text("officialRiotClientExecutablePath"), $"正在运行 {clients.Field("launchedClients").Items().Count()} 个客户端" }.Where(s => s.Length > 0)); })); p.Children.Add(detected);
        p.Children.Add(Label("游戏设置文件"));
        bool connected = false, observed = false, busy = false, syncingShortcut = false, fixing = false; int revision = 0, refreshRevision = 0;
        string fileMode = "unavailable"; JsonElement appState = default;
        var mode = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var inputStatus = new TextBlock { TextWrapping = TextWrapping.Wrap }; var adjustStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var terminate = new CheckBox { Content = "启用终止游戏快捷键", IsEnabled = false };
        var terminateShortcut = ShortcutChooser("终止游戏快捷键"); terminateShortcut.IsEnabled = false;
        var width = new NumberBox { Header = "宽度", Value = 1280, Minimum = 1, Width = 180 }; var height = new NumberBox { Header = "高度", Value = 720, Minimum = 1, Width = 180 };
        Button? read = null, lockFile = null, unlockFile = null, fix = null;
        void Apply()
        {
            var input = ToolkitClientData.Requirement(appState, "nativeInput"); var adjustment = ToolkitClientData.Requirement(appState, "adjustLeagueClientWindowSize");
            terminate.IsEnabled = terminateShortcut.IsEnabled = input.Available;
            if (fix is not null) fix.IsEnabled = width.IsEnabled = height.IsEnabled = connected && adjustment.Available && !fixing;
            if (read is not null) read.IsEnabled = lockFile!.IsEnabled = unlockFile!.IsEnabled = connected && !busy;
            mode.Text = fileMode switch { "readonly" => Localization.Key("toolkit.client.gameClient.settingsFileMode.setToReadonly", "Game settings locked"), "writable" => Localization.Key("toolkit.client.gameClient.settingsFileMode.setToWritable", "Game settings unlocked"), _ => Localization.Text("设置文件状态不可用", "Settings file is unavailable") };
            terminate.Content = Localization.Key("toolkit.client.gameClient.terminateGameClientWithShortcut." + (input.NeedsElevation ? "labelAdminRequired" : "label"), "Enable terminate game shortcut");
            terminateShortcut.Header = Localization.Key("toolkit.client.gameClient.terminateShortcut." + (input.NeedsElevation ? "labelAdminRequired" : "label"), "Terminate shortcut");
            inputStatus.Text = input.Available ? "" : Localization.Key("toolkit.client.gameClient." + (input.AvailableOnPlatform ? "nativeAddonRequiresAdministrator" : "windowsOnlyNativeAddon"), "Native input is unavailable");
            adjustStatus.Text = adjustment.Available ? "" : Localization.Key("toolkit.client.leagueClientUx.fixWindowMethodAOptions." + (adjustment.AvailableOnPlatform ? "requiresAdministrator" : "unsupportedCurrentPlatform"), "Window adjustment is unavailable");
        }
        async Task ReadMode()
        {
            if (!connected) { fileMode = "unavailable"; Apply(); return; }
            int current = revision;
            try { string result = ToolkitClientData.FileMode(await _backend.CallAsync("game-client-main", "getSettingsFileReadonlyOrWritable")); if (current == revision && connected) fileMode = result; }
            catch { if (current == revision) fileMode = "unavailable"; }
            Apply();
        }
        async Task FileOperation(string? value)
        {
            if (!connected || busy) return;
            busy = true; Apply();
            try { if (value is not null) await _backend.CallAsync("game-client-main", "setSettingsFileReadonlyOrWritable", value); await ReadMode(); }
            catch (Exception error) { fileMode = "unavailable"; _status.Text = error.Message; throw; }
            finally { busy = false; Apply(); }
        }
        read = Action("读取当前锁定状态", async () => { try { await FileOperation(null); } finally { DispatcherQueue.TryEnqueue(Apply); } });
        lockFile = Action("锁定为只读", async () => { try { await FileOperation("readonly"); } finally { DispatcherQueue.TryEnqueue(Apply); } });
        unlockFile = Action("恢复可写", async () => { try { await FileOperation("writable"); } finally { DispatcherQueue.TryEnqueue(Apply); } });
        p.Children.Add(mode); p.Children.Add(Row(read, lockFile, unlockFile));
        var details = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void TranslateDetails() => details.Text = string.Join("\n", new[] { "readonly", "writable", "scope" }.Select(key => Localization.Key("toolkit.client.gameClient.settingsFileMode.details." + key, key)));
        TranslateDetails(); p.Children.Add(details);
        p.Children.Add(Label("修复客户端窗口尺寸"));
        async Task FixWindow()
        {
            if (!connected || !ToolkitClientData.Requirement(appState, "adjustLeagueClientWindowSize").Available) return;
            if (fixing) return; fixing = true; Apply();
            try
            {
                if (!double.IsFinite(width.Value) || !double.IsFinite(height.Value) || width.Value < 1 || height.Value < 1) throw new InvalidOperationException(Localization.Text("请输入有效宽高", "Enter a valid width and height"));
                var dimensions = new { baseWidth = width.Value, baseHeight = height.Value };
                if (await Confirm(Localization.Key("toolkit.client.leagueClientUx.fixWindowMethodAOptions.dialog.title", "Adjust window size"), Localization.Key("toolkit.client.leagueClientUx.fixWindowMethodAOptions.dialog.content", "Adjust the client window size?")))
                {
                    var current = await _backend.StateAsync("league-client-main");
                    if (current.Text("connectionState") != "connected") return;
                    await _backend.CallAsync("league-client-main", "fixWindowMethodA", dimensions);
                }
            }
            catch (Exception error) { _status.Text = error.Message; }
            finally { fixing = false; Apply(); }
        }
        fix = new Button { Content = "修复窗口" }; fix.Click += async (_, _) => await FixWindow();
        width.KeyDown += (_, args) => { if (args.Key == global::Windows.System.VirtualKey.Enter) height.Focus(FocusState.Programmatic); };
        height.KeyDown += async (_, args) => { if (args.Key == global::Windows.System.VirtualKey.Enter) await FixWindow(); };
        p.Children.Add(Row(width, height, fix)); p.Children.Add(adjustStatus);
        p.Children.Add(Action("重建 WMI 查询", async () => { if (await Confirm("重建 WMI", "修复客户端进程信息查询？")) await _backend.CallAsync("league-client-ux-main", "rebuildWmi"); }));
        terminate.Click += async (_, _) => { try { await Set("game-client-main", "terminateGameClientWithShortcut", terminate.IsChecked == true); } catch (Exception error) { _status.Text = error.Message; } };
        terminateShortcut.SelectionChanged += async (_, _) => { if (syncingShortcut) return; try { await Set("game-client-main", "terminateShortcut", (terminateShortcut.SelectedItem as ComboBoxItem)?.Tag); } catch (Exception error) { _status.Text = error.Message; } };
        p.Children.Add(terminate); p.Children.Add(terminateShortcut); p.Children.Add(inputStatus);
        async Task Refresh()
        {
            int refresh = ++refreshRevision;
            var values = await Task.WhenAll(_backend.StateAsync("league-client-main"), _backend.StateAsync("app-common-main"), _backend.StateAsync("game-client-main", "settings"));
            if (!observed || refresh != refreshRevision) return;
            bool next = values[0].Text("connectionState") == "connected";
            if (next != connected) revision++;
            connected = next; appState = values[1];
            try { syncingShortcut = true; terminate.IsChecked = values[2].Boolean("terminateGameClientWithShortcut"); RestoreShortcut(terminateShortcut, values[2].Text("terminateShortcut")); } finally { syncingShortcut = false; }
            if (!connected) fileMode = "unavailable"; Apply(); if (connected && !busy) await ReadMode();
        }
        void Changed(JsonElement envelope)
        {
            string name = envelope.Text("name");
            if (name.Contains("league-client-main:state") || name.Contains("app-common-main:state") || name.Contains("game-client-main:settings"))
                p.DispatcherQueue.TryEnqueue(async () => { if (!observed) return; try { await Refresh(); } catch (Exception error) { _status.Text = error.Message; } });
        }
        void LocaleChanged() => p.DispatcherQueue.TryEnqueue(() => { Apply(); TranslateDetails(); });
        p.Loaded += async (_, _) => { observed = true; _backend.EventReceived -= Changed; _backend.EventReceived += Changed; Localization.Changed += LocaleChanged; try { await Refresh(); } catch (Exception error) { _status.Text = error.Message; } };
        p.Unloaded += (_, _) => { observed = false; revision++; refreshRevision++; _backend.EventReceived -= Changed; Localization.Changed -= LocaleChanged; };
        Apply(); return p;
    }
}

using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LeagueAkari.WinUI.Services;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class ToolkitPage
{
    private StackPanel SendTools()
    {
        var p = Panel(); var controller = new FixedTextPresetController((ns, method, args) => _backend.CallAsync(ns, method, args));
        bool loaded = false, applying = false, statusBusy = false, statusPending = false, sendingLines = false, settingsReady = false;
        int lifecycle = 0, settingsRead = 0;
        Task<bool>? blurSave = null;
        string phase = "none", nativeReason = "unavailable"; bool nativeAvailable = false, elevated = false;
        string Key(string suffix) => Localization.Key("toolkit.inGameSend.presets.fixedText." + suffix);
        var text = new TextBox { AcceptsReturn = true, MinHeight = 100, TextWrapping = TextWrapping.Wrap };
        var sendLines = new Button { IsEnabled = false };
        var interval = new NumberBox { Value = 65, Minimum = 10, Maximum = 3500, SmallChange = 15, LargeChange = 150, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var cancelShortcut = ShortcutChooser("");
        var heading = Label(""); var presets = new ComboBox { MinWidth = 320 };
        var title = new TextBox { MaxLength = FixedTextPresetController.TitleLimit };
        var content = new TextBox { AcceptsReturn = true, MinHeight = 160, TextWrapping = TextWrapping.Wrap, MaxLength = FixedTextPresetController.ContentLimit };
        var shortcut = ShortcutChooser("");
        void TranslateShortcutRecorder(ComboBox chooser)
        {
            if (chooser.Header is not StackPanel header || header.Children.ElementAtOrDefault(1) is not Button record) return;
            bool recording = record.Content?.ToString() is "按组合键（Esc 取消）" or "Press a shortcut (Esc cancels)";
            record.Content = recording ? Localization.Text("按组合键（Esc 取消）", "Press a shortcut (Esc cancels)") : Localization.Text("录制", "Record");
        }
        foreach (var chooser in new[] { cancelShortcut, shortcut })
        {
            if (chooser.Header is StackPanel header && header.Children.ElementAtOrDefault(1) is Button record)
            {
                record.Click += (_, _) => TranslateShortcutRecorder(chooser);
                record.KeyDown += (_, _) => TranslateShortcutRecorder(chooser);
            }
        }
        var add = new Button(); var save = new Button(); var send = new Button(); var up = new Button(); var down = new Button(); var delete = new Button(); var refresh = new Button();
        var progress = new ProgressRing { Width = 18, Height = 18, IsActive = false, Visibility = Visibility.Collapsed };
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7 }; var footer = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = true };
        void ShortcutHeader(ComboBox chooser, string value)
        {
            if (chooser.Header is StackPanel header && header.Children[0] is TextBlock label) label.Text = value;
            if (chooser.Items[0] is ComboBoxItem none) none.Content = Localization.Text("不设置", "Unset");
            TranslateShortcutRecorder(chooser);
        }
        string NativeMessage() => Localization.Key("toolkit.inGameSend.presets.footer.shortcutUnavailable", arguments: new Dictionary<string, object?> { ["reason"] = Localization.Key("toolkit.inGameSend.presets.nativeInput." + nativeReason) });
        void Apply()
        {
            if (!loaded) return;
            applying = true;
            try
            {
                text.Header = Localization.Text("发送内容（每行一条）", "Send content (one message per line)");
                interval.Header = Localization.Key("toolkit.inGameSend.settings.sendInterval.label");
                ShortcutHeader(cancelShortcut, Localization.Key("toolkit.inGameSend.settings.cancelShortcut.label"));
                cancelShortcut.IsEnabled = elevated;
                heading.Text = Key("label"); presets.Header = Key("listTitle"); title.Header = Key("editTitle"); content.Header = Localization.Text("预设内容", "Preset content");
                ShortcutHeader(shortcut, Key("shortcutLabel"));
                string[] ids = presets.Items.Cast<ComboBoxItem>().Select(item => (string)item.Tag).ToArray();
                if (!ids.SequenceEqual(controller.Items.Select(item => item.Id)))
                {
                    presets.Items.Clear(); foreach (var item in controller.Items) presets.Items.Add(new ComboBoxItem { Tag = item.Id });
                }
                foreach (ComboBoxItem item in presets.Items) item.Content = controller.Items.First(preset => preset.Id == (string)item.Tag).Title is { Length: > 0 } savedTitle ? savedTitle : Key("unnamed");
                presets.SelectedItem = presets.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == controller.SelectedId);
                if (title.Text != controller.Title) title.Text = controller.Title;
                if (content.Text != controller.Content) content.Text = controller.Content;
                RestoreShortcut(shortcut, controller.Selected?.Shortcut ?? "");
                title.IsEnabled = content.IsEnabled = shortcut.IsEnabled = controller.Selected is not null && !controller.Busy;
                presets.IsEnabled = !controller.Busy; add.IsEnabled = settingsReady && controller.CanCreate; save.IsEnabled = controller.Dirty && !controller.Busy;
                up.IsEnabled = controller.CanMove("up"); down.IsEnabled = controller.CanMove("down"); delete.IsEnabled = controller.Selected is not null && !controller.Busy; refresh.IsEnabled = !controller.Busy;
                add.Content = Key("empty.action"); save.Content = Key("save"); up.Content = Key("moveUp"); down.Content = Key("moveDown"); delete.Content = Key("delete"); refresh.Content = Localization.Text("刷新预设", "Refresh presets");
                string sendLabel = phase == "in-game" ? Key("sendToGame") : phase is "lobby" or "champ-select" ? Key("sendToChat") : Key("send");
                send.Content = sendLabel; sendLines.Content = sendLabel;
                string? disabled = FixedTextPresetController.SendDisabledReason(phase, nativeAvailable, controller.Selected is not null, controller.Dirty, controller.Busy);
                send.IsEnabled = disabled is null; hint.Text = disabled == "nativeInput" ? NativeMessage() : disabled == "busy" ? Localization.Text("处理中…", "Working…") : disabled is null ? "" : Key("disabled." + disabled);
                sendLines.IsEnabled = !sendingLines && text.Text.Split('\n').Any(line => !string.IsNullOrWhiteSpace(line)) && FixedTextPresetController.SendDisabledReason(phase, nativeAvailable, true, false, false) is null;
                progress.IsActive = controller.Busy || sendingLines; progress.Visibility = progress.IsActive ? Visibility.Visible : Visibility.Collapsed;
                footer.Text = (elevated ? "" : NativeMessage() + "\n") + Localization.Key("toolkit.inGameSend.presets.footer.shortcutUsage") + (phase == "draft" ? "\n" + Localization.Key("toolkit.inGameSend.presets.footer.draftOnly") : "");
                if (controller.Error is { } failure) { error.Title = Localization.Text("固定文本预设操作失败", "Fixed text preset failed"); error.Message = failure.Message == "send-unavailable" ? Localization.Text("当前无可发送目标，或游戏窗口未在前台", "No send target is available, or the game window is not in the foreground") : failure.Message; error.IsOpen = true; }
                else if (controller.Busy) error.IsOpen = false;
            }
            finally { applying = false; }
        }
        async Task<(string Phase, bool NativeAvailable)> ReadStatus()
        {
            int version = lifecycle;
            var ongoing = await _backend.StateAsync("ongoing-game-main");
            var app = await _backend.StateAsync("app-common-main");
            string currentPhase = ongoing.Field("draft").ValueKind == JsonValueKind.Object ? "draft" : ongoing.Field("queryStage").Text("phase", "none"); var native = app.Field("nativeSupport").Field("nativeInput");
            if (loaded && version == lifecycle)
            {
                phase = currentPhase; nativeAvailable = native.Boolean("available"); elevated = app.Boolean("isElevated");
                nativeReason = !native.Boolean("availableOnCurrentPlatform") ? "unsupported" : native.Boolean("requiresElevation") && !elevated ? "needAdmin" : "unavailable"; Apply();
            }
            return (currentPhase, native.Boolean("available"));
        }
        async Task RefreshStatus()
        {
            if (!loaded) return;
            if (statusBusy) { statusPending = true; return; }
            statusBusy = true;
            try { do { statusPending = false; await ReadStatus(); } while (loaded && statusPending); }
            catch (Exception failure) { if (loaded) { phase = "none"; nativeAvailable = false; Apply(); error.Title = Localization.Text("读取发送状态失败", "Failed to read send status"); error.Message = failure.Message; error.IsOpen = true; } }
            finally { statusBusy = false; }
        }
        async Task Settings()
        {
            int version = lifecycle, request = ++settingsRead; var settings = await _backend.StateAsync("in-game-send-main", "settings");
            if (!loaded || version != lifecycle || request != settingsRead) return;
            applying = true; interval.Value = settings.Number("sendInterval", 65); RestoreShortcut(cancelShortcut, settings.Text("cancelShortcut")); applying = false;
            settingsReady = true; controller.ApplyItems(settings.Field("fixedTextPresetItems"));
        }
        async Task Complete(Task<bool> operation, string success)
        {
            if (await operation && loaded) _status.Text = success;
        }
        async Task<bool> FinishBlurSave() => blurSave is null || await blurSave;
        async Task SaveOnBlur()
        {
            if (!loaded || !controller.Dirty || controller.Busy) return;
            var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot);
            // Toolbar and preset changes save inside their own operation so one click completes it.
            if (new object[] { presets, add, save, up, down, delete, shortcut }.Any(control => ReferenceEquals(focused, control))) return;
            var task = controller.SaveAsync(); blurSave = task;
            try { await Complete(task, Key("saved")); }
            finally { if (ReferenceEquals(blurSave, task)) blurSave = null; }
        }
        text.TextChanged += (_, _) => Apply();
        sendLines.Click += async (_, _) =>
        {
            sendingLines = true; Apply(); int version = lifecycle;
            try
            {
                var status = await ReadStatus(); if (!loaded || version != lifecycle || FixedTextPresetController.SendDisabledReason(status.Phase, status.NativeAvailable, true, false, false) is not null) return;
                var sent = await _backend.CallAsync("in-game-send-main", "sendLines", (object)text.Text.Replace("\r", "").Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).ToArray());
                if (loaded && version == lifecycle) { if (sent.ValueKind != JsonValueKind.True) throw new InvalidOperationException(Localization.Text("当前无可发送目标，或游戏窗口未在前台", "No send target is available, or the game window is not in the foreground")); _status.Text = Key("sendSucceeded"); }
            }
            catch (Exception failure) { if (loaded && version == lifecycle) { error.Title = Localization.Text("发送失败", "Send failed"); error.Message = failure.Message; error.IsOpen = true; } }
            finally { if (loaded && version == lifecycle) { sendingLines = false; Apply(); } }
        };
        interval.ValueChanged += async (_, _) =>
        {
            if (applying || !loaded || double.IsNaN(interval.Value)) return;
            double value = Math.Clamp(interval.Value, 10, 3500);
            try { await _backend.CallAsync("setting-factory-main", "set", "in-game-send-main", "sendInterval", value); }
            catch (Exception failure) { _status.Text = failure.Message; }
        };
        cancelShortcut.SelectionChanged += async (_, _) => { if (applying || !loaded || !elevated) return; try { await _backend.CallAsync("setting-factory-main", "set", "in-game-send-main", "cancelShortcut", (cancelShortcut.SelectedItem as ComboBoxItem)?.Tag); } catch (Exception failure) { _status.Text = failure.Message; } };
        presets.SelectionChanged += async (_, _) => { if (applying || presets.SelectedItem is not ComboBoxItem { Tag: string id }) return; if (await FinishBlurSave()) await controller.SelectAsync(id); Apply(); };
        title.TextChanged += (_, _) => { if (!applying) controller.Edit(title.Text, content.Text); };
        content.TextChanged += (_, _) => { if (!applying) controller.Edit(title.Text, content.Text); };
        title.LostFocus += async (_, _) => await SaveOnBlur();
        content.LostFocus += async (_, _) => await SaveOnBlur();
        title.KeyDown += async (_, args) => { if (args.Key == global::Windows.System.VirtualKey.Enter) { args.Handled = true; await Complete(controller.SaveAsync(), Key("saved")); } };
        shortcut.SelectionChanged += async (_, _) => { if (!applying && await FinishBlurSave()) await Complete(controller.SetShortcutAsync((shortcut.SelectedItem as ComboBoxItem)?.Tag as string), Key("saved")); };
        add.Click += async (_, _) => { if (await FinishBlurSave() && await controller.CreateAsync()) { _status.Text = Key("saved"); title.Focus(FocusState.Programmatic); } };
        save.Click += async (_, _) => { if (await FinishBlurSave()) await Complete(controller.SaveAsync(), Key("saved")); };
        send.Click += async (_, _) => await Complete(controller.SendAsync(ReadStatus), Key("sendSucceeded"));
        up.Click += async (_, _) => { if (await FinishBlurSave()) await controller.MoveAsync("up"); }; down.Click += async (_, _) => { if (await FinishBlurSave()) await controller.MoveAsync("down"); };
        delete.Click += async (_, _) => await Complete(controller.DeleteAsync(async item => await NativeDialogs.TryShowAsync(new ContentDialog { Title = Key("delete"), Content = Key("deleteConfirm") + " " + (item.Title.Length > 0 ? item.Title : Key("unnamed")), PrimaryButtonText = Key("delete"), CloseButtonText = Localization.Text("取消", "Cancel"), DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot }, () => loaded) == ContentDialogResult.Primary), Key("deleted"));
        refresh.Click += async (_, _) => await controller.RefreshAsync();
        controller.Changed += Apply;
        void Changed(JsonElement envelope)
        {
            string name = envelope.Text("name"); var args = envelope.Field("args").Items().ToArray();
            p.DispatcherQueue.TryEnqueue(async () =>
            {
                if (!loaded) return;
                if (name is "update-state-prop/ongoing-game-main:state" or "update-state-prop/app-common-main:state") { await RefreshStatus(); return; }
                if (name == "update-state-prop/in-game-send-main:settings" && args.Length >= 2)
                {
                    ++settingsRead;
                    string property = args[0].GetString() ?? "";
                    if (property == "fixedTextPresetItems") { settingsReady = true; controller.ApplyItems(args[1]); }
                    else { applying = true; if (property == "sendInterval") interval.Value = args[1].TryNumber(65); if (property == "cancelShortcut") RestoreShortcut(cancelShortcut, args[1].ValueKind == JsonValueKind.String ? args[1].GetString()! : ""); applying = false; }
                }
                else if (name == "in-game-send-main/shortcut-error" && args.Length >= 2)
                {
                    string target = args[0].GetString() ?? "";
                    if (target == "in-game-send-main/cancel" || target == "in-game-send-main/preset/fixed-text/" + controller.SelectedId) { error.Title = Localization.Text("快捷键注册失败", "Shortcut registration failed"); error.Message = args[1].ToString(); error.IsOpen = true; }
                }
            });
        }
        p.Loaded += async (_, _) => { loaded = true; ++lifecycle; controller.Activate(); Localization.Changed += Apply; _backend.EventReceived += Changed; Apply(); try { await Settings(); await RefreshStatus(); } catch (Exception failure) { if (loaded) _status.Text = failure.Message; } };
        p.Unloaded += (_, _) => { loaded = false; ++lifecycle; controller.Deactivate(); sendingLines = false; settingsReady = false; _backend.EventReceived -= Changed; Localization.Changed -= Apply; };
        p.Children.Add(text); p.Children.Add(sendLines); p.Children.Add(interval); p.Children.Add(cancelShortcut); p.Children.Add(heading); p.Children.Add(Row(add, refresh, progress)); p.Children.Add(error); p.Children.Add(presets); p.Children.Add(title); p.Children.Add(content); p.Children.Add(shortcut); p.Children.Add(Row(save, send, up, down, delete)); p.Children.Add(hint); p.Children.Add(footer);
        p.Children.Add(new Expander { Header = "战绩评价预设", Content = PresetTools("rating", "战绩评价", ("kda", "KDA"), ("winRate", "胜率"), ("avgSoloKills", "平均单杀"), ("avgVisionScore", "视野分"), ("avgChampionDamage", "英雄伤害"), ("avgDamageTaken", "承受伤害"), ("avgGold", "金币"), ("avgCsPerMinute", "每分钟补刀"), ("avgKillParticipation", "参团率"), ("avgDamageGoldEfficiency", "伤害金币效率"), ("mainChampions", "常用英雄"), ("mainPositions", "常用位置"), ("showCurrentChampion", "仅当前英雄统计")) });
        p.Children.Add(new Expander { Header = "打野分析预设", Content = PresetTools("jungle", "打野分析", ("activityPreference", "活动偏好"), ("firstClearDistribution", "首轮清野路线"), ("earlyGank", "早期抓人"), ("dragonControl", "小龙控制"), ("monsterControl", "野区控制"), ("mainChampions", "常用英雄"), ("showCurrentChampion", "仅当前英雄统计")) });
        p.Children.Add(new Expander { Header = "组队分析预设", Content = PresetTools("premade", "组队分析") });
        return p;
    }
}

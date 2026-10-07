using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LeagueAkari.WinUI.Services;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class ToolkitPage
{
    private StackPanel PresetTools(string kind, string title, params (string Key, string Label)[] fields)
    {
        var p = Panel(); var controller = new ToolkitPresetController(kind, (ns, method, args) => _backend.CallAsync(ns, method, args));
        var images = new NativeImages(_backend); bool loaded = false, applying = false; string? rosterFingerprint = null;
        string Key(string suffix, Dictionary<string, object?>? args = null) => Localization.Key("toolkit.inGameSend.presets." + suffix, arguments: args);
        var heading = Label(""); var description = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = true };
        var progress = new ProgressRing { Width = 18, Height = 18, IsActive = false, Visibility = Visibility.Collapsed };
        var refresh = new Button(); var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
        var targets = new Dictionary<string, (TextBlock Label, TextBlock Description, ComboBox Shortcut, Button Send, Button Preview)>();
        p.Children.Add(heading); p.Children.Add(description); p.Children.Add(Row(refresh, progress)); p.Children.Add(error);
        foreach (string target in ToolkitPresetController.Targets)
        {
            var label = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }; var details = new TextBlock { FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
            var shortcut = ShortcutChooser(""); var send = new Button { IsEnabled = false }; var dryRun = new Button { IsEnabled = false };
            var section = new StackPanel { Spacing = 6 }; section.Children.Add(label); section.Children.Add(details); section.Children.Add(Row(shortcut, send, dryRun)); p.Children.Add(section);
            targets[target] = (label, details, shortcut, send, dryRun);
            shortcut.SelectionChanged += async (_, _) => { if (!applying && loaded) await controller.SetShortcutAsync(target, (shortcut.SelectedItem as ComboBoxItem)?.Tag as string); };
            send.Click += async (_, _) =>
            {
                if (await controller.SendAsync(target) && loaded) _status.Text = Key("controls.sendSucceeded", new() { ["preset"] = Key(kind + ".label"), ["target"] = Key("targets." + target + ".label") });
            };
            dryRun.Click += async (_, _) => await controller.DryRunAsync(target);
        }
        p.Children.Add(hint);
        var previewPanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        var previewTitle = Label(""); var copy = new Button(); var close = new Button();
        var preview = new TextBox { AcceptsReturn = true, IsReadOnly = true, MinHeight = 80, MaxHeight = 280, TextWrapping = TextWrapping.Wrap };
        previewPanel.Children.Add(Row(previewTitle, copy, close)); previewPanel.Children.Add(preview); p.Children.Add(previewPanel);
        close.Click += (_, _) => controller.ClosePreview();
        copy.Click += (_, _) =>
        {
            try
            {
                string text = string.Join("\n", controller.Preview?.Lines ?? []); if (text.Length == 0) return;
                var package = new global::Windows.ApplicationModel.DataTransfer.DataPackage(); package.SetText(text); global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package); _status.Text = Key("preview.copied");
            }
            catch (Exception failure) { error.Title = Key("preview.copyFailed"); error.Message = failure.Message; error.IsOpen = true; }
        };
        var options = new Dictionary<string, (CheckBox Check, TextBlock Label, TextBlock Description, bool Config)>();
        var displayHeading = Label(""); var configHeading = Label("");
        void AddOption(string key, bool config)
        {
            var label = new TextBlock(); var details = new TextBlock { FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
            var text = new StackPanel { Spacing = 2 }; text.Children.Add(label); text.Children.Add(details);
            var check = new CheckBox { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch }; options[key] = (check, label, details, config); p.Children.Add(check);
            check.Click += async (_, _) => { if (!applying) await controller.UpdateOptionsAsync(new Dictionary<string, object?> { [key] = check.IsChecked == true }); };
        }
        if (kind != "premade")
        {
            p.Children.Add(displayHeading); foreach (string field in ToolkitPresetController.DisplayFields(kind)) AddOption(field, false);
            p.Children.Add(configHeading); AddOption("showCurrentChampion", true);
        }
        var names = new ComboBox { MinWidth = 260 }; var nameDescription = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.7 };
        foreach (string value in ToolkitPresetController.NameStrategies) names.Items.Add(new ComboBoxItem { Tag = value });
        names.SelectionChanged += async (_, _) => { if (!applying && names.SelectedItem is ComboBoxItem { Tag: string selected }) await controller.UpdateOptionsAsync(new Dictionary<string, object?> { ["nameDisplayStrategy"] = selected }); };
        p.Children.Add(names); p.Children.Add(nameDescription);
        var selectionHeading = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }; var all = new Button(); var clear = new Button();
        var selectionPanel = new StackPanel { Spacing = 8 }; var teams = new Grid { RowSpacing = 10, ColumnSpacing = 10 };
        var playerChecks = new Dictionary<string, CheckBox>(); var bucketChecks = new Dictionary<int, List<CheckBox>>(); var teamChecks = new Dictionary<string, (CheckBox Check, TextBlock Name)>();
        selectionPanel.Children.Add(selectionHeading); selectionPanel.Children.Add(Row(all, clear)); selectionPanel.Children.Add(teams); p.Children.Add(selectionPanel);
        all.Click += async (_, _) => await controller.SetAllAsync(true); clear.Click += async (_, _) => await controller.SetAllAsync(false);
        void LayoutTeams()
        {
            int columns = p.ActualWidth >= 640 ? 2 : 1;
            teams.ColumnDefinitions.Clear(); teams.RowDefinitions.Clear();
            for (int column = 0; column < columns; ++column) teams.ColumnDefinitions.Add(new());
            for (int row = 0; row < (teams.Children.Count + columns - 1) / columns; ++row) teams.RowDefinitions.Add(new() { Height = GridLength.Auto });
            for (int index = 0; index < teams.Children.Count; ++index) { Grid.SetRow((FrameworkElement)teams.Children[index], index / columns); Grid.SetColumn((FrameworkElement)teams.Children[index], index % columns); }
        }
        FrameworkElement Player(ToolkitPresetPlayer player, int index)
        {
            var icon = new Image { Width = 24, Height = 24 };
            string path = player.ChampionId.HasValue ? "/lol-game-data/assets/v1/champion-icons/" + Math.Max(0, player.ChampionId.Value) + ".png" : "/lol-game-data/assets/v1/profile-icons/" + player.ProfileIconId + ".jpg";
            _ = images.SetAsync(icon, path);
            var name = new TextBlock { Text = PrivatePlayer(JsonSerializer.SerializeToElement(new { puuid = player.Puuid, gameName = player.GameName, tagLine = player.TagLine }), index), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var row = new Grid { ColumnSpacing = 8 }; row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new()); row.Children.Add(icon); Grid.SetColumn(name, 1); row.Children.Add(name); return row;
        }
        void RenderTeams()
        {
            teams.Children.Clear(); playerChecks.Clear(); bucketChecks.Clear(); teamChecks.Clear(); int playerIndex = 0;
            foreach (var team in controller.Teams)
            {
                var section = new StackPanel { Spacing = 6, Padding = new Thickness(8) };
                var teamName = new TextBlock { TextWrapping = TextWrapping.Wrap };
                var teamSelect = new CheckBox { Content = teamName };
                teamChecks[team.Id] = (teamSelect, teamName);
                teamSelect.Click += async (_, _) =>
                {
                    if (applying) return;
                    bool wasAllSelected = kind == "premade" ? team.Buckets.All(bucket => controller.SelectedGroups.Contains(bucket.Index)) : team.Players.All(player => controller.SelectedPlayers.Contains(player.Puuid));
                    await controller.SetTeamAsync(team.Id, !wasAllSelected);
                }; section.Children.Add(teamSelect);
                if (kind == "premade")
                {
                    if (team.Buckets.Length == 0) section.Children.Add(new TextBlock { Text = Key("selection.emptyPremadeGroups"), Opacity = 0.6 });
                    foreach (var bucket in team.Buckets)
                    {
                        var members = new StackPanel { Spacing = 4 }; members.Children.Add(new TextBlock { Text = Key("selection.bucketSize", new() { ["count"] = bucket.Players.Length }), FontSize = 11 });
                        foreach (var player in bucket.Players) members.Children.Add(Player(player, playerIndex++));
                        var check = new CheckBox { Content = members, HorizontalAlignment = HorizontalAlignment.Stretch };
                        if (!bucketChecks.TryGetValue(bucket.Index, out var checks)) bucketChecks[bucket.Index] = checks = []; checks.Add(check);
                        check.Click += async (_, _) => { if (!applying) await controller.SetBucketAsync(bucket.Index, check.IsChecked == true); }; section.Children.Add(check);
                    }
                }
                else foreach (var player in team.Players)
                {
                    var check = new CheckBox { Content = Player(player, playerIndex++), HorizontalAlignment = HorizontalAlignment.Stretch }; playerChecks[player.Puuid] = check;
                    check.Click += async (_, _) => { if (!applying) await controller.SetPlayerAsync(player.Puuid, check.IsChecked == true); }; section.Children.Add(check);
                }
                teams.Children.Add(section);
            }
            LayoutTeams();
        }
        void UpdateTeams()
        {
            string fingerprint = JsonSerializer.Serialize(new { controller.Teams, Locale = Localization.Locale, Streamer = NativeAppearance.Current?.StreamerMode });
            if (rosterFingerprint != fingerprint) { rosterFingerprint = fingerprint; RenderTeams(); }
            bool editable = controller.Ready && !controller.Busy && !controller.Refreshing;
            foreach (var (id, check) in playerChecks) { check.IsChecked = controller.SelectedPlayers.Contains(id); check.IsEnabled = editable; }
            foreach (var (index, checks) in bucketChecks) foreach (var check in checks) { check.IsChecked = controller.SelectedGroups.Contains(index); check.IsEnabled = editable; }
            foreach (var team in controller.Teams)
            {
                var (check, label) = teamChecks[team.Id];
                int selected = kind == "premade" ? team.Buckets.Count(bucket => controller.SelectedGroups.Contains(bucket.Index)) : team.Players.Count(player => controller.SelectedPlayers.Contains(player.Puuid));
                int total = kind == "premade" ? team.Buckets.Length : team.Players.Length;
                string teamName = Localization.Key("common.teams." + team.Id, team.Id);
                string name = team.Friendly is null ? teamName : Key("teams.labelWithName", new() { ["side"] = Key(team.Friendly.Value ? "teams.friendly" : "teams.enemy"), ["team"] = teamName });
                label.Text = name + " (" + selected + "/" + total + ")"; check.IsChecked = selected == 0 ? false : selected == total ? true : null; check.IsEnabled = editable && total > 0;
            }
        }
        void Apply()
        {
            if (!loaded || applying) return;
            applying = true;
            try
            {
                heading.Text = Key(kind + ".label"); description.Text = Key(kind + ".description"); refresh.Content = Localization.Text("刷新预设配置与对局玩家", "Refresh presets and players"); refresh.IsEnabled = !controller.Refreshing && !controller.Busy;
                progress.IsActive = controller.Refreshing || controller.Busy; progress.Visibility = progress.IsActive ? Visibility.Visible : Visibility.Collapsed;
                foreach (var (id, controls) in targets)
                {
                    controls.Label.Text = Key("targets." + id + ".label"); controls.Description.Text = Key("targets." + id + ".description");
                    if (controls.Shortcut.Header is StackPanel header && header.Children[0] is TextBlock shortcutTitle) shortcutTitle.Text = Key("controls.shortcut");
                    RestoreShortcut(controls.Shortcut, controller.Options.Field("targetShortcuts").Text(id)); controls.Shortcut.IsEnabled = controller.Ready && !controller.Busy && !controller.Refreshing && controller.NativeAvailable;
                    ToolTipService.SetToolTip(controls.Shortcut, controller.NativeAvailable ? "" : Key("nativeInput." + controller.NativeUnavailableReason));
                    controls.Send.Content = Key("controls." + (controller.Phase == "in-game" ? "sendToGame" : controller.Phase is "lobby" or "champ-select" ? "sendToChat" : "send")); controls.Send.IsEnabled = controller.SendDisabledReason is null;
                    controls.Preview.Content = Key("controls.dryRun"); controls.Preview.IsEnabled = controller.Ready && !controller.Busy && !controller.Refreshing; ToolTipService.SetToolTip(controls.Preview, Key("controls.dryRunDescription"));
                }
                string? disabled = controller.SendDisabledReason;
                hint.Text = disabled == "nativeInput" ? Key("nativeInput." + controller.NativeUnavailableReason) : disabled == "busy" ? Localization.Text("处理中…", "Working…") : disabled == "loading" ? Localization.Text("加载预设…", "Loading presets…") : disabled is null ? "" : Key("controls.disabled." + disabled);
                previewPanel.Visibility = controller.Preview is not null || controller.PreviewLoading ? Visibility.Visible : Visibility.Collapsed;
                previewTitle.Text = Key("preview.title"); copy.Content = Key("preview.copy"); close.Content = Key("preview.close"); copy.IsEnabled = !controller.PreviewLoading && controller.Preview?.Lines.Length > 0;
                preview.Text = controller.PreviewLoading ? Localization.Text("正在生成预览…", "Generating preview…") : controller.Preview is { Lines.Length: > 0 } result ? string.Join("\n", result.Lines.Select((line, index) => (index + 1) + "  " + line)) : Key("preview.empty");
                displayHeading.Text = Key(kind + ".displayOptionsTitle"); configHeading.Text = Key(kind + ".configOptionsTitle");
                foreach (var (key, option) in options)
                {
                    string prefix = kind + "." + (option.Config ? "configOptions." : "displayOptions.") + key;
                    option.Label.Text = Key(prefix + ".label"); option.Description.Text = Key(prefix + ".description"); option.Check.IsChecked = controller.Options.Boolean(key); option.Check.IsEnabled = controller.Ready && !controller.Busy && !controller.Refreshing;
                }
                names.Header = Key("nameDisplayStrategy.title"); names.IsEnabled = controller.Ready && !controller.Busy && !controller.Refreshing;
                foreach (ComboBoxItem item in names.Items) item.Content = Key("nameDisplayStrategy.options." + item.Tag + ".label");
                names.SelectedItem = names.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == controller.Options.Text("nameDisplayStrategy"));
                nameDescription.Text = names.SelectedItem is ComboBoxItem selectedName ? Key("nameDisplayStrategy.options." + selectedName.Tag + ".description") : "";
                selectionPanel.Visibility = controller.TotalCount > 0 ? Visibility.Visible : Visibility.Collapsed;
                selectionHeading.Text = Key(kind == "premade" ? "selection.premadeTitle" : "selection.playersTitle", new() { ["selected"] = controller.SelectedCount, ["total"] = kind == "premade" ? controller.TotalGroupCount : controller.TotalCount });
                all.Content = Key("selection.selectAll"); clear.Content = Key("selection.clear"); all.IsEnabled = clear.IsEnabled = controller.Ready && !controller.Busy && !controller.Refreshing && (kind != "premade" || controller.TotalGroupCount > 0);
                UpdateTeams();
                if (controller.Error is { } failure)
                {
                    error.Title = Localization.Text("预设操作失败", "Preset operation failed");
                    error.Message = failure.Message switch
                    {
                        "preset-send-unavailable" => hint.Text,
                        "preset-send-rejected" => Localization.Text("当前没有可发送的内容或目标，或游戏窗口未在前台", "No sendable content or target is available, or the game window is not in the foreground"),
                        "preset-shortcut-unavailable" => Key("nativeInput." + controller.NativeUnavailableReason),
                        "preset-shortcut-reserved" => Localization.Key("settings.shortcutSelector.reservedShortcut"),
                        "preset-shortcut-occupied" => Localization.Key("settings.shortcutSelector.beingOccupied"), _ => failure.Message
                    }; error.IsOpen = true;
                }
                else if (controller.RefreshError is { } refreshError) { error.Title = Localization.Text("读取预设失败", "Failed to load presets"); error.Message = refreshError.Message; error.IsOpen = true; }
                else if (controller.ShortcutError is { } shortcutError) { error.Title = Localization.Text("快捷键注册失败", "Shortcut registration failed"); error.Message = shortcutError; error.IsOpen = true; }
                else if (controller.Busy || controller.Refreshing) error.IsOpen = false;
            }
            finally { applying = false; }
        }
        refresh.Click += async (_, _) => await controller.RefreshAsync(); p.SizeChanged += (_, _) => LayoutTeams(); controller.Changed += Apply;
        void Event(JsonElement envelope) => p.DispatcherQueue.TryEnqueue(() => { if (loaded) controller.ApplyEvent(envelope); });
        void AppearanceChanged() { rosterFingerprint = null; Apply(); }
        p.Loaded += async (_, _) => { loaded = true; controller.Activate(); _backend.EventReceived += Event; Localization.Changed += Apply; if (NativeAppearance.Current is { } appearance) appearance.Changed += AppearanceChanged; Apply(); await controller.RefreshAsync(); };
        p.Unloaded += (_, _) => { loaded = false; controller.Deactivate(); _backend.EventReceived -= Event; Localization.Changed -= Apply; if (NativeAppearance.Current is { } appearance) appearance.Changed -= AppearanceChanged; };
        return p;
    }
    private static ComboBox ShortcutChooser(string title)
    {
        var c = new ComboBox { MinWidth = 260 }; var record = new Button { FontSize = 11 }; var header = Row(new TextBlock { Text = title }, record); c.Header = header; bool recording = false;
        void Translate() { record.Content = recording ? Localization.Text("按组合键（Esc 取消）", "Press a shortcut (Esc cancels)") : Localization.Text("录制", "Record"); if (c.Items.Count > 0) ((ComboBoxItem)c.Items[0]).Content = Localization.Text("不设置", "Unset"); }
        record.Click += (_, _) => { recording = true; Translate(); record.Focus(FocusState.Programmatic); };
        record.KeyDown += (_, args) =>
        {
            if (!recording) return; args.Handled = true;
            if (args.Key == global::Windows.System.VirtualKey.Escape) { recording = false; Translate(); return; }
            if (args.Key is global::Windows.System.VirtualKey.Control or global::Windows.System.VirtualKey.Shift or global::Windows.System.VirtualKey.Menu or global::Windows.System.VirtualKey.LeftWindows or global::Windows.System.VirtualKey.RightWindows) return;
            string value = "";
            foreach (var (key, name) in new[] { (global::Windows.System.VirtualKey.Control, "Control"), (global::Windows.System.VirtualKey.Shift, "Shift"), (global::Windows.System.VirtualKey.Menu, "Alt"), (global::Windows.System.VirtualKey.LeftWindows, "LeftMeta"), (global::Windows.System.VirtualKey.RightWindows, "RightMeta") })
                if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down)) value += name + "+";
            int code = (int)args.Key;
            value += code is >= 48 and <= 57 ? ((char)code).ToString() : code is >= 96 and <= 105 ? "Numpad" + (code - 96) : code switch { 37 => "LeftArrow", 38 => "UpArrow", 39 => "RightArrow", 40 => "DownArrow", 20 => "CapsLock", 144 => "NumLock", 145 => "ScrollLock", 106 => "NumpadMultiply", 107 => "NumpadPlus", 109 => "NumpadMinus", 110 => "NumpadDot", 111 => "NumpadDivkeyIde", 186 => "Semicolon", 187 => "Equals", 188 => "Comma", 189 => "Minus", 190 => "Dot", 191 => "ForwardSlash", 192 => "Backtick", 219 => "OpenBracket", 220 => "Backslash", 221 => "CloseBracket", 222 => "Quote", _ => args.Key.ToString() };
            RestoreShortcut(c, value); recording = false; Translate();
        };
        c.Items.Add(new ComboBoxItem { Content = "不设置", Tag = null });
        foreach (var modifier in new[] { "", "Control+", "Alt+", "Shift+", "Control+Shift+", "Control+Alt+" }) foreach (var key in Enumerable.Range(1, 12).Select(i => "F" + i).Concat(Enumerable.Range(65, 26).Select(i => ((char)i).ToString()))) c.Items.Add(new ComboBoxItem { Content = modifier + key, Tag = modifier + key });
        c.Loaded += (_, _) => { Localization.Changed += Translate; Translate(); }; c.Unloaded += (_, _) => { Localization.Changed -= Translate; recording = false; };
        c.SelectedIndex = 0; Translate(); return c;
    }
}



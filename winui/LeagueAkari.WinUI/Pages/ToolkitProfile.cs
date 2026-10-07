using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LeagueAkari.WinUI.Services;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class ToolkitPage
{
    private StackPanel ProfileTools()
    {
        var p = Panel(); p.Children.Add(Label("在线状态与签名"));
        var availability = new ComboBox { MinWidth = 180, IsEnabled = false };
        var availabilityValues = new[] { "chat", "mobile", "away", "offline", "dnd", "spectating", "online" };
        var availabilityFallbacks = new[] { "Chat", "Online group", "Away", "Offline", "In game", "Spectating", "Online" };
        foreach (var value in availabilityValues) availability.Items.Add(new ComboBoxItem { Tag = value });
        void TranslateAvailability()
        {
            for (int index = 0; index < availabilityValues.Length; index++)
                ((ComboBoxItem)availability.Items[index]).Content = Localization.Key("toolkit.chatAvailability.availability.radio." + availabilityValues[index], availabilityFallbacks[index]);
        }
        void ShowAvailability(JsonElement me)
        {
            availability.IsEnabled = me.ValueKind == JsonValueKind.Object;
            availability.SelectedItem = availability.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == me.Text("availability"));
        }
        TranslateAvailability();
        var offline = new CheckBox { Content = "持续锁定离线状态" };
        p.Children.Add(Row(availability, Action("应用状态", async () =>
        {
            if (!availability.IsEnabled || availability.SelectedItem is not ComboBoxItem { Tag: string selected })
                throw new InvalidOperationException(Localization.Text("当前聊天状态不可用", "Chat availability is unavailable"));
            var settings = await _backend.StateAsync("auto-misc-main", "settings");
            if ((selected is "chat" or "away") && settings.Boolean("lockOfflineStatus"))
            {
                await Set("auto-misc-main", "lockOfflineStatus", false);
                offline.IsChecked = false;
            }
            await Lcu("PUT", "/lol-chat/v1/me", new { availability = selected });
        })));
        offline.Click += async (_, _) => { try { await Set("auto-misc-main", "lockOfflineStatus", offline.IsChecked == true); } catch (Exception ex) { _status.Text = ex.Message; } }; p.Children.Add(offline);
        bool observingAvailability = false;
        void AvailabilityEvent(JsonElement envelope)
        {
            string name = envelope.Text("name");
            if (name is not ("update-state-prop/auto-misc-main:settings" or "update-state-prop/league-client-main:chat")) return;
            var args = envelope.Field("args").Items().ToArray();
            if (args.Length < 2) return;
            string key = args[0].ValueKind == JsonValueKind.String ? args[0].GetString()! : "";
            p.DispatcherQueue.TryEnqueue(() =>
            {
                if (!observingAvailability) return;
                if (name == "update-state-prop/auto-misc-main:settings" && key == "lockOfflineStatus") offline.IsChecked = args[1].ValueKind == JsonValueKind.True;
                else if (name == "update-state-prop/league-client-main:chat" && key == "me") ShowAvailability(args[1]);
            });
        }
        p.Loaded += (_, _) => { observingAvailability = true; _backend.EventReceived -= AvailabilityEvent; _backend.EventReceived += AvailabilityEvent; Localization.Changed += TranslateAvailability; TranslateAvailability(); };
        p.Unloaded += (_, _) => { observingAvailability = false; _backend.EventReceived -= AvailabilityEvent; Localization.Changed -= TranslateAvailability; };
        _initializers.Add(async () => ShowAvailability((await _backend.StateAsync("league-client-main", "chat")).Field("me")));
        var signature = new TextBox { Header = "签名", AcceptsReturn = true, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, MaxLength = 2000 };
        p.Children.Add(signature); p.Children.Add(Action("保存并应用签名", async () => { await Set("auto-misc-main", "statusMessage", signature.Text); var state = await _backend.StateAsync("league-client-main"); if (state.Text("connectionState") == "connected") await _backend.CallAsync("auto-misc-main", "applyStatusMessage", signature.Text); }));
        var lockSignature = new CheckBox { Content = "持续锁定签名" }; lockSignature.Click += async (_, _) => { try { await Set("auto-misc-main", "autoSetStatusMessageEnabled", lockSignature.IsChecked == true); } catch (Exception ex) { _status.Text = ex.Message; } }; p.Children.Add(lockSignature);
        p.Children.Add(Label("聊天段位展示"));
        var queue = Choose("RANKED_SOLO_5x5", "RANKED_FLEX_SR", "RANKED_TFT"); var tier = Choose("IRON", "BRONZE", "SILVER", "GOLD", "PLATINUM", "EMERALD", "DIAMOND", "MASTER", "GRANDMASTER", "CHALLENGER", "UNRANKED"); var division = Choose("I", "II", "III", "IV");
        p.Children.Add(Row(queue, tier, division)); p.Children.Add(Action("保存并应用展示段位", async () => { var config = new { queue = queue.SelectedItem, tier = tier.SelectedItem, division = division.SelectedItem }; await Set("auto-misc-main", "rankedStatus", config); var state = await _backend.StateAsync("league-client-main"); if (state.Text("connectionState") == "connected") await _backend.CallAsync("auto-misc-main", "applyRankedStatus", config); }));
        var lockRank = new CheckBox { Content = "持续锁定展示段位" }; lockRank.Click += async (_, _) => { try { await Set("auto-misc-main", "autoSetRankedStatusEnabled", lockRank.IsChecked == true); } catch (Exception ex) { _status.Text = ex.Message; } }; p.Children.Add(lockRank);
        _initializers.Add(async () => { var settings = await _backend.StateAsync("auto-misc-main", "settings"); signature.Text = settings.Text("statusMessage"); offline.IsChecked = settings.Boolean("lockOfflineStatus"); lockSignature.IsChecked = settings.Boolean("autoSetStatusMessageEnabled"); lockRank.IsChecked = settings.Boolean("autoSetRankedStatusEnabled"); var rank = settings.Field("rankedStatus"); queue.SelectedItem = rank.Text("queue", "RANKED_SOLO_5x5"); tier.SelectedItem = rank.Text("tier", "CHALLENGER"); division.SelectedItem = rank.Text("division", "I"); });
        p.Children.Add(BackgroundTools());
        p.Children.Add(Label("资料装饰"));
        p.Children.Add(Action("设置资料横幅", async () => await Lcu("POST", "/lol-challenges/v1/update-player-preferences/", new { bannerAccent = "2" })));
        p.Children.Add(Action("移除等级边框", async () => { var regalia = await Lcu("GET", "/lol-regalia/v2/current-summoner/regalia"); await Lcu("PUT", "/lol-regalia/v2/current-summoner/regalia", new { preferredCrestType = "prestige", preferredBannerType = regalia.Text("bannerType"), selectedPrestigeCrest = 22 }); }));
        p.Children.Add(Action("移除挑战勋章", async () => { var me = await Lcu("GET", "/lol-chat/v1/me"); await Lcu("POST", "/lol-challenges/v1/update-player-preferences/", new { challengeIds = Array.Empty<int>(), bannerAccent = me.Field("lol").Field("bannerIdSelected") }); }));
        p.Children.Add(Action("清空表情配置", async () => { if (!await Confirm("清空表情", "清空账号的表情轮盘及触发表情？")) return; var account = (await Lcu("GET", "/lol-loadouts/v4/loadouts/scope/account")).Items().FirstOrDefault(); var emotes = new[] { "EMOTES_ACE", "EMOTES_FIRST_BLOOD", "EMOTES_VICTORY", "EMOTES_WHEEL_CENTER", "EMOTES_WHEEL_UPPER", "EMOTES_WHEEL_RIGHT", "EMOTES_WHEEL_UPPER_RIGHT", "EMOTES_WHEEL_UPPER_LEFT", "EMOTES_WHEEL_LOWER", "EMOTES_WHEEL_LEFT", "EMOTES_WHEEL_LOWER_RIGHT", "EMOTES_WHEEL_LOWER_LEFT", "EMOTES_START" }.ToDictionary(key => key, key => new { inventoryType = "EMOTE", itemId = -1 }); await Lcu("PATCH", "/lol-loadouts/v4/loadouts/" + account.Text("id"), new { loadout = emotes }); }));
        return p;
    }
    private StackPanel Friends()
    {
        var p = Panel(); var controller = new FriendToolsController((ns, method, args) => _backend.CallAsync(ns, method, args));
        var images = new NativeImages(_backend);
        bool loaded = false, rendering = false, selecting = false; int lifecycle = 0, connectionRead = 0;
        string? subscription = null; string server = "";
        var heading = Label(""); var filter = new TextBox { MinWidth = 260 };
        var list = new TreeView { SelectionMode = TreeViewSelectionMode.Multiple, MaxHeight = 600 };
        var refresh = new Button(); var delete = new Button(); var cancel = new Button { Visibility = Visibility.Collapsed }; var selectAll = new Button(); var clear = new Button();
        var progress = new ProgressRing { Width = 18, Height = 18, IsActive = false, Visibility = Visibility.Collapsed };
        var error = new InfoBar { IsClosable = true, Severity = InfoBarSeverity.Error }; var result = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var groupHeader = new TextBlock(); var lastHeader = new TextBlock(); var sinceHeader = new TextBlock();
        var columns = new Grid { ColumnSpacing = 16, Margin = new Thickness(48, 0, 0, 0) };
        columns.ColumnDefinitions.Add(new() { Width = new GridLength(240) }); columns.ColumnDefinitions.Add(new() { Width = new GridLength(210) }); columns.ColumnDefinitions.Add(new() { Width = new GridLength(210) });
        columns.Children.Add(groupHeader); Grid.SetColumn(lastHeader, 1); columns.Children.Add(lastHeader); Grid.SetColumn(sinceHeader, 2); columns.Children.Add(sinceHeader);
        string Key(string suffix, Dictionary<string, object?>? args = null) => Localization.Key("toolkit.friends." + suffix, arguments: args);
        string Date(Dictionary<string, DateTimeOffset> dates, string puuid, string fallback) => dates.TryGetValue(puuid, out var value)
            ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "\n(" + FriendToolsDate.Relative(value, DateTimeOffset.Now, Localization.IsEnglish) + ")" : Key(fallback);
        void Apply()
        {
            if (!loaded || rendering) return;
            heading.Text = Key("title"); filter.PlaceholderText = Key("searchPlaceholder");
            groupHeader.Text = Key("columns.groupName"); lastHeader.Text = Key("columns.lastGameDate"); sinceHeader.Text = Key("columns.friendSince");
            refresh.Content = Key("refreshButton"); refresh.IsEnabled = controller.Connected && !controller.Busy;
            delete.Content = controller.Selected.Count > 0 ? Key("deleteButtonC", new() { ["count"] = controller.Selected.Count }) : Key("deleteButton"); delete.IsEnabled = controller.Connected && !controller.Busy && controller.Selected.Count > 0;
            cancel.Content = Key("cancelButton"); cancel.Visibility = controller.Deleting ? Visibility.Visible : Visibility.Collapsed; cancel.IsEnabled = !controller.CancelRequested;
            selectAll.Content = Localization.Key("toolkit.inGameSend.presets.selection.selectAll"); clear.Content = Localization.Key("toolkit.inGameSend.presets.selection.clear"); selectAll.IsEnabled = clear.IsEnabled = controller.Connected && !controller.Busy;
            list.IsEnabled = controller.Connected && !controller.Busy; progress.IsActive = controller.Busy; progress.Visibility = controller.Busy ? Visibility.Visible : Visibility.Collapsed;
            if (controller.Error is { } failure) { error.Title = Localization.Text("好友操作失败", "Friend operation failed"); error.Message = failure.Message; error.IsOpen = true; }
            else if (controller.Busy) error.IsOpen = false;
            if (controller.DeleteOutcome is { } outcome) result.Text = Key("deleteSuccess", new() { ["count"] = outcome.Deleted }) + (outcome.Cancelled ? " · " + Localization.Text("已取消后续删除", "Remaining deletions cancelled") : "");
            else result.Text = controller.Connected ? "" : Localization.Text("客户端未连接", "Client disconnected");
        }
        void Render()
        {
            if (!loaded || rendering) return;
            rendering = true;
            try
            {
                var collapsed = list.RootNodes.Where(node => !node.IsExpanded).Select(node => ((FrameworkElement)node.Content).Tag).ToHashSet();
                list.RootNodes.Clear(); int index = 0;
                foreach (var group in controller.Groups(filter.Text))
                {
                    var root = new TreeViewNode { IsExpanded = !collapsed.Contains(group.Id), Content = new TextBlock { Text = Localization.Key("toolkit.friends.groupNames." + group.Name, group.Name), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Tag = group.Id } };
                    foreach (var friend in group.Friends)
                    {
                        string id = friend.Text("id"), puuid = friend.Text("puuid");
                        var row = new Grid { ColumnSpacing = 16, Tag = id, MinHeight = 44 };
                        row.ColumnDefinitions.Add(new() { Width = new GridLength(240) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(210) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(210) });
                        var icon = new Image { Width = 20, Height = 20 }; _ = images.SetAsync(icon, "/lol-game-data/assets/v1/profile-icons/" + friend.Number("icon") + ".jpg");
                        var name = new TextBlock { Text = PrivatePlayer(friend, index++), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 185 };
                        var link = new HyperlinkButton { Content = Row(icon, name), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
                        link.Click += (_, _) => { if (puuid.Length > 0) PlayerOpened?.Invoke(puuid, server); };
                        var recent = new TextBlock { Text = Date(controller.LastGames, puuid, "neverPlayed"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                        var since = new TextBlock { Text = Date(controller.Since, puuid, "unknown"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                        row.Children.Add(link); Grid.SetColumn(recent, 1); row.Children.Add(recent); Grid.SetColumn(since, 2); row.Children.Add(since);
                        root.Children.Add(new TreeViewNode { Content = row });
                    }
                    list.RootNodes.Add(root);
                    foreach (var node in root.Children) if (controller.Selected.Contains((string)((FrameworkElement)node.Content).Tag)) list.SelectedNodes.Add(node);
                }
            }
            finally { rendering = false; }
            Apply();
        }
        list.SelectionChanged += (_, _) =>
        {
            if (rendering) return;
            var visible = list.RootNodes.SelectMany(node => node.Children).Select(node => (string)((FrameworkElement)node.Content).Tag).ToHashSet();
            var selected = new HashSet<string>(controller.Selected.Where(id => !visible.Contains(id)));
            foreach (var node in list.SelectedNodes)
                if (((FrameworkElement)node.Content).Tag is string id) selected.Add(id);
                else foreach (var child in node.Children) selected.Add((string)((FrameworkElement)child.Content).Tag);
            selecting = true; try { controller.Select(selected); } finally { selecting = false; }
        };
        filter.TextChanged += (_, _) => Render(); controller.Changed += () => { if (selecting) Apply(); else Render(); }; _refreshPrivateContent += Render;
        selectAll.Click += (_, _) => controller.Select(controller.Selected.Concat(controller.Groups(filter.Text).SelectMany(group => group.Friends).Select(friend => friend.Text("id"))));
        clear.Click += (_, _) => controller.Select([]); cancel.Click += (_, _) => controller.CancelDelete();
        refresh.Click += async (_, _) => { if (await controller.RefreshAsync() && loaded) _status.Text = Key("refreshSuccess"); };
        delete.Click += async (_, _) => await controller.DeleteAsync(async count => await NativeDialogs.TryShowAsync(new ContentDialog
        {
            Title = Key("deleteButtonC", new() { ["count"] = count }), Content = Key("deletePopconfirm"), PrimaryButtonText = Key("deleteButton"), CloseButtonText = Localization.Text("取消", "Cancel"), DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot
        }, () => loaded && controller.Connected) == ContentDialogResult.Primary);
        async Task Connection(bool refreshIfConnected)
        {
            int version = lifecycle, request = ++connectionRead;
            try
            {
                var state = await _backend.StateAsync("league-client-main");
                if (!loaded || version != lifecycle || request != connectionRead) return;
                server = state.Field("auth").Text("rsoPlatformId", state.Field("auth").Text("platformId"));
                bool changed = controller.UpdateConnection(state);
                if (controller.Connected && (changed || refreshIfConnected)) await controller.RefreshAsync();
            }
            catch (Exception failure) { if (loaded && version == lifecycle && request == connectionRead) { error.Title = Localization.Text("读取好友状态失败", "Failed to read friend state"); error.Message = failure.Message; error.IsOpen = true; } }
        }
        void Changed(JsonElement envelope)
        {
            string name = envelope.Text("name"); var args = envelope.Field("args").Items().ToArray();
            p.DispatcherQueue.TryEnqueue(async () =>
            {
                if (!loaded) return;
                if (name == "update-state-prop/league-client-main:state" && args.Length >= 1 && args[0].GetString() is "connectionState" or "auth") await Connection(false);
                else if (name == "league-client-main/extra-lcu-event" && args.Length >= 3 && args[0].GetString() == subscription)
                    controller.ApplyFriendEvent(args[2].Text("id"), args[1].Text("eventType"), args[1].Field("data"));
            });
        }
        p.Loaded += async (_, _) =>
        {
            loaded = true; int version = ++lifecycle; controller.Activate(); Localization.Changed += Render; _backend.EventReceived += Changed; Apply();
            try
            {
                var id = await _backend.CallAsync("league-client-main", "subscribeLcuEndpoint", "/lol-chat/v1/friends/:id");
                string returned = id.GetString() ?? "";
                if (!loaded || version != lifecycle) { if (returned.Length > 0) await _backend.CallAsync("league-client-main", "unsubscribeLcuEndpoint", returned); return; }
                subscription = returned; await Connection(true);
            }
            catch (Exception failure) { if (loaded && version == lifecycle) { error.Title = Localization.Text("读取好友失败", "Failed to load friends"); error.Message = failure.Message; error.IsOpen = true; } }
        };
        p.Unloaded += async (_, _) =>
        {
            loaded = false; ++lifecycle; controller.Deactivate(); _backend.EventReceived -= Changed; Localization.Changed -= Render;
            string? previous = subscription; subscription = null;
            if (previous is { Length: > 0 }) try { await _backend.CallAsync("league-client-main", "unsubscribeLcuEndpoint", previous); } catch { }
        };
        p.Children.Add(heading); p.Children.Add(Row(delete, cancel, refresh, progress)); p.Children.Add(filter); p.Children.Add(error); p.Children.Add(columns); p.Children.Add(list); p.Children.Add(Row(selectAll, clear)); p.Children.Add(result);
        return p;
    }
}


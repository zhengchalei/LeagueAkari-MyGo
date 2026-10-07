using System.Text.Json;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class SettingsPage
{
    public event Action<string, string>? PlayerOpened;
    private UIElement Storage()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(SettingsForms.Section("设置文件",
            StorageFileAction("导出设置", "setting-factory-main", "exportSettingsToJsonFile"),
            StorageFileAction("导入设置", "setting-factory-main", "importSettingsFromJsonFile", async () => await _forms.Reload(), true)));
        var section = SettingsForms.Section("标记玩家");
        var search = new TextBox { Header = "搜索标记内容或 PUUID" };
        var current = new CheckBox { Content = "仅当前账户", IsChecked = true, IsEnabled = false };
        var reveal = new CheckBox { Content = "主播模式下显示玩家身份" };
        var size = SettingsForms.Select("每页数量", [("10", "10"), ("20", "20"), ("50", "50"), ("100", "100")], "20");
        var table = new StackPanel { Spacing = 8 };
        var mask = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var pager = new TextBlock();
        var previous = new Button { Content = "上一页", IsEnabled = false };
        var next = new Button { Content = "下一页", IsEnabled = false };
        var refresh = new Button { Content = "刷新" };
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        navigation.Children.Add(previous); navigation.Children.Add(next); navigation.Children.Add(pager);
        var images = new NativeImages(_backend);
        var identities = new Dictionary<(string Server, string Puuid), JsonElement>();
        int page = 1, total = 0, version = 0; bool loaded = false; bool previousStreamer = NativeAppearance.Current?.StreamerMode == true;
        bool Privacy() => NativeAppearance.Current?.StreamerMode == true && reveal.IsChecked != true;
        void ShowMask()
        {
            bool masked = Privacy(); table.Visibility = masked ? Visibility.Collapsed : Visibility.Visible;
            mask.Visibility = masked ? Visibility.Visible : Visibility.Collapsed;
            mask.Text = Localization.Key("settings.taggedPlayers.streamerModeWarning", "Player tags are hidden in streamer mode");
        }
        async Task<JsonElement> Identity(SavedTag row, string puuid, JsonElement sgp)
        {
            var key = (row.Server, puuid); if (identities.TryGetValue(key, out var cached)) return cached;
            if (sgp.Field("leagueServers").Field("servers").Field(row.Server).Text("common").Length == 0) return default;
            JsonElement player;
            if (row.Server == sgp.Field("availability").Text("sgpServerId"))
                player = await _backend.CallAsync("winui-backend", "lcuRequest", "GET", "/lol-summoner/v2/summoners/puuid/" + Uri.EscapeDataString(puuid), null);
            else
            {
                var profiles = await _backend.CallAsync("winui-backend", "sgpRequest", row.Server, "league-session", "POST", "/summoner-ledge/v1/regions/@akari:sgpServerSubId@/summoners/puuids", new[] { puuid });
                var profile = profiles.Items().FirstOrDefault(); if (profile.ValueKind != JsonValueKind.Object) return default;
                var names = await _backend.CallAsync("winui-backend", "riotRequest", "POST", "/player-account/lookup/v1/namesets-for-puuids", new { puuids = new[] { puuid } });
                var name = names.Field("namesets").Items().FirstOrDefault().Field("gnt");
                player = PlayerDataSource.NormalizeSummoner(profile, true, name.Text("gameName"), name.Text("tagLine"));
            }
            if (player.ValueKind == JsonValueKind.Object) identities[key] = player;
            return player;
        }
        FrameworkElement PlayerCell(SavedTag row, string puuid, JsonElement sgp, int request)
        {
            var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var avatar = new Image { Width = 20, Height = 20, Visibility = Visibility.Collapsed };
            var open = new HyperlinkButton { Content = "N/A (" + puuid[..Math.Min(8, puuid.Length)] + ")", IsEnabled = false, Padding = new Thickness(0) };
            ToolTipService.SetToolTip(open, puuid); cell.Children.Add(avatar); cell.Children.Add(open);
            open.Click += (_, _) => { if (!Privacy()) PlayerOpened?.Invoke(puuid, row.Server); };
            _ = Resolve(); return cell;
            async Task Resolve()
            {
                try
                {
                    var player = await Identity(row, puuid, sgp);
                    if (!loaded || request != version || player.ValueKind != JsonValueKind.Object) return;
                    open.Content = player.Text("gameName", player.Text("displayName")) + "#" + player.Text("tagLine"); open.IsEnabled = true;
                    avatar.Visibility = Visibility.Visible;
                    await images.SetAsync(avatar, "/lol-game-data/assets/v1/profile-icons/" + (int)player.Number("profileIconId") + ".jpg");
                }
                catch { /* The original table retains its PUUID fallback when a profile is unavailable. */ }
            }
        }
        async Task Reload()
        {
            if (!SettingsForms.TrySelected(size, out string selection) || !int.TryParse(selection, out int pageSize)) return;
            int request = ++version; refresh.IsEnabled = previous.IsEnabled = next.IsEnabled = false;
            try
            {
                var self = (await _backend.StateAsync("league-client-main", "summoner")).Field("me");
                if (request != version) return;
                current.IsEnabled = self.ValueKind == JsonValueKind.Object;
                string? puuid = current.IsChecked == true && current.IsEnabled ? self.Text("puuid") : null;
                var result = await _backend.CallAsync("saved-player-main", "getAllPlayerTags", new { page, pageSize, selfPuuid = puuid, search = search.Text });
                if (request != version) return;
                total = (int)result.Number("total"); int last = SavedTagData.LastPage(total, pageSize);
                if (page > last) { page = last; await Reload(); return; }
                var sgp = await _backend.StateAsync("sgp-main"); if (request != version) return;
                table.Children.Clear(); ShowMask();
                pager.Text = Localization.Text($"第 {page}/{last} 页 · {total} 条", $"Page {page}/{last} · {total} records");
                int ordinal = (page - 1) * pageSize;
                foreach (var row in SavedTagData.Read(result))
                {
                    var record = new StackPanel { Spacing = 6 };
                    record.Children.Add(new TextBlock { Text = $"#{++ordinal} · {Localization.Key("sgpServers." + row.Server, row.Server)}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                    record.Children.Add(new TextBlock { Text = Localization.Key("settings.taggedPlayers.columns.tagger", "Tagger") }); record.Children.Add(PlayerCell(row, row.SelfPuuid, sgp, request));
                    record.Children.Add(new TextBlock { Text = Localization.Key("settings.taggedPlayers.columns.tagged", "Tagged player") }); record.Children.Add(PlayerCell(row, row.Puuid, sgp, request));
                    record.Children.Add(new TextBlock { Text = row.Tag, TextWrapping = TextWrapping.Wrap });
                    var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    var edit = new Button { Content = Localization.Key("settings.taggedPlayers.editButton", "Edit") };
                    edit.Click += async (_, _) =>
                    {
                        edit.IsEnabled = false;
                        try
                        {
                            var text = new TextBox { Text = row.Tag, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90 };
                            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = Localization.Key("settings.taggedPlayers.editModal.title", "Edit tag"), Content = text, PrimaryButtonText = NativeFormText.Text("保存"), CloseButtonText = NativeFormText.Text("取消"), DefaultButton = ContentDialogButton.Close };
                            if (await NativeDialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
                            await _backend.CallAsync("saved-player-main", "updatePlayerTag", row.Update(text.Text)); await Reload();
                        }
                        catch (Exception ex) { _forms.Status.Text = ex.Message; }
                        finally { edit.IsEnabled = true; }
                    };
                    var remove = new Button { Content = NativeFormText.Text("删除") };
                    remove.Click += async (_, _) =>
                    {
                        remove.IsEnabled = false;
                        try
                        {
                            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = Localization.Key("settings.taggedPlayers.deleteButton", "Delete tag"), Content = Localization.Key("settings.taggedPlayers.deletePopconfirmContent", "Delete this player tag?"), PrimaryButtonText = NativeFormText.Text("删除"), CloseButtonText = NativeFormText.Text("取消"), DefaultButton = ContentDialogButton.Close };
                            if (await NativeDialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
                            await _backend.CallAsync("saved-player-main", "updatePlayerTag", row.Update(null)); await Reload();
                        }
                        catch (Exception ex) { _forms.Status.Text = ex.Message; }
                        finally { remove.IsEnabled = true; }
                    };
                    actions.Children.Add(edit); actions.Children.Add(remove); record.Children.Add(actions); table.Children.Add(record);
                }
            }
            catch (Exception ex) { if (request == version) _forms.Status.Text = Localization.Text("标记列表加载失败：", "Failed to load tags: ") + ex.Message; }
            finally { if (request == version) { refresh.IsEnabled = true; previous.IsEnabled = page > 1; next.IsEnabled = page < SavedTagData.LastPage(total, pageSize); } }
        }
        section.Children.Add(StorageFileAction("导出标记玩家", "saved-player-main", "exportTaggedPlayersToJsonFile"));
        section.Children.Add(StorageFileAction("导入标记玩家", "saved-player-main", "importTaggedPlayersFromJsonFile", Reload));
        search.TextChanged += (_, _) => { page = 1; }; refresh.Click += async (_, _) => await Reload();
        previous.Click += async (_, _) => { page = Math.Max(1, page - 1); await Reload(); }; next.Click += async (_, _) => { page++; await Reload(); };
        current.Click += async (_, _) => { page = 1; await Reload(); }; reveal.Click += async (_, _) => await Reload(); size.SelectionChanged += async (_, _) => { page = 1; await Reload(); };
        void Changed(JsonElement envelope)
        {
            string name = envelope.Text("name");
            if (name.Contains("sgp-main:state") || name.Contains("league-client-main:summoner"))
                section.DispatcherQueue.TryEnqueue(async () => { if (loaded) await Reload(); });
        }
        void LanguageChanged() => section.DispatcherQueue.TryEnqueue(async () => { if (loaded) await Reload(); });
        section.Loaded += async (_, _) => { loaded = true; _backend.EventReceived -= Changed; _backend.EventReceived += Changed; Localization.Changed += LanguageChanged; await Reload(); };
        section.Unloaded += (_, _) => { loaded = false; version++; _backend.EventReceived -= Changed; Localization.Changed -= LanguageChanged; };
        _refreshTags = async () =>
        {
            if (!loaded) return;
            bool streamer = NativeAppearance.Current?.StreamerMode == true;
            if (streamer != previousStreamer && streamer) reveal.IsChecked = false;
            previousStreamer = streamer; await Reload();
        };
        foreach (var control in new UIElement[] { search, current, reveal, size, refresh, mask, table, navigation }) section.Children.Add(control);
        panel.Children.Add(section); return panel;
    }
    private Button StorageFileAction(string label, string ns, string method, Func<Task>? after = null, bool confirm = false)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                if (confirm)
                {
                    var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = NativeFormText.Text(label), Content = Localization.Text("导入将覆盖当前配置。是否继续？", "Importing will overwrite the current settings. Continue?"), PrimaryButtonText = NativeFormText.Text("确认"), CloseButtonText = NativeFormText.Text("取消"), DefaultButton = ContentDialogButton.Close };
                    if (await NativeDialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
                }
                var result = await _backend.CallAsync(ns, method);
                if (result.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(result.GetString())) return;
                if (after is not null) await after();
                _forms.Status.Text = NativeFormText.Text(label) + ": " + result.GetString();
            }
            catch (Exception ex)
            {
                _forms.Status.Text = ex.Message.Contains("InvalidDatabaseVersion", StringComparison.Ordinal) ? Localization.Text("文件数据库版本不兼容", "The file database version is incompatible") :
                    ex.Message.Contains("InvalidTaggedPlayers", StringComparison.Ordinal) ? Localization.Text("无效的玩家标记文件", "Invalid player tag file") :
                    ex.Message.Contains("InvalidSettings", StringComparison.Ordinal) ? Localization.Text("无效的设置文件", "Invalid settings file") : ex.Message;
            }
            finally { button.IsEnabled = true; }
        };
        return button;
    }
}

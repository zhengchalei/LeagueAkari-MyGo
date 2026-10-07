using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LeagueAkari.WinUI.Services;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class ToolkitPage
{
    private StackPanel LobbyTools()
    {
        var panel = Panel(); panel.Children.Add(Label("创建匹配大厅"));
        var queue = new NumberBox { Header = "队列 ID", Minimum = 1, Maximum = int.MaxValue, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var search = new TextBox { PlaceholderText = "搜索模式" };
        var queues = new ComboBox { Header = "当前可加入的模式", MinWidth = 320 };
        var swarm = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed }; var unavailable = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var champions = new ComboBox { Header = "英雄", MinWidth = 240 }; var championSearch = new TextBox { PlaceholderText = "搜索英雄" };
        var maps = new ComboBox { Header = "地图", MinWidth = 300 }; var difficulty = new ComboBox { Header = "难度", MinWidth = 180 };
        foreach (var (text, value) in new[] { ("🐰", 1), ("★★", 2), ("★★★", 3) }) difficulty.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        ToolkitQueue[] queueOptions = []; SwarmChampion[] championOptions = [];
        JsonElement client = default, lobby = default; string accountLoadout = "";
        bool connected = false, loaded = false, initializing = false, initialized = false, readingQueues = false, writing = false, drawingQueues = false; int request = 0, connectionRevision = 0;
        var images = new NativeImages(_backend);
        var create = new Button { Content = "创建大厅", IsEnabled = false };
        var setChampion = new Button { Content = "应用英雄及难度", IsEnabled = false }; var setMap = new Button { Content = "应用地图", IsEnabled = false }; var setDifficulty = new Button { Content = "应用账号难度", IsEnabled = false };
        void Apply()
        {
            bool available = ToolkitLobbyData.IsSwarm(client, lobby);
            create.IsEnabled = connected && !writing && ToolkitLobbyData.ValidQueue(queue.Value);
            queues.IsEnabled = connected; swarm.Visibility = available ? Visibility.Visible : Visibility.Collapsed; unavailable.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
            unavailable.Text = Localization.Key("toolkit.strawberry.unavailable", "Currently not in Swarm mode");
            setChampion.IsEnabled = available && !writing && champions.SelectedItem is ComboBoxItem { Tag: SwarmChampion };
            setMap.IsEnabled = available && !writing && maps.SelectedItem is ComboBoxItem { Tag: SwarmMap };
            setDifficulty.IsEnabled = available && !writing && difficulty.SelectedItem is ComboBoxItem && accountLoadout.Length > 0;
        }
        void DrawQueues()
        {
            int? selected = queues.SelectedItem is ComboBoxItem { Tag: ToolkitQueue item } ? item.Id : null;
            try
            {
                drawingQueues = true; queues.Items.Clear();
                foreach (var option in queueOptions.Where(item => (item.Name + " " + item.Id).Contains(search.Text, StringComparison.CurrentCultureIgnoreCase)))
                {
                    var entry = new ComboBoxItem { Tag = option, Content = Localization.Key("toolkit.lobby.queueOptions." + (option.Eligible ? "available" : "unavailable"), option.Eligible ? "Available" : "Unavailable") + " · " + option.Name + " (" + option.Id + ")" };
                    queues.Items.Add(entry); if (selected == option.Id) queues.SelectedItem = entry;
                }
            }
            finally { drawingQueues = false; }
        }
        void DrawChampions()
        {
            int? previous = champions.SelectedItem is ComboBoxItem { Tag: SwarmChampion selectedHero } ? selectedHero.Id : null;
            champions.Items.Clear();
            foreach (var hero in championOptions.Where(hero => hero.Name.Contains(championSearch.Text, StringComparison.CurrentCultureIgnoreCase)))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; var icon = new Image { Width = 18, Height = 18 }; _ = images.SetAsync(icon, "/lol-game-data/assets/v1/champion-icons/" + hero.Id + ".png"); row.Children.Add(icon);
                row.Children.Add(new TextBlock { Text = Localization.Key("toolkit.strawberry.championOptions." + (hero.Specific ? "modeSpecific" : "other"), hero.Specific ? "Mode-specific" : "Other") + " · " + hero.Name });
                var entry = new ComboBoxItem { Content = row, Tag = hero }; champions.Items.Add(entry); if (hero.Id == previous) champions.SelectedItem = entry;
            }
            Apply();
        }
        async Task ReadQueues()
        {
            if (!connected || readingQueues) return; readingQueues = true; int version = connectionRevision;
            try
            {
                var data = await Task.WhenAll(Lcu("POST", "/lol-lobby/v2/eligibility/self"), Lcu("POST", "/lol-lobby/v2/eligibility/party"), _backend.StateAsync("league-client-main", "gameData"));
                if (!loaded || version != connectionRevision || !connected) return;
                queueOptions = ToolkitLobbyData.Queues(data[2].Field("queues"), data[0], data[1]); DrawQueues();
            }
            catch (Exception error) { _status.Text = error.Message; }
            finally { readingQueues = false; }
        }
        async Task InitializeSwarm()
        {
            if (!ToolkitLobbyData.IsSwarm(client, lobby) || initialized || initializing) return;
            initializing = true; int version = connectionRevision;
            try
            {
                var data = await Task.WhenAll(Lcu("GET", "/lol-game-data/assets/v1/strawberry-hub.json"), Lcu("GET", "/lol-loadouts/v4/loadouts/scope/account"));
                if (!loaded || version != connectionRevision || !ToolkitLobbyData.IsSwarm(client, lobby)) return;
                accountLoadout = data[1].Items().FirstOrDefault().Text("id");
                maps.Items.Clear(); foreach (var item in ToolkitLobbyData.Maps(data[0])) maps.Items.Add(new ComboBoxItem { Content = item.Name, Tag = item }); initialized = true; Apply();
            }
            catch (Exception error) { _status.Text = error.Message; }
            finally { initializing = false; }
        }
        async Task Refresh()
        {
            int version = ++request;
            var data = await Task.WhenAll(_backend.StateAsync("league-client-main"), _backend.StateAsync("league-client-main", "lobby"), _backend.StateAsync("league-client-main", "gameData"));
            if (!loaded || version != request) return;
            client = data[0]; lobby = data[1]; bool nextConnection = client.Text("connectionState") == "connected";
            if (nextConnection != connected) connectionRevision++;
            connected = nextConnection;
            if (!connected) { initialized = false; accountLoadout = ""; }
            championOptions = ToolkitLobbyData.Champions(data[2].Field("champions")); DrawChampions(); Apply();
        }
        async Task Write(Func<Task> action, bool requireSwarm)
        {
            if (writing) return; writing = true; Apply();
            try
            {
                var states = await Task.WhenAll(_backend.StateAsync("league-client-main"), _backend.StateAsync("league-client-main", "lobby"));
                client = states[0]; lobby = states[1]; connected = client.Text("connectionState") == "connected";
                if (!connected || requireSwarm && !ToolkitLobbyData.IsSwarm(client, lobby)) throw new InvalidOperationException(Localization.Text("当前大厅操作不可用", "The lobby operation is unavailable"));
                await action(); _status.Text = Localization.Key("toolkit.strawberry.requestSent", "Request sent");
            }
            catch (Exception error) { _status.Text = error.Message; }
            finally { writing = false; Apply(); }
        }
        queues.DropDownOpened += async (_, _) => await ReadQueues();
        queues.SelectionChanged += (_, _) => { if (!drawingQueues && queues.SelectedItem is ComboBoxItem { Tag: ToolkitQueue option }) queue.Value = option.Id; };
        queue.ValueChanged += (_, _) => Apply(); search.TextChanged += (_, _) => DrawQueues();
        championSearch.TextChanged += (_, _) => DrawChampions(); champions.SelectionChanged += (_, _) => Apply();
        maps.DropDownOpened += async (_, _) => await InitializeSwarm(); difficulty.DropDownOpened += async (_, _) => await InitializeSwarm(); maps.SelectionChanged += (_, _) => Apply(); difficulty.SelectionChanged += (_, _) => Apply();
        create.Click += async (_, _) => { double selected = queue.Value; if (!ToolkitLobbyData.ValidQueue(selected)) return; await Write(async () => await Lcu("POST", "/lol-lobby/v2/lobby", new { queueId = (int)selected }), false); };
        setChampion.Click += async (_, _) =>
        {
            if (champions.SelectedItem is not ComboBoxItem { Tag: SwarmChampion hero }) return;
            var map = (maps.SelectedItem as ComboBoxItem)?.Tag as SwarmMap; int? level = difficulty.SelectedItem is ComboBoxItem { Tag: int selected } ? selected : null;
            await Write(async () => await Lcu("PUT", "/lol-lobby/v1/lobby/members/localMember/player-slots", ToolkitLobbyData.Slots(hero.Id, map, level)), true);
        };
        setMap.Click += async (_, _) => { if (maps.SelectedItem is ComboBoxItem { Tag: SwarmMap map }) await Write(async () => await Lcu("PUT", "/lol-lobby/v2/lobby/strawberryMapId", new { contentId = map.ContentId, itemId = map.ItemId }), true); };
        setDifficulty.Click += async (_, _) => { if (difficulty.SelectedItem is ComboBoxItem { Tag: int level } && accountLoadout.Length > 0) { string id = accountLoadout; await Write(async () => await Lcu("PATCH", "/lol-loadouts/v4/loadouts/" + id, new { loadout = new { STRAWBERRY_DIFFICULTY = new { inventoryType = "STRAWBERRY_LOADOUT_ITEM", itemId = level } } }), true); } };
        void Changed(JsonElement envelope)
        {
            string name = envelope.Text("name");
            if (name.Contains("league-client-main:state") || name.Contains("league-client-main:lobby") || name.Contains("league-client-main:gameData"))
                panel.DispatcherQueue.TryEnqueue(async () => { if (!loaded) return; try { await Refresh(); } catch (Exception error) { _status.Text = error.Message; } });
        }
        void LocaleChanged() => panel.DispatcherQueue.TryEnqueue(() => { DrawQueues(); DrawChampions(); Apply(); });
        panel.Loaded += async (_, _) => { loaded = true; _backend.EventReceived -= Changed; _backend.EventReceived += Changed; Localization.Changed += LocaleChanged; try { await Refresh(); } catch (Exception error) { _status.Text = error.Message; } };
        panel.Unloaded += (_, _) => { loaded = false; request++; connectionRevision++; _backend.EventReceived -= Changed; Localization.Changed -= LocaleChanged; };
        panel.Children.Add(Action("读取可用模式", ReadQueues)); panel.Children.Add(search); panel.Children.Add(queues); panel.Children.Add(queue); panel.Children.Add(create);
        panel.Children.Add(Label("无尽狂潮")); panel.Children.Add(unavailable);
        foreach (var control in new UIElement[] { championSearch, champions, setChampion, maps, setMap, difficulty, setDifficulty }) swarm.Children.Add(control);
        panel.Children.Add(swarm); Apply(); return panel;
    }
}

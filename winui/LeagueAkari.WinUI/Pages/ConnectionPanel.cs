using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Text.Json;
using Windows.Storage.Streams;

namespace LeagueAkari.WinUI.Pages;

public sealed class ConnectionPanel : UserControl
{
    private readonly BackendClient _backend;
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _account = new() { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 190, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Image _portrait = new() { Width = 28, Height = 28, Stretch = Stretch.UniformToFill };
    private readonly ComboBox _clients = new() { Width = 250 };
    private readonly InfoBar _error = new() { IsClosable = true, Severity = InfoBarSeverity.Error };
    private readonly Button _refresh = new(), _connect = new(), _disconnect = new(), _restart = new(), _playAgain = new(), _details = new();
    private readonly DropDownButton _more = new();
    private readonly StackPanel _clientCards = new() { Spacing = 8 };
    private readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly Dictionary<int, (JsonElement Data, DateTimeOffset At)> _peeks = [];
    private ConnectionClient[] _otherClients = [];
    private JsonElement _state, _summoner, _gameflow, _sgp;
    private bool _loaded, _refreshing, _refreshPending, _actionBusy, _connecting, _cancelConnection, _peeking, _disconnecting;
    private int _revision, _pendingPid;
    private string _portraitKey = "";
    private readonly ContentControl _cardsContent = new();
    private string _errorPayload = "", _errorKind = "";
    private NativeAppearance? _appearance;

    public ConnectionPanel(BackendClient backend)
    {
        _backend = backend;
        var root = new StackPanel { Spacing = 5 }; var bar = new WrapPanel { Spacing = 7, Margin = new Thickness(12, 5, 12, 5) };
        foreach (var control in new UIElement[] { _portrait, _account, _status, _clients, _refresh, _connect, _disconnect, _details, _restart, _playAgain, _more }) bar.Children.Add(control);
        root.Children.Add(bar); root.Children.Add(_error); Content = root;
        _cardsContent.Content = _clientCards; _details.Flyout = new Flyout { Content = new ScrollViewer { Content = _cardsContent, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        _refresh.Click += async (_, _) => await RefreshAsync(); _connect.Click += async (_, _) => await ConnectSelectedAsync(); _disconnect.Click += async (_, _) => await DisconnectAsync();
        _restart.Click += async (_, _) => await RunAsync(() => Lcu("/riotclient/kill-and-restart-ux"));
        _playAgain.Click += async (_, _) => await RunAsync(() => Lcu("/lol-lobby/v2/play-again"));
        _clients.SelectionChanged += (_, _) => RenderControls();
        // Programmatic closes can complete after another request reports an error.
        // Only the user's close-button click dismisses the currently displayed message.
        _error.CloseButtonClick += (_, _) => { _errorKind = ""; _errorPayload = ""; };
        _peekTimer.Tick += async (_, _) => await PeekClientsAsync();
        Loaded += async (_, _) =>
        {
            _loaded = true; _revision++; Localization.Changed -= LanguageChanged; Localization.Changed += LanguageChanged;
            _backend.EventReceived -= Changed; _backend.EventReceived += Changed; _appearance = NativeAppearance.Current;
            if (_appearance != null) { _appearance.Changed -= AppearanceChanged; _appearance.Changed += AppearanceChanged; }
            Render(); _peekTimer.Start(); if (_backend.IsReady) await RefreshAsync();
        };
        Unloaded += (_, _) => { _loaded = false; _revision++; _peekTimer.Stop(); _backend.EventReceived -= Changed; Localization.Changed -= LanguageChanged; if (_appearance != null) _appearance.Changed -= AppearanceChanged; };
    }
    private static string Label(string key) => Localization.Key("leagueClient.connection." + key, key);
    private void LanguageChanged() => DispatcherQueue.TryEnqueue(() => { if (_loaded) Render(); });
    private void AppearanceChanged() => DispatcherQueue.TryEnqueue(() => { if (_loaded) Render(); });
    private void Changed(JsonElement envelope)
    {
        string name = envelope.Text("name");
        if (name is "update-state-prop/league-client-main:state" or "update-state-prop/league-client-main:summoner" or "update-state-prop/league-client-main:gameflow" or "update-state-prop/league-client-ux-main:state" or "update-state-prop/sgp-main:state")
            DispatcherQueue.TryEnqueue(async () => { if (_loaded) await RefreshAsync(); });
    }
    public async Task RefreshAsync()
    {
        if (!_backend.IsReady) return;
        if (_refreshing) { _refreshPending = true; return; }
        _refreshing = true; int revision = _revision; RenderControls();
        try
        {
            var state = await _backend.StateAsync("league-client-main"); var ux = await _backend.StateAsync("league-client-ux-main");
            var summoner = await _backend.StateAsync("league-client-main", "summoner"); var gameflow = await _backend.StateAsync("league-client-main", "gameflow"); var sgp = await _backend.StateAsync("sgp-main");
            if (!_loaded || revision != _revision) return;
            int previous = (_clients.SelectedItem as ComboBoxItem)?.Tag is int pid ? pid : 0;
            _state = state; _summoner = summoner; _gameflow = gameflow; _sgp = sgp; _otherClients = ConnectionData.OtherClients(state, ux);
            var chosen = ConnectionData.Choose(_otherClients, previous, ConnectionData.ConnectingPid(state)); RebindClients(chosen?.Pid ?? 0);
            foreach (int stale in _peeks.Keys.Where(id => _otherClients.All(client => client.Pid != id)).ToArray()) _peeks.Remove(stale);
            if (ux.Boolean("hasClientButNoCommandLine") && _errorKind.Length == 0) _errorKind = "discovery";
            else if (!ux.Boolean("hasClientButNoCommandLine") && _errorKind == "discovery") _errorKind = "";
            Render(); await PeekClientsAsync();
        }
        catch (Exception ex) { if (_loaded && revision == _revision) ShowError("refresh", ex.Message); }
        finally
        {
            _refreshing = false; RenderControls();
            if (_refreshPending) { _refreshPending = false; if (_loaded) await RefreshAsync(); }
        }
    }
    private void Render()
    {
        _refresh.Content = Localization.Text("刷新", "Refresh"); _connect.Content = Localization.Text("连接", "Connect"); _disconnect.Content = Label("disconnect");
        _restart.Content = Label("restartUx"); _playAgain.Content = Label("playAgain"); _details.Content = Localization.Text("客户端列表", "Client list"); _more.Content = Label("more");
        _clients.PlaceholderText = Localization.Text("选择 LOL 客户端", "Select a League client"); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_clients, _clients.PlaceholderText);
        bool connected = _state.Field("auth").ValueKind == JsonValueKind.Object; var auth = _state.Field("auth"); var me = _summoner.Field("me");
        _status.Text = _connecting || ConnectionData.ConnectingPid(_state) > 0 ? Label("connecting") : _state.Text("connectionState") switch { "connected" => "● " + Label("connectedGroup") + " · " + Server(auth), "connecting" => Label("connecting"), _ => "○ " + (_otherClients.Length > 0 ? Label("launchedClientsGroup") : Label("noClient")) };
        _account.Text = connected ? Account(me) : ""; _account.Visibility = _portrait.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        if (connected) { int pid = (int)auth.Number("pid"), icon = (int)me.Number("profileIconId", -1); string key = pid + ":" + icon; if (key != _portraitKey) { _portraitKey = key; _ = LoadPortraitAsync(_portrait, icon >= 0 ? $"/lol-game-data/assets/v1/profile-icons/{icon}.jpg" : "", pid); } } else { _portraitKey = ""; _portrait.Source = null; }
        if (_clients.Items.OfType<ComboBoxItem>().Any(item => item.Tag is int pid && _otherClients.FirstOrDefault(client => client.Pid == pid) is { } client && item.Content as string != ClientLabel(client)))
            RebindClients((_clients.SelectedItem as ComboBoxItem)?.Tag is int selectedPid ? selectedPid : 0);
        var menu = new MenuFlyout(); foreach (var (key, endpoint) in new[] { ("launchUx", "/riotclient/launch-ux"), ("killUx", "/riotclient/kill-ux"), ("quitClient", "/process-control/v1/process/quit") }) { var item = new MenuFlyoutItem { Text = Label(key) }; item.Click += async (_, _) => await RunAsync(() => Lcu(endpoint)); menu.Items.Add(item); } _more.Flyout = menu;
        RenderCards(); RenderError(); RenderControls();
    }
    private string ClientLabel(ConnectionClient client) => $"{Account(_peeks.GetValueOrDefault(client.Pid).Data.Field("summoner"))} · {Server(client.Command)} · PID {client.Pid}";
    private void RebindClients(int selectedPid)
    {
        // WinUI caches the selected content; recreate labeled options before selecting
        // so a late peek or locale change also updates the selection box.
        var items = _otherClients.Select(client => new ComboBoxItem { Tag = client.Pid, Content = ClientLabel(client) }).ToArray();
        _clients.Items.Clear(); foreach (var item in items) _clients.Items.Add(item);
        _clients.SelectedItem = items.FirstOrDefault(item => item.Tag is int pid && pid == selectedPid);
    }
    private string Server(JsonElement auth) => NativeAppearance.Current?.StreamerMode == true ? Localization.Key("common.region", Localization.Text("大区", "Region")) : ConnectionData.ServerName(_sgp, auth.Text("region"), auth.Text("rsoPlatformId"), Localization.Locale);
    private static string Account(JsonElement profile) => NativeAppearance.Current?.StreamerMode == true ? Localization.Key("common.summoner", Localization.Text("召唤师", "Summoner")) : profile.ValueKind == JsonValueKind.Object ? profile.Text("gameName") + " #" + profile.Text("tagLine") : Label("noData");
    private void RenderControls()
    {
        bool connected = _state.Field("auth").ValueKind == JsonValueKind.Object, connecting = _connecting || ConnectionData.ConnectingPid(_state) > 0;
        bool selectedConnecting = _clients.SelectedItem is ComboBoxItem { Tag: int pid } && pid == (_connecting ? _pendingPid : ConnectionData.ConnectingPid(_state));
        _refresh.IsEnabled = !_refreshing && !_actionBusy && !_disconnecting; _clients.IsEnabled = !_disconnecting && (!_actionBusy || connecting);
        _connect.IsEnabled = !_cancelConnection && !_disconnecting && _clients.SelectedItem is not null && (!_actionBusy || selectedConnecting);
        _connect.Content = selectedConnecting ? Localization.Text("取消连接", "Cancel connection") : Localization.Text("连接", "Connect");
        _disconnect.IsEnabled = (connected || connecting) && (!_actionBusy || connecting) && !_cancelConnection && !_disconnecting;
        _restart.Visibility = _more.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        _restart.IsEnabled = _more.IsEnabled = connected && !_actionBusy && !_disconnecting;
        _playAgain.Visibility = connected && ConnectionData.IsEndgame(_gameflow.Text("phase")) ? Visibility.Visible : Visibility.Collapsed; _playAgain.IsEnabled = !_actionBusy && !_disconnecting;
        _details.IsEnabled = connected || _otherClients.Length > 0;
    }
    private void RenderCards()
    {
        _clientCards.Children.Clear();
        if (_state.Field("auth").ValueKind == JsonValueKind.Object) { _clientCards.Children.Add(new TextBlock { Text = Label("connectedGroup"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }); _clientCards.Children.Add(Card((int)_state.Field("auth").Number("pid"), _state.Field("auth"), _summoner.Field("me"), "", false)); }
        _clientCards.Children.Add(new TextBlock { Text = Label(_state.Field("auth").ValueKind == JsonValueKind.Object ? "launchedOtherClientsGroup" : _otherClients.Length > 0 ? "launchedClientsGroup" : "noClientGroup"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var client in _otherClients) { var peek = _peeks.GetValueOrDefault(client.Pid).Data; _clientCards.Children.Add(Card(client.Pid, client.Command, peek.Field("summoner"), peek.Text("profileIcon"), true)); }
        if (_otherClients.Length == 0 && _state.Field("auth").ValueKind != JsonValueKind.Object) _clientCards.Children.Add(new TextBlock { Text = Label("noClient"), TextWrapping = TextWrapping.Wrap, MaxWidth = 330 });
    }
    private UIElement Card(int pid, JsonElement auth, JsonElement profile, string image, bool selectable)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; var portrait = new Image { Width = 36, Height = 36 }; row.Children.Add(portrait);
        var texts = new StackPanel { Spacing = 3 }; texts.Children.Add(new TextBlock { Text = Account(profile), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }); texts.Children.Add(new TextBlock { Text = Server(auth) + (selectable ? $" (PID: {pid})" : ""), FontSize = 11 });
        if ((_connecting ? _pendingPid : ConnectionData.ConnectingPid(_state)) == pid) texts.Children.Add(new TextBlock { Text = Label("connecting"), FontSize = 11 }); row.Children.Add(texts);
        if (image.Length == 0 && !selectable && profile.Field("profileIconId").ValueKind == JsonValueKind.Number) image = $"/lol-game-data/assets/v1/profile-icons/{profile.Number("profileIconId")}.jpg";
        _ = LoadPortraitAsync(portrait, image, pid, !selectable);
        var button = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Left, MinWidth = 320, IsEnabled = !selectable || !_actionBusy || (_connecting ? _pendingPid : ConnectionData.ConnectingPid(_state)) == pid };
        if (selectable) button.Click += async (_, _) => await ConnectClientAsync(pid); return button;
    }
    private async Task LoadPortraitAsync(Image target, string path, int pid, bool validateConnected = true)
    {
        try
        {
            if (path.Length == 0) { target.Source = null; return; } int revision = _revision;
            byte[]? bytes = path.StartsWith("data:", StringComparison.Ordinal) && path.IndexOf(',') is int comma && comma > 0 ? Convert.FromBase64String(path[(comma + 1)..]) : await _backend.ImageAsync(path);
            if (bytes is null || !_loaded || revision != _revision || validateConnected && pid != (int)_state.Field("auth").Number("pid")) return;
            using var stream = new InMemoryRandomAccessStream(); using var writer = new DataWriter(stream); writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); stream.Seek(0); var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); target.Source = bitmap;
        }
        catch { target.Source = null; }
    }
    private async Task PeekClientsAsync()
    {
        if (!_loaded || _peeking || !_backend.IsReady) return; _peeking = true; int revision = _revision;
        try
        {
            foreach (var client in _otherClients.Where(client => ConnectionData.ShouldPeek(_peeks.TryGetValue(client.Pid, out var cached) ? cached.At : null, DateTimeOffset.UtcNow)).ToArray())
            {
                var data = await _backend.CallAsync("league-client-main", "peekClient", client.Command);
                if (!_loaded || revision != _revision) return;
                if (_otherClients.All(current => current.Pid != client.Pid)) continue;
                if (data.ValueKind == JsonValueKind.Object && data.Field("summoner").ValueKind == JsonValueKind.Object) _peeks[client.Pid] = (data, DateTimeOffset.UtcNow); else _peeks.Remove(client.Pid);
            }
            if (_loaded && revision == _revision) Render();
        }
        catch (Exception ex) { if (_loaded && revision == _revision) ShowError("peek", ex.Message); }
        finally { _peeking = false; }
    }
    private Task ConnectSelectedAsync() => _clients.SelectedItem is ComboBoxItem { Tag: int pid } ? ConnectClientAsync(pid) : Task.CompletedTask;
    private async Task ConnectClientAsync(int pid)
    {
        if ((_connecting ? _pendingPid : ConnectionData.ConnectingPid(_state)) == pid && pid > 0) { await DisconnectAsync(); return; }
        if (_actionBusy) return; var client = _otherClients.FirstOrDefault(client => client.Pid == pid); if (client is null) return;
        _connecting = true; _pendingPid = pid; _cancelConnection = false;
        await RunAsync(() => _backend.CallAsync("league-client-main", "connect", new { pid }));
        _connecting = false; _pendingPid = 0; _cancelConnection = false; Render();
    }
    private async Task DisconnectAsync()
    {
        if (_disconnecting) return; _disconnecting = true;
        if (_connecting) _cancelConnection = true; RenderControls();
        try { await _backend.CallAsync("league-client-main", "disconnect"); await RefreshAsync(); }
        catch (Exception ex) { ShowError("action", ex.Message); }
        finally { _disconnecting = false; RenderControls(); }
    }
    private async Task<JsonElement> Lcu(string endpoint)
    {
        int expected = (int)_state.Field("auth").Number("pid"); var current = await _backend.StateAsync("league-client-main");
        if (expected <= 0 || (int)current.Field("auth").Number("pid") != expected) throw new InvalidOperationException(Localization.Text("连接的客户端已切换，请重试", "The connected client changed; try again"));
        return await _backend.CallAsync("winui-backend", "lcuRequest", "POST", endpoint, (object?)null);
    }
    private async Task RunAsync(Func<Task> operation)
    {
        if (_actionBusy || _disconnecting) return; _actionBusy = true; _errorKind = ""; _errorPayload = ""; RenderError(); RenderControls(); RenderCards();
        try { await operation(); }
        catch (Exception ex) { ShowError("action", ex.Message); }
        finally { _actionBusy = false; if (_loaded) await RefreshAsync(); RenderControls(); }
    }
    private void ShowError(string kind, string payload) { _errorKind = kind; _errorPayload = payload; RenderError(); }
    private void RenderError()
    {
        _error.Title = _errorKind switch { "discovery" => Localization.Text("检测到客户端，但无法读取连接信息；可尝试重新启动客户端。", "A client is running, but its connection details are unavailable. Try restarting the client."), "refresh" => Localization.Text("连接状态读取失败：", "Failed to read connection status: ") + _errorPayload, "peek" => Localization.Text("客户端资料读取失败：", "Failed to read client profile: ") + _errorPayload, "action" => Localization.Text("客户端操作失败：", "Client action failed: ") + _errorPayload, _ => "" };
        _error.IsOpen = _errorKind.Length > 0;
    }
}

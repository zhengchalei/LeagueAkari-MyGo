using System.Text.Json;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace LeagueAkari.WinUI.Pages;

/// <summary>Three independent claim tools share the client's real rewards and event subscriptions.</summary>
public sealed class RewardClaimToolsView : UserControl
{
    private sealed class Section(RewardClaimKind kind, string key)
    {
        public RewardClaimKind Kind { get; } = kind;
        public string Key { get; } = "toolkit.claim." + key;
        public TextBlock Title { get; } = new() { FontSize = 18 };
        public TextBlock Hint { get; } = new() { TextWrapping = TextWrapping.Wrap };
        public TextBlock Column { get; } = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        public TextBlock Status { get; } = new() { TextWrapping = TextWrapping.Wrap };
        public ListView Items { get; } = new() { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 600 };
        public Button Claim { get; } = new();
        public Button Cancel { get; } = new();
        public Button Refresh { get; } = new();
        public CheckBox SelectAll { get; } = new();
        public ClaimableRewardEntry[] Entries = [];
        public RewardOperationState Operations { get; } = new();
        public bool Loading => Operations.Loading;
        public bool Claiming => Operations.Claiming;
        public bool Cancelled => Operations.Cancelled;
        public bool Binding;
        public int DataRevision;
        public string StatusKey = "", StatusValue = "";
    }
    private readonly BackendClient _backend;
    private readonly Section[] _sections = [new(RewardClaimKind.Grants, "rewards"), new(RewardClaimKind.Missions, "missions"), new(RewardClaimKind.EventHub, "eventHub")];
    private readonly Dictionary<string, RewardClaimKind> _subscriptions = [];
    private bool _loaded, _connected;
    private int _revision, _connectionRequest;
    private long _clientPid;

    public RewardClaimToolsView(BackendClient backend)
    {
        _backend = backend; var panel = new StackPanel { Spacing = 24, MaxWidth = 800, HorizontalAlignment = HorizontalAlignment.Stretch }; Content = panel;
        foreach (var section in _sections)
        {
            var body = new StackPanel { Spacing = 8 }; body.Children.Add(section.Title);
            var actions = new WrapPanel { Spacing = 6 }; foreach (var control in new UIElement[] { section.Claim, section.Cancel, section.Refresh, section.SelectAll }) actions.Children.Add(control); body.Children.Add(actions); body.Children.Add(section.Column); body.Children.Add(section.Items); body.Children.Add(section.Status); body.Children.Add(section.Hint); panel.Children.Add(body);
            section.Claim.Click += async (_, _) => await ClaimAsync(section); section.Cancel.Click += (_, _) => { section.Operations.Cancel(); Render(section); }; section.Refresh.Click += async (_, _) => await RefreshAsync(section, true);
            section.Items.SelectionChanged += (_, _) => { if (!section.Binding) RenderControls(section); };
            section.SelectAll.Checked += (_, _) => { if (!section.Binding) section.Items.SelectAll(); };
            section.SelectAll.Unchecked += (_, _) => { if (!section.Binding) section.Items.SelectedItems.Clear(); };
        }
        Loaded += async (_, _) =>
        {
            _loaded = true; _revision++; _connected = false; _clientPid = 0; _backend.EventReceived += OnEvent; Localization.Changed += OnLanguageChanged;
            int revision = _revision; RenderAll(); await SubscribeAsync(); if (_loaded && revision == _revision) await ConnectAsync();
        };
        Unloaded += async (_, _) =>
        {
            _loaded = false; _revision++; _backend.EventReceived -= OnEvent; Localization.Changed -= OnLanguageChanged;
            foreach (var section in _sections) section.Operations.Reset();
            var subscriptions = _subscriptions.Keys.ToArray(); _subscriptions.Clear(); foreach (var id in subscriptions) { try { await _backend.CallAsync("league-client-main", "unsubscribeLcuEndpoint", id); } catch { } }
        };
    }
    private string Text(Section section, string suffix, params (string Key, object? Value)[] args) => Localization.Key(section.Key + "." + suffix, null, args.ToDictionary(pair => pair.Key, pair => pair.Value));
    private void OnLanguageChanged() => DispatcherQueue.TryEnqueue(RenderAll);
    private void RenderAll() { foreach (var section in _sections) { Draw(section); Render(section); } }
    private void Render(Section section)
    {
        section.Title.Text = Text(section, "title"); section.Hint.Text = Text(section, "hint"); section.Column.Text = Text(section, "columns.rewardList"); section.Refresh.Content = Text(section, "refreshButton"); section.Cancel.Content = Text(section, "cancelButton"); section.SelectAll.Content = Localization.Text("全选", "Select all");
        section.Status.Text = section.StatusKey.Length > 0 ? Text(section, section.StatusKey, (section.StatusKey == "claimed" ? "item" : "reason", section.StatusValue)) : "";
        RenderControls(section);
    }
    private void RenderControls(Section section)
    {
        int selected = section.Items.SelectedItems.Count;
        section.Claim.Content = Text(section, selected > 0 ? "claimButtonC" : "claimButton", ("count", selected));
        section.Claim.IsEnabled = _connected && !section.Loading && selected > 0;
        section.Refresh.IsEnabled = _connected && !section.Loading;
        section.Items.IsEnabled = _connected && !section.Loading; section.SelectAll.IsEnabled = _connected && !section.Loading && section.Entries.Length > 0;
        section.Cancel.Visibility = section.Claiming ? Visibility.Visible : Visibility.Collapsed; section.Cancel.IsEnabled = !section.Cancelled;
        section.Binding = true; section.SelectAll.IsChecked = selected > 0 && selected == section.Entries.Length ? true : selected > 0 ? null : false; section.Binding = false;
    }
    private string[] Selected(Section section) => section.Items.SelectedItems.Cast<ListViewItem>().Select(row => ((ClaimableRewardEntry)row.Tag).Id).ToArray();
    private void Draw(Section section)
    {
        var selected = RewardData.PreserveSelected(Selected(section), section.Entries).ToHashSet(); section.Binding = true; section.Items.Items.Clear();
        foreach (var entry in section.Entries)
        {
            var body = new StackPanel { Spacing = 8 }; string title = entry.Title.Contains("DO NOT TRANSLATE", StringComparison.Ordinal) ? Localization.Key("toolkit.claim.item.untranslatedC", null, new Dictionary<string, object?> { ["count"] = entry.Rewards.Length }) : entry.Title;
            body.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var rewards = new WrapPanel { Spacing = 5 };
            foreach (var reward in entry.Rewards)
            {
                var item = new StackPanel { Width = 68, Spacing = 4 }; var image = new Image { Width = 32, Height = 32, Stretch = Stretch.Uniform }; item.Children.Add(image); item.Children.Add(new TextBlock { Text = reward.Name, FontSize = 10, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
                var preview = new StackPanel { Spacing = 6 }; var large = new Image { Width = 128, Height = 128, Stretch = Stretch.Uniform }; preview.Children.Add(large); preview.Children.Add(new TextBlock { Text = reward.Name, TextWrapping = TextWrapping.Wrap, MaxWidth = 220 }); ToolTipService.SetToolTip(item, preview);
                _ = ImageAsync(image, large, reward.Icon, _revision); rewards.Children.Add(item);
            }
            body.Children.Add(rewards); var row = new ListViewItem { Content = body, Tag = entry, HorizontalContentAlignment = HorizontalAlignment.Stretch }; section.Items.Items.Add(row); if (selected.Contains(entry.Id)) section.Items.SelectedItems.Add(row);
        }
        section.Binding = false; RenderControls(section);
    }
    private void Replace(Section section, ClaimableRewardEntry[] entries) { section.Entries = entries; section.DataRevision++; Draw(section); }
    private async Task ConnectAsync()
    {
        int revision = _revision, connectionRequest = ++_connectionRequest;
        try
        {
            var state = await _backend.StateAsync("league-client-main"); if (!_loaded || revision != _revision || connectionRequest != _connectionRequest) return;
            bool wasConnected = _connected; long oldPid = _clientPid; _connected = state.Text("connectionState") == "connected"; _clientPid = (long)state.Field("auth").Number("pid");
            if (!_connected || oldPid != _clientPid) foreach (var section in _sections) { section.Operations.Reset(); section.DataRevision++; section.Entries = []; section.StatusKey = section.StatusValue = ""; Draw(section); }
            RenderAll(); if (_connected && (!wasConnected || oldPid != _clientPid)) await Task.WhenAll(_sections.Select(section => RefreshAsync(section)));
        }
        catch (Exception ex) { if (_loaded && revision == _revision && connectionRequest == _connectionRequest) foreach (var section in _sections) { section.StatusKey = "refreshFailed"; section.StatusValue = ex.Message; Render(section); } }
    }
    private async Task SubscribeAsync()
    {
        int revision = _revision;
        foreach (var (endpoint, kind) in new[] { ("/lol-rewards/v1/grants", RewardClaimKind.Grants), ("/lol-missions/v1/missions", RewardClaimKind.Missions) })
        {
            try
            {
                var response = await _backend.CallAsync("league-client-main", "subscribeLcuEndpoint", endpoint); string id = response.ValueKind == JsonValueKind.String ? response.GetString()! : ""; if (id.Length == 0) continue;
                if (!_loaded || revision != _revision) { await _backend.CallAsync("league-client-main", "unsubscribeLcuEndpoint", id); return; } _subscriptions[id] = kind;
            }
            catch (Exception ex) { if (!_loaded || revision != _revision) return; var section = _sections.First(item => item.Kind == kind); section.StatusKey = "refreshFailed"; section.StatusValue = ex.Message; Render(section); }
        }
    }
    private void OnEvent(JsonElement envelope)
    {
        string name = envelope.Text("name"); var args = envelope.Field("args").Items().ToArray();
        if (name == "update-state-prop/league-client-main:state") DispatcherQueue.TryEnqueue(async () => { if (_loaded) await ConnectAsync(); });
        else if (envelope.Text("namespace") == "league-client-main" && name == "extra-lcu-event" && args.Length > 1) DispatcherQueue.TryEnqueue(() =>
        {
            if (!_loaded || !_connected || !_subscriptions.TryGetValue(args[0].ToString(), out var kind)) return;
            var section = _sections.First(item => item.Kind == kind); var data = args[1].Field("data"); Replace(section, kind == RewardClaimKind.Missions ? RewardData.Missions(data) : RewardData.Grants(data)); Render(section);
        });
    }
    private Task<JsonElement> Lcu(string method, string path, object? body = null) => _backend.CallAsync("winui-backend", "lcuRequest", method, path, body);
    private async Task RefreshAsync(Section section, bool manually = false)
    {
        if (!_loaded || !_connected || section.Operations.Begin(false) is not { } operation) return; Render(section); int revision = _revision, dataRevision = section.DataRevision; long pid = _clientPid;
        bool Current() => _loaded && _connected && revision == _revision && pid == _clientPid && section.Operations.IsCurrent(operation);
        try
        {
            string path = section.Kind switch { RewardClaimKind.Grants => "/lol-rewards/v1/grants?status=PENDING_SELECTION", RewardClaimKind.Missions => "/lol-missions/v1/missions", _ => "/lol-event-hub/v1/events" };
            var response = await Lcu("GET", path); if (!Current()) return;
            if (dataRevision == section.DataRevision) Replace(section, section.Kind switch { RewardClaimKind.Grants => RewardData.Grants(response, true), RewardClaimKind.Missions => RewardData.Missions(response), _ => RewardData.Events(response) });
            if (manually) { section.StatusKey = "refreshSuccess"; section.StatusValue = ""; }
            if (section.Kind == RewardClaimKind.EventHub) await Task.WhenAll(section.Entries.Select(async entry =>
            {
                try
                {
                    if (!Current()) return;
                    var track = await Lcu("GET", $"/lol-event-hub/v1/events/{Uri.EscapeDataString(entry.Id)}/reward-track/items"); if (!Current()) return;
                    var bonus = await Lcu("GET", $"/lol-event-hub/v1/events/{Uri.EscapeDataString(entry.Id)}/reward-track/bonus-items"); if (!Current()) return;
                    section.Entries = section.Entries.Select(current => current.Id == entry.Id ? current with { Rewards = RewardData.EventRewards(track, bonus) } : current).ToArray(); Draw(section);
                }
                catch { /* Like the original allSettled track fetch, keep other events claimable. */ }
            }));
        }
        catch (Exception ex) { if (Current()) { section.StatusKey = "refreshFailed"; section.StatusValue = ex.Message; } }
        finally { if (section.Operations.Complete(operation) && _loaded) Render(section); }
    }
    private async Task ClaimAsync(Section section)
    {
        if (!_loaded || !_connected || section.Loading || section.Items.SelectedItems.Count == 0) return;
        var selected = Selected(section); if (section.Operations.Begin(true) is not { } operation) return; Render(section); int revision = _revision; long pid = _clientPid;
        bool Current() => _loaded && _connected && revision == _revision && pid == _clientPid && section.Operations.IsCurrent(operation);
        try
        {
            await RewardClaimBatch.RunAsync(selected, id => section.Entries.FirstOrDefault(entry => entry.Id == id), section.Kind, () => Current() && !operation.Cancelled, async request =>
            {
                var client = await _backend.StateAsync("league-client-main"); if (client.Text("connectionState") != "connected" || (long)client.Field("auth").Number("pid") != pid || operation.Cancelled || !Current()) { operation.Cancel(); throw new OperationCanceledException(); }
                await Lcu(request.Method, request.Path, request.Body);
            }, request => { if (Current()) { section.StatusKey = "claimed"; section.StatusValue = request.Claimed; Render(section); } });
        }
        catch (OperationCanceledException) when (operation.Cancelled) { }
        catch (Exception ex) { if (Current()) { section.StatusKey = "claimFailed"; section.StatusValue = ex.Message; } }
        finally { if (section.Operations.Complete(operation) && _loaded) Render(section); }
        if (section.Kind == RewardClaimKind.EventHub) await Task.Delay(2000);
        if (_loaded && _connected && revision == _revision && pid == _clientPid && operation.Generation == section.Operations.Generation) await RefreshAsync(section);
    }
    private async Task ImageAsync(Image small, Image preview, string path, int revision)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (Uri.TryCreate(path, UriKind.Absolute, out var remote) && remote.Scheme is "https" or "http") { var bitmap = new BitmapImage(remote); small.Source = preview.Source = bitmap; return; }
            var bytes = await _backend.ImageAsync(path); if (bytes == null || !_loaded || revision != _revision) return;
            using var stream = new InMemoryRandomAccessStream(); using var writer = new DataWriter(stream); writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); stream.Seek(0); var local = new BitmapImage(); await local.SetSourceAsync(stream); small.Source = preview.Source = local;
        }
        catch { /* Reward labels remain readable when the client cannot serve an image. */ }
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LeagueAkari.WinUI.Pages;

internal sealed class AutomationSelectEditor : UserControl
{
    private const string Namespace = "auto-select-main";
    private readonly BackendClient _backend;
    private readonly SettingsForms _forms;
    private readonly GameAssets _assets;
    private readonly StackPanel _panel = new() { Spacing = 12 };
    private readonly ComboBox _group = new() { MinWidth = 240 };
    private readonly ComboBox _kind = new() { MinWidth = 240 };
    private readonly StackPanel _settings = new() { Spacing = 10 };
    private readonly InfoBar _paused = new() { IsClosable = false, Severity = InfoBarSeverity.Warning };
    private AutoSelectGroup[] _groups = [];
    private AutoSelectChampion[] _champions = [];
    private JsonElement _client, _select, _recommendations;
    private bool _attached, _refreshing, _rendering, _pending;
    private string _structureSignature = "", _language = "";
    private static string T(string key) => Localization.Key("automation.champSelect." + key);
    private static string O(string key) => Localization.Key("automation.orderedChampionList." + key);

    public AutomationSelectEditor(BackendClient backend, SettingsForms forms)
    {
        _backend = backend; _forms = forms; _assets = new(backend);
        _panel.Children.Add(_paused); _panel.Children.Add(_group); _panel.Children.Add(_kind); _panel.Children.Add(_settings); Content = _panel;
        _kind.Items.Add(new ComboBoxItem { Tag = "pickConfig", Content = T("pick.title") }); _kind.Items.Add(new ComboBoxItem { Tag = "banConfig", Content = T("ban.title") }); _kind.SelectedIndex = 0;
        _group.SelectionChanged += (_, _) => { if (!_rendering) Render(); }; _kind.SelectionChanged += (_, _) => { if (!_rendering) Render(); };
        _paused.ActionButton = forms.Action(Localization.Text("恢复本次自动选禁", "Resume auto pick / ban"), Namespace, "setTemporarilyDisabled", false);
        Loaded += async (_, _) => { if (_attached) return; _attached = true; _backend.EventReceived += BackendEvent; _forms.Changed += SettingsChanged; Localization.Changed += LanguageChanged; await RefreshAsync(true); };
        Unloaded += (_, _) => { _attached = false; _backend.EventReceived -= BackendEvent; _forms.Changed -= SettingsChanged; Localization.Changed -= LanguageChanged; };
    }
    private void SettingsChanged(string ns, string key) { if (ns == Namespace && key is "pickConfig" or "banConfig") Render(); }
    private void LanguageChanged() => DispatcherQueue.TryEnqueue(() => { _language = ""; RenderGroups(); Render(); });
    private void BackendEvent(JsonElement ev)
    {
        string name = ev.Text("name");
        if (name.Contains(Namespace) || name.Contains("league-client-main:champSelect") || name.Contains("league-client-main:gameData")) DispatcherQueue.TryEnqueue(async () => await RefreshAsync(name.Contains("gameData")));
    }
    private async Task RefreshAsync(bool resources)
    {
        if (!_attached) return;
        if (_refreshing) { _pending = true; return; } _refreshing = true;
        try
        {
            var states = await Task.WhenAll(_backend.StateAsync(Namespace), _backend.StateAsync("league-client-main", "champSelect"), _backend.StateAsync("league-client-main", "gameflow"));
            if (!_attached) return;
            var groupData = AutoSelectData.Groups(states[0]); _select = states[0]; _client = JsonSerializer.SerializeToElement(new { champSelect = states[1], gameflow = states[2] });
            bool changed = JsonSerializer.Serialize(groupData) != JsonSerializer.Serialize(_groups); _groups = groupData;
            if (resources || _champions.Length == 0)
            {
                var catalog = await _backend.StateAsync("league-client-main", "gameData"); JsonElement extra = default;
                try { extra = await _backend.StateAsync("extra-assets-main", "gtimg"); } catch { }
                _champions = AutoSelectData.Champions(catalog.Field("champions"), extra);
                try { _recommendations = await _backend.CallAsync("winui-backend", "lcuRequest", "GET", "/lol-perks/v1/recommended-champion-positions", null); } catch { _recommendations = default; }
                changed = true;
            }
            _paused.IsOpen = _select.Boolean("temporarilyDisabled"); _paused.Message = T("temporarilyDisabled.description");
            var champSelect = _client.Field("champSelect"); string signature = (champSelect.ValueKind == JsonValueKind.Object ? champSelect.GetRawText() : "") + _client.Field("gameflow").Text("phase");
            if (signature != _structureSignature) { _structureSignature = signature; changed = true; }
            if (changed) { RenderGroups(); Render(); }
        }
        catch (Exception failure) { _forms.Status.Text = failure.Message; }
        finally { _refreshing = false; if (_pending) { _pending = false; await RefreshAsync(resources); } }
    }
    private void RenderGroups()
    {
        _rendering = true; string? previous = (_group.SelectedItem as ComboBoxItem)?.Tag?.ToString(); _group.Items.Clear(); _group.Header = T("groupTitle");
        foreach (var group in _groups) _group.Items.Add(new ComboBoxItem { Tag = group.Id, Content = T("groups." + group.Id) });
        _group.SelectedItem = _group.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == previous) ?? _group.Items.OfType<ComboBoxItem>().FirstOrDefault();
        string operation = (_kind.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "pickConfig";
        _kind.Items.Clear(); _kind.Items.Add(new ComboBoxItem { Tag = "pickConfig", Content = T("pick.title") }); _kind.Items.Add(new ComboBoxItem { Tag = "banConfig", Content = T("ban.title") });
        _kind.SelectedItem = _kind.Items.OfType<ComboBoxItem>().First(i => i.Tag?.ToString() == operation); _kind.Header = Localization.Text("操作", "Action");
        _rendering = false; _language = Localization.Locale;
    }
    private void Render()
    {
        if (_rendering || !_attached) return;
        if (_language != Localization.Locale) RenderGroups();
        _settings.Children.Clear();
        if (_group.SelectedItem is not ComboBoxItem selected || _groups.FirstOrDefault(g => g.Id == selected.Tag?.ToString()) is not { } group) { _settings.Children.Add(new TextBlock { Text = T("groupEmpty") }); return; }
        bool ban = (_kind.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "banConfig";
        string kind = ban ? "ban" : "pick", prefix = (ban ? "banConfig." : "pickConfig.") + group.Id;
        _settings.Children.Add(_forms.Toggle(Namespace, prefix + ".enabled", T(kind + ".enabled.label")));
        foreach (string position in group.Positions) _settings.Children.Add(ChampionList(group, position, ban));
        _settings.Children.Add(_forms.Choice(Namespace, prefix + ".strategy", T(kind + ".strategy.label"), ("just-show", T(kind + ".strategy.options.just-show")), ("show-and-lock-in", T(kind + ".strategy.options.show-and-lock-in")), ("lock-in-immediately", T(kind + ".strategy.options.lock-in-immediately"))));
        _settings.Children.Add(_forms.Number(Namespace, prefix + ".delaySeconds", T(kind + ".delaySeconds.label"), 0, 99999, 0, .1));
        if (!ban)
        {
            _settings.Children.Add(_forms.Toggle(Namespace, prefix + ".showIntent", T("pick.showIntent.label")));
            _settings.Children.Add(_forms.Toggle(Namespace, prefix + ".ignoreIntent", T("pick.ignoreIntent.label")));
            _settings.Children.Add(_forms.Number(Namespace, prefix + ".benchSwapAccumulatedDelaySeconds", T("pick.benchSwapAccumulatedDelaySeconds.label"), 0, 99999, 0, .1));
            _settings.Children.Add(_forms.Toggle(Namespace, prefix + ".benchSelectFirstAvailableChampion", T("pick.benchSelectFirstAvailableChampion.label")));
            _settings.Children.Add(_forms.Toggle(Namespace, prefix + ".benchHandleTradeEnabled", T("pick.benchHandleTradeEnabled.label")));
        }
        _settings.Children.Add(_forms.Action(Localization.Text("临时暂停本次自动选禁", "Pause auto pick / ban for this selection"), Namespace, "setTemporarilyDisabled", true));
        _settings.Children.Add(_forms.Action(Localization.Text("恢复本次自动选禁", "Resume auto pick / ban"), Namespace, "setTemporarilyDisabled", false));
    }
    private FrameworkElement ChampionList(AutoSelectGroup group, string position, bool ban)
    {
        string path = AutoSelectData.Path(ban, group.Id, position);
        var ids = (_forms.Value(Namespace, path) as JsonArray)?.Select(n => n?.GetValue<int>() ?? 0).Where(i => i != 0).Distinct().ToArray() ?? [];
        var panel = new StackPanel { Spacing = 6 }; panel.Children.Add(new TextBlock { Text = Localization.Key("common.positions." + (position == "default" ? "ALL" : position), position), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var preview = new WrapPanel { Spacing = 4 }; panel.Children.Add(preview);
        foreach (int id in ids.Take(5)) preview.Children.Add(ChampionDisplay(id, ban));
        if (ids.Length > 5) preview.Children.Add(new TextBlock { Text = "+" + (ids.Length - 5) });
        if (ids.Length == 0) preview.Children.Add(new TextBlock { Text = O("unselected") });
        var edit = new Button { Content = Localization.Text("编辑优先列表", "Edit priority list") }; panel.Children.Add(edit);
        edit.Click += async (_, _) => await EditChampionsAsync(group, position, ban, ids);
        return panel;
    }
    private FrameworkElement ChampionDisplay(int id, bool ban)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        if (id > 0) row.Children.Add(_assets.Icon("champion-summary", id, 22)); else row.Children.Add(new TextBlock { Text = id == -3 ? "?" : "—", Width = 22 });
        string name = id == -1 ? Localization.Key("common.champions.dummy") : id == -3 ? Localization.Key("common.champions.bravery") : _champions.FirstOrDefault(c => c.Id == id)?.Name ?? id.ToString();
        row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        var champSelect = _client.Field("champSelect"); string phase = _client.Field("gameflow").Text("phase");
        var available = AutoSelectData.Numbers(champSelect.Field(ban ? "currentBannableChampionIds" : "currentPickableChampionIds"));
        if (phase == "ChampSelect" && !available.Contains(id)) row.Opacity = .5;
        ToolTipService.SetToolTip(row, name); return row;
    }
    private async Task EditChampionsAsync(AutoSelectGroup group, string position, bool ban, int[] initial)
    {
        var disabled = AutoSelectData.Numbers(_client.Field("champSelect").Field("disabledChampionIds")).ToHashSet();
        var candidates = AutoSelectData.Candidates(_champions, group, ban, disabled, Localization.Key("common.champions.dummy"), Localization.Key("common.champions.bravery"));
        int[] ids = [.. initial]; bool saving = false;
        var source = new ListView { SelectionMode = ListViewSelectionMode.None }; var target = new ListView { SelectionMode = ListViewSelectionMode.None }; var error = new InfoBar { IsClosable = true, Severity = InfoBarSeverity.Error };
        var search = new TextBox { PlaceholderText = O("searchForChampion") }; var filter = SettingsForms.Select(Localization.Text("英雄位置筛选", "Champion position"), [("", Localization.Text("全部位置", "All positions")), ("TOP", Localization.Key("common.positions.TOP")), ("JUNGLE", Localization.Key("common.positions.JUNGLE")), ("MIDDLE", Localization.Key("common.positions.MIDDLE")), ("BOTTOM", Localization.Key("common.positions.BOTTOM")), ("UTILITY", Localization.Key("common.positions.UTILITY"))]);
        var grid = new Grid { ColumnSpacing = 12, MinWidth = 560, Height = 440 }; grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new());
        var left = new Grid { RowSpacing = 6 }; left.RowDefinitions.Add(new() { Height = GridLength.Auto }); left.RowDefinitions.Add(new() { Height = GridLength.Auto }); left.RowDefinitions.Add(new()); left.Children.Add(search); Grid.SetRow(filter, 1); left.Children.Add(filter); Grid.SetRow(source, 2); left.Children.Add(source); grid.Children.Add(left); Grid.SetColumn(target, 1); grid.Children.Add(target);
        var body = new StackPanel { Spacing = 8 }; body.Children.Add(grid); body.Children.Add(error);
        var dialog = new ContentDialog { Title = T(ban ? "ban.expectedChampions.label" : "pick.expectedChampions.label"), Content = body, CloseButtonText = Localization.Text("关闭", "Close"), XamlRoot = XamlRoot };
        dialog.Closing += (_, args) => args.Cancel = saving;
        async Task SaveAsync(int[] proposed)
        {
            if (saving) return; saving = true; source.IsEnabled = target.IsEnabled = false; error.IsOpen = false;
            try { await _forms.Save(Namespace, AutoSelectData.Path(ban, group.Id, position), new JsonArray(proposed.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray())); ids = proposed; DrawTarget(); DrawSource(); }
            catch (Exception failure) { error.Title = failure.Message; error.IsOpen = true; }
            finally { saving = false; source.IsEnabled = target.IsEnabled = true; }
        }
        void DrawSource()
        {
            source.Items.Clear(); string chosen = (filter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            foreach (var candidate in candidates.Where(c => !ids.Contains(c.Id) && AutoSelectData.Matches(c, search.Text, chosen, _recommendations)))
            {
                var add = new Button { Content = ChampionDisplay(candidate.Id, ban), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(4) }; add.Click += async (_, _) => await SaveAsync(AutoSelectData.Add(ids, candidate.Id)); source.Items.Add(add);
            }
        }
        void DrawTarget()
        {
            target.Items.Clear();
            foreach (int id in ids)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, CanDrag = true, AllowDrop = true }; row.Children.Add(ChampionDisplay(id, ban));
                var up = new Button { Content = "↑", IsEnabled = Array.IndexOf(ids, id) > 0, Padding = new Thickness(4) }; ToolTipService.SetToolTip(up, O("moveUp")); up.Click += async (_, _) => await SaveAsync(AutoSelectData.Move(ids, id, -1)); row.Children.Add(up);
                var down = new Button { Content = "↓", IsEnabled = Array.IndexOf(ids, id) < ids.Length - 1, Padding = new Thickness(4) }; ToolTipService.SetToolTip(down, O("moveDown")); down.Click += async (_, _) => await SaveAsync(AutoSelectData.Move(ids, id, 1)); row.Children.Add(down);
                var remove = new Button { Content = "×", Padding = new Thickness(4) }; remove.Click += async (_, _) => await SaveAsync(AutoSelectData.Remove(ids, id)); row.Children.Add(remove);
                row.DragStarting += (_, args) => { args.Data.Properties["championId"] = id; args.Data.RequestedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move; };
                row.DragOver += (_, args) => { if (args.DataView.Properties.ContainsKey("championId")) args.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move; };
                row.Drop += async (_, args) => { if (args.DataView.Properties.TryGetValue("championId", out var value) && value is int dragged) await SaveAsync(AutoSelectData.Drop(ids, dragged, id)); };
                target.Items.Add(row);
            }
        }
        search.TextChanged += (_, _) => DrawSource(); filter.SelectionChanged += (_, _) => DrawSource(); DrawSource(); DrawTarget(); await NativeDialogs.ShowAsync(dialog);
    }
}

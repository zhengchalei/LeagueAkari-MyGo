using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LeagueAkari.WinUI.Services;

namespace LeagueAkari.WinUI.Pages;

public sealed class HistoryFilterEditor : UserControl
{
    private readonly BackendClient _backend;
    private readonly string _puuid;
    private readonly StackPanel _body = new() { Spacing = 10 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private JsonElement _catalog;
    private JsonElement _extra;
    private readonly NativeImages _images;
    private readonly StackPanel _root = new() { Spacing = 12, Padding = new Thickness(12) };
    private readonly Dictionary<string, JsonElement> _pageSummoners = new();
    private readonly Func<string, Task<JsonElement>>? _findPlayer;
    private readonly List<HistoryFilterPlayerSearch> _searches = new();
    private bool _loaded;
    private int _revision;
    private int _loadRevision;
    private bool _rendering;
    public HistoryFilterSettings Settings { get; private set; }
    public bool CollectEnabled => Settings.CollectEnabled;
    public event EventHandler? Changed;
    private static readonly (string Key, string Label)[] Times = [("all", "全部时间"), ("last3Hours", "最近 3 小时"), ("last12Hours", "最近 12 小时"), ("last24Hours", "最近 24 小时"), ("last3Days", "最近 3 天"), ("last7Days", "最近 7 天"), ("last30Days", "最近 30 天")];
    private static readonly (string Key, string Label)[] Positions = [("TOP", "上路"), ("JUNGLE", "打野"), ("MIDDLE", "中路"), ("BOTTOM", "下路"), ("UTILITY", "辅助")];
    public HistoryFilterEditor(BackendClient backend, string currentPuuid, HistoryFilterSettings settings, IEnumerable<JsonElement>? pageSummoners = null, Func<string, Task<JsonElement>>? findPlayer = null)
    {
        _backend = backend; _puuid = currentPuuid; Settings = HistoryFilterEditorData.Draft(settings); _images = new(backend); _findPlayer = findPlayer;
        foreach (var player in pageSummoners ?? []) if (player.Text("puuid") is { Length: > 0 } id) _pageSummoners[id] = player;
        Content = _root; Render(); Loaded += LoadAsync; Unloaded += (_, _) => { _loaded = false; _revision++; _loadRevision++; CancelSearches(); Localization.Changed -= LanguageChanged; };
    }
    private static string T(string zh, string en) => Localization.Text(zh, en);
    private static string L(string suffix, string? fallback = null) => Localization.Key("playerTabs.matchHistory.filters." + suffix, fallback);
    private async void LoadAsync(object sender, RoutedEventArgs args)
    {
        if (_loaded) return; _loaded = true; int revision = ++_loadRevision; Localization.Changed += LanguageChanged;
        try
        {
            var result = await Task.WhenAll(_backend.StateAsync("league-client-main", "gameData"), ExtraAsync());
            if (!_loaded || revision != _loadRevision) return; _catalog = result[0]; _extra = result[1]; Render();
        }
        catch (Exception ex) { if (_loaded && revision == _loadRevision) _status.Text = ex.Message; }
    }
    private async Task<JsonElement> ExtraAsync() { try { return await _backend.StateAsync("extra-assets-main", "gtimg"); } catch { return default; } }
    private void LanguageChanged() => DispatcherQueue.TryEnqueue(Render);
    private void CancelSearches() { foreach (var search in _searches) search.Cancel(); _searches.Clear(); }
    public void SetPositionAvailable(bool value) { Settings.EnablePosition = value; Render(); }
    private void Notify() { if (!_rendering) Changed?.Invoke(this, EventArgs.Empty); }
    private void Render()
    {
        _rendering = true;
        try
        {
            _revision++; CancelSearches(); _root.Children.Clear();
            var mode = Select(T("筛选方式", "Filter type"), [("simple", L("simpleTab")), ("advanced", L("advancedTab"))], Settings.Mode, value => { Settings.Mode = value; Render(); Notify(); });
            var collect = new CheckBox { Content = T("收集模式：逐页扫描符合条件的对局", "Collection mode: scan pages for matching games"), IsChecked = Settings.CollectEnabled };
            collect.Click += (_, _) => { Settings.CollectEnabled = collect.IsChecked == true; Notify(); };
            _root.Children.Add(mode); _root.Children.Add(collect); _root.Children.Add(_status); _root.Children.Add(_body);
            _body.Children.Clear(); if (Settings.Mode == "advanced") Advanced(); else Simple();
        }
        finally { _rendering = false; }
    }
    private static StackPanel Panel() => new() { Spacing = 8 };
    private static StackPanel Row(params UIElement[] items) { var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; foreach (var item in items) row.Children.Add(item); return row; }
    private static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap };
    private Image Icon(string path)
    {
        var image = new Image { Width = 20, Height = 20 };
        image.Loaded += async (_, _) => { if (path.Length > 0) await _images.SetAsync(image, path); };
        return image;
    }
    private static (string Key, string Label)[] TimeChoices() => Times.Select(p => (p.Key, Localization.Key("playerTabs.matchHistory.timeRange." + p.Key, p.Label))).ToArray();
    private static (string Key, string Label)[] PositionChoices() => Positions.Select(p => (p.Key, Localization.Key("positions." + p.Key, p.Label))).ToArray();
    private bool ChampionMatches(int id, string label, string search)
    {
        string keywords = _extra.Field("heroListMap").Field(id.ToString()).Text("keywords", _extra.Field("heroList").Field("hero").Items().FirstOrDefault(h => h.Text("heroId") == id.ToString()).Text("keywords"));
        return id.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) || (label + "," + keywords).Split(',').Any(value => NativePinyin.Matches(search, value));
    }
    private Button Button(string text, Action action) { var button = new Button { Content = text }; button.Click += (_, _) => { action(); Notify(); }; return button; }
    private ComboBox Select(string header, IEnumerable<(string Key, string Label)> choices, string? current, Action<string> changed)
    {
        var control = new ComboBox { Header = header, MinWidth = 180, MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (key, label) in choices) { var item = new ComboBoxItem { Content = label, Tag = key }; control.Items.Add(item); if (key == current) control.SelectedItem = item; }
        control.SelectionChanged += (_, _) => { if (!_rendering && control.SelectedItem is ComboBoxItem item) changed((string)item.Tag); };
        return control;
    }
    private void Simple()
    {
        var simple = Settings.Simple;
        _body.Children.Add(Select(L("winLoss"), [("all", L("all")), ("win", L("win")), ("loss", L("loss"))], simple.WinLoss, value => { simple.WinLoss = value; Notify(); }));
        _body.Children.Add(Select(L("timeRange"), TimeChoices(), simple.TimeRange, value => { simple.TimeRange = value; Notify(); }));
        if (Settings.EnablePosition)
        {
            _body.Children.Add(Text(L("position"))); var row = new Grid { ColumnSpacing = 8, RowSpacing = 6 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 3; i++) row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            int positionIndex = 0;
            foreach (var (key, label) in PositionChoices()) { var check = new CheckBox { Content = label, IsChecked = simple.Positions.Contains(key) }; check.Click += (_, _) => { if (check.IsChecked == true) { if (!simple.Positions.Contains(key)) simple.Positions.Add(key); } else simple.Positions.Remove(key); Notify(); }; Grid.SetColumn(check, positionIndex % 2); Grid.SetRow(check, positionIndex / 2); positionIndex++; row.Children.Add(check); } _body.Children.Add(row);
        }
        _body.Children.Add(Text(L("champions") + T("（多选条件同时满足）", " (all selected champions required)")));
        var search = new TextBox { PlaceholderText = T("搜索名称、拼音、别名或编号", "Search name, pinyin, alias or ID"), MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left }; var champions = new ListView { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 220, MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left };
        bool selecting = false;
        void RenderChampions()
        {
            selecting = true; champions.Items.Clear(); var entries = Catalog("champions").Where(v => v.Number("id") > 0).Select(v => (Id: (int)v.Number("id"), Name: v.Text("name"))).ToList();
            foreach (int id in simple.ChampionIds.Where(id => entries.All(c => c.Id != id))) entries.Add((id, T("英雄 ", "Champion ") + id));
            foreach (var entry in entries.OrderBy(e => e.Name).Where(e => ChampionMatches(e.Id, e.Name, search.Text))) { var item = new ListViewItem { Content = Row(Icon($"/lol-game-data/assets/v1/champion-icons/{entry.Id}.png"), Text($"{entry.Name} · {entry.Id}")), Tag = entry.Id }; champions.Items.Add(item); if (simple.ChampionIds.Contains(entry.Id)) champions.SelectedItems.Add(item); } selecting = false;
        }
        champions.SelectionChanged += (_, args) => { if (selecting || _rendering) return; foreach (var item in args.AddedItems.Cast<ListViewItem>()) if (!simple.ChampionIds.Contains((int)item.Tag)) simple.ChampionIds.Add((int)item.Tag); foreach (var item in args.RemovedItems.Cast<ListViewItem>()) simple.ChampionIds.Remove((int)item.Tag); Notify(); };
        search.TextChanged += (_, _) => RenderChampions(); RenderChampions(); _body.Children.Add(search); _body.Children.Add(champions);
        _body.Children.Add(Text(L("summoners") + T("（多选条件同时满足）", " (all selected players required)"))); _body.Children.Add(PlayerSearch(null, id => { if (id is null) return; if (!simple.SummonerPuuids.Contains(id)) simple.SummonerPuuids.Add(id); CachePlayer(id); Render(); Notify(); }));
        foreach (string id in simple.SummonerPuuids.ToArray()) _body.Children.Add(Row(Text(PlayerLabel(id)), Button(L("delete"), () => { simple.SummonerPuuids.Remove(id); Render(); })));
        _body.Children.Add(Button(L("reset"), () => { Settings.Simple = HistoryFilterEditorData.Clear(Settings.Simple); Render(); }));
    }
    private void Advanced()
    {
        var presets = Select(T("示例模板", "Example presets"), [("", T("选择模板", "Select a preset")), ("clean-match-samples", T("干净的匹配样本", "Clean matched samples")), ("strong-self-performance", T("自身强势发挥", "Strong self performance")), ("kiwi-jayce-slow-and-steady", T("敌方慢慢来杰斯（海克斯大乱斗）", "Enemy Slow and Steady Jayce (Hextech ARAM)"))], "", value => { if (value.Length == 0) return; var cached = Settings.Advanced.CachedSummoners; Settings.Advanced = HistoryFilter.Preset(value, _puuid); Settings.Advanced.CachedSummoners = cached; Render(); Notify(); });
        _body.Children.Add(presets); _body.Children.Add(Text(T("未完成的条件暂时视为满足。每层只提供适用于当前范围的条件。", "Incomplete conditions match temporarily. Each level offers conditions valid for its scope.")));
        if (!Settings.Advanced.NodeMap.TryGetValue(Settings.Advanced.RootId, out var root)) { _status.Text = T("筛选根节点缺失，请清空规则后重试", "Filter root missing. Clear conditions and retry."); }
        else _body.Children.Add(BuildNode(root, "game", 0));
        _body.Children.Add(Button(L("reset"), () => { Settings.Advanced.Clear(); _status.Text = ""; Render(); }));
    }
    private FrameworkElement BuildNode(HistoryFilterNode node, string scope, int depth)
    {
        var box = Panel(); if (depth > 48) { box.Children.Add(Text(T("筛选层级过深", "Too many nested conditions"))); return box; }
        var spec = HistoryFilter.Specs.FirstOrDefault(s => s.Type == node.Type); string provided = spec?.Provide ?? scope;
        if (node.Type != "game")
        {
            var heading = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
            heading.Children.Add(Text(L("combinatorLabels." + node.Type, spec?.Label ?? node.Type)));
            var actions = new WrapPanel(); actions.Children.Add(Button(L("delete"), () => { Settings.Advanced.Remove(node.Id); Render(); }));
            if (node.Type is "and" or "or") actions.Children.Add(Button(L(node.Type == "and" ? "switchToOr" : "switchToAnd"), () => { HistoryFilterEditorData.ToggleLogic(node); Render(); }));
            foreach (Button action in actions.Children) if (action.Content is string label) action.Content = Text(label);
            heading.Children.Add(actions);
            box.Children.Add(heading);
            if (spec is null || !spec.Require.Contains(scope)) { box.Children.Add(Text(T("此条件与当前范围不兼容", "Condition incompatible with this scope"))); return box; }
        }
        foreach (var element in Parameters(node)) box.Children.Add(element);
        for (int index = 0; index < node.Args.Count; index++)
        {
            var arg = node.Args[index]; if (arg.Kind != "node") continue;
            if (arg.StringValue is { } childId && Settings.Advanced.NodeMap.TryGetValue(childId, out var child))
                box.Children.Add(new Border { Padding = new Thickness(10), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightGray), Child = BuildNode(child, provided, depth + 1) });
            else { int slot = index; box.Children.Add(AddCondition(node, provided, slot)); }
        }
        if (node.Type is "and" or "or") box.Children.Add(AddCondition(node, provided, null));
        return box;
    }
    private FrameworkElement AddCondition(HistoryFilterNode parent, string scope, int? slot)
    {
        var control = new ComboBox { MinWidth = 180, MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left };
        var placeholder = new ComboBoxItem { Content = L("addCondition"), Tag = "" }; control.Items.Add(placeholder); control.SelectedItem = placeholder;
        string? previous = null;
        foreach (var spec in HistoryFilterEditorData.Choices(scope))
        {
            string category = HistoryFilterEditorData.Category(spec.Type);
            if (category != previous && category != "logicGroups") control.Items.Add(new ComboBoxItem { Content = L(category), IsEnabled = false });
            previous = category; control.Items.Add(new ComboBoxItem { Content = L("combinatorLabels." + spec.Type, spec.Label), Tag = spec.Type });
        }
        control.SelectionChanged += (_, _) =>
        {
            if (_rendering || control.SelectedItem is not ComboBoxItem { Tag: string type } || type.Length == 0) return;
            var child = Settings.Advanced.Add(type, parent.Id, HistoryFilterEditorData.Defaults(type));
            if (slot.HasValue) parent.Args[slot.Value] = HistoryFilterArg.Node(child.Id); else parent.Args.Add(HistoryFilterArg.Node(child.Id)); Render(); Notify();
        };
        return control;
    }
    private IEnumerable<FrameworkElement> Parameters(HistoryFilterNode node)
    {
        void Update(int index, object? value) { node.Args[index] = HistoryFilterArg.Param(value); Notify(); }
        if (node.Type is "player" or "allies" or "enemies" or "hasPlayer") { yield return PlayerSearch(node.Args[0].StringValue, value => { if (value is not null) CachePlayer(value); Update(0, value); Render(); }, true); yield break; }
        if (node.Type == "isLoss") { var surrender = new CheckBox { Content = L("surrenderLoss"), IsChecked = node.Args[0].Value.ValueKind == JsonValueKind.True }; surrender.Click += (_, _) => Update(0, surrender.IsChecked == true); yield return surrender; yield break; }
        if (node.Type is "isGameMode" or "isPosition" or "gameCreationInTimeRange")
        {
            var choices = node.Type == "isPosition" ? PositionChoices() : node.Type == "gameCreationInTimeRange" ? TimeChoices() : HistoryFilterEditorData.GameModes.Select(mode => (mode, L("gameModes." + mode, mode) + " (" + mode + ")")).ToArray();
            if (!choices.Any(c => c.Item1 == node.Args[0].StringValue) && node.Args[0].StringValue is { } saved) choices = choices.Append((saved, saved)).ToArray();
            yield return Select(L("selectCondition"), choices, node.Args[0].StringValue, value => Update(0, value)); yield break;
        }
        if (node.Type == "durationBetween" || HistoryFilter.NumberStats.ContainsKey(node.Type))
        {
            var range = HistoryFilterEditorData.Range(node);
            if (HistoryFilterEditorData.SupportsMeasure(node.Type))
                yield return Select(L("measureMode"), new[] { "value", "teamShare", "teamMaxShare", "gameShare", "gameMaxShare" }.Select(mode => (mode, L("measureModes." + mode))), range.Mode, value => { HistoryFilterEditorData.SetMeasure(node, value); Render(); Notify(); });
            var warning = Text(T("最小值大于最大值，这个条件不会匹配任何对局。", "Minimum exceeds maximum; this condition will match no games."));
            void CheckRange() { var current = HistoryFilterEditorData.Range(node); warning.Visibility = current.Min > current.Max ? Visibility.Visible : Visibility.Collapsed; }
            yield return Number(L(range.Mode == "value" ? "min" : "minPercent"), range.Min, value => { HistoryFilterEditorData.SetRange(node, value, HistoryFilterEditorData.Range(node).Max); CheckRange(); Notify(); });
            yield return Number(L(range.Mode == "value" ? "max" : "maxPercent"), range.Max, value => { HistoryFilterEditorData.SetRange(node, HistoryFilterEditorData.Range(node).Min, value); CheckRange(); Notify(); });
            CheckRange(); yield return warning; yield break;
        }
        if (node.Type is "isChampion" or "hasItem" or "hasSpell" or "hasPerk" or "hasPerkStyle" or "hasAugment" or "isQueue" or "isMap")
        {
            string catalog = node.Type switch { "isChampion" => "champions", "hasItem" => "items", "hasSpell" => "summonerSpells", "hasPerk" => "perks", "hasPerkStyle" => "perkStyles", "hasAugment" => "augments", "isQueue" => "queues", _ => "maps" };
            yield return CatalogPicker(catalog, node.Args[0].Value, value => Update(0, value));
            if (node.Args.Count > 1) yield return Select(T("出现位置", "Slot"), Enumerable.Range(-1, HistoryFilterEditorData.SlotCount(node.Type) + 1).Select(i => (i.ToString(), i == -1 ? L("anyPosition", T("任意位置", "Any slot")) : node.Type == "hasPerkStyle" ? L(i == 0 ? "primaryStyle" : "subStyle") : T($"第 {i + 1} 个", $"Slot {i + 1}"))), node.Args[1].Value.TryNumber(-1).ToString(), value => Update(1, int.Parse(value)));
        }
    }
    private NumberBox Number(string header, double value, Action<double> changed)
    {
        var box = new NumberBox { Header = header, Value = value, Width = 170, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact }; box.ValueChanged += (_, args) => { if (!_rendering && double.IsFinite(args.NewValue)) changed(args.NewValue); }; return box;
    }
    private IEnumerable<JsonElement> Catalog(string name)
    {
        return HistoryFilterEditorData.Catalog(_catalog, name).Select(v => v.Data);
    }
    private FrameworkElement CatalogLabel(string name, HistoryFilterCatalogEntry value)
    {
        var image = Icon(name == "champions" ? $"/lol-game-data/assets/v1/champion-icons/{value.Id}.png" : value.Data.Text("iconPath", value.Data.Text("icon")));
        var row = Row(); if (name is not ("queues" or "maps")) row.Children.Add(image);
        row.Children.Add(Text($"{value.Name} · {value.Id}"));
        if (name == "augments" && value.Group != "other") row.Children.Add(Text(L(value.Group == "kiwi" ? "augmentTypeHai" : "augmentTypeDou")));
        string description = value.Data.Text("descTRA", value.Data.Text("description"));
        if (description.Length > 0) ToolTipService.SetToolTip(row, System.Text.RegularExpressions.Regex.Replace(description, "<[^>]*>", ""));
        return row;
    }
    private FrameworkElement CatalogPicker(string name, JsonElement current, Action<int?> changed)
    {
        var panel = Panel(); var search = new TextBox { PlaceholderText = name == "champions" ? T("搜索名称、拼音、别名或编号", "Search name, pinyin, alias or ID") : T("按名称或编号搜索", "Search name or ID"), MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left }; var select = new ComboBox { MinWidth = 220, MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left }; bool loading = false;
        var idBox = new NumberBox { Header = T("编号", "ID"), Value = current.ValueKind == JsonValueKind.Number ? current.TryNumber() : double.NaN, Minimum = 0, Maximum = int.MaxValue, Width = 170 };
        void RenderList()
        {
            loading = true; select.Items.Clear(); var empty = new ComboBoxItem { Content = T("请选择…", "Select…"), Tag = null!, IsEnabled = name == "augments" }; select.Items.Add(empty); int selected = (int)current.TryNumber(-1); if (selected < 0) select.SelectedItem = empty;
            var values = HistoryFilterEditorData.Catalog(_catalog, name).ToList();
            if (selected >= 0 && values.All(v => v.Id != selected)) values.Add(new(selected, name == "queues" ? MatchData.QueueLabel(selected) : T("编号 ", "ID ") + selected, "other", default));
            string? previousGroup = null;
            foreach (var value in values.Where(v => name == "champions" ? ChampionMatches(v.Id, v.Name, search.Text) : (v.Name + v.Id).Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
            {
                if (name == "augments" && value.Group != previousGroup)
                {
                    previousGroup = value.Group;
                    select.Items.Add(new ComboBoxItem { Content = L(value.Group switch { "kiwi" => "augmentGroupHextech", "cherry" => "augmentGroupArena", _ => "augmentGroupOther" }), IsEnabled = false });
                }
                var item = new ComboBoxItem { Content = CatalogLabel(name, value), Tag = value.Id }; select.Items.Add(item); if (value.Id == selected) select.SelectedItem = item;
            }
            loading = false;
        }
        select.SelectionChanged += (_, _) => { if (loading || _rendering || select.SelectedItem is not ComboBoxItem item) return; int? id = item.Tag is int value ? value : null; current = JsonSerializer.SerializeToElement(id); loading = true; idBox.Value = id.HasValue ? id.Value : double.NaN; loading = false; changed(id); };
        search.TextChanged += (_, _) => RenderList(); RenderList(); panel.Children.Add(search); panel.Children.Add(select);
        // 新补丁数据未进入目录时仍可通过编号选择，避免把版本差异变成无法编辑的条件。
        idBox.ValueChanged += (_, args) => { if (!_rendering && !loading && double.IsFinite(args.NewValue)) { current = JsonSerializer.SerializeToElement((int)args.NewValue); changed((int)args.NewValue); RenderList(); } }; panel.Children.Add(idBox); return panel;
    }
    private string PlayerLabel(string id)
    {
        var cached = Settings.Advanced.CachedSummoners.GetValueOrDefault(id, Settings.Simple.CachedSummoners.GetValueOrDefault(id, _pageSummoners.GetValueOrDefault(id))); return cached.ValueKind == JsonValueKind.Object ? cached.Text("gameName") + " #" + cached.Text("tagLine") : id == _puuid ? T("当前玩家", "Current player") : id;
    }
    private void CachePlayer(string id)
    {
        if (!_pageSummoners.TryGetValue(id, out var player)) return;
        if (Settings.Mode == "advanced") Settings.Advanced.CachedSummoners[id] = player; else Settings.Simple.CachedSummoners[id] = player;
    }
    private FrameworkElement PlayerSearch(string? selected, Action<string?> choose, bool clearable = false)
    {
        var panel = Panel();
        if (selected is not null)
        {
            var selection = new StackPanel { Spacing = 6 }; selection.Children.Add(Text(T("已选择：", "Selected: ") + PlayerLabel(selected)));
            if (clearable) selection.Children.Add(Button(T("清除选择", "Clear selection"), () => choose(null))); panel.Children.Add(selection);
        }
        var input = new AutoSuggestBox { PlaceholderText = T("玩家名#编号", "Name#tag"), TextMemberPath = nameof(HistoryFilterPlayerSuggestion.Label), MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Stretch };
        var progress = new ProgressRing { Width = 18, Height = 18, IsActive = false, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
        var status = Text(""); int revision = _revision, inputRevision = 0;
        var search = new HistoryFilterPlayerSearch(async alias =>
        {
            if (_findPlayer is not null) return await _findPlayer(alias);
            var pieces = alias.Split('#');
            return (await _backend.CallAsync("winui-backend", "lcuRequest", "POST", "/lol-summoner/v1/summoners/aliases", new[] { new { gameName = pieces[0], tagLine = pieces[1] } })).Items().FirstOrDefault();
        });
        _searches.Add(search);
        bool Current() => _loaded && revision == _revision;
        void Suggestions(bool open)
        {
            var cached = Settings.Mode == "advanced" ? Settings.Advanced.CachedSummoners : Settings.Simple.CachedSummoners;
            var suggestions = HistoryFilterEditorData.PlayerSuggestions(cached.Values, _pageSummoners.Values, _puuid, input.Text);
            input.ItemsSource = suggestions;
            if (open && input.FocusState != FocusState.Unfocused) input.IsSuggestionListOpen = suggestions.Length > 0;
        }
        void ChooseSuggestion(object? item)
        {
            if (item is not HistoryFilterPlayerSuggestion suggestion || !Current()) return;
            inputRevision++; search.Cancel(); CachePlayer(suggestion.Puuid); choose(suggestion.Puuid); Notify();
        }
        input.TextChanged += async (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || !Current()) return;
            int request = ++inputRevision;
            status.Text = ""; Suggestions(true);
            var result = await search.SearchAsync(input.Text, busy => { progress.IsActive = busy; progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; });
            if (!Current() || request != inputRevision || result is null) return;
            if (result.Error is not null) { status.Text = result.Error == "not-found" ? T("未找到玩家", "Player not found") : result.Error; return; }
            string id = result.Player.Text("puuid");
            if (Settings.Mode == "advanced") Settings.Advanced.CachedSummoners[id] = result.Player; else Settings.Simple.CachedSummoners[id] = result.Player;
            status.Text = ""; Suggestions(true); Notify();
        };
        input.SuggestionChosen += (_, args) => ChooseSuggestion(args.SelectedItem);
        input.QuerySubmitted += (_, args) => ChooseSuggestion(args.ChosenSuggestion);
        input.GotFocus += (_, _) => Suggestions(true);
        Suggestions(false); panel.Children.Add(input); panel.Children.Add(progress); panel.Children.Add(status);
        panel.Children.Add(Button(T("选择当前玩家", "Select current player"), () => { inputRevision++; search.Cancel(); CachePlayer(_puuid); choose(_puuid); }));
        return panel;
    }
}

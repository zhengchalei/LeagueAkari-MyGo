using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinRT.Interop;
using Localization = LeagueAkari.WinUI.Services.Localization;
using NativeAppearance = LeagueAkari.WinUI.Services.NativeAppearance;
using MiniViewData = LeagueAkari.WinUI.Services.MiniViewData;

namespace LeagueAkari.WinUI.Windows;

/// <summary>Native Mini. Backend snapshots own all selection state; buttons only dispatch intents.</summary>
public sealed class MiniWindow : Window
{
    private readonly Func<Task<JsonElement>> _snapshot;
    private readonly Func<string, int, bool, Task> _action;
    private readonly Func<string, Task<byte[]?>> _loadImage;
    private readonly Func<string, JsonElement, Task> _saveSetting;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _saveBounds = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly StackPanel _body = new() { Spacing = 8, Padding = new Thickness(10) };
    private readonly StackPanel _controls = new() { Spacing = 8 };
    private readonly StackPanel _plans = new() { Spacing = 5 };
    private readonly TextBlock _status = Text(Localization.Text("等待英雄联盟客户端连接", "Waiting for League client connection"), 11);
    private readonly TextBlock _error = Text("", 11);
    private readonly ToggleButton _pin = new() { Content = Localization.Text("置顶", "Pin"), MinWidth = 48, Padding = new Thickness(6) };
    private readonly Dictionary<string, (BitmapImage Image, long Access)> _images = [];
    private readonly Dictionary<string, Task<BitmapImage?>> _pendingImages = [];
    private readonly ScrollViewer _scroll;
    private GridView? _skinGrid;
    private int _skinHero;
    private double _skinScrollOffset;
    private string _renderSignature = "", _controlsSignature = "";
    private bool _visible, _refreshing, _dispatching, _shutdown, _applyingPin;
    private bool _english;
    private long _imageAccess;
    private double _opacity = 1;
    private IDisposable? _appearanceBinding;
    private readonly TextBlock _heading = Text(Localization.Text("选人助手", "Champion assistant"), 15, true);
    private Button? _options;
    private readonly LeagueAkari.WinUI.Services.MiniInteractionState _interaction = new();
    private readonly List<(Control Control, bool Enabled)> _interactive = [];
    private readonly List<(Control Control, bool Enabled)> _selectionInteractive = [];
    private readonly TextBlock _pending = Text("", 11);
    private TextBlock? _skinStatus;
    private JsonElement _lastSnapshot;

    public MiniWindow(Func<Task<JsonElement>> getSnapshot,
        Func<string, int, bool, Task> action,
        Func<string, Task<byte[]?>> loadImage,
        Func<string, JsonElement, Task> saveSetting,
        JsonElement savedSettings = default)
    {
        _snapshot = getSnapshot;
        _action = action;
        _loadImage = loadImage;
        _saveSetting = saveSetting;
        _opacity = Math.Clamp(savedSettings.Number("opacity", 1), .1, 1);
        Title = "LeagueAkari-MyGo · Mini";
        string icon = System.IO.Path.Combine(AppContext.BaseDirectory, "LA_ICON.ico");
        if (System.IO.File.Exists(icon)) AppWindow.SetIcon(icon);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        toolbar.Children.Add(_heading);
        toolbar.Children.Add(_pin);
        var options = new Button { Content = "⋯", Padding = new Thickness(8, 4, 8, 4) };
        _options = options;
        options.Flyout = OptionsFlyout();
        toolbar.Children.Add(options);
        _body.Children.Add(toolbar);
        var selection = new StackPanel { Spacing = 8, Name = "Selection" };
        _body.Children.Add(selection);
        _body.Children.Add(_plans);
        _body.Children.Add(_controls);
        _body.Children.Add(_status);
        _body.Children.Add(_pending);
        _body.Children.Add(_error);
        _error.Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 210, 51, 64));
        _scroll = new ScrollViewer { Content = _body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Content = _scroll;
        _appearanceBinding = NativeAppearance.Current?.Watch(this, _scroll, "window-manager-main/aux-window");
        Localization.Changed += AppearanceChanged;
        if (NativeAppearance.Current is { } appearance) appearance.Changed += AppearanceChanged;
        Closed += (_, _) => { _appearanceBinding?.Dispose(); Localization.Changed -= AppearanceChanged; if (NativeAppearance.Current is { } current) current.Changed -= AppearanceChanged; };
        AppWindow.Resize(new SizeInt32(340, 620));
        RestoreBounds(savedSettings.Field("trackedBounds"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = savedSettings.Flag("pinned");
        }
        _pin.IsChecked = savedSettings.Flag("pinned");
        SetOpacity(_opacity);
        _pin.Checked += async (_, _) => await SetPinnedAsync(true);
        _pin.Unchecked += async (_, _) => await SetPinnedAsync(false);
        _refresh.Tick += async (_, _) => await RefreshAsync();
        _saveBounds.Tick += async (_, _) => { _saveBounds.Stop(); await PersistBoundsAsync(); };
        AppWindow.Changed += (_, args) =>
        {
            if (!args.DidSizeChange && !args.DidPositionChange) return;
            if (AppWindow.Size.Width < 340 || AppWindow.Size.Height < 420)
                AppWindow.Resize(new SizeInt32(Math.Max(340, AppWindow.Size.Width), Math.Max(420, AppWindow.Size.Height)));
            _saveBounds.Stop();
            _saveBounds.Start();
        };
        AppWindow.Closing += (_, args) =>
        {
            if (_shutdown) return;
            args.Cancel = true;
            SetVisible(false);
        };
    }

    public bool IsVisible => _visible;

    private void AppearanceChanged()
    {
        _english = Localization.IsEnglish;
        _heading.Text = T("选人助手", "Champion assistant");
        _status.Text = T("等待英雄联盟客户端连接", "Waiting for League client connection");
        if (_options != null) _options.Flyout = OptionsFlyout();
        _renderSignature = _controlsSignature = "";
        if (_visible) _ = RefreshAsync();
    }

    public void ApplySettings(JsonElement settings)
    {
        _opacity = Math.Clamp(settings.Number("opacity", 1), .1, 1);
        SetOpacity(_opacity);
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = settings.Flag("pinned");
        _applyingPin = true;
        _pin.IsChecked = settings.Flag("pinned");
        _applyingPin = false;
        _ = RefreshAsync();
    }

    public void Show() => SetVisible(true);

    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (visible)
        {
            Activate();
            AppWindow.Show();
            _refresh.Start();
            _ = RefreshAsync();
        }
        else
        {
            _refresh.Stop();
            AppWindow.Hide();
            _ = PersistBoundsAsync();
        }
    }

    public void Shutdown()
    {
        _shutdown = true;
        _refresh.Stop();
        _saveBounds.Stop();
        _images.Clear();
        Close();
    }

    public async Task<object?> HandleAsync(string method, JsonElement[] args)
    {
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        switch (method)
        {
            case "ensure": return null;
            case "show": SetVisible(true); return null;
            case "restore": presenter?.Restore(); SetVisible(true); return null;
            case "toggle": SetVisible(!IsVisible); return null;
            case "hide": case "close": SetVisible(false); return null;
            case "minimize": presenter?.Minimize(); return null;
            case "maximize": presenter?.Maximize(); return null;
            case "unmaximize": presenter?.Restore(); return null;
            case "getSize": return new[] { AppWindow.Size.Width, AppWindow.Size.Height };
            case "getPosition": return new[] { AppWindow.Position.X, AppWindow.Position.Y };
            case "setTitle": Title = args[0].GetString() ?? ""; return null;
            case "setSize": AppWindow.Resize(new SizeInt32((int)args[0].GetDouble(), (int)args[1].GetDouble())); return null;
            case "setPosition": AppWindow.Move(new PointInt32((int)args[0].GetDouble(), (int)args[1].GetDouble())); return null;
            case "resetPosition": case "repositionWindowIfInvisible": Snap("center"); return null;
            case "setPinned": await SetPinnedAsync(args[0].ValueKind == JsonValueKind.True); return null;
            case "setOpacity": _opacity = args[0].GetDouble(); SetOpacity(_opacity); await _saveSetting("opacity", JsonSerializer.SerializeToElement(_opacity)); return null;
            case "applySettings": await RefreshAsync(); return null;
            case "setIgnoreMouseEvents":
                var hwnd = WindowNative.GetWindowHandle(this);
                var style = GetWindowLongPtr(hwnd, -20).ToInt64();
                SetWindowLongPtr(hwnd, -20, new IntPtr(args[0].ValueKind == JsonValueKind.True ? style | 0x80020 : style & ~0x20)); return null;
            case "toggleDevtools": return null;
            default: throw new NotSupportedException($"原生 Mini 不支持：{method}");
        }
    }

    public async Task RefreshAsync()
    {
        if (!_visible || _refreshing || AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return;
        _refreshing = true;
        try
        {
            var envelope = await _snapshot();
            if (!_visible) return;
            var snapshot = envelope.Field("snapshot");
            _lastSnapshot = snapshot.CloneOrDefault();
            _interaction.Observe(snapshot);
            var controls = ProjectControls(envelope);
            _english = NativeAppearance.Current == null ? controls.String("Locale").StartsWith("en", StringComparison.OrdinalIgnoreCase) : Localization.IsEnglish;
            var theme = NativeAppearance.Current?.ThemeId ?? controls.String("Theme");
            _body.RequestedTheme = theme is "dark" or "graphite" or "cyber" or "aurora" ? ElementTheme.Dark :
                theme is "light" or "sakura" or "butter" or "mint" ? ElementTheme.Light : ElementTheme.Default;
            _applyingPin = true;
            var pinned = controls.Flag("Pinned");
            _pin.IsChecked = pinned;
            _pin.Content = T(pinned ? "已置顶" : "置顶", pinned ? "Pinned" : "Pin");
            if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = pinned;
            _applyingPin = false;
            // Preserve virtualized scroll containers while countdown-only controls change.
            var signature = MiniViewData.SelectionSignature(snapshot, envelope, _english, theme);
            if (signature != _renderSignature)
            {
                _renderSignature = signature;
                RenderSelection(snapshot, envelope);
            }
            RenderPlans(envelope, controls);
            if (controls.String("QueueText") != "") _plans.Children.Add(Text(controls.String("QueueText"), 11));
            if (controls.String("LoungeStatus") != "") _plans.Children.Add(Text(controls.String("LoungeStatus"), 11));
            if (controls.Flag("DodgeLooping")) _plans.Children.Add(Text(T("秒退尝试", "Dodge attempts") + " · " + controls.Int("DodgeIterations"), 11));
            var stableControls = controls.ValueKind == JsonValueKind.Object ? string.Join("|", controls.EnumerateObject().Where(p => p.Name is not ("Plans" or "QueueText" or "LoungeStatus" or "DodgeIterations")).Select(p => p.ToString())) : "";
            var controlsSignature = stableControls + snapshot.String("Phase") + _english;
            if (controlsSignature != _controlsSignature)
            {
                _controlsSignature = controlsSignature;
                RenderControls(snapshot, controls, envelope);
            }
            _status.Text = _english ? snapshot.Flag("Connected") ? PhaseLabel(snapshot.String("Phase")) : "Waiting for League client connection" : snapshot.String("Status");
            ApplyBusyState(); UpdateFeedback();
        }
        catch (Exception error) { _error.Text = error.Message; }
        finally { _refreshing = false; _applyingPin = false; }
    }

    private void RenderSelection(JsonElement snapshot, JsonElement envelope)
    {
        var panel = (StackPanel)_body.Children[1];
        _skinScrollOffset = _skinHero == snapshot.Int("ChampionID") ? FindScrollViewer(_skinGrid)?.VerticalOffset ?? _skinScrollOffset : 0;
        _skinHero = snapshot.Int("ChampionID");
        panel.Children.Clear();
        _skinGrid = null;
        _skinStatus = null;
        _selectionInteractive.Clear();
        if (!snapshot.Flag("Connected")) return;
        var phase = snapshot.String("Phase");
        if (phase != "ChampSelect" || envelope.Field("state").Field("champSelect").Field("session").Flag("isSpectating"))
        {
            var lounge = new StackPanel { Spacing = 8 };
            lounge.Children.Add(Text(PhaseLabel(phase), 16, true));
            var state = envelope.Field("state");
            var flow = state.Field("gameflow").Field("session");
            var queue = flow.Field("gameData").Field("queue").String("name");
            var map = flow.Field("map").String("name");
            if (queue != "" || map != "") lounge.Children.Add(Text(queue == map ? queue : string.Join(" · ", new[] { queue, map }.Where(x => x != ""))));
            var path = flow.Field("map").Field("assets").String("game-select-icon-hover");
            if (path != "") lounge.Children.Add(Picture(path, 64, 64));
            panel.Children.Add(Card(lounge));
            return;
        }
        var champion = new StackPanel { Spacing = 7 };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heading.Children.Add(Picture(snapshot.String("IconPath"), 42, 42));
        var title = new StackPanel { Spacing = 2 };
        title.Children.Add(Text(snapshot.Int("ChampionID") > 0 ? snapshot.String("ChampionName") : T("选择你的英雄", "Choose your champion"), 15, true));
        title.Children.Add(Text(snapshot.Int("ChampionID") > 0 ? T("当前英雄 · 已选择", "Current champion · selected") : T("点击头像确定英雄", "Click a portrait to confirm"), 11));
        heading.Children.Add(title);
        champion.Children.Add(heading);
        var choices = snapshot.Field("Choices").Array();
        var grid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        var columns = Math.Max(1, Math.Min(5, choices.Length));
        for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < (choices.Length + columns - 1) / columns; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < choices.Length; i++)
        {
            var choice = choices[i];
            var content = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(Picture(choice.String("IconPath"), 38, 38));
            content.Children.Add(Text(choice.String("Name"), 10));
            var counts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            if (choice.Number("Buffs") > 0) counts.Children.Add(ColoredText("+" + choice.Int("Buffs"), true, 10));
            if (choice.Number("Nerfs") > 0) counts.Children.Add(ColoredText("−" + choice.Int("Nerfs"), false, 10));
            content.Children.Add(counts);
            var id = choice.Int("ID");
            var button = Interactive(new Button
            {
                Content = content,
                Padding = new Thickness(3),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsEnabled = choice.Flag("Enabled"),
                BorderThickness = new Thickness(choice.Flag("Selected") ? 2 : 1)
            }, selection: true);
            if (choice.Flag("Selected")) button.BorderBrush = Accent();
            var tooltip = new StackPanel { Spacing = 4 };
            tooltip.Children.Add(Text(choice.String("Name"), 12, true));
            foreach (var entry in choice.Field("Balance").Array())
            {
                var detail = new Grid { ColumnSpacing = 12 };
                detail.ColumnDefinitions.Add(new ColumnDefinition()); detail.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                detail.Children.Add(Text(_english ? EnglishBalance(entry.String("Type"), entry.String("Name")) : entry.String("Name"), 11));
                var value = Text(entry.String("Value"), 11); Grid.SetColumn(value, 1); detail.Children.Add(value); tooltip.Children.Add(detail);
            }
            if (choice.Field("Balance").Array().Length == 0 && choice.Flag("BalanceKnown")) tooltip.Children.Add(Text(T("数据源未列出该英雄的调整", "Source lists no changes for this champion"), 11));
            foreach (var note in choice.Field("Notes").Array()) tooltip.Children.Add(Text(note.ToString(), 11));
            ToolTipService.SetToolTip(button, tooltip);
            button.Click += async (_, _) => { if (!choice.Flag("Selected")) await DispatchAsync("champion", id); };
            var contextMenu = new MenuFlyout();
            var preview = Interactive(new MenuFlyoutItem { Text = T("预选英雄", "Preview champion"), IsEnabled = choice.Flag("Enabled") }, selection: true);
            preview.Click += async (_, _) => { if (!choice.Flag("Selected")) await DispatchAsync("champion-preview", id); };
            contextMenu.Items.Add(preview);
            button.ContextFlyout = contextMenu;
            Grid.SetColumn(button, i % columns);
            Grid.SetRow(button, i / columns);
            grid.Children.Add(button);
        }
        champion.Children.Add(grid);
        if (MiniViewData.ShowReroll(snapshot, envelope))
        {
            var reroll = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            reroll.Children.Add(ActionButton(T("重随机", "Reroll") + $" ({snapshot.Int("Rerolls")})", "reroll", snapshot.Flag("CanReroll"), selection: true));
            reroll.Children.Add(ActionButton(T("重随机并取回", "Reroll & take back"), "reroll-grab-back", snapshot.Flag("CanReroll"), selection: true));
            champion.Children.Add(reroll);
        }
        panel.Children.Add(Card(champion));
        if (snapshot.Int("ChampionID") <= 0) return;
        var balance = new StackPanel { Spacing = 4 };
        balance.Children.Add(Text(T("增益 / 减益", "Buffs / nerfs"), 12, true));
        var adjustments = snapshot.Field("Balance").Array();
        foreach (var row in adjustments)
        {
            var buffed = row.String("Effect") == "buffed";
            var neutral = row.String("Effect") is not ("buffed" or "nerfed");
            var color = neutral ? global::Windows.UI.Color.FromArgb(255, 128, 128, 128) : BalanceColor(buffed);
            var line = new Grid { ColumnSpacing = 6, Padding = new Thickness(6), Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(18, color.R, color.G, color.B)) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var effect = Text(neutral ? T("调整", "Change") : T(buffed ? "增益" : "减益", buffed ? "Buff" : "Nerf"), 10);
            effect.Foreground = new SolidColorBrush(color);
            line.Children.Add(effect);
            var label = Text(_english ? EnglishBalance(row.String("Type"), row.String("Name")) : row.String("Name"), 12);
            Grid.SetColumn(label, 1);
            line.Children.Add(label);
            var value = Text(row.String("Value"), 21, true);
            value.Foreground = new SolidColorBrush(color);
            ToolTipService.SetToolTip(value, row.String("Original"));
            Grid.SetColumn(value, 2);
            line.Children.Add(value);
            balance.Children.Add(line);
        }
        if (adjustments.Length == 0) balance.Children.Add(Text(snapshot.Flag("BalanceKnown") ?
            T("数据源未列出该英雄的调整", "Source lists no changes for this champion") : T("暂无此模式独立调整数据", "No independent data for this mode"), 11));
        foreach (var note in snapshot.Field("Notes").Array()) balance.Children.Add(Text(note.ToString(), 11));
        var source = Text(snapshot.String("Source") + " " + snapshot.String("Version") + (snapshot.Flag("Cached") ? T(" · 缓存", " · cached") : ""), 10);
        ToolTipService.SetToolTip(source, snapshot.String("SourceURL"));
        balance.Children.Add(source);
        panel.Children.Add(Card(balance));
        if (snapshot.Flag("ShowSkins")) panel.Children.Add(BuildSkins(snapshot));
    }

    private Border BuildSkins(JsonElement snapshot)
    {
        var content = new StackPanel { Spacing = 5 };
        var skins = LeagueAkari.WinUI.Services.MiniSkinData.Owned(snapshot).Select(row => new SkinRow(row.Id, row.Name, row.ImagePath, row.Selected, row.Enabled)).ToArray();
        content.Children.Add(Text(T("已有皮肤", "Owned skins") + $" ({skins.Length})", 12, true));
        _skinStatus = Text(_interaction.SkinStatus(snapshot, _english), 11);
        if (snapshot.Flag("SkinsLoading")) { content.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20 }); content.Children.Add(_skinStatus); return Card(content); }
        if (skins.Length == 0) { content.Children.Add(_skinStatus); return Card(content); }
        var grid = Interactive(new GridView
        {
            ItemsSource = skins,
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = true,
            Height = Math.Min(228, Math.Ceiling(skins.Length / 3d) * 90),
            ItemTemplate = (DataTemplate)XamlReader.Load("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                  <Border CornerRadius="4" BorderThickness="1" Padding="3" Width="86" Height="84">
                    <StackPanel Spacing="3"><Image Width="78" Height="48" Stretch="UniformToFill"/><TextBlock FontSize="10" TextWrapping="Wrap" MaxLines="2" TextAlignment="Center"/></StackPanel>
                  </Border>
                </DataTemplate>
                """),
            ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("""
                <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><ItemsWrapGrid Orientation="Horizontal" MaximumRowsOrColumns="3"/></ItemsPanelTemplate>
                """)
        }, selection: true);
        _skinGrid = grid;
        ScrollViewer.SetHorizontalScrollMode(grid, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Disabled);
        var savedOffset = _skinScrollOffset;
        void FitSkins()
        {
            var columns = LeagueAkari.WinUI.Services.MiniSkinData.Columns(grid.ActualWidth);
            if (grid.ItemsPanelRoot is ItemsWrapGrid wrap) wrap.MaximumRowsOrColumns = columns;
            grid.Height = Math.Min(228, Math.Ceiling(skins.Length / (double)columns) * 90);
        }
        grid.Loaded += (_, _) => { FitSkins(); FindScrollViewer(grid)?.ChangeView(null, savedOffset, null, true); };
        grid.SizeChanged += (_, _) => FitSkins();
        grid.ContainerContentChanging += async (_, args) =>
        {
            if (args.ItemContainer.ContentTemplateRoot is not Border)
            {
                if (!args.InRecycleQueue && args.Phase == 0)
                    args.RegisterUpdateCallback(async (_, next) => await PopulateSkinAsync(next));
                return;
            }
            await PopulateSkinAsync(args);
        };
        grid.ItemClick += async (_, args) => { if (args.ClickedItem is SkinRow { Enabled: true, Selected: false } skin) await DispatchAsync("skin", skin.ID); };
        content.Children.Add(grid);
        content.Children.Add(_skinStatus);
        return Card(content);
    }

    private async Task PopulateSkinAsync(ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is not Border border || border.Child is not StackPanel stack) return;
        var picture = (Image)stack.Children[0];
        picture.Source = null;
        if (args.InRecycleQueue || args.Item is not SkinRow skin) { picture.Tag = null; return; }
        picture.Tag = skin;
        ((TextBlock)stack.Children[1]).Text = skin.Name;
        border.BorderBrush = skin.Selected ? Accent() : new SolidColorBrush(global::Windows.UI.Color.FromArgb(100, 140, 140, 140));
        border.BorderThickness = new Thickness(skin.Selected ? 2 : 1);
        args.ItemContainer.IsEnabled = skin.Enabled;
        border.Opacity = skin.Enabled ? 1 : .55;
        ToolTipService.SetToolTip(args.ItemContainer, skin.Name);
        AutomationProperties.SetName(args.ItemContainer, skin.Name + (skin.Selected ? T("，已选择", ", selected") : ""));
        args.Handled = true;
        var bitmap = await LoadBitmapAsync(skin.ImagePath);
        if (ReferenceEquals(picture.Tag, skin)) picture.Source = bitmap;
    }

    private void RenderPlans(JsonElement envelope, JsonElement controls)
    {
        _plans.Children.Clear();
        var state = envelope.Field("state");
        var championData = state.Field("gameData").Field("champions");
        var selection = envelope.Field("automation").Field("auto-select-main");
        var count = 0;
        foreach (var item in new[] { ("delayedPick", "自动选择", "Automatic pick"), ("delayedBan", "自动禁用", "Automatic ban"), ("delayedBenchSwap", "自动交换", "Automatic bench swap"), ("delayedChampionSwap", "自动接受交换", "Automatic champion trade") })
        {
            var plan = selection.Field(item.Item1);
            if (plan.ValueKind != JsonValueKind.Object) continue;
            var hero = plan.Int(item.Item1 == "delayedChampionSwap" ? "requesterChampionId" : "championId");
            var data = championData.Field(hero.ToString());
            var seconds = Math.Max(0, (plan.Number("finishAt") - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000);
            var duration = Math.Max(1, plan.Number("finishAt") - plan.Number("startAt"));
            var block = new StackPanel { Spacing = 4 };
            block.Children.Add(Text(T(item.Item2, item.Item3), 12, true));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            row.Children.Add(Picture(data.String("squarePortraitPath"), 18, 18));
            row.Children.Add(Text(data.String("name") + $" · {seconds:0.0}s", 11));
            block.Children.Add(row);
            block.Children.Add(new ProgressBar { Value = Math.Clamp(seconds * 1000 / duration * 100, 0, 100), Height = 3 });
            _plans.Children.Add(Card(block));
            count++;
        }
        var gameflow = envelope.Field("automation").Field("auto-gameflow-main");
        if (gameflow.Flag("willAccept")) _plans.Children.Add(Text(T("自动接受对局", "Automatic accept") + $" · {Math.Max(0, (gameflow.Number("willAcceptAt") - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000):0.0}s", 11));
        if (count == 0 && selection.ValueKind == JsonValueKind.Undefined && gameflow.ValueKind == JsonValueKind.Undefined)
            foreach (var plan in controls.Field("Plans").Array()) _plans.Children.Add(Text(Localization.Translate(plan.ToString()), 11));
    }

    private void RenderControls(JsonElement snapshot, JsonElement controls, JsonElement envelope)
    {
        _controls.Children.Clear();
        _interactive.Clear();
        var actions = new StackPanel { Spacing = 6 };
        if (controls.Flag("CanAccept") || snapshot.String("Phase") == "ReadyCheck")
        {
            var response = controls.String("PlayerResponse");
            if (response != "") actions.Children.Add(Text(response == "Accepted" ? T("已接受，等待其他玩家", "Accepted, waiting for other players") : response == "Declined" ? T("已拒绝", "Declined") : T("已找到对局", "Match found"), 13, true));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            buttons.Children.Add(ActionButton(T("接受对局", "Accept"), "accept"));
            buttons.Children.Add(ActionButton(T("拒绝", "Decline"), "decline"));
            actions.Children.Add(buttons);
        }
        if (controls.Flag("CanCancel")) actions.Children.Add(ActionButton(T("取消匹配", "Cancel queue"), "cancel-queue"));
        if (controls.Flag("CanCancelAutoAccept")) actions.Children.Add(ActionButton(T("取消自动接受", "Cancel automatic accept"), "cancel-auto-accept"));
        if (controls.Flag("CanCancelAutoMatchmaking")) actions.Children.Add(ActionButton(T("取消自动匹配", "Cancel automatic matchmaking"), "cancel-auto-matchmaking"));
        if (controls.Flag("CanDodge"))
        {
            var dodge = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var looping = controls.Flag("DodgeLooping");
            var count = controls.Int("DodgeIterations");
            dodge.Children.Add(ActionButton(T("立即秒退", "Dodge") + (count > 0 ? $" ({count})" : ""), "start-dodge-loop", !looping));
            if (looping) dodge.Children.Add(ActionButton(T("取消", "Cancel"), "cancel-dodge-loop"));
            actions.Children.Add(dodge);
            var pause = Interactive(new ToggleSwitch { Header = T("临时取消自动选择/禁用", "Pause automatic pick / ban"), IsOn = controls.Flag("TemporarilyDisabled"), FontSize = 11 });
            pause.Toggled += async (_, _) => await DispatchAsync("disable-auto", value: pause.IsOn);
            actions.Children.Add(pause);
        }
        var timeline = controls.Field("Actions").Array();
        foreach (var step in timeline)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            if (step.Int("ChampionID") > 0) row.Children.Add(Picture(step.String("IconPath"), 18, 18));
            var kind = step.String("Type");
            var label = kind switch { "pick" => T("选择英雄", "Picking"), "ban" => T("禁用英雄", "Banning"), "vote" => T("投票", "Voting"), "ten_bans_reveal" or "phase_transition" or "vote_transition" or "team_vote_reveal" => Localization.Key("auxWindow.champSelect.actions.ceremonies." + kind), _ => kind };
            label += step.Flag("Completed") ? T("（已完成）", " (complete)") : step.Flag("IsInProgress") ? T("（进行中）", " (in progress)") : "";
            row.Children.Add(Text(label, 11));
            actions.Children.Add(row);
        }
        if (actions.Children.Count > 0) _controls.Children.Add(Card(actions));
        if (snapshot.String("Phase") is "Lobby" or "Matchmaking" or "ReadyCheck")
        {
            var lounge = new StackPanel { Spacing = 5 };
            foreach (var item in new[] { ("AutoAcceptEnabled", "自动接受", "Automatic accept", "set-auto-accept"), ("AutoMatchmakingEnabled", "自动匹配", "Automatic matchmaking", "set-auto-matchmaking"), ("AutoMatchmakingWaitForInvitees", "等待受邀玩家", "Wait for invitees", "set-wait-invitees") })
            {
                var toggle = Interactive(new ToggleSwitch { Header = T(item.Item2, item.Item3), IsOn = controls.Flag(item.Item1), FontSize = 11 });
                toggle.Toggled += async (_, _) => await DispatchAsync(item.Item4, value: toggle.IsOn);
                lounge.Children.Add(toggle);
            }
            var minimum = Interactive(new NumberBox { Header = T("匹配最少人数", "Minimum members"), Minimum = 1, Maximum = 99, Value = Math.Max(1, controls.Number("AutoMatchmakingMinimumMembers", 1)), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact });
            minimum.ValueChanged += async (_, _) => { if (!double.IsNaN(minimum.Value)) await DispatchAsync("set-auto-min-members", (int)minimum.Value); };
            var delay = Interactive(new NumberBox { Header = T("自动匹配延迟（秒）", "Matchmaking delay (seconds)"), Minimum = 0, Maximum = 300, Value = controls.Number("AutoMatchmakingDelaySeconds"), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact });
            delay.ValueChanged += async (_, _) => { if (!double.IsNaN(delay.Value)) await DispatchAsync("set-auto-delay", (int)Math.Round(delay.Value * 1000)); };
            var acceptDelay = Interactive(new NumberBox { Header = T("自动接受延迟（秒）", "Accept delay (seconds)"), Minimum = 0, Maximum = 300, Value = controls.Number("AutoAcceptDelaySeconds"), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact });
            acceptDelay.ValueChanged += async (_, _) => { if (!double.IsNaN(acceptDelay.Value)) await DispatchAsync("set-auto-accept-delay", (int)Math.Round(acceptDelay.Value * 1000)); };
            if (controls.Flag("IsCustomGame")) lounge.Children.Add(Text(T("自定义对局中不可用", "Unavailable in custom games"), 11));
            lounge.Children.Add(minimum);
            lounge.Children.Add(delay);
            lounge.Children.Add(acceptDelay);
            _controls.Children.Add(Card(lounge));
        }
    }

    private async Task DispatchAsync(string kind, int id = 0, bool value = false)
    {
        if (_dispatching) return;
        if (kind == "dodge")
        {
            var confirmation = new ContentDialog { Title = T("退出英雄选择", "Leave champion select"), Content = T("确定退出当前英雄选择？可能产生等待惩罚。", "Leave champion select? Queue penalties may apply."), PrimaryButtonText = T("退出", "Leave"), CloseButtonText = T("取消", "Cancel"), DefaultButton = ContentDialogButton.Close, XamlRoot = _body.XamlRoot };
            if (await LeagueAkari.WinUI.Services.NativeDialogs.ShowAsync(confirmation) != ContentDialogResult.Primary) return;
        }
        _dispatching = true;
        long revision = _interaction.Begin(kind, id);
        ApplyBusyState(); UpdateFeedback();
        try { await _action(kind, id, value); _interaction.Complete(revision, ""); }
        catch (Exception error) { _interaction.Complete(revision, error.Message); }
        finally { _dispatching = false; ApplyBusyState(); UpdateFeedback(); await RefreshAsync(); }
    }

    private Button ActionButton(string label, string action, bool enabled = true, bool selection = false)
    {
        var button = Interactive(new Button { Content = label, FontSize = 11, Padding = new Thickness(8, 5, 8, 5), IsEnabled = enabled }, selection);
        button.Click += async (_, _) => await DispatchAsync(action);
        return button;
    }

    private TControl Interactive<TControl>(TControl control, bool selection = false) where TControl : Control
    {
        if (control is ToggleSwitch toggle) { toggle.OnContent = T("开", "On"); toggle.OffContent = T("关", "Off"); }
        (selection ? _selectionInteractive : _interactive).Add((control, control.IsEnabled));
        if (_dispatching) control.IsEnabled = false;
        return control;
    }
    private void ApplyBusyState()
    {
        foreach (var item in _interactive.Concat(_selectionInteractive)) item.Control.IsEnabled = !_dispatching && !_lastSnapshot.Flag("Busy") && item.Enabled;
    }
    private void UpdateFeedback()
    {
        _pending.Text = _dispatching || _lastSnapshot.Flag("Busy") ? T("正在处理…", "Applying…") : "";
        _error.Text = _interaction.DisplayError(_lastSnapshot);
        if (_skinStatus is not null) _skinStatus.Text = _interaction.SkinStatus(_lastSnapshot, _english);
    }

    private JsonElement ProjectControls(JsonElement envelope)
    {
        var controls = envelope.Field("controls");
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (controls.ValueKind == JsonValueKind.Object)
            foreach (var item in controls.EnumerateObject()) values[item.Name] = item.Value.Clone();
        var state = envelope.Field("state");
        var auto = envelope.Field("automation");
        var settings = envelope.Field("settings").Field("autoGameflow");
        foreach (var key in new[] { "autoAcceptEnabled", "autoAcceptDelaySeconds", "autoMatchmakingEnabled", "autoMatchmakingDelaySeconds", "autoMatchmakingMinimumMembers", "autoMatchmakingWaitForInvitees" })
        {
            var setting = settings.Field(key);
            if (setting.ValueKind != JsonValueKind.Undefined) values[char.ToUpperInvariant(key[0]) + key[1..]] = setting.Clone();
        }
        var flow = state.Field("gameflow");
        var flowSession = flow.Field("session");
        values["IsCustomGame"] = flowSession.Field("gameData").Flag("isCustomGame");
        var matchmaking = state.Field("matchmaking");
        values["PlayerResponse"] = matchmaking.Field("readyCheck").String("playerResponse");
        var gameflowAuto = auto.Field("auto-gameflow-main");
        if (gameflowAuto.Flag("willSearchMatch"))
        {
            var seconds = Math.Max(0, (gameflowAuto.Number("willSearchMatchAt") - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000);
            values["LoungeStatus"] = T("自动匹配", "Automatic matchmaking") + $" · {seconds:0.0} " + T("秒", "s");
        }
        else if (settings.Flag("autoMatchmakingEnabled"))
        {
            var penalty = matchmaking.Field("search").Field("errors").Array().Select(error => error.Number("penaltyTimeRemaining")).DefaultIfEmpty(0).Max();
            values["LoungeStatus"] = penalty > 0 ? T("等待处罚结束", "Waiting for penalty") + $" · {penalty:0}s" :
                gameflowAuto.String("activityStartStatus") switch
                {
                    "insufficient-members" => T("等待队伍人数", "Waiting for members") + " · " + settings.Int("autoMatchmakingMinimumMembers", 1),
                    "waiting-for-invitees" => T("等待受邀玩家", "Waiting for invitees"),
                    _ => ""
                };
        }
        if (flow.String("phase") == "Matchmaking")
        {
            var search = matchmaking.Field("search");
            var lowPriority = search.Field("lowPriorityData");
            var queue = $"{search.Number("timeInQueue"):0.0}s / {search.Number("estimatedQueueTime"):0.0}s";
            if (settings.String("autoMatchmakingRematchStrategy") == "fixed-duration")
                queue = $"{search.Number("timeInQueue"):0.0}s ({T("最多", "at most")} {settings.Number("autoMatchmakingRematchFixedDuration"):0}s) / {search.Number("estimatedQueueTime"):0.0}s";
            if (lowPriority.Number("penaltyTime") > 0) queue = T("等待", "Wait") + $" {lowPriority.Number("penaltyTimeRemaining"):0.0}s ({lowPriority.Number("penaltyTime"):0.0}s)";
            values["QueueText"] = queue;
        }
        var session = state.Field("champSelect").Field("session");
        var ownCell = session.Int("localPlayerCellId", -1);
        var championData = state.Field("gameData").Field("champions");
        var actions = new List<object>();
        foreach (var group in session.Field("actions").Array())
            foreach (var action in group.Array())
                if (action.Int("actorCellId", -2) == ownCell)
                {
                    var champion = championData.Field(action.Int("championId").ToString());
                    actions.Add(new { Type = action.String("type"), Completed = action.Flag("completed"), IsInProgress = action.Flag("isInProgress"), ChampionID = action.Int("championId"), IconPath = champion.String("squarePortraitPath"), ChampionName = champion.String("name") });
                    break;
                }
        values["Actions"] = actions;
        return JsonSerializer.SerializeToElement(values);
    }

    private async Task SetPinnedAsync(bool pinned)
    {
        if (_applyingPin) return;
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = pinned;
        try { await _saveSetting("pinned", JsonSerializer.SerializeToElement(pinned)); await _action("pin", 0, pinned); }
        catch (Exception error) { _error.Text = error.Message; }
    }

    private Flyout OptionsFlyout()
    {
        var content = new StackPanel { Spacing = 8, Width = 220 };
        var opacity = new Slider { Header = T("不透明度", "Opacity"), Minimum = .1, Maximum = 1, Value = _opacity, StepFrequency = .05 };
        opacity.ValueChanged += async (_, _) => { _opacity = opacity.Value; SetOpacity(_opacity); try { await _saveSetting("opacity", JsonSerializer.SerializeToElement(_opacity)); } catch (Exception error) { _error.Text = error.Message; } };
        content.Children.Add(opacity);
        foreach (var pair in new[] { (T("左上", "Top left"), "top-left"), (T("右上", "Top right"), "top-right"), (T("左下", "Bottom left"), "bottom-left"), (T("右下", "Bottom right"), "bottom-right"), (T("居中", "Center"), "center") })
        {
            var button = new Button { Content = pair.Item1, HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += (_, _) => Snap(pair.Item2);
            content.Children.Add(button);
        }
        return new Flyout { Content = content };
    }

    private void Snap(string position)
    {
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var size = AppWindow.Size;
        var x = position.Contains("right") ? work.X + work.Width - size.Width : work.X;
        var y = position.Contains("bottom") ? work.Y + work.Height - size.Height : work.Y;
        if (position == "center") { x = work.X + (work.Width - size.Width) / 2; y = work.Y + (work.Height - size.Height) / 2; }
        AppWindow.Move(new PointInt32(x, y));
    }

    private void RestoreBounds(JsonElement bounds)
    {
        if (bounds.ValueKind != JsonValueKind.Object) return;
        var work = DisplayArea.GetFromRect(new RectInt32(bounds.Int("x"), bounds.Int("y"), bounds.Int("width", 340), bounds.Int("height", 620)), DisplayAreaFallback.Nearest).WorkArea;
        var width = Math.Clamp(bounds.Int("width", 340), 340, Math.Max(340, work.Width));
        var height = Math.Clamp(bounds.Int("height", 620), 420, Math.Max(420, work.Height));
        var x = Math.Clamp(bounds.Int("x", work.X), work.X, work.X + Math.Max(0, work.Width - width));
        var y = Math.Clamp(bounds.Int("y", work.Y), work.Y, work.Y + Math.Max(0, work.Height - height));
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private readonly SemaphoreSlim _persistGate = new(1, 1);
    public Task PersistAsync() => PersistBoundsAsync();
    private async Task PersistBoundsAsync()
    {
        await _persistGate.WaitAsync();
        try
        {
            if (_shutdown || AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored }) return;
            await _saveSetting("trackedBounds", JsonSerializer.SerializeToElement(new { x = AppWindow.Position.X, y = AppWindow.Position.Y, width = AppWindow.Size.Width, height = AppWindow.Size.Height }));
        }
        catch (Exception error) { _error.Text = error.Message; }
        finally { _persistGate.Release(); }
    }

    private void SetOpacity(double opacity)
    {
        var handle = WindowNative.GetWindowHandle(this);
        var extended = GetWindowLongPtr(handle, -20).ToInt64();
        SetWindowLongPtr(handle, -20, new IntPtr(extended | 0x80000));
        SetLayeredWindowAttributes(handle, 0, (byte)Math.Round(opacity * 255), 2);
    }

    private Image Picture(string path, double width, double height)
    {
        var image = new Image { Width = width, Height = height, Stretch = Stretch.UniformToFill, Tag = path };
        image.Loaded += async (_, _) => { var bitmap = await LoadBitmapAsync(path); if ((string?)image.Tag == path) image.Source = bitmap; };
        return image;
    }

    private Task<BitmapImage?> LoadBitmapAsync(string path)
    {
        if (path == "") return Task.FromResult<BitmapImage?>(null);
        if (_images.TryGetValue(path, out var hit)) { _images[path] = (hit.Image, ++_imageAccess); return Task.FromResult<BitmapImage?>(hit.Image); }
        if (_pendingImages.TryGetValue(path, out var pending)) return pending;
        var task = FetchBitmapAsync(path);
        _pendingImages[path] = task;
        return task;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root is null) return null;
        if (root is ScrollViewer scroll) return scroll;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, index)) is { } child) return child;
        return null;
    }

    private async Task<BitmapImage?> FetchBitmapAsync(string path)
    {
        await Task.Yield(); // Register the pending task before a cache hit or empty response can finish.
        try
        {
            var bytes = await _loadImage(path);
            if (bytes is null || bytes.Length == 0) return null;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream)) { writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); }
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = 160 };
            await bitmap.SetSourceAsync(stream);
            while (_images.Count >= 128) _images.Remove(_images.MinBy(item => item.Value.Access).Key);
            _images[path] = (bitmap, ++_imageAccess);
            return bitmap;
        }
        catch { return null; }
        finally { _pendingImages.Remove(path); }
    }

    private string T(string chinese, string english) => _english ? english : chinese;
    private string PhaseLabel(string phase) => phase switch { "ChampSelect" => T("英雄选择", "Champion select"), "Lobby" => T("组队大厅", "Lobby"), "Matchmaking" => T("正在匹配", "Matchmaking"), "ReadyCheck" => T("已找到对局", "Match found"), "InProgress" => T("对局进行中", "In game"), _ => T("等待进入选人", "Waiting for champion select") };
    private static string EnglishBalance(string kind, string fallback) => kind switch { "damage-dealt" => "Damage dealt", "damage-taken" => "Damage taken", "healing" => "Healing", "shielding" => "Shielding", "ability-haste" => "Ability haste", "attack-speed" => "Attack speed", "attack-speed-growth" => "Attack speed growth", "tenacity" => "Tenacity", "movement-speed" => "Movement speed", "mana-regen" => "Mana regen", "energy-regen" => "Energy regen", "resource-regen" => "Resource regen", "area-of-effect-damage" => "Area damage", _ => fallback };
    private static TextBlock Text(string text, double size = 12, bool bold = false) => new() { Text = text, FontSize = size, FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private global::Windows.UI.Color BalanceColor(bool buffed) => _body.ActualTheme == ElementTheme.Dark ? buffed ? global::Windows.UI.Color.FromArgb(255, 109, 214, 172) : global::Windows.UI.Color.FromArgb(255, 255, 144, 153) : buffed ? global::Windows.UI.Color.FromArgb(255, 22, 118, 80) : global::Windows.UI.Color.FromArgb(255, 191, 61, 70);
    private TextBlock ColoredText(string text, bool buffed, double size, bool bold = false) { var block = Text(text, size, bold); block.Foreground = new SolidColorBrush(BalanceColor(buffed)); return block; }
    private static Brush Accent() => new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 40, 119, 228));
    private static Border Card(UIElement content) => new() { Child = content, Padding = new Thickness(8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(70, 128, 128, 128)) };
    private sealed record SkinRow(int ID, string Name, string ImagePath, bool Selected, bool Enabled);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr handle, uint color, byte alpha, uint flags);
}

internal static class MiniJson
{
    public static JsonElement Field(this JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }
    public static string String(this JsonElement value, string name) { var field = value.Field(name); return field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : ""; }
    public static bool Flag(this JsonElement value, string name) => value.Field(name).ValueKind == JsonValueKind.True;
    public static double Number(this JsonElement value, string name, double fallback = 0) { var field = value.Field(name); return field.ValueKind == JsonValueKind.Number && field.TryGetDouble(out var result) ? result : fallback; }
    public static int Int(this JsonElement value, string name, int fallback = 0) => (int)value.Number(name, fallback);
    public static JsonElement[] Array(this JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
    public static IEnumerable<JsonProperty> Properties(this JsonElement value) => value.ValueKind == JsonValueKind.Object ? value.EnumerateObject() : Enumerable.Empty<JsonProperty>();
    public static JsonElement CloneOrDefault(this JsonElement value) => value.ValueKind == JsonValueKind.Undefined ? default : value.Clone();
}

using System.Globalization;
using System.Text.Json;
using System.Runtime.InteropServices;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace LeagueAkari.WinUI.Windows;

/// <summary>Client-side stopwatch/countdown timestamps mirror the original utility window.</summary>
public sealed class CDTimerWindow : UtilityWindow
{
    private readonly StackPanel _rows = new() { Spacing = 4, Margin = new Thickness(8) };
    private readonly TextBlock _status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _adjustment = new() { FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly NativeImages _images;
    private readonly Dictionary<string, TimerState> _timers = [];
    private readonly Dictionary<string, TextBlock> _labels = [];
    private readonly Dictionary<string, Border> _masks = [];
    private readonly Dictionary<string, long> _rightClicks = [];
    private JsonElement _gameData, _platform, _settings;
    private string _signature = "", _timerType = "countdown", _adjustedId = "";
    private bool _refreshing, _reverse, _syncType;
    private long _adjustmentAt, _adjustmentTotal;
    private readonly ComboBox _type = new() { FontSize = 11 };
    private int _visibilityRevision, _settingRevision;
    private bool _sending;

    public CDTimerWindow(BackendClient backend, JsonElement settings = default)
        : base(backend, "cd-timer-window", "Timer", 190, 310, settings)
    {
        _images = new NativeImages(backend);
        _settings = settings;
        _timerType = settings.String("timerType") == "countup" ? "countup" : "countdown";
        _reverse = settings.Flag("reverseAdjustmentDirection");
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.IsAlwaysOnTop = true; presenter.IsResizable = false; presenter.IsMinimizable = false; presenter.SetBorderAndTitleBar(false, false); }
        var handle = WindowNative.GetWindowHandle(this);
        SetWindowLongPtr(handle, -20, new IntPtr((GetWindowLongPtr(handle, -20).ToInt64() | 0x80) & ~0x40000));
        var content = new StackPanel { Spacing = 5 };
        var titlebar = new Grid { Height = 20, Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(180, 252, 130, 234)) };
        titlebar.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); titlebar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var title = new TextBlock { Text = "Timer", Margin = new Thickness(8, 0, 0, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button { Content = "×", Height = 20, MinHeight = 20, Padding = new Thickness(6, 0, 6, 0) }; close.Click += (_, _) => SetVisible(false);
        var nonClient = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        XamlRoot? captionRoot = null;
        void UpdateCaptionRegion()
        {
            if (!titlebar.IsLoaded || titlebar.ActualWidth <= 0 || close.ActualWidth <= 0 || Content is not FrameworkElement root) return;
            double scale = root.XamlRoot.RasterizationScale;
            var origin = titlebar.TransformToVisual(root).TransformPoint(default);
            var closeOrigin = close.TransformToVisual(root).TransformPoint(default);
            int left = (int)Math.Round(origin.X * scale), top = (int)Math.Round(origin.Y * scale);
            int right = (int)Math.Round(closeOrigin.X * scale), bottom = (int)Math.Round((origin.Y + titlebar.ActualHeight) * scale);
            // The OS handles caption hit-testing and dragging; the close button remains a normal client region.
            nonClient.SetRegionRects(NonClientRegionKind.Caption, [new global::Windows.Graphics.RectInt32(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top))]);
        }
        void CaptionRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateCaptionRegion();
        titlebar.Loaded += (_, _) =>
        {
            if (captionRoot != titlebar.XamlRoot)
            {
                if (captionRoot is not null) captionRoot.Changed -= CaptionRootChanged;
                captionRoot = titlebar.XamlRoot; captionRoot.Changed += CaptionRootChanged;
            }
            UpdateCaptionRegion();
        };
        titlebar.SizeChanged += (_, _) => UpdateCaptionRegion();
        close.SizeChanged += (_, _) => UpdateCaptionRegion();
        AppWindow.Changed += (_, args) => { if (args.DidSizeChange) UpdateCaptionRegion(); };
        Closed += (_, _) => { if (captionRoot is not null) captionRoot.Changed -= CaptionRootChanged; };
        titlebar.Children.Add(title); Grid.SetColumn(close, 1); titlebar.Children.Add(close);
        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 0, 8, 0) };
        var type = _type;
        type.ItemsSource = new[] { Localization.Text("倒计时", "Countdown"), Localization.Text("秒表", "Stopwatch") };
        type.SelectedIndex = _timerType == "countdown" ? 0 : 1;
        type.SelectionChanged += async (_, _) => { if (_syncType || type.SelectedIndex < 0) return; try { await Backend.CallAsync("setting-factory-main", "set", SettingsNamespace, "timerType", type.SelectedIndex == 0 ? "countdown" : "countup"); } catch (Exception error) { _status.Text = error.Message; } };
        options.Children.Add(type);
        content.Children.Add(options);
        content.Children.Add(_rows);
        content.Children.Add(_adjustment);
        content.Children.Add(_status);
        var frame = new Grid();
        frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
        frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        frame.Children.Add(titlebar);
        var scroll = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 5, 0, 0) };
        Grid.SetRow(scroll, 1); frame.Children.Add(scroll); Content = frame;
        _clock.Tick += (_, _) => { UpdateTimes(); UpdateMousePassthrough(); };
        _refresh.Tick += async (_, _) => await RefreshAsync();
        RenderRows(CDTimerRoster.Defaults());
        Localization.Changed += OnLocaleChanged;
        Backend.EventReceived += BackendChanged;
        Closed += (_, _) => { Localization.Changed -= OnLocaleChanged; Backend.EventReceived -= BackendChanged; _clock.Stop(); _refresh.Stop(); _visibilityRevision++; };
    }

    private void OnLocaleChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var selected = _type.SelectedIndex;
            try { _syncType = true; _type.ItemsSource = new[] { Localization.Text("倒计时", "Countdown"), Localization.Text("秒表", "Stopwatch") }; _type.SelectedIndex = selected; } finally { _syncType = false; }
            _signature = "";
            if (IsVisible) _ = RefreshAsync();
        });
    }

    protected override void OnVisibilityChanged(bool visible)
    {
        _visibilityRevision++;
        if (visible) { SetPinned(true); UpdateMousePassthrough(); _clock.Start(); _refresh.Start(); _ = RefreshAsync(); }
        else { _clock.Stop(); _refresh.Stop(); SetClickThrough(true); _rightClicks.Clear(); }
    }
    public override void SetPinned(bool pinned) => base.SetPinned(true);

    private void UpdateMousePassthrough()
    {
        if (!IsVisible || !GetCursorPos(out var cursor) || !GetWindowRect(WindowNative.GetWindowHandle(this), out var bounds)) return;
        SetClickThrough(cursor.X < bounds.Left || cursor.X >= bounds.Right || cursor.Y < bounds.Top || cursor.Y >= bounds.Bottom);
    }
    private void ApplyTimerSetting(string key, JsonElement value)
    {
        if (key is "timerType" or "reverseAdjustmentDirection") _settingRevision++;
        if (key == "timerType")
        {
            string type = value.ValueKind == JsonValueKind.String && value.GetString() == "countup" ? "countup" : "countdown";
            if (type != _timerType) { _timerType = type; _timers.Clear(); _rightClicks.Clear(); _signature = ""; }
            try { _syncType = true; _type.SelectedIndex = type == "countdown" ? 0 : 1; } finally { _syncType = false; }
        }
        else if (key == "reverseAdjustmentDirection") _reverse = value.ValueKind == JsonValueKind.True;
    }
    private void BackendChanged(JsonElement envelope)
    {
        string name = envelope.Text("name");
        if (name == "update-state-prop/" + SettingsNamespace + ":settings")
        {
            var args = envelope.Field("args").Items().ToArray(); if (args.Length < 2 || args[0].ValueKind != JsonValueKind.String) return;
            DispatcherQueue.TryEnqueue(() => { ApplyTimerSetting(args[0].GetString()!, args[1]); if (IsVisible) _ = RefreshAsync(); });
        }
        else if (name.Contains("league-client-main:gameflow") || name.Contains("league-client-main:summoner") || name.Contains("ongoing-game-main"))
            DispatcherQueue.TryEnqueue(() => { if (IsVisible) _ = RefreshAsync(); });
    }

    public async Task RefreshAsync()
    {
        if (!IsVisible || _refreshing) return;
        _refreshing = true;
        int revision = _visibilityRevision;
        int settingsRevision = _settingRevision;
        try
        {
            var requests = await Task.WhenAll(Backend.StateAsync("league-client-main", "gameflow"), Backend.StateAsync("league-client-main", "summoner"), Backend.StateAsync("league-client-main", "gameData"), Backend.StateAsync("ongoing-game-main"), Backend.StateAsync(SettingsNamespace), Backend.CallAsync("setting-factory-main", "getByPrefix", SettingsNamespace, ""));
            if (!IsVisible || revision != _visibilityRevision) return;
            _gameData = requests[2]; _platform = requests[4]; _settings = requests[5];
            if (settingsRevision == _settingRevision)
            {
                ApplyTimerSetting("timerType", _settings.Field("timerType"));
                ApplyTimerSetting("reverseAdjustmentDirection", _settings.Field("reverseAdjustmentDirection"));
            }
            _mode = requests[0].Field("session").Field("gameData").Field("queue").String("gameMode");
            var rows = CDTimerRoster.Read(requests[0], requests[1], requests[3], _platform, _timerType);
            var signature = JsonSerializer.Serialize(rows) + string.Join("|", rows.SelectMany(row => new[] { row.FirstSpell, row.SecondSpell }).Distinct().Select(id => _gameData.Field("summonerSpells").Field(id.ToString()).ToString()));
            if (signature != _signature) { _signature = signature; RenderRows(rows); }
            _status.Text = "";
            UpdateTimes();
        }
        catch (Exception error) { if (IsVisible && revision == _visibilityRevision) _status.Text = error.Message; }
        finally { _refreshing = false; }
    }

    private void RenderRows(CDTimerRow[] rows)
    {
        _rows.Children.Clear(); _labels.Clear(); _masks.Clear();
        foreach (var row in rows)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            if (row.Hero > 0)
            {
                var hero = new Image { Width = 32, Height = 32 };
                _ = _images.SetAsync(hero, $"/lol-game-data/assets/v1/champion-icons/{row.Hero}.png");
                line.Children.Add(hero);
            }
            else line.Children.Add(new TextBlock { Text = "◷", FontSize = 26, Width = 32 });
            line.Children.Add(new TextBlock { Text = "›", FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(SpellButton(row.FirstKey, row.Hero, row.FirstSpell, row.Type));
            line.Children.Add(SpellButton(row.SecondKey, row.Hero, row.SecondSpell, row.Type));
            _rows.Children.Add(line);
        }
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(190, Math.Max(150, 100 + rows.Length * 36)));
        var keep = rows.SelectMany(row => new[] { row.FirstKey, row.SecondKey }).ToHashSet();
        foreach (var key in _timers.Keys.Where(key => !keep.Contains(key)).ToArray()) _timers.Remove(key);
        foreach (var key in _rightClicks.Keys.Where(key => !keep.Contains(key)).ToArray()) _rightClicks.Remove(key);
    }

    private Button SpellButton(string key, int hero, int spellId, string type)
    {
        var grid = new Grid { Width = 32, Height = 32 };
        var image = new Image { Width = 32, Height = 32, Stretch = Stretch.UniformToFill };
        var spell = _gameData.Field("summonerSpells").Field(spellId.ToString());
        if (hero > 0) _ = _images.SetAsync(image, spell.String("iconPath"));
        else grid.Children.Add(new TextBlock { Text = "◷", FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center });
        grid.Children.Add(image);
        var label = new TextBlock { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var mask = new Border { Child = label, Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(170, 0, 0, 0)), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        grid.Children.Add(mask); _labels[key] = label; _masks[key] = mask;
        var button = new Button { Content = grid, Padding = new Thickness(0), MinWidth = 32, MinHeight = 32, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        ToolTipService.SetToolTip(button, hero > 0 ? spell.String("name") + Localization.Text(" · 点击开始/清除，滚轮校正，双右键发送", " · click to start/reset, scroll to correct, double right-click to send") : Localization.Text("秒表 · 点击开始/清除，滚轮校正", "Stopwatch · click to start/reset, scroll to correct"));
        button.Click += (_, _) =>
        {
            if (!_timers.Remove(key))
            {
                var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (type == "countdown")
                {
                    var currentSpell = _gameData.Field("summonerSpells").Field(spellId.ToString());
                    var target = CDTimerData.CountdownTarget(stamp, currentSpell.Field("cooldown").ValueKind == JsonValueKind.Number ? currentSpell.Number("cooldown") : null, CDTimerData.AbilityHaste(_platform, _mode));
                    if (!target.HasValue) return;
                    stamp = target.Value;
                }
                _timers[key] = new TimerState(type, stamp);
            }
            UpdateTimes();
        };
        button.PointerWheelChanged += (_, args) =>
        {
            if (!_timers.TryGetValue(key, out var timer)) return;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var next = CDTimerData.Adjust(type, timer.Timestamp, args.GetCurrentPoint(button).Properties.MouseWheelDelta, _reverse, now);
            _timers[key] = timer with { Timestamp = next };
            if (_adjustedId != key || now - _adjustmentAt > 500) _adjustmentTotal = 0;
            _adjustmentTotal += CDTimerData.AdjustmentIndicatorDelta(type, timer.Timestamp, next, args.GetCurrentPoint(button).Properties.MouseWheelDelta, _reverse); _adjustedId = key; _adjustmentAt = now;
            var display = type == "countup" ? -_adjustmentTotal : _adjustmentTotal;
            _adjustment.Text = display == 0 ? "= 0 s" : $"{(display > 0 ? "+" : "−")} {Math.Abs(display / 1000d):0} s";
            args.Handled = true; UpdateTimes();
        };
        button.PointerPressed += async (_, args) =>
        {
            if (!args.GetCurrentPoint(button).Properties.IsRightButtonPressed) return;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_rightClicks.TryGetValue(key, out var prior) && now - prior < 400) { _rightClicks.Remove(key); await SendTimerAsync(key, hero, spellId); }
            else _rightClicks[key] = now;
            args.Handled = true;
        };
        return button;
    }

    private string _mode = "";
    private void UpdateTimes()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var (key, label) in _labels)
        {
            if (!_timers.TryGetValue(key, out var timer)) { label.Text = ""; if (_masks.TryGetValue(key, out var emptyMask)) emptyMask.Visibility = Visibility.Collapsed; continue; }
            if (_masks.TryGetValue(key, out var mask)) mask.Visibility = Visibility.Visible;
            label.Text = CDTimerData.Display(timer.Type, timer.Timestamp, now);
        }
        if (now - _adjustmentAt >= 500) _adjustment.Text = "";
    }

    private async Task SendTimerAsync(string key, int hero, int spellId)
    {
        if (!_timers.TryGetValue(key, out var timer) || hero <= 0 || _platform.Field("gameTime").ValueKind != JsonValueKind.Number || !CDTimerData.AbilityHaste(_platform, _mode).HasValue) return;
        if (_sending) { _status.Text = Localization.Key("cdTimer.window.alreadySending", "Sending in progress"); return; }
        var relative = timer.Timestamp - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _platform.Number("gameTime") * 1000;
        var minutes = (int)Math.Floor(relative / 60000); var seconds = (int)Math.Floor(relative % 60000 / 1000);
        var name = _gameData.Field("champions").Field(hero.ToString()).Text("name", hero.ToString(CultureInfo.InvariantCulture));
        if (_gameData.Field("summonerSpells").Field(spellId.ToString()).ValueKind != JsonValueKind.Object) return;
        var spell = _gameData.Field("summonerSpells").Field(spellId.ToString()).String("name");
        var text = Localization.Key("cdTimer.window." + timer.Type, null, new Dictionary<string, object?> { ["championName"] = name, ["spellName"] = spell, ["minutes"] = minutes, ["seconds"] = seconds.ToString("00") });
        _sending = true;
        try
        {
            var support = (await Backend.StateAsync("app-common-main")).Field("nativeSupport").Field("nativeInput");
            if (!support.Boolean("available")) { _status.Text = Localization.Key(support.Boolean("availableOnCurrentPlatform") ? "cdTimer.window.adminRequired" : "cdTimer.window.nativeInjectionUnsupported", "Native input is unavailable"); return; }
            await Backend.CallAsync(SettingsNamespace, "sendInGame", text); _status.Text = "";
        }
        catch (Exception error) { _status.Text = Localization.Key(CDTimerData.SendFailureKey(error.Message), error.Message, new Dictionary<string, object?> { ["reason"] = error.Message }); }
        finally { _sending = false; }
    }

    private sealed record TimerState(string Type, long Timestamp);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out Rect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
}

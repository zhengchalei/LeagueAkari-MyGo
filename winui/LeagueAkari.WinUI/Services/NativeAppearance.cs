using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace LeagueAkari.WinUI.Services;

/// <summary>Applies the desktop appearance settings to native windows without creating or showing them.</summary>
public sealed class NativeAppearance : IDisposable
{
    private readonly BackendClient _backend;
    private readonly List<WindowBinding> _windows = [];
    private readonly global::Windows.UI.ViewManagement.UISettings _system = new();
    private readonly SemaphoreSlim _reload = new(1, 1);
    private JsonElement _windowSettings;
    private bool _disposed;
    public static NativeAppearance? Current { get; private set; }
    public string ThemeId { get; private set; } = "default";
    public bool IsDark => Palette().Dark;
    public SolidColorBrush CardBrush() => Brush(Palette().Card);
    public bool StreamerMode { get; private set; }
    public bool UseAkariStyledName { get; private set; }
    public bool ContentProtection => _windowSettings.Boolean("contentProtection");
    public string Mask(string sensitiveText) => StreamerMode ? "●●●●●●" : sensitiveText;
    public string SummonerPlaceholder(string puuid, int index)
    {
        if (!UseAkariStyledName) return Localization.Key("common.summonerPlaceholder", null, new Dictionary<string, object?> { ["index"] = index + 1 });
        var names = Localization.IsEnglish ? EnglishNames : ChineseNames;
        var hash = 0;
        foreach (var character in puuid)
        {
            if (character == '-') continue;
            var value = character is >= '0' and <= '9' ? character - '0' : character is >= 'a' and <= 'f' ? character - 'a' + 10 : character is >= 'A' and <= 'F' ? character - 'A' + 10 : 0;
            hash = (hash * 16 + value) % names.Length;
        }
        return names[hash];
    }
    public event Action? Changed;

    public NativeAppearance(BackendClient backend)
    {
        _backend = backend;
        Current = this;
        backend.EventReceived += OnBackendEvent;
        _system.ColorValuesChanged += OnSystemColor;
    }

    public async Task StartAsync() => await ReloadAsync();
    public IDisposable Watch(Window window, FrameworkElement content, string settingsNamespace = "window-manager-main/main-window")
    {
        var binding = new WindowBinding(window, content, settingsNamespace);
        _windows.Add(binding);
        ApplyWindow(window, content, settingsNamespace);
        return new ActionDisposable(() => _windows.Remove(binding));
    }

    public void ApplyWindow(Window window, FrameworkElement content, string settingsNamespace = "window-manager-main/main-window")
    {
        var palette = Palette();
        ApplyResources(content, palette);
        // Install local resources before changing RequestedTheme; never mutate a resolved theme brush.
        content.RequestedTheme = palette.Dark ? ElementTheme.Dark : ElementTheme.Light;
        var body = Brush(palette.Background);
        var mica = settingsNamespace == "window-manager-main/main-window" && _windowSettings.Text("backgroundMaterial") == "mica" && Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported();
        window.SystemBackdrop = mica ? new MicaBackdrop() : null;
        if (content is Panel panel) panel.Background = mica ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : body;
        else if (content is Control control) control.Background = mica ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : body;
        SetWindowDisplayAffinity(WindowNative.GetWindowHandle(window), ContentProtection ? 0x11u : 0u);
    }

    private static void ApplyResources(FrameworkElement content, (bool Dark, string Background, string Card, string Accent) palette)
    {
        void SetBrush(string name, string color)
        {
            // WinUI may seal/shared-cache brushes when a control template resolves them.
            // Resource replacement is legal; writing Color on the resolved brush can throw E_ACCESSDENIED.
            content.Resources[name] = Brush(color);
        }
        SetBrush("ApplicationPageBackgroundThemeBrush", palette.Background);
        SetBrush("LayerFillColorDefaultBrush", palette.Card);
        SetBrush("CardBackgroundFillColorDefaultBrush", palette.Card);
        SetBrush("CardBackgroundFillColorSecondaryBrush", palette.Card);
        SetBrush("AccentFillColorDefaultBrush", palette.Accent);
        // Expander's visible header and ContentDialog's popup use aliases, not their public Background.
        SetBrush("ExpanderHeaderBackground", palette.Card);
        SetBrush("ExpanderContentBackground", palette.Card);
        SetBrush("ContentDialogBackground", palette.Card);
        SetBrush("ContentDialogTopOverlay", palette.Card);
        SetBrush("ContentDialogForeground", palette.Dark ? "#f4f4f5" : "#27272a");
        var accent = Brush(palette.Accent);
        content.Resources["SystemAccentColor"] = accent.Color;
        SetBrush("TextFillColorPrimaryBrush", palette.Dark ? "#f4f4f5" : "#27272a");
    }

    public void ApplyDialog(ContentDialog dialog)
    {
        var palette = Palette();
        ApplyResources(dialog, palette);
        dialog.RequestedTheme = palette.Dark ? ElementTheme.Dark : ElementTheme.Light;
        dialog.Background = Brush(palette.Card);
        dialog.Foreground = Brush(palette.Dark ? "#f4f4f5" : "#27272a");
    }

    public static void BindCardSurface(Border border)
    {
        NativeAppearance? subscribed = null;
        void Apply()
        {
            border.Background = Current?.CardBrush() ?? Brush(border.ActualTheme == ElementTheme.Dark ? "#202024" : "#fafafa");
        }
        border.Loaded += (_, _) => { if (subscribed != null) subscribed.Changed -= Apply; subscribed = Current; if (subscribed != null) subscribed.Changed += Apply; Apply(); };
        border.Unloaded += (_, _) => { if (subscribed != null) subscribed.Changed -= Apply; subscribed = null; };
        border.ActualThemeChanged += (_, _) => Apply();
        Apply();
    }

    private async Task ReloadAsync()
    {
        if (_disposed) return;
        await _reload.WaitAsync();
        try
        {
            if (_disposed) return;
            var values = await Task.WhenAll(_backend.CallAsync("setting-factory-main", "getByPrefix", "app-common-main", ""), _backend.CallAsync("setting-factory-main", "getByPrefix", "window-manager-main", ""));
            if (_disposed) return;
            Dispatch(() =>
            {
                ThemeId = values[0].Text("theme", "default");
                StreamerMode = values[0].Boolean("streamerMode");
                UseAkariStyledName = values[0].Boolean("streamerModeUseAkariStyledName");
                _windowSettings = values[1];
                Localization.SetLocale(values[0].Text("locale", "zh-CN"));
                ApplyAll();
            });
        }
        finally { _reload.Release(); }
    }

    private void OnBackendEvent(JsonElement update)
    {
        if (update.Text("namespace") != "mobx-utils-main") return;
        var name = update.Text("name");
        if (name == "update-state-prop/app-common-main:settings" || name == "update-state-prop/window-manager-main:settings")
        {
            var key = update.Field("args").Items().FirstOrDefault().ValueKind == JsonValueKind.String ? update.Field("args").Items().First().GetString() : "";
            if (key is "locale" or "theme" or "streamerMode" or "streamerModeUseAkariStyledName" or "backgroundMaterial" or "contentProtection" or "") _ = RefreshSafelyAsync();
        }
    }
    private async Task RefreshSafelyAsync() { try { await ReloadAsync(); } catch { /* The host reports backend connection failures centrally. */ } }
    private void OnSystemColor(global::Windows.UI.ViewManagement.UISettings sender, object args) { if (ThemeId == "default") Dispatch(ApplyAll); }
    private void ApplyAll()
    {
        foreach (var binding in _windows.ToArray()) ApplyWindow(binding.Window, binding.Content, binding.SettingsNamespace);
        Changed?.Invoke();
    }
    private void Dispatch(Action action)
    {
        var dispatcher = _windows.FirstOrDefault()?.Window.DispatcherQueue;
        if (dispatcher == null || dispatcher.HasThreadAccess) action(); else dispatcher.TryEnqueue(() => action());
    }

    private (bool Dark, string Background, string Card, string Accent) Palette() => ThemeId switch
    {
        "light" => (false, "#f3f3f4", "#fafafa", "#587c66"),
        "sakura" => (false, "#fff4f6", "#fffafb", "#d45a86"),
        "butter" => (false, "#fdf7ea", "#fdfbf7", "#c27f22"),
        "mint" => (false, "#f3f8f5", "#fbfdfc", "#248873"),
        "graphite" => (true, "#09121c", "#152436", "#69caff"),
        "cyber" => (true, "#000000", "#0c0c0c", "#dfff00"),
        "aurora" => (true, "#1e1b30", "#2d2a42", "#c1a8ff"),
        "default" when _system.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Background).R > 127 => (false, "#f3f3f4", "#fafafa", "#587c66"),
        _ => (true, "#141416", "#202024", "#90c6a6")
    };
    private static SolidColorBrush Brush(string hex) => new(global::Windows.UI.Color.FromArgb(255, Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16)));
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _backend.EventReceived -= OnBackendEvent; _system.ColorValuesChanged -= OnSystemColor;
        _windows.Clear(); if (Current == this) Current = null;
    }
    private sealed record WindowBinding(Window Window, FrameworkElement Content, string SettingsNamespace);
    private static readonly string[] ChineseNames = ["灯里", "京子", "结衣", "千夏", "绫乃", "千岁", "樱子", "向日葵", "理世", "茜", "友子", "千鹤", "抚子", "花子", "枫", "奈奈", "真理", "赤座", "岁纳", "船见", "吉川", "杉浦", "池田", "大室", "古谷", "松本", "西垣"];
    private static readonly string[] EnglishNames = ["Akari", "Kyoko", "Yui", "Chinatsu", "Ayano", "Chitose", "Sakurako", "Himawari", "Rise", "Akane", "Tomoko", "Chizuru", "Nadeshiko", "Hanako", "Kaede", "Nana", "Mari", "Akaza", "Toshino", "Funami", "Yoshikawa", "Sugiura", "Ikeda", "Omuro", "Furutani", "Matsumoto", "Nishigaki"];
    private sealed class ActionDisposable(Action action) : IDisposable { public void Dispose() => action(); }
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
}


using System.Runtime.InteropServices;
using System.Text.Json;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace LeagueAkari.WinUI.Windows;

/// <summary>Shared lifecycle for native utility windows; hidden windows retain their state.</summary>
public abstract class UtilityWindow : Window
{
    protected readonly BackendClient Backend;
    protected readonly string SettingsNamespace;
    private readonly DispatcherTimer _persist = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _shutdown;
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private IDisposable? _appearanceBinding;
    public bool IsVisible { get; private set; }

    protected UtilityWindow(BackendClient backend, string name, string title, int width, int height, JsonElement settings)
    {
        Backend = backend;
        SettingsNamespace = "window-manager-main/" + name;
        Title = title;
        AppWindow.Resize(new SizeInt32(width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = settings.Field("pinned").ValueKind != JsonValueKind.False;
            presenter.IsMaximizable = false;
        }
        var saved = settings.Field("trackedBounds");
        if (saved.ValueKind == JsonValueKind.Object)
        {
            var rectangle = new RectInt32(saved.Int("x"), saved.Int("y"), saved.Int("width", width), saved.Int("height", height));
            var area = DisplayArea.GetFromRect(rectangle, DisplayAreaFallback.Nearest).WorkArea;
            width = Math.Clamp(saved.Int("width", width), 160, Math.Max(160, area.Width));
            height = Math.Clamp(saved.Int("height", height), 200, Math.Max(200, area.Height));
            AppWindow.MoveAndResize(new RectInt32(Math.Clamp(saved.Int("x", area.X), area.X, area.X + Math.Max(0, area.Width - width)), Math.Clamp(saved.Int("y", area.Y), area.Y, area.Y + Math.Max(0, area.Height - height)), width, height));
        }
        SetOpacity(settings.Number("opacity", 1));
        AppWindow.Closing += (_, args) => { if (_shutdown) return; args.Cancel = true; SetVisible(false); };
        AppWindow.Changed += (_, args) => { if (!args.DidPositionChange && !args.DidSizeChange) return; _persist.Stop(); _persist.Start(); };
        _persist.Tick += async (_, _) => { _persist.Stop(); await SaveBoundsAsync(); };
    }

    public void Show() => SetVisible(true);
    public void SetVisible(bool visible)
    {
        if (_appearanceBinding == null && Content is FrameworkElement content) _appearanceBinding = NativeAppearance.Current?.Watch(this, content, SettingsNamespace);
        IsVisible = visible;
        if (visible) AppWindow.Show(false);
        else { AppWindow.Hide(); _ = SaveBoundsAsync(); }
        OnVisibilityChanged(visible);
    }
    protected abstract void OnVisibilityChanged(bool visible);
    public virtual void SetPinned(bool pinned) { if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = pinned; }
    public void SetClickThrough(bool clickThrough)
    {
        var handle = WindowNative.GetWindowHandle(this);
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        SetWindowLongPtr(handle, -20, new IntPtr(clickThrough ? style | 0x80020 : style & ~0x20));
    }
    public void SetOpacity(double opacity)
    {
        var handle = WindowNative.GetWindowHandle(this);
        SetWindowLongPtr(handle, -20, new IntPtr(GetWindowLongPtr(handle, -20).ToInt64() | 0x80000));
        SetLayeredWindowAttributes(handle, 0, (byte)Math.Round(Math.Clamp(opacity, .1, 1) * 255), 2);
    }
    public void Shutdown() { _shutdown = true; _persist.Stop(); _appearanceBinding?.Dispose(); OnVisibilityChanged(false); Close(); }
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
            case "setFakeShow": SetVisible(args.Length > 0 && args[0].ValueKind == JsonValueKind.True); return null;
            case "minimize": presenter?.Minimize(); return null;
            case "maximize": presenter?.Maximize(); return null;
            case "unmaximize": presenter?.Restore(); return null;
            case "getSize": return new[] { AppWindow.Size.Width, AppWindow.Size.Height };
            case "getPosition": return new[] { AppWindow.Position.X, AppWindow.Position.Y };
            case "setTitle": Title = args[0].GetString() ?? ""; return null;
            case "setSize": AppWindow.Resize(new SizeInt32((int)args[0].GetDouble(), (int)args[1].GetDouble())); return null;
            case "setPosition": AppWindow.Move(new PointInt32((int)args[0].GetDouble(), (int)args[1].GetDouble())); return null;
            case "resetPosition":
                var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
                AppWindow.Move(new PointInt32(area.X + (area.Width - AppWindow.Size.Width) / 2, area.Y + (area.Height - AppWindow.Size.Height) / 2)); return null;
            case "repositionWindowIfInvisible":
                var position = AppWindow.Position; var size = AppWindow.Size;
                var saved = new RectInt32(position.X, position.Y, size.Width, size.Height);
                var work = DisplayArea.GetFromRect(saved, DisplayAreaFallback.Nearest).WorkArea;
                var visibleWidth = Math.Max(0, Math.Min(saved.X + size.Width, work.X + work.Width) - Math.Max(saved.X, work.X));
                var visibleHeight = Math.Max(0, Math.Min(saved.Y + size.Height, work.Y + work.Height) - Math.Max(saved.Y, work.Y));
                if (size.Width <= work.Width && size.Height <= work.Height && (double)visibleWidth * visibleHeight >= .98 * size.Width * size.Height) return null;
                saved.Width = Math.Min(size.Width, work.Width); saved.Height = Math.Min(size.Height, work.Height);
                saved.X = Math.Clamp(saved.X, work.X, work.X + work.Width - saved.Width); saved.Y = Math.Clamp(saved.Y, work.Y, work.Y + work.Height - saved.Height);
                if (presenter?.State == OverlappedPresenterState.Minimized) presenter.Restore();
                AppWindow.MoveAndResize(saved); return null;
            case "setPinned": SetPinned(args[0].ValueKind == JsonValueKind.True); await Backend.CallAsync("setting-factory-main", "set", SettingsNamespace, "pinned", args[0]); return null;
            case "setOpacity": SetOpacity(args[0].GetDouble()); await Backend.CallAsync("setting-factory-main", "set", SettingsNamespace, "opacity", args[0]); return null;
            case "setIgnoreMouseEvents": SetClickThrough(args[0].ValueKind == JsonValueKind.True); return null;
            case "applySettings":
                var settings = await Backend.CallAsync("setting-factory-main", "getByPrefix", SettingsNamespace, "");
                SetOpacity(settings.Number("opacity", 1)); SetPinned(settings.Flag("pinned")); return null;
            case "toggleDevtools": return null; // Native controls have no browser developer tools.
            default: throw new NotSupportedException($"原生工具窗口不支持：{method}");
        }
    }
    public Task PersistAsync() => SaveBoundsAsync();
    private async Task SaveBoundsAsync()
    {
        await _persistGate.WaitAsync();
        try { if (_shutdown || AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored }) return; await Backend.CallAsync("setting-factory-main", "set", SettingsNamespace, "trackedBounds", new { x = AppWindow.Position.X, y = AppWindow.Position.Y, width = AppWindow.Size.Width, height = AppWindow.Size.Height }); }
        catch { /* Shutdown may close the backend before the last bounds write. */ }
        finally { _persistGate.Release(); }
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr handle, uint color, byte alpha, uint flags);
}

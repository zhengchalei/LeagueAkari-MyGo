using System.Text.Json;
using LeagueAkari.WinUI.Pages;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace LeagueAkari.WinUI.Windows;

public sealed class OngoingWindow : UtilityWindow
{
    private readonly OngoingPage _page;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    public OngoingWindow(BackendClient backend, JsonElement settings = default, Action<string, string>? openPlayer = null)
        : base(backend, "ongoing-game-window", Localization.Text("LeagueAkari · 对局战绩", "LeagueAkari · Ongoing game"), 1300, 840, settings)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.IsAlwaysOnTop = true; presenter.IsResizable = false; presenter.IsMinimizable = false; presenter.SetBorderAndTitleBar(false, false); }
        var handle = WindowNative.GetWindowHandle(this);
        SetWindowLongPtr(handle, -20, new IntPtr((GetWindowLongPtr(handle, -20).ToInt64() | 0x80 | 0x8000000) & ~0x40000));
        _page = new OngoingPage(backend, openPlayer, standalone: true, canPreview: () => IsVisible) { IsRefreshEnabled = false };
        Content = _page;
        _refresh.Tick += async (_, _) => { if (IsVisible) await _page.RefreshAsync(); };
        Localization.Changed += OnLocaleChanged;
        Closed += (_, _) => { Localization.Changed -= OnLocaleChanged; _refresh.Stop(); };
    }
    private void OnLocaleChanged() => DispatcherQueue.TryEnqueue(() => Title = Localization.Text("LeagueAkari · 对局战绩", "LeagueAkari · Ongoing game"));
    protected override void OnVisibilityChanged(bool visible)
    {
        _page.IsRefreshEnabled = visible;
        if (visible) { SetPinned(true); SetClickThrough(false); _refresh.Start(); _ = _page.RefreshAsync(); }
        else
        {
            _refresh.Stop(); SetClickThrough(true);
            if (_page.XamlRoot is { } root)
                foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root)) HideDialogs(popup.Child);
        }
    }
    private static void HideDialogs(DependencyObject? element)
    {
        if (element is null) return;
        if (element is ContentDialog dialog) { dialog.Hide(); return; }
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++) HideDialogs(VisualTreeHelper.GetChild(element, index));
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
}


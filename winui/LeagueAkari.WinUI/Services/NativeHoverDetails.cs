using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace LeagueAkari.WinUI.Services;

public static class NativeHoverDetails
{
    public static void Attach(Button trigger, Func<FrameworkElement> createContent)
    {
        var flyout = new Flyout(); trigger.Flyout = flyout;
        bool overTrigger = false, overContent = false; int revision = 0;
        void EnsureContent()
        {
            if (flyout.Content != null) return;
            var content = createContent();
            content.PointerEntered += (_, _) => { overContent = true; ++revision; };
            content.PointerExited += (_, _) => { overContent = false; CloseLater(); };
            flyout.Content = content;
        }
        async void CloseLater() { int current = ++revision; await Task.Delay(100); if (current == revision && !overTrigger && !overContent) flyout.Hide(); }
        flyout.Opening += (_, _) => EnsureContent();
        trigger.PointerEntered += async (_, _) =>
        {
            overTrigger = true; int current = ++revision; await Task.Delay(50);
            if (current != revision || !overTrigger || !trigger.IsLoaded || trigger.XamlRoot == null) return;
            EnsureContent(); flyout.ShowAt(trigger, new FlyoutShowOptions { ShowMode = FlyoutShowMode.Transient });
        };
        trigger.PointerExited += (_, _) => { overTrigger = false; CloseLater(); };
        trigger.Unloaded += (_, _) => { ++revision; overTrigger = overContent = false; flyout.Hide(); };
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace LeagueAkari.WinUI.Services;

public static class NativePlayerNavigation
{
    public static void AttachBackgroundOpen(Button button, Action? open)
    {
        if (open == null) return;
        bool middle = false;
        // Button handles routed pointer events before ordinary subscribers see them.
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) => { if (!button.IsEnabled) return; middle = args.GetCurrentPoint(button).Properties.IsMiddleButtonPressed; if (middle) args.Handled = true; }), true);
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, args) => { if (!middle) return; middle = false; args.Handled = true; if (button.IsEnabled) open(); }), true);
        button.PointerCanceled += (_, _) => middle = false;
        button.PointerCaptureLost += (_, _) => middle = false;
        button.Unloaded += (_, _) => middle = false;
    }
}

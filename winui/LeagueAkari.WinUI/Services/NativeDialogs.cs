using Microsoft.UI.Xaml.Controls;
namespace LeagueAkari.WinUI.Services;

public static class NativeDialogs
{
    private static readonly NativeDialogQueue Queue = new();
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog) => await TryShowAsync(dialog) ?? ContentDialogResult.None;
    public static Task<ContentDialogResult?> TryShowAsync(ContentDialog dialog, Func<bool>? canShow = null)
    {
        var root = dialog.XamlRoot ?? throw new InvalidOperationException("A native dialog requires the owning window's XamlRoot.");
        return Queue.RunAsync(root, async () =>
        {
            void ApplyTheme() { if (NativeAppearance.Current is { } appearance) appearance.ApplyDialog(dialog); else if (root.Content is Microsoft.UI.Xaml.FrameworkElement owner) dialog.RequestedTheme = owner.ActualTheme; }
            ApplyTheme();
            var appearance = NativeAppearance.Current;
            if (appearance != null) appearance.Changed += ApplyTheme;
            try { return await dialog.ShowAsync(); }
            finally { if (appearance != null) appearance.Changed -= ApplyTheme; }
        }, canShow);
    }
}

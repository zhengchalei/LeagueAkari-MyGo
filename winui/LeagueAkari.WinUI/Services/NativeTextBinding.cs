using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.CompilerServices;

namespace LeagueAkari.WinUI.Services;

public static class NativeTextBinding
{
    private sealed class Original { public string Text = ""; public string Last = ""; }
    private static readonly ConditionalWeakTable<DependencyObject, Original> Originals = new();
    public static void TranslateTree(DependencyObject root)
    {
        string? current = root switch { TextBlock text => text.Text, ContentControl control when control.Content is string value => value, AutoSuggestBox input => input.PlaceholderText, TextBox input => input.PlaceholderText, _ => null };
        if (current is not null)
        {
            var original = Originals.GetOrCreateValue(root);
            if (current != original.Last) original.Text = current;
            var translated = Localization.Translate(original.Text);
            switch (root)
            {
                case TextBlock text: text.Text = translated; break;
                case ContentControl control: control.Content = translated; break;
                case AutoSuggestBox input: input.PlaceholderText = translated; break;
                case TextBox input: input.PlaceholderText = translated; break;
            }
            original.Last = translated;
        }
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++) TranslateTree(VisualTreeHelper.GetChild(root, i));
    }
}

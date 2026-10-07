using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LeagueAkari.WinUI.Services;

public static class NativeNoticeMarkdown
{
    public static FrameworkElement Create(string markdown, Func<Uri, Task>? internalLink = null)
    {
        var panel = new StackPanel { Spacing = 8, MaxWidth = 650 };
        foreach (var block in NoticeMarkdownData.Parse(markdown))
        {
            if (block.Kind == "table" && block.Cells is { } rows)
            {
                var table = new Grid(); int columns = rows.Max(row => row.Length);
                for (int i = 0; i < columns; i++) table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (int row = 0; row < rows.Length; row++)
                {
                    table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    for (int col = 0; col < rows[row].Length; col++)
                    {
                        var text = Rich(rows[row][col], internalLink); if (row == 0) text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                        var border = new Border { Child = text, Padding = new Thickness(6), BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray) }; Grid.SetRow(border, row); Grid.SetColumn(border, col); table.Children.Add(border);
                    }
                }
                panel.Children.Add(table); continue;
            }
            if (block.Kind == "rule") { panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Microsoft.UI.Colors.Gray) }); continue; }
            var body = Rich(block.Text, internalLink);
            if (block.Kind == "heading") { body.FontSize = Math.Max(14, 28 - block.Level * 2); body.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; }
            if (block.Kind == "code") body.FontFamily = new FontFamily("Cascadia Mono");
            if (block.Kind is "quote" or "code") panel.Children.Add(new Border { Child = body, Padding = new Thickness(8), BorderThickness = new Thickness(3, 0, 0, 0), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray) });
            else panel.Children.Add(body);
        }
        return panel;
    }
    private static RichTextBlock Rich(string value, Func<Uri, Task>? internalLink)
    {
        var text = new RichTextBlock { FontSize = 13, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap }; var paragraph = new Paragraph(); text.Blocks.Add(paragraph);
        Append(paragraph.Inlines, value, internalLink); return text;
    }
    private static void Append(InlineCollection inlines, string value, Func<Uri, Task>? internalLink)
    {
        int cursor = 0;
        foreach (Match match in Regex.Matches(value, @"!?\[([^\]]*)\]\(([^)]+)\)|\*\*\*([^*]+)\*\*\*|\*\*([^*]+)\*\*|__([^_]+)__|\*([^*]+)\*|_([^_]+)_|`([^`]+)`"))
        {
            inlines.Add(new Run { Text = value[cursor..match.Index] });
            if (match.Groups[2].Success && Uri.TryCreate(match.Groups[2].Value, UriKind.Absolute, out var target) && target.Scheme is "http" or "https" or "akari")
            {
                if (match.Value.StartsWith('!'))
                {
                    var image = new Image { Source = new BitmapImage(target), MaxWidth = 620, MaxHeight = 420, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(image, match.Groups[1].Value); inlines.Add(new InlineUIContainer { Child = image });
                }
                else { var link = new Hyperlink(); if (target.Scheme == "akari") link.Click += async (_, _) => { if (internalLink != null) await internalLink(target); }; else link.NavigateUri = target; Append(link.Inlines, match.Groups[1].Value, internalLink); inlines.Add(link); }
            }
            else if (match.Groups[3].Success) { var bold = new Bold(); var italic = new Italic(); italic.Inlines.Add(new Run { Text = match.Groups[3].Value }); bold.Inlines.Add(italic); inlines.Add(bold); }
            else if (match.Groups[4].Success || match.Groups[5].Success) { var bold = new Bold(); Append(bold.Inlines, match.Groups[4].Success ? match.Groups[4].Value : match.Groups[5].Value, internalLink); inlines.Add(bold); }
            else if (match.Groups[6].Success || match.Groups[7].Success) { var italic = new Italic(); italic.Inlines.Add(new Run { Text = match.Groups[6].Success ? match.Groups[6].Value : match.Groups[7].Value }); inlines.Add(italic); }
            else if (match.Groups[8].Success) inlines.Add(new Run { Text = match.Groups[8].Value, FontFamily = new FontFamily("Cascadia Mono") });
            else inlines.Add(new Run { Text = match.Groups[1].Value });
            cursor = match.Index + match.Length;
        }
        inlines.Add(new Run { Text = value[cursor..] });
    }
}

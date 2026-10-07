using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace LeagueAkari.WinUI.Services;

public sealed class WrapPanel : Panel
{
    public double Spacing { get; set; } = 8;
    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, row = 0, widest = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableSize.Width) { y += row + Spacing; x = row = 0; }
            x += size.Width + Spacing;
            widest = Math.Max(widest, x - Spacing);
            row = Math.Max(row, size.Height);
        }
        return new Size(widest, y + row);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, row = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width) { y += row + Spacing; x = row = 0; }
            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width + Spacing;
            row = Math.Max(row, size.Height);
        }
        return finalSize;
    }
}

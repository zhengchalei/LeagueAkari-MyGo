using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace LeagueAkari.WinUI.Services;

public sealed record ChartSeries(string Name, double[] Values, global::Windows.UI.Color Color);

/// <summary>Native XAML geometry for match charts. Values and labels remain selectable tooltips.</summary>
public static class MatchCharts
{
    public static Canvas Lines(IReadOnlyList<ChartSeries> series, double[] times, double width = 740, double height = 280)
    {
        var canvas = new Canvas { Width = width, Height = height };
        var values = series.SelectMany(s => s.Values).Where(double.IsFinite).ToArray();
        var min = Math.Min(0, values.DefaultIfEmpty(0).Min());
        var max = Math.Max(1, values.DefaultIfEmpty(1).Max());
        var range = Math.Max(1, max - min);
        for (var axis = 0; axis <= 4; axis++)
        {
            var y = 12 + axis * (height - 40) / 4;
            canvas.Children.Add(new Line { X1 = 55, X2 = width - 10, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(Microsoft.UI.Colors.Gray), Opacity = .2 });
            var label = new TextBlock { Text = (max - range * axis / 4).ToString("0.#"), FontSize = 10 };
            Canvas.SetLeft(label, 2); Canvas.SetTop(label, y - 6); canvas.Children.Add(label);
        }
        foreach (var item in series)
        {
            var line = new Polyline { Stroke = new SolidColorBrush(item.Color), StrokeThickness = 2, Points = new PointCollection() };
            for (var index = 0; index < item.Values.Length; index++)
            {
                if (!double.IsFinite(item.Values[index]))
                {
                    if (line.Points.Count > 0) canvas.Children.Add(line);
                    line = new Polyline { Stroke = new SolidColorBrush(item.Color), StrokeThickness = 2, Points = new PointCollection() };
                    continue;
                }
                var x = 55 + index * (width - 65) / Math.Max(1, item.Values.Length - 1);
                var y = 12 + (max - item.Values[index]) / range * (height - 40);
                line.Points.Add(new global::Windows.Foundation.Point(x, y));
                var point = new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(item.Color) };
                ToolTipService.SetToolTip(point, item.Name + " · " + (index < times.Length ? Time(times[index]) : index) + " · " + item.Values[index].ToString("N0"));
                Canvas.SetLeft(point, x - 2); Canvas.SetTop(point, y - 2); canvas.Children.Add(point);
            }
            canvas.Children.Add(line);
        }
        for (var index = 0; index < times.Length; index += Math.Max(1, times.Length / 8))
        {
            var label = new TextBlock { Text = Time(times[index]), FontSize = 10 };
            Canvas.SetLeft(label, 55 + index * (width - 65) / Math.Max(1, times.Length - 1)); Canvas.SetTop(label, height - 20); canvas.Children.Add(label);
        }
        return canvas;
    }

    public static UIElement Bars(IEnumerable<(string Name, double Value, global::Windows.UI.Color Color)> input, double width = 450)
    {
        var items = input.ToArray(); var maximum = Math.Max(1, items.Where(i => double.IsFinite(i.Value)).Select(i => i.Value).DefaultIfEmpty(1).Max());
        var panel = new StackPanel { Spacing = 5, Width = width };
        foreach (var item in items)
        {
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = item.Name, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            var bar = new Grid();
            bar.Children.Add(new Rectangle { Fill = new SolidColorBrush(item.Color), Width = double.IsFinite(item.Value) ? Math.Max(0, (width - 130) * item.Value / maximum) : 0, HorizontalAlignment = HorizontalAlignment.Left, Height = 18, Opacity = .65 });
            bar.Children.Add(new TextBlock { Text = double.IsFinite(item.Value) ? item.Value.ToString("N0") : "—", FontSize = 11, Margin = new Thickness(4, 0, 0, 0) });
            Grid.SetColumn(bar, 1); row.Children.Add(bar); panel.Children.Add(row);
        }
        return panel;
    }

    public static Canvas Radar(string[] labels, IReadOnlyList<ChartSeries> series, double size = 300)
    {
        var canvas = new Canvas { Width = size, Height = size }; var center = size / 2; var radius = size * .34;
        global::Windows.Foundation.Point Point(int index, double scale) { var angle = index * Math.PI * 2 / labels.Length - Math.PI / 2; return new(center + Math.Cos(angle) * radius * scale, center + Math.Sin(angle) * radius * scale); }
        for (var ring = 1; ring <= 4; ring++)
        {
            var polygon = new Polygon { Stroke = new SolidColorBrush(Microsoft.UI.Colors.Gray), Opacity = .25, Points = new PointCollection() };
            for (var i = 0; i < labels.Length; i++) polygon.Points.Add(Point(i, ring / 4d)); canvas.Children.Add(polygon);
        }
        for (var i = 0; i < labels.Length; i++)
        {
            var axis = Point(i, 1); canvas.Children.Add(new Line { X1 = center, Y1 = center, X2 = axis.X, Y2 = axis.Y, Stroke = new SolidColorBrush(Microsoft.UI.Colors.Gray), Opacity = .2 });
            var point = Point(i, 1.17); var label = new TextBlock { Text = labels[i], FontSize = 10, Width = 86, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
            Canvas.SetLeft(label, point.X - 43); Canvas.SetTop(label, point.Y - 10); canvas.Children.Add(label);
        }
        foreach (var seriesItem in series)
        {
            if (seriesItem.Values.Length < labels.Length) continue;
            if (seriesItem.Values.All(double.IsFinite))
            {
                var polygon = new Polygon { Fill = new SolidColorBrush(seriesItem.Color), Stroke = new SolidColorBrush(seriesItem.Color), Opacity = .3, StrokeThickness = 2, Points = new PointCollection() };
                for (var i = 0; i < labels.Length; i++) polygon.Points.Add(Point(i, Math.Clamp(seriesItem.Values[i], 0, 1)));
                canvas.Children.Add(polygon);
            }
            for (var i = 0; i < labels.Length; i++)
            {
                if (!double.IsFinite(seriesItem.Values[i])) continue;
                var point = Point(i, Math.Clamp(seriesItem.Values[i], 0, 1));
                var dot = new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(seriesItem.Color) };
                ToolTipService.SetToolTip(dot, labels[i] + "\n" + seriesItem.Name + ": " + seriesItem.Values[i].ToString("P1"));
                Canvas.SetLeft(dot, point.X - 3.5); Canvas.SetTop(dot, point.Y - 3.5); canvas.Children.Add(dot);
                var next = (i + 1) % labels.Length;
                if (seriesItem.Values.All(double.IsFinite) || !double.IsFinite(seriesItem.Values[next])) continue;
                var end = Point(next, Math.Clamp(seriesItem.Values[next], 0, 1)); canvas.Children.Add(new Line { X1 = point.X, Y1 = point.Y, X2 = end.X, Y2 = end.Y, Stroke = new SolidColorBrush(seriesItem.Color), StrokeThickness = 2 });
            }
        }
        return canvas;
    }
    public static string Time(double milliseconds) => $"{Math.Max(0, (int)(milliseconds / 60000))}:{Math.Max(0, (int)(milliseconds / 1000) % 60):00}";
}

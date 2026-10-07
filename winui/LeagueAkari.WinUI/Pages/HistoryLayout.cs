using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LeagueAkari.WinUI.Services;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class HistoryPage
{
    private const double SmallSizeThreshold = 1064;
    private readonly Grid _pageLayout = new() { MaxWidth = 1064, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(12) };
    private readonly Grid _historyColumns = new() { ColumnSpacing = 12 };
    private readonly StackPanel _sidebar = new() { Spacing = 10 };
    private readonly Grid _overview = new() { ColumnSpacing = 8, RowSpacing = 8 };
    private readonly StackPanel _pagination = new() { Spacing = 8 };
    private readonly StackPanel _narrowHeading = new() { Spacing = 8 };
    private readonly StackPanel _playerHeader = new() { Spacing = 10, Margin = new Thickness(4, 0, 4, 12) };
    private readonly StackPanel _listHeader = new() { Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };
    private readonly Expander _overviewCollapse = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ScrollViewer _sidebarScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly ScrollViewer _overviewScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private FrameworkElement? _playerTag;
    private bool? _smallLayout;

    private void InitializeHistoryLayout(WrapPanel search, WrapPanel filters, WrapPanel pagination, WrapPanel collect, WrapPanel tag, Expander mastery)
    {
        if (_openPlayer is null) _playerHeader.Children.Add(search);
        _playerHeader.Children.Add(_profile);
        // Region and API choice describe the player being viewed; queue/time/result filters belong to pagination.
        filters.Children.Remove(_region); filters.Children.Remove(_preferred);
        var sources = new WrapPanel { Spacing = 7 }; sources.Children.Add(_region); sources.Children.Add(_preferred); _playerHeader.Children.Add(sources);
        _pagination.Children.Add(filters); _pagination.Children.Add(pagination);
        _pageLabel.TextWrapping = TextWrapping.Wrap; _pageLabel.MaxWidth = 270;
        _playerTag = tag; _tag.Width = 230;
        _overview.Children.Add(Section(_summary));
        _overview.Children.Add(Section(mastery));
        _overview.Children.Add(Section(_assetsSummary));
        _overview.Children.Add(Section(_recent));
        // Detailed collection knobs remain reachable without pushing the overview below the fold.
        _pagination.Children.Add(new Expander { Header = Localization.Text("收集战绩", "Collect games"), Content = collect, HorizontalAlignment = HorizontalAlignment.Stretch });
        _overviewCollapse.Header = Localization.Key("playerTabs.summary.title", Localization.Text("总览", "Overview"));
        _overviewScroll.Content = _overview; _overviewCollapse.Content = _overviewScroll;
        _sidebarScroll.Content = _sidebar;
        _pageLayout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _pageLayout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _pageLayout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var feedback = new StackPanel { Spacing = 4 }; feedback.Children.Add(_progress); feedback.Children.Add(_message); Grid.SetRow(feedback, 1); _pageLayout.Children.Add(feedback);
        Grid.SetRow(_historyColumns, 2); _pageLayout.Children.Add(_historyColumns);
        var root = new Grid(); root.Children.Add(_background.Image); root.Children.Add(_pageLayout); Content = root;
        SizeChanged += (_, args) => UpdateHistoryLayout(args.NewSize.Width, args.NewSize.Height);
        Loaded += (_, _) => UpdateHistoryLayout(ActualWidth, ActualHeight);
        UpdateHistoryLayout(ActualWidth, ActualHeight);
    }

    private static Border Section(UIElement content)
    {
        var border = new Border { Padding = new Thickness(10), CornerRadius = new CornerRadius(5), Child = content };
        NativeAppearance.BindCardSurface(border);
        return border;
    }

    private void UpdateHistoryLayout(double width, double height)
    {
        bool small = width < SmallSizeThreshold;
        int overviewColumns = small && width >= 560 ? 2 : 1;
        if (_overview.ColumnDefinitions.Count != overviewColumns)
        {
            _overview.ColumnDefinitions.Clear(); _overview.RowDefinitions.Clear();
            for (int column = 0; column < overviewColumns; column++) _overview.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            for (int row = 0; row < (_overview.Children.Count + overviewColumns - 1) / overviewColumns; row++) _overview.RowDefinitions.Add(new() { Height = GridLength.Auto });
            for (int index = 0; index < _overview.Children.Count; index++) { Grid.SetColumn((FrameworkElement)_overview.Children[index], index % overviewColumns); Grid.SetRow((FrameworkElement)_overview.Children[index], index / overviewColumns); }
        }
        _overviewScroll.MaxHeight = Math.Max(160, height * .45);
        _overviewCollapse.Header = Localization.Key("playerTabs.summary.title", Localization.Text("总览", "Overview"));
        if (_smallLayout == small) return;
        _smallLayout = small;
        // Reparent the existing controls instead of constructing a second page or duplicating input state.
        _games.Header = null;
        _pageLayout.Children.Remove(_playerHeader);
        _listHeader.Children.Clear();
        _historyColumns.Children.Clear(); _sidebar.Children.Clear(); _narrowHeading.Children.Clear();
        _overviewScroll.Content = null;
        _historyColumns.RowDefinitions.Clear(); _historyColumns.ColumnDefinitions.Clear();
        if (small)
        {
            _historyColumns.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            _historyColumns.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            _narrowHeading.Children.Add(_playerTag!); _overviewScroll.Content = _overview; _narrowHeading.Children.Add(_overviewCollapse); _narrowHeading.Children.Add(_pagination);
            _listHeader.Children.Add(_playerHeader); _listHeader.Children.Add(_narrowHeading); _games.Header = _listHeader;
            Grid.SetColumn(_games, 0); Grid.SetRow(_games, 0); _historyColumns.Children.Add(_games);
        }
        else
        {
            _pageLayout.Children.Add(_playerHeader);
            _historyColumns.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            _historyColumns.ColumnDefinitions.Add(new() { Width = new GridLength(300) });
            _historyColumns.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            _sidebar.Children.Add(_pagination); _sidebar.Children.Add(_playerTag!); _sidebar.Children.Add(_overview);
            Grid.SetColumn(_sidebarScroll, 0); Grid.SetRow(_sidebarScroll, 0); _historyColumns.Children.Add(_sidebarScroll);
            Grid.SetColumn(_games, 1); Grid.SetRow(_games, 0); _historyColumns.Children.Add(_games);
        }
    }
}

using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class HistoryPage
{
    private HistoryJungleLoader? _historyJungleLoader;
    private void AttachHistoryJungleTab(TabView tabs, ChampionSummary champion)
    {
        string puuid = _puuid, server = _source.Server, source = _source.Source;
        var games = champion.Games.Where(g => HistoryJungleData.Eligible(g, puuid)).ToArray();
        if (games.Length == 0) return;
        int revision = _revision; var body = new StackPanel { Spacing = 8 }; var content = new StackPanel { Spacing = 8 };
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap }; var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var actions = new WrapPanel(); var retry = new Button { Content = Localization.Text("重新查询", "Retry") }; var cancel = new Button { Content = Localization.Text("取消", "Cancel"), Visibility = Visibility.Collapsed };
        actions.Children.Add(retry); actions.Children.Add(cancel); body.Children.Add(feedback); body.Children.Add(progress); body.Children.Add(actions); body.Children.Add(content);
        var tab = new TabViewItem { Header = ChampionLabel("jungleTab"), IsClosable = false, Content = new ScrollViewer { Content = body, MaxHeight = 360 } };
        tabs.TabItems.Add(tab);
        CancellationTokenSource? pending = null; bool completed = false;
        _historyJungleLoader ??= new(async (targetServer, preferred, id, token) =>
        {
            // Capture source per request; another player tab or region selection cannot redirect a late timeline.
            var data = new PlayerDataSource(_backend); await data.ConfigureAsync(targetServer, preferred).WaitAsync(token);
            if (data.Source != preferred) throw new InvalidOperationException(Localization.Text("战绩来源已不可用，请刷新战绩后重试", "The history source is unavailable; refresh history before retrying."));
            token.ThrowIfCancellationRequested(); return await data.TimelineAsync(id).WaitAsync(token);
        });
        async Task LoadAsync()
        {
            if (pending is not null || revision != _revision || !IsLoaded) return;
            using var stop = new CancellationTokenSource(); pending = stop;
            progress.Visibility = Visibility.Visible; cancel.Visibility = Visibility.Visible; retry.IsEnabled = false;
            feedback.Text = Localization.Text($"正在查询 {games.Length} 场打野时间线", $"Loading {games.Length} jungle timelines");
            try
            {
                var result = await _historyJungleLoader.LoadAsync(games, puuid, server, source, stop.Token);
                if (revision != _revision || !IsLoaded || !body.IsLoaded) return;
                completed = true; content.Children.Clear();
                var jungle = result.Analysis.Field("jungle");
                feedback.Text = Localization.Text($"{result.LoadedGames}/{result.RequestedGames} 场时间线 · {jungle.Number("gamesAnalyzed"):0} 场详细样本", $"{result.LoadedGames}/{result.RequestedGames} timelines · {jungle.Number("gamesAnalyzed"):0} detailed samples");
                if (jungle.ValueKind == System.Text.Json.JsonValueKind.Object)
                    content.Children.Add(new JunglePathingView(_assets, result.Analysis, champion.ChampionId, inline: true));
                else content.Children.Add(SummaryText(Localization.Text("没有可分析的打野时间线", "No jungle timeline samples available")));
                foreach (var error in result.Errors) content.Children.Add(SummaryText(Localization.Text("时间线", "Timeline") + " " + error.GameId + " · " + error.Message));
            }
            catch (OperationCanceledException) { if (body.IsLoaded) feedback.Text = Localization.Text("已取消查询", "Loading cancelled"); }
            catch (Exception ex) { if (body.IsLoaded) feedback.Text = ex.Message; }
            finally { pending = null; progress.Visibility = Visibility.Collapsed; cancel.Visibility = Visibility.Collapsed; retry.IsEnabled = true; }
        }
        retry.Click += async (_, _) => { completed = false; await LoadAsync(); };
        cancel.Click += (_, _) => pending?.Cancel();
        tabs.SelectionChanged += async (_, _) => { if (ReferenceEquals(tabs.SelectedItem, tab)) { if (!completed) await LoadAsync(); } else pending?.Cancel(); };
        body.Loaded += async (_, _) => { if (ReferenceEquals(tabs.SelectedItem, tab) && !completed) await LoadAsync(); };
        tabs.Unloaded += (_, _) => pending?.Cancel();
    }
}

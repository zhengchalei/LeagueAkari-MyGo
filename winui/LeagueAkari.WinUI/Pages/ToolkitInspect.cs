using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LeagueAkari.WinUI.Services;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class ToolkitPage
{
    public event Action? DraftOpened;
    public event Action<string, string>? PlayerOpened;
    private StackPanel InspectTools()
    {
        var panel = Panel();
        var gameId = new NumberBox { Header = "对局 ID", Minimum = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
        var detail = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var sourceLabel = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var lookup = new GameLookupData(_backend);
        panel.Children.Add(Label("查看指定对局")); panel.Children.Add(gameId);
        panel.Children.Add(Action("读取对局详情", async () =>
        {
            if (!GameLookupData.ValidGameId(gameId.Value)) throw new InvalidOperationException(Localization.Text("请输入有效对局 ID", "Enter a valid game ID"));
            var result = await lookup.LoadAsync((long)gameId.Value);
            var view = new MatchDetailsView(_backend, result.Summary, "", result.Server, result.Source, puuid => PlayerOpened?.Invoke(puuid, result.Server));
            view.SimulationConfigured += () => DraftOpened?.Invoke();
            detail.Content = view;
            sourceLabel.Text = result.Source.ToUpperInvariant() + (result.FallbackReason is null ? "" : Localization.Text(" · SGP 不可用，使用 LCU", " · SGP unavailable; using LCU"));
        }));
        panel.Children.Add(sourceLabel); panel.Children.Add(detail);
        panel.Children.Add(Action("清除模拟，恢复真实对局", async () => { await _backend.CallAsync("ongoing-game-main", "clearDraft"); DraftOpened?.Invoke(); }));
        return panel;
    }
}

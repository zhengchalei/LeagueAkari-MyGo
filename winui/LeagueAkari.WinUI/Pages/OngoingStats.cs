using System.Text.Json;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class OngoingPage
{
    private string QueueName(int id) => _resources.Field("queues").Field(id.ToString()).Text("name", Localization.Translate(MatchData.QueueLabel(id)));
    private string MatchResult(JsonElement game, MatchParticipant player) => game.Text("gameMode") == "PRACTICETOOL" ? "N/A" : player.WinResult is "remake" or "abort" ? Localization.Key("matchCard.result." + player.WinResult, player.WinResult) : game.Text("gameMode") == "CHERRY" ? player.Num("subteamPlacement") > 0 ? L($"第{player.Num("subteamPlacement"):0}名", $"Place {player.Num("subteamPlacement"):0}") : "?" : Localization.Key("matchCard.result." + player.WinResult, player.WinResult);

    private FrameworkElement PlayerStats(string id, JsonElement[] games, HistorySummary summary)
    {
        var analysis = _analysis.GetValueOrDefault(id); var stats = new WrapPanel { Spacing = 5 };
        var wins = OngoingCardData.Wins(_state.Field("queryStage").Field("gameInfo").Text("queueType"), analysis, games, id);
        var rate = new TextBlock { Text = (wins.Count == 0 ? "- %" : $"{wins.Rate:P0}") + (wins.Top1Rate is double top ? " / " + K("playerCard.1st", ("rate", (top * 100).ToString("F0"))) : "") + $" ({wins.Count})", FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = new SolidColorBrush(wins.Rate >= .53 ? Microsoft.UI.Colors.ForestGreen : wins.Rate <= .47 ? Microsoft.UI.Colors.Firebrick : Microsoft.UI.Colors.Gray) };
        ToolTipService.SetToolTip(rate, K(wins.Top1Rate.HasValue ? "playerCard.cherryWinRatePopover" : "playerCard.winRatePopover", ("count", summary.Count), ("winRate", (summary.Wins / (double)Math.Max(1, summary.Count) * 100).ToString("F2")), ("wins", summary.Wins), ("losses", summary.Losses), ("cherryCount", wins.Count), ("top1Rate", ((wins.Top1Rate ?? 0) * 100).ToString("F2")))); stats.Children.Add(rate);
        double Kda(string player) => _analysis.GetValueOrDefault(player).Field("summary").Number("avgKda", MatchData.Summarize(History(player), player).Kda);
        var outliers = OngoingCardData.KdaOutliers(_analysis.Keys.ToDictionary(p => p, Kda));
        var kda = new TextBlock { Text = summary.Count == 0 ? "KDA N/A" : $"KDA {Kda(id):F2}", FontWeight = Microsoft.UI.Text.FontWeights.Bold }; if (outliers.TryGetValue(id, out var kind)) kda.Foreground = new SolidColorBrush(kind == "over" ? Microsoft.UI.Colors.ForestGreen : Microsoft.UI.Colors.Firebrick);
        ToolTipService.SetToolTip(kda, K("playerCard.kdaPopover", ("count", summary.Count), ("kda", Kda(id).ToString("F2")), ("kills", (summary.Kills / Math.Max(1, summary.Count)).ToString("F2")), ("deaths", (summary.Deaths / Math.Max(1, summary.Count)).ToString("F2")), ("assists", (summary.Assists / Math.Max(1, summary.Count)).ToString("F2"))) + $"\nKDA CV: {summary.KdaCv:F2}"); stats.Children.Add(kda);
        var position = OngoingCardData.Position(_state.Field("positionAssignments").Field(id), analysis);
        if (position != null)
        {
            string Name(string value) => Localization.Key("common.positions." + value, value);
            string reason = position.Reason.Length > 0 && position.Reason != "NONE" ? Localization.Key("common.positionAssignmentReason." + position.Reason, position.Reason) : "";
            var text = new TextBlock { Text = (position.Reason == "AUTOFILL" ? reason + " · " : "") + Name(position.Current) + " · " + string.Join(" / ", (position.Recent.Length > 0 ? position.Recent.Take(3) : new[] { position.Primary, position.Secondary }.Where(p => p.Length > 0 && p != "UNSELECTED")).Select(Name)), FontSize = 11 };
            ToolTipService.SetToolTip(text, reason + "\n" + K("playerCard.position.recentlyPlayed") + ": " + string.Join(" / ", position.Recent.Select(Name)) + "\n" + K("playerCard.position.selection") + ": " + string.Join(" / ", new[] { position.Primary, position.Secondary }.Where(p => p.Length > 0 && p != "UNSELECTED").Select(Name))); stats.Children.Add(text);
        }
        return stats;
    }

    private async Task PreviewMatchAsync(string id, long gameId)
    {
        if (!CanPreview) return; int revision = _visibilityRevision; string server = _server;
        var source = new PlayerDataSource(_backend); await source.ConfigureAsync(server, _all.Field("matchHistory").Field(id).Text("source", "lcu"));
        if (!CanPreview || revision != _visibilityRevision) return;
        var detail = await source.DetailsAsync(gameId);
        if (!CanPreview || revision != _visibilityRevision) return;
        await NativeDialogs.TryShowAsync(new ContentDialog { Title = L("对局详情", "Match details"), Content = new MatchDetailsView(_backend, detail, id, server, source.Source, puuid => _openPlayer?.Invoke(puuid, server)), CloseButtonText = L("关闭", "Close"), XamlRoot = XamlRoot }, () => CanPreview && revision == _visibilityRevision);
    }
}

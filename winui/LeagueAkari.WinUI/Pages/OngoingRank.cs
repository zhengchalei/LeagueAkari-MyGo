using System.Text.Json;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class OngoingPage
{
    private FrameworkElement RankTable(JsonElement ranked)
    {
        var table = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        string[] headers = [Localization.Key("ranked.table.queueType", L("队列", "Queue")), Localization.Key("ranked.table.tier", L("段位", "Tier")), "LP", Localization.Key("ranked.table.wins", L("胜", "Wins")), Localization.Key("ranked.table.losses", L("负", "Losses")), Localization.Key("ranked.table.previousSeasonEndTier", L("上赛季结束", "Previous season end")), Localization.Key("ranked.table.previousSeasonHighestTier", L("上赛季最高", "Previous season peak")), Localization.Key("ranked.table.highestTier", L("历史最高", "All-time peak"))];
        foreach (var _ in headers) table.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        void Cell(int row, int col, string value, bool header = false) { var text = new TextBlock { Text = value, FontSize = 11, FontWeight = header ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal }; Grid.SetRow(text, row); Grid.SetColumn(text, col); table.Children.Add(text); }
        table.RowDefinitions.Add(new() { Height = GridLength.Auto }); for (int column = 0; column < headers.Length; column++) Cell(0, column, headers[column], true);
        string Tier(string tier, string division) => PlayerRankEntry.IsTier(tier) ? Localization.Key("common.shortTiers." + tier, tier) + (division.Length > 0 && division != "NA" ? " " + division : "") : "—";
        string Number(double? value) => value?.ToString("N0") ?? "—";
        int index = 1;
        foreach (var rank in PlayerRankEntry.All(ranked))
        {
            table.RowDefinitions.Add(new() { Height = GridLength.Auto });
            string[] values = [Localization.Key("common.queueTypes." + rank.QueueType, rank.QueueType), Tier(rank.Tier, rank.Division), rank.IsRanked ? Number(rank.LeaguePoints) : "—", rank.IsRanked ? Number(rank.Wins) : "—", rank.IsRanked ? Number(rank.Losses) : "—", Tier(rank.PreviousEndTier, rank.PreviousEndDivision), Tier(rank.PreviousHighestTier, rank.PreviousHighestDivision), Tier(rank.HighestTier, rank.HighestDivision)];
            for (int column = 0; column < values.Length; column++) Cell(index, column, values[column]); ++index;
        }
        return new ScrollViewer { Content = table, MaxWidth = 850, MaxHeight = 320, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled };
    }
}

using System.Text.Json;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class OngoingPage
{
    private WrapPanel BuildBadges(string id)
    {
        var tags = new Dictionary<string, FrameworkElement>();
        var analysis = _analysis.GetValueOrDefault(id);
        foreach (var tag in BuildTags(id)) tags[tag.Id] = TagChip(tag, analysis);
        AddSavedBadges(tags, id);
        var badges = new WrapPanel { Spacing = 3 };
        foreach (var key in OngoingCardTags.Order) if (tags.TryGetValue(key, out var chip)) badges.Children.Add(chip);
        return badges;
    }
    private void AddSavedBadges(Dictionary<string, FrameworkElement> badges, string id)
    {
        if (id == SelfPuuid) return;
        var saved = _all.Field("savedInfo").Field(id);
        if (TagEnabled("showTaggedTag"))
        {
            var tags = saved.Field("tags").Items().OrderByDescending(t => t.Boolean("markedBySelf")).ToArray();
            if (tags.Length > 0)
            {
                var details = new StackPanel { Spacing = 8 };
                foreach (var tag in tags)
                {
                    string author = tag.Text("selfPuuid"); var summoner = _all.Field("summoner").Field(author);
                    var row = new StackPanel { Spacing = 3 };
                    if (tag.Boolean("markedBySelf")) row.Children.Add(new TextBlock { Text = K("playerCard.taggedBySelf"), FontSize = 11 });
                    else
                    {
                        var by = Row(); by.Children.Add(new TextBlock { Text = K("playerCard.taggedByOther"), FontSize = 11 });
                        if (summoner.ValueKind == JsonValueKind.Object) { var open = new HyperlinkButton { Content = PrivacyName(author, summoner.Text("gameName"), summoner.Text("tagLine")), Padding = new Thickness(0) }; open.Click += (_, _) => _openPlayer?.Invoke(author, _server); by.Children.Add(open); }
                        else by.Children.Add(new TextBlock { Text = K("playerCard.unknown"), FontSize = 11 });
                        row.Children.Add(by);
                    }
                    row.Children.Add(new TextBlock { Text = tag.Text("tag"), TextWrapping = TextWrapping.Wrap, MaxWidth = 340 }); details.Children.Add(row);
                }
                bool canEdit = !_standalone && SelfPuuid.Length > 0;
                var chip = TagButton(K("playerCard.tagged"), "#49914d");
                var detailsFlyout = AttachTagDetails(chip, new ScrollViewer { Content = details, MaxHeight = 240 }, clickOpen: false);
                if (canEdit) chip.Click += async (_, _) => { detailsFlyout.Hide(); await RunAsync(() => EditTagAsync(id, PrivacyName(id, _all.Field("summoner").Field(id).Text("gameName")))); };
                badges["tagged"] = chip;
            }
        }
        if (!TagEnabled("showMetTag")) return;
        var encounters = OngoingCardData.Encounters(saved, _all.Field("matchHistory"), _all.Field("gameDetails"), SelfPuuid, id, QueueName);
        if (encounters.Games.Length == 0 && !encounters.LastMetAt.HasValue) return;
        var panel = new StackPanel { Spacing = 7, MaxWidth = 768 };
        panel.Children.Add(TagParagraph(K("playerCard.metPopover.title", ("date", encounters.LastMetAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—"), ("count", encounters.Games.Length))));
        panel.Children.Add(TagParagraph(K("playerCard.metPopover.titleNote", ("count", encounters.Games.Length))));
        var table = new Grid { ColumnSpacing = 10, RowSpacing = 8 };
        for (int column = 0; column < 6; column++) table.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        for (int row = 0; row <= encounters.Games.Length; row++) table.RowDefinitions.Add(new() { Height = GridLength.Auto });
        void Cell(FrameworkElement element, int row, int column) { Grid.SetRow(element, row); Grid.SetColumn(element, column); table.Children.Add(element); }
        string[] headers = ["gameId", "date", "result", "relation", "self", "encounteredPlayer"];
        for (int column = 0; column < headers.Length; column++) Cell(new TextBlock { Text = K("playerCard.metPopover." + headers[column]), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, 0, column);
        for (int index = 0; index < encounters.Games.Length; index++)
        {
            var encounter = encounters.Games[index]; int row = index + 1;
            var game = new HyperlinkButton { Content = K("playerCard.metPopover.inspectByGameId", ("gameId", Private ? (index + 1).ToString().PadLeft(6, '●') : encounter.Record.GameId.ToString())), Padding = new Thickness(0) };
            game.Click += async (_, _) => await RunAsync(() => PreviewMatchAsync(id, encounter.Record.GameId)); Cell(game, row, 0);
            var date = encounter.PlayedAt ?? encounter.Record.RecordedAt; var time = new StackPanel(); time.Children.Add(new TextBlock { Text = date?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "—", FontSize = 11 }); if (date.HasValue) time.Children.Add(new TextBlock { Text = "(" + RelativeDate(date.Value) + ")", FontSize = 11, Opacity = .6 }); Cell(time, row, 1);
            if (encounter.Self == null || encounter.Target == null) { for (int column = 2; column < 6; column++) Cell(new TextBlock { Text = "—", Opacity = .5 }, row, column); continue; }
            var result = encounter.Self.Result.Length > 0 ? encounter.Self.Result : encounter.Self.Win ? "win" : "loss";
            Cell(new TextBlock { Text = K("playerCard.metPopover.winResult." + result), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ResultBrush(result) }, row, 2);
            bool enemy = encounter.IsOpponent == true;
            Cell(new TextBlock { Text = K("playerCard.metPopover.team." + (enemy ? "opponent" : "teammate")), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ResultBrush(enemy ? "loss" : "win") }, row, 3);
            Cell(EncounterStats(encounter.Self), row, 4); Cell(EncounterStats(encounter.Target), row, 5);
        }
        panel.Children.Add(table);
        var met = TagButton(K("playerCard." + encounters.LabelKey), "#5cacea", true);
        AttachTagDetails(met, new ScrollViewer { Content = panel, MaxHeight = 320, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto }); badges["met"] = met;
    }
    private static SolidColorBrush ResultBrush(string result) => TagBrush(result == "win" ? "#059669" : result == "loss" ? "#dc2626" : "#6b7280");
    private FrameworkElement EncounterStats(EncounterPlayer player)
    {
        var row = Row();
        if (player.Placement > 0) row.Children.Add(new TextBlock { Text = Localization.IsEnglish ? EnglishOrdinal(player.Placement) : $"第{player.Placement}名", FontSize = 10, Foreground = ResultBrush(player.Result), VerticalAlignment = VerticalAlignment.Center });
        if (player.Position.Length > 0)
        {
            var position = EncounterPositionIcon(player.Position);
            ToolTipService.SetToolTip(position, Localization.Key("common.positions." + player.Position, player.Position)); row.Children.Add(position);
        }
        row.Children.Add(_assets.Icon("champion-summary", player.ChampionId, 16)); row.Children.Add(new TextBlock { Text = $"{player.Kills:0} / {player.Deaths:0} / {player.Assists:0}", FontSize = 11, VerticalAlignment = VerticalAlignment.Center }); return row;
    }
    private static FrameworkElement EncounterPositionIcon(string position)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        void Path(string data, double opacity = 1, bool rotate = false, double center = 13)
        {
            var path = (Microsoft.UI.Xaml.Shapes.Path)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='" + data + "' Fill='{ThemeResource TextFillColorPrimaryBrush}' />");
            path.Opacity = opacity;
            if (rotate) path.RenderTransform = new RotateTransform { Angle = 180, CenterX = center, CenterY = center };
            canvas.Children.Add(path);
        }
        switch (position.ToUpperInvariant())
        {
            case "TOP": case "BOTTOM": case "BOT": case "ADC":
                bool bottom = position != "TOP";
                Path("M19 3l-4 4H7v8l-4 4V3h16z", bottom ? .2 : 1);
                Path("M21 5l-4 4H9v8l-4 4V5h16z", bottom ? 1 : .2, true);
                Path("M10 10H14V14H10z", .2); break;
            case "MIDDLE": case "MID":
                Path("M15 3l-4 4H7v4l-4 4V3h12z", .2);
                Path("M21 9l-4 4h-4v4l-4 4V9h12z", .2, true, 15);
                Path("M18 3L21 3 21 6 6 21 3 21 3 18z"); break;
            case "JUNGLE": case "JUG":
                Path("M5.14 2c1.58 1.21 5.58 5.023 6.976 9.953s0 10.047 0 10.047c-2.749-3.164-5.893-5.2-6.18-5.382l-.02-.013C5.45 13.814 3 8.79 3 8.79c3.536.867 4.93 4.279 4.93 4.279C7.558 8.698 5.14 2 5.14 2zm14.976 5.907s-1.243 2.471-1.814 4.604c-.235.878-.285 2.2-.29 3.058v.282c.003.347.01.568.01.568s-1.738 2.397-3.38 3.678c.088-1.601.062-3.435-.208-5.334.928-2.023 2.846-5.454 5.682-6.856zm-2.124-5.331s-2.325 3.052-2.836 6.029c-.11.636-.201 1.194-.284 1.695-.379.584-.73 1.166-1.05 1.733-.033-.125-.06-.25-.095-.375-.302-1.07-.704-2.095-1.16-3.08.053-.146.103-.29.17-.438 0 0 1.814-3.78 5.255-5.564z"); break;
            case "UTILITY": case "SUPPORT":
                Path("M12.833 10.833L14.5 17.53v.804L12.833 20h-1.666L9.5 18.333v-.804l1.667-6.696h1.666zM7 7.5L9.5 10l-1.667 4.167-2.5-2.5L6.167 10h-2.5L2 7.5h5zm15 0L20.333 10h-2.5l.834 1.667-2.5 2.5L14.5 10 17 7.5h5zM13.743 5l.757.833v.834l-1.667 2.5h-1.666L9.5 6.667v-.834L10.257 5h3.486z"); break;
            default:
                Path("M16.293 17.03c.362.628.147 1.43-.48 1.793-.629.364-1.431.149-1.794-.479l-2.144-3.717-2.144 3.717c-.363.628-1.165.843-1.793.48-.628-.363-.843-1.166-.48-1.793l2.144-3.718h-4.29c-.724 0-1.312-.587-1.312-1.312 0-.727.588-1.314 1.313-1.314h4.289L7.457 6.969c-.362-.627-.147-1.43.48-1.792.629-.364 1.431-.149 1.794.479l2.144 3.717 2.144-3.717c.363-.628 1.165-.843 1.793-.48.628.363.843 1.166.48 1.793l-2.144 3.718h4.29c.725 0 1.312.587 1.312 1.312 0 .727-.587 1.314-1.312 1.314h-4.29l2.145 3.718z"); break;
        }
        return new Viewbox { Child = canvas, Width = 16, Height = 16 };
    }
    private static string EnglishOrdinal(int number) => number + ((number % 100 is >= 11 and <= 13) ? "th" : (number % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
    private static string RelativeDate(DateTimeOffset date)
    {
        double seconds = (DateTimeOffset.Now - date).TotalSeconds; bool future = seconds < 0; seconds = Math.Abs(seconds);
        string zh, en;
        if (seconds < 45) { zh = "几秒"; en = "a few seconds"; }
        else if (seconds < 90) { zh = "1 分钟"; en = "a minute"; }
        else if (seconds < 2700) { zh = $"{Math.Round(seconds / 60)} 分钟"; en = $"{Math.Round(seconds / 60)} minutes"; }
        else if (seconds < 5400) { zh = "1 小时"; en = "an hour"; }
        else if (seconds < 79200) { zh = $"{Math.Round(seconds / 3600)} 小时"; en = $"{Math.Round(seconds / 3600)} hours"; }
        else if (seconds < 129600) { zh = "1 天"; en = "a day"; }
        else if (seconds < 2246400) { zh = $"{Math.Round(seconds / 86400)} 天"; en = $"{Math.Round(seconds / 86400)} days"; }
        else if (seconds < 3888000) { zh = "1 个月"; en = "a month"; }
        else if (seconds < 27648000) { zh = $"{Math.Round(seconds / 2592000)} 个月"; en = $"{Math.Round(seconds / 2592000)} months"; }
        else if (seconds < 47088000) { zh = "1 年"; en = "a year"; }
        else { zh = $"{Math.Round(seconds / 31536000)} 年"; en = $"{Math.Round(seconds / 31536000)} years"; }
        return Localization.IsEnglish ? future ? "in " + en : en + " ago" : zh + (future ? "后" : "前");
    }
}

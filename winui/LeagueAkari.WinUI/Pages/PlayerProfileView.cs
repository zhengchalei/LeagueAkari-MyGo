using System.Text.Json;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;

namespace LeagueAkari.WinUI.Pages;

/// <summary>Native player header, two ranked cards and the complete original ranked table.</summary>
public sealed class PlayerProfileView : UserControl
{
    private readonly BackendClient _backend;
    private readonly NativeImages _images;
    private readonly Func<Task>? _refresh, _editTag;
    private readonly StackPanel _body = new() { Spacing = 8 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private PlayerProfileData? _player;
    private PlayerRankEntry[] _ranks = [];
    private bool _crossRegion, _isSelf, _compact, _loaded, _refreshing;
    private int _revision;
    private PlayerDataSource? _source;
    private string _phase = "";
    private CancellationTokenSource? _rankRetry;
    private static string L(string zh, string en) => Localization.Text(zh, en);
    private static string Queue(string queue) => Localization.Key("common.queueTypes." + queue, queue);
    private static string Tier(string tier, string division, bool table = false)
    {
        if (!PlayerRankEntry.IsTier(tier)) return table ? "—" : Localization.Key("playerTabs.ranked.unranked", L("未定级", "Unranked"));
        string name = Localization.Key("common.tiers." + tier, tier);
        return name + (PlayerRankEntry.IsTier(division) ? " " + division : "");
    }
    public PlayerProfileView(BackendClient backend, Func<Task>? refresh = null, Func<Task>? editTag = null)
    {
        _backend = backend; _images = new(backend); _refresh = refresh; _editTag = editTag;
        Content = _body;
        Loaded += async (_, _) => { _loaded = true; Localization.Changed += AppearanceChanged; _backend.EventReceived += BackendEvent; if (NativeAppearance.Current is { } appearance) appearance.Changed += AppearanceChanged; Render(); try { _phase = (await _backend.StateAsync("league-client-main", "gameflow")).Text("phase"); } catch { } };
        Unloaded += (_, _) => { _loaded = false; _rankRetry?.Cancel(); Localization.Changed -= AppearanceChanged; _backend.EventReceived -= BackendEvent; if (NativeAppearance.Current is { } appearance) appearance.Changed -= AppearanceChanged; };
        SizeChanged += (_, args) => { bool compact = args.NewSize.Width < 740; if (compact != _compact) { _compact = compact; Render(); } };
    }
    public void SetData(JsonElement player, JsonElement ranked = default, JsonElement social = default, bool crossRegion = false, bool isSelf = false, PlayerDataSource? source = null)
    {
        if (source is not null) _source = source;
        _revision++; _player = PlayerProfileData.Read(player, social); _ranks = PlayerRankEntry.All(ranked); _crossRegion = crossRegion; _isSelf = isSelf; Render();
    }
    public async Task UpdateAsync(JsonElement player, PlayerDataSource source, bool isSelf = false)
    {
        _source = source;
        string puuid = player.Text("puuid");
        if (_player?.Puuid != puuid) _ranks = [];
        _player = PlayerProfileData.Read(player); _crossRegion = source.IsCrossRegion; _isSelf = isSelf; Render();
        int revision = ++_revision; string? error = null; JsonElement ranked = default;
        if (source.SupportsRanked) try { ranked = await source.RankedAsync(puuid); } catch (Exception ex) { error = L("段位：", "Rank: ") + ex.Message; }
        if (revision != _revision) return;
        if (ranked.ValueKind == JsonValueKind.Object) _ranks = PlayerRankEntry.All(ranked);
        Render(); _status.Text = error ?? "";
    }
    private void BackendEvent(JsonElement envelope)
    {
        if (envelope.Text("name") != "update-state-prop/league-client-main:gameflow") return;
        var args = envelope.Field("args").Items().ToArray();
        if (args.Length < 2 || args[0].ValueKind != JsonValueKind.String || args[0].GetString() != "phase") return;
        string phase = args[1].ValueKind == JsonValueKind.String ? args[1].GetString() ?? "" : "";
        DispatcherQueue.TryEnqueue(async () =>
        {
            bool entering = phase is "EndOfGame" or "PreEndOfGame" && _phase is not "EndOfGame" and not "PreEndOfGame";
            _phase = phase;
            if (!entering || !_loaded || !_isSelf || _crossRegion || _source is null || _player is null) return;
            try
            {
                var flow = await _backend.StateAsync("league-client-main", "gameflow");
                var queue = flow.Field("session").Field("gameData").Field("queue");
                if (queue.Number("id") is not 420 and not 440 && queue.Text("type") is not "RANKED_SOLO_5x5" and not "RANKED_FLEX_SR") return;
                _rankRetry?.Cancel(); _rankRetry = new(); var retry = _rankRetry;
                int revision = _revision; string before = Snapshot();
                await RefreshRankOnly(revision);
                if (Snapshot() != before) return;
                await Task.Delay(3000, retry.Token);
                if (_loaded && revision == _revision) await RefreshRankOnly(revision);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _status.Text = ex.Message; }
        });
    }
    private string Snapshot() => string.Join("|", _ranks.Where(rank => rank.QueueType is "RANKED_SOLO_5x5" or "RANKED_FLEX_SR").Select(rank => $"{rank.QueueType}:{rank.Tier}:{rank.Division}:{rank.LeaguePoints}:{rank.Wins}:{rank.Losses}"));
    private async Task RefreshRankOnly(int revision)
    {
        if (_source is null || _player is null) return;
        var result = await _source.RankedAsync(_player.Puuid);
        if (!_loaded || revision != _revision) return;
        _ranks = PlayerRankEntry.All(result); Render();
    }
    private void AppearanceChanged() => DispatcherQueue.TryEnqueue(() => { if (_loaded) Render(); });
    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private void Render()
    {
        _body.Children.Clear(); if (_player is not { } player) return;
        bool privateMode = NativeAppearance.Current?.StreamerMode == true;
        var header = new WrapPanel();
        var portrait = new Grid { Width = 64, Height = 70 }; var avatar = new Image { Width = 64, Height = 64, VerticalAlignment = VerticalAlignment.Top };
        _ = _images.SetAsync(avatar, $"/lol-game-data/assets/v1/profile-icons/{player.IconId}.jpg"); portrait.Children.Add(avatar);
        if (player.Level is { } level) portrait.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 1, 4, 1), Background = new SolidColorBrush(Microsoft.UI.Colors.DimGray), Child = new TextBlock { Text = level.ToString("0"), Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), FontSize = 10 } });
        header.Children.Add(portrait);
        var identity = new StackPanel { Spacing = 3, Margin = new Thickness(10, 0, 14, 0), MinWidth = 180, MaxWidth = 350 };
        identity.Children.Add(new TextBlock { Text = privateMode ? NativeAppearance.Current!.SummonerPlaceholder(player.Name is "" or "—" ? player.Puuid : player.Name, 0) : player.Name, FontSize = player.Name.Length >= 16 ? 16 : 24, FontWeight = Microsoft.UI.Text.FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
        identity.Children.Add(new TextBlock { Text = privateMode ? "#####" : player.Tag.Length > 0 ? "#" + player.Tag : "—", FontSize = 12 });
        header.Children.Add(identity);
        var ranks = new WrapPanel();
        if (_crossRegion) ranks.Children.Add(new Border { Padding = new Thickness(16), Child = new TextBlock { Text = Localization.Key("playerTabs.ranked.crossRegion", L("跨大区", "Cross Region")) + "\n" + Localization.Key("playerTabs.ranked.unavailable", L("段位信息不可用", "Unavailable")), TextWrapping = TextWrapping.Wrap } });
        else foreach (var entry in _ranks.Where(rank => rank.QueueType is "RANKED_SOLO_5x5" or "RANKED_FLEX_SR")) ranks.Children.Add(RankCard(entry));
        header.Children.Add(ranks);
        var actions = Row();
        var copy = Action(L("复制名字", "Copy Riot ID"), () => { var data = new DataPackage(); data.SetText(player.RiotId); Clipboard.SetContent(data); return Task.CompletedTask; }); copy.IsEnabled = !privateMode; actions.Children.Add(copy);
        if (_ranks.Count(rank => rank.QueueType is "RANKED_SOLO_5x5" or "RANKED_FLEX_SR") > 1 && !_crossRegion) actions.Children.Add(Action(Localization.Key("playerTabs.profile.rankedMore", L("更多排位信息", "More ranked information")), ShowRanks));
        if (_editTag is not null && !_isSelf && !_crossRegion) actions.Children.Add(Action(L("标记玩家", "Tag player"), _editTag));
        if (_refresh is not null)
        {
            var refresh = Action(L("刷新", "Refresh"), async () => { _refreshing = true; Render(); try { await _refresh(); } finally { _refreshing = false; Render(); } });
            refresh.IsEnabled = !_refreshing; actions.Children.Add(refresh);
        }
        _body.Children.Add(header); _body.Children.Add(actions); _body.Children.Add(_status);
    }
    private Button Action(string label, Func<Task> action)
    {
        var button = new Button { Content = label, Padding = new Thickness(8, 4, 8, 4) };
        button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); _status.Text = ""; } catch (Exception ex) { _status.Text = ex.Message; } finally { button.IsEnabled = true; } }; return button;
    }
    private UIElement RankCard(PlayerRankEntry rank)
    {
        var card = new StackPanel { Spacing = 5, Width = _compact ? 145 : 220, Margin = new Thickness(0, 0, 10, 4) };
        var heading = Row(); heading.Children.Add(new TextBlock { Text = Queue(rank.QueueType), FontSize = 11 });
        if (rank.WinRate is { } rate) heading.Children.Add(new TextBlock { Text = L("胜率 ", "Win ") + rate.ToString("P1"), FontSize = 10 }); card.Children.Add(heading);
        var content = Row(); if (!_compact) content.Children.Add(RankImage(rank.Tier, true, 58));
        var text = new StackPanel { Spacing = 3 }; text.Children.Add(new TextBlock { Text = Tier(rank.Tier, rank.Division), FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 16 });
        text.Children.Add(new TextBlock { Text = rank.IsRanked ? $"{Number(rank.Wins)} {L("胜", "W")}" + (rank.Losses is > 0 ? $" {Number(rank.Losses)} {L("负", "L")}" : "") + $" · {Number(rank.LeaguePoints)} LP" : "—", FontSize = 11 });
        var peak = Row(); peak.Children.Add(new TextBlock { Text = Localization.Key("playerTabs.ranked.highest", L("最高", "Peak")), FontSize = 10 }); if (PlayerRankEntry.IsTier(rank.HighestTier)) peak.Children.Add(RankImage(rank.HighestTier, false, 15)); peak.Children.Add(new TextBlock { Text = Tier(rank.HighestTier, rank.HighestDivision), FontSize = 10 }); text.Children.Add(peak); content.Children.Add(text); card.Children.Add(content);
        return new Border { Child = card, Padding = new Thickness(10), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(35, 128, 128, 128)), Margin = new Thickness(0, 0, 8, 4) };
    }
    private static string Number(double? value) => value?.ToString("0") ?? "—";
    private static Image RankImage(string tier, bool large, int size)
    {
        string name = PlayerRankEntry.IsTier(tier) ? tier.ToLowerInvariant() : "unranked";
        var image = new Image { Width = size, Height = size, Stretch = Stretch.Uniform };
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, large ? "ranked-icons-large" : "ranked-icons", name + ".png");
        if (System.IO.File.Exists(path)) image.Source = new BitmapImage(new Uri(path)); return image;
    }
    private async Task ShowRanks()
    {
        var table = new Grid { ColumnSpacing = 14, RowSpacing = 10 };
        string[] labels = [Localization.Key("ranked.table.queueType", L("队列", "Queue")), Localization.Key("ranked.table.tier", L("段位", "Tier")), "LP", Localization.Key("ranked.table.wins", L("胜", "Wins")), Localization.Key("ranked.table.losses", L("负", "Losses")), Localization.Key("ranked.table.previousSeasonEndTier", L("上赛季结束", "Previous season end")), Localization.Key("ranked.table.previousSeasonHighestTier", L("上赛季最高", "Previous season peak")), Localization.Key("ranked.table.highestTier", L("历史最高", "All-time peak"))];
        for (int column = 0; column < labels.Length; column++) table.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        void Cell(int row, int column, FrameworkElement content) { Grid.SetRow(content, row); Grid.SetColumn(content, column); table.Children.Add(content); }
        table.RowDefinitions.Add(new() { Height = GridLength.Auto });
        for (int column = 0; column < labels.Length; column++) Cell(0, column, new TextBlock { Text = labels[column], FontWeight = Microsoft.UI.Text.FontWeights.Bold });
        for (int index = 0; index < _ranks.Length; index++)
        {
            int row = index + 1; var rank = _ranks[index]; table.RowDefinitions.Add(new() { Height = GridLength.Auto });
            Cell(row, 0, new TextBlock { Text = Queue(rank.QueueType) });
            void TierCell(int column, string tier, string division) { var content = Row(); if (PlayerRankEntry.IsTier(tier)) content.Children.Add(RankImage(tier, false, 16)); content.Children.Add(new TextBlock { Text = Tier(tier, division, true) }); Cell(row, column, content); }
            TierCell(1, rank.Tier, rank.Division);
            Cell(row, 2, new TextBlock { Text = rank.IsRanked ? Number(rank.LeaguePoints) : "—" }); Cell(row, 3, new TextBlock { Text = rank.IsRanked ? Number(rank.Wins) : "—" }); Cell(row, 4, new TextBlock { Text = rank.IsRanked ? Number(rank.Losses) : "—" });
            TierCell(5, rank.PreviousEndTier, rank.PreviousEndDivision); TierCell(6, rank.PreviousHighestTier, rank.PreviousHighestDivision); TierCell(7, rank.HighestTier, rank.HighestDivision);
        }
        var body = new StackPanel { Spacing = 16 }; var cards = new WrapPanel(); foreach (var rank in _ranks.Where(rank => rank.QueueType is "RANKED_SOLO_5x5" or "RANKED_FLEX_SR")) cards.Children.Add(RankCard(rank)); body.Children.Add(cards); body.Children.Add(table);
        await NativeDialogs.ShowAsync(new ContentDialog { Title = Localization.Key("playerTabs.profile.rankedMore", L("排位信息", "Ranked information")), Content = new ScrollViewer { Content = body, MaxHeight = 520, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto }, CloseButtonText = L("关闭", "Close"), XamlRoot = XamlRoot });
    }
}

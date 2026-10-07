using System.Text.Json;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LeagueAkari.WinUI.Pages;

public sealed class RecentlyPlayersView : UserControl
{
    private readonly BackendClient _backend;
    private readonly NativeImages _images;
    private readonly Action<string> _openPlayer;
    private readonly StackPanel _recent = new() { Spacing = 10 };
    private readonly StackPanel _encounters = new() { Spacing = 8 };
    private readonly TextBlock _heading = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _previous = new();
    private readonly Button _next = new();
    private readonly Button _reload = new();
    private RecentPlayer[] _players = [];
    private EncounterGame[] _games = [];
    private PlayerDataSource? _source;
    private string _target = "", _self = "", _region = "", _platform = "";
    private int _page = 1, _revision;
    private int _total;
    private bool _busy, _eligible;
    private bool _reloadPending;
    private string? _error;
    public event Action<EncounterGame>? OpenEncounter;
    public event Action<string>? OpenPlayerInBackground;
    public Func<int, string>? QueueName { get; set; }

    public RecentlyPlayersView(BackendClient backend, Action<string> openPlayer)
    {
        _backend = backend; _images = new(backend); _openPlayer = openPlayer;
        var layout = new StackPanel { Spacing = 14 }; layout.Children.Add(_recent);
        var saved = new StackPanel { Spacing = 8 }; saved.Children.Add(_heading); saved.Children.Add(_encounters);
        var paging = new WrapPanel(); paging.Children.Add(_previous); paging.Children.Add(_status); paging.Children.Add(_next); paging.Children.Add(_reload); saved.Children.Add(paging); layout.Children.Add(saved); Content = layout;
        _previous.Click += async (_, _) => await LoadPageAsync(_page - 1); _next.Click += async (_, _) => await LoadPageAsync(_page + 1);
        _reload.Click += async (_, _) => await LoadPageAsync(_page);
        Loaded += (_, _) => { Localization.Changed += RefreshText; if (NativeAppearance.Current is { } appearance) appearance.Changed += RefreshText; RefreshText(); };
        Unloaded += (_, _) => { Localization.Changed -= RefreshText; if (NativeAppearance.Current is { } appearance) appearance.Changed -= RefreshText; _reloadPending = false; ++_revision; };
    }
    public void SetHistory(IEnumerable<JsonElement> filteredGames, string targetPuuid)
    {
        _players = EncounterData.RecentPlayers(filteredGames, targetPuuid); RenderRecent();
    }
    public async Task ConfigureEncountersAsync(PlayerDataSource source, string targetPuuid, string selfPuuid, string region, string rsoPlatformId)
    {
        ++_revision; _source = source; _target = targetPuuid; _self = selfPuuid; _region = region; _platform = rsoPlatformId;
        _eligible = EncounterData.CanLoadSaved(_target, _self, source.IsCrossRegion); _games = []; _total = 0; _page = 1;
        RefreshText(); if (_eligible) { if (_busy) _reloadPending = true; else await LoadPageAsync(1); }
    }
    private void RefreshText() => DispatcherQueue.TryEnqueue(() => { RenderRecent(); RenderEncounters(); });
    private void RenderRecent()
    {
        _recent.Children.Clear();
        foreach (bool enemy in new[] { false, true })
        {
            var players = _players.Where(p => p.IsOpponent == enemy).ToArray(); if (players.Length == 0) continue;
            var group = new StackPanel { Spacing = 5 }; group.Children.Add(new TextBlock { Text = enemy ? Localization.Text("近期对手", "Recent opponents") : Localization.Text("近期队友", "Recent teammates"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            for (int i = 0; i < players.Length; i++)
            {
                var player = players[i]; var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var icon = new Image { Width = 20, Height = 20, Margin = new Thickness(0, 0, 5, 0) }; row.Children.Add(icon); _ = _images.SetAsync(icon, $"/lol-game-data/assets/v1/profile-icons/{player.ProfileIconId}.jpg");
                string name = NativeAppearance.Current?.StreamerMode == true ? NativeAppearance.Current.SummonerPlaceholder(player.Puuid, i) : player.Name + (player.Tag.Length > 0 ? " #" + player.Tag : "");
                var open = new HyperlinkButton { Content = name, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left }; open.Click += (_, _) => _openPlayer(player.Puuid); bool middle = false;
                open.PointerPressed += (_, args) => { middle = args.GetCurrentPoint(open).Properties.IsMiddleButtonPressed; if (middle) args.Handled = true; };
                open.PointerReleased += (_, args) => { if (!middle) return; middle = false; args.Handled = true; if (OpenPlayerInBackground is { } background) background(player.Puuid); else _openPlayer(player.Puuid); };
                Grid.SetColumn(open, 1); row.Children.Add(open);
                var count = new TextBlock { Text = Localization.Text($"{player.Games} 次 · {player.Wins} 胜 {player.Losses} 负", $"{player.Games} games · {player.Wins}W {player.Losses}L"), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(count, 2); row.Children.Add(count); group.Children.Add(row);
            }
            _recent.Children.Add(group);
        }
    }
    public async Task LoadPageAsync(int page)
    {
        if (!_eligible || _source is null || _busy || page < 1) return;
        int revision = ++_revision; _busy = true; _error = null; _previous.IsEnabled = _next.IsEnabled = _reload.IsEnabled = false;
        try
        {
            var result = await _backend.CallAsync("saved-player-main", "queryEncounteredGames", new { puuid = _target, selfPuuid = _self, region = _region.Length > 0 ? _region : null, rsoPlatformId = _platform.Length > 0 ? _platform : null, page, pageSize = 10 });
            var records = result.Field("data").Items().Select(EncounterRecord.Parse).ToArray(); var source = _source;
            var loaded = await Task.WhenAll(records.Select(async record => { JsonElement summary = default; try { summary = await source.DetailsAsync(record.GameId); } catch { } return EncounterData.Project(record, summary, QueueName); }));
            if (revision != _revision) return;
            _page = page; _total = (int)result.Number("total"); _games = loaded.OfType<EncounterGame>().ToArray(); RenderEncounters();
        }
        catch (Exception ex) { if (revision == _revision) _error = ex.Message; }
        finally { _busy = false; if (revision == _revision) UpdatePaging(); if (_reloadPending) { _reloadPending = false; await LoadPageAsync(1); } }
    }
    private void UpdatePaging()
    {
        _previous.Content = Localization.Text("上一页", "Previous"); _next.Content = Localization.Text("下一页", "Next");
        _reload.Content = Localization.Text("刷新", "Refresh"); _reload.IsEnabled = !_busy;
        _previous.IsEnabled = !_busy && _page > 1; _next.IsEnabled = !_busy && _page * 10 < _total;
        _status.Text = _error ?? (_total > 0 ? $" {_page} / {Math.Max(1, (_total + 9) / 10)} " : "");
    }
    private void RenderEncounters()
    {
        _heading.Text = Localization.Text($"遇见记录 ({_total})", $"Encounter history ({_total})"); _heading.Visibility = _eligible ? Visibility.Visible : Visibility.Collapsed;
        _encounters.Children.Clear(); _previous.Visibility = _next.Visibility = _reload.Visibility = _status.Visibility = _eligible ? Visibility.Visible : Visibility.Collapsed;
        foreach (var game in _games)
        {
            var card = new StackPanel { Spacing = 5 }; string side = game.IsOpponent is null ? Localization.Text("详情不可用", "Details unavailable") : game.IsOpponent.Value ? Localization.Text("对手", "Opponent") : Localization.Text("队友", "Teammate");
            var open = new Button { Content = $"{side} · {game.QueueName} · {game.PlayedAt?.ToLocalTime().ToString("MM-dd") ?? "—"} · #{game.Record.GameId}", HorizontalAlignment = HorizontalAlignment.Stretch }; open.Click += (_, _) => OpenEncounter?.Invoke(game); card.Children.Add(open);
            var stats = new WrapPanel(); if (game.Self is { } self) stats.Children.Add(PlayerStats(self, Localization.Text("我", "Me"))); if (game.Target is { } target) stats.Children.Add(PlayerStats(target, Localization.Text("对方", "Player")));
            var delete = new Button { Content = Localization.Text("删除记录", "Delete record") }; delete.Click += async (_, _) => await DeleteAsync(game.Record); stats.Children.Add(delete); card.Children.Add(stats); _encounters.Children.Add(card);
        }
        if (_eligible && _total == 0) _encounters.Children.Add(new TextBlock { Text = Localization.Text("没有保存的遇见记录", "No saved encounters") }); UpdatePaging();
    }
    private FrameworkElement PlayerStats(EncounterPlayer player, string label)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 0, 12, 0) }; var icon = new Image { Width = 24, Height = 24 }; panel.Children.Add(icon); _ = _images.SetAsync(icon, $"/lol-game-data/assets/v1/champion-icons/{player.ChampionId}.png");
        string result = player.Placement > 0 ? Localization.Text($"第{player.Placement}名", $"Place {player.Placement}") : player.Win ? Localization.Text("胜", "Win") : Localization.Text("负", "Loss"); panel.Children.Add(new TextBlock { Text = $"{label} · {result} · {player.Kills}/{player.Deaths}/{player.Assists}", VerticalAlignment = VerticalAlignment.Center }); return panel;
    }
    private async Task DeleteAsync(EncounterRecord record)
    {
        var confirm = new ContentDialog { Title = Localization.Text("删除遇见记录", "Delete encounter"), Content = Localization.Text("仅删除这条保存的遇见记录，是否继续？", "Delete this saved encounter record?"), PrimaryButtonText = Localization.Text("删除", "Delete"), CloseButtonText = Localization.Text("取消", "Cancel"), DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot };
        if (await NativeDialogs.ShowAsync(confirm) != ContentDialogResult.Primary) return;
        try { await _backend.CallAsync("saved-player-main", "deleteEncounteredGame", record.Id); await LoadPageAsync(_games.Length == 1 && _page > 1 ? _page - 1 : _page); } catch (Exception ex) { _status.Text = ex.Message; }
    }
}

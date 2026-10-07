using System.Text.Json;
using System.Text.Json.Nodes;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LeagueAkari.WinUI.Pages;

internal sealed class AutomationMiscView : UserControl
{
    private readonly BackendClient _backend;
    private readonly SettingsForms _forms;
    private readonly AutoReplyDraft _reply;
    private readonly AutomationInvitationsController _invitations;
    private readonly NativeImages _images;
    private readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _save = new() { AllowFocusOnInteraction = false }, _refresh = new();
    private readonly TextBlock _replyStatus = new() { TextWrapping = TextWrapping.Wrap }, _invitationStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _search = new() { Width = 288, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _friends = new() { Spacing = 8 };
    private readonly TextBlock _replyTitle = new() { FontSize = 20 }, _invitationTitle = new() { FontSize = 20 }, _invitationDescription = new() { TextWrapping = TextWrapping.Wrap };
    private bool _attached, _updating;
    private int _lifecycle;
    private int _replySaveVersion;
    private bool _replyExplicitlySaved;
    private string? _friendSubscription;
    public AutomationMiscView(BackendClient backend, SettingsForms forms)
    {
        _backend = backend; _forms = forms; _images = new(backend);
        _reply = new(text => forms.Save("auto-misc-main", "autoReplyText", JsonValue.Create(text)));
        _invitations = new((ns, method, args) => backend.CallAsync(ns, method, args));
        var root = new StackPanel { Spacing = 24 };
        var reply = new StackPanel { Spacing = 12 }; reply.Children.Add(_replyTitle);
        reply.Children.Add(forms.Toggle("auto-misc-main", "autoReplyEnabled", "自动回复"));
        var away = forms.Toggle("auto-misc-main", "autoReplyEnableOnAway", "仅离开时回复");
        ToolTipService.SetToolTip(away, Localization.Key("automation.misc.autoReply.enableOnAway.description")); reply.Children.Add(away);
        reply.Children.Add(_text); reply.Children.Add(_save); reply.Children.Add(_replyStatus); root.Children.Add(reply);
        var invitations = new StackPanel { Spacing = 12 }; invitations.Children.Add(_invitationTitle); invitations.Children.Add(_invitationDescription);
        invitations.Children.Add(_search); invitations.Children.Add(_refresh); invitations.Children.Add(_invitationStatus);
        invitations.Children.Add(new ScrollViewer { Content = _friends, MaxHeight = 400 }); root.Children.Add(invitations); Content = root;
        _text.TextChanged += (_, _) => { if (!_updating) { _replyExplicitlySaved = false; _reply.Text = _text.Text; RenderReply(); } };
        _text.LostFocus += async (_, _) =>
        {
            // A click on Save also moves focus; let that explicit action own the request.
            int saveVersion = _replySaveVersion, lifecycle = _lifecycle; await Task.Delay(50);
            if (_attached && lifecycle == _lifecycle && saveVersion == _replySaveVersion && !_replyExplicitlySaved) await _reply.SaveAsync();
        };
        _save.Click += async (_, _) => { _replyExplicitlySaved = true; ++_replySaveVersion; await _reply.SaveAsync(); };
        _search.TextChanged += (_, _) => RenderFriends(); _refresh.Click += async (_, _) => await _invitations.RefreshAsync();
        _reply.Changed += RenderReply; _invitations.Changed += RenderFriends;
        forms.ObserveValue(this, "auto-misc-main", "autoReplyText", value => _reply.Update(value?.GetValue<string>() ?? ""));
        forms.ObserveValue(this, "auto-misc-main", "autoReplyEnabled", _ => RenderReply());
        Loaded += async (_, _) =>
        {
            _attached = true; int lifecycle = ++_lifecycle; _reply.Activate(forms.Value("auto-misc-main", "autoReplyText")?.GetValue<string>() ?? ""); _invitations.Activate();
            _backend.EventReceived += BackendEvent; Localization.Changed += LanguageChanged;
            if (NativeAppearance.Current is { } appearance) appearance.Changed += LanguageChanged;
            LanguageChanged(); await _invitations.RefreshAsync();
            try
            {
                var result = await _backend.CallAsync("league-client-main", "subscribeLcuEndpoint", "/lol-chat/v1/friends/:id");
                string id = result.ValueKind == JsonValueKind.String ? result.GetString()! : "";
                if (!_attached || lifecycle != _lifecycle) { if (id.Length > 0) await _backend.CallAsync("league-client-main", "unsubscribeLcuEndpoint", id); }
                else _friendSubscription = id;
            }
            catch (Exception error) { if (_attached && lifecycle == _lifecycle) _invitationStatus.Text = error.Message; }
        };
        Unloaded += (_, _) =>
        {
            _attached = false; ++_lifecycle; _reply.Deactivate(); _invitations.Deactivate(); _backend.EventReceived -= BackendEvent; Localization.Changed -= LanguageChanged;
            string? subscription = _friendSubscription; _friendSubscription = null; if (!string.IsNullOrEmpty(subscription)) _ = UnsubscribeAsync(subscription);
            if (NativeAppearance.Current is { } appearance) appearance.Changed -= LanguageChanged;
        };
    }
    private async Task UnsubscribeAsync(string subscription) { try { await _backend.CallAsync("league-client-main", "unsubscribeLcuEndpoint", subscription); } catch { } }
    private void BackendEvent(JsonElement update) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (!_attached) return;
        var args = update.Field("args").Items().ToArray();
        bool friend = update.Text("name") == "league-client-main/extra-lcu-event" && args.Length >= 3 && args[0].ValueKind == JsonValueKind.String && args[0].GetString() == _friendSubscription;
        if (_invitations.ApplyEvent(update) || friend) await _invitations.RefreshAsync();
    });
    private void LanguageChanged()
    {
        if (!_attached) return;
        _replyTitle.Text = Localization.Key("automation.misc.autoReply.title"); _text.Header = Localization.Key("automation.misc.autoReply.text.label");
        _save.Content = Localization.Key("automation.misc.autoReply.text.save");
        _invitationTitle.Text = Localization.Key("automation.misc.autoInvitation.title"); _invitationDescription.Text = Localization.Key("automation.misc.autoInvitation.description");
        _search.PlaceholderText = Localization.Key("automation.misc.autoInvitation.searchPlaceholder"); _refresh.Content = Localization.Text("刷新好友", "Refresh friends");
        RenderReply(); RenderFriends();
    }
    private void RenderReply()
    {
        if (!_attached) return;
        _updating = true; try { if (_text.Text != _reply.Text) _text.Text = _reply.Text; } finally { _updating = false; }
        _text.IsEnabled = !_reply.Busy; _save.IsEnabled = !_reply.Busy && _reply.Dirty;
        _replyStatus.Text = _reply.Error?.Message ?? (_reply.Text.Length == 0 && _forms.Value("auto-misc-main", "autoReplyEnabled")?.GetValue<bool>() == true ? Localization.Text("回复内容为空", "Reply text is empty") : "");
    }
    private void RenderFriends()
    {
        if (!_attached) return; _friends.Children.Clear();
        bool available = _invitations.Connected && _invitations.InLobby; _search.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        _refresh.IsEnabled = !_invitations.Busy;
        if (_invitations.Error is { } error) _invitationStatus.Text = error.Message;
        else if (!available) _invitationStatus.Text = Localization.Key(_invitations.Connected ? "automation.misc.autoInvitation.notInLobby" : "automation.misc.autoInvitation.unavailable");
        else _invitationStatus.Text = "";
        if (!available) return;
        var friends = AutomationMiscData.Friends(_invitations.Friends, _search.Text);
        if (friends.Length == 0) { _friends.Children.Add(new TextBlock { Text = Localization.Key("automation.misc.autoInvitation.noFriends") }); return; }
        foreach (var friend in friends)
        {
            string puuid = friend.Text("puuid"); var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(8) };
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var portrait = new Grid { Width = 40, Height = 40 }; var image = new Image { Width = 40, Height = 40 };
            int icon = (int)friend.Number("icon", 29); image.Loaded += async (_, _) => await _images.SetAsync(image, $"/lol-game-data/assets/v1/profile-icons/{icon}.jpg"); portrait.Children.Add(image);
            var color = friend.Text("availability") switch { "chat" => Microsoft.UI.Colors.LimeGreen, "dnd" => Microsoft.UI.Colors.Cyan, "away" => Microsoft.UI.Colors.IndianRed, _ => Microsoft.UI.Colors.Gray };
            portrait.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(color), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom }); row.Children.Add(portrait);
            string name = NativeAppearance.Current?.StreamerMode == true ? NativeAppearance.Current.SummonerPlaceholder(puuid, _friends.Children.Count) : friend.Text("gameName") + "#" + friend.Text("gameTag");
            var label = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(label, 1); row.Children.Add(label);
            var button = new Button { Content = Localization.Key(_invitations.Scheduled.Contains(puuid) ? "automation.misc.autoInvitation.cancelSchedule" : "automation.misc.autoInvitation.scheduleInvite"), IsEnabled = _invitations.CanSchedule(puuid), VerticalAlignment = VerticalAlignment.Center };
            button.Click += async (_, _) => await _invitations.ToggleAsync(puuid); Grid.SetColumn(button, 2); row.Children.Add(button); _friends.Children.Add(row);
        }
    }
}

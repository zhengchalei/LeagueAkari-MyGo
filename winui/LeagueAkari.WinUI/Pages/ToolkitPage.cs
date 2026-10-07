using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LeagueAkari.WinUI.Services;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class ToolkitPage : UserControl
{
    private readonly BackendClient _backend;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private bool _loadingSettings;
    private readonly List<Func<Task>> _initializers = new();
    private Action? _refreshPrivateContent;
    public ToolkitPage(BackendClient backend)
    {
        _backend = backend;
        var root = new Grid { RowSpacing = 12, Margin = new Thickness(20) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        root.Children.Add(new TextBlock { Text = "工具集", FontSize = 24 });
        Grid.SetRow(_status, 1); root.Children.Add(_status);
        var tabs = new TabView { IsAddTabButtonVisible = false, CanReorderTabs = false };
        AddTab(tabs, "客户端", Clients());
        AddTab(tabs, "局内发送", SendTools());
        AddTab(tabs, "对局流程", ProcessTools());
        AddTab(tabs, "大厅", LobbyTools());
        AddTab(tabs, "资料与社交", ProfileTools());
        AddTab(tabs, "对局查询", InspectTools());
        AddTab(tabs, "领取奖励", Rewards());
        AddTab(tabs, "好友管理", Friends());
#if DEBUG
        AddTab(tabs, "战利品", Loot());
#endif
        Grid.SetRow(tabs, 2); root.Children.Add(tabs);
        Content = root;
        Loaded += (_, _) => { Localization.Changed += LanguageChanged; if (NativeAppearance.Current is { } appearance) appearance.Changed += AppearanceChanged; LanguageChanged(); };
        Unloaded += (_, _) => { Localization.Changed -= LanguageChanged; if (NativeAppearance.Current is { } appearance) appearance.Changed -= AppearanceChanged; };
        Loaded += async (_, _) => { _loadingSettings = true; try { foreach (var initialize in _initializers) { try { await initialize(); } catch (Exception ex) { _status.Text = ex.Message; } } } finally { _loadingSettings = false; } };
    }
    private void LanguageChanged() => DispatcherQueue.TryEnqueue(() => { _refreshPrivateContent?.Invoke(); NativeFormText.Apply(this); });
    private void AppearanceChanged() => LanguageChanged();
    private static string PrivatePlayer(JsonElement player, int index = 0)
    {
        var appearance = NativeAppearance.Current;
        string tag = player.Text("gameTag", player.Text("tagLine"));
        return appearance?.StreamerMode == true ? appearance.SummonerPlaceholder(player.Text("puuid"), index) : player.Text("gameName", player.Text("name", player.Text("summonerName"))) + (tag.Length > 0 ? " #" + tag : "");
    }
    private static void AddTab(TabView tabs, string title, FrameworkElement page)
    {
        tabs.TabItems.Add(new TabViewItem { Header = title, IsClosable = false, Content = new ScrollViewer { Content = page, HorizontalContentAlignment = HorizontalAlignment.Stretch } });
        if (tabs.SelectedIndex < 0) tabs.SelectedIndex = 0;
    }
    private static StackPanel Panel() => new() { Spacing = 12, Padding = new Thickness(12) };
    private static TextBlock Label(string title) => new() { Text = title, FontSize = 17, TextWrapping = TextWrapping.Wrap };
    private static StackPanel Row(params UIElement[] children) { var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; foreach (var child in children) row.Children.Add(child); return row; }
    private static ComboBox Choose(params string[] choices) { var selector = new ComboBox { MinWidth = 180 }; foreach (var choice in choices) selector.Items.Add(choice); if (selector.Items.Count > 0) selector.SelectedIndex = 0; return selector; }
    private Button Action(string title, Func<Task> action)
    {
        var button = new Button { Content = title };
        button.Click += async (_, _) => { button.IsEnabled = false; try { _status.Text = "处理中…"; await action(); _status.Text = "操作完成"; } catch (Exception ex) { _status.Text = ex.Message; } finally { button.IsEnabled = true; } };
        return button;
    }
    private Task<JsonElement> Lcu(string method, string path, object? body = null) => _backend.CallAsync("winui-backend", "lcuRequest", method, path, body);
    private Task<JsonElement> Set(string ns, string key, object? value) => _loadingSettings ? Task.FromResult(default(JsonElement)) : _backend.CallAsync("setting-factory-main", "set", ns, key, value);
    private static void RestoreShortcut(ComboBox control, string saved) { var item = control.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == saved); if (item is null && saved.Length > 0) { item = new ComboBoxItem { Content = saved, Tag = saved }; control.Items.Add(item); } control.SelectedItem = item ?? control.Items[0]; }
    private async Task<bool> Confirm(string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = message, PrimaryButtonText = "确定", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot };
        return await LeagueAkari.WinUI.Services.NativeDialogs.ShowAsync(dialog) == ContentDialogResult.Primary;
    }
}

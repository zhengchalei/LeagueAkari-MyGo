using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using global::Windows.System;

namespace LeagueAkari.WinUI.Services;

public sealed partial class HostNotifications
{
    private readonly NotificationKeyCombo _akariCombo = new("AKARI", 250), _subscribeCombo = new("SUBSCRIBE", 500), _giftCombo = new("GIVEMEAKARI", 500);
    private readonly FunnyPricingCredit _pricing = new();
    private UIElement? _keyboardRoot;
    private ContentDialog? _declarationDialog, _pricingDialog;
    private bool _declarationBypass, _declarationComboClosed;
    private Action? _renderPricing;
    public event Action<string>? AppTitleChanged;

    private void InitializeEasterEggs()
    {
        Panel.Loaded += (_, _) =>
        {
            if (_keyboardRoot != null || Panel.XamlRoot?.Content is not UIElement root) return;
            _keyboardRoot = root; root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnComboKey), true);
        };
        Panel.Unloaded += (_, _) => DetachKeyboard();
    }
    private void DetachKeyboard()
    {
        _keyboardRoot?.RemoveHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnComboKey)); _keyboardRoot = null;
    }
    private void OnComboKey(object sender, KeyRoutedEventArgs args)
    {
        if (_disposed || !_visible) return;
        int key = (int)args.Key; char value = key is >= 65 and <= 90 ? (char)key : '\0'; long now = Environment.TickCount64;
        // AKARI was bound to body itself, unlike the two pricing sequences.
        bool bodyKey = ReferenceEquals(args.OriginalSource, _keyboardRoot) || ReferenceEquals(args.OriginalSource, _declarationDialog) || Panel.XamlRoot != null && FocusManager.GetFocusedElement(Panel.XamlRoot) == null;
        if (bodyKey && _akariCombo.Push(value, now))
        {
            if (_declarationDialog != null) { _declarationComboClosed = true; _declarationDialog.Hide(); }
            else
            {
                var bar = new InfoBar { Title = "League Akari", IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Success }; Panel.Children.Add(bar);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) }; timer.Tick += (_, _) => { timer.Stop(); Panel.Children.Remove(bar); }; timer.Start();
            }
        }
        if (_subscribeCombo.Push(value, now)) _ = ShowPricingAsync();
        if (_giftCombo.Push(value, now)) { _pricing.Gift(); _renderPricing?.Invoke(); }
    }
    private void AttachDeclarationBypass(ContentDialog dialog)
    {
        if (FindNamed(dialog, "PrimaryButton") is not UIElement button) return;
        int count = 0; long previous = 0;
        button.AddHandler(UIElement.RightTappedEvent, new RightTappedEventHandler((_, args) =>
        {
            long now = Environment.TickCount64; count = now - previous <= 500 ? count + 1 : 1; previous = now; args.Handled = true;
            if (count < 3) return; _declarationBypass = true; dialog.Hide();
        }), true);
    }
    private static DependencyObject? FindNamed(DependencyObject root, string name)
    {
        if (root is FrameworkElement element && element.Name == name) return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (FindNamed(VisualTreeHelper.GetChild(root, i), name) is { } result) return result;
        return null;
    }
    private async Task ShowPricingAsync()
    {
        if (_pricingDialog != null) return;
        _pricing.Open(); var content = new StackPanel { Spacing = 12 }; var credit = new TextBlock(); content.Children.Add(credit);
        var topup = new Button { Content = "充值", HorizontalAlignment = HorizontalAlignment.Left }; content.Children.Add(topup);
        var particle = new TextBlock(); content.Children.Add(particle);
        topup.KeyDown += (_, args) => { if (args.Key == VirtualKey.Enter) args.Handled = true; };
        topup.KeyUp += (_, args) => { if (args.Key == VirtualKey.Enter) args.Handled = true; };
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; content.Children.Add(choices);
        var buyButtons = new List<(Button Button, string Id)>();
        foreach (var (id, title, price, description, extra) in new[] { ("basic", "基础版", 0d, "最实惠的选择！", ""), ("pro", "⭐ Pro 版", 30000d, "进阶用户最爱！", "多花 30 点数"), ("max", "⭐ Max 版", 198000d, "满足一切需求！", "多花 30 点数\n再多花 168 点数") })
        {
            var plan = new StackPanel { Width = 200, Spacing = 8 };
            plan.Children.Add(new TextBlock { Text = title, FontSize = 23 }); plan.Children.Add(new TextBlock { Text = price == 0 ? "免费" : price / 1000 + " 点数 / 月", FontSize = 20 }); plan.Children.Add(new TextBlock { Text = description });
            plan.Children.Add(new TextBlock { Text = "✓ 战绩查询以及跨区查询\n✓ 玩家 ID 查询\n✓ 对局战绩分析\n✓ 观战玩家\n✓ 自动英雄选择或禁用，自动游戏流程（接受对局，自动匹配，自动点赞，自动回到房间）等\n✓ 小工具集合，包括修改生涯背景，伪装段位信息，领取奖励等" + (extra.Length > 0 ? "\n✓ " + extra : ""), TextWrapping = TextWrapping.Wrap });
            var buy = new Button { HorizontalAlignment = HorizontalAlignment.Stretch }; plan.Children.Add(buy); buyButtons.Add((buy, id));
            buy.Click += (_, _) => { if (_pricing.Buy(id, price)) { AppTitleChanged?.Invoke(Localization.Key("appName", "LeagueAkari-MyGo") + " " + title); _renderPricing?.Invoke(); } };
            choices.Children.Add(new Border { Padding = new Thickness(12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = plan });
        }
        _renderPricing = () => { credit.Text = "当前余额：" + (_pricing.Balance / 1000).ToString("0.####") + " 点数"; foreach (var (button, id) in buyButtons) { button.Content = _pricing.Current == id ? "当前的方案" : "订阅"; button.IsEnabled = _pricing.Current != id; } if (_pricing.Balance >= 29950) topup.Margin = new Thickness(Random.Shared.NextDouble() * 300, 0, 0, 0); };
        int clicks = 0; long lastClick = 0; bool nice = false, amazing = false;
        topup.Click += (_, _) =>
        {
            long now = Environment.TickCount64; if (now - lastClick < 500) clicks++; else { clicks = 1; nice = amazing = false; } lastClick = now;
            double amount = _pricing.TopUp(); particle.Text = "+" + (amount / 1000).ToString("0.####");
            if (!amazing && clicks >= 25) { particle.Text = "+非常厉害！"; amazing = true; } else if (!nice && clicks >= 10) { particle.Text = "+很棒很棒！"; nice = true; }
            _renderPricing();
        };
        _renderPricing(); _pricingDialog = new ContentDialog { Title = "选择你的订阅", Content = new ScrollViewer { Content = content, MaxHeight = 530, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto }, CloseButtonText = L("关闭", "Close") };
        _pricingDialog.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnComboKey), true);
        try { await DialogAsync(_pricingDialog, _ => Task.CompletedTask); }
        finally { _pricingDialog = null; _renderPricing = null; }
    }
    private void DisposeNotificationExtras() { DetachKeyboard(); _elevatedTimer.Stop(); foreach (var dialog in _warningDialogs.Values) dialog.Hide(); _badSgpDialog?.Hide(); }
}

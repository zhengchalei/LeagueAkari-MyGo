using System.Text.Json;
using System.Text.Json.Nodes;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class AutomationPage : UserControl
{
    private readonly BackendClient _backend;
    private readonly SettingsForms _forms;
    private readonly TabView _tabs = new() { IsAddTabButtonVisible = false };
    private bool _loaded;
    private readonly List<(string Namespace, HashSet<string> Keys, Action Render)> _structuralEditors = new();
    private static readonly (string Value, string Label)[] Modes = [("ranked", "排位"), ("normal", "匹配"), ("aram", "大乱斗 / 海斗"), ("cherry", "斗魂竞技场"), ("urf", "无限火力"), ("oneforall", "克隆"), ("ultbook", "终极魔典"), ("bot", "人机"), ("custom", "自定义")];
    private static readonly (string Value, string Label)[] Positions = [("default", "默认"), ("top", "上单"), ("jungle", "打野"), ("middle", "中单"), ("bottom", "下路"), ("utility", "辅助")];
    public AutomationPage(BackendClient backend)
    {
        _backend = backend;
        _forms = new(backend);
        _forms.Changed += (ns,key) => { foreach(var editor in _structuralEditors.Where(e=>e.Namespace==ns&&e.Keys.Contains(key))) editor.Render(); LanguageChanged(); };
        var layout = new Grid(); layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.Children.Add(_tabs); Grid.SetRow(_forms.Status, 1); layout.Children.Add(_forms.Status); Content = layout;
        Loaded += (_, _) => { _forms.Observe(); Localization.Changed += LanguageChanged; if (NativeAppearance.Current is { } appearance) appearance.Changed += AppearanceChanged; LanguageChanged(); };
        Unloaded += (_, _) => { _forms.Release(); Localization.Changed -= LanguageChanged; if (NativeAppearance.Current is { } appearance) appearance.Changed -= AppearanceChanged; };
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            try { await _forms.Load("auto-gameflow-main", "auto-select-main", "auto-champ-config-main", "auto-misc-main"); Add("游戏流程", Gameflow()); Add("自动选禁", await SelectEditor()); Add("英雄配置", await ChampionEditor()); var misc = new StackPanel { Spacing = 12 }; misc.Children.Add(new AutomationMiscView(_backend, _forms)); misc.Children.Add(Misc()); Add("其他自动操作", misc); _loaded = true; LanguageChanged(); }
            catch (Exception ex) { _forms.Status.Text = "自动操作加载失败：" + ex.Message; }
        };
    }
    private void LanguageChanged() => DispatcherQueue.TryEnqueue(() => NativeFormText.Apply(this));
    private void AppearanceChanged() => LanguageChanged();
    private void Add(string title, UIElement panel) { _tabs.TabItems.Add(new TabViewItem { Header = title, IsClosable = false, Content = SettingsForms.Scroll(panel) }); if (_tabs.SelectedIndex < 0) _tabs.SelectedIndex = 0; }
    private UIElement Gameflow()
    {
        const string ns = "auto-gameflow-main";
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(SettingsForms.Section("接受匹配", _forms.Toggle(ns, "autoAcceptEnabled", "自动接受"), _forms.Number(ns, "autoAcceptDelaySeconds", "延迟（秒）", 0, 10), _forms.Action("取消本次自动接受", ns, "cancelAutoAccept")));
        panel.Children.Add(SettingsForms.Section("点赞和下一局", _forms.Toggle(ns, "autoHonorEnabled", "自动点赞"), _forms.Choice(ns, "autoHonorStrategy", "点赞对象", ("prefer-lobby-member", "优先组队队友"), ("only-lobby-member", "仅组队队友"), ("all-member", "所有队友"), ("all-member-including-opponent", "队友和对手"), ("opt-out", "放弃点赞")), _forms.Toggle(ns, "playAgainEnabled", "自动再来一局")));
        var rematch = _forms.Choice(ns, "autoMatchmakingRematchStrategy", "重新排队策略", ("never", "不重新排队"), ("fixed-duration", "固定时间"), ("estimated-duration", "预计等待时间"));
        var duration = _forms.Number(ns, "autoMatchmakingRematchFixedDuration", "固定重排时长（秒）", 1, 999, 2);
        duration.IsEnabled = SettingsForms.Selected(rematch) == "fixed-duration";
        rematch.SelectionChanged += (_, _) => { if (SettingsForms.TrySelected(rematch, out string strategy)) duration.IsEnabled = strategy == "fixed-duration"; };
        panel.Children.Add(SettingsForms.Section("自动匹配", _forms.Toggle(ns, "autoMatchmakingEnabled", "启用自动匹配"), _forms.Number(ns, "autoMatchmakingMinimumMembers", "最少队伍人数", 1, 99, 1), _forms.Number(ns, "autoMatchmakingDelaySeconds", "开始匹配延迟（秒）", 0, 999, 5), _forms.Toggle(ns, "autoMatchmakingWaitForInvitees", "等待被邀请的好友", true), rematch, duration, _forms.Number(ns, "autoMatchmakingMaximumMatchDuration", "匹配最大持续时间（秒，0 禁用）"), _forms.Action("取消本次自动匹配", ns, "cancelAutoMatchmaking")));
        panel.Children.Add(SettingsForms.Section("其他流程", _forms.Toggle(ns, "autoReconnectEnabled", "自动重新连接"), _forms.Toggle(ns, "autoSkipLeaderEnabled", "自动跳过队长"), _forms.Toggle(ns, "autoHandleInvitationsEnabled", "自动处理邀请"), _forms.Toggle(ns, "rejectInvitationWhenAway", "离开时拒绝邀请"), Invitations(), _forms.Toggle(ns, "autoSendARAMTeamSideEnabled", "自动发送大乱斗队伍边"), _forms.Toggle(ns, "autoSendARAMTeamSideVisibleToTeam", "队伍边消息对队友可见")));
        return panel;
    }
    private UIElement Invitations()
    {
        var panel = new StackPanel { Spacing = 8 };
        var strategies = (_forms.Value("auto-gameflow-main", "invitationHandlingStrategies") as JsonObject)?.DeepClone().AsObject() ?? new();
        var queues = SettingsForms.Select("添加邀请队列", [("<DEFAULT>", "默认"), ("RANKED_SOLO_5x5", "单双排"), ("RANKED_FLEX_SR", "灵活排位"), ("NORMAL", "匹配"), ("ARAM_UNRANKED_5x5", "大乱斗"), ("KIWI", "海斗"), ("CHERRY", "斗魂"), ("URF", "无限火力"), ("NORMAL_TFT", "云顶匹配"), ("RANKED_TFT", "云顶排位"), ("RANKED_TFT_TURBO", "狂暴模式"), ("RANKED_TFT_DOUBLE_UP", "双人作战")]);
        var rows = new StackPanel { Spacing = 8 };
        void Render()
        {
            rows.Children.Clear();
            foreach (var key in strategies.Select(pair => pair.Key).ToArray())
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var selection = SettingsForms.Select(key, [("accept", "接受"), ("decline", "拒绝"), ("ignore", "忽略")], strategies[key]?.GetValue<string>());
                selection.SelectionChanged += async (_, _) => { if (!SettingsForms.TrySelected(selection, out string value)) return; strategies[key] = value; try { await _forms.Save("auto-gameflow-main", "invitationHandlingStrategies", strategies); } catch { } };
                var remove = new Button { Content = "移除" };
                remove.Click += async (_, _) => { strategies.Remove(key); try { await _forms.Save("auto-gameflow-main", "invitationHandlingStrategies", strategies); Render(); } catch { } };
                row.Children.Add(selection); row.Children.Add(remove); rows.Children.Add(row);
            }
        }
        var add = new Button { Content = "添加队列" };
        add.Click += async (_, _) => { if (!SettingsForms.TrySelected(queues, out string queue)) return; strategies[queue] = "ignore"; try { await _forms.Save("auto-gameflow-main", "invitationHandlingStrategies", strategies); Render(); } catch { } };
        _structuralEditors.Add(("auto-gameflow-main", new HashSet<string> { "invitationHandlingStrategies" }, () => { strategies = (_forms.Value("auto-gameflow-main", "invitationHandlingStrategies") as JsonObject)?.DeepClone().AsObject() ?? new(); Render(); }));
        panel.Children.Add(queues); panel.Children.Add(add); panel.Children.Add(rows); Render(); return panel;
    }
    private Task<UIElement> SelectEditor() => Task.FromResult<UIElement>(new AutomationSelectEditor(_backend, _forms));
    private sealed record CatalogEntry(int ID, string Name, JsonObject Data);
    private static List<CatalogEntry> Catalog(JsonNode? source)
    {
        IEnumerable<JsonNode?> values = source switch { JsonObject obj => obj.Select(x => x.Value), JsonArray arr => arr, _ => [] };
        return values.OfType<JsonObject>().Select(x => new CatalogEntry(x["id"]?.GetValue<int>() ?? 0, x["name"]?.GetValue<string>() ?? "", x)).Where(x => x.ID > 0).OrderBy(x => x.Name).ToList();
    }
    private UIElement Misc()
    {
        const string ns = "auto-misc-main";
        return SettingsForms.Section("其他自动操作", _forms.Toggle(ns, "lockOfflineStatus", "锁定离线状态"), _forms.Toggle(ns, "autoSetStatusMessageEnabled", "自动设置状态消息"), _forms.Text(ns, "statusMessage", "状态消息"), _forms.Toggle(ns, "autoSetRankedStatusEnabled", "自动设置显示段位"), _forms.Choice(ns, "rankedStatus.queue", "队列", ("RANKED_SOLO_5x5", "单双排"), ("RANKED_FLEX_SR", "灵活排位")), _forms.Choice(ns, "rankedStatus.tier", "段位", new[] { "IRON", "BRONZE", "SILVER", "GOLD", "PLATINUM", "EMERALD", "DIAMOND", "MASTER", "GRANDMASTER", "CHALLENGER" }.Select(s => (s, s)).ToArray()), _forms.Choice(ns, "rankedStatus.division", "分段", ("I", "I"), ("II", "II"), ("III", "III"), ("IV", "IV")));
    }
}

using System.Runtime.CompilerServices;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LeagueAkari.WinUI.Pages;

internal static class NativeFormText
{
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<string, string>> Originals = new();
    private static readonly ConditionalWeakTable<TextBlock, DynamicText> TextBindings = new();
    private sealed class DynamicText { public bool Updating; }
    private static readonly Dictionary<string, string> English = """
匹配的 LCU 事件|Matching LCU events
应用|Application
查询|Queries
隐私|Privacy
透明度|Opacity
应用方案|Apply build
更新|Updates
无|None
开|On
关|Off
LCU 连接|LCU connection
对局阶段|Game phase
按组合键（Esc 取消）|Press a shortcut (Esc cancels)
保存并应用签名|Save and apply status message
保存并应用展示段位|Save and apply displayed rank
保存配置|Save configuration
不设置|Unset
不设置形态|No variant
查看指定对局|Inspect a game
查询所选好友的最近对局|Load selected friends' latest games
查询所有好友最近游戏时间|Load every friend's latest game time
持续锁定离线状态|Keep offline status locked
持续锁定签名|Keep status message locked
持续锁定展示段位|Keep displayed rank locked
创建大厅|Create lobby
创建匹配大厅|Create matchmaking lobby
打野分析预设|Jungle analysis preset
当前可加入的模式|Available game modes
地图|Map
地图 ID|Map ID
地图内容 ID|Map content ID
读取当前锁定状态|Load current locks
读取对局详情|Load game details
读取好友添加时间|Load friendship dates
读取可用模式|Load available modes
读取无尽狂潮英雄与地图|Load Swarm champions and maps
读取英雄列表|Load champions
搜索皮肤|Search skins
皮肤|Skin
对局 ID|Game ID
对局结束后|After a game ends
发送到当前大厅／选人／对局|Send to the current lobby, champion select or game
发送间隔（毫秒）|Send interval (ms)
发送内容（每行一条）|Messages (one per line)
高度|Height
个人资料背景|Profile background
固定文本预设|Fixed text presets
恢复可写|Make writable
活动通行证|Event pass
接受对局|Accept match
结束游戏客户端进程|Terminate game client
拒绝对局|Decline match
宽度|Width
离开大厅|Leave lobby
礼包奖励|Gift rewards
聊天段位展示|Displayed chat rank
领取所选活动|Claim selected events
领取所选礼包|Claim selected gifts
领取所选任务|Claim selected missions
录制|Record
名称|Name
皮肤背景形态|Background skin variant
皮肤与炫彩|Skins and chromas
启动器|Launcher
启用终止游戏快捷键|Enable game termination shortcut
签名|Status message
清除模拟，恢复真实对局|Clear simulation and restore the current game
清空|Clear
清空表情配置|Clear emote configuration
取消匹配|Cancel matchmaking
取消选择|Clear selection
全选|Select all
全选活动|Select all events
全选礼包|Select all gifts
全选任务|Select all missions
任务奖励|Mission rewards
删除所选好友|Delete selected friends
上移|Move up
设置资料横幅|Set profile banner
生成预览|Generate preview
刷新安装与客户端进程|Refresh installations and client processes
刷新待选择礼包|Refresh selectable gifts
刷新好友与分组|Refresh friends and groups
刷新可领取活动|Refresh claimable events
刷新可领取任务|Refresh claimable missions
刷新预设|Refresh presets
刷新预设配置与对局玩家|Refresh preset settings and game players
刷新战利品|Refresh loot
搜索好友或分组|Search friends or groups
搜索战利品|Search loot
锁定为只读|Make read-only
停止批量操作|Stop batch operation
停止批量领取|Stop batch claiming
下移|Move down
新建|Create
修复窗口|Repair window
修复客户端窗口尺寸|Repair client window size
移除等级边框|Remove level border
移除挑战勋章|Remove challenge tokens
已保存预设|Preset saved
以此对局模拟查询双方战绩|Use this game to simulate both teams
应用地图|Apply map
应用英雄及难度|Apply champion and difficulty
应用账号难度|Apply account difficulty
应用状态|Apply status
应用资料背景|Apply profile background
英雄 ID|Champion ID
游戏设置文件|Game settings file
预设内容|Preset content
再来一局|Play again
在线状态与签名|Availability and status message
战绩评价预设|Rating preset
重建 WMI 查询|Rebuild WMI query
重新连接对局|Reconnect to game
资料装饰|Profile decorations
组队分析预设|Premade analysis preset
确认|Confirm
检查更新|Check for updates
未设置|Not set
保存失败|Save failed
客户端|Client
局内发送|In-game messages
对局流程|Game flow
大厅|Lobby
资料与社交|Profile and social
对局查询|Game lookup
领取奖励|Claim rewards
好友管理|Friends
战利品|Loot
处理中…|Processing…
启动 TCLS|Launch TCLS
启动 WeGame|Launch WeGame
从 WeGame 启动 LOL|Launch League through WeGame
启动 Riot 客户端|Launch Riot Client
战绩|Match history
对局|Ongoing game
存储|Storage
其他|Other
调试|Debug
关于|About
设置加载失败：|Settings failed to load: 
简体中文|Simplified Chinese
主题|Theme
浅色|Light
樱花|Sakura
奶油|Butter
薄荷|Mint
深色|Dark
石墨|Graphite
赛博|Cyber
极光|Aurora
信息|Information
警告|Warning
错误|Error
删除本机应用及配置，无法撤销。是否继续？|Delete this application and local configuration? This cannot be undone.
单双排|Ranked solo/duo
排序|Sort
组队|Premade team
玩家标签|Player tags
连胜|Winning streak
连败|Losing streak
容易被抓|Easy to gank
亮眼表现|Great performance
倒计时|Countdown
按快捷键（Esc 取消）|Press a shortcut (Esc cancels)
导入将覆盖当前配置。是否继续？|Import will overwrite the current settings. Continue?
导入文件中的玩家标记。是否继续？|Import player tags from this file?
删除玩家记录|Delete player record
删除此玩家记录和标记？|Delete this player record and its tag?
操作完成|Operation completed
主播模式|Streamer mode
克隆|One for All
自定义|Custom game
所有队友|All teammates
斗魂|Arena
移除|Remove
禁用英雄|Ban champion
技能已保存|Spells saved
基石|Keystone
符文已保存|Runes saved
选取率|Pick rate
英雄名称|Champion name
OP.GG 返回格式错误|Unexpected OP.GG response
加载 OP.GG 数据…|Loading OP.GG data…
能量恢复|Energy regeneration
已设置 OP.GG 召唤师技能|Applied OP.GG summoner spells
多窗口|Windows
基本设置|General settings
关闭主窗口|Closing the main window
每次询问|Ask every time
语言 / Language|Language
首选战绩来源|Preferred match history source
SGP（支持大区与队列查询）|SGP (regions and queue filters)
LCU（本地客户端）|LCU (local client)
资料皮肤作为背景|Use profile skin as background
窗口背景材质|Window backdrop
主窗口透明度|Main window opacity
主窗口置顶|Keep main window on top
显示免费软件声明|Show free software declaration
自动下载更新|Automatically download updates
下载并应用更新|Download and apply update
取消下载|Cancel download
打开更新目录|Open update folder
忽略的版本|Ignored version
远程配置源|Remote configuration source
自动获取最新发行版|Fetch the latest release
代理策略|Proxy policy
强制代理|Force proxy
代理主机|Proxy host
代理端口|Proxy port
日志与网络|Logging and network
日志等级|Log level
应用管理|Application management
打开数据目录|Open data folder
卸载应用|Uninstall application
战绩默认设置|Match history defaults
对局结束后刷新战绩|Refresh history after a game ends
战绩优先使用 SGP|Prefer SGP for history
每页战绩数量|Matches per page
默认队列|Default queue
默认时间范围|Default time range
显示训练模式|Show practice games
显示重开等非正常对局|Show remakes and irregular games
启用对局查询|Enable ongoing game queries
进入对局时切换页面|Switch page when a game starts
每位玩家查询数量|Matches loaded per player
查询并发数|Concurrent queries
预加载详细对局数量|Preloaded game details
查询模式|Query mode
当前模式|Current mode
大厅阶段查询|Query during lobby phase
组队推断共同对局阈值|Shared games required for premade inference
玩家卡片|Player cards
英雄使用列表|Champion usage
最近使用|Recent champions
显示战绩边框|Show match borders
显示打野路径|Show jungle paths
所有玩家显示打野路径|Show jungle paths for all players
可疑闪现位置|Unusual Flash position
平均伤害|Average damage
平均承伤|Average damage taken
平均经济|Average gold
补刀每分钟|CS per minute
经济伤害效率|Damage to gold efficiency
问号次数|Missing pings
视野得分|Vision score
击杀伤害效率|Kill damage efficiency
遇见过|Previously met
队伍胜率|Team win rate
对局悬浮窗口|Ongoing game overlay
冷却计时|Cooldown timer
显示 / 隐藏|Show / hide
重置位置|Reset position
选人时自动显示|Automatically show during champion select
显示已有皮肤列表|Show owned skins
显示快捷键|Visibility shortcut
计时方式|Timer mode
正计时|Stopwatch
滚轮调整方向反转|Reverse scroll adjustment
左击开始或清除计时，右键双击将时间发送到游戏，滚轮调整秒数。|Left click starts or clears; double right click sends time in game; scroll adjusts seconds.
清除快捷键|Clear shortcut
设置文件|Settings files
导出设置|Export settings
导入设置|Import settings
标记玩家|Tagged players
导出标记玩家|Export tagged players
导入标记玩家|Import tagged players
搜索标记内容或 PUUID|Search tag or PUUID
仅当前账户|Current account only
主播模式下显示玩家身份|Reveal player identities in streamer mode
每页数量|Rows per page
玩家身份已隐藏|Player identity hidden
保存标记|Save tag
标记已保存|Tag saved
删除记录|Delete record
添加玩家标记|Add player tag
标记者 PUUID（当前账户）|Tag owner PUUID (current account)
被标记玩家 PUUID|Tagged player PUUID
大区 / rsoPlatformId|Region / rsoPlatformId
标记内容|Tag content
PUUID 和 Region 不能为空|PUUID and region are required
计时与隐私|Timers and privacy
复活计时|Respawn timer
使用匿名风格名称|Use styled anonymous names
禁止窗口截屏|Exclude windows from capture
打开日志目录|Open log folder
打开用户目录|Open user folder
使用 WMI 获取客户端|Discover clients with WMI
刷新客户端列表|Refresh client list
重建 WMI|Rebuild WMI
事件与远程配置|Events and remote configuration
开启所有 LCU 事件日志|Log all LCU events
关闭所有 LCU 事件日志|Stop logging all LCU events
显示测试页|Show test page
折叠导航|Collapse navigation
LCU 事件记录规则|LCU event recording rules
端点规则|Endpoint rule
添加规则|Add rule
GitHub 仓库|GitHub repository
大乱斗 / 海斗|ARAM / Mayhem
游戏流程|Game flow
自动选禁|Automatic pick and ban
英雄配置|Champion configuration
其他自动操作|Other automation
接受匹配|Accept match
取消本次自动接受|Cancel this automatic accept
延迟（秒）|Delay (seconds)
点赞和下一局|Honor and next game
点赞对象|Honor recipients
优先组队队友|Prefer premade teammates
仅组队队友|Premade teammates only
队友和对手|Teammates and opponents
放弃点赞|Opt out of honor
自动再来一局|Automatically play again
重新排队策略|Requeue strategy
不重新排队|Never requeue
固定时间|Fixed duration
预计等待时间|Estimated waiting time
固定重排时长（秒）|Requeue after (seconds)
启用自动匹配|Enable automatic matchmaking
最少队伍人数|Minimum party members
开始匹配延迟（秒）|Matchmaking delay (seconds)
等待被邀请的好友|Wait for invited friends
匹配最大持续时间（秒，0 禁用）|Maximum matchmaking time (seconds; 0 disables)
取消本次自动匹配|Cancel this automatic matchmaking
其他流程|Other game flow
自动重新连接|Automatically reconnect
自动跳过队长|Automatically skip leader
自动处理邀请|Automatically handle invitations
离开时拒绝邀请|Reject invitations while away
自动发送大乱斗队伍边|Automatically send ARAM team side
队伍边消息对队友可见|Team side message visible to teammates
添加邀请队列|Add invitation queue
云顶匹配|TFT normal
云顶排位|TFT ranked
狂暴模式|Hyper Roll
双人作战|Double Up
添加队列|Add queue
只展示|Show intent only
展示后锁定|Show then lock in
立即锁定|Lock in immediately
提前展示意图|Show intent early
忽略其他玩家意图|Ignore other players' intents
自动处理英雄交换|Automatically handle champion trades
选择第一个可用英雄|Select first available champion
备选池累积切换延迟（秒）|Bench swap accumulated delay (seconds)
名称或英雄 ID|Name or champion ID
加入优先列表|Add to priority list
临时暂停本次自动选禁|Pause picks and bans for this session
恢复本次自动选禁|Resume picks and bans for this session
自动应用英雄符文与技能|Automatically apply champion runes and spells
配置模式|Configuration mode
排位位置|Ranked position
连接 LOL 客户端后读取英雄、技能和符文目录。|Connect to the League client to load champions, spells, and runes.
召唤师技能 1|Summoner spell 1
召唤师技能 2|Summoner spell 2
保存召唤师技能|Save summoner spells
两个召唤师技能不能相同|Summoner spells must be different
清除技能配置|Clear spell configuration
主系|Primary rune style
副系|Secondary rune style
保存符文|Save runes
请选择完整符文|Select a complete rune page
请选择允许的副系，两个副系符文应来自不同行|Select an allowed secondary style and perks from different rows
清除符文配置|Clear rune configuration
仅离开时回复|Reply only while away
锁定离线状态|Lock offline status
自动设置状态消息|Automatically set status message
自动设置显示段位|Automatically set displayed rank
计划邀请好友|Scheduled friend invitations
搜索好友名称或 #tag|Search friends by name or tag
刷新好友|Refresh friends
邀请计划已更新|Invitation schedule updated
数据地区|Data region
无位置|No position
最新版本|Latest version
取消加载|Cancel loading
已取消加载|Loading canceled
自动应用|Automatically apply
闪现位置|Flash position
保留客户端位置|Keep current client position
自动应用 OP.GG 符文（已有英雄配置优先）|Automatically apply OP.GG runes (champion configuration takes precedence)
自动应用召唤师技能（已有英雄配置优先）|Automatically apply spells (champion configuration takes precedence)
自动应用装备方案|Automatically apply item sets
海斗海克斯强化（OP.GG 社区统计）|Mayhem augments (OP.GG community statistics)
暂无该英雄的海斗独立数据|No independent Mayhem data for this champion
数据源未列出该英雄调整|No adjustments listed for this champion
输出伤害|Damage dealt
受到伤害|Damage taken
对线英雄|Matchups
协同英雄|Synergies
应用符文|Apply runes
技能加点|Skill order
出门装备|Starting items
棱彩装备|Prismatic items
后续装备|Additional items
导入完整装备方案|Import complete item set
已应用到客户端|Applied to client
技能方案不完整|Incomplete spell configuration
客户端没有可替换的符文页|No rune page can be replaced
请在英雄选择中应用配置|Apply configuration during champion select
客户端当前英雄已切换，请重新选择方案|The selected champion changed; choose a new build
""".Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim().Split('|', 2)).ToDictionary(parts => parts[0], parts => parts[1]);

    public static string Text(string value)
    {
        if (!Localization.IsEnglish) return value;
        if (English.TryGetValue(value, out var translated)) return translated;
        foreach (var (prefix, replacement) in new[] { ("设置加载失败：", "Settings failed to load: "), ("标记列表加载失败：", "Tags failed to load: "), ("自动操作加载失败：", "Automation failed to load: "), ("好友加载失败：", "Friends failed to load: "), ("保存失败：", "Save failed: "), ("加载失败：", "Load failed: "), ("OP.GG 查询失败，保留已加载内容：", "OP.GG query failed; keeping loaded content: "), ("英雄数据加载失败：", "Champion data failed to load: "), ("应用失败：", "Apply failed: "), ("海克斯大乱斗专属增减益（普通大乱斗不适用） · RESG ", "Mayhem-only adjustments (not regular ARAM) · RESG "), ("竞技场强化 · 稀有度 ", "Arena augments · Rarity "), ("来源原值 ", "Source value "), ("版本 ", "Version ") })
            if (value.StartsWith(prefix)) return replacement + value[prefix.Length..];
        if (value.StartsWith("主系第") && value.EndsWith("行")) return "Primary row " + value[3..^1];
        if (value.StartsWith("副系符文 ")) return "Secondary rune " + value[5..];
        if (value.StartsWith("属性碎片 ")) return "Stat shard " + value[5..];
        if (value.StartsWith("OP.GG · 版本 ")) return value.Replace("版本 ", "Version ").Replace(" 个英雄", " champions").Replace("统计 ", "Statistics ");
        if (value.StartsWith("胜率 ") || value.StartsWith("T") && value.Contains("    胜率 "))
            return value.Replace("胜率 ", "Win ").Replace("选取率 ", "Pick ").Replace("选取 ", "Pick ").Replace("禁用率 ", "Ban ").Replace("禁用 ", "Ban ").Replace(" 场", " games").Replace("均名次 ", "Avg. placement ").Replace("第一 ", "First ");
        if (value.Contains("    表现 ") && value.Contains("    热度 ")) return value.Replace("表现 ", "Performance ").Replace("热度 ", "Popularity ");
        return Localization.Translate(value);
    }
    public static void Apply(DependencyObject root) => Apply(root, new HashSet<DependencyObject>());
    private static void Apply(DependencyObject root, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(root)) return;
        var originals = Originals.GetOrCreateValue(root);
        string Translate(string property, string value)
        {
            if (!originals.TryGetValue(property, out var original) || value != original && value != originals.GetValueOrDefault(property + "-last")) originals[property] = original = value;
            string translated = Text(original); originals[property + "-last"] = translated; return translated;
        }
        if (root is TextBlock text)
        {
            if (!TextBindings.TryGetValue(text, out var binding))
            {
                binding = new DynamicText(); TextBindings.Add(text, binding);
                text.RegisterPropertyChangedCallback(TextBlock.TextProperty, (sender, _) =>
                {
                    if (binding.Updating) return;
                    binding.Updating = true;
                    try { Apply(sender, new HashSet<DependencyObject>()); }
                    finally { binding.Updating = false; }
                });
            }
            binding.Updating = true;
            try { text.Text = Translate("text", text.Text); }
            finally { binding.Updating = false; }
        }
        if (root is ContentControl content && content.Content is string contentValue) content.Content = Translate("content", contentValue);
        switch (root)
        {
            case ContentDialog c:
                if (c.Title is string dialogTitle) c.Title = Translate("title", dialogTitle);
                c.PrimaryButtonText = Translate("primary", c.PrimaryButtonText);
                c.SecondaryButtonText = Translate("secondary", c.SecondaryButtonText);
                c.CloseButtonText = Translate("close", c.CloseButtonText);
                break;
            case ToggleSwitch c:
                if (c.Header is string toggleHeader) c.Header = Translate("header", toggleHeader);
                if (c.OnContent is string on) c.OnContent = Translate("on", on);
                if (c.OffContent is string off) c.OffContent = Translate("off", off);
                break;
            case TextBox c:
                if (c.Header is string textHeader) c.Header = Translate("header", textHeader);
                c.PlaceholderText = Translate("placeholder", c.PlaceholderText);
                break;
            case NumberBox c when c.Header is string value: c.Header = Translate("header", value); break;
            case ComboBox c when c.Header is string value: c.Header = Translate("header", value); foreach (var item in c.Items.OfType<ComboBoxItem>()) Apply(item, visited); break;
            case TabView c: foreach (var tab in c.TabItems.OfType<TabViewItem>()) { if (tab.Header is string value) tab.Header = Translate("tab" + tab.GetHashCode(), value); if (tab.Content is DependencyObject child) Apply(child, visited); } break;
        }
        if (root is ContentControl { Content: DependencyObject childContent }) Apply(childContent, visited);
        if (root is Panel panel) foreach (var child in panel.Children) Apply(child, visited);
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++) Apply(VisualTreeHelper.GetChild(root, i), visited);
    }
}

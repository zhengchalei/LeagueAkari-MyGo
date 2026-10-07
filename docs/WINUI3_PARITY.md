# WinUI 3 全功能迁移验收矩阵

当前决策（2026-10-07）：以 MyGo 为主线，WinUI 3 作为保留实验方案，暂停全量迁移。下表记录已有实现和未完成验收项，供后续评估，不代表发布或继续迁移的承诺。

目标是当前 LeagueAkari-MyGo 的全部用户功能在 Windows 原生界面中还原；保留 Go 数据与自动化服务，Windows App SDK / WinUI 3 承担所有内部窗口。检查基线为 2026-10-07 工作树，不把既有 MyGo Native Mini 算作 WinUI 3 已完成，不把编译成功算作功能验收。

## 状态和共同验收门槛

- 待实现：尚无对应 WinUI 操作流程。
- 已接线待验：已有控件和调用，尚无匹配范围的运行证据。
- 已验：在真实打包应用中观察到指定行为，注明证据路径和限制。
- Go 已有：下面的接口是迁移复用点，不代表对应原生 UI 已完成。

共同门槛：控件可由键盘访问；中文/英文、浅色/深色、DPI 100%/150%、最小窗口与最大化无裁切；按钮忙碌与失败状态可见；网络失败保留可用内容；设置即时持久化并重启还原；更新事件在 DispatcherQueue 应用；窗口关闭释放订阅；内部 UI 不得创建 WebView，也不要求安装 WebView2 Runtime；Windows App SDK 的传递组件原 DLL 可以保留并在随包说明中列明。文件对话框应由 WinUI 绑定 HWND，不能继续借用 MyGo WebView 窗口。游戏写操作使用模拟 LCU 契约回归加用户授权的真实客户端验证，不能为了 UI 验收修改正在运行的真实对局。

## 窗口、导航和应用生命周期

| ID | 功能与验收 | 现有源 | Go 复用点 | WinUI 状态 |
|---|---|---|---|---|
| W01 | 主窗口启动默认我的战绩；进入对局切换一次；用户能回去且不被每次更新夺回导航 | `desktop/src/renderer/src-main-window/shards/main-window-ui/auto-route-watcher.ts` | `league-client-main` gameflow/session 状态 | 19项导航业务fixture及模拟原生GUI通过：大厅→选人自动切页、手动回战绩保持、结束刷新且不重复；证据 `winui/evidence/lifecycle-parity/acceptance.md`。真实客户端/跨区与非首页条件待验 |
| W02 | 自定义标题栏任意页面拖动、最小化/最大化/还原/关闭；最大化边界无重影 | `components/titlebar`、`mygo/windows.go` | WinUI AppWindow；不依赖 MyGo windowCall | 打包应用真实战绩页拖动、边框缩放、关闭询问与退出通过；模拟应用最大化及深浅主题切换通过，证据 `winui/evidence/theme-parity/acceptance.md`。其他页面/窗口及最小化/还原、多DPI重影仍待验 |
| W03 | 所有窗口位置/尺寸恢复；离屏修复、重置位置、置顶、透明度 | `mygo/windows.go`、`mygo/native_mini_runtime.go` | `window-manager-main/*` settings、trackedBounds | 主窗口实际保存1100×740、位置(138,138)，正常退出重启后捕获边界1086×733、原点(145,138)与退出前完全一致；系统非客户区导致尺寸差异。其他窗口、离屏/重置/置顶/透明度待验 |
| W04 | 关闭动作询问/退出/最小化托盘；单实例；托盘显示各窗口、退出、启动客户端 | `mygo/tray.go`、`mygo/instance_windows.go` | platform、store；宿主需要 Win32 托盘实现 | 6项真实Windows双进程启动/隐藏/最小化恢复及锁释放检查通过；原生fixture关闭默认托盘、记住后直接隐藏、重复启动只恢复原宿主和单后端通过。证据 `winui/evidence/connection-shell-parity/acceptance.md`。完整托盘菜单/重启持久化及多DPI仍待验 |
| W05 | Mini WinUI 英雄图像、三选一、备选池、点击确认/切换、增减益、皮肤/炫彩、随机与抢回，显示自动交易计划 | `mygo/native_mini/*`、`src-aux-window/*` | LCU http-request、catalog、automation、champion | 原生模拟三选一确认/切换、英雄皮肤共存、增减益分组、换肤失败保留/重试、炫彩、24项滚动、英文深色与0次随机禁用通过；15项新Mini契约及Go模型/写前校验回归通过。证据 `winui/evidence/mini-parity/acceptance.md`；真实资产/LCU写入、备用池抢回竞态、DPI/键盘仍待验。原版无手动随机皮肤或交易按钮 |
| W06 | OP.GG 原生数据窗口：地区/模式/位置/英雄选择，胜率/对线数据与符文装备信息，外部原链接可访问；不能用外部浏览器代替内部数据功能 | `desktop/src/renderer/src-opgg-window` | `mygo/protocol.go` public proxy、OP.GG state | 源码补齐且模拟原生GUI部分验收通过：`Windows/OpggWindow.cs`、`Windows/OpggWidgets.cs`、`Services/OpggData.cs`；查询原子提交、榜单双向排序/位置过滤/关键词拼音检索、英雄/榜单Tabs、分组强化、折叠详情、装备blocks与自动跟随优先级 |
| W07 | 对局独立悬浮窗口和主页面共享数据、按住快捷键/松开隐藏、游戏前台门控、鼠标穿透；隐藏后关闭预览 | `src-ongoing-game-window/App.vue`、`main/shards/window-manager/ongoing-game-window/window.ts` | ongoing-game；platform stateful shortcut/foreground；native preview visibility gate | `OngoingWindow.cs` 已补原1300×840无边框固定置顶/no-activate/toolwindow、显示可交互/隐藏穿透、隐藏关闭现有详情及停止刷新；OngoingPage owner 补 canPreview 请求/队列/显隐版本门控。真实游戏前台按住/松开快捷键、鼠标与详情生命周期仍待统一实机验收 |
| W08 | CD敌方召唤师技能/5组自定义秒表，倒计时/正计时，左击开始/清除、双右键发送、滚轮/反向修正、权限反馈、鼠标动态穿透、快捷键 | `src-cd-timer-window/components/SummonerSpellsCdTimer.vue`、`TimerItem.vue`、`main/shards/window-manager/cd-timer-window/windows.ts` | cd-timer gameTime/supportedGameModes；native Input；WinUI计时/Win32 | `CDTimerWindow.cs / CDTimerRoster.cs` 已补缺当前玩家时回退5秒表、敌方与位置排序、旧数据冷却刷新、隐藏异步结果版本门控、timerType/reverse即时事件同步、白字遮罩、原发送权限/中英前台错误、固定置顶/toolwindow/自标题与动态穿透。21项独立CD数据/交互公式夹具通过；模拟GUI左击开始/清除、滚轮校正、主设置即时切换秒表、显示/隐藏、原生Caption拖动及正确位移记录通过，证据 `winui/evidence/cd-parity/acceptance.md`。真实游戏发送/hover穿透/快捷键仍待实机验收 |
| W09 | 连接状态、已启动客户端列表、手动连接/断开、国服自动发现、多客户端选择、错误和重连 | `components/sidebar/ClientConnection.vue`、`shards/league-client-peek/peek-task-controller.ts`、`mygo/internal/client` | league-client connect/disconnect/peekClient、platform ResolveAuth、真实UX/playAgain POST | 25项连接业务契约通过；原生fixture账号头像/其他PID排除、手选失败保留原连接、重试切PID/断开、同目标取消、中文英文错误保留与关闭及结算/重启UX请求通过。Go真实本地HTTP/WebSocket回归验证取消/换PID旧响应与事件隔离、唯一客户端发现。证据 `winui/evidence/connection-shell-parity/acceptance.md`；真实多客户端、客户端退出、全UX菜单与DPI仍待验 |
| W10 | TCLS/WeGame/LOL 默认 UAC 启动，取消 UAC 显示可理解结果，助手无需提权 | `mygo/internal/platform/installation.go` | client-installation-main launch* | 已接线待验：`Pages/ToolkitPage.cs + Go UAC` |
| W11 | 战绩玩家 Tab 打开/去重/关闭/相邻选中；导航折叠状态保存；搜索历史 | `shards/player-tabs`、main-window-ui | setting-factory；WinUI view-model | 15项标签相邻选中/菜单边界/真实搜索历史持久化契约通过；原生fixture精确搜索新标签、最后标签关闭右侧禁用、全标签关闭与重新打开本人头像、外部sidebarCollapsed事件且不重复回写通过，证据 `winui/evidence/notifications-parity/acceptance.md`。中键/双击、历史固定/删除GUI与语言/DPI仍待验 |
| W12 | 游戏复活计时、通知、匹配接受倒计时/取消、更新通知、公告与声明 | `shards/simple-notifications`、respawn-timer | automation state、respawn-timer、update | 48项通知/公告已读/文本格式/文档解析与彩蛋业务契约通过；原生fixture自动接受/匹配/更新取消、复活计时事件、3661秒排队格式和结束隐藏、更新弹层82%忙碌禁用、medium公告打开即已读且关闭不重复写、声明倒计时/Escape保留及明确退出通过，证据 `winui/evidence/notifications-parity/acceptance.md`。补独立系统弹窗队列、主播提示三天记忆、SGP真实统计和更新失败事件。真实LCU时序、全部警告/彩蛋与复杂HTML/CSS仍待验；Go独立数据库不生成原版高版本DB诊断 |

## 战绩、搜索和玩家详情

| ID | 功能与验收 | 现有源（desktop/src/renderer 下） | Go 复用点 | WinUI 状态 |
|---|---|---|---|---|
| P01 | 名字#tag / PUUID / 当前账户搜索，明确大区，历史与好友快速选择，搜索错误 | `src-main-window/components` player-search、`views/player-tabs` | league-client HTTP、SGP proxy | `GlobalPlayerSearch / HistoryTabsPage` 已补原精确/模糊/PUUID分类、跨区条件、结果选择及历史好友；27业务fixture通过，模拟当前区精确搜索→新标签GUI通过；真实LCU/SGP错误与跨区待验 |
| P02 | 头像、名字/tag、等级、名字复制、皮肤背景、刷新（按原 Header 实际行为） | `player-tabs/components/player-tab/widgets/PlayerTabHeader.vue` | summoner/profile LCU/SGP | 打包应用读取真实国服当前玩家头像、名字/tag、等级并显示通过；模拟数据也通过。复制、皮肤背景与刷新交互仍待验 |
| P03 | 单双/灵排段位、LP、胜负/胜率、最高段位、无排位状态 | `RankedPane.vue`、`data/ranked-stats.ts` | player-data LCU/SGP | 打包应用真实当前玩家单双/灵排图像、段位、LP、胜负/胜率与最高段位显示通过；模拟数据通过。无排位、跨区/LCU来源及完整DPI布局待验 |
| P04 | 战绩队列选择、每页数量、前后页、页码、加载/错误/空白；大区来源显式选择 | `widgets/match-history-pagination`、`data/source-selection.ts` | SGP / LCU history proxy | 打包应用真实国服SGP第1→第2→第1页通过，第1页上一页禁用、来源/大区显示一致。队列/每页量/跳转、网络错误/空白、LCU与跨区仍待验 |
| P05 | 普通过滤：队列、时间范围、训练、重开/特殊局、清除筛选 | `widgets/match-history-filters/SimpleMatchHistoryFilter.vue` | Go 保持数据源；UI 本地过滤 | 已接线待验：`HistoryPage.cs / HistoryFilterEditor.cs` |
| P06 | 高级组合过滤与收藏模式：AND/OR/NOT、any/every、胜负/英雄/位置/符文/技能/装备/海克斯、玩家/成员/数值/对局类型；扫描进度/停止、模板示例 | `widgets/match-history-filters`、`CollectModePagination.vue` | history proxy；WinUI 组合器需同一 DSL | `HistoryFilter / HistoryFilterEditor / HistoryLoadController`：108筛选及42可控异步契约通过；原生GUI验证三模板、嵌套条件和比例口径。收集全流程GUI、远程搜索与全部布局仍待验 |
| P07 | 总览 AkariScore/KDA/参团/伤害/承伤/经济/补刀/活跃时间/胜负/阵营/英雄分布 | `SummaryPane.vue` | shared data-adapter/statistics算法；不能以单一 KDA 代替 | `HistorySummaryData / HistoryAnalysis` 补原总览分行/胜率/评分8项明细、活跃时段与连胜败条件、英雄角标/排序及完整统计浮层；32项总览与37项打野业务fixture通过，含6份原始LCU快照与原TypeScript逐字段对照；模拟GUI总览/英雄名/完整统计浮层及历史英雄打野8场timeline加载通过。新原生首清/抓人地图、取消/重试和真实跨区网络尚需继续验收 |
| P08 | 英雄点数等级/分数排序、更多、图像；挑战成就 | `ChampionMasteryPane.vue`、`ChampionMasteryModal.vue`、`PlayerChallenges.vue` | LCU/SGP mastery、challenges | 已接线待验：`HistoryPage.cs` |
| P09 | 最近同局玩家、遇见次数/对局、双方身份、来源大区 | `RecentlyPlayers.vue`、`EncounteredGames.vue` | saved-player-main queryEncounteredGames | 已接线待验：`HistoryPage.cs` |
| P10 | 个人标签显示、添加/编辑/删除；账户+被标记者+大区隔离 | `NormalTagBlock.vue`、`data/tags.ts` | saved-player-main updatePlayerTag/getPlayerTags | 已接线待验：`HistoryPage.cs / SettingsPage.cs` |
| P11 | 战绩行胜败/重开色、英雄、技能/符文/装备、KDA、伤害占比、时长时间地图、十人阵容、海克斯/多杀/拆塔/标签 | `../../renderer-shared/components/match-card/MatchCardOverview.vue` | LCU/SGP summary adapter | 已接线待验：`HistoryPage.cs / MatchDetailsView.cs` |
| P12 | 战绩展开概览：十人表、队伍目标、伤害占比悬浮分解、选择玩家跳转 | `match-card/MatchCardDetails.vue`、`widgets/TeamTable.vue` | detail LCU/SGP | `MatchTeamTable.cs / MatchTeamData.cs`补原队名/排名/投降、固定装备槽/职业任务装、五六强化槽、对英雄及承伤条、分钟统计；18新增数据契约通过。模拟原生GUI验证KIWI团队行、42.55%全场最高伤害占比弹层与物魔真分解、点击队友打开战绩Tab，证据 `winui/evidence/history-details-parity/acceptance.md`。新增七指标雷达弹层、详情/时间线失败保留与重试、中键后台Tab原生fixture点测通过；竞技场/禁用列表、hover穿行及完整DPI键盘主题仍待验 |
| P13 | 详情指标表完整分组、概览、雷达图、柱状图及数值提示 | `tabs/MatchCardDetailsTab.vue`、`utils/details-table` | 原始参与者所有统计 | 已接线待验：`MatchStatsMetadata.cs / MatchCharts.cs / MatchDetailsView.cs` |
| P14 | 符文详情、购买/出售装备构筑时间、地图击杀事件与被害伤害分解 | `MatchCardRunesTab.vue`、`MatchCardBuildsTab.vue`、`MatchCardEventsTab.vue`、`VictimDamageDetails.vue` | ongoing-game loadGameDetails / history timeline | 已接线待验：`MatchDetailsView.cs` |
| P15 | 时间轴曲线、双方经济/经验差、个人指标、选取时间点 | `tabs/timeline` | timeline frames/events | 已接线待验：`MatchCharts.cs / MatchDetailsView.cs` |
| P16 | 跨区仅在 endpoint 支持且 config/token ready 时启用；隐私/不可查询/断网不能伪装无战绩 | `data/source-selection.ts`、SGP技能 | mygo/internal/sgp | `PlayerDataSource / SourceAvailability`：26协议检查覆盖最新token/config门控、跨区只用SGP、来源失败保留原因、LCU完成摘要；真实跨区/网络失效GUI仍待验 |

## 对局与选人

| ID | 功能与验收 | 现有源 | Go 复用点 | WinUI 状态 |
|---|---|---|---|---|
| G01 | 真实红蓝名单优先，三选一/备选池/进入游戏过渡不重复，旁观者和不可见对手正确 | `mygo/internal/game/roster.go`、`players.go` | ongoing-game-main getAll、gameflow | 已接线待验：`Pages/OngoingPage.cs（复用 Go roster）` |
| G02 | 团队胜率/KDA/组队标签；排序与响应式排布；每个玩家身份/段位/战绩/标签/头像 | `ongoing-game-panel/OngoingGamePanel.vue`、widgets | ongoing-game getAll、state | 已接线待验：`OngoingPage.cs` |
| G03 | 玩家最近英雄/点数切换，胜败缩略战绩列表、详情点击、隐私头像回退 | `PlayerInfoCardChampionUsage.vue`、`PlayerInfoCardMatchHistory.vue` | game data/player-data | 已接线待验：`OngoingPage.cs（英雄点数切换待核对）` |
| G04 | 卡片全部21类标签、原注册顺序/设置开关/阈值/精度/深浅主题颜色；标签hover详情、标记click编辑、遇到过详情与预览 | `player-card-tags/tags/{index,basic,great-performance,suspicious-flash-position,akari-score,tagged,met}.tsx`、`akari-score/score-breakdown.ts` | `ongoing-game-main` settings.playerCardTags；`in-game-send-main.generatePlayerAnalysis` actual analysis（含root补齐的CS团队份额）；`saved-player-main`/`reloadPlayer`/真实matchHistory与cached gameDetails | 源码及纯业务验证通过：`OngoingCardTags.cs`、`OngoingCards.cs`、`OngoingSavedInfo.cs`；73项OngoingCardTests +25项EncounterTests（阈值、顺序、取值、空值、精度、保存记录缓存缺失仍保留）。已补原生8项评分进度、闪现D/F饼图比例、补刀团队份额、效率定义、met六列表/位置SVG/abort-remake/名次/相对时间/40条与隐私编号。统一编译及模拟闪现D/F饼图、悬停配色点测通过（winui/evidence/summary-tags-parity/acceptance.md）；GUI gate仍需测试其他hover穿行、点标记直接编辑保存刷新、遇到过预览、主题/语言/隐私及真实客户端分析更新，业务测试不代替GUI验收。 |
| G05 | 玩家右键菜单刷新、标签、复制、战绩 Tab、观战/通知等现有动作；整队刷新与单人刷新 | `player-actions.ts`、header | reloadPlayer、saved-player、LCU | 已接线待验：`OngoingPage.cs` |
| G06 | 打野路径首轮清野/首次抓人/地图/野区偏好/龙控制，开关及非打野显示范围 | `PlayerInfoCardJunglePathing.vue`、jungle-pathing-info | Go timeline/jungle分析 | 已接线待验：`JunglePathingView.cs`、真实 timeline 查询 |
| G07 | 战绩模式/时间查询、实时进度/错误重试、演示对局 dry-run 与结束草稿 | `views/ongoing-game/OngoingGame.vue` | setMatchHistoryTagParams、setDraft/clearDraft | 已接线待验：`OngoingPage.cs / ToolkitInspect.cs` |
| G08 | 英雄与皮肤同时显示、实际已解锁皮肤/炫彩、客户端确认选中、快速切英雄旧响应不覆盖 | `ChampionSelectionPanel.vue`、`SkinSelectionMini.vue` | LCU champions/skins/actions | 已接线待验：`Windows/MiniWindow.cs` |
| G09 | ARAM与KIWI交互一致，RESG独立增减益、准确符号/原值/来源版本/缓存状态/无数据提示 | `useFandomBalanceData.ts`、`mygo/internal/catalog/kiwi.go` | extra-assets-main kiwi/fandom、OPGG | 已接线待验：`Windows/MiniWindow.cs` |
| G10 | 对线优势英雄列表，OP.GG 数据模式/版本，能选择或外部查看；不向产品增加未请求装备推荐 | `ChampionSelectionPanel.vue` | existing public proxy | 已接线待验：`Windows/OpggWindow.cs` |

## 自动操作

| ID | 功能与验收 | Vue 源（views/automation） | Go 复用点 | WinUI 状态 |
|---|---|---|---|---|
| A01 | 自动接受开关、0–10 秒延时、倒计时取消 | `AutoGameflow.vue` | auto-gameflow settings、cancelAutoAccept | 已接线待验：`Windows/MiniWindow.cs / AutomationPage.cs` |
| A02 | 自动点赞5种策略：优先组队/仅组队/队友/队友与对手/放弃；再来一局与实际点赞状态 | `AutoGameflow.vue` | automation/honor.go | 已接线待验：`AutomationPage.cs` |
| A03 | 自动匹配人数/延迟/等待邀请/重排策略和固定时间；取消；好友邀请选择 | `AutoGameflow.vue` | cancelAutoMatchmaking/setFriendsToBeInvited | 已接线待验：`AutomationPage.cs / MiniWindow.cs` |
| A04 | 重新连接、跳过队长、队列邀请接受/拒绝/忽略编辑及离开时拒绝 | `AutoGameflow.vue` | automation/remaining_gameflow.go、lobby.go | 已接线待验：`AutomationPage.cs` |
| A05 | ARAM队伍边自动发消息与可见范围 | `AutoGameflow.vue` | autoSendARAMTeamSideEnabled/VisibleToTeam | 已接线待验：`AutomationPage.cs` |
| A06 | 后端分组/各模式实际位置的有序选英雄列表，启用/延迟/忽略意图/展示意图/只展示或锁定/交易/抢第一个可用/备选池累积延迟 | `AutoSelect.vue`、`AutoSelectEditor.vue`、ordered-champion-list | auto-select setPickConfig | 模拟原生GUI验证模式/操作文案、中路选用103、其他模式bravery-3、去重与单项箭头禁用；后端配置写入匹配原形状。完整多项排序、真实自动锁定待验 |
| A07 | 后端分组/各模式实际位置的有序禁用列表、启用/延迟/策略；临时暂停只影响当前选人 | 同上 | setBanConfig/setTemporarilyDisabled | 模拟禁用dummy-1及暂停/恢复GUI通过，实际事件显示/关闭原生提示；真实禁用流程待验 |
| A08 | 英雄搜索图像/位置筛选；有序列表上下移动/删除/去重，编辑目标模式准确 | `OrderedChampionList.vue`、`string-match.ts` | local champion catalog/LCU；pinyin-pro 默认分词 | 搜索业务已验：`AutoSelectData / NativePinyin` 33,824转写及165匹配oracle通过；模拟ahri与dmxyzl搜索、去重GUI通过（图片为fixture图标）；真实图像/筛选与多项排序待验 |
| A09 | 英雄与模式符文编辑：主副系/基石/小符文/属性；自动创建/应用客户端符文 | `AutoChampConfig.vue`、`RuneV2Edit.vue` | auto-champ-config updateRunes | `AutomationChampionEditor / ChampionConfigDraft / ChampionConfigEditorData` 已补六模式、排位位置、九槽符文、副系替换顺序、清除/还原、忙态与失败草稿；24项英雄配置契约及全包编译通过。原生图片、完整编辑写入、DPI与真实客户端仍待验 |
| A10 | 英雄与模式双召唤师技能，禁止同一技能，启用与存储后自动应用 | `SummonerSpellEdit.vue`、`ChampionConfig.vue` | updateSummonerSpells | 共用英雄配置原生编辑器，已补模式技能修正、不同双技能、清除/还原与单次保存；24项英雄配置契约及全包编译通过。真实技能应用与完整原生交互仍待验 |
| A11 | 自动回复/离开时回复/文字、锁离线、自动状态、自动伪段位队列/段位/分段 | `AutoMisc.vue` | auto-misc-main、misc runner | `AutomationMiscView / AutomationMiscData` 21项契约通过；隔离原生点测验证自动回复单次提交、失败保留/显式重试、好友排序/搜索/预约/取消/离开房间隐藏。Go恢复两秒资料稳定事件计时及断开/切PID隔离；真实聊天、状态与段位写入仍待验 |

## 工具集

| ID | 功能与验收 | Vue 源（views/toolkit） | Go 复用点 | WinUI 状态 |
|---|---|---|---|---|
| T01 | 游戏终止快捷键启用/录制；配置文件只读/可写状态与设置；客户端窗口大小与修复确认 | `client/Client.vue` | platform game/client/native calls | `ToolkitClient.cs / ToolkitClientData.cs` 已核对写后重读原已存在；新增当前连接/能力与终止快捷键外部设置实时反映、文件操作忙态互斥及断线/未知状态不可用、按原要求分别提示提权/平台不支持、宽高min1和Enter焦点/确认流程。15项Client/Lobby纯业务夹具通过；真实文件只读切换/窗口调整/shortcut仍待实机验证。原界面无窥视工具，不列迁移范围 |
| T02 | 一键发送评价/打野/组队/固定文本：选择己方/敌方/所有/具体玩家组队，生成预览、名称策略、显示项、间隔、取消和快捷键 | `in-game-send/*` | in-game-send-main send*、selection、options | `ToolkitPresets / ToolkitPresetData` 已补authoritative选择保持、按队伍选择、三个目标发送/预览/快捷键、原选项与名称策略、阶段/native gate；62项契约及全包编译通过。Go恢复原默认目标变化规则和写入过滤，4组新回归含跨PID/对局晚响应。原生GUI与真实发送仍待验 |
| T03 | 固定文本 CRUD 排序、发送选项/目标快捷键；发送预览与真正发送一致 | `FixedTextPresetPane.vue` | create/update/delete/moveFixedTextPresetItem | `ToolkitSend / FixedTextPresetController` 已补草稿保存、失败保留、创建/排序/删除邻项、快捷键、实时发送条件和外部事件；与好友工具合计57项契约及全包编译通过。原生GUI及真实发送仍待验 |
| T04 | 当前流程 readycheck接受/拒绝、匹配开始/取消、英雄选择退出/秒退/重连/再玩；禁用条件正确 | `in-process/GameflowInProcess.vue` | LCU gameflow/lobby/dodge | `ToolkitProcess.cs / ToolkitProcessData.cs` 已按阶段限制11类操作；44项命令/确认/旧响应契约通过，模拟原生ReadyCheck接受与EndOfGame返回房间通过。证据 `winui/evidence/settings-toolkit-parity/acceptance.md`；真实流程写入、其他按钮与DPI仍待验 |
| T05 | 任意队列 ID 或列表创建大厅；无尽狂潮英雄、地图与账号难度 | `lobby/LobbyTool.vue`、`StrawberryTool.vue` | LCU lobby eligibility/lobby/player-slots/strawberryMapId/loadouts | `ToolkitLobby.cs / ToolkitLobbyData.cs` 已核对任意ID及写前STRAWBERRY guard原已存在；补列表打开即查询双方eligibility且刷新不覆盖手输ID、断线禁创建、非狂潮大厅隐藏全部狂潮表单、3000-3999专属英雄组/头像与其他组、map/difficulty展开首次读真实hub/accountloadout、未选map/difficulty时hero写默认1、写入busy互斥及过期结果防护。15项Client/Lobby业务夹具通过；真实大厅写入待验。原 LobbyTool 无邀请好友/自定义大厅表单，不列此项迁移范围 |
| T06 | 七种聊天状态、锁离线、签名、伪段位；资料皮肤与任务形态背景、横幅、等级边框、挑战勋章、表情清空；指定对局详细预览与模拟 | `misc/*.vue` | auto-misc + LCU profile/chat/loadouts + 原 match preview | 背景流程已补 `ToolkitBackground.cs` / `ProfileBackgroundData.cs`：任务皮肤 tiers 去重、overlay 预览、形态 unset 空字符串写、异步英雄数据过期防护；9 项业务夹具通过，真实客户端写入待验。聊天已补 online 第七状态与原 locale 标签、仅 chat/away 时取消现有离线锁、外部锁定和当前聊天状态更新同步及 chat.me 可用性门控。GameView 完整预览由对应 owner 补；其真实详情与背景/聊天写入仍须实机验证。原没有资料头像/头衔更换或对局视野配置，此行旧描述已纠正 |
| T07 | 活动任务/奖励/EventHub清单、单个领取/全部领取、领取结果/刷新；API不可用时明确错误 | `claim-tools/*.vue` | LCU missions/rewards/event endpoints | `ToolkitClaimTools.cs / RewardData.cs` 恢复三类原字段与随机选择请求；44项契约/异步跨客户端归属检查通过，模拟原生礼包失败保留/重试、任务刷新保留/双领取、活动普通+额外奖励领取后刷新通过。真实账号领取/取消批次/推送事件与DPI待验 |
| T08 | 好友列表分组、筛选、最近在线时间、复选批量删除、取消、刷新及确认 | `friend-tools/FriendTools.vue` | LCU friends DELETE/GET | `ToolkitProfile / FriendToolsData` 已补分组多选、Riot ID筛选、添加日期和最近对局、好友战绩导航、确认删除/取消/剩余重试与连接请求归属；与固定文本合计57项契约及全包编译通过。原生GUI与真实好友操作仍待验 |
| T09 | 战利品开发工具仅DEV路径；不在生产默认导航增加调试入口 | `loot-tools/LootTools.vue` | LCU loot endpoints | 已接线待验：`ToolkitRewards.cs（仅 DEBUG 编译）` |

## 设置、存储、更新和调试

| ID | 功能与验收 | Vue 源（components/settings-modal） | Go 复用点 | WinUI 状态 |
|---|---|---|---|---|
| S01 | 应用设置：语言/主题完整列表/背景皮肤/Mica/窗口关闭动作/opacity/置顶/声明等；依赖项禁用与保存 | `AppSettings.vue` | setting-factory-main、window manager | 背景互斥选择与逐步保存、原应用LCU连接设置已补齐；26项应用设置契约与部分模拟原生点测通过，证据 `winui/evidence/settings-toolkit-parity/acceptance.md`。完整主题/关闭/透明度/重启与DPI仍待验 |
| S02 | 更新检查/版本/忽略/自动下载/进度/取消/打开更新目录/应用重启；运行包完整替换 | `AppSettings.vue`、UpdateModal | self-update-main、remote-config | 原检查结果/忙碌/进度/取消/目录条件已接线；模拟原生检查新版本与82%禁重复下载通过，隔离目录更新回归已有；完整生产包更新生命周期仍待验 |
| S03 | 首选数据源、远程配置源、日志等级、HTTP代理策略/主机/端口、启动配置/卸载 | `AppSettings.vue` | app-common/remote/logger/platform | 已接线待验：`SettingsPage.cs` |
| S04 | 战绩默认页数、刷新、来源及筛选，和具体查询流程一致 | `MatchHistorySettings.vue`、player-tabs store | setting-factory player-tabs-renderer | 已接线待验：`SettingsPage.cs` |
| S05 | 对局查询并发/数量/详情预取/历史模式/大厅阶段/组队阈值/排序/英雄显示/路径/全部标签配置 | `OngoingGameSettings.vue` | ongoing-game-main settings | 数量200/并发不限16、50/4/20默认与显式旧值、降历史数持久化详情数已恢复；实际HTTP测试24并发DETAILS/LCU完整summary/同game去重/切客户端隔离通过。原生LCU禁用查询模式及详情动态上限模拟通过；预取已恢复原300ms reaction：跨窗口并发限制/共享game去重/迟到历史/客户端隔离及单人刷新回归通过，其他字段/布局/真实查询待验 |
| S06 | 多窗口 Mini/OPGG/悬浮/CD 启用/自动显示/透明度/快捷键/重置/皮肤，CD行为配置 | `MultiWindowSettings.vue` | each window settings+WinUI宿主 | 已接线待验：`SettingsPage.cs / UtilityWindow.cs` |
| S07 | 标记玩家表：标记者/被标记者头像昵称与战绩跳转、大区/内容、当前账户过滤、刷新/编辑/清除标记、分页、文件导入导出；全表主播隐私 | `storage-settings/TaggedPlayers.vue` | saved-player-main；LCU/SGP summoner + Riot namesets | `SettingsStorage.cs` 已修删除仅 updatePlayerTag(tag:null)、空编辑转null而保留遇见记录；跨区真实身份查询、PlayerOpened 跳转事件、分页边界、导入后重读及全表遮罩。13 项存储/调试业务夹具通过；真实文件选择/账户数据流程待验。原表无时间列，旧矩阵已纠正 |
| S08 | 设置JSON文件导入导出、确认覆盖、非法文件/版本错误、导入后当前控件与运行设置更新 | `storage-settings/SavedSettings.vue` | settings store import/export；WinUI文件选择 | `SettingsStorage.cs / SettingsForms.Reload` 已修取消不报成功、成功反馈文件路径、导入完成后全部设置缓存与绑定重读；Go owner 已使成功导入返回path。真实 HWND 文件选择及导入仍待验；保留错误与原有数据由原后端解析/事务测试负责 |
| S09 | 复活计时、主播模式/匿名风格、阻止截图；隐藏姓名贯穿搜索/卡片/战绩/标签 | `MiscSettings.vue` | respawn-timer/app-common/window-manager | 已接线待验：`SettingsPage.cs（全链路隐私待核对）` |
| S10 | 调试：日志/用户目录、LCU五项连接字段复制、事件规则添加/启停/删除及端点模糊补全、全部事件写日志、阶段译名、管理员状态、实际运行信息、测试页开关 | `DebugSettings.vue`、`lcu-endpoints.ts`、renderer-debug rule-manager/debug-watcher | renderer-debug-main；logger/app-common；保存规则设置 | `SettingsPage.cs / DebugEndpoints.cs` 已补原1008端点子序列/长度排序建议、合法规则按钮门控、logAll当前值/外部更新同步、外部保存规则重画、5字段复制及译名/管理员说明；DebugRoute 原16项与新增13项业务夹具通过。真实LCU事件/文件日志写入待验。原 DebugSettings 无远程刷新/WMI操作/深链接参数输入，旧矩阵额外范围已纠正 |
| S11 | 关于/版本/许可证/仓库链接/统计信息与 LeagueAkari/MyGo 致谢保留 | `AboutPane.vue`、README、THIRD_PARTY_NOTICES | app-common version/API | 已接线待验：`SettingsPage.cs / 随包许可证` |

## 审计规则

后附设置键清单取自实际 default-settings.json 与前端持久化 store。任一键仅以 JSON 文本框暴露不算完成，必须有与原语义等价的开关/范围数值/枚举/结构编辑器。字典空对象（符文、技能、邀请策略等）应按上述对应编辑器验收。嵌套设置保存完整顶层对象而不擦除其他字段。MyGo 原有行为因迁移而缺失也属于本次缺口，不能用“Go已有/后台仍运行”证明 UI 还原。

矩阵记录实现与验收分别进行的状态。需要实机的节点若仅完成模拟测试，应保留已接线待验；不得据此将全迁移目标标为完成。

## 2026-10-07 本地实现与验收记录

当前代码已包含主窗口、玩家战绩 Tabs、对局页、自动化、工具、设置以及 Mini / OP.GG / 对局悬浮 / CD 原生窗口。下表对应代码入口，不代表已经完整通过上述共同验收门槛。

WinUI 更新准备与替换完整的自包含目录，必须包含宿主 PRI/XBF 资源及便携安装标记；卸载验证独立安装根和宿主/后端布局，仅删除应用目录、目标匹配的快捷方式及自有用户数据文件，不删除共享用户目录整体。临时 fixture 已通过安装目录删除、保留数据选项、非应用文件保留和源码/缺失资源/重叠路径拒绝测试；没有对真实安装执行卸载。

`scripts/package-winui.ps1` 已生成自包含 Release 便携包并补入发布目标遗漏的应用 PRI/XBF。实际从便携目录启动，独立 profile 禁用自动连接，观察到原生主窗口和唯一 Go 后端子进程；正常关闭后宿主及后端均退出。证据：`winui/evidence/portable-smoke/startup.json`。此项仅验启动/退出与分发资源完整性，不替代游戏功能验收。

最新独立业务回归包括 54 项筛选/资料数据源、75 项原生详情/战绩卡/回放/皮肤/时间线/通知/CD 契约、10 项资料段位、25 项遇见记录、8 项语言和 16 项路径模型测试，Go 两种入口测试通过。真实原生窗口配合模拟 backend 已点测 Mini 接受对局、接受倒计时取消、更新下载取消，观察到约定请求与对应状态变化；这些是模拟协议验证，没有对真实客户端执行这些写操作。全部游戏场景、外部设置同步、UAC 取消、更新与卸载生命周期仍按上表逐项保留验收边界。

- `mygo/winui_backend*.go` 与 `internal/winuiipc`：真实命名管道连接、并发调用、宿主回调、退出取消测试通过；实际 Go 后端 smoke 的主窗口句柄为 0、子进程数为 0，断开管道后退出。数据使用隔离目录，未修改真实对局。
- `Services/HistoryFilter.cs` / `Pages/HistoryFilterEditor.cs`：原 version=1/rootId=root/nodeMap 规则树，简单英雄/玩家条件采用 AND；高级 scope 限制、any/every/AND/OR/NOT、范围/队伍总计和最高占比、装备/符文/技能/海克斯以及三个原模板。`winui/tests/HistoryFilterTests` 的 47 项 LCU/SGP 筛选与资料数据源业务 fixture 通过。编辑器已编译，尚需原生交互验收。
- `internal/update/winui*.go`：WinUI ZIP 布局和路径校验测试通过；隔离测试实际整目录替换，旧文件被移除、新依赖被复制、保留旧目录备份并重启测试宿主成功。尚未针对真实 GitHub WinUI 发行物执行下载安装。
- Release WinUI 编译通过，0 警告 / 0 错误；便携包由 `scripts/package-winui.ps1` 生成，包含完整 .NET 与 Windows App SDK。旧 `scripts/build-mygo.cjs` / `package-mygo.ps1` 仍保留。

仍需逐项验收：真实客户端全部查询与写操作、英文全文与完整主题样式、所有窗口的键盘和 DPI/缩放、全部配置的即时应用与重启恢复、快捷键覆盖、断网保留内容。工具的原生选择器已有控件，但图像布局、快捷键录制范围和个别原版辅助流程尚须对照；对局标签/打野路径仍需与原分析输出逐项核对。没有受控内存测量，不宣称 WinUI 相比 MyGo 的固定内存降幅。

## 实际持久化设置键清单

此清单逐顶层键枚举，复杂字典附结构说明。每一项均需原生语义控件、保存成功反馈、重启值恢复。当前全项尚待对应运行验收。

| Namespace | 键 | 默认值/结构 | UI 来源 | 原生控制与验收 |
|---|---|---|---|---|
| `app-common-main` | `showFreeSoftwareDeclaration` | false | AppSettings.vue / MiscSettings.vue | ToggleSwitch |
| `app-common-main` | `locale` | "zh-CN" | AppSettings.vue / MiscSettings.vue | 枚举 ComboBox 或文本控件 |
| `app-common-main` | `theme` | "light" | AppSettings.vue / MiscSettings.vue | 枚举 ComboBox 或文本控件 |
| `app-common-main` | `httpProxy` | {"strategy":"disable","port":7890,"host":"127.0.0.1"} | AppSettings.vue / MiscSettings.vue | 结构编辑器，不能用 JSON 文本框 |
| `app-common-main` | `streamerMode` | false | AppSettings.vue / MiscSettings.vue | ToggleSwitch |
| `app-common-main` | `streamerModeUseAkariStyledName` | false | AppSettings.vue / MiscSettings.vue | ToggleSwitch |
| `app-common-main` | `preferredLolSource` | "sgp" | AppSettings.vue / MiscSettings.vue | 枚举 ComboBox 或文本控件 |
| `auto-champ-config-main` | `enabled` | false | AutoChampConfig.vue / ChampionConfig.vue | ToggleSwitch |
| `auto-champ-config-main` | `runesV2` | {} | AutoChampConfig.vue / ChampionConfig.vue | 结构编辑器，不能用 JSON 文本框 |
| `auto-champ-config-main` | `summonerSpells` | {} | AutoChampConfig.vue / ChampionConfig.vue | 结构编辑器，不能用 JSON 文本框 |
| `auto-gameflow-main` | `autoAcceptDelaySeconds` | 0 | AutoGameflow.vue | NumberBox 范围/步进保持 |
| `auto-gameflow-main` | `autoAcceptEnabled` | false | AutoGameflow.vue | ToggleSwitch |
| `auto-gameflow-main` | `autoHonorEnabled` | false | AutoGameflow.vue | ToggleSwitch |
| `auto-gameflow-main` | `autoHonorStrategy` | "all-member" | AutoGameflow.vue | 枚举 ComboBox 或文本控件 |
| `auto-gameflow-main` | `autoMatchmakingDelaySeconds` | 5 | AutoGameflow.vue | NumberBox 范围/步进保持 |
| `auto-gameflow-main` | `autoMatchmakingEnabled` | false | AutoGameflow.vue | ToggleSwitch |
| `auto-gameflow-main` | `autoMatchmakingMinimumMembers` | 1 | AutoGameflow.vue | NumberBox 范围/步进保持 |
| `auto-gameflow-main` | `autoMatchmakingRematchFixedDuration` | 2 | AutoGameflow.vue | NumberBox 范围/步进保持 |
| `auto-gameflow-main` | `autoMatchmakingRematchStrategy` | "never" | AutoGameflow.vue | 枚举 ComboBox 或文本控件 |
| `auto-gameflow-main` | `autoMatchmakingWaitForInvitees` | true | AutoGameflow.vue | ToggleSwitch |
| `auto-gameflow-main` | `autoSkipLeaderEnabled` | false | AutoGameflow.vue | ToggleSwitch |
| `auto-gameflow-main` | `playAgainEnabled` | false | AutoGameflow.vue | ToggleSwitch |
| `auto-gameflow-main` | `autoHandleInvitationsEnabled` | false | AutoGameflow.vue | ToggleSwitch |
| `auto-gameflow-main` | `autoReconnectEnabled` | false | AutoGameflow.vue | ToggleSwitch |
| `auto-gameflow-main` | `autoMatchmakingMaximumMatchDuration` | 0 | AutoGameflow.vue | NumberBox 范围/步进保持 |
| `auto-gameflow-main` | `invitationHandlingStrategies` | {} | AutoGameflow.vue | 结构编辑器，不能用 JSON 文本框 |
| `auto-gameflow-main` | `rejectInvitationWhenAway` | false | AutoGameflow.vue | ToggleSwitch |
| `auto-gameflow-main` | `autoSendARAMTeamSideEnabled` | false | AutoGameflow.vue | ToggleSwitch |
| `auto-gameflow-main` | `autoSendARAMTeamSideVisibleToTeam` | false | AutoGameflow.vue | ToggleSwitch |
| `auto-select-main` | `pickConfig` | 模式 ranked/normal/aram/cherry/urf/oneforall/ultbook/bot/custom；位置 top/jungle/middle/bottom/utility/default；champions 有序 ID 数组 | AutoSelectEditor.vue | 结构编辑器，不能用 JSON 文本框 |
| `auto-select-main` | `banConfig` | 模式 ranked/normal/aram/cherry/urf/oneforall/ultbook/bot/custom；位置 top/jungle/middle/bottom/utility/default；champions 有序 ID 数组 | AutoSelectEditor.vue | 结构编辑器，不能用 JSON 文本框 |
| `auto-misc-main` | `autoReplyEnabled` | false | AutoMisc.vue / toolkit/misc | ToggleSwitch |
| `auto-misc-main` | `autoReplyEnableOnAway` | false | AutoMisc.vue / toolkit/misc | ToggleSwitch |
| `auto-misc-main` | `autoReplyText` | "" | AutoMisc.vue / toolkit/misc | 枚举 ComboBox 或文本控件 |
| `auto-misc-main` | `lockOfflineStatus` | false | AutoMisc.vue / toolkit/misc | ToggleSwitch |
| `auto-misc-main` | `autoSetStatusMessageEnabled` | false | AutoMisc.vue / toolkit/misc | ToggleSwitch |
| `auto-misc-main` | `statusMessage` | "" | AutoMisc.vue / toolkit/misc | 枚举 ComboBox 或文本控件 |
| `auto-misc-main` | `autoSetRankedStatusEnabled` | false | AutoMisc.vue / toolkit/misc | ToggleSwitch |
| `auto-misc-main` | `rankedStatus` | {"queue":"RANKED_SOLO_5x5","tier":"CHALLENGER","division":"I"} | AutoMisc.vue / toolkit/misc | 结构编辑器，不能用 JSON 文本框 |
| `game-client-main` | `terminateGameClientWithShortcut` | false | toolkit/client/Client.vue | ToggleSwitch |
| `game-client-main` | `terminateShortcut` | null | toolkit/client/Client.vue | 结构编辑器，不能用 JSON 文本框 |
| `in-game-send-main` | `sendInterval` | 65 | toolkit/in-game-send/presets | NumberBox 范围/步进保持 |
| `in-game-send-main` | `cancelShortcut` | null | toolkit/in-game-send/presets | 结构编辑器，不能用 JSON 文本框 |
| `in-game-send-main` | `ratingPresetOptions` | targetShortcuts, kda, winRate, avgSoloKills, avgVisionScore, avgChampionDamage, avgDamageTaken, avgGold, avgCsPerMinute, avgKillParticipation, avgDamageGoldEfficiency, mainChampions, mainPositions, nameDisplayStrategy, showCurrentChampion | toolkit/in-game-send/presets | 结构编辑器，不能用 JSON 文本框 |
| `in-game-send-main` | `junglePresetOptions` | targetShortcuts, activityPreference, firstClearDistribution, earlyGank, dragonControl, monsterControl, mainChampions, nameDisplayStrategy, showCurrentChampion | toolkit/in-game-send/presets | 结构编辑器，不能用 JSON 文本框 |
| `in-game-send-main` | `premadePresetOptions` | targetShortcuts, nameDisplayStrategy | toolkit/in-game-send/presets | 结构编辑器，不能用 JSON 文本框 |
| `in-game-send-main` | `fixedTextPresetItems` | [] | toolkit/in-game-send/presets | 结构编辑器，不能用 JSON 文本框 |
| `league-client-ux-main` | `useWmi` | false | DebugSettings.vue | ToggleSwitch |
| `ongoing-game-main` | `concurrency` | 4 | OngoingGameSettings.vue | NumberBox 范围/步进保持 |
| `ongoing-game-main` | `enabled` | true | OngoingGameSettings.vue | ToggleSwitch |
| `ongoing-game-main` | `matchHistoryLoadCount` | 50 | OngoingGameSettings.vue | NumberBox 范围/步进保持 |
| `ongoing-game-main` | `matchHistoryTagPreference` | "current" | OngoingGameSettings.vue | 枚举 ComboBox 或文本控件 |
| `ongoing-game-main` | `gameDetailsLoadCount` | 0 | OngoingGameSettings.vue | NumberBox 范围/步进保持 |
| `ongoing-game-main` | `orderPlayerBy` | "default" | OngoingGameSettings.vue | 枚举 ComboBox 或文本控件 |
| `ongoing-game-main` | `showChampionUsage` | "recent" | OngoingGameSettings.vue | 枚举 ComboBox 或文本控件 |
| `ongoing-game-main` | `showMatchHistoryItemBorder` | false | OngoingGameSettings.vue | ToggleSwitch |
| `ongoing-game-main` | `showJunglePathing` | true | OngoingGameSettings.vue | ToggleSwitch |
| `ongoing-game-main` | `showJunglePathingForAllPlayers` | false | OngoingGameSettings.vue | ToggleSwitch |
| `ongoing-game-main` | `autoRouteWhenGameStarts` | true | OngoingGameSettings.vue | ToggleSwitch |
| `ongoing-game-main` | `playerCardTags` | showPremadeTeamTag, showSuspiciousFlashPositionTag, showWinningStreakTag, showLosingStreakTag, showSoloKillsTag, showEasyGankTag, showGreatPerformanceTag, showAverageTeamDamageTag, showAverageTeamDamageTakenTag, showAverageTeamGoldTag, showAverageCsPerMinuteTag, showAverageDamageGoldEfficiencyTag, showAverageEnemyMissingPingsTag, showAverageVisionScoreTag, showAverageKillDamageEfficiencyTag, showSelfTag, showMetTag, showTaggedTag, showWinRateTeamTag, showPrivacyTag, showAkariScoreTag | OngoingGameSettings.vue | 结构编辑器，不能用 JSON 文本框 |
| `ongoing-game-main` | `queryInLobbyPhase` | true | OngoingGameSettings.vue | ToggleSwitch |
| `ongoing-game-main` | `premadeTeamInferMatchCountThreshold` | 5 | OngoingGameSettings.vue | NumberBox 范围/步进保持 |
| `remote-config-main` | `preferredSource` | "gitee" | AppSettings.vue | 枚举 ComboBox 或文本控件 |
| `remote-config-main` | `updateLatestRelease` | true | AppSettings.vue | ToggleSwitch |
| `respawn-timer-main` | `enabled` | false | MiscSettings.vue | ToggleSwitch |
| `self-update-main` | `autoDownloadUpdates` | false | AppSettings.vue | ToggleSwitch |
| `self-update-main` | `ignoreVersion` | null | AppSettings.vue | 结构编辑器，不能用 JSON 文本框 |
| `window-manager-main` | `backgroundMaterial` | "none" | AppSettings.vue / MiscSettings.vue | 枚举 ComboBox 或文本控件 |
| `window-manager-main` | `contentProtection` | false | AppSettings.vue / MiscSettings.vue | ToggleSwitch |
| `window-manager-main/main-window` | `opacity` | 1 | MultiWindowSettings.vue | NumberBox 范围/步进保持 |
| `window-manager-main/main-window` | `pinned` | false | MultiWindowSettings.vue | ToggleSwitch |
| `window-manager-main/main-window` | `closeAction` | "ask" | MultiWindowSettings.vue | 枚举 ComboBox 或文本控件 |
| `window-manager-main/aux-window` | `opacity` | 1 | MultiWindowSettings.vue | NumberBox 范围/步进保持 |
| `window-manager-main/aux-window` | `pinned` | false | MultiWindowSettings.vue | ToggleSwitch |
| `window-manager-main/aux-window` | `enabled` | true | MultiWindowSettings.vue | ToggleSwitch |
| `window-manager-main/aux-window` | `autoShow` | false | MultiWindowSettings.vue | ToggleSwitch |
| `window-manager-main/aux-window` | `showSkinSelector` | true | MultiWindowSettings.vue | ToggleSwitch |
| `window-manager-main/opgg-window` | `opacity` | 1 | MultiWindowSettings.vue | NumberBox 范围/步进保持 |
| `window-manager-main/opgg-window` | `pinned` | false | MultiWindowSettings.vue | ToggleSwitch |
| `window-manager-main/opgg-window` | `enabled` | true | MultiWindowSettings.vue | ToggleSwitch |
| `window-manager-main/opgg-window` | `autoShow` | false | MultiWindowSettings.vue | ToggleSwitch |
| `window-manager-main/opgg-window` | `showShortcut` | null | MultiWindowSettings.vue | 结构编辑器，不能用 JSON 文本框 |
| `window-manager-main/ongoing-game-window` | `opacity` | 1 | MultiWindowSettings.vue | NumberBox 范围/步进保持 |
| `window-manager-main/ongoing-game-window` | `pinned` | true | MultiWindowSettings.vue | ToggleSwitch |
| `window-manager-main/ongoing-game-window` | `enabled` | false | MultiWindowSettings.vue | ToggleSwitch |
| `window-manager-main/ongoing-game-window` | `showShortcut` | null | MultiWindowSettings.vue | 结构编辑器，不能用 JSON 文本框 |
| `window-manager-main/cd-timer-window` | `opacity` | 1 | MultiWindowSettings.vue | NumberBox 范围/步进保持 |
| `window-manager-main/cd-timer-window` | `pinned` | true | MultiWindowSettings.vue | ToggleSwitch |
| `window-manager-main/cd-timer-window` | `enabled` | false | MultiWindowSettings.vue | ToggleSwitch |
| `window-manager-main/cd-timer-window` | `showShortcut` | null | MultiWindowSettings.vue | 结构编辑器，不能用 JSON 文本框 |
| `window-manager-main/cd-timer-window` | `timerType` | "countdown" | MultiWindowSettings.vue | 枚举 ComboBox 或文本控件 |
| `window-manager-main/cd-timer-window` | `reverseAdjustmentDirection` | false | MultiWindowSettings.vue | ToggleSwitch |

### 自动选择结构字段

下面每个字段在9模式中分别持久化；champions 再分别映射6位置。

| 结构 | 字段 | 控件和保存 |
|---|---|---|
| pickConfig.<mode> | `enabled` | 开关；setPickConfig(mode, patch) |
| pickConfig.<mode> | `champions` | 6位置可搜索有序英雄列表，上移/下移/移除/去重；setPickConfig(mode, patch) |
| pickConfig.<mode> | `ignoreIntent` | 开关；setPickConfig(mode, patch) |
| pickConfig.<mode> | `showIntent` | 开关；setPickConfig(mode, patch) |
| pickConfig.<mode> | `delaySeconds` | 受限NumberBox；setPickConfig(mode, patch) |
| pickConfig.<mode> | `strategy` | 策略枚举 just-show/show-and-lock-in/lock-in-immediately；setPickConfig(mode, patch) |
| pickConfig.<mode> | `benchHandleTradeEnabled` | 开关；setPickConfig(mode, patch) |
| pickConfig.<mode> | `benchSelectFirstAvailableChampion` | 开关；setPickConfig(mode, patch) |
| pickConfig.<mode> | `benchSwapAccumulatedDelaySeconds` | 受限NumberBox；setPickConfig(mode, patch) |
| banConfig.<mode> | `enabled` | 开关；setBanConfig(mode, patch) |
| banConfig.<mode> | `champions` | 6位置可搜索有序英雄列表，上移/下移/移除/去重；setBanConfig(mode, patch) |
| banConfig.<mode> | `delaySeconds` | 受限NumberBox；setBanConfig(mode, patch) |
| banConfig.<mode> | `strategy` | 策略枚举 just-show/show-and-lock-in/lock-in-immediately；setBanConfig(mode, patch) |

### 前端持久化（不可因重写Vue而丢失）

| Namespace | 键 | 原默认值 |
|---|---|---|
| player-tabs-renderer | refreshTabsAfterGameEnds | true |
| player-tabs-renderer | matchHistoryUseSgpApi | true |
| player-tabs-renderer | loadCount | 20 |
| player-tabs-renderer | defaultMatchHistoryTag | "<akari:all>" |
| player-tabs-renderer | defaultMatchHistoryTimeRange | "all" |
| player-tabs-renderer | defaultShowPractice | false |
| player-tabs-renderer | defaultShowIrregularGames | false |
| main-window-ui-renderer | useProfileSkinAsBackground | false |
| main-window-ui-renderer | sidebarCollapsed | false |
| main-window-ui-renderer | showTestPage | false |

### 结构字段进一步验收

- app-common-main.httpProxy: `strategy`、`port`、`host`。
- auto-misc-main.rankedStatus: `queue`、`tier`、`division`。
- ongoing-game-main.playerCardTags: `showPremadeTeamTag`、`showSuspiciousFlashPositionTag`、`showWinningStreakTag`、`showLosingStreakTag`、`showSoloKillsTag`、`showEasyGankTag`、`showGreatPerformanceTag`、`showAverageTeamDamageTag`、`showAverageTeamDamageTakenTag`、`showAverageTeamGoldTag`、`showAverageCsPerMinuteTag`、`showAverageDamageGoldEfficiencyTag`、`showAverageEnemyMissingPingsTag`、`showAverageVisionScoreTag`、`showAverageKillDamageEfficiencyTag`、`showSelfTag`、`showMetTag`、`showTaggedTag`、`showWinRateTeamTag`、`showPrivacyTag`、`showAkariScoreTag`。
- in-game-send-main.ratingPresetOptions: `targetShortcuts`、`kda`、`winRate`、`avgSoloKills`、`avgVisionScore`、`avgChampionDamage`、`avgDamageTaken`、`avgGold`、`avgCsPerMinute`、`avgKillParticipation`、`avgDamageGoldEfficiency`、`mainChampions`、`mainPositions`、`nameDisplayStrategy`、`showCurrentChampion`。
- in-game-send-main.junglePresetOptions: `targetShortcuts`、`activityPreference`、`firstClearDistribution`、`earlyGank`、`dragonControl`、`monsterControl`、`mainChampions`、`nameDisplayStrategy`、`showCurrentChampion`。
- in-game-send-main.premadePresetOptions: `targetShortcuts`、`nameDisplayStrategy`。
- targetShortcuts: friendly/enemy/all 分别录制、注销快捷键、重启恢复；fixedTextPresetItems 列表每条 id/title/shortcut/content 及排序与 CRUD 按现有服务契约。
- auto-champ-config runesV2: [championId][模式key] = { primaryStyleId, subStyleId, selectedPerkIds[9] } 或 null；summonerSpells: [championId][模式key] = { spell1Id, spell2Id } 或 null。
- invitationHandlingStrategies: 队列字典，DEFAULT/SOLO/FLEX/NORMAL/ARAM/KIWI/CHERRY/URF/TFT各枚举accept/decline/ignore，添加/删除队列，不能固定只支持一个队列。




## 本次实现与证据边界（2026-10-07）

- 原生自动操作页面覆盖 `auto-gameflow-main`、`auto-select-main`、`auto-champ-config-main`、`auto-misc-main`：九模式、六位置有序英雄优先列表，邀请队列策略与好友预约，符文主副系和九符文/碎片选择、技能模式过滤及合法性检查。源为 `Pages/AutomationPage.cs`，均为已接线待验。
- `Pages/SettingsPage.cs` / `SettingsForms.cs` 覆盖设置语义表单、原生快捷键录制、标记玩家 CRUD/导入导出、事件规则；设置事件更新控件而不重新保存。`NativeFormText.cs` 显式中英控件文本映射并复用原字典；WinUI `NativeAppearance` 负责主题/隐私/截屏排除。动态错误消息和所有 DPI/语言布局仍需实机验收。
- `Windows/OpggWindow.cs` 包含内部地区/模式/位置/版本/等级列表、英雄胜率与对线数据、符文/技能/物品方案/竞技场强化/海斗数据，原生全部窗口管理接口。自动应用前核对真实选中英雄；网络请求全数成功后原子提交，失败恢复上次成功筛选和完整详情，避免混合新条件与旧内容。OP.GG 公开API只读已实测；网络故障原生GUI、真实LCU写入仍待验。
- `mygo/send_presets*.go` + `send_calls.go` 保留原 `generate/sendRating/Jungle/PremadePreset` 契约，全部选项、三个发送目标、实名/英雄名策略、重复英雄回退、主玩显著性、两种语言与复数。快捷键直接调用后端，无旧 WebView listener 依赖。
- `generatePlayerAnalysis(puuid, {includeJungle,includeDetails})` 为原生对局卡片提供真实摘要聚合。返回 count/summary/winLoss.all/champions/positions/spells/akariScore/jungle/details；缺单杀/pings 保持 null。按需时间线提供前十四分钟位置、击杀权重、开野、早抓、团队目标和前十五分钟敌方打野参与阵亡。gameDetailsLoadCount>0 限制加载数量，0 不后台预取，显式分析时请求已加载的符合条件的样本。缓存 64 个实际历史内容和选项签名，网络失败返回错误。
- Go 八个回归测试：原 League Akari TS fixture（中文/英文战绩、海斗前后筛选、组队/重复名/目标）、1.5 主玩阈值、真实摘要伤害占比和加权 KDA、真实时间线位置/开野/早抓/目标、Akari 分数与敌打野参与阵亡、竞技场胜负/阵营及连胜序列。运行 `go test ./...` 全通过。编译 `dotnet build ... -p:OutputPath=bin/agent-parity/` 通过；二者均不替代真实客户端、键盘/DPI、完整生命周期验收。

遗漏门槛：原生 form 新增英文不等于所有动态状态全部本地化；共享 `ongoing-game-main.state.analysis` 仍由宿主协调，新增分析 API 的真实 UI 接线和结果必须核对；player analysis 的竞技场专属 `winLoss.cherry`/teamSide 已实现但并未列为已验。OP.GG 和真实网络/写操作需要单独证据。
- 后续补充：`generatePlayerAnalysis` 卡片后台刷新严格遵守 `gameDetailsLoadCount=0`，此时只消费缓存时间线；大于零才按上限预加载。预设的显式分析/发送允许按需查询，与后台刷新区分。缓存签名包括已缓存明细 ID，新增明细后重新聚合。新增第七个测试覆盖原 Akari 八项 15 分满分/标志阈值、敌打野参与阵亡时间窗口。
- 实机发现初始动态 TabView 未自动选择首项，设置与自动操作显式设置首项，工具集由对应 owner 同步修复。中英文由各页面负责，宿主不对这些页面二次翻译；Application tab 与 Apply 操作区分，开关的 On/Off、None 与更新区本地化。动态 TextBlock 修改和对话框均覆盖，仍需检查所有动态表格字符串。
- 原 `DebugSettings.vue:443` 的 2 秒 runtime 更新已接入：LCU 连接、阶段与运行信息；原生另展示 Go 与 WinUI 各自 working set，退出页面停止定时器。快捷键表单读取重启保留值并消费外部设置更新；邀请策略、选禁队列及英雄符文/技能配置结构消费外部设置变化。
- 原 radix 通配端点语义由 `Services/DebugRoute.cs` 移植，16 个独立回归夹具覆盖静态、命名/匿名参数与尾部多段通配。符文/技能保存期间按钮忙碌防重复提交。OP.GG 仅 ranked 开放位置选择，其他模式强制 none，并防并发初始化。
- 实机回归发现设置枚举绑定将未匹配值恢复为 `SelectedItem=null`，触发代理/重新匹配依赖下拉空引用。`SettingsForms.Choice` 仅恢复匹配的有效项；所有取值经 `TrySelected`/空值处理，不因缺选择写入设置。代理、自动流程、结构编辑器、分页及 OP.GG 空选择期间停止依赖动作；技能/符文保存检查选择和数值。该修复需要重新构建后实机复验，不能以先前编译记录算通过。
- `PlayerProfileView.cs` / `PlayerProfileData.cs` 补齐原 `PlayerTabHeader.vue`、`RankedPane.vue`、`RankedTable.vue`：头像与等级、Riot ID 复制及主播模式遮罩、非自己非跨区标记、两张带图段位卡与真实历史最高；更多弹层保留全部队列与前赛季结束/最高和历史最高。跨大区段位不可用，不伪请求支持。`ranked-stats.ts` 的排位结束立即查询、LP 未变时三秒重试，仅在自己、本地、420/440 模式、页面存活时执行。十项原业务字段/NA/queueMap 与历史最高夹具通过；图片、窄布局及真实结束后更新仍需实机验收。未添加原 UI 不存在的头像复制、简介与 XP 进度。

### 2026-10-07 战绩功能接入补齐

- PlayerProfileView 原头像/等级/名字复制/标记/刷新与两张段位卡，更多表保留所有队列及历史最高；排位结束仅刷新段位，未变时延迟重试。原 PlayerTabHeader 并无简介、XP条或头像复制，矩阵已纠正该来源误记。
- RecentlyPlayersView 依据已筛选战绩计算近期队友/对手，保留中键后台打开；保存遇见记录10条分页、双方英雄/KDA/关系、原字符串队列及单条删除确认。
- HistoryMatchCard 还原原标签排序、多杀扣除、装备/主符文和副系/增幅、竞技场名次、地图mutator、时间提示、十人阵容、雷达及懒加载详情；回放按原配置创建metadata并支持下载/观看状态。原卡片只用英雄头像，矩阵移除误记的皮肤。
- HistoryCollection 使用该玩家 scope 的英雄/位置筛选，避免把其他玩家的英雄算入；按原每批20场、目标数与最多1000场扫描上限创建条件。
- HistoryTabsPage 补关闭全部/其他/右侧、账号切换与断开、可拖动排序；无Tab时顶部搜索和我的战绩仍可用。
- Debug x64统一编译0警告0错误，188项独立业务测试通过。原生GUI在隔离fixture中观察段位完整弹层、回放下载状态转换、玩家Tab打开/关闭全部/重新打开、21条遇见记录三页及末页禁用。证据 winui/evidence/history-parity/acceptance.md 与 requests.jsonl/profile-requests.jsonl。
- 以上GUI为模拟数据，不代表真实回放下载/播放、删除或排位结束事件已实机验收。整体验收边界继续适用，未标全目标完成。

### 工具与对局操作补漏（同日续）

- 工具指定对局查询采用实时 preferredLolSource 决策、请求/返回ID匹配与完整 MatchDetailsView；玩家导航和模拟成功后跳转对局已接Main事件。
- 资料背景解析保留原 quest skin tiers、叠加形态预览与 unset 空串写入，保护切换英雄时的旧响应；聊天恢复7种状态，只有chat/away取消离线锁。
- 大厅支持原任意queueID；狂潮写入确认当前 STAWBERRY 大厅；客户端配置锁定后重读状态，原生快捷键/尺寸修复按backend availability门控。
- 工具页面改Grid约束滚动区随可用窗口高度，避免原固定850造成底部控件溢出。
- 对局编辑标签实时读 getPlayerTags，空白保存null，成功 reloadPlayer(savedInfo)；失败保留输入并可重试；动态SGP queue/tag选择携带原AND查询参数；名字与头像点击战绩。
- 模拟CHERRY使用原TEAM-ALL分组；无选中玩家和未上报位置时发送null，防止错误draft来源。
- 最新Debug x64统一构建0警告0错误；8套独立合同共221项通过。新增工具/对局写入流程尚未在真实客户端执行，未覆盖完整目标的运行验收。

### 选禁、OP.GG、设置与窗口验收续项

- 符文/技能编辑已补原草稿清除、恢复、合法副系及保存语义；普通ARAM OP.GG平衡与KIWI RESG独立显示，KIWI不自动写普通ARAM符文/技能。设置取消导入不假报成功，玩家标记删除仅清tag保留遇见记录。
- Release隔离配置中验证我的战绩页标题栏拖动、正常退出、重启还原1696×1118窗口及(427,204)位置；Go子进程正常退出。证据 `winui/evidence/window-persistence/acceptance.json`。该证据只证明主窗口指定行为，未证明全部悬浮窗、多显示器、最小DPI或游戏快捷键。
- 本轮自动选禁改用backend实际groups/positions及additional/excluded特殊项，保留精确groupId存储键、图像/关键词搜索、推荐位置筛选与有序列表。英雄搜索已补原 `string-match.ts` 的三阶段子序列：字面、标题无声调拼音、搜索词与标题均转拼音；首字母沿用全拼子序列规则。符文/技能英雄搜索复用同规则及GTIMG别名。
- `NativePinyin` 从已安装 pinyin-pro 3.28.1 生成21,132字符/4,184默认短语及数字多音规则；原最大概率分词、特殊“了/々”、ü、Unicode字符与JS大小写语义保留。压缩数据134,865字节，不启动Node、不依赖WebView；MIT声明随包保留。无自定义词典、姓氏优先或显式繁体转换，和原调用的默认参数一致。
- 独立 NativeDataContracts 当前124项通过，其中原库生成oracle覆盖33,824转写输入及165匹配对，包含全部字符/短语、交叉词、长文本概率缩放、混合中文英文/emoji及Unicode大小写边界。该证据为纯业务验收，未替代真实客户端或原生GUI搜索点测。
- Go CD显示由当前支持模式的使用状态变化驱动；禁用窗口的显示快捷键不会重新创建窗口。原 `settingChanged` 已能触发启用/autoShow重评估，未将此既有行为误记为缺失。
- 上次Release验收包262项业务测试通过；本轮114项原生合同与13项CD专用夹具已通过，完整GUI与真实客户端验收仍继续。

### W06 OP.GG 源码对齐及独立证据（2026-10-07）

- 对照 `opgg/context.ts`，原生查询先取得版本/榜单/英雄/海斗强化全部结果，再提交同一成功视图；失败恢复成功筛选，取消用token终止等待并在每个公开请求前后检查，保证不继续发后续查询，晚到响应不提交；原生强化各tab共享展开/高级/排序控件状态，窄窗口actions换行。ranked none 回退 mid；其他模式固定 none；Arena 请求不带 tier。加载禁用筛选及详情（ContentControl 的 IsEnabled，同时覆盖鼠标/键盘），已有英雄配置仍优先、KIWI 不自动导入普通ARAM技能/符文、-3 bravery 与 disabled英雄不跟随。
- 对照 `OpggChampionTable.vue`，榜单/英雄详情两个原生Tabs；按位置过滤、位置rank/tier_data与非排位flat tier/rank、win/play、rank/strength/win/pick/ban/name双向排序；采用原keywords+NativePinyin检索，含ID。520/600宽度门槛控制counter/ban显示。
- 对照原widgets，克制对手/全对线切换，协同4、技能/符文2、starter/boots/prism/core4、last8默认限数，独立展开状态；竞技场银/金/棱彩三组tab，海斗全部/银/金/棱彩tab、S–F强度、16默认限数、性能/热度排序与高级统计；普通ARAM与RESG海斗balance明确分开。符文主副系/碎片和装备/强化描述图标来自真实gameData。
- 对照 `utils/loadout.ts`，导入3套starter+4套core，boots/prism/last各合并一block，12个recipe恢复，与响应meta.version生成UID；none省略位置名称、空装备不导入；设置弹层包含enabled/flash D/F/auto/三个自动应用开关及原说明，使用原i18n键。语言/资源变更重新绘制并保留展开状态、balance缓存和当前tab。
- 独立业务验证 `dotnet run --project winui/tests/OpggDataTests` **43 passed**，涵盖请求参数/角色规范、位置与平均数据区别、Arena win/play、导入分组限数/配方/版本UID、active pick与bravery、强化零热度排序、缺统计/显式零值区别、缺rank索引回退、位置查询复用榜单、取消中断后续请求和强化失败不返回部分成功；`OpggSessionTests` **14 passed**。纯测试与GUI证据分开记录；原生模拟GUI另见下条。
- 真实公开OP.GG只读请求：全球ARAM/竞技场versions返回16.19/16.18，ARAM提莫返回5符文/5技能/15starter/15core，提莫海斗强化199项，竞技场榜单245项、flat tier/rank与win/play真实结构已核对。未访问任何真实游戏写接口。最终gate：原生窗口各模式过滤/详情/分组展开、设置与中英/深浅切换、失败取消与旧数据保留、真实选人自动跟随和客户端导入仍需宿主点测。

- 原生隔离GUI已验证：timo检索/榜单英雄tab/详情展开、ARAM位置none、海斗稀有度tab和高级统计共享、普通ARAM与RESG分离、设置弹层、英文深色重绘、最终强化请求失败保留旧完整详情、30秒版本延迟立即取消且不继续后续请求。证据 `winui/evidence/opgg-parity/acceptance.md`，均为模拟协议；真实选人自动应用/客户端导入、其他模式全部GUI、DPI/键盘仍待验。

### W09 连接面板源码对齐（2026-10-07，部分原生GUI已验）

- 原 `components/sidebar/ClientConnection.vue` 将当前auth与其他launched PID分组，展示账号头像/名字与本地化大区，点击正在连接的同PID取消；原生现用紧凑选择栏与原生客户端列表Flyout呈现相同资料及点击行为，排除当前connected PID、保存有效选择、connecting目标优先。`peekClient`已有真实Go服务，其他客户端按原10秒周期读取；已退出PID缓存删除。
- 原连接卡片包含 `restartUx`、三个结算phase下的 `playAgain` 与更多 `launchUx/killUx/quitClient`。已接对应真实POST端点，操作前确认目标PID未切换，操作忙碌避免重复请求，连中保持取消入口；没有调用真实客户端写操作进行验证。
- 事件刷新合并并保留错误至主动关闭或下一操作；Loaded/Unloaded/locale/privacy订阅及异步晚到响应门控，错误前缀可切语言而接口错误原文保留。资料与连接状态用真实snapshot，Go自动发现设置由已有settings负责，不在连接组件新增原版没有的开关。
- `dotnet run --project winui/tests/ConnectionDataTests` **25 passed**：多客户端去重与排除连接PID、连接中/旧选择/退出选择处理、国服/其他区SGP映射及locale名称、原playAgain phases、10秒peek间隔。纯业务测试不代表GUI通过。
- 原生fixture已验多客户端账号头像/候选排除、失败保留原连接、重试切换与断开、连接中取消、中英文错误保留与关闭、EndOfGame回房间及重启UX模拟POST；证据 `winui/evidence/connection-shell-parity/acceptance.md`。Go真实本地HTTP与WebSocket回归已验证唯一客户端发现、autoConnect恢复manual标记、取消/换PID关闭旧stream并拒绝旧响应和事件。真实多客户端、无客户端/单客户端GUI、客户端退出、主播模式和全部UX菜单/结算phase/DPI仍待验。


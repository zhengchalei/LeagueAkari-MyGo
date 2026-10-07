# WinUI 原生界面测试后端

这个控制台程序只用于 WinUI Debug 版的独立验收，不参与生产打包，不连接 LOL，不执行真实选人、皮肤、自动匹配或游戏输入。

```powershell
dotnet build winui/tests/FixtureBackend/FixtureBackend.csproj
$env:LEAGUE_AKARI_WINUI_BACKEND = (Resolve-Path winui/tests/FixtureBackend/bin/Debug/net10.0/FixtureBackend.exe).Path
$env:LEAGUE_AKARI_WINUI_USER_DATA = (Join-Path $env:TEMP 'league-akari-winui-fixture')
$env:WINUI_FIXTURE_ASSET = (Resolve-Path desktop/build/icon.png).Path
$env:WINUI_FIXTURE_LOG = (Join-Path $env:TEMP 'league-akari-winui-fixture-actions.jsonl')
$env:WINUI_FIXTURE_PHASE = 'ChampSelect'
```

支持 `ChampSelect`、`Lobby`、`ReadyCheck`、`Matchmaking`、`InProgress`。数据是模拟的 10 位玩家、8 场 SGP/LCU 战绩、16 个时间线帧；Mini 的 5 个英雄、24 张皮肤和 3 条调整使用测试图片。点击英雄/皮肤后在下一次 Mini 快照返回选中状态，原始操作记录到 `WINUI_FIXTURE_LOG`。

验收应检查原生 WinUI 卡片、窄窗口、图片、虚拟皮肤滚动、客户端回传选中状态、战绩详情的 6 个标签、指标筛选、符文数值、事件/位置/伤害弹层、构筑、曲线及隐藏后不刷新。界面截图必须注明模拟数据，不能把这个后端作为真实客户端功能的证据。

`WINUI_FIXTURE_REQUEST_LOG` 可选指定请求日志路径，以验证隐藏窗口不再轮询。Fixture 自动创建两种日志的父目录；设置变更发送真实协议的 `update-state-prop/{namespace}:settings` 事件，用于实时主题、语言和隐私模式验收。

原生通知场景（仍然全部为模拟数据）：

```powershell
$env:WINUI_FIXTURE_PHASE = 'ReadyCheck'
$env:WINUI_FIXTURE_NOTIFICATIONS = 'auto-accept,respawn,update-progress'
$env:WINUI_FIXTURE_COUNTDOWN_SECONDS = '120'
```

`WINUI_FIXTURE_NOTIFICATIONS` 支持逗号组合 `auto-accept`、`auto-matchmaking`、`respawn`、`reconnect`、`update-progress`、`update-failed`、`update-ready`、`new-release`、`announcement`。取消自动接受/匹配、取消/重试更新仅改变本进程测试状态，发送原生 `update-state-prop/*:state` 事件并记录操作。`update-ready` 的退出按钮会关闭测试宿主，测试前保存其它工作。复活时间随 Mini 快照发送模拟事件；下载进度固定 65%，用于截图和按钮验收。

`WINUI_FIXTURE_NOTIFICATION_CONTROL` 可指向本地 JSON 文件，格式为 `{"revision":"1","states":{"league-client-main:login":{"loginQueueState":{"estimatedPositionInQueue":100,"maxDisplayedPosition":1000,"approximateWaitTimeSeconds":3661}}}}`。修改 revision 后，下次请求会合并这些测试字段并发送对应的 `update-state-prop/{namespace}:{state}` 事件。支持 `:state`、`:login`、`:settings` 等真实协议分区，可验证登录排队变化、公告/更新生命周期、复活状态和设置刷新；仅作用于此模拟进程，不连接 LOL。

纯业务边界测试：`dotnet run --project winui/tests/NativeDataContracts/NativeDataContracts.csproj`，涵盖 LCU/SGP 时间线路由、缺失数值与零值、模拟对局身份约束、皮肤/chroma 客户端确认与禁用状态、皮肤列数适配。

`WINUI_FIXTURE_SOURCE=lcu` 将个人/对局摘要切成 LCU 结构，用真实 `/lol-match-history/v1/game-timelines/{id}` 请求返回不含 SGP `damageStats`、`championStats`、被害伤害明细的测试帧。默认 `sgp` 返回完整测试帧。NativeDataContracts 另读取仓库现有腾讯 HN10 API 捕获快照，验证真实 LCU/SGP 字段约定；这些离线快照测试不代表当前 LOL 客户端联机已通过。
# Auto pick / ban scenario

Reward acceptance uses `WINUI_FIXTURE_REWARD_FAIL_ONCE=1` to fail the first in-memory claim. Three test lists cover pending grants, selectable missions and events with normal plus bonus rewards. Successful simulated writes remove their entries on subsequent reads; this never reaches League. `WINUI_FIXTURE_LIFECYCLE_CONTROL` can point to a JSON file containing `revision` and `phase` to exercise gameflow button availability. See `winui/evidence/settings-toolkit-parity/acceptance.md` for observed coverage and limitations.

`bin/AutomationVerification/FixtureBackend.exe` includes the real local `select-groups.json` definitions and synthetic champion availability and recommended positions. The editor can verify ranked five-position lists, default-only groups, Cherry bravery (`-3`), no-ban (`-1`), ordering, filtering, temporary pause / resume and settings persistence within this isolated fixture process. It does not issue requests to a real League client.


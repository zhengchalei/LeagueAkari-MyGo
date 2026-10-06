# LeagueAkari-MyGo

**v0.5.4 · 国服 LOL 战绩与选人助手的轻量版本。** 使用 [MyGo](https://github.com/egoist/mygo) 0.2.10，以 Go 后端和 Windows WebView2 承载原有 Vue 界面，替换 Electron 宿主。

本项目基于 [LeagueAkari](https://github.com/LeagueAkari/LeagueAkari) 开发，是独立维护的非官方版本。感谢 Hanxven 与 LeagueAkari 贡献者提供原始界面和业务实现，感谢 egoist 与 MyGo 贡献者提供桌面框架。

## 下载与启动

从 [最新发布](https://github.com/zhengchalei/LeagueAkari-MyGo/releases/latest) 下载 Windows x64 压缩包 `LeagueAkari-MyGo-<版本>-win-x64.zip`，完整解压后运行 **LeagueAkari-MyGo.exe**。这是便携版，运行时不需要 Node.js 或独立本地服务。

系统需要 Windows x64 和 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。启动 LOL 客户端后，助手会自动识别国服 WeGame 客户端；也支持手动连接和切换客户端。常规查询可在非管理员权限下使用，原生游戏输入需要助手与游戏具有相同权限。

## 功能

- v0.5.4 将 Mini 改为紧凑常显列表：增益绿色、减益红色，显示相对常规值的变化（例如伤害 −5%、承伤 −10%）。已有皮肤以卡片显示，保留已拥有筛选、炫彩与客户端确认；切换英雄时清除旧皮肤请求结果。辅助窗口首次默认高度为 620，仍优先恢复用户保存的窗口大小。
- v0.5.3 接入 [Bilibili RESG](https://www.bilibili.com/toy/resg/index.html) 的独立海斗英雄调整：主窗口和 Mini 显示增益、减益、来源、数据版本及缓存状态，保留攻速增长等小数单位；未知调整保留原文说明。
- v0.5.2 补齐聊天在线状态、选人／房间／赛后会话与成员同步，恢复选人和房间中的快捷文本发送；同步本人资料背景和登录排队信息，登录期间不会因召唤师尚未加载而误断连。英雄与可选／禁用列表通过事件实时更新。
- 导入配置即时应用到自动连接、战绩查询数量、快捷键和辅助窗口样式；硬件加速在下次启动时生效。HTTP 代理设置即时用于 SGP、OP.GG 和其他外部请求，本地客户端请求保持直连。
- 修复 Windows 程序与桌面快捷方式显示默认图标的问题，图标和版本信息随构建嵌入 EXE。
- v0.5.1 修复海克斯大乱斗“三选一”候选英雄未同步的问题：主窗口和 Mini 显示个人候选卡，支持点击锁定，并在选人结束或客户端断连时清理候选。
- 打开默认显示本人真实战绩；进入选人或对局时自动切到“对局”，用户可以手动切回。
- 大乱斗与海克斯大乱斗共用选人交互：点击英雄卡片选择或交换，已有皮肤与英雄同时显示，点击皮肤提交选择。平衡信息只展示有可靠来源的增益与减益。
- 查看队友、对手、段位、近期战绩、胜率、KDA、玩家标签、组队信息与战绩详情。完整时间线按需查询。
- 玩家标记、多账户标签、历史相遇记录、历史对局模拟、筛选分页，以及配置和标签文件导入导出。
- 自动接受、结算回房间、重连、匹配与排队重启、邀请处理、转交房主、选禁与预选、备选席交换、英雄符文和召唤师技能配置。新安装的自动化默认关闭。
- 自动点赞支持五种目标策略。“仅本局队友”覆盖组队与随机队友，排除对手。
- 自动回复、在线状态锁定、登录状态配置、好友上线补邀请、地图位置发送、英雄交易与克隆投票。
- 游戏内快捷键可发送战绩、打野、组队信息或自定义文本，也可调整计时器和显示对局面板。
- WeGame/TCLS 启动、客户端窗口修复、游戏配置读取及前台游戏终止工具。
- 辅助窗口按需创建。主窗口关闭时可退出或隐藏到托盘，重复启动会显示已有窗口；窗口位置、尺寸和最大化状态会记忆，支持拖动与缩放。

LCU REST/事件、SGP 和客户端图片均由 Go 访问，认证凭据不返回界面。已有皮肤来自客户端库存。支持绝对 `file:` 图片路径，经本地图片代理提供给 WebView。

普通大乱斗使用 OP.GG 平衡数据，海克斯大乱斗使用第三方社区站 RESG 的独立 KIWI 数据，两者分开保存。启动及每 30 分钟检查数据更新；RESG 版本、采集时间或页面修订变化时才拉取英雄详情，并只保留英雄调整。随包附带 16.19 数据快照（173 个英雄，106 个列有调整），联网失败继续使用缓存并显示来源版本；未列出调整表示该来源没有列出调整，不代表官方确认无调整。RESG 不是 Riot／腾讯官方数据源，其版本可能落后于当前客户端。接入说明见 [海斗数据说明](docs/KIWI_BALANCE.md)。

## 数据与迁移

用户数据位于 `%APPDATA%/LeagueAkari-MyGo`：`settings.json` 保存配置，`players.sqlite` 保存玩家标记与相遇记录，`kiwi-balance.json` 保存最近一次完整海斗调整数据，窗口位置、正常尺寸与最大化状态保存在配置中；旧 `window-state.json` 仅用于迁移兼容。

首次使用新名称启动时，自动迁移旧 `%APPDATA%/TimoMyGo` 的必要数据。玩家库通过只读连接执行 SQLite 一致快照备份，包含已提交的 WAL 数据；配置、窗口状态及数据库完整性检查通过后，才启用新目录。旧目录保留；新目录已存在时以新目录为准，不重复覆盖。日志和浏览器缓存不随迁移复制。

也支持只读导入旧 Timo/LeagueAkari 数据库中的设置、玩家标记与相遇记录，已有 MyGo 用户设置优先。首次迁移将对局查询调整为每人 20 场、自动详情预取设为 0，后续尊重用户修改。

## 内存观测

以下为同一台 Windows 机器上的实际观测，单位 **MiB**。统计覆盖助手主进程及其全部子进程，而非只统计 Go 或 Electron 主进程。

| 版本与观测场景                                            | 进程数 | 工作集 | 私有提交 |
| --------------------------------------------------------- | -----: | -----: | -------: |
| 迁移前 Timo Electron，真实客户端使用                      |      6 | 1665.0 |   1626.1 |
| 官方 LeagueAkari 1.4.3，长时间运行，2026-10-06 03:32 观测 |      5 |  827.1 |    895.7 |
| MyGo 0.4.0，先期运行参考                                  |      — |  660.1 |    507.9 |
| LeagueAkari-MyGo 0.5.0                                    |   待测 |   待测 |     待测 |

本次观测相较官方 LeagueAkari 1.4.3，工作集少约 **23.9%**、私有提交少约 **46.6%**；相较迁移前 Timo Electron，分别少约 **62.2%** 和 **70.6%**。

这些观测不是受控性能基准：版本、使用场景和运行时长不同，不能据此保证固定下降比例。工作集是当前驻留内存，各进程共享页相加时可能重复计入；私有提交是进程独占的已提交内存，不等同于物理内存占用。详细方法与记录见 [内存说明](docs/MEMORY.md)。

降低占用既来自更换宿主，也来自查询与缓存调整：默认每人 20 场摘要、时间线懒加载、摘要去除界面不使用的 `missions`、手动时间线最多缓存 4 场、页面最多缓存两个，以及辅助窗口按需创建。同一对局不反复读取所有玩家战绩，跨局和人员变化会清理相关缓存。用户可以显式开启有数量限制的详情预取，用于打野路线和分析。

MyGo 仍会创建 WebView2 子进程，Vue 界面、战绩数据和浏览器渲染仍占用内存。

## 更新

默认更新源为 [zhengchalei/LeagueAkari-MyGo](https://github.com/zhengchalei/LeagueAkari-MyGo)，支持检查、下载、取消、重启替换与失败回滚，使用适配 MyGo 的 Windows x64 发布包。

可通过环境变量 `LEAGUE_AKARI_MYGO_UPDATE_REPOSITORY=owner/repo` 指定自己的 GitHub 发布仓库；同时兼容旧变量 `TIMO_UPDATE_REPOSITORY`，新变量优先。便携版卸载删除自身程序，玩家数据可选择保留。

## 构建与测试

在 Windows x64 上安装 Node.js 和 Go，仓库根目录执行：

```powershell
npm run desktop:install
npm run mygo:build
npm start
npm run mygo:package
```

前端依赖使用 `desktop/` 的 Yarn 锁文件，Go 工具链按 `go.mod` 选择版本。构建结果为 `mygo/build/LeagueAkari-MyGo.exe`，压缩包位于同目录。构建前退出正在运行的同名程序；页面静态资源内嵌在可执行文件中，发布包附带许可证。

构建脚本通过固定版本的 `go-winres` 生成 Windows 图标和版本资源；首次构建需要下载该构建工具，不增加程序的运行依赖。需要保留运行中的旧构建时，可传入独立输出目录，例如 `npm run mygo:build -- build/release-0.5.2`，随后用 `scripts/package-mygo.ps1 -BuildDirectory mygo/build/release-0.5.2` 打包该目录。

```powershell
npm run mygo:test
npm run mygo:check
node --test mygo/frontend/runtime.test.cjs
node desktop/node_modules/vitest/vitest.mjs run --config mygo/frontend/vitest.config.ts
cd mygo
go vet ./...
```

已验证 Go 单元测试、原生 SQLite 事务与中断恢复、前端兼容与分析测试、Vue 类型检查，以及 Windows 构建。真实国服客户端验证覆盖本人资料、段位、战绩、对局玩家、客户端图片和只读 SGP 查询；自动化写操作采用模拟客户端测试。

`desktop/` 保留原有 Electron 源码供业务和界面对照，默认桌面入口使用 MyGo。早期浏览器原型位于 `app/`，`npm run web` 启动原型；演示数据不代表登录账号。

## 许可证与致谢

本项目采用 [MIT 许可证](LICENSE)。源自 Hanxven 与贡献者的 [LeagueAkari](https://github.com/LeagueAkari/LeagueAkari) 1.5.0-rabi.2，保留其 [原始 MIT 许可证](desktop/LICENSE)。宿主使用 egoist 与贡献者的 [MyGo](https://github.com/egoist/mygo) v0.2.10，遵循其 MIT 许可证。发布包包含本项目、LeagueAkari 和 MyGo 的完整许可证，其他依赖遵循各自许可证，详见 [第三方说明](THIRD_PARTY_NOTICES.md)。

英雄联盟、美术资源与相关商标属于 Riot Games 及各自权利人。本项目独立维护，与 LeagueAkari 官方发行版、Riot Games 和腾讯无官方关联。

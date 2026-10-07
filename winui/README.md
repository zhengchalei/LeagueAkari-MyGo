# LeagueAkari · WinUI 3 保留实验方案

项目主线为 [LeagueAkari-MyGo](../README.md)。WinUI 3 源码和独立打包入口保留供后续评估，当前暂停全量迁移，不作为默认启动或发布版本。尚无受控测试证明其内存占用低于 MyGo。

这是 WinUI 3 全原生界面迁移的本地验收包，版本号沿用 0.5.6 业务基线。目前尚未完成全部功能的 1:1 运行验收，不是正式发布版本，也没有进行受控内存对比。

完整解压压缩包，运行 **LeagueAkari.WinUI.exe**。不要单独移动 EXE，程序依赖同目录中的 Go 后端、.NET 与 Windows App SDK 文件。便携包包含这些运行时，不需要另外安装 .NET、Windows App SDK、WebView2、Node.js 或 Electron。支持 Windows 10 1809 及以上的 x64 系统；构建目标使用 Windows 11 SDK。运行前启动 LOL 客户端，连接状态和客户端选择位于窗口顶部。

主窗口、Mini、OP.GG、对局悬浮窗口和 CD 计时窗口使用 WinUI 控件；后台 `LeagueAkari.Backend.exe` 提供既有 Go 数据、自动化和本地客户端访问。宿主通过仅当前用户可访问的命名管道交换请求与事件，关闭宿主会停止后端。此入口不会创建 MyGo 窗口或 WebView2 页面，Go 构建不内嵌 Vue 页面。Go 代码目前仍保留 MyGo 的兼容路径和依赖，旧 MyGo 版本继续支持独立构建。

配置和玩家数据默认复用 `%APPDATA%\LeagueAkari-MyGo`。验收时可在启动前设置 `LEAGUE_AKARI_WINUI_USER_DATA` 到独立目录，避免覆盖日常配置；使用同一个数据目录时，请先退出旧 MyGo 程序。

```powershell
$env:LEAGUE_AKARI_WINUI_USER_DATA = "$env:LOCALAPPDATA\LeagueAkari-WinUI-Acceptance"
.\LeagueAkari.WinUI.exe
```

内部窗口、筛选规则、设置与工具的迁移状态见随包 [验收矩阵](docs/WINUI3_PARITY.md)。打包检查包含 Go 后端回归与 32 个原生功能套件共 1116 项业务与交互契约测试、隔离目录更新替换测试和真实 Windows 编译；模拟原生界面验证了导航、部分自动选禁、搜索、战绩总览、历史英雄打野分析、闪现分布与Mini三选一/皮肤失败重试/炫彩/英文深色/0次随机状态，以及OP.GG筛选/详情/强化分组/失败保留/取消请求。新增关闭动作记忆、重复启动恢复原窗口、多客户端选择/连接失败保留及中英文错误提示/连接中取消验证；后台轮询和 WebSocket 已补断开与切换时的旧请求取消、旧响应和事件隔离回归。通知新增取消操作、超过一小时的排队时间、更新下载按钮忙碌态、公告已读和声明 Escape 行为验证；战绩标签关闭菜单边界、全关闭后本人头像恢复及外部导航折叠事件也经过原生模拟点测。应用设置背景互斥/更新结果/下载禁用/源测速、奖励失败保留重试/任务与活动领取、流程接受与返回房间新增模拟点测；Go补齐配置并发与LCU完整摘要，切客户端旧响应隔离经过HTTP回归。战绩新增分页/收集请求归属、来源协议与筛选规则回归；原生点测验证详情/时间线失败重试、七指标雷达、后台中键标签和高级筛选模板。Go详情预取恢复原300ms响应窗口。新增英雄符文/技能编辑、自动回复与好友预约、固定文本/好友管理及评价发送共164项契约；自动回复失败草稿保留与显式重试、好友预约/取消/搜索/离开房间隐藏经过原生模拟点测；这些验证不代表真实客户端写操作验收完成。真实对局写操作、全部页面布局/DPI、完整快捷键和原版逐项交互仍须验收。自动化设置来自当前配置，显式独立目录不自动导入外部旧版配置，新目录默认关闭。

WinUI 更新只接受 `LeagueAkari-WinUI-<版本>-win-x64.zip`，校验必需宿主和后端文件后准备整个目录，退出后替换目录并重新启动宿主；失败时尝试恢复旧目录。它不会将 MyGo 单 EXE 更新包装到 WinUI 中。本地验收包未上传 GitHub，也没有创建版本标签。

卸载会在设置页确认后删除独立的 `LeagueAkari-WinUI-*` 便携目录，以及指向当前宿主的 LeagueAkari 快捷方式。必须保留随包安装标记；开发目录或源码目录不能卸载。共享用户数据目录本身及其他文件会保留，只移除应用自有配置、玩家数据库、日志和缓存；复用原 MyGo 配置时，这些共享应用数据也会被删除。

源码构建需要 Windows x64、.NET 10 SDK（包含 Windows SDK 构建组件）、Go 工具链（版本见 `mygo/go.mod`）与首次还原依赖的网络连接。从仓库根目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/package-winui.ps1
```

输出位于 `winui/build/LeagueAkari-WinUI-0.5.6-win-x64.zip`。脚本先发布自包含 WinUI 运行目录，再运行 Go 与筛选测试、构建专用后端并打包。原版 MyGo 构建命令保持不变。

本项目遵循 MIT 许可证。感谢 Hanxven 与 LeagueAkari 贡献者提供原业务与界面，感谢 egoist 与 MyGo 贡献者提供既有后端宿主。Windows App SDK、.NET 和其他依赖遵循随包 `licenses/` 中的许可证与第三方声明。Windows App SDK 的传递依赖包含 WebView2 的桥接 DLL；程序没有创建 WebView2 页面，不要求安装浏览器运行时。英雄联盟图像与商标属于 Riot Games 及各自权利人。


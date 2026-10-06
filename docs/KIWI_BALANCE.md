# 海克斯大乱斗独立平衡数据与来源

核验日期：2026-10-06。公开网页与接口均为只读 GET；本地检查只读取游戏安装包中的静态资源，没有访问玩家资料或执行游戏写操作。

## 已接入 Bilibili RESG

v0.5.3 根据用户提供的 [Bilibili RESG 页面](https://www.bilibili.com/toy/resg/index.html)，取得其独立海斗英雄调整。2026-10-06 只读核验覆盖 16.19 的全部 173 个英雄：106 个英雄列有调整，其余 67 个没有 `bb` 调整字段。该站自称“海克斯大乱斗英雄与强化符文数据站”，页面署名 `resg.top`，注明使用 CommunityDragon 与 Riot Games API 资料。它是第三方社区站，不是 Riot 或腾讯官方平衡公告。

页面的 iframe 指向 `https://www.bilibilitoy.com/toy/resg/19226257645568-v21512/index.html`，实际资源位于该页面目录：

- `api/v1/versions.js`：版本列表，16.19 的 `collectedAt` 为 `2026-10-02T06:18:36.545Z`。
- `api/v1/versions/16.19/champions.js`：173 个英雄 ID 的列表。
- `api/v1/versions/16.19/champions/{id}.js`：英雄详情，`bb` 为中文属性名称到字符串／数字的映射。

例如 [萨勒芬妮详情](https://www.bilibilitoy.com/toy/resg/19226257645568-v21512/api/v1/versions/16.19/champions/147.js) 列有 `护盾效果:80%`、`治疗效果:80%`、`造成伤害:90%`；[蛮王详情](https://www.bilibilitoy.com/toy/resg/19226257645568-v21512/api/v1/versions/16.19/champions/23.js) 列有 `承受伤害:90%`、`治疗效果:120%`、`造成伤害:110%`。这次接入按该海斗站的公开字段展示，未用 OP.GG 的普通 ARAM 数据填补缺失值，也未声称已在当前游戏内逐英雄核验。

字段单位：伤害／承伤／治疗／护盾／施法资源回复使用百分比倍率；技能急速与韧性保留站点的直接数值；攻击速度增长 `+2.5%` 表示增长调整，不能显示为总攻速 102.5% 或四舍五入到 3%。降低承伤属于增益，其他已识别属性按方向分类。无法确认的新属性或特殊规则保留原文说明，不猜测其 buff／debuff 方向。

Go 后端每 30 分钟检查页面修订和数据版本。只有版本、采集时间或页面修订变化时才并发最多 3 个详情请求，并仅存储精简调整表。只解析 `export default` 后的 JSON 字面值，不执行远程 JavaScript，也不加载该站的整套页面或出装统计。一个详情失败时整批保留旧快照；成功后持久化，离线可使用随包快照。界面显示来源、版本和缓存状态。普通 ARAM 与 KIWI 始终分开；未确认其他模式使用同一修正表。

数据源没有 `bb` 时界面显示“数据源未列出调整”；英雄不在当前快照时显示暂无独立数据。来源更新可能滞后或字段遗漏，因此这两种状态均不代表官方确认“没有调整”。

验证包括全部真实公开详情的解析、承伤方向／小数／字符串与数字混用、动态 iframe 修订、相同版本不重复拉取详情、半批失败保留快照、重启缓存与主窗／Mini 状态同步。真实游戏内核验尚未完成。

## 此前未找到独立表的来源

以下为接入 RESG 之前的调查记录。OP.GG 与客户端基础资源中没有找到独立表的结论，仅限这些已检查入口。

## 已核验的来源

| 来源                                                                                                                                                                                                                              | 实际可用的数据                                                             | 独立 KIWI 平衡值         |
| --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------- | ------------------------ |
| [OP.GG 海斗英雄列表](https://op.gg/lol/modes/aram-mayhem)与[萨勒芬妮页面](https://op.gg/lol/modes/aram-mayhem/seraphine/build)                                                                                                    | 当前 16.19 的英雄梯度、增幅与出装；页面及 Next.js 载荷中未发现独立平衡对象 | 未取得                   |
| [OP.GG ARAM 平衡接口](https://lol-api-champion.op.gg/api/contents/aram-balance)                                                                                                                                                   | 普通 ARAM 英雄调整表                                                       | 不能视为 KIWI            |
| [腾讯英雄资料](https://game.gtimg.cn/images/lol/act/img/js/hero/147.js)                                                                                                                                                           | `hero`、`skins`、`spells`、版本信息；英雄基础属性                          | 未包含                   |
| [腾讯海斗海克斯资源](https://game.gtimg.cn/images/lol/act/img/js/kiwi/kiwi_augments.json)                                                                                                                                         | `augmentID`、中英文名称、等级、说明、图标                                  | 增幅数据，不是英雄平衡表 |
| [Riot 26.19 更新说明](https://www.leagueoflegends.com/en-us/news/game-updates/league-of-legends-patch-26-19-notes)与[26.18 更新说明](https://www.leagueoflegends.com/en-us/news/game-updates/league-of-legends-patch-26-18-notes) | Mayhem 章节说明增幅调整与修复                                              | 未提供完整英雄调整表     |
| 本地客户端 `rcp-be-lol-game-data` 静态包                                                                                                                                                                                          | 英雄、地图、模式增幅列表与召唤师技能资料                                   | 已检查对象中未包含       |
| CommunityDragon 的客户端与游戏资源镜像                                                                                                                                                                                            | 客户端英雄 JSON、模式规则、解包英雄技能资源                                | 已检查对象中未包含       |

OP.GG `/api/contents/aram-balance?type=aram_mayhem` 和 `?mode=aram_mayhem` 返回内容与不带参数的普通 ARAM 响应完全相同。接口接受未知参数并不能证明它支持另一个模式。少数独立海斗平衡路径检查返回 404；这不能证明 OP.GG 永远没有该数据，只说明本次没有找到可确认的公开契约。

### 数据结构边界

OP.GG 普通 ARAM 响应为 `data` 数组，每项包含 `champion_id`、`damage_dealt`、`damage_taken`、`attack_speed`、`cooldown_reduction`、`healing`、`tenacity`、`shield_amount`、`energy_regen`、`area_of_effect_damage` 和 `default`。该响应没有明确的 KIWI 模式标识，也没有独立海斗版本信息。

本地客户端汉化静态包中检查了 332 个可解析 JSON，包括阿狸与萨勒芬妮英雄资料。英雄对象包含 `id`、`skins`、`passive`、`spells` 等展示资源；两个英雄对象均没有 KIWI 或英雄模式平衡字段。模式相关对象包含 KIWI／KIWI_JADE 增幅列表、地图介绍、召唤师技能适用模式，这些标识只能证明模式资源存在。

客户端 `game-mode-mutators.json` 的结构是 `MapId`、`Mutators`、`MapNameBase`；`Mutators` 中包含 `ExpandedMutator` 与 `MapNameOverride`，用于地图皮肤和名称。这不是各英雄的伤害或治疗修正表。

作为补充检查，以下公开解包资源与本地结果一致：

- [CommunityDragon 英雄 147 JSON](https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/v1/champions/147.json)：没有模式平衡字段。
- [CommunityDragon 模式规则 JSON](https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/v1/game-mode-mutators.json)：只有地图规则资源。
- [CommunityDragon 萨勒芬妮游戏资源](https://raw.communitydragon.org/latest/game/data/characters/seraphine/seraphine.bin.json)：含技能对象与 ARAM 推荐装备覆盖，未发现 KIWI 平衡对象。

CommunityDragon 是社区维护的游戏资源解包镜像，**不是 Riot 官方 API**。`latest` 会更新，部分游戏资源仍使用哈希字段或脚本定义；上述检查不能证明实际游戏中没有模式修正。若后续从这些资源发现数字，仍需确认字段语义、适用模式和客户端版本，不能只根据字段名推断。

本轮没有可连接的运行中 LOL 客户端，因此尚未实时验证 LCU `/lol-game-data/assets/v1/` 是否另有独立 KIWI 数值入口。安装包静态检查与在线 LCU 验证是不同的证据。

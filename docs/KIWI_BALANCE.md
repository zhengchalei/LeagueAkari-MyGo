# 海克斯大乱斗独立平衡数据调查

核验日期：2026-10-06。公开网页与接口均为只读 GET；本地检查只读取游戏安装包中的静态资源，没有访问玩家资料或执行游戏写操作。

## 当前结论

目前未取得可验证的、完整的 **KIWI 海克斯大乱斗英雄增益／减益数值表**，因此本次没有新增数字展示，也没有把普通 ARAM 数值映射成 KIWI。这表示数据源尚未确认，不表示所有英雄均无调整。

普通大乱斗仍使用 OP.GG 的独立 ARAM 平衡数据。海斗已有英雄梯度、海克斯增幅等资源，但这些资源不能用于推算伤害、承伤、治疗或护盾百分比。

## 已核验的来源

| 来源 | 实际可用的数据 | 独立 KIWI 平衡值 |
| --- | --- | --- |
| [OP.GG 海斗英雄列表](https://op.gg/lol/modes/aram-mayhem)与[萨勒芬妮页面](https://op.gg/lol/modes/aram-mayhem/seraphine/build) | 当前 16.19 的英雄梯度、增幅与出装；页面及 Next.js 载荷中未发现独立平衡对象 | 未取得 |
| [OP.GG ARAM 平衡接口](https://lol-api-champion.op.gg/api/contents/aram-balance) | 普通 ARAM 英雄调整表 | 不能视为 KIWI |
| [腾讯英雄资料](https://game.gtimg.cn/images/lol/act/img/js/hero/147.js) | `hero`、`skins`、`spells`、版本信息；英雄基础属性 | 未包含 |
| [腾讯海斗海克斯资源](https://game.gtimg.cn/images/lol/act/img/js/kiwi/kiwi_augments.json) | `augmentID`、中英文名称、等级、说明、图标 | 增幅数据，不是英雄平衡表 |
| [Riot 26.19 更新说明](https://www.leagueoflegends.com/en-us/news/game-updates/league-of-legends-patch-26-19-notes)与[26.18 更新说明](https://www.leagueoflegends.com/en-us/news/game-updates/league-of-legends-patch-26-18-notes) | Mayhem 章节说明增幅调整与修复 | 未提供完整英雄调整表 |
| 本地客户端 `rcp-be-lol-game-data` 静态包 | 英雄、地图、模式增幅列表与召唤师技能资料 | 已检查对象中未包含 |
| CommunityDragon 的客户端与游戏资源镜像 | 客户端英雄 JSON、模式规则、解包英雄技能资源 | 已检查对象中未包含 |

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

## 界面行为建议

海斗选人保持现有英雄与皮肤交互。独立数据缺失时显示“暂无海斗独立增益／减益数据”；不要显示“无调整”，也不要展示普通大乱斗百分比。刷新应重新尝试已确认的数据源，失败保留上次可靠快照；在没有可用源时，刷新结果应明确提示仍未取得数据。

## 完整接入需要什么

至少需要一个能明确区分 KIWI 与普通 ARAM 的数据源，提供英雄 ID、适用版本、各修正属性及其单位／基准值，并能确认其适用于国服当前模式。来源可以是可验证的 OP.GG 独立接口、腾讯公开资源，或客户端实际使用的 KIWI 静态数据／规则。

确认后才应新增独立获取、解析、刷新与缓存，按原有 `ChampionBalance.modes.KIWI` 契约接入；ARAM 数据继续保存在 `modes.ARAM`。离线测试应覆盖两个模式数值不同、属性方向、默认值过滤、缺失字段与刷新失败保留快照，联网核验则检查实际源的模式与版本。无需 OCR 或模拟数值。

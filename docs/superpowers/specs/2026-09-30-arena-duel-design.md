# ArenaDuel Design

## Goal

在 `game/ArenaDuel/` 交付一款接入 **LanHub.Sdk** 的局域网 **1v1 即时俯视对战** 游戏（WPF + Canvas）。  
双方盲选原创角色，每局随机原创场景；主机权威模拟；限时 2 分钟或击杀分胜负。

**GameId：** `arena-duel`（所有机器在 LanHub 游戏库登记同一 ID）

## Stack

- .NET 8 / WPF / Canvas 2D
- 单项目一体：`game/ArenaDuel/`（菜单、选人、战斗、渲染、联网同工程）
- ProjectReference → `src/LanHub.Sdk`
- 角色/场景/技能数值：`Data/Characters.json`、`Data/Arenas.json`

## Scope (V1)

| 有 | 无 |
|----|----|
| 2 人 1v1 局域网联机 | 3+ 人、观战、排位 |
| 8 原创角色，各 普攻 + 2 技能 + 1 大招 | 皮肤商店、成长养成 |
| 5 原创场景，每局主机随机 | 玩家自选场景 |
| 盲选角色（互不可见）+ Ready | 选人超时强制开打（V1 不做） |
| 120s 限时；击杀或血量优势获胜；平局 | 三局两胜 |
| 主机权威 + 输入/快照同步 | 客户端预测、回滚 |
| 色块/剪影表现 | 正版 IP 立绘、复杂骨骼动画 |

## Architecture

```text
game/ArenaDuel/
  ArenaDuel.csproj
  App.xaml / MainWindow.xaml
  Online/
    HubSession.cs          # ConnectFromEnvironment、事件、收发
    NetMessages.cs         # 应用层协议（type + body）
  Data/
    Characters.json
    Arenas.json
  Gameplay/
    CombatSim.cs           # 仅主机：移动、技能、碰撞、伤害、计时、胜负
    InputState.cs
    SkillEffects.cs        # projectile / aoe / dash / buff
  Views/
    LobbyView              # 等满 2 人
    PickView               # 盲选 + Ready
    BattleView             # Canvas + HUD
    ResultView             # 结算 / 再来一局
```

**权威模型：** `hub.IsHost` 的一端跑 `CombatSim`；两端都渲染最新快照。客户端只发送输入。

## Match Flow

```text
LanHub 拉起 exe（注入 LANHUB_* 环境变量）
  → ConnectFromEnvironment
  → Lobby：恰好 2 人进入 Pick（第三人忽略，V1 不观战）
  → Pick：各自选 1 角色 → Ready（不广播对方所选）
  → 双方 Ready 后主机：随机场景 → reliable MatchStart（双方角色Id、场景Id、种子、开局时间）
  → Battle：120s
       · HP≤0 → 立即 MatchEnd
       · 时间到 → HP 高者胜；相同平局
  → Result → 可再来一局（重新盲选 + 再随机场景）
```

**断线：** 战斗中断开 → 短暂停并重连；失败则对方获胜。

## Controls

| 输入 | 作用 |
|------|------|
| WASD | 移动 |
| 鼠标左键 / J | 普攻（自动朝向对手） |
| Q / E | 普通技能 1 / 2（朝鼠标世界方向） |
| C | 大招（朝鼠标世界方向） |

蓝量战斗中缓慢回复。技能需满足：冷却结束、蓝量足够、不在硬直。

## Networking Protocol

Payload 前 1 字节 `type`，其余为 JSON 或紧凑二进制（实现可选 JSON 优先，便于调试）。

| type | 名称 | 信道 | 方向 | 用途 |
|------|------|------|------|------|
| 1 | Input | unreliable | Client→Host | 移动轴、按键、鼠标朝向、序号 |
| 2 | Snapshot | unreliable | Host→All | 实体位置、HP/MP、CD、弹道、剩余时间 |
| 3 | PickReady | reliable | All→Host / Host 确认 | 所选角色Id、Ready |
| 4 | MatchStart | reliable | Host→All | 角色、场景、种子 |
| 5 | MatchEnd | reliable | Host→All | 胜负原因、最终血量 |

输入 / 快照均为 60 Hz。单包控制在 LanHub unreliable 建议上限内（≤1200 字节）。

## Characters (8, original)

每人：**MaxHP / MaxMP / MoveSpeed** 不同；**普攻 + Skill1 + Skill2 + Ultimate(C)**；冷却与耗蓝写入 JSON，互不相同。

| Id | 名 | 定位 | 倾向 |
|----|----|------|------|
| `akishun` | 赤瞬 | 刺客 | 低血高移速；冲刺/标记/C 瞬杀突进 |
| `tiezhang` | 铁嶂 | 坦克 | 高血低移速；护盾/缓速/C 震地 |
| `qinglan` | 青岚 | 法师 | 中血高蓝耗；弹道/击退/落雷/C 岚爆 |
| `shazhi` | 砂织 | 风筝 | 中低血；飞针/陷阱/弹射/C 领域 |
| `baizang` | 白葬 | 续航 | 中血；吸血/禁疗/C 汲取 |
| `yanya` | 焰牙 | 爆发战士 | 中高血；火柱/DOT/C 焰冲锋 |
| `wuyin` | 雾隐 | 控制 | 中血；沉默/定身/C 迷雾减速 |
| `leichan` | 雷忏 | 均衡 | 均衡数值；闪步/链式雷/C 天雷 |

### Skill effect types

| type | 行为 |
|------|------|
| `projectile` | 沿方向飞行，命中或出界销毁 |
| `aoe` | 指定落点/自身周围范围结算 |
| `dash` | 短距位移 + 路径或终点伤害 |
| `buff` | 短时改移速/护盾/状态（沉默、禁疗等） |

普攻可视为短程 `projectile` 或即时射线，自动瞄准对手当前位置。

## Arenas (5, original)

每局主机从下列随机一张；布局含尺寸、障碍多边形、背景主色、出界规则。

| Id | 名 | 玩法要点 |
|----|----|----------|
| `arena_crimson_eaves` | 绯瓦夜巷 | 矮墙巷道，绕点切入 |
| `arena_mirror_span` | 镜渊天桥 | 窄桥；出界弹回或小额坠伤 |
| `arena_twin_cataract` | 双瀑裂谷 | 左右高台 + 中央低地 |
| `arena_obsidian_ring` | 玄晶斗环 | 圆形开阔，技能对波 |
| `arena_pale_monoliths` | 苍碑荒原 | 多石碑挡弹道，绕柱风筝 |

视觉：Canvas 色块/剪影，动漫对决氛围；**不使用任何现有动漫 IP 地名或角色名**。

## UI Pages

1. **Lobby** — 连接状态、本机是否主机、人数 0/2→2/2  
2. **Pick** — 8 人卡片（血蓝简介 + 技能名）；Ready；不显示对方选择  
3. **Battle** — 全屏战场；顶栏倒计时；两侧血蓝条；底栏 Q/E/C CD 与蓝耗提示；场景名  
4. **Result** — 胜/负/平、双方剩余 HP、再来一局 / 退出  

## Error Handling

- SDK `Failed` / `SessionEnded`：回 Lobby 或退出并提示  
- `Reconnecting`：战斗暂停提示「重连中」  
- 非法输入（无蓝放技能）：主机忽略，不惩罚  
- 仅 1 人：停在 Lobby，不开 Pick  

## Testing (manual V1)

1. 两台机（或本机双开若平台允许）登记 `arena-duel`，开房开始  
2. 盲选不同角色 → Ready → 确认随机场景一致  
3. 移动/普攻/Q/E/C 冷却与耗蓝符合 JSON  
4. 击杀与 120s 血量胜负、平局  
5. 战斗中断网 → 重连或判负  

## Out of V1

选人超时、观战、回放、排行榜、本地 AI 人机、自定义键位 UI、音频资源包（可后补简单音效）。

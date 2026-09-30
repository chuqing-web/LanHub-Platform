# LanHub

[English](README.md) | **中文**

**用自己的想法，做属于自己的局域网联机游戏。**

LanHub 是同一 Wi‑Fi 下的局域网派对平台 + **LanHub.Sdk**：你构思玩法并接入 SDK，同网好友通过主机开房即可联机。**只有接入 SDK 的游戏才能通过平台互联**；客户端负责开房、发现、校验房间号、拉起进程，游戏内联机一律走 SDK。

![LanHub 大厅](picture/Lanhub%20Homepage.png)

---

## 亮点

- 把个人游戏想法变成可玩的局域网局，无需自建大厅协议栈
- 主机 / 客户端房间、六位房间号、加密信令与加密游戏 UDP
- 可靠 + 不可靠信道（TCP 中继 / UDP P2P，失败则主机转发）
- 面向 .NET 的 C# SDK；游戏库用统一 `GameId` 登记任意 exe
- `game/` 下提供示例程序，可对照完成接入

### 界面截图

| 游戏库 | 设置 |
|:------:|:----:|
| ![游戏库](picture/Game%20Library.png) | ![设置](picture/Settings.png) |

---

## 下载体验

预编译 Windows 包已发布在 **[GitHub Releases](https://github.com/chuqing-web/LanHub-Platform/releases/latest)**（v1.0.0）。

| 资源 | 内容 | GameId |
|------|------|--------|
| [LanHub.zip](https://github.com/chuqing-web/LanHub-Platform/releases/download/v1.0.0/LanHub.zip) | 启动器 | — |
| [Lanchat.zip](https://github.com/chuqing-web/LanHub-Platform/releases/download/v1.0.0/Lanchat.zip) | 示例局域网聊天 | `lan-chat` |
| [ArenaDuel.zip](https://github.com/chuqing-web/LanHub-Platform/releases/download/v1.0.0/ArenaDuel.zip) | 示例 1v1 对战 | `arena-duel` |

1. 解压 `LanHub.zip`，运行 `LanHub.exe`（Windows 10/11 x64）。
2. 解压示例包；在 **游戏库 → 添加** 中登记各 `.exe`，GameId 填上表（每台电脑必须一致）。
3. 主机开房，客人用六位房间号加入。
4. 双方从游戏库打开 **同一 GameId** — 不要在资源管理器里直接双击 exe。

---

## 从源码构建

用 **Visual Studio 2022** 打开 `LanHub.sln`（.NET 8）。

```bat
dotnet build LanHub.sln -c Release
dotnet run --project src\LanHub\LanHub.csproj
```

| 用途 | 路径 |
|------|------|
| 启动器 | `src\LanHub\bin\Release\net8.0-windows\LanHub.exe` |
| 给游戏引用的 DLL | `src\LanHub.Sdk\bin\Release\net8.0\LanHub.Sdk.dll`（依赖同目录的 `LanHub.Core.dll`） |
| SDK 自测控制台 | `src\LanHub.Sdk.Probe\bin\Release\net8.0\LanHub.Sdk.Probe.exe` |

---

## 项目结构

| 项目 | 说明 |
|------|------|
| `LanHub` | WPF 启动器（主机/客户端、游戏库、设置） |
| `LanHub.Core` | 发现、房间、加密、本机 SDK 端点、UDP P2P |
| `LanHub.Sdk` | **游戏必须引用的 DLL** |
| `LanHub.Sdk.Probe` | 开发自测（不是游戏） |
| `game/LanChat` | 示例：基于 SDK 的局域网聊天 |
| `game/ArenaDuel` | 示例：基于 SDK 的 1v1 俯视对战 |

默认端口：发现 UDP `37810`、房间 TCP `37811`、本机 SDK `37812`、游戏 UDP `37813`。

---

## 示例程序（`game/`）

以下为**测试 / 参考程序**，不是平台本体。用于端到端验证联机，也可作为接入模板。预编译包见 [Releases](https://github.com/chuqing-web/LanHub-Platform/releases/latest)；源码在 `game/`。

### LanChat（`game/LanChat`）

基于 LanHub.Sdk 的局域网群聊（含私信）。覆盖环境变量连接、花名册同步、可靠文本消息、输入中提示与重连体验。

![LanChat](picture/LanChat.png)

| | |
|--|--|
| **GameId** | `lan-chat` |
| **适合** | 学习最小可靠消息接入 |

### ArenaDuel（`game/ArenaDuel`）

1v1 俯视竞技场对战：盲选角色、主机权威战斗、技能输入，以及可靠 + 不可靠信道上的状态同步。

![ArenaDuel](picture/ArenaDuel.png)

| | |
|--|--|
| **GameId** | `arena-duel` |
| **适合** | 学习主机权威、输入同步与高频快照 |

在每台机器的 LanHub 游戏库用对应 GameId 登记编译出的 exe，进房后从游戏库启动（不要在资源管理器里直接双击）。

---

## 传输模型

| 信道 | 路径 | 适用数据 |
|------|------|----------|
| **reliable** | 游戏 → 本机 LanHub（TCP）→ 房间 TCP（AES-GCM）→ 对端 | 操作指令、聊天、局内关键事件 |
| **unreliable** | 游戏 → 本机 LanHub → **UDP P2P**（失败则主机中继） | 位置、朝向、高频快照 |

不可靠单包建议 ≤ **1200** 字节；可靠单包上限默认 **256 KiB**（以 `Session.MaxPayloadBytes` 为准）。

---

## 游戏接入 SDK（完整指南）

### 0. 适配前先搞清分工

| 谁负责 | 做什么 |
|--------|--------|
| **LanHub 启动器** | 开房 / 发现 / 房间号 / 绑定 GameId / 注入环境变量 / 从游戏库拉起 `exe` |
| **你的游戏 + SDK** | 连接本机运行时、同步玩家列表、收发联机数据、实现玩法 |
| **不要做的事** | 游戏里自己再搜一遍局域网开房；不要绕过 SDK 直连别人的游戏端口（除非你另有需求，将无法享受平台房间与安全） |

```text
主机开房 → 客人加入同一房间
  → 双方在「游戏库」双击打开同一款游戏（GameId 必须一致）
  → 主机打开时绑定本局 GameId，客人打开同款时注入 Token
  → 游戏调用 LanHub.Sdk 连接本机 127.0.0.1
  → 之后所有联机消息经平台中继 / UDP P2P
```

### 1. 在启动器里登记游戏（必做）

1. 打开 **LanHub** → **游戏库** → **添加**，选中你的游戏 `.exe`。
2. 点 **编辑**，确认字段：

| 字段 | 含义 | 要求 |
|------|------|------|
| **标题** | 显示名称 | 任意 |
| **GameId** | 游戏唯一 ID | 所有玩家机器上 **必须相同**（建议自己定死，如 `mygame-demo`） |
| **Exe 路径** | 本机可执行文件 | 每台电脑路径可以不同 |
| **启动参数** | 附加命令行 | 可选；平台联机 **不依赖** 参数，靠环境变量 |

> 调试阶段：可用 `LanHub.Sdk.Probe` + GameId=`probe` 验证联机，不必先有真正游戏。

### 2. 在游戏工程中引用 SDK（必做）

#### 方式 A：项目引用（推荐，与本仓库同解决方案）

```xml
<ItemGroup>
  <ProjectReference Include="..\..\src\LanHub.Sdk\LanHub.Sdk.csproj" />
</ItemGroup>
```

（按实际相对路径修改。目标框架建议 **.NET 8**。）

#### 方式 B：直接引用 DLL

Release 编译后，把 `LanHub.Sdk.dll` 与 `LanHub.Core.dll` 拷到游戏输出目录（或固定 `libs/`），再引用：

```xml
<ItemGroup>
  <Reference Include="LanHub.Sdk">
    <HintPath>libs\LanHub.Sdk.dll</HintPath>
  </Reference>
</ItemGroup>
```

运行时请保证 `LanHub.Core.dll` 与 `LanHub.Sdk.dll` **同目录**。

#### Unity

- 可将编译好的 DLL 放入 `Assets/Plugins`（需脚本后端能加载 .NET 8 程序集）。
- 连接与收发请在异步/后台处理；把 `MessageReceived` 派发回主线程再改场景物体。

### 3. 启动时连接平台（必做）

**正确姿势：进房后从 LanHub「游戏库」双击打开游戏**。资源管理器直接双击没有环境变量，进不了平台联机。

| 变量 | 含义 |
|------|------|
| `LANHUB_TOKEN` | 本局一次性会话令牌 |
| `LANHUB_GAME_ID` | 与游戏库中的 GameId 一致 |
| `LANHUB_PLAYER_ID` | 本机玩家 ID（全房唯一） |
| `LANHUB_SDK_PORT` | 本机 SDK 端口（默认 37812） |

```csharp
using LanHub.Sdk;

public sealed class OnlineSession : IAsyncDisposable
{
    private LanHubClient? _hub;

    public async Task StartAsync()
    {
        _hub = await LanHubClient.ConnectFromEnvironmentAsync();

        _hub.StateChanged += OnStateChanged;
        _hub.RosterUpdated += OnRosterUpdated;
        _hub.PlayerJoined += p => Debug.Log($"加入: {p.DisplayName}");
        _hub.PlayerLeft += p => Debug.Log($"离开: {p.DisplayName}");
        _hub.SessionUpdated += OnSessionUpdated;
        _hub.MessageReceived += OnMessage;
        _hub.Error += (code, msg) => Debug.LogWarning($"SDK {code}: {msg}");
        _hub.Disconnected += (code, msg) => Debug.LogWarning($"断开 {code}: {msg}");

        BootstrapGameplay(_hub);
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub != null)
            await _hub.DisposeAsync();
    }
}
```

手动调试（不推荐上线路径）：

```csharp
var hub = new LanHubClient();
await hub.ConnectAsync(sdkPort: 37812, token, gameId, playerId);
```

### 4. 会话与主机权威（强烈建议）

```csharp
var me = hub.PlayerId;
var session = hub.Session;

session.SessionId;
session.HostPlayerId;
session.IsHost;       // 等价 hub.IsHost
session.Transport;
session.Peers;
session.MaxPayloadBytes;
```

推荐玩法架构：

1. **主机权威**：`hub.IsHost == true` 的一端模拟世界、判定胜负、广播快照。
2. **客户端**：只发输入，收主机状态再表现。
3. 用 `RosterUpdated` / `PlayerJoined` / `PlayerLeft` 维护本地玩家实体。
4. 断线后若 `State == Reconnecting`，暂停模拟；`Failed` / `SessionEnded` 则退回菜单。

### 5. 收发消息（必做）

SDK 只传 **`byte[]`**。序列化格式由游戏自己定。

建议在 payload 最前面加 1 字节消息类型：`[type:u8][body...]`。

```csharp
await hub.SendToAllAsync(bytes);                    // 可靠广播
await hub.SendTextAsync("hello");                   // UTF-8 可靠（调试）
await hub.SendToAsync(targetPlayerId, bytes);       // 可靠单播
await hub.SendUnreliableToAllAsync(posBytes);       // UDP P2P

_hub.MessageReceived += msg =>
{
    // msg.FromPlayerId、msg.Channel、msg.Payload、msg.AsUtf8Text() …
    HandlePacket(msg.FromPlayerId, msg.Payload);
};
```

### 6. 连接生命周期与「完全适配」清单

| 事件 / 状态 | 游戏应做 |
|-------------|----------|
| `Connected` | 进入联机局、刷新花名册 |
| `Reconnecting` | 显示重连中，暂停关键操作 |
| `Disconnected` / `Failed` | 清理局内对象 |
| `PayloadTooLarge` | 减小包或拆包 |
| `InvalidToken` | 不是从 LanHub 启动，或会话已过期 |

退出时务必 `await hub.DisposeAsync()`。

**完全适配检查清单：**

- [ ] 工程引用 `LanHub.Sdk`（并能加载 `LanHub.Core`）
- [ ] **仅通过** `ConnectFromEnvironmentAsync()`（或等价环境变量）连接
- [ ] 在 LanHub **游戏库**登记，且多机 **GameId 一致**
- [ ] 启动后订阅 `MessageReceived`、`RosterUpdated`、`StateChanged`、`Disconnected`
- [ ] 用 `IsHost` / `HostPlayerId` 实现主机权威或明确的锁步方案
- [ ] 关键数据走 **reliable**，高频表现数据走 **unreliable**
- [ ] 处理重连与会话结束
- [ ] 退出时 `DisposeAsync`

### 7. 推荐的最小可运行示例（控制台）

```csharp
using System.Text;
using LanHub.Sdk;

await using var hub = await LanHubClient.ConnectFromEnvironmentAsync();
Console.WriteLine($"我是 {hub.PlayerId} host={hub.IsHost}");

hub.MessageReceived += m =>
    Console.WriteLine($"[{m.Transport}/{m.Channel}] {m.FromPlayerId}: {m.AsUtf8Text()}");

hub.RosterUpdated += list =>
    Console.WriteLine("人数 " + list.Count);

while (true)
{
    var line = Console.ReadLine();
    if (string.IsNullOrEmpty(line)) break;
    if (line.StartsWith("/u "))
        await hub.SendUnreliableToAllAsync(Encoding.UTF8.GetBytes(line[3..]));
    else
        await hub.SendTextAsync(line);
}
```

### 常见问题

**Q: 在资源管理器里双击游戏 exe 能联机吗？**  
A: 不能。没有 `LANHUB_TOKEN`，SDK 会握手失败。请先加入房间，再在 LanHub **游戏库**里打开。

**Q: 两边都启动了，但收不到消息？**  
A: 检查是否同一房间；GameId 是否一致；是否从游戏库启动；防火墙是否放行游戏 UDP `37813`；本机是否误开两个 LanHub 抢同一端口。

**Q: 同一台电脑测两个客户端？**  
A: 可以。两个 LanHub 实例在 **设置** 里使用不同的 TCP / SDK / GameUDP 端口；两套游戏库各自登记 exe。

**Q: 能否不改游戏、只包装现有联机游戏？**  
A: 不能「自动」适配。未引用 SDK 的成品游戏无法走平台联机。

---

## 快速自测

1. 两边都在 **游戏库** 添加同一示例（`LanChat` / `ArenaDuel`，GameId 见上表）
2. 主机：主机模式 → **开房** → 记下房间号
3. 客人：发现主机 → 输入房间号 → **加入**
4. 主机在游戏库 **双击** 打开该游戏；客人再双击打开 **同一款**
5. 游戏内应显示已连接 / 房间人数；聊天或对战即可互通

用 Probe 自测时：进房后查看状态栏 Token / PlayerId，手动填入 `LanHub.Sdk.Probe`（GameId=`probe`）。普通输入 = 可靠 TCP；`/u 文本` = 不可靠 UDP；`/players` 查看花名册。

---

## 安全说明

- 房间号不出现在 UDP 发现广播中
- 进房：HMAC 证明 + HKDF 会话密钥 + AES-GCM
- 进房按 IP 限速；SDK 仅监听 `127.0.0.1`，需一次性 Token
- 游戏 UDP 载荷使用同一会话密钥密封

---

## 尚未支持

- 语音、成就、云存档、商店下载
- 官方 Unity Package / C++ SDK（C# DLL 可先用）
- 未接入 SDK 的第三方成品游戏的自动联机劫持

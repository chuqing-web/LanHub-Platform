# ArenaDuel Implementation Plan

> **For agentic workers:** Implement task-by-task. Steps use checkbox syntax.

**Goal:** 在 `game/ArenaDuel/` 落地可 VS2022 / `dotnet build` 的 WPF 1v1 即时对战，接入 LanHub.Sdk。

**Architecture:** 单项目 WPF；主机 `CombatSim` 权威；客户端发 Input、收 Snapshot；JSON 驱动角色与场景。无 Token 时可本地调试对战。

**Tech Stack:** .NET 8, WPF, Canvas, System.Text.Json, LanHub.Sdk

---

### Task 1: 工程脚手架
- [ ] 创建 `game/ArenaDuel/ArenaDuel.csproj`，引用 Sdk，Copy JSON
- [ ] 加入 `LanHub.sln`，`dotnet build` 通过空壳

### Task 2: 数据与模型
- [ ] `Characters.json`（8 人）+ `Arenas.json`（5 图）
- [ ] C# 数据模型与加载器

### Task 3: 战斗模拟
- [ ] `CombatSim`：移动、障碍、普攻、Q/E/C、蓝耗 CD、120s、胜负
- [ ] 投射物 / AOE / dash / buff

### Task 4: 联网协议 + HubSession
- [ ] Msg types + JSON payload
- [ ] 连接环境变量；主机/客机角色

### Task 5: UI 流程
- [ ] Lobby / Pick / Battle / Result
- [ ] 本地调试模式（无 LanHub 也可编译运行测战斗）

### Task 6: 编译验证
- [ ] `dotnet build game/ArenaDuel -c Release` 成功

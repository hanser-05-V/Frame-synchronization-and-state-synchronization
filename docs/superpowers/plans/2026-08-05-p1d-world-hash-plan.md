# P1-D World Hash Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为完整帧快照提供稳定的 64 位 Hash，并在正常帧及回滚完成后输出双端可比较的最终状态日志。

**Architecture:** 新增纯 `WorldHash` 深模块，对 `FrameSnapshot` 按 schema 版本和固定小端字段顺序执行 FNV-1a 64-bit。`PredictionSystem` 返回/读取已保存快照，`GameController` 只负责最终时点和日志上下文，不把展示或网络状态混入 Hash。

**Tech Stack:** Unity 2022.3.62f2、C#、NUnit EditMode、FNV-1a 64-bit。

---

### Task 1: 固化纯 Hash 接口

**Files:**
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/WorldHashTests.cs`
- Create: `Project/Frame Synchronization/Assets/Scripts/FrameSync/WorldHash.cs`

- [ ] **Step 1: 写入失败测试**

测试构造完整固定快照，要求 `WorldHash.SchemaVersion == 1`、`AlgorithmName == "FNV1A64"`，并锁定参考值 `0x38A103CB9DCCFC1E`。再逐字段改变 `frameID`、两名玩家位置/朝向/状态/球权及篮球位置/速度/状态/持有者，要求 Hash 变化；交换玩家槽位也必须变化。

- [ ] **Step 2: 运行定向测试并确认 RED**

```powershell
& "C:\Unity\unity2022\Editor\Unity.exe" -projectPath "E:\帧同步_RouteC\Project\Frame Synchronization" -batchmode -runTests -runSynchronously -testPlatform EditMode -testFilter FrameSyncDemo.Tests.WorldHashTests -testResults "E:\帧同步_RouteC\Project\Frame Synchronization\TestResults-P1D-Red.xml" -logFile "E:\帧同步_RouteC\Project\Frame Synchronization\P1D-Red.log"
```

预期：编译失败，原因是 `WorldHash` 尚不存在。禁止添加生产代码前跳过此证据。

- [ ] **Step 3: 实现最小纯模块**

```csharp
public static class WorldHash
{
    public const int SchemaVersion = 1;
    public const string AlgorithmName = "FNV1A64";

    public static ulong Compute(FrameSnapshot snapshot)
    {
        ulong hash = 14695981039346656037UL;
        AddInt32(ref hash, SchemaVersion);
        AddInt32(ref hash, snapshot.frameID);
        AddInt32(ref hash, snapshot.player1X._raw);
        AddInt32(ref hash, snapshot.player1Y._raw);
        AddInt32(ref hash, snapshot.player1Z._raw);
        AddInt32(ref hash, snapshot.player2X._raw);
        AddInt32(ref hash, snapshot.player2Y._raw);
        AddInt32(ref hash, snapshot.player2Z._raw);
        AddInt32(ref hash, snapshot.player1FacingX._raw);
        AddInt32(ref hash, snapshot.player1FacingY._raw);
        AddInt32(ref hash, snapshot.player1FacingZ._raw);
        AddInt32(ref hash, snapshot.player2FacingX._raw);
        AddInt32(ref hash, snapshot.player2FacingY._raw);
        AddInt32(ref hash, snapshot.player2FacingZ._raw);
        AddInt32(ref hash, snapshot.player1State);
        AddInt32(ref hash, snapshot.player2State);
        AddInt32(ref hash, snapshot.player1HasBall ? 1 : 0);
        AddInt32(ref hash, snapshot.player2HasBall ? 1 : 0);
        AddInt32(ref hash, snapshot.ballPosX._raw);
        AddInt32(ref hash, snapshot.ballPosY._raw);
        AddInt32(ref hash, snapshot.ballPosZ._raw);
        AddInt32(ref hash, snapshot.ballVelX._raw);
        AddInt32(ref hash, snapshot.ballVelY._raw);
        AddInt32(ref hash, snapshot.ballVelZ._raw);
        AddInt32(ref hash, snapshot.ballState);
        AddInt32(ref hash, snapshot.ballHolder);
        return hash;
    }
}
```

`AddInt32` 必须把 `int` 按无符号 32 位、小端四字节逐一执行 XOR 和 `* 1099511628211UL`，放在 `unchecked` 块中。

- [ ] **Step 4: 运行定向测试并确认 GREEN**

使用同一命令和独立结果文件，预期 `WorldHashTests` 全部通过。

### Task 2: 暴露已保存的完整快照

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/PredictionSystemSnapshotTests.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/FrameSync/PredictionSystem.cs`

- [ ] **Step 1: 写入失败测试**

新增测试要求：

```csharp
FrameSnapshot captured = prediction.TakeWorldSnapshot(37, players, ball);
Assert.AreEqual(37, captured.frameID);
Assert.IsTrue(prediction.TryGetWorldSnapshot(37, out FrameSnapshot stored));
Assert.AreEqual(WorldHash.Compute(captured), WorldHash.Compute(stored));
Assert.IsFalse(prediction.TryGetWorldSnapshot(404, out FrameSnapshot missing));
Assert.IsFalse(missing.IsValid);
```

- [ ] **Step 2: 运行测试并确认 RED**

预期：当前 `TakeWorldSnapshot` 返回 `void`，且不存在 `TryGetWorldSnapshot`。

- [ ] **Step 3: 实现最小接口**

将 `TakeWorldSnapshot` 返回类型改为 `FrameSnapshot`，写入缓冲后返回同一值。新增 `TryGetWorldSnapshot`：存在则返回缓冲值；缺失则返回 `new FrameSnapshot { frameID = -1 }` 和 `false`，不得改变世界。

- [ ] **Step 4: 运行 `PredictionSystemSnapshotTests` 并确认 GREEN**

预期该 fixture 全部通过。

### Task 3: 证明回滚后的 Hash 追平

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/FrameReplaySystemTests.cs`

- [ ] **Step 1: 写入集成测试**

沿用现有权威世界和预测世界：先让预测路径使用错误远端输入并断言最终快照 Hash 不同，再执行 `FrameReplaySystem.Replay`，读取最后帧重建快照，断言其 Hash 与权威同帧快照相同。

- [ ] **Step 2: 运行测试并确认当前集成行为**

该测试依赖 Task 1/2 的新接口；预期在日志尚未接入前已经验证纯回滚追平行为。若失败，修正快照读取或测试构造，不改变现有回滚算法。

- [ ] **Step 3: 运行 `FrameReplaySystemTests`**

预期该 fixture 全部通过，既有世界逐字段断言保持不变。

### Task 4: 接入最终时点日志

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Scripts/GameController.cs`

- [ ] **Step 1: 移除旧活动调用**

删除 `OnFrameUpdate` 末尾的 `MD5Checker.CheckAndLog` 调用，但保留 `MD5Checker.cs` 文件。

- [ ] **Step 2: 增加日志配置和正常帧接入**

新增 `[SerializeField] private int _worldHashLogIntervalFrames = 200;`。`OnPostFrameUpdate` 保存并接收快照；当 `_pendingRollback == false` 且帧号命中间隔时输出：

```text
[RouteC][WorldHash] schema=1 algo=FNV1A64 localPlayer={index} frame={frame} phase=final source=normal hash=0x{hash:X16}
```

间隔小于等于零时关闭正常日志。

- [ ] **Step 3: 增加回滚完成接入**

`FrameReplaySystem.Replay` 成功后读取 `lastExecutedFrame` 快照并强制输出回滚日志，包含 `errorFrame`、`restoredFrame`、`replayedFrameCount`。失败或快照缺失时不输出最终 Hash。

- [ ] **Step 4: 编译并运行相关测试**

预期无编译错误，WorldHash、快照和回滚 fixture 全部通过。

### Task 5: 全量验证与审查

**Files:**
- Verify: all changed files

- [ ] **Step 1: 运行完整 EditMode**

命令必须包含 `-runTests -runSynchronously` 且不能带 `-quit`。预期失败 `0`、跳过 `0`、编译错误 `0`。

- [ ] **Step 2: 静态差异检查**

```powershell
git -C "E:\帧同步_RouteC" diff --check
git -C "E:\帧同步_RouteC" status --short
```

预期无空白错误，P0～P1-C 既有成果仍全部存在。

- [ ] **Step 3: 独立代码审查**

审查字段覆盖、字节序、回滚时点、日志抑制、旧路径兼容和测试证据。所有 Critical/Important 必须修复并重新验证。

- [ ] **Step 4: 隔离复核**

只读复核 `E:\帧同步` 的 `main`、指定 HEAD、3 条状态和 SHA256 指纹，必须与交接值一致。

## 仓库约束

本计划不执行 commit、merge、reset、clean、checkout 覆盖或 push。所有变更只发生在 `E:\帧同步_RouteC`。

## 验收修正任务（2026-08-05）

### Task 6: 精确历史帧预测校验

- [x] 先增加 `静止 → 短暂移动 → 静止` 的漏检回归测试，要求返回短暂移动所在的历史本地帧及其精确真值。
- [x] 增加同批多个历史错误的测试，要求只返回最早错误帧，并由一次回滚覆盖后续污染。
- [x] 改造 `ResolveRemote`，由调用方提供“本地帧 → 该帧远端真值”的只读查询；共同时间线启用后，帧 0 冷启动预测也按精确真值校验。
- [x] 运行 `PredictionSystemResolveRemoteTests`，确认先 RED 后 GREEN。

### Task 7: 统一世界帧 Hash schema 2

- [x] 增加 Player0/Player1 本地帧映射到同一世界帧的测试，以及未锚定和非法玩家编号边界测试。
- [x] `WorldHash.Compute` 接受统一世界帧；两份动态状态相同但本地帧不同的快照，在同一世界帧下必须得到相同 Hash。
- [x] 正常帧与回滚日志输出 `frame={统一世界帧}` 和 `localFrame={本地帧}`，并将 schema 升为 `2`。
- [x] 运行相关定向测试、完整 EditMode、静态差异检查和独立审查，再交给帅老大双端人工复验。

### Task 8: 双客户端共同起跑

**Files:**
- Modify: `Project/NetworkServer/NetworkServer.cs`
- Modify: `Project/NetworkServer/NetworkServer.exe`
- Modify: `Project/Frame Synchronization/Assets/Scripts/GameController.cs`
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/CanonicalFrameTests.cs`

- [x] **Step 1: 建立 RED 证据**

验证旧服务器会在只有第一个客户端连接时立即发送玩家编号；共同起跑要求此时读取超时，第二个客户端连接后两端才分别收到 `0/1`。

- [x] **Step 2: 实现双客户端放行**

服务器先完成两次 `AcceptTcpClient` 并保存连接，再依次发送玩家编号和启动接收线程。客户端收到编号后启动帧引擎，并将远端帧偏移固定为 `0`；删除首包估算偏移路径。

- [x] **Step 3: 重建和自动验证服务器**

使用 `dotnet build Project/NetworkServer/NetworkServer.csproj -c Release`，将生成物更新到版本库中的 `Project/NetworkServer/NetworkServer.exe`。运行双 socket 自动检查，要求首客户端在第二客户端加入前收不到编号，加入后收到 `0/1`。

### Task 9: 回滚请求与输入边界

**Files:**
- Create: `Project/Frame Synchronization/Assets/Scripts/FrameSync/RollbackRequestBuffer.cs`
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/RollbackRequestBufferTests.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/GameController.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/Gameplay/FrameReplaySystem.cs`
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/FrameReplaySystemTests.cs`

- [x] **Step 1: 写入并运行 RED 测试**

测试连续请求晚错误帧和早错误帧时只取最早帧及其真值；测试重演前置帧只有单元素输入时返回 `missingInputFrame`，而不是读取远端槽位越界。

- [x] **Step 2: 实现最小修复并运行 GREEN**

`GameController` 通过 `RollbackRequestBuffer` 聚合待处理错误；`FrameReplaySystem` 在读取任一玩家槽位前验证输入数组长度。运行两个 fixture 并确认通过。

- [x] **Step 3: 完整验证和独立审查**

运行完整 EditMode、服务器共同起跑检查、`git diff --check`、RouteC 分支/HEAD 与旧项目状态指纹复核。所有 Critical/Important 清零后才进入双端人工验收。

# 003 — FrameEngine 帧循环引擎

> 📊 **本篇配图：**
> - [003-FrameEngine双线程模型.html](../charts-output/003-FrameEngine双线程模型.html) — 逻辑线程 + 网络线程 + 主线程的三线协作模型

## 一句话定位

`FrameEngine` 是整个帧同步的**心跳引擎**——以 30FPS（33ms/帧）固定速率驱动战斗逻辑，通过**逻辑线程 + 网络线程分离**解耦计算和通信。所有帧同步操作（读 Input、跑逻辑、收发包）都在它的节奏下进行。

---

## 设计意图

### 为什么需要独立的 FrameEngine？

Unity 的 `Update()` 帧率不固定（受渲染负担影响），而帧同步要求**逻辑帧间隔严格恒定**。如果活逻辑和渲染耦合在一起：
- 渲染卡顿 → 逻辑帧漏掉 → 不同步
- 每帧的 `deltaTime` 不同 → 确定性被破坏

**解决方案**：FrameEngine 维护自己的固定时钟，不受渲染帧率影响。

### 为什么逻辑和网络分线程？

```
❌ 单线程模型：
  EngineUpdate → 跑逻辑(3ms) → 收发包(50ms网络延迟) → 跑逻辑(3ms) → ...
  ↑ 网络延迟会阻塞逻辑帧，一帧卡住后面全卡

✅ 双线程模型：
  逻辑线程：跑逻辑(3ms) → 跑逻辑(3ms) → 跑逻辑(3ms) → ... 永远 30FPS
  网络线程：收发包(50ms) → ...                             独立频率
  ↑ 网络卡顿不影响逻辑帧率（但可能触发追帧）
```

---

## 接口层 — 暴露了什么

```csharp
// E:\帧同步\Common\Battle\FrameEngine.cs:9-149
public class FrameEngine
{
    // ----- 生命周期 -----
    + StartEngine(FixedNumber frameInterval, bool allowMultiThread=true)
      // 启动引擎：创建逻辑线程 + 网络线程
    + StopEngine()
      // 停止引擎：Join 线程 + 清理 EntitySystem
    + Pause { get; set; }

    // ----- 帧速率 -----
    + static frameInterval : FixedNumber    // 只读，通常 = 33ms
    + timeScale { get; set; }               // 时间缩放（默认 1.0）

    // ----- 回调注册 -----
    + RegisterFrameUpdateListener(Action)   // 注册逻辑更新回调
    + RegisterNetUpdateListener(Action)     // 注册网络更新回调

    // ----- 单线程兼容 -----
    + Update()    // allowMultiThread=false 时，在 Unity 主线程调
}
```

> 📂 **[点此打开 UML 图](../charts-output/003-FrameEngine双线程模型.html)**

---

## 设计逻辑（核心）

### 双线程架构

```
┌─────────────────────────────────────────────────────────┐
│  Unity 主线程 (60FPS)                                    │
│  │  RenderUpdate() — 读取 MatchEntity 快照 → 插值渲染    │
│  │  完全独立于逻辑线程，不等待                             │
└─────────────────────────────────────────────────────────┘

┌────────────────────────────────────────────────┐
│  逻辑线程 (LogicThread, 后台线程)                │
│  while (!_threadStop) {                        │
│      if (!Pause) _frameUpdateListeners();      │
│      Thread.Sleep(1);  ← 1ms 精度轮询          │
│  }                                              │
│                                                 │
│  frameUpdateListeners 注册的是:                  │
│    → BattleManager.EngineUpdate()               │
│      → IBattleController.LogicUpdate()          │
│        → 按 33ms 间隔执行一帧逻辑               │
└────────────────────────────────────────────────┘

┌────────────────────────────────────────────────┐
│  网络线程 (NetThread, 后台线程)                  │
│  while (!_threadStop) {                        │
│      if (!Pause) _netUpdateListeners();        │
│      Thread.Sleep(1);                          │
│  }                                              │
│                                                 │
│  netUpdateListeners 注册的是:                    │
│    → RemoteBattleNetworkController              │
│      → Send: 本地 Input → KCP → Server         │
│      → Recv: Server → KCP → _frameQueue        │
└────────────────────────────────────────────────┘
```

> 💡 **设计意图**：`Thread.Sleep(1)` ——不是精确的 1ms 定时器，而是"让出 CPU 时间片"。实际调度粒度取决于操作系统（Windows 默认 ~15ms），但因为逻辑帧在回调内部自己做 `累计时间 ≥ 33ms` 判断，所以线程休眠精度不影响帧间隔的准确性。

### 帧间隔的精确控制不在 FrameEngine 内部

```csharp
// FrameEngine 本身不维护帧间隔——它只管不停回调
// 帧间隔的精确控制在 LogicUpdate() 内部：

// E:\帧同步\Script\Battle\ClientOnly\Manager\LocalBattleController.cs:138
while (startMillSecondes - _lastMilliseconds >= BattleSetting.FrameInterval)
{
    _lastMilliseconds += BattleSetting.FrameInterval;  // 累加 33ms
    // ... 执行一帧逻辑
}
```

> 💡 **设计意图**：FrameEngine 提供"心跳"（不停调回调），但**帧间隔的精确控制在消费端自己实现**。这样设计的好处是：
> - FrameEngine 本身极简（核心逻辑 < 50 行）
> - 消费端可以灵活处理追帧（时间差 >= 66ms → 一次跑 2 帧）
> - 暂停/时间缩放只需改 `Pause`/`timeScale` 标记

### 为什么用 Thread 而非 Task/Coroutine？

```csharp
_logicThread = new Thread(new ThreadStart(LogicThreadUpdate));
_logicThread.IsBackground = true;  // ← 后台线程，主线程退出时自动结束
_logicThread.Start();
```

- **Thread**：逻辑需要独立于 Unity 主循环运行，`Task` 默认用线程池可能被 Unity 阻塞
- **不是 Coroutine**：协程跑在主线程，渲染卡顿会阻塞协程
- `IsBackground = true`：Unity 退出时自动回收线程，防止进程残留

### 线程安全

FrameEngine 本身**不做锁**。它在逻辑线程回调中执行游戏逻辑（读取 `FrameBuffer`），网络线程回调中收发网络包（写入 `_frameQueue`）。真正的线程安全在：
- `RemoteBattleController._inputLock` — 保护帧数据读写
- `FrameBuffer` 的 `SyncFrame` / `TryGetFrame` — 不跨线程调用同一帧

---

## ⚠️ 实践注意

1. **逻辑线程不是精确 33ms 定时器**——`Thread.Sleep(1)` 出让 CPU，实际调度可能 1-15ms。帧间隔的精确控制依赖 `LogicUpdate()` 内部的 `while (时间差 >= 33ms)` 累加逻辑。
2. **追帧**——如果逻辑线程被系统调度延迟了 100ms，`LogicUpdate()` 的 while 循环会一次跑 3 帧追赶。每帧 `_lastMilliseconds += 33`，不会因为追帧而"跳过"帧号。
3. **`timeScale` 不是暂停**——`timeScale = 0` 时 `deltaTime = FrameEngine.frameInterval * 0 = 0`，帧号仍然递增但逻辑不推进。暂停用 `Pause = true`。
4. **单线程模式（`allowMultiThread=false`）**——编辑器调试用的 fallback，主线程手动调 `Update()`，避免多线程调试困难。
5. **StopEngine 的 Join**——`_logicThread.Join()` 是阻塞等待线程退出。确保在 Unity `OnDestroy` 中调用，防止线程残留。

---

## 与其他模块的关系

| 模块 | 关系 |
|------|------|
| BattleManager | 注册 `EngineUpdate` 到 `_frameUpdateListeners`，是 FrameEngine 的唯一消费者 |
| IBattleController | `LogicUpdate()` 在 FrameEngine 的回调中被调用 |
| RemoteBattleNetworkController | 注册 `NetUpdate` 到 `_netUpdateListeners` |
| BattleSetting | 提供 `FrameInterval=33`, `FramePerSecond=30` 等常量 |
| MatchController | `LogicUpdate()` 内部调用 `MatchController.LogicUpdate()` |

---

## 关联笔记

- [[001-Input输入数据结构]] — FrameEngine 驱动消费的 Input
- [[002-FrameBuffer帧缓存]] — FrameEngine 在逻辑帧中读写 FrameBuffer
- [[001-帧同步与状态同步]] — 帧同步需要固定步长引擎的根本原因

---

*记录时间：2026-06-30*

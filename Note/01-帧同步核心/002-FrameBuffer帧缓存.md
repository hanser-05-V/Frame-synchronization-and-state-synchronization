# 002 — FrameBuffer 帧缓存

> 📊 **本篇配图：**
> - [002-FrameBuffer环形缓冲区.html](../charts-output/002-FrameBuffer环形缓冲区.html) — 环形缓冲区设计 + SyncFrame/TryGetFrame 读写协议

## 一句话定位

`FrameBuffer` 是帧同步的数据仓库——用**环形缓冲区（Circular Buffer）**存储每一帧的所有玩家 Input。基类 `FrameBuffer` 定义了 Frame/Input 数据结构和读写协议，子类 `LocalFrameBuffer`（单机/2000帧）和 `RemoteFrameBuffer`（联网/10000帧）分别适配不同场景。

---

## 设计意图

### 为什么用环形缓冲区？

帧同步的特点是**帧序号严格递增 + 只向前消费**。帧 100 消费完后不会再读帧 99，但帧 99 还没被覆盖前可能在回滚时需要。所以需要：
- 固定大小的缓冲区（节省内存）
- 按 `frame % capacity` 定位（O(1) 读写）
- 旧帧被新帧自然覆盖（不需要额外 GC）

```
环形缓冲区示意 (capacity=8):

  index:  0    1    2    3    4    5    6    7
         ┌────┬────┬────┬────┬────┬────┬────┬────┐
         │ 96 │ 97 │ 98 │ 99 │100 │101 │102 │103 │  ← 帧序号
         └────┴────┴────┴────┴────┴────┴────┴────┘
                                           ↑
                                      最新写入的帧

  当帧 104 写入 → 覆盖 index 0（帧 96）：frame 104 = buffer[104 % 8] = buffer[0]
```

---

## 接口层 — 暴露了什么

### FrameBuffer 基类

```csharp
// E:\帧同步\Common\Battle\FrameBuffer.cs:7-770
public class FrameBuffer
{
    // ----- 构造 -----
    + FrameBuffer(int playerCount, int capacity=1000)
    + Reset()
    + Start(long seed, EMatchState enterState, bool immediatelyStart)

    // ----- 写入 -----
    + SyncFrame(int frame, ref Frame inputFrame, bool force) → bool
      // 将一帧数据写入缓冲区，frame%capacity 定位

    // ----- 读取 -----
    + TryGetFrame(int frame, ref Frame result, bool remove=true) → bool
      // 读取并可选清除指定帧，读取后该槽位置 -1
    + PeekFrame(int frame, ref Frame result) → bool
      // 只读不删，回滚时需要"偷看"历史帧
    + HasFrame(int frame) → bool
      // 检查某帧是否在缓冲区中

    // ----- GM (调试作弊) -----
    + AddGM(int pos, int frame, GM gm)
    + TryGetGM(int pos, int frame, out GM gm) → bool
```

### 子类差异

| 维度 | LocalFrameBuffer | RemoteFrameBuffer |
|------|:---:|:---:|
| 容量 | 2000 帧 (~66秒) | 10000 帧 (~333秒) |
| 场景 | 单机/录像 | 联网 PVP |
| 写入来源 | 本地 Input | 服务器下发的远程 Frame |
| 消费方式 | 立即消费 | 可能延迟（等服务器） |

> 📂 **[点此打开 UML 图](../charts-output/002-FrameBuffer环形缓冲区.html)**

### Frame 结构体 — 一帧 = 所有玩家 Input 的集合

```csharp
// E:\帧同步\Common\Battle\FrameBuffer.cs:263-413
public struct Frame
{
    + int frame;           // 帧序号
    + int playerCount;     // 实际玩家数
    + Input i0, i1, i2, i3, i4, i5, i6;  // 最多 7 个玩家

    + this[int index] { get; set; }  // 索引器，按位置取 Input
    + SetInputByPos(int pos, Input)  // 按玩家 pos 设定 Input
    + GetInputByPos(int pos, ref Input) → bool  // 按玩家 pos 获取 Input
}
```

> ⚠️ Frame 只有 7 个槽位（i0-i6），最多支持 7 个玩家（3v3 + 1 裁判）。

---

## 设计逻辑（核心）

### 索引定位公式

```
buffer_index = (frame % _capacity) * _frameSize

_frameSize = _inputSize × _playerCount + 4(帧序号)
           = 4 × playerCount + 4
```

```
内部布局（playerCount=6, capacity=1000）：

byte[] _buffer 总大小 = (4×6 + 4) × 1000 = 28000 字节

┌───────────── Frame  ─────────────┐
│ [4B 帧号] [4B P0] [4B P1] ... [4B P5] │ × capacity
└──────────────────────────────────────┘

访问帧 N 的 P0 输入：
  偏移 = (N % 1000) × 28
  P0数据 = *(Input*)(buffer + 偏移 + 4)
```

### SyncFrame — 写入协议

```csharp
// E:\帧同步\Common\Battle\FrameBuffer.cs:699-762
public virtual bool SyncFrame(int frame, ref Frame inputFrame, bool force, ref int diff)
{
    // ① 去重：已经收过的帧不重复写入
    if (frame <= _maxRecvedFrame) return true;

    // ② 冲突检测：如果该槽位已有数据且非 force，拒绝写入
    currentFrame = *(int*)input;  // 读取槽位中已有的帧号
    if (currentFrame != -1 && !force)
    {
        // "Frame buffer is full when insert"
        return false;
    }

    // ③ 连续性检测：写入帧号必须比上一帧大 1
    diff = frame - _lastSetFrameIndex;
    if (diff > 1) return false;  // 跳帧了

    // ④ 逐玩家写入 Input（通过 unsafe 指针直接写内存）
    *(Input*)(input + 4 + 0*_inputSize) = inputFrame.i0;
    *(Input*)(input + 4 + 1*_inputSize) = inputFrame.i1;
    // ...

    // ⑤ 写入帧号 + 更新游标
    *(int*)input = frame;
    _maxRecvedFrame = frame;
    _lastSetFrameIndex = frame;
}
```

> 💡 **设计意图**：用 `unsafe` 指针直接写入 byte[]，跳过了结构体序列化。帧同步每帧都要写一次，这个操作频率极高（30次/秒），指针操作是最高效的方式。

### TryGetFrame — 读取协议

```csharp
// E:\帧同步\Common\Battle\FrameBuffer.cs:613-694
public virtual bool TryGetFrame(int frame, ref Frame result, bool remove = true)
{
    // ① 连续性检查：非首帧时，当前帧号必须是上一帧号 +1
    if (frame != 0 && _lastGetFrame.frame + 1 != frame)
        return false;  // 帧丢失

    // ② 帧号校验：缓冲区中读出的帧号必须匹配
    currentFrame = *(int*)input;
    if (frame != currentFrame) return false;

    // ③ 逐玩家读取 Input
    result.i0 = *(Input*)(input + 4 + 0 * _inputSize);
    // ...

    // ④ 消费后清除槽位（写入 -1）
    if (remove) *(int*)input = -1;

    // ⑤ 空槽补全：pos==7 的槽位用上一帧的数据填充
    for (var i = 0; i < result.playerCount; ++i)
    {
        if (result[i].pos == 7)
            result[i] = _lastGetFrame[i];
    }

    _lastGetFrame = result;
}
```

> 💡 **设计意图**：`remove=true` 时消费后清除——这防止了帧序号溢出后的混淆。如果不清除，1008 帧会覆盖 8 帧，旧数据留在缓冲区会导致误读。

### 空槽补全机制

帧实际玩家数可能小于最大槽位数。比如 2v2 的比赛，Frame 有 4 个槽位但只有 4 个玩家，`i4-i6` 闲置。代码用 `pos == 7` 标记空槽——但 7 是合法玩家 ID？不，注释说明 `pos` 合法值只有 0-6，7 实际上不是合法玩家 ID，被用作哨兵值（但代码里写的是 `pos == 7`，而非 `pos == 255`）。

```
// 一个可能的 BUG 或设计缺陷：
// TryGetFrame 检查 pos == 7（第682行）
// 但 Input.pos 的非法值是 255 (0xF)（第61行）
// 两处用了不同的哨兵值！
// 实际影响：如果 Frame 初始化时 pos 被设为 7（而非 255），
// pos==7 的槽位会被上一帧的数据填充（空槽补全逻辑）
```

---

## ⚠️ 实践注意

1. **frame 不是无限递增的**——环形缓冲区 `frame % capacity` 定位，帧号从 0 开始，一局游戏通常几万帧。不会溢出 `int`（21 亿帧 = 约 2 年连续运行）。
2. **SyncFrame 的连续性检测**——如果 `diff > 1`（跳帧），写入失败。这意味着帧必须严格按序插入，不能乱序。
3. **TryGetFrame 的连续性检测**——同样的，消费也必须按序。如果上次读的是帧 99，下次只能读帧 100。
4. **remove=false 用于回滚**——`PeekFrame` 和 `TryGetFrame(remove:false)` 读取但不消费，回滚时用，确保历史帧数据还在。
5. **GM 数据是单独存储的**——`_gmMap` 字典存调试作弊数据，不在 Input 结构体内。GM 操作的 flag 标记了哪些帧有 GM 数据。

---

## 与其他模块的关系

| 模块 | 关系 |
|------|------|
| FrameBuffer.Input | Frame 内部由多个 Input 组成 |
| FrameEngine | 按照 FrameEngine 的固定步长消费 Frame |
| LocalBattleController | 单机模式：SyncFrame 写入本地输入 → TryGetFrame 消费 |
| RemoteBattleController | 联网模式：服务器帧写入 RemoteFrameBuffer → 消费时对比预测 |
| RecordFrameBuffer / ReplayFrameBuffer | 录像回放的子类，从 Protobuf 反序列化帧数据 |

---

## 关联笔记

- [[001-Input输入数据结构]] — Frame 中的每个 Input 的内部结构
- [[003-FrameEngine帧循环引擎]] — 谁在驱动 FrameBuffer 的读写
- [[001-帧同步与状态同步]] — 帧缓存存在的意义

---

*记录时间：2026-06-30*

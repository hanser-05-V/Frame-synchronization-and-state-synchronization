# 001 — Input 输入数据结构

> 📊 **本篇配图：**
> - [001-Input位域布局图.html](../charts-output/001-Input位域布局图.html) — 32-bit 位域分配与位运算拆解

## 一句话定位

`FrameBuffer.Input` 是帧同步的最小数据单元——一个玩家在一帧中的全部操作，**压缩为 1 个 `uint`（4 字节）**。通过位域（bit field）将 6 个字段紧凑存入 32-bit，位运算存取比序列化类快 100 倍以上。

---

## 设计意图

### 为什么要压缩到 4 字节？

帧同步每帧都要把所有玩家的 Input 发给服务器再转发给所有客户端。如果每个字段用一个 `int` 或 `byte`（加上对齐 padding），一个 Input 轻松上 24 字节。6 个玩家 × 30FPS × 24 字节 = 4.3KB/秒。压缩后：6 × 30 × 4 = **720 字节/秒**。

**核心洞察**：每个字段的值域很小——位置 0-6（3 bit）、方向 0-24（5 bit）、按钮 7 个（7 bit）、标记 5 bit、表情 4 bit、投降 4 bit，总和只需要 28 bit，刚好塞进 32-bit 的 `uint`。

---

## 接口层 — 暴露了什么

### Input 结构体全貌

```csharp
// E:\帧同步\Common\Battle\FrameBuffer.cs:38-229
public struct Input
{
    public uint _raw;  // 底层存储，所有字段读写都通过位运算

    // 6 个属性，每个对应一段 bit 区间
    + pos    : byte  // get/set — 玩家位置 (4 bit, 值域 0-6, 0xF 表示无效)
    + yaw    : byte  // get/set — 移动方向 (8 bit, 值域 0-24)
    + realBtn: byte  // get/set — 按键 bitmask (7 bit)
    + flag   : byte  // get/set — 身份标记 (5 bit)
    + emoji  : byte  // get/set — 表情 ID (4 bit)
    + giveup : byte  // get/set — 投降标记 (4 bit)

    // 构造与序列化
    + Input(uint value)          // 从 uint 恢复
    + ToUint() → uint            // 反序列化为 uint
    + Reset()                    // 清零所有字段
    + Compare(Input other) → bool     // 精确对比（包含 pos）
    + CompareIgnoreOffset(Input) → bool  // 忽略 yaw 高位 offset 的对比
    + PredictCompare(Input) → bool  // 预测对比（跳过 GM 标记差异）
}
```

> 📂 **[点此打开 UML 图](../charts-output/001-Input位域布局图.html)**

### 关键方法行为

| 方法 | 行为 |
|------|------|
| `Compare` | `_raw == other._raw`，逐 bit 对比 |
| `CompareIgnoreOffset` | 忽略 yaw 的高 3 bit（offset 位），只比低 5 bit |
| `PredictCompare` | 网络预测用——yaw/btn/giveup 相同 + flag 非 GM → 认为一致 |
| `HasGM` | flag 中是否有 EFlag.GM (4) |

---

## 设计逻辑（核心）

### 位域布局详解

```
 31  30  29  28 | 27  26  25  24  23  22  21  20 | 19  18  17  16  15  14  13 | 12  11  10  9  8 | 7  6  5  4 | 3  2  1  0
 ───────────────┼─────────────────────────────────┼────────────────────────────┼──────────────────┼───────────┼───────────
      pos       |              yaw                |            btn             |       flag       |   emoji   |  giveup
     4 bit      |            8 bit                |           7 bit            |      5 bit       |   4 bit   |   4 bit
   0xF0000000   |          0x0FF00000             |         0x000FE000         |    0x00001F00    | 0x000000F0| 0x0000000F
```

**读操作（以 pos 为例）：**

```
pos = (0xF0000000 & _raw) >> 28
       ↑                 ↑
       掩码取出高4位      右移28位得到值

特殊处理：如果 pos == 0xF → 返回 255（无效玩家）
```

**写操作（以 pos 为例）：**

```
_raw = (_raw & ~0xF0000000) | ((0xF & value) << 28)
       ↑                     ↑
       先清除旧值             新值左移到正确位置后 OR 进去
```

### 各字段的含义

| 字段 | bit 数 | 值域 | 含义 |
|------|:---:|------|------|
| `pos` | 4 | 0-6, 0xF=255 | 玩家在列表中的位置索引 |
| `yaw` | 8 | 0-24 | 移动方向：(n-1)×15°，Z轴=0°，顺时针。高 3 bit 在部分模式下复用存帧差值 |
| `realBtn` | 7 | 0-127 | 按键 bitmask，bit0=按键1, bit1=按键2 … 按下=1 |
| `flag` | 5 | 1/2/4 | EFlag: PLAYER(1), NP(2), GM(4) |
| `emoji` | 4 | 0-15 | 互动表情ID，0=无，实际ID = emoji-1 |
| `giveup` | 4 | 0-15 | ELostFlag: GivenUp(1), Require(2), Accept(4), Deny(8) |

### 为什么 pos 特殊——值 0xF 表示无效？

```csharp
// 源码 BUG 或特性：pos 字段只用了 4 bit，最大值 15(0xF)
// 但 pos 的真实值域是 0-6（最多 7 个玩家）
// 所以 0xF 被当作"无此玩家"的哨兵值
if (v == 0xF) return 255;
```

> 💡 **设计意图**：`pos = 255` 表示该 Input 槽位为空。Frame 固定 7 个槽位（i0-i6）但实际可能只有 2-6 个玩家，空槽用 pos=255 标记，消费端自然跳过。

### yaw 的高 3 bit 复用

```csharp
// E:\帧同步\Common\Battle\FrameBuffer.cs:76-84
// 部分模式下 yaw 的高 3位 存「输入帧和渲染帧的差值 offset」
// 写入: yaw = (offset << 5) | (yaw & 0x1F)
// 读取: offset = (yaw & 0xE0) >> 5
//       yaw   = yaw & 0x1F
//
// 因为 yaw 最大值 24 < 0x1F(31)，低 5 位够用
// 高 3 位空闲 → 不浪费，复用存其他信息
```

> 💡 **设计意图**：极致压缩——yaw 只用 5 bit 就够表达 24 个方向，剩下的 3 bit 空闲不用白不用。在需要帧差值的场景下复用这 3 bit，无需额外字段。

### PredictCompare 为什么跳过 GM 标记？

```csharp
// E:\帧同步\Common\Battle\FrameBuffer.cs:177-193
public bool PredictCompare(Input other)
{
    // 只要 yaw/btn/giveup 相同，且双方都不带 GM 标记 → 认为一致
    if (yaw == other.yaw && realBtn == other.realBtn && giveup == other.giveup)
    {
        if (((byte)EFlag.GM & flag) != 0 || ((byte)EFlag.GM & other.flag) != 0)
            return false;  // GM 操作不参与预测对比
        if (((byte)EFlag.PLAYER & flag) != ((byte)EFlag.PLAYER & other.flag))
            return false;  // 玩家标记必须一致
        return true;
    }
    return false;
}
```

> 💡 **设计意图**：联网模式下，本地预测的 Input 和服务器确认的 Input 的 flag 字段可能不同（本地加的 GM 标记服务器没有），所以对比时排除 GM 标记。但 PLAYER 标记必须一致——这是基础身份确认。

---

## ⚠️ 实践注意

1. **pos 不能直接当数组索引**——pos=255 的槽位是空的，遍历 Input 数组前要判断 `pos != 255`。
2. **yaw 的复用逻辑是"读时拆、写时合"**——如果你在非 offset 模式下错误地读了高 3 bit，会得到错误的方向值。
3. **realBtn 的语义在战斗中 vs 庆祝中不同**——战斗中每个 bit 是一个按键（可多按），庆祝中值表示按键序号（单选）。
4. **Compare vs PredictCompare 用错后果严重**——Compare 用于本地确定性，PredictCompare 用于网络预测对比。用 Compare 去对比预测和服务器的帧会因 flag 差异导致误判。

---

## 与其他模块的关系

| 模块 | 关系 |
|------|------|
| FrameBuffer.Frame | Frame 包含 `playerCount` 个 `Input`，是一个完整帧 |
| BattleManager.GetInput() | 从 UI 读取玩家操作，生成 `Input` |
| RemoteBattleController.CompareInput | 对比两个 Frame 时，逐 Input 调 `PredictCompare` |
| AIManager | AI 的 Input 由行为树生成（非玩家操作），同样打包成 `Input` 结构 |

---

## 关联笔记

- [[001-帧同步与状态同步]] — 为什么帧同步只传 Input 而不是状态
- [[002-FrameBuffer帧缓存]] — Frame 和 Input 怎么存入环形缓冲区
- [[003-FrameEngine帧循环引擎]] — FrameEngine 以什么频率消费 Input

---

*记录时间：2026-06-30*

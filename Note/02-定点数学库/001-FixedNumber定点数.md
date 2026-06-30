# 001 — FixedNumber 定点数

> 📊 **本篇配图：**
> - [004-定点数类图.html](../charts-output/004-定点数类图.html) — FixedNumber + FixedVector + FixedMath 完整类关系

## 一句话定位

`FixedNumber` 是帧同步**确定性的基石**——用 `long` 整数模拟小数（32.16 格式：32位整数 + 16位小数），替代 `float`。因为 IEEE 754 浮点数在不同 CPU/编译器/优化级别下结果可能不同，而定点数用整数运算，结果在任何设备上完全一致。

---

## 设计意图

### 为什么帧同步不能接受任何不确定性？

```
假设两个客户端用 float 算球的抛物线轨迹：

Client A (ARM CPU): sin(30°) = 0.499999999999
Client B (x86 CPU): sin(30°) = 0.500000000001

差异只有 0.000000000002，但乘上速度和帧数：
  帧100: A球在 y=5.0001, B球在 y=5.0003
  帧200: A球在 y=10.0005, B球在 y=10.0010
  ... 10秒后，一个球进了，一个球没进 → 不同步！
```

定点数解决了这个问题——所有运算都是整数，不存在浮点舍入差异。

---

## 接口层 — 暴露了什么

```csharp
// E:\帧同步\Common\Battle\FixedMath\FixedNumber.cs:7-28
public partial struct FixedNumber
{
    // ----- 格式常量 -----
    internal const int FRACTIONAL_BITS = 16;   // 16位小数
    // RAW_ONE = 1 << 16 = 65536
    public const long RAW_ONE = 1L << FRACTIONAL_BITS;

    // ----- 内部存储 -----
    public long _raw;          // 底层整数（long = 64-bit）

    // ----- 静态常量 -----
    + Zero, One, NegOne, Half      // 常用值
    + Hundred, OneTenth            // 常用分数
    + MinValue, MaxValue           // 值域边界
    + ApproximatelyError           // 近似误差容忍 (0.01)

    // ----- 运算符 -----
    + +, -, *, /, ==, !=, <, >    // 基础算术
    + MakeFixNum(int, int) → FixedNumber  // 构造: MakeFixNum(3,2) = 1.5
}
```

### 核心换算

```
FixedNumber 内部存储:
  1.0   = 65536  (1 << 16)
  0.5   = 32768  (1 << 15)
  0.01  = 655    (约等于)

C# float → FixedNumber:
  3.14f → (long)(3.14 * 65536) = 205783

FixedNumber → C# float:
  _raw / 65536.0f → 用于最终渲染（逻辑层不用）
```

> 📂 **[点此打开 UML 图](../charts-output/004-定点数类图.html)**

---

## 设计逻辑（核心）

### 为什么选 32.16 而非其他精度？

| 精度 | 范围 | 小数精度 | 评估 |
|------|------|:---:|------|
| 16.16 (int) | ±32768 | 1/65536≈0.000015 | 范围太小，位置可能溢出 |
| **32.16 (long)** | **±2万亿** | **1/65536≈0.000015** | **✅ 范围够用，精度够用** |
| 48.16 (自定义) | ±很大 | 同上 | 太复杂，没有语言原生支持 |

> 32.16 用 `long`（64bit），范围 ±9×10¹⁸，精度约 0.000015。街篮2 的球场大小约几十米，位置计算完全不会溢出。

### 加减乘除怎么保证确定性？

```csharp
// 加/减：直接整数运算（完全确定）
a + b → _raw = a._raw + b._raw

// 乘：先乘再右移16位（防止溢出用 long 中间值）
a * b → _raw = (a._raw * b._raw) >> 16

// 除：先左移16位再除（保证精度）
a / b → _raw = (a._raw << 16) / b._raw
```

> 💡 **设计意图**：乘法先乘再移——相当于 `(a × b) / 65536`，保持 32.16 格式。除法先移再除——先放大被除数保证不丢失精度。

### YawOffset 的设计

```csharp
// E:\帧同步\Common\Battle\FixedMath\FixedMath.cs:35-39
public const int YawOffset = 1;  // 方向值的偏移
// Input.yaw = 0 → 原地站立
// Input.yaw = 1 → 角度 = (1-1)×15° = 0° (Z轴方向)
// Input.yaw = 2 → 角度 = (2-1)×15° = 15°
```

> 💡 `yaw` 的值 0 被用作"停止移动"的哨兵，所以实际方向值从 1 开始，计算角度时减去 `YawOffset`。

---

## ⚠️ 实践注意

1. **不要混用 float 和 FixedNumber**——逻辑层永远用 FixedNumber，只在渲染层转 float 用于显示。
2. **FixedNumber.MakeFixNum(3, 2) = 1.5**——分子/分母构造法，不是 `new FixedNumber(1.5f)`（这会把 float 的不确定性带进来）。
3. **运算顺序影响精度**——`(a+b)*c` 与 `a*c+b*c` 在定点数下可能因舍入方式不同产生微小差异。保证所有客户端用相同的运算顺序。
4. **三角函数用查表**——`FixedMath.Sin(angle)` 查预计算表而非实时计算，保证所有设备结果完全一致。

---

## 关联笔记

- [[002-FixedMath定点数学库全览]] — 查表法实现的三角函数/对数/向量库
- [[001-帧同步与状态同步]] — 为什么定点数是帧同步的前提
- [[001-Input输入数据结构]] — Input.yaw 怎么用 0-24 映射到方向

---

*记录时间：2026-06-30*

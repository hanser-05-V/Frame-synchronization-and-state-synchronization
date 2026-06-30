# 002 — FixedMath 定点数学库全览

> 📊 **本篇配图：**
> - [004-定点数类图.html](../charts-output/004-定点数类图.html) — FixedNumber + FixedVector + FixedMath 完整类关系（与 001 共用）

## 一句话定位

`FixedMath` 是帧同步的**确定性数学工具箱**——除 `FixedNumber` 基础类型外，还包括向量（`FixedVector2/3`）、四元数（`FixedQuaternion`）和三角函数/对数等超越函数。所有超越函数**用查表法实现**，保证不同平台结果完全一致。

---

## 设计意图

Unity 的 `Mathf.Sin()` 等函数底层调用系统数学库，不同平台（ARM/x86）、不同编译器（Mono/IL2CPP）结果可能不同。FixedMath 自己预计算了一张三角函数查找表，查表结果是完全确定的。

### 查表 vs 实时计算

```
❌ 实时计算: sin(45°) → 调系统lib → ARM vs x86 → 不同
✅ 查表:    sin(45°) → table[45] → 预存值 → 永远相同
```

代价：精度受限（查表分辨率），但街篮2 只要求 15° 方向精度（24 个方向），查表完全够用。

---

## 接口层 — 类库全貌

```csharp
// FixedMath (E:\帧同步\Common\Battle\FixedMath\FixedMath.cs)
+ Pi, HalfPi, TwoPi            // π 常量（定点）
+ Deg2Rad, Rad2Deg             // 角度↔弧度转换系数
+ Sin(Fixed) → Fixed           // 正弦（查表）
+ Cos(Fixed) → Fixed           // 余弦（查表）
+ Atan2(Fixed, Fixed) → Fixed  // 反正切（查表）
+ Sqrt(Fixed) → Fixed          // 平方根（牛顿迭代）
+ Abs(Fixed) → Fixed
+ Clamp/Max/Min/Lerp           // 基础工具
+ MirrorYaw(byte) → byte       // 镜像方向（对手视角用）

// FixedNumber (E:\帧同步\Common\Battle\FixedMath\FixedNumber.cs)
+ struct FixedNumber { long _raw; }   // 32.16 定点数核心
+ MakeFixNum(int,int) → FixedNumber   // 构造：分子/分母
+ operators: + - * / == != < >
+ ToFloat() / ToInt()                 // 转换（仅渲染层用）

// FixedVector2 / FixedVector3 / FixedQuaternion
+ 完全模拟 Unity 的 Vector2/3/Quaternion，但基于定点数
+ 含: Dot, Cross, Normalize, Distance, Slerp 等
```

---

## 设计逻辑（核心）

### 查表法的精度取舍

街篮2 移动方向只有 24 个（每个 15°），所以三角函数的输入角度也都是 15° 的整数倍。查表只需要预计算 0°, 15°, 30°, … 345° 的 sin/cos 值即可，表大小只有 24 项。

### 为什么不用 C# 的 `decimal`？

`decimal` 是 128-bit 十进制浮点数，虽然是确定性的但：
- 运算速度比 `long` 慢 10-50 倍
- Unity IL2CPP 不完全支持
- 没有配套的向量/四元数库

### 牛顿迭代法求平方根

`Sqrt` 不用查表（平方根的值域太大），而是用牛顿迭代法——给定相同输入，迭代相同次数得到相同结果。

---

## ⚠️ 实践注意

1. **FixedVector3 不是 Vector3 的直接替换**——Convert 只在渲染层做。
2. **可序列化**——FixedNumber 标记了 `[Serializable]`，可以存入 Protobuf 协议。
3. **比较用 ApproximatelyError**——`a == b` 是精确对比，`(a-b).Abs() < ApproximatelyError` 是近似对比（容差 0.01）。

---

## 关联笔记

- [[001-FixedNumber定点数]] — 核心 FixedNumber 类型详解
- [[001-Input输入数据结构]] — yaw 方向值怎么映射到 0-24

---

*记录时间：2026-06-30*

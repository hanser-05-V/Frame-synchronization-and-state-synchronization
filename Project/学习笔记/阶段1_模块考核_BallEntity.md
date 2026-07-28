# 阶段1 模块考核 — BallEntity

> 记录人：💜小爱 | 提问人：🦉架构导师 | 学习模式：L3
> 日期：2026-07-28 | 模块：BallEntity.cs

---

### BallEntity — Q1：为什么 BallEntity 是 `class`，而 FixedVector3 是 `struct`？

**帅老大回答：**
BallEntity 是篮球实体，有自己的行为处理。FixedVector3 是定点数向量的数据集合，属于数据的集合所以用 struct。值类型存储在栈上，执行更快。

**评价：** ⚠️ 两个细节错误，漏核心原因

**❌ 错误点：**
1. BallEntity **没有行为逻辑**——只有一个 `Reset()` 加 4 个数据字段，行为在 `BallPhysicsSystem` 里处理。说"有自己的行为"不对。
2. "值类型存储在栈上"——`FixedVector3` 作为 BallEntity 的字段时，它跟着 BallEntity 对象一起存在**堆上**。只有局部变量、方法参数才在栈上。"struct = 栈"是常见面试陷阱。
3. 没说出区分 class/struct 的真正原因。

**✅ 真正原因：**

| | FixedVector3（struct） | BallEntity（class） |
|--|------|------|
| 语义 | **值**——复制是安全的 | **实体**——"那个球"有独立身份 |
| 共享 | 不需要，每次赋值=新副本 | **多个系统需要引用同一个球** |
| 快照 | 值拷贝，确定性高 | 需要显式 DeepClone |

最关键：GameController、BallPhysicsSystem、PlayerEntity 都要操作**同一个球**。用 struct 的话每次赋值都是副本，BallPhysicsSystem 改了球的位置，GameController 看到的还是旧的。

**💜 小爱参考回答（面试标准）：**
BallEntity 用 class 是为了**引用共享**。多个系统（GameController、BallPhysicsSystem、PlayerEntity）需要指向"同一个球"——球被物理系统更新位置后，所有引用方看到的是同一个对象的变化。如果用 struct（值类型），每次赋值都是副本，BallPhysicsSystem 改了球的位置但 GameController 持有的版本不会同步，这在帧同步里是灾难。反过来说，FixedVector3 用 struct 是因为它代表一个"值"概念——位置就是三个数字的组合，拷贝是安全的，且 struct 避免了堆分配和 GC 压力。区分"实体"和"值对象"是面向对象设计的基本功。

---

### BallEntity — Q2：为什么 `holderPlayerIndex` 用 int 而不是 PlayerEntity 引用？

**帅老大回答：**
`-1` 表示无人持球。直接存 PlayerEntity 引用不符合设计，篮球初始状态无人持球。

**评价：** ⚠️ 说清了 -1 的含义，但没回答"为什么不用引用"的深层原因

**缺失的关键点：**

帧快照必须能序列化。回滚时要把整个游戏状态保存到 FrameSnapshot 然后完整恢复：
- 存引用：引用 = 内存地址，换一台机器不同，无法序列化/恢复
- 存 int：存到快照 → 恢复时拿 index 去 PlayerEntity 列表里找 → 跨机器完全一致

**这是帧同步里"用 ID/Index 代替引用"的经典模式——和数据库外键替代对象引用的道理一样。** 项目里所有跨系统关联都用 index 而非引用，不是巧合。

**💜 小爱参考回答（面试标准）：**
`holderPlayerIndex = -1` 看似简单，背后是帧同步的序列化约束。帧回滚机制需要将游戏状态保存到快照中并能在未来任意时刻完整恢复。如果 `holderPlayerIndex` 是一个 `PlayerEntity` 对象引用（即内存地址），序列化时存的只是一串指针值，不同机器/不同运行时环境下恢复这个指针没有任何意义。用 int index 则完全解决了这个问题：序列化存一个整数，恢复时通过 index 在 PlayerEntity 列表里查找——`players[holderPlayerIndex]`——跨平台、跨时刻完全一致。这个设计在帧同步中随处可见：用数据的"逻辑标识"替代"物理引用"，确保快照序列化的确定性。

---

### BallEntity — Q3：Airborne 状态何时触发？谁负责每帧更新？停在哪个状态？

**帅老大回答：**
初始化/Reset 时可以设置 Airborne。后续投篮、传球、被盖帽时触发。球物理消费在 GameController 注册回调调用 BallPhysicsSystem 的单帧物理更新，引擎执行单帧消费执行。

**🦉纠正后的确认（帅老大的描述经代码验证是正确的）：**
```csharp
// GameController.cs:53 — 绑定委托
_frameEngine.OnPostFrameUpdate += OnPostFrameUpdate;

// FrameEngine.cs:134-135 — 引擎每帧触发
OnPostFrameUpdate?.Invoke(frameID, inputs);

// GameController.cs:271 — 回调中调用
BallPhysicsSystem.Update(_ballEntity, FixedInt.FromFloat(0.033f));
```
确实是 GameController 给 FrameEngine 绑定委托 → 引擎每帧 Invoke → GameController 回调调用 BallPhysicsSystem。🦉之前说"不是回调模式"是错的。

**评价：** ✅ 调用链描述正确（🦉错），⚠️ 漏了 Airborne 的终止状态

**缺失：Airborne 的三个出口：**
```
Airborne →
  ├── Scored      球穿过篮筐平面 → 进球
  ├── OutOfBounds 球飞出球场边界
  └── Free        球落地（没进）→ 自由弹跳，等待篮板
```

**💜 小爱参考回答（面试标准）：**
Airborne 是球在空中飞行的状态，由 BallPhysicsSystem.Simulate() 每帧做 Euler 积分更新位置。BallPhysicsSystem 的调用链是：FrameEngine.OnPostFrameUpdate 事件 → GameController 订阅的回调 → 直接调用 BallPhysicsSystem.Update()。这个状态有三个退出路径：穿过篮筐平面 → Scored（进球）；飞出球场边界 → OutOfBounds；落地且未穿过篮筐 → Free（自由滚动，等待篮板争夺）。BasketballCourt 提供了篮筐平面坐标和球场边界供 BallPhysicsSystem 做判定。

---

### BallEntity — Q4：为什么用 `Reset()` 而不是构造函数？什么场景反复调用？

**帅老大回答：**
防止每次都新建，提供一个方法供外部调用。

**评价：** ⚠️ 方向对但太模糊，没说核心原因

**✅ 三个核心原因：**

1. **对象复用，避免 GC：** 帧同步里球反复重置（进球→跳球→进球→跳球）。每次 `new BallEntity()` 旧对象被 GC，新对象要分配。GC 在帧同步里是大忌——`Reset()` 复用同一实例，零分配。

2. **回滚恢复：** 预测回滚时从 FrameSnapshot 恢复到之前帧的状态——不是 new 新球，而是把现有球数据**覆盖**成快照值。

3. **球的生命周期：** 一场比赛中球要 Reset 几十次：跳球 → 持球 → 投篮 → 进球 → Reset → 跳球...

**💜 小爱参考回答（面试标准）：**
`Reset()` 而不是构造函数，是因为球在一场比赛中会经历几十次"重新开始"（进球后跳球、出界后发球、回滚恢复），每次都 `new BallEntity()` 会对 GC 造成持续压力——在帧同步里 GC 暂停意味着逻辑帧延迟，直接表现为追帧。`Reset()` 的设计实现了对象复用：只覆盖字段值，不分配新内存，零 GC 开销。这实际上是手动实现的轻量对象池，在游戏引擎和帧同步中非常常见。额外的收益是为回滚恢复提供了统一入口：从 FrameSnapshot 反序列化后直接调 `Reset(pos, airborne)` 就能把球恢复到目标帧的状态。

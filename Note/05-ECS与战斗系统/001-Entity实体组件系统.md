# 001 — Entity 实体组件系统

> 📊 **本篇配图：**
> - [007-ECS实体类图.html](../charts-output/007-ECS实体类图.html) — MatchEntity/PlayerEntity/BallEntity + Component 体系

## 一句话定位

街篮2 的战斗逻辑采用 **ECS（实体-组件-系统）架构**——`MatchEntity` 是比赛全局实体（含 15+ 组件），`PlayerEntity` 是单个球员实体（含 ~20 组件），`BallEntity` 是球实体。每个"组件"是一组相关数据，每个"系统"负责处理一组组件的逻辑。30+ System 按 `[EntitySystem]` 注册顺序依次执行。

---

## 设计意图

### 为什么用 ECS 而非传统 OOP 继承？

```
❌ 继承层次（传统OOP）：
  GameObject → Player → BasketballPlayer → StreetballPlayer
  深度继承导致"钻石问题"——一个球员既是"进攻者"又是"防守者"时无法归类

✅ ECS（组件模式）：
  PlayerEntity 挂上:
    TransformComponent, InputComponent, PropertyComponent,
    SkillComponent, AnimationComponent, BuffComponent, ...
  每个 System 只关心自己的组件，互不干扰
```

**收益：**
- 组合优于继承——球员能力 = 组件的集合，而非类层次的产物
- 确定性友好——System 按固定顺序执行，每次执行结果确定
- 确定性拷贝——组件可以直接序列化/拷贝（`CopyTo`），用于回滚

---

## 接口层 — 实体类关系

```csharp
// Entity 基类 (E:\帧同步\Common\Battle\Entity\Entity.cs)
public class Entity
{
    + CalculateMD5() → MD5哈希
    + CopyTo(Entity target)     // 深拷贝（回滚用）
    + Release()                 // 归还对象池
}

// MatchEntity — 比赛全局状态 (E:\帧同步\Common\Battle\Entity\MatchEntity.cs)
public partial class MatchEntity : Entity
{
    + frame : int               // 当前帧号
    + deltaTime : FixedNumber   // 当前帧时间间隔
    + time : FixedNumber        // 累计时间

    + playerList : List<PlayerEntity>    // 所有球员
    + ballEntity : BallEntity            // 球

    // 15+ 组件:
    + state : StateComponent
    + runtimeProperty : MatchRuntimeProperyComponent
    + effect : MatchEffectComponent
    + camera : MatchCameraComponent
    + audio : AudioComponent
    + matchEvent : MatchEventComponent
}

// PlayerEntity — 单个球员
public class PlayerEntity : Entity
{
    + transform : TransformComponent    // 位置/朝向（定点）
    + input : InputComponent            // 当前帧输入
    + property : PropertyComponent      // 属性值（速度/力量...）
    + skill : SkillComponent            // 技能状态
    + animation : AnimationComponent    // 动画驱动
    + buff : BuffComponent              // Buff/Debuff
    // ... ~20 个组件
}
```

> 📂 **[点此打开 UML 图](../charts-output/007-ECS实体类图.html)**

---

## 设计逻辑

### CopyTo — 为什么不用反射自动拷贝？

`MatchEntity` 的 `CopyTo` 是手写的，部分字段标注了 `[DontAutoCopy]`（不拷贝）或 `[ReferenceCopy]`（引用拷贝而非深度拷贝）。因为：
- 性能：手写比反射快 10-50 倍
- 精确控制：不是所有字段都需要拷贝（如对象池引用）
- 回滚时的特殊处理：部分字段需要重置而非拷贝

### 对象池

```csharp
// MatchController 预创建 6 个 MatchEntity（BattleSetting 配置）
_matchController.matchEntityPool.Get();    // 获取
_matchController.matchEntityPool.Release(); // 归还
```

> 💡 每帧都要深拷贝 MatchEntity（回滚 + 快照），如果用 `new` 会产生大量 GC。对象池避免了 GC 抖动对帧率的影响。

---

## ⚠️ 实践注意

1. **组件越多，拷贝越慢**——`CopyTo` 需要遍历所有组件，MatchEntity 15+ 组件 × PlayerEntity 20 组件 × 6 个球员 = 每次拷贝 135+ 个组件对象。
2. **每帧至少 2 次拷贝**——`ConfirmeMatchEntity`（确认态）+ `SaveViewMatchEntity`（渲染快照），回滚时再加一次。

---

## 关联笔记

- [[002-System与State状态机概览]] — 处理这些组件的 System 体系
- [[002-MatchController战斗逻辑核心]] — 谁调用 CopyTo 和对象池

---

*记录时间：2026-06-30*

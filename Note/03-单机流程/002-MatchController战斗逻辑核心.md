# 002 — MatchController 战斗逻辑核心

> 📊 **本篇配图：**
> - [005-单机帧同步流程图.html](../charts-output/005-单机帧同步流程图.html) — 与 001 共用，含 LogicUpdate/Confirme/SaveView 三条管线

## 一句话定位

`MatchController` 是**战斗逻辑的中央调度器**——它持有 `MatchEntity`（当前确认态 + 预测态 + 视图快照），提供 `LogicUpdate()`（驱动状态机 + 30+ System）、`Rollback()`（回滚）、`ConfirmeMatchEntity()`（确认）和 `SaveViewMatchEntity()`（渲染快照）四条核心管线。

---

## 设计意图

### 为什么需要一个中央调度器？

30+ System 的执行顺序决定了帧同步的确定性。如果 MoveSystem 和 ShootSystem 的调用顺序在不同客户端不一致，即使 Input 相同也会不同步。MatchController 统一管理 System 的注册和调用顺序。

### 三个 MatchEntity 的职责

```
MatchEntity 对象池:
  ├── matchEntity        ← 当前确认态（服务器验证通过）
  ├── predictMatchEntity ← 预测态（客户端预测，可能回滚）
  └── viewMatchEntity    ← 渲染快照（插值用，不等逻辑帧）
```

---

## 接口层

```csharp
public class MatchController
{
    // 核心驱动
    + LogicUpdate(int frame, FrameBuffer buffer)   // ⭐ 一帧逻辑
    + UpdateGameState(MatchEntity)                  // 状态机+System

    // 确认与快照
    + ConfirmeMatchEntity(MatchEntity)  // 保存确认态
    + SaveViewMatchEntity()             // 保存渲染快照

    // 回滚（联网用）
    + Rollback(MatchEntity)             // 退回确认帧

    // Entity 生命周期
    + nextFrame : int                   // 下一帧序号
    + matchEntity : MatchEntity         // 当前确认态
    + predictMatchEntity                // 当前预测态
    + matchEntityPool : EntityPool      // 对象池
}
```

---

## 设计逻辑

### LogicUpdate 调用链

```
LogicUpdate(frame, buffer)
  │
  ├── ① CopyInput(matchEntity, ref frame)
  │     从 FrameBuffer 取出该帧所有 Input → 写入各 PlayerEntity.InputComponent
  │
  ├── ② RefreshMatchEntity(matchEntity)
  │     frame += 1; deltaTime=33ms*timeScale; time += deltaTime
  │
  └── ③ UpdateGameState(matchEntity)
         │
         ├── PlayerStateMachine.Update()  ← 140+ 状态
         ├── BallStateMachine.Update()    ← 15+ 状态
         ├── MatchStateMachine.Update()   ← 比赛状态
         └── 30+ Systems (按 [EntitySystem] 注册顺序):
              MoveSystem → ShootSystem → PassSystem → BlockSystem → ...

ConfirmeMatchEntity(matchEntity)
  └── 验证 + 存档: 将当前态存入确认列表

SaveViewMatchEntity()
  └── 深拷贝当前态 → 渲染线程读取（不阻塞逻辑线程）
```

> 💡 **设计意图**：System 通过 `[EntitySystem]` 属性标注执行阶段（Initialize/Update/Release），MatchController 用反射按声明顺序自动发现并调用。新增一个 System 只需加一个带 `[EntitySystem]` 的类，不需要改动 MatchController。

---

## ⚠️ 实践注意

1. **System 执行顺序是确定的**——反射发现 System 并按声明顺序调用，不能动态调整。
2. **ViewEntity 是深拷贝**——`SaveViewMatchEntity` 会完整拷贝当前 MatchEntity，渲染线程独立读取，不阻塞逻辑线程。
3. **对象池管理**——`matchEntityPool` 预创建 6 个 MatchEntity（`MaxConfirmedMatchEntityCacheCount=6`），回滚时从池中取而非 new。

---

## 关联笔记

- [[001-LocalBattleController单机控制器]] — LogicUpdate 的调用者
- [[001-Entity实体组件系统]] — MatchEntity 内部组件结构
- [[002-System与State状态机概览]] — 30+ System 详解

---

*记录时间：2026-06-30*

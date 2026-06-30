# 002 — System 与 State 状态机概览

> 📊 **本篇配图：** 无独立配图（全景总览型笔记）

## 一句话定位

街篮2 的战斗逻辑由 **30+ System（系统）**和 **3 个 StateMachine（状态机）**共同驱动。System 负责处理具体逻辑（移动、投篮、传球、碰撞...），StateMachine 管理状态转换（如球员从"运球"→"投篮"→"落地"）。二者通过 `MatchController.UpdateGameState()` 统一调度。

---

## 设计意图

### 为什么分离 System 和 StateMachine？

```
StateMachine = "球员现在是什么状态？能转到什么状态？"
  → 运球 → 投篮 → 落地 → 回防...

System = "在这个状态下具体做什么？"
  → MoveSystem: 在运球状态下，根据 yaw 方向移动球员
  → ShootSystem: 在投篮状态下，计算球的抛物线轨迹
  → BlockSystem: 在防守状态下，检测是否碰撞到投篮球员
```

---

## 30+ System 概览

| 分类 | System | 职责 |
|------|--------|------|
| **移动** | MoveSystem | 根据 Input.yaw 移动球员 |
| | DistanceSystem | 计算球员间距离/角度 |
| | DefenseSystem | 防守站位逻辑 |
| **投篮/传球** | ShootSystem | 投篮判定+轨迹计算 |
| | PassSystem | 传球逻辑 |
| | BlockSystem | 盖帽判定 |
| | ReboundSystem | 篮板判定 |
| | StealSystem | 抢断判定 |
| **物理/碰撞** | BallSystem | 球的物理运动（抛物线/弹跳） |
| | PhysisSystem | 碰撞检测 |
| | DistrictSystem | 区域判定（三分线内/外） |
| **属性/Buff** | AttributeSystem | 属性影响（速度/力量加成） |
| | BuffSystem | Buff/Debuff 管理 |
| | PropertySystem | 属性数值计算 |
| **动画/特效** | AnimationSystem | 动画状态机驱动 |
| | EffectSystem | 特效播放 |
| | CelebrationSystem | 庆祝动作 |
| **输入** | CheckSkillInputSystem | 检测输入是否触发技能 |
| | KeySystem | 组合键解析 |
| **其他** | EventSystem | 事件分发（进球/犯规...） |
| | AudioSystem | 音频触发 |
| | RandomSystem | 确定性随机数生成 |
| | AISystem | AI 行为（Behaviac） |

---

## 3 个状态机

### PlayerStateMachine（球员状态机 — 140+ 状态）

```
Dribble (运球)
  ├── Hold (持球)
  ├── Pass (传球)
  ├── Shoot (投篮)
  │   ├── JumpShoot (跳投)
  │   ├── Dunk (灌篮)
  │   └── Layup (上篮)
  ├── Block (盖帽)
  ├── Steal (抢断)
  ├── Rebound (抢篮板)
  └── Idle (闲置/倒地)
```

### BallStateMachine（球状态机 — 15+ 状态）

```
Attach (附着在球员身上)
  └── Pass (传球飞行中)
  └── Shoot (投篮飞行中)
  └── Physical (自由物理运动：反弹/滚地)
  └── JumpBall (跳球)
```

### MatchStateMachine（比赛状态机）

```
EnterShowScene (入场展示)
  → RoundReady (回合准备)
  → RoundPlaying (比赛中)
  → RoundOver (回合结束)
  → End (比赛结束)
```

---

## System 调度机制

```csharp
// System 通过 [EntitySystem] 属性声明执行阶段
[EntitySystem]
public class MoveSystem
{
    [EntitySystem.Initialize]  // 比赛开始时调用一次
    [EntitySystem.Update]      // 每帧调用（默认）
    [EntitySystem.Release]     // 比赛结束时调用一次
}

// MatchController 通过 Util.InvokeAttributeCall 反射发现并调用
// 按 System 类的声明顺序执行（不是按方法名）
```

> 💡 **设计意图**：`[EntitySystem]` 属性驱动——新增 System 不需要改 MatchController 代码，只需加一个带 `[EntitySystem]` 的类即可自动被发现并调度。配合 `using Streetball2.Battle;` namespace 确保 System 在各客户端上的发现顺序一致。

---

## ⚠️ 实践注意

1. **System 执行顺序 = 文件中的声明顺序**——不能在运行时动态调整。如果你写的 System 依赖其他 System 的输出，必须排在它后面。
2. **状态多在 State 目录下**——`Common/Battle/State/` 包含完整的 Player/Ball/Match 状态转换逻辑。

---

## 关联笔记

- [[001-Entity实体组件系统]] — System 处理的 Entity 和 Component
- [[002-MatchController战斗逻辑核心]] — System 被谁调度

---

*记录时间：2026-06-30*

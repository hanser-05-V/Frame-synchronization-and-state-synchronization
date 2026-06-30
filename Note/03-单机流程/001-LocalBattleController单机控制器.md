# 001 — LocalBattleController 单机控制器

> 📊 **本篇配图：**
> - [005-单机帧同步流程图.html](../charts-output/005-单机帧同步流程图.html) — 输入→组装帧→Sync→逻辑→确认→渲染 全链路

## 一句话定位

`LocalBattleController` 是**最纯粹的帧同步实现**——没有网络、没有预测、没有回滚。玩家输入直接存入 `LocalFrameBuffer`，`FrameEngine` 以 30FPS 固定步长消费帧数据，驱动 `MatchController.LogicUpdate()`。这是学习帧同步的最佳入口。

---

## 设计意图

### 为什么单机模式也用帧同步？

```
传统单机游戏：
  按键 → 直接调逻辑 → 更新渲染
  简单，但代码和联机完全不同

街篮2 单机:
  按键 → 打包成Input → SyncFrame → LogicUpdate → 渲染
  和联机用完全相同的逻辑层，只是 Input 来源不同
```

**收益：**
1. **代码统一**——单机和联机共用同一套 `MatchController`、状态机、30+ System
2. **调试友好**——单机能跑的逻辑，联机确定性下一定能跑
3. **录像天然**——`LocalFrameBuffer` 存的 Input 序列就是完整录像

---

## 接口层 — 暴露了什么

```csharp
// E:\帧同步\Script\Battle\ClientOnly\Manager\LocalBattleController.cs:19-317
public class LocalBattleController : IBattleController
{
    // ----- 数据 -----
    - LocalFrameBuffer localFrameBuffer;  // 容量2000帧
    + override FrameBuffer frameBuffer { get; }

    // ----- 生命周期 -----
    + Initialize() / Release()
    + GameOver()

    // ----- 核心驱动 -----
    + LogicUpdate()    // ⭐ 帧同步主循环
    + RenderUpdate()   // 渲染更新
    + NetUpdate()      // 单机为空（无网络）

    // ----- AI 控制 -----
    + SingleModeOpenAI()   // 开启AI托管
    + SingleModeCloseAI()  // 关闭AI（手动操控所有球员）
    + ChangeSelfPlayer(int selfId)  // 切换操控球员
}
```

> 📂 **[点此打开 UML 图](../charts-output/005-单机帧同步流程图.html)**

---

## 设计逻辑（核心）

### LogicUpdate() — 帧同步主循环

```csharp
// E:\帧同步\Script\Battle\ClientOnly\Manager\LocalBattleController.cs:126-233
public override void LogicUpdate()
{
    long startMillSecondes = BattleManager.instance.Time - _enterMilliseconds;

    // 追帧机制：如果时间差≥33ms，一次跑多帧
    while (startMillSecondes - _lastMilliseconds >= BattleSetting.FrameInterval
        && _matchController.currentEntitySet.GetUseableMatchEntityCount() > 0)
    {
        _lastMilliseconds += BattleSetting.FrameInterval;  // 累加33ms

        if (!Paused)
        {
            // ① 读取玩家输入
            var input = BattleManager.instance.GetInput(...);

            // ② 组装完整帧：遍历所有玩家，填充Input
            var frame = new FrameBuffer.Frame();
            for (var i = 0; i < playerList.Count; ++i)
            {
                if (player == selfPlayer && !player.AIGM)
                    frame[i] = input.input;        // 玩家操作
                else if (player.isAI)
                    frame[i] = AIManager.inputs[i]; // AI操作
                else
                    frame[i] = emptyInput;          // 空操作
            }

            // ③ 写入帧缓存
            frameBuffer.SyncFrame(nextFrame, ref frame);

            // ④ 驱动战斗逻辑
            _matchController.LogicUpdate(nextFrame, frameBuffer);

            // ⑤ 确认实体 + 保存视图快照
            _matchController.ConfirmeMatchEntity(matchEntity);
            _matchController.SaveViewMatchEntity();
        }
    }
}
```

> 💡 **设计意图**：while 循环追帧——正常情况每 33ms 跑 1 帧，如果系统卡了 100ms 就会一次跑 3 帧追赶。每帧累加 33ms 而非重置时间，保证帧间隔永远不会漂移。

### AI 的 Input 也是统一格式

```csharp
// AI 操作被包装成和玩家操作完全相同的 Input 结构
var aiInput = AIManager.inputs[player.ID];
frame[i] = new FrameBuffer.Input()
{
    realBtn = aiInput.realBtn,   // AI 算出的按键
    pos = (byte)player.ID,
    yaw = aiInput.yaw,          // AI 算出的方向
};
```

> 💡 **设计意图**：AI 和玩家用完全相同的 Input 管道。好处——(1) 切换托管时无需改动逻辑层；(2) 录像回放时 AI 也是确定的（给定相同的随机种子）。

### 镜像方向（玩家视角）

```csharp
// 对手视角下需要镜像 yaw 方向
var isMirrorYawActive = AttributeSystem.CheckAttributeInfluence(
    player, EPlayerAttribute.MirrorYawInput);
var yaw = isMirrorYawActive
    ? (byte)(FixedMath.MirrorYaw(yawInput) & 0x1F)
    : aiInput.yaw;
```

> 💡 玩家始终从"自己向右攻"的视角看球场，对手的移动方向需要镜像。这个只在渲染端做，逻辑端维持原始方向。

---

## ⚠️ 实践注意

1. **`_lastMilliseconds` 是累加的**——不会因为追帧而重置，这保证长期运行的帧间隔不会漂移。
2. **Pause 时帧号不递增**——while 循环在 `Paused=true` 时跳过逻辑，但 `_lastMilliseconds` 也不推进。
3. **GM 标记额外处理**——玩家按 GM 键时，额外调用 `frameBuffer.AddGM` 把调试作弊数据存到 `_gmMap`。
4. **AI Swap 是开发调试功能**——按 RightShift 键一键切换所有 AI 开启/关闭，方便测试。

---

## 关联笔记

- [[002-MatchController战斗逻辑核心]] — LogicUpdate 内部到底做了什么
- [[003-FrameEngine帧循环引擎]] — 谁调用了 LocalBattleController 的 LogicUpdate
- [[002-FrameBuffer帧缓存]] — LocalFrameBuffer vs RemoteFrameBuffer

---

*记录时间：2026-06-30*

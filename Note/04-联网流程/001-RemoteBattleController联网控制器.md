# 001 — RemoteBattleController 联网控制器

> 📊 **本篇配图：**
> - [006-联网预测回滚流程图.html](../charts-output/006-联网预测回滚流程图.html) — 预测→对比→回滚→重算 + MD5校验

## 一句话定位

`RemoteBattleController` 在 `LocalBattleController` 的基础上加上了**预测 + 校验 + 回滚**三个网络同步机制。客户端不等服务器确认就先用本地输入预测执行（减少延迟），服务器帧到达后对比验证，不一致就回滚到最后一个确认帧重新计算。

---

## 设计意图

### 为什么需要预测？

帧同步的服务器只是"输入中转站"，客户端必须等服务器的帧到来才能推进逻辑。网络延迟通常 50-200ms，如果干等，按下按键后 200ms 才有反应——体验不可接受。

**解决方案**：客户端预测——把自己本地的输入直接当成下一帧，不等服务器。

```
时间轴：
  客户端 帧100(预测) ~~帧101(预测)~~帧102(预测)  ← 不等服务器
  服务器                             帧101到达！
  对比: 客户端预测的帧101 vs 服务器帧101
    ├── ✅ 一致 → 预测正确，确认帧101
    └── ❌ 不一致 → 回滚！退回帧100重新算
```

---

## 接口层 — 关键数据结构

```csharp
// E:\帧同步\Script\Battle\ClientOnly\Manager\RemoteBattleController.cs:8-106
public class RemoteBattleController : IBattleController
{
    // ----- 预测队列 -----
    - Queue<MatchEntity> _predictMatchQueue;  // 预测的游戏状态
    - Queue<FrameBuffer.Frame> _predictInputQueue;  // 预测的输入帧

    // ----- 网络控制器 -----
    - RemoteBattleNetworkController _battleNetworkMgr;

    // ----- 性能指标 -----
    + PredictFrame       // 当前预测帧号
    + RollbackCount      // 回滚次数
    + LoseFrameCount     // 丢帧次数
    + RollbackCalcCount  // 回滚中重算的帧数
}
```

> 📂 **[点此打开 UML 图](../charts-output/006-联网预测回滚流程图.html)**

---

## 设计逻辑（核心）

### ReCalculate — 核心对比逻辑

```csharp
// E:\帧同步\Script\...\RemoteBattleController.cs:177-324
private int ReCalculate(Queue<FrameBuffer.Frame> input, ref FrameBuffer.Frame lastInput)
{
    while (input.Count > 0)
    {
        lastInput = input.Dequeue();  // 取出一个服务器帧

        // ① 检查自己发的帧是否被服务器丢弃
        if (lastInput.frame == _lastSendPlayerInputFrame
            && hasSelfInput
            && !testInput.Compare(_lastSendPlayerInput))
        {
            _needResend = true;    // 需要重发
            LoseFrameCount++;      // 丢帧计数
        }

        if (_predictInputQueue.Count > 0)
        {
            var predictInput = _predictInputQueue.Dequeue();

            // ② 对比
            if (CompareInput(ref lastInput, ref predictInput) && !needCacluate)
            {
                // ✅ 一致 — 直接使用预测结果
                confirmedMatch = _predictMatchQueue.Dequeue();
            }
            else
            {
                // ❌ 不一致 — 丢弃预测，触发回滚
                _matchController.matchEntityPool.Release(
                    _predictMatchQueue.Dequeue());
                needCacluate = true;
            }
        }

        if (needCacluate)
        {
            // ③ 从确认帧重新计算
            RefreshMatchEntity(confirmedMatch);
            UpdateGameState(confirmedMatch);
            count++;
        }
    }
}
```

### 回滚后的重预测

回滚发生后，不只是重新算当前帧——还要把排队中的后续预测帧**用新的网络输入重新预测一遍**：

```csharp
if (needCacluate)
{
    confirmedMatch.CopyTo(_matchController.predictMatchEntity);
    for (int i = 0; i < cacheCnt; i++)
    {
        // 每一帧：替换其他玩家的 Input 为服务器值，重新跑 UpdateMatchState
        UpdateMatchState(predictMatchEntity, ref predictInput);
    }
}
```

### MD5 校验

```csharp
// 每 200 帧发一次 MD5 给服务器校验
if (matchEntity.frame % BattleSetting.MD5CheckFrames == 0)
{
    RemoteBattleNetworkController.instance.SendCheckInfo(matchEntity, player);
}
```

---

## ⚠️ 实践注意

1. **回滚只发生在逻辑层**——渲染层通过 `viewMatchEntity` 插值，玩家通常感知不到回滚。
2. **丢帧不等于掉线**——`LoseFrameCount` 统计的是"与预测不一致"的次数，不是网络断连。
3. **重连需要追帧**——`DoFastLogicUpdate()` 加速回放历史帧赶上服务器。

---

## 关联笔记

- [[002-预测与回滚机制详解]] — ReCalculate 更深入的拆解
- [[003-FrameEngine帧循环引擎]] — 逻辑线程驱动 RemoteBattleController
- [[001-LocalBattleController单机控制器]] — 对比网络版和单机版的区别

---

*记录时间：2026-06-30*

# P1-B 正常帧与回滚共用模拟设计

## 阶段目标

完成一个可运行、可自动验证的最小回滚原型：正常逻辑帧和回滚重演必须通过同一个确定性模拟入口，整帧执行完玩家、持球/投篮和篮球物理后保存完整世界快照。预测错误发生后，从错误帧之前的完整快照恢复，并用纠正输入重演到当前帧。

本阶段不扩展 Hash、不修改网络协议、不重写预测算法，也不制造可视化延迟开关。这些内容留到后续复盘开发。

## 方案比较

### 方案 A：新增深模块 `FrameSimulationSystem`（采用）

提供一个 `Step` 接口，内部完成玩家状态、移动、持球/投篮和篮球物理。正常帧与回滚各自只负责提供输入、保存快照和同步表现。

- 优点：正常与重演天然共用实现；确定性逻辑可直接做 EditMode 测试；以后新增篮球机制只修改一个模块。
- 代价：需要把现有 `GameController` 中的部分逻辑搬入新模块。

### 方案 B：在 `GameController` 内提取私有方法

改动文件较少，但测试无法自然跨越私有接缝，回滚正确性只能依赖场景手测。

### 方案 C：保留正常逻辑并在回滚中复制篮球代码

初始修改最少，但正常帧与重演会再次形成两套实现，后续功能必然产生漂移，拒绝采用。

## 深模块接口

```csharp
public static FrameSimulationResult Step(
    PlayerEntity[] players,
    PlayerStateMachine[] stateMachines,
    BallEntity ball,
    FrameInput[] inputs,
    FixedInt moveDistance,
    FixedInt deltaTime)
```

`Step` 内部固定顺序：

```text
恢复上一帧 Shooting 状态
→ 按输入更新玩家状态、位置和朝向
→ 处理 Held 跟随或投篮释放
→ 推进 Airborne/Free/Scored 篮球物理
→ 返回本帧事件摘要
```

`FrameSimulationResult` 只返回表现和诊断需要的信息：模拟前篮球状态、模拟前 Y、成功投篮者索引、持球不变量是否有效。它不包含或修改 Unity `Transform`。

## 正常帧时序

`GameController.OnFrameUpdate`：

1. 调用 `FrameSimulationSystem.Step`。
2. 从 `PlayerEntity.position` 同步兼容的 `_blockPosX/_blockPosZ`。
3. 根据结果打印现有球状态日志。
4. 执行现有播放结束检查和旧 Hash 日志。

`GameController.OnPostFrameUpdate`：

1. 把玩家与篮球逻辑状态同步到 Unity 表现对象。
2. 调用 `TakeWorldSnapshot`，确保快照位于完整逻辑帧结束之后。

## 回滚时序

1. 从 `errorFrame - 1` 向前查找完整世界快照。
2. 找到时恢复玩家与篮球全部动态状态，从下一帧开始重演。
3. 找不到时恢复帧 0 执行前的完整初始世界，并从帧 0 重演。
4. 每个重演帧都调用同一个 `FrameSimulationSystem.Step`。
5. 每个重演帧结束后重新保存完整世界快照。
6. 全部重演完成后只同步一次 Unity 表现，并输出一条回滚摘要日志。

远端输入选择沿用当前原型规则：优先使用该远端目标帧的真实输入；缺失时使用本次纠正输入。P1-B 不改变预测历史算法。

## 兼容状态

`_blockPosX/_blockPosZ` 暂时保留给现有 `MD5Checker`、调试器和旧代码，但不再作为玩家位置的权威来源；每次完整模拟或恢复后都从 `PlayerEntity.position` 单向同步。

旧 XZ 快照方法仍保留在 `PredictionSystem`，但 `GameController` 在 P1-B 后不再调用它们。P1 完成复盘时再决定删除时机。

## 测试与验收

新增纯 EditMode 测试：

1. 持球者输入投篮后，同一 `Step` 内完成球权释放、`Airborne` 和第一次篮球物理推进。
2. 构造“权威路径在帧 1 投篮、预测路径未投篮”的分歧。
3. 预测路径恢复帧 0 完整快照，以正确投篮输入重演帧 1～2。
4. 重演后的两名玩家与篮球全部动态字段逐个原始值等于权威路径。
5. 全部既有 EditMode 测试通过，编译错误为零。
6. P0 本地场景仍能完成持球、投篮、进球、下落和落地。

人工验收由帅老大在阶段完成后统一执行。

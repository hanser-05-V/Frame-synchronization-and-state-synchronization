# 逻辑帧与渲染帧分离设计

## 目标

保持确定性逻辑帧、输入预测、回滚重演、完整世界快照和 WorldHash 全部不变，将 Unity Transform 的显示更新从 30FPS 逻辑阶梯中分离出来。渲染层统一落后一个逻辑帧，在相邻两个已完成逻辑帧之间插值，使本地球员、远端球员和篮球在正常移动及回滚纠正期间都保持视觉连续。

本阶段只解决位置显示。逻辑碰撞、球权、投篮、得分、网络输入和 Hash 仍只读取确定性实体，不读取 Transform 或插值结果。

## 已确认根因

`FrameEngine` 每 33ms 执行一次逻辑帧；Unity 的 `LateUpdate` 每个渲染帧执行。当前 `SyncPresentationFromLogic` 虽然已经移动到 `LateUpdate`，但它只读取最新逻辑坐标：

- 本地球员始终直接使用最新目标。
- 未处于回滚纠正期时，`PresentationCorrectionSmoother.Evaluate` 也直接返回最新目标。
- 因此多个渲染帧会重复显示同一坐标，下一逻辑帧到达时再跳到新坐标，形成 30FPS 阶梯。
- 现有 100ms smoother 只衰减回滚造成的视觉误差，无法填充正常逻辑帧之间的渲染位置。
- Editor 的渲染帧率与 Console 开销更不稳定，所以阶梯感通常比打包客户端明显。

## 方案比较

### 方案 A：前后逻辑帧缓冲插值（采用）

表现层保存最近两个已完成逻辑帧的位置，使用逻辑帧剩余时间计算 `alpha`，每个 `LateUpdate` 执行线性插值。

优点：结果有界、不会无限拖尾；所有渲染对象使用同一时间轴；追帧后仍可保留最终两个正确端点；不会污染确定性状态。代价：画面固定落后约一个逻辑帧，即约 33ms。帅老大已接受该取舍。

### 方案 B：每帧 Lerp 到最新逻辑目标（不采用）

实现较少，但收敛时间取决于渲染帧率，可能持续拖尾，无法定义严格的前后帧关系；不同帧率下手感不同。

### 方案 C：对最新逻辑状态进行外推（本阶段不采用）

可以减少一帧显示延迟，但停止、换向和网络纠正时容易过冲，需要额外速度预测与误差回收。当前原型优先选择可验证的标准插值。

## 模块边界

新增纯表现深模块 `PresentationFrameInterpolator`。模块内部拥有固定大小的前一逻辑帧和当前逻辑帧缓存，对调用方隐藏帧端点切换、初始化、插值、回滚替换和边界钳制。

建议接口：

```csharp
public sealed class PresentationFrameInterpolator
{
    public PresentationFrameInterpolator(int playerCount);
    public bool IsReady { get; }

    public void Reset(
        int frameID,
        PlayerEntity[] players,
        BallEntity ball);

    public void PushLogicFrame(
        int frameID,
        PlayerEntity[] players,
        BallEntity ball);

    public void ReplaceAfterRollback(
        int frameID,
        PlayerEntity[] players,
        BallEntity ball);

    public void Evaluate(
        float alpha,
        Vector3[] playerPositions,
        out Vector3 ballPosition);
}
```

模块约束：

- 构造时一次性分配固定数组；正常逻辑帧和渲染帧不得产生托管分配。
- 缓存只包含转换后的表现坐标和逻辑帧号，不保存或修改同步实体。
- 第一次 `Reset` 后前后端点相同，避免初始化跳变。
- `PushLogicFrame` 将当前端点移动为前一端点，再复制新的完整逻辑端点。
- 同一帧或倒退帧不得破坏时间顺序；回滚改写必须使用专用入口。
- `Evaluate` 将 NaN、无穷或越界 alpha 钳制到 `[0, 1]`，输出数组长度不正确时明确抛出参数异常。
- 篮球端点来自确定性篮球坐标；Held 状态下最终展示仍继承持球球员的视觉修正，不能与持球人分离。

`PresentationCorrectionSmoother` 保留，职责收窄为“回滚后视觉误差衰减”。它叠加在正常帧插值结果之上，不能代替前后帧插值。

## 渲染时间比例

`FrameEngine` 在执行完本轮所有逻辑追帧后，计算只读的表现比例：

```text
alpha = clamp01((elapsedMs - lastLogicMs) / frameIntervalMs)
```

含义：

- `alpha = 0`：显示前一逻辑帧端点。
- `alpha = 0.5`：显示两帧中点。
- `alpha = 1`：到达当前逻辑帧端点。
- 下一逻辑帧执行后，旧的当前帧成为新的前一帧，因此显示轨迹连续。

该比例只服务表现层，不写入快照、输入、回滚数据或 WorldHash。暂停时保持最后显示结果，不继续推进插值或回滚误差计时。

## 正常帧数据流

1. `FrameEngine.ExecuteOneFrame` 请求输入并触发确定性逻辑推进。
2. `FrameSimulationSystem.Step` 更新球员与篮球实体。
3. 完整世界快照和正常 WorldHash 仍在原帧完成时点生成。
4. 完整逻辑状态确认后，`PresentationFrameInterpolator.PushLogicFrame` 复制新的表现端点，不写 Transform。
5. 本轮可能执行多次追帧；模块最终保留最后两个已完成逻辑帧。
6. `LateUpdate` 只执行一次：读取 `FrameEngine` 的表现 alpha，计算所有对象的基础插值位置。
7. 在基础位置上叠加尚未结束的回滚视觉误差，然后统一写入 Transform 和 FrameDebugger 的渲染位置。

所有球员统一使用前后帧插值，因此显示延迟和运动节奏一致。输入采集、预测和逻辑响应仍在原逻辑帧立即发生，只有画面晚约一帧。

## 回滚数据流

1. 回滚开始前保存当前肉眼显示位置。
2. `FrameReplaySystem.Replay` 恢复并重演确定性世界；重演过程不写 Transform。
3. 重演失败时不替换表现端点，也不启动新的视觉纠正。
4. 重演成功后，用最终正确世界调用 `ReplaceAfterRollback`，将前后端点同时重置为回滚后的最终位置，避免把错误预测端点继续插入后续画面。
5. 现有 `PresentationCorrectionSmoother` 从回滚前显示位置开始，把它与新基础位置的差值在约 100ms 内衰减为零。
6. 下一正常逻辑帧到达后，重新形成“回滚后最终帧 → 新逻辑帧”的正常插值对。
7. 快照和 WorldHash 始终使用回滚后的确定性实体，与表现缓存和视觉误差无关。

## 篮球与持球关系

- `Held`：先得到持球球员的最终显示位置，再把篮球相对持球人的确定性挂点偏移叠加上去，保证球与人共用同一插值和回滚修正。
- `Airborne`、`Free`、`Scored`：篮球使用自身前后逻辑端点插值；若发生回滚，再叠加自身的视觉误差衰减。
- 状态切换只影响表现目标选择，不参与逻辑判定。
- 投篮、命中、落地和球权不得读取插值后的篮球 Transform。

## 生命周期

以下路径不执行渐变，直接重置前后端点并清除回滚视觉误差：

- 初始化。
- Reset/Clear。
- 回放开始、停止或自动结束。
- 世界对象重新创建。
- 表现模块尚未准备完成。

暂停时冻结当前显示；恢复后从冻结位置继续。追帧可以一次推进多个逻辑帧，但一个 Unity 渲染帧只写一次 Transform。

## 自动测试

新增 EditMode 测试至少覆盖：

- 初始化后前后端点一致。
- 连续推入两个逻辑帧，在 alpha 为 `0`、`0.5`、`1` 时得到准确位置。
- 多逻辑帧追帧后只保留最终两个端点。
- 所有球员使用相同 alpha，不发生本地直跳、远端插值的不一致。
- Held 篮球在整个插值区间与持球人保持确定性相对偏移。
- Airborne、Free、Scored 篮球按自身端点插值。
- 回滚替换后旧预测端点被清除，第一帧基础目标为纠正后的世界。
- 回滚误差衰减与正常插值组合后连续，最终精确归零。
- 暂停不推进插值或纠正计时。
- 非法 alpha、输出数组长度和非法玩家数量按约定处理。
- 现有快照、重演、预测、WorldHash 与展示平滑测试全部保持通过。

## 人工验收

在 Editor 与打包客户端双端验证：

- 正常持续移动、启动、停止和八方向换向不再呈现 30FPS 阶梯抽搐。
- 本地与远端球员均连续移动，允许约 33ms 的统一显示延迟，不允许持续漂浮或拖尾。
- 追帧及连续回滚时无硬跳，约 100ms 内视觉误差归零。
- 远端持球时篮球不脱离球员；投篮后篮球飞行连续。
- 最终双端逻辑位置正确，同一规范帧 WorldHash 一致。
- Reset、回放切换和暂停恢复无异常跳变，Console 无新错误。

## 风险与明确不做

- 低渲染帧率本身无法由插值消除；本阶段保证时间模型正确，不承诺掩盖严重性能卡顿。
- 固定一逻辑帧显示延迟是采用标准插值的明确代价。
- 回滚超过平滑能力时仍可能看到快速纠正，但不得出现瞬时硬跳或永久偏差。
- 不修改逻辑帧率、输入延迟、服务器协议、预测算法、回滚算法、快照结构或 WorldHash schema。
- 不实现外推、动画混合、旋转插值、摄像机平滑、物理 Rigidbody 插值或网络自适应缓冲。
- 不修改 `E:\帧同步`，不自动 commit、merge、push、reset、clean 或 checkout 覆盖。

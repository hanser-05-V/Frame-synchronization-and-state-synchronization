# 回滚表现平滑设计

## 目标

在不改变确定性逻辑、输入预测、回滚重演、完整世界快照和 WorldHash 的前提下，消除远端球员与篮球在 3～4 帧回滚纠正时产生的肉眼硬跳。本地球员必须继续零延迟响应，双端最终逻辑位置与相同帧 Hash 必须保持一致。

## 已确认根因

当前逻辑回滚能够正确收敛，但 `GameController` 在正常逻辑帧和 `DoRollback` 完成后都会把 Transform 直接写成最新逻辑坐标。领先客户端在远端输入变化时通常先按旧输入预测 3～4 帧，真值到达后一次重演这些帧，Transform 随即跨越整段纠正距离。打包客户端当前稍落后，常在执行同号帧前收到 Editor 输入，因此较少触发同类回滚。

相同帧 WorldHash 已证明逻辑世界最终一致；本阶段只处理表现层误差，不修改同步结果。

## 方案比较

### 方案 A：视觉误差衰减（采用）

逻辑立即回滚。表现层记录回滚前显示位置与回滚后逻辑位置的差值，并在约 100ms 内把该误差平滑衰减为零。逻辑目标继续移动时，显示对象仍跟随最新目标，只叠加逐渐消失的视觉偏移。

优点：本地操作无额外延迟；最终严格追到逻辑坐标；只影响表现层；连续回滚可以从当前显示位置重新接续。

### 方案 B：持续 Lerp 到逻辑位置（不采用）

每个渲染帧都对 Transform 和逻辑位置做插值。实现较简单，但远端对象会永久落后逻辑目标，形成漂浮和拖尾感，且很难定义准确追平时点。

### 方案 C：增加输入延迟（不采用）

延迟执行逻辑帧以等待远端输入。它能减少回滚频率，但会增加操作延迟，也无法处理所有网络抖动，不符合本地零延迟目标。

## 深模块与接口

新增纯表现深模块 `PresentationCorrectionSmoother`。它隐藏视觉误差、计时、衰减曲线、连续纠正合并和结束归零；调用方只需要提交目标位置和渲染帧时间。

建议接口：

```csharp
public sealed class PresentationCorrectionSmoother
{
    public PresentationCorrectionSmoother(float durationSeconds);
    public bool IsCorrecting { get; }

    public void BeginCorrection(
        Vector3 currentDisplayPosition,
        Vector3 correctedTargetPosition);

    public Vector3 Evaluate(Vector3 currentTargetPosition, float deltaTime);
    public void Snap();
}
```

接口约束：

- `durationSeconds` 必须大于零，默认由 `GameController` 配置为 `0.1f`；构造时传入零、负数、NaN 或无穷值均抛出 `ArgumentOutOfRangeException`。
- `BeginCorrection` 只建立误差，不消耗时间；紧接着的第一次 `Evaluate` 必须等于调用前肉眼看到的位置，即使该次传入了非零 `deltaTime`，也不允许在启动纠正的同一渲染帧跳变。
- `Evaluate` 使用当前目标而非回滚时的静态目标，因此对象移动期间仍能追随逻辑运动。
- `Evaluate` 收到负 `deltaTime` 时按零处理，避免计时倒退；NaN 或无穷值按零处理，避免污染 Transform。
- 100ms 到期时误差必须精确归零，不保留指数插值尾巴。
- `Snap` 清除正在进行的纠正；下一次 `Evaluate` 直接返回目标位置。
- 浮点坐标和渲染时间仅存在于该表现模块，不进入任何同步状态。

## 衰减曲线

保存初始视觉偏移：

```text
offset = currentDisplayPosition - correctedTargetPosition
```

在持续时间内计算归一化进度，并使用 SmoothStep 权重把偏移从 100% 衰减到 0：

```text
display = currentTargetPosition + offset * remainingWeight
```

到期后直接返回 `currentTargetPosition` 并清零内部状态。连续回滚时以当前 Transform 作为新的 `currentDisplayPosition`，因此不会产生第二次硬跳。

## GameController 数据流

### 正常帧

1. `FrameSimulationSystem.Step` 更新确定性世界。
2. `OnPostFrameUpdate` 保存完整快照，但不在每个追帧逻辑帧中反复硬写 Transform。
3. Unity 渲染阶段读取最新逻辑目标，只执行一次表现同步。
4. 本地球员直接使用逻辑位置；远端球员通过其 smoother 求显示位置。
5. FrameDebugger 接收最终显示位置，不改变逻辑记录。

### 回滚帧

1. `DoRollback` 开始前记录远端球员和篮球当前 Transform 位置。
2. `FrameReplaySystem.Replay` 立即恢复并重演确定性世界。
3. 重演成功后，以回滚前显示位置和回滚后最新目标调用 `BeginCorrection`。
4. 当帧渲染只显示 smoother 结果，不直接硬切到回滚后逻辑位置。
5. 快照和 WorldHash 继续使用回滚后的逻辑坐标，与 smoother 无关。

若 Replay 失败，不启动新的视觉纠正，并保留现有错误处理。

## 球员规则

- 本地球员：始终直接显示逻辑位置，不创建或累积视觉误差。
- 远端球员：只在回滚成功后开始约 100ms 的误差衰减；无纠正时直接显示逻辑位置。
- 本地/远端身份必须使用服务器分配的 `LocalPlayerIndex`，不能依赖 Editor/Build 环境判断。
- 连续回滚时从当前显示位置重新计算偏移。

## 篮球规则

- 本地持球：篮球直接跟随本地球员的逻辑持球挂点，保持本地操作即时。
- 远端持球：篮球目标由平滑后的远端球员位置加确定性持球相对偏移得到，确保球与持球人一起移动。
- `Airborne`、`Free`、`Scored`：篮球使用独立 smoother，根据回滚前显示位置和回滚后篮球逻辑位置执行约 100ms 纠正。
- 回滚造成持球状态切换时，以回滚前球的显示位置开始纠正，结束后严格落到新状态的表现目标。
- 篮球物理、命中、落地和持有者索引仍完全由确定性逻辑决定。

## 主动瞬移与生命周期

以下操作属于用户主动切换，不执行视觉平滑：

- 初始化世界。
- `ResetPositions`。
- 录制回放开始、停止或恢复起始位置。
- 对象重建或表现模块尚未初始化。

这些路径调用 `Snap` 并立即把 Transform 对齐逻辑坐标，避免把旧世界的误差带入新世界。

暂停期间调用方传入零 `deltaTime`，不推进平滑计时；恢复后从暂停前的显示位置继续。表现更新使用 `Time.deltaTime`，不使用墙钟时间或固定逻辑帧时间。

## 配置与日志

`GameController` 增加私有序列化配置：

```csharp
[SerializeField] private float _rollbackVisualSmoothingSeconds = 0.1f;
```

默认 100ms。该配置不参与 Hash。正常运行不增加逐渲染帧日志；现有预测错误、Rollback 和 WorldHash 日志足以定位纠正来源，避免 Editor Console 开销反向放大卡顿。

## 自动测试

新增 EditMode 测试覆盖：

- 未开始纠正时，`Evaluate` 精确返回目标位置。
- `BeginCorrection` 后第一帧保持原显示位置。
- 100ms 到期后精确返回最新目标并结束纠正。
- 目标在纠正期间继续移动时，显示位置保留衰减偏移并最终追平。
- 连续两次 `BeginCorrection` 从当前显示位置接续，不出现不连续。
- `Snap` 立即清除误差。
- 非法持续时间抛出预期异常；负数、NaN 和无穷 `deltaTime` 均按零处理且不污染结果。
- 远端持球目标使用平滑球员位置加持球相对偏移。
- 本地球员路径不进入 smoother。
- 现有完整 EditMode 测试、回滚追平和 WorldHash 参考值全部保持通过。

## 人工验收

在服务器正常、100ms 和 200ms 延迟模式分别验证：

- 两端相同帧最终 WorldHash 一致，静止后的逻辑位置一致。
- 远端球员启动、停止和换向时无明显硬跳。
- 本地球员操作响应无新增延迟或漂浮感。
- 远端持球时篮球不脱离持球人。
- 投篮、飞行、得分、落地阶段发生回滚时篮球无明显硬跳。
- 连续快速按键触发多次回滚时，画面保持连续并在约 100ms 内追平。
- Reset、回放切换继续立即对齐，Console 无异常。

## 风险与明确不做

- 平滑只隐藏短时表现误差，不能掩盖 WorldHash 不一致；逻辑不同步仍必须按错误处理。
- 100ms 内显示位置可能与逻辑位置存在有限偏差，因此碰撞、投篮判定和球权绝不能读取 Transform。
- 本阶段不增加输入延迟、服务器状态仲裁、动态时间同步、动画混合、旋转插值、摄像机平滑或网络 Hash 传输。
- 不修改 `E:\帧同步`，不自动 commit、merge、push、reset、clean 或 checkout 覆盖。

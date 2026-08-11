# Rollback Velocity Correction And Diagonal Speed Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 消除回滚表现纠正的首帧冻结与突兀反向抽动，并让八方向确定性移动保持相同标称速度。

**Architecture:** 同步逻辑只在 `FrameSimulationSystem` 对斜向输入应用预先量化的固定点系数，不让浮点数进入模拟、快照或 World Hash。表现层继续与逻辑层分离，`PresentationCorrectionSmoother` 保存显示位置和显示速度，以带初速度的三次 Hermite 误差曲线消除回滚偏差，并对每个渲染帧的误差变化限速；超大误差直接跳到逻辑目标。

**Tech Stack:** Unity 2022.3.62f2、C#、NUnit EditMode、`FixedInt`、Unity `Vector3`

**Approved specification:** `Project/项目中疑问解答/Q004-正常帧同步如何处理预测失败与渲染纠正.md`

**Workspace constraint:** 直接在帅老大指定的 `E:\帧同步_RouteC`、`delivery/route-c` 分支工作，保留现有全部未提交成果。禁止 commit、merge、push、reset、clean 和 checkout 覆盖，因此本计划没有提交步骤。

---

## File Map

- Modify: `Project/Frame Synchronization/Assets/Scripts/Gameplay/FrameSimulationSystem.cs` — 对方向 2、4、6、8 的两个移动轴应用固定点 `1/sqrt(2)` 系数。
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/FrameSimulationSystemTests.cs` — 覆盖单轴、斜向速度和回滚重演确定性。
- Modify: `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationCorrectionSmoother.cs` — 保存显示速度、取消首帧冻结，执行带速度连续性的限速误差衰减。
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationCorrectionSmootherTests.cs` — 覆盖首帧推进、初始运动连续、限速、完成、重复纠正和大误差跳转。
- Modify: `Project/Frame Synchronization/Assets/Scripts/GameController.cs` — 暴露最大平滑时长、最大纠正速度和直接跳转距离，并让球员与篮球使用同一组参数。

---

### Task 1: Deterministic Diagonal Normalization

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/FrameSimulationSystemTests.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/Gameplay/FrameSimulationSystem.cs`

- [x] **Step 1: Write the failing diagonal-distance test**

新增测试分别执行方向 3（右）和方向 2（右上），断言单轴位移为完整 `moveDistance`，斜向两个分量都小于完整距离，并断言斜向平方距离与单轴平方距离只存在固定点量化容差：

```csharp
[Test]
public void Step_DiagonalInput_HasSameNominalSpeedAsCardinalInput()
{
    World cardinal = CreateWorld(1);
    World diagonal = CreateWorld(1);
    FixedInt moveDistance = FixedInt.FromInt(1);

    FrameSimulationSystem.Step(
        cardinal.players,
        cardinal.stateMachines,
        cardinal.ball,
        CreateInputs(player0Direction: 3),
        moveDistance,
        CourtConstant.LogicDeltaTime);
    FrameSimulationSystem.Step(
        diagonal.players,
        diagonal.stateMachines,
        diagonal.ball,
        CreateInputs(player0Direction: 2),
        moveDistance,
        CourtConstant.LogicDeltaTime);

    int cardinalX = cardinal.players[0].position.x._raw + 3000;
    int diagonalX = diagonal.players[0].position.x._raw + 3000;
    int diagonalZ = diagonal.players[0].position.z._raw;
    long cardinalSquared = (long)cardinalX * cardinalX;
    long diagonalSquared = (long)diagonalX * diagonalX +
        (long)diagonalZ * diagonalZ;

    Assert.AreEqual(moveDistance._raw, cardinalX);
    Assert.AreEqual(707, diagonalX);
    Assert.AreEqual(707, diagonalZ);
    Assert.LessOrEqual(
        System.Math.Abs(diagonalSquared - cardinalSquared),
        2000L);
}
```

- [x] **Step 2: Run the focused test and verify RED**

Run the repository's temporary Roslyn/NUnit-compatible focused runner against `FrameSimulationSystemTests`.

Expected: the new test fails because the old implementation produces diagonal components `(1000, 1000)` and squared speed `2,000,000` instead of approximately `1,000,000`.

- [x] **Step 3: Implement the minimal deterministic normalization**

Use an integer raw constant; do not call `FromFloat`, square root, or normalization per simulation frame:

```csharp
private static readonly FixedInt DiagonalScale = new FixedInt(707);

FixedInt directionScale =
    DirX[direction] != 0 && DirZ[direction] != 0
        ? DiagonalScale
        : FixedInt.One;
FixedInt scaledMoveDistance = moveDistance * directionScale;
FixedInt moveX = scaledMoveDistance * FixedInt.FromInt(DirX[direction]);
FixedInt moveZ = scaledMoveDistance * FixedInt.FromInt(DirZ[direction]);
```

- [x] **Step 4: Run focused and replay tests and verify GREEN**

Expected: diagonal-distance test passes; existing replay/world-state tests remain green because normal execution and replay share the same `Step` entry point.

---

### Task 2: Velocity-Continuous Bounded Presentation Correction

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationCorrectionSmootherTests.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationCorrectionSmoother.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/GameController.cs`

- [x] **Step 1: Replace the obsolete frozen-frame assertion with failing behavior tests**

Required tests:

```csharp
[Test]
public void BeginCorrection_FirstEvaluate_ConsumesTimeAndDoesNotFreeze()
{
    var smoother = CreateSmoother();
    var previousDisplay = new Vector3(1f, 0f, 0f);

    smoother.BeginCorrection(previousDisplay, Vector3.zero);
    Vector3 result = smoother.Evaluate(Vector3.zero, 0.016f);

    Assert.Less(result.x, previousDisplay.x);
    Assert.Greater(result.x, 0f);
}

[Test]
public void BeginCorrection_MovingDisplay_FirstStepPreservesForwardMotion()
{
    var smoother = CreateSmoother();
    smoother.Evaluate(Vector3.zero, 0.016f);
    smoother.Evaluate(new Vector3(0.16f, 0f, 0f), 0.016f);

    smoother.BeginCorrection(
        new Vector3(0.16f, 0f, 0f),
        new Vector3(-0.1f, 0f, 0f));
    Vector3 result = smoother.Evaluate(
        new Vector3(-0.1f, 0f, 0f),
        0.016f);

    Assert.Greater(result.x, 0.16f);
}
```

Also add tests proving correction displacement is bounded by `maximumCorrectionSpeed * deltaTime`, completion returns the latest moving target exactly, invalid delta time does not advance, repeated correction starts from the supplied current display, and error at or above `snapDistance` jumps on the next evaluation.

- [x] **Step 2: Run the focused tests and verify RED**

Expected: the first-frame test fails because the old `_returnStartDisplayOnNextEvaluate` branch returns the unchanged display; the motion-continuity test fails because the old smoother does not track display velocity.

- [x] **Step 3: Implement display-state tracking and bounded Hermite error decay**

Constructor contract:

```csharp
public PresentationCorrectionSmoother(
    float minimumDurationSeconds,
    float maximumDurationSeconds,
    float maximumCorrectionSpeed,
    float snapDistance)
```

State and behavior:

```csharp
private Vector3 _currentDisplayPosition;
private Vector3 _previousTargetPosition;
private Vector3 _displayVelocity;
private Vector3 _startOffset;
private Vector3 _initialRelativeVelocity;
private float _activeDurationSeconds;
private float _elapsedSeconds;
private bool _hasDisplayState;
private bool _initializeCorrectionVelocity;
```

- `Evaluate` while idle returns the target and records display velocity from consecutive evaluations.
- `BeginCorrection` preserves the recorded display velocity, records the corrected target as the new target baseline, and does not create a frozen-frame flag.
- At the first corrected evaluation, derive target velocity only from motion after the corrected baseline, then set `initialRelativeVelocity = displayVelocity - targetVelocity`.
- Moving away from the corrected target uses the configured maximum shaping duration so the display decelerates before reversing; moving toward a nearby target clamps the axial tangent to the monotonic Hermite boundary `3 * distance / duration`, preventing overshoot and pullback.
- Calculate the desired error with cubic Hermite basis from `startOffset` and `initialRelativeVelocity`; its endpoint error and relative velocity are both zero.
- Clamp the per-frame error change magnitude to `maximumCorrectionSpeed * safeDeltaTime`.
- Choose active shaping duration between the configured minimum and maximum according to error distance and correction-speed budget.
- Continue consuming any bounded residual after the Hermite interval; finish only when the residual is negligible, then return the latest target exactly.
- If initial error is greater than or equal to `snapDistance`, leave correction inactive so the next `Evaluate` returns the corrected target immediately.
- Reject duration above `0.2s` and any speed/time/snap-distance combination that cannot complete; this prevents a positive-but-unusable speed from leaving `IsCorrecting` active forever.
- `Snap` clears correction and tracked velocity.

- [x] **Step 4: Wire the same parameters for players and ball**

Add serialized presentation-only settings to `GameController`:

```csharp
[SerializeField] private float _rollbackVisualMaxSmoothingSeconds = 0.2f;
[SerializeField] private float _rollbackVisualMaxCorrectionSpeed = 10f;
[SerializeField] private float _rollbackVisualSnapDistance = 2f;
```

Validate them in `InitializePresentationSmoothing`; invalid values fall back to `0.2f`, `10f`, and `2f`. Construct every player smoother and the basketball smoother with the same values. These floats remain presentation-only and never enter snapshots or World Hash.

- [x] **Step 5: Run focused tests and verify GREEN**

Expected: all correction-smoother, interpolation, target-resolver, simulation, replay, snapshot, and WorldHash-focused tests pass.

---

### Task 3: Verification And Independent Review

**Files:**
- Verify all changed files; no commit or push.

- [x] **Step 1: Compile runtime and EditMode test sources outside the Unity UI**

Use the locally installed Unity managed assemblies and NUnit assemblies. Expected: zero C# compiler errors.

- [x] **Step 2: Run the complete non-engine focused suite**

Expected: all previously covered RouteC tests plus the new regression tests pass with zero failures.

- [x] **Step 3: Inspect repository boundaries**

Run whitespace validation, inspect the exact diff, confirm branch and HEAD are unchanged, and verify the old `E:\帧同步` branch/HEAD/status/fingerprint using read-only commands.

- [x] **Step 4: Request independent code review**

Reviewer checks deterministic movement, presentation-only isolation, correction completion, ball/player shared behavior, invalid configuration handling, and regression risk. Fix every Critical or Important issue before handoff.

- [x] **Step 5: Provide Unity manual acceptance steps without controlling Unity**

帅老大在空闲时执行：等待 Unity 编译完成、确认 Console 无错误、运行完整 EditMode、启动双端，验证普通移动、连续突变输入、远端球员、自由球、持球切换、斜向速度和 WorldHash 双端一致性。

---

## Execution Evidence

- RED：旧斜向实现返回 `(1000, 1000)`，未归一化；旧纠错首帧保持 `1.0` 不动。
- RED：旧纠错在远离目标场景第二帧从 `0.255` 回落到 `0.245`；在接近目标场景首帧从 `0.16` 穿越到 `0.296`，目标仅为 `0.21`。
- RED：旧构造接受不可完成的极小速度/参数组合，且单参数 `0.2s` 错误扩张最大时长到 `0.4s`。
- GREEN：最终 runtime 与 EditMode 测试程序集经 Unity Roslyn 编译，退出码为 0。
- GREEN：脱离 Unity 原生运行时可执行的测试 `145/145` 通过；8 项依赖 Unity native logging 的测试留待 Unity 完整 EditMode 执行。
- REVIEW：独立复审 Critical 0、Important 0，代码 Ready；阶段最终验收仍等待 Unity 全量测试与双端人工检查。

---

## Follow-up Stage: P1-E Remote Presentation Buffer

**Status:** 已加入开发计划，等待新窗口完成设计确认后实施。它不是本轮回滚纠错修复的一部分，不得在未确认时间线与篮球归属规则前直接改代码。

**Goal:** 在保留当前逻辑回滚、完整世界快照和 World Hash 的前提下，让远端突然变向时更少展示预测路径，从而减少“平滑回到变向点”的可见幅度。

### Candidate approaches

1. **Remote-only fixed buffer（推荐）**：本地玩家保持现有响应；远端玩家额外落后一个逻辑帧，使远端总显示延迟从约一帧提高到约两帧（约 `66ms`）。远端持球篮球必须跟随同一缓冲时间线；本地持球篮球保持本地时间线；自由、飞行和得分篮球使用一致的缓冲世界端点。优点是显著减少远端预测失败的可见距离，又不增加本地输入延迟；难点是球权切换时必须避免人球分离。
2. **Whole-world fixed buffer**：所有玩家与篮球统一再延迟一帧。实现最简单、时间线最一致，但本地玩家也会增加约 `33ms` 的显示延迟，不符合当前优先保证本地手感的目标。
3. **Adaptive remote buffer**：根据延迟、抖动或近期回滚频率动态改变缓冲深度。网络适应性最好，但会引入缓冲深度切换、时间伸缩和更多边界，本阶段不推荐首轮实现。

### Recommended boundary

- 首轮只实现固定的远端额外一帧缓冲，不做动态网络自适应。
- 不改变逻辑帧、输入预测、回滚重演、快照或 World Hash。
- 不把表现缓冲数据写入同步状态。
- 本地玩家不得因远端缓冲增加额外操作延迟。
- 远端玩家、远端持球篮球和自由/飞行篮球必须使用明确且可测试的同帧端点。
- 球权、Held/Free/Airborne/Scored 转换时不得闪跳、重复纠正或人球分离。
- 现有 `PresentationCorrectionSmoother` 保留为缓冲仍无法覆盖预测错误时的第二层保护。

### Required design gate in the next window

新窗口必须先只读分析 `PresentationFrameInterpolator`、`PresentationTargetResolver`、`GameController.SyncPresentationFromLogic`、`BeginRollbackPresentationCorrection` 和球权状态转换。随后向帅老大确认以下推荐设计：本地保持当前显示路径；远端玩家额外缓冲一帧；篮球按状态选择本地或远端缓冲时间线；自由/飞行篮球固定使用缓冲世界端点。设计确认后再生成独立 spec 与详细 TDD 实施计划。

### Planned automated acceptance

- 缓冲保存连续、递增的逻辑帧端点，容量固定且无每帧托管分配。
- 远端输出帧比当前逻辑展示端点额外落后一帧；本地输出不增加该延迟。
- 缓冲未填满、暂停、录制回放、重置和回滚替换端点时行为明确。
- 远端 Held 篮球继承同帧持有者显示位置；状态切换没有人球分离。
- 缓冲层测试不读取或修改同步快照与 World Hash 字段。
- 现有回滚、插值、表现纠错、Replay、Snapshot 和 WorldHash 回归保持通过。

### Planned manual acceptance

- 双端普通移动仍无抖动，本地输入手感没有新增延迟。
- 远端连续快速变向时，回到变向点的可见距离明显小于当前版本。
- 远端球员、自由篮球、飞行篮球、远端持球和本地持球分别检查，无闪跳或人球分离。
- 双端最终逻辑位置及同 canonical frame 的 WorldHash 一致。

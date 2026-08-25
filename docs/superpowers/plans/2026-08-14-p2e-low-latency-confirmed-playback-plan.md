# P2-E Low-Latency Confirmed Playback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the fixed-one-frame remote Confirmed cursor with a configurable low-latency playback controller that starts at ready backlog 0, catches up smoothly, degrades explicitly on underflow/overflow, and exposes enough diagnostics for dual-client acceptance.

**Architecture:** Keep `FrameInputLedger`, Confirmed/Predicted worlds, snapshots, deterministic simulation and `WorldHash` unchanged. `ConfirmedPresentationCursor` becomes a pure presentation clock driven by a validated `ConfirmedPlaybackSettings`; `GameController` samples one final View World per Unity render frame and applies overflow/rollback correction atomically to remote player and Confirmed ball sources. `RuntimeNetworkDiagnostics` remains a default-off sidecar and correlates mapped Actual arrival frames with first visible Confirmed samples.

**Tech Stack:** Unity 2022.3.62f2, C#/.NET profile used by Unity, NUnit EditMode tests, PowerShell, existing `FrameSyncDemo` presentation and network diagnostics code.

## Global Constraints

- Canonical design: `docs/superpowers/specs/2026-08-14-p2e-low-latency-confirmed-playback-design.md`.
- Highest-priority correction basis: `docs/architecture/p2e-streetball2-16-item-correction-handoff.md`.
- Default `TargetReadyBacklog = 0`; mode `1` is configurable stability-first behavior and must expose its added-frame cost.
- `MaxReadyBacklog = 8`; the ninth complete ready interval triggers presentation overflow.
- Initial Route C tuning only: gain `0.15`, maximum speed `1.5`, smoothing `0.10s`, enter excess `0.25`, exit excess `0.10`, overflow correction maximum `0.20s`.
- Local player remains Predicted; remote player remains Confirmed; ball/holder source arbitration remains atomic and follows the existing P2-D rules.
- Do not change the 8-byte protocol, server forwarding semantics, Ledger facts, deterministic world, snapshot state, hash schema, gameplay rules or P2-F.
- Diagnostics default off and must not own or mutate logic state.
- Do not claim P2-E visual acceptance until the fixed-100ms dual-client matrix passes.
- Do not commit, merge or push. Each task ends at a review checkpoint; git actions require a new explicit instruction from 帅老大.

---

## File map

### Create

- `Project/Frame Synchronization/Assets/Scripts/Presentation/ConfirmedPlaybackSettings.cs` — serialized Route C playback parameters and validation.
- `Project/Frame Synchronization/Assets/Tests/EditMode/ConfirmedPlaybackSettingsTests.cs` — default/invalid setting contracts.
- `Project/Frame Synchronization/Assets/Tests/EditMode/ConfirmedPresentationCursorTests.cs` — waterline, catch-up, long-frame, underflow, overflow, rollback and fault contracts.
- `Project/Frame Synchronization/Assets/Tests/EditMode/ConfirmedPlaybackRuntimeAdapterTests.cs` — GameController presentation orchestration without opening sockets.

### Modify

- `Project/Frame Synchronization/Assets/Scripts/Presentation/ConfirmedPresentationCursor.cs` — continuous playback clock and observable advance result.
- `Project/Frame Synchronization/Assets/Scripts/Presentation/ViewWorldBuilder.cs` — retain the existing explicit requested-frame construction and consecutive-endpoint failure contract; do not add a second snapshot cache.
- `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationFrameInterpolator.cs` — preserve Predicted/Confirmed dual alpha and support one final sample after multi-interval clock advance.
- `Project/Frame Synchronization/Assets/Scripts/Presentation/RuntimeNetworkDiagnostics.cs` — mapped Actual-to-visible latency and playback/ball transition events.
- `Project/Frame Synchronization/Assets/Scripts/GameController.cs` — validated settings, cursor orchestration, fault handling, rollback speed refresh, overflow correction and diagnostics wiring.
- `Project/Frame Synchronization/Assets/Tests/EditMode/ViewWorldBuilderTests.cs` — exact interval and missing-history regression coverage.
- `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationFrameInterpolatorTests.cs` — dual-clock and ball-phase regression coverage.
- `Project/Frame Synchronization/Assets/Tests/EditMode/RuntimeNetworkDiagnosticsTests.cs` — new JSON fields and first-arrival correlation.
- `Project/Frame Synchronization/Assets/Tests/EditMode/P2CRuntimeOwnershipTests.cs` — settings/cursor remain presentation-only and no third logic world appears.
- `Project/Frame Synchronization/Assets/Tests/EditMode/RemotePresentationCorrectionTests.cs` — overflow/holder correction starts from current screen state and converges within `0.20s`.
- `docs/architecture/route-c-frame-sync.md`, `docs/architecture/streetball2-minimal-frame-sync-v2.md`, `Project/路线规划_街篮帧同步最小实现.md` — update only after fresh evidence and manual result are known.

Unity-generated `.meta` files for new C# assets must be retained with their corresponding source files.

---

### Task 1: Lock settings and public playback contracts

**Files:**

- Create: `Project/Frame Synchronization/Assets/Scripts/Presentation/ConfirmedPlaybackSettings.cs`
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/ConfirmedPlaybackSettingsTests.cs`
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/ConfirmedPresentationCursorTests.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/Presentation/ConfirmedPresentationCursor.cs`

**Interfaces:**

- Produces: `ConfirmedPlaybackSettings`, validated properties, and `ValidateOrReset(Action<string>)`.
- Produces: `ConfirmedPlaybackAdvance` with frame interval, alpha, backlog, speed and degradation fields.
- Produces: `ConfirmedPresentationCursor.Advance`, `EnterPresentationFault`, and `RecalculateAfterRollback` signatures consumed by later tasks.

- [ ] **Step 1: Add failing settings tests**

```csharp
[Test]
public void Defaults_AreLowLatencyRouteCTuning()
{
    var settings = new ConfirmedPlaybackSettings();

    Assert.AreEqual(0, settings.TargetReadyBacklog);
    Assert.AreEqual(8, settings.MaxReadyBacklog);
    Assert.AreEqual(0.15f, settings.CatchUpGainPerFrame);
    Assert.AreEqual(1.5f, settings.MaximumPlaybackSpeed);
    Assert.AreEqual(0.10f, settings.SpeedSmoothingSeconds);
    Assert.AreEqual(0.25f, settings.CatchUpEnterExcessFrames);
    Assert.AreEqual(0.10f, settings.CatchUpExitExcessFrames);
    Assert.AreEqual(0.20f, settings.OverflowCorrectionMaximumSeconds);
}

[Test]
public void ValidateOrReset_InvalidCombination_RestoresWholeDefaultSetOnce()
{
    var warnings = new List<string>();
    var settings = new ConfirmedPlaybackSettings(
        targetReadyBacklog: 9,
        maxReadyBacklog: 8,
        catchUpGainPerFrame: -1f,
        maximumPlaybackSpeed: 0.5f,
        speedSmoothingSeconds: 0f,
        catchUpEnterExcessFrames: 0.1f,
        catchUpExitExcessFrames: 0.25f,
        overflowCorrectionMaximumSeconds: 0f);

    settings.ValidateOrReset(warnings.Add);

    Assert.AreEqual(1, warnings.Count);
    Assert.AreEqual(0, settings.TargetReadyBacklog);
    Assert.AreEqual(8, settings.MaxReadyBacklog);
}
```

- [ ] **Step 2: Run the focused suite and save RED evidence**

```powershell
& 'C:\Unity\unity2022\Editor\Unity.exe' `
  -projectPath 'E:\帧同步_RouteC\Project\Frame Synchronization' `
  -batchmode -runTests -testPlatform EditMode `
  -testFilter 'FrameSyncDemo.Tests.ConfirmedPlaybackSettingsTests' `
  -testResults 'E:\帧同步_RouteC\TestResults-p2e-low-latency-settings-red.xml' `
  -logFile 'E:\帧同步_RouteC\p2e-low-latency-settings-red.log' -quit
```

Expected: compile/test failure because `ConfirmedPlaybackSettings` and the new cursor result API do not exist.

- [ ] **Step 3: Add the exact settings API**

```csharp
[Serializable]
public sealed class ConfirmedPlaybackSettings
{
    public const int DefaultTargetReadyBacklog = 0;
    public const int DefaultMaxReadyBacklog = 8;
    public const float DefaultCatchUpGainPerFrame = 0.15f;
    public const float DefaultMaximumPlaybackSpeed = 1.5f;
    public const float DefaultSpeedSmoothingSeconds = 0.10f;
    public const float DefaultCatchUpEnterExcessFrames = 0.25f;
    public const float DefaultCatchUpExitExcessFrames = 0.10f;
    public const float DefaultOverflowCorrectionMaximumSeconds = 0.20f;

    [SerializeField]
    private int _targetReadyBacklog = DefaultTargetReadyBacklog;
    [SerializeField]
    private int _maxReadyBacklog = DefaultMaxReadyBacklog;
    [SerializeField]
    private float _catchUpGainPerFrame = DefaultCatchUpGainPerFrame;
    [SerializeField]
    private float _maximumPlaybackSpeed = DefaultMaximumPlaybackSpeed;
    [SerializeField]
    private float _speedSmoothingSeconds = DefaultSpeedSmoothingSeconds;
    [SerializeField]
    private float _catchUpEnterExcessFrames =
        DefaultCatchUpEnterExcessFrames;
    [SerializeField]
    private float _catchUpExitExcessFrames =
        DefaultCatchUpExitExcessFrames;
    [SerializeField]
    private float _overflowCorrectionMaximumSeconds =
        DefaultOverflowCorrectionMaximumSeconds;

    public int TargetReadyBacklog => _targetReadyBacklog;
    public int MaxReadyBacklog => _maxReadyBacklog;
    public float CatchUpGainPerFrame => _catchUpGainPerFrame;
    public float MaximumPlaybackSpeed => _maximumPlaybackSpeed;
    public float SpeedSmoothingSeconds => _speedSmoothingSeconds;
    public float CatchUpEnterExcessFrames =>
        _catchUpEnterExcessFrames;
    public float CatchUpExitExcessFrames =>
        _catchUpExitExcessFrames;
    public float OverflowCorrectionMaximumSeconds =>
        _overflowCorrectionMaximumSeconds;

    public ConfirmedPlaybackSettings()
    {
    }

    public ConfirmedPlaybackSettings(
        int targetReadyBacklog,
        int maxReadyBacklog,
        float catchUpGainPerFrame,
        float maximumPlaybackSpeed,
        float speedSmoothingSeconds,
        float catchUpEnterExcessFrames,
        float catchUpExitExcessFrames,
        float overflowCorrectionMaximumSeconds)
    {
        _targetReadyBacklog = targetReadyBacklog;
        _maxReadyBacklog = maxReadyBacklog;
        _catchUpGainPerFrame = catchUpGainPerFrame;
        _maximumPlaybackSpeed = maximumPlaybackSpeed;
        _speedSmoothingSeconds = speedSmoothingSeconds;
        _catchUpEnterExcessFrames = catchUpEnterExcessFrames;
        _catchUpExitExcessFrames = catchUpExitExcessFrames;
        _overflowCorrectionMaximumSeconds =
            overflowCorrectionMaximumSeconds;
    }

    public void ValidateOrReset(Action<string> warningSink)
    {
        if (warningSink == null)
            throw new ArgumentNullException(nameof(warningSink));

        bool invalid =
            _targetReadyBacklog < 0 ||
            _maxReadyBacklog < 1 ||
            _targetReadyBacklog > _maxReadyBacklog ||
            !IsFinite(_catchUpGainPerFrame) ||
            _catchUpGainPerFrame < 0f ||
            !IsFinite(_maximumPlaybackSpeed) ||
            _maximumPlaybackSpeed < 1f ||
            !IsFinite(_speedSmoothingSeconds) ||
            _speedSmoothingSeconds <= 0f ||
            !IsFinite(_catchUpEnterExcessFrames) ||
            !IsFinite(_catchUpExitExcessFrames) ||
            _catchUpExitExcessFrames < 0f ||
            _catchUpEnterExcessFrames <=
                _catchUpExitExcessFrames ||
            !IsFinite(_overflowCorrectionMaximumSeconds) ||
            _overflowCorrectionMaximumSeconds <= 0f;
        if (!invalid)
            return;

        ResetToDefaults();
        warningSink(
            "[RouteC][ConfirmedPlayback] Invalid settings; " +
            "restored Route C defaults.");
    }

    private void ResetToDefaults()
    {
        _targetReadyBacklog = DefaultTargetReadyBacklog;
        _maxReadyBacklog = DefaultMaxReadyBacklog;
        _catchUpGainPerFrame = DefaultCatchUpGainPerFrame;
        _maximumPlaybackSpeed = DefaultMaximumPlaybackSpeed;
        _speedSmoothingSeconds = DefaultSpeedSmoothingSeconds;
        _catchUpEnterExcessFrames =
            DefaultCatchUpEnterExcessFrames;
        _catchUpExitExcessFrames =
            DefaultCatchUpExitExcessFrames;
        _overflowCorrectionMaximumSeconds =
            DefaultOverflowCorrectionMaximumSeconds;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
```

The parameterized constructor exists for tests; the parameterless constructor installs the locked defaults through field initializers. Any NaN, infinity, negative gain, speed below `1`, invalid waterline/capacity relation, non-positive duration, or invalid hysteresis ordering resets the entire set and emits exactly one warning.

- [ ] **Step 4: Define the cursor result contract without implementing behavior**

```csharp
public readonly struct ConfirmedPlaybackAdvance
{
    public ConfirmedPlaybackAdvance(
        int activeFromFrame,
        int activeToFrame,
        float alpha,
        int readyBacklog,
        float fractionalBacklog,
        float excessBacklog,
        float targetSpeed,
        float playbackSpeed,
        int crossedIntervalCount,
        long renderFrameDropCount,
        bool hasPresentationFrame,
        bool isUnderflow,
        bool isOverflowRebase,
        int droppedFromFrame,
        int droppedToFrame,
        bool isFaulted,
        int missingFromFrame,
        int missingToFrame)
    {
        ActiveFromFrame = activeFromFrame;
        ActiveToFrame = activeToFrame;
        Alpha = alpha;
        ReadyBacklog = readyBacklog;
        FractionalBacklog = fractionalBacklog;
        ExcessBacklog = excessBacklog;
        TargetSpeed = targetSpeed;
        PlaybackSpeed = playbackSpeed;
        CrossedIntervalCount = crossedIntervalCount;
        RenderFrameDropCount = renderFrameDropCount;
        HasPresentationFrame = hasPresentationFrame;
        IsUnderflow = isUnderflow;
        IsOverflowRebase = isOverflowRebase;
        DroppedFromFrame = droppedFromFrame;
        DroppedToFrame = droppedToFrame;
        IsFaulted = isFaulted;
        MissingFromFrame = missingFromFrame;
        MissingToFrame = missingToFrame;
    }

    public int ActiveFromFrame { get; }
    public int ActiveToFrame { get; }
    public float Alpha { get; }
    public int ReadyBacklog { get; }
    public float FractionalBacklog { get; }
    public float ExcessBacklog { get; }
    public float TargetSpeed { get; }
    public float PlaybackSpeed { get; }
    public int CrossedIntervalCount { get; }
    public long RenderFrameDropCount { get; }
    public bool HasPresentationFrame { get; }
    public bool IsUnderflow { get; }
    public bool IsOverflowRebase { get; }
    public int DroppedFromFrame { get; }
    public int DroppedToFrame { get; }
    public bool IsFaulted { get; }
    public int MissingFromFrame { get; }
    public int MissingToFrame { get; }
}
```

Add this exact public surface to the existing `ConfirmedPresentationCursor` class in Tasks 2–3:

```text
ConfirmedPresentationCursor(ConfirmedPlaybackSettings settings)
ConfirmedPlaybackAdvance Advance(int confirmedHead, float deltaTimeSeconds, float frameDurationSeconds)
int ActiveFromFrame { get; }
int ActiveToFrame { get; }
float InterpolationAlpha { get; }
float TargetSpeed { get; }
float PlaybackSpeed { get; }
bool IsFaulted { get; }
void EnterPresentationFault(int missingFromFrame, int missingToFrame)
void RecalculateAfterRollback(int confirmedHead)
```

`ConfirmedPresentationCursor` copies the validated setting values at construction; it does not retain a mutable settings reference.

Use these concrete test helpers in `ConfirmedPresentationCursorTests`; they build cursor state only through the public API and keep Confirmed arrivals at the selected target waterline, so setup does not accidentally trigger catch-up:

```csharp
private const float FrameDuration = 0.033f;

private static ConfirmedPresentationCursor CreateCursor(
    int targetReadyBacklog,
    float catchUpGainPerFrame = 0.15f,
    float maximumPlaybackSpeed = 1.5f,
    float speedSmoothingSeconds = 0.10f)
{
    var settings = new ConfirmedPlaybackSettings(
        targetReadyBacklog: targetReadyBacklog,
        maxReadyBacklog: 8,
        catchUpGainPerFrame: catchUpGainPerFrame,
        maximumPlaybackSpeed: maximumPlaybackSpeed,
        speedSmoothingSeconds: speedSmoothingSeconds,
        catchUpEnterExcessFrames: 0.25f,
        catchUpExitExcessFrames: 0.10f,
        overflowCorrectionMaximumSeconds: 0.20f);
    settings.ValidateOrReset(_ => { });
    return new ConfirmedPresentationCursor(settings);
}

private static ConfirmedPresentationCursor StartedCursorAt(
    int activeToFrame,
    float alpha,
    int targetReadyBacklog)
{
    ConfirmedPresentationCursor cursor = CreateCursor(targetReadyBacklog);
    cursor.Advance(targetReadyBacklog, 0f, FrameDuration);
    for (int nextToFrame = 1;
         nextToFrame <= activeToFrame;
         nextToFrame++)
    {
        cursor.Advance(
            nextToFrame + targetReadyBacklog,
            FrameDuration,
            FrameDuration);
    }

    if (alpha > 0f)
    {
        cursor.Advance(
            activeToFrame + targetReadyBacklog,
            alpha * FrameDuration,
            FrameDuration);
    }
    return cursor;
}
```

- [ ] **Step 5: Run settings tests and compile the surface GREEN**

Run the same command with `settings-green.xml`/`.log`. Expected: settings tests pass; cursor behavior tests introduced in later tasks may still fail.

- [ ] **Step 6: Review checkpoint**

Verify the new files contain no Ledger, world, snapshot, network or Transform fields. Do not commit.

---

### Task 2: Implement low-latency waterlines and fractional catch-up

**Files:**

- Modify: `Project/Frame Synchronization/Assets/Scripts/Presentation/ConfirmedPresentationCursor.cs`
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/ConfirmedPresentationCursorTests.cs`

**Interfaces:**

- Consumes: validated `ConfirmedPlaybackSettings` from Task 1.
- Produces: normal `Advance` behavior and stable speed telemetry used by GameController and diagnostics.

- [ ] **Step 1: Add failing waterline boundary tests**

```csharp
[Test]
public void WaterlineZero_FirstConfirmedInterval_StartsWithoutWaitingForNextFrame()
{
    var cursor = CreateCursor(targetReadyBacklog: 0);

    ConfirmedPlaybackAdvance sample = cursor.Advance(0, 0f, 0.033f);

    Assert.IsTrue(sample.HasPresentationFrame);
    Assert.AreEqual(-1, sample.ActiveFromFrame);
    Assert.AreEqual(0, sample.ActiveToFrame);
    Assert.AreEqual(0, sample.ReadyBacklog);
}

[Test]
public void WaterlineOne_FirstConfirmedInterval_WaitsUntilOneReadyFrameRemains()
{
    var cursor = CreateCursor(targetReadyBacklog: 1);

    Assert.IsFalse(cursor.Advance(0, 0f, 0.033f).HasPresentationFrame);
    ConfirmedPlaybackAdvance sample = cursor.Advance(1, 0f, 0.033f);

    Assert.AreEqual(0, sample.ActiveToFrame);
    Assert.AreEqual(1, sample.ReadyBacklog);
}

[TestCase(0f, 2f)]
[TestCase(0.5f, 1.5f)]
[TestCase(1f, 1f)]
public void FractionalBacklog_IncludesActiveAlpha(float alpha, float expected)
{
    ConfirmedPresentationCursor cursor = StartedCursorAt(
        activeToFrame: 1,
        alpha: alpha,
        targetReadyBacklog: 0);

    ConfirmedPlaybackAdvance sample = cursor.Advance(2, 0f, 0.033f);

    Assert.AreEqual(expected, sample.FractionalBacklog, 0.0001f);
}
```

- [ ] **Step 2: Run cursor tests and save RED evidence**

Use Unity EditMode with `-testFilter 'FrameSyncDemo.Tests.ConfirmedPresentationCursorTests'` and output `TestResults-p2e-low-latency-cursor-red.xml`. Expected: the old startup-buffer behavior and old `TryGetNext/MarkPresented` API fail the new waterline contract.

- [ ] **Step 3: Implement interval activation and fractional backlog**

Use these exact calculations:

```csharp
int readyBacklog = confirmedHead - activeToFrame;
float fractionalBacklog = confirmedHead - (activeFromFrame + alpha);
float excessBacklog = Math.Max(
    0f,
    fractionalBacklog - (targetReadyBacklog + 1f));
```

Activate `nextToFrame = currentToFrame + 1` only when `confirmedHead - nextToFrame >= TargetReadyBacklog`. Starting state is the initial endpoint `-1` with no active interval. Waterline `0` therefore activates `-1 -> 0` as soon as head `0` exists; waterline `1` waits for head `1`.

- [ ] **Step 4: Add failing proportional-speed, cap, smoothing and hysteresis tests**

```csharp
[Test]
public void OneReadyInterval_TargetSpeedUsesFractionalExcess()
{
    ConfirmedPresentationCursor cursor = StartedCursorAt(0, 0f, 0);

    ConfirmedPlaybackAdvance sample = cursor.Advance(1, 0f, 0.033f);

    Assert.AreEqual(1.15f, sample.TargetSpeed, 0.0001f);
}

[Test]
public void BacklogEight_TargetSpeedNeverExceedsOnePointFive()
{
    ConfirmedPresentationCursor cursor = StartedCursorAt(0, 0f, 0);

    ConfirmedPlaybackAdvance sample = cursor.Advance(8, 0f, 0.033f);

    Assert.AreEqual(1.5f, sample.TargetSpeed, 0.0001f);
}

[Test]
public void CatchUpHysteresis_DoesNotToggleInsideDeadBand()
{
    ConfirmedPresentationCursor cursor = CreateCursor(
        targetReadyBacklog: 0,
        speedSmoothingSeconds: 10f);
    cursor.Advance(0, 0f, 0.033f);
    cursor.Advance(1, 0f, 0.033f);

    ConfirmedPlaybackAdvance insideBand = cursor.Advance(
        1,
        0.8f * 0.033f,
        0.033f);

    Assert.Greater(insideBand.TargetSpeed, 1f);
}
```

- [ ] **Step 5: Implement target and actual speed**

```csharp
if (!_catchingUp && excessBacklog > _catchUpEnterExcessFrames)
    _catchingUp = true;
else if (_catchingUp && excessBacklog < _catchUpExitExcessFrames)
    _catchingUp = false;

_targetSpeed = _catchingUp
    ? Math.Min(
        _maximumPlaybackSpeed,
        1f + _catchUpGainPerFrame * excessBacklog)
    : 1f;

float maximumSpeedDelta =
    (_maximumPlaybackSpeed - 1f) *
    deltaTimeSeconds / _speedSmoothingSeconds;
_playbackSpeed = MoveTowards(
    _playbackSpeed,
    _targetSpeed,
    maximumSpeedDelta);
```

`MoveTowards` must be a private pure C# helper with no Unity dependency and no overshoot. Under zero delta time, target telemetry updates but actual speed does not jump.

The per-render update order is fixed: calculate hysteresis/target from the starting Alpha, move actual speed once, consume `deltaTimeSeconds * PlaybackSpeed` across consecutive intervals, then recalculate final backlog and the target reported for the next update without moving actual speed a second time. Do not switch speed tiers once per crossed interval inside a long render frame.

- [ ] **Step 6: Add and pass the 30-second no-pulse test**

Simulate Confirmed arrivals every `0.033s` and presentation updates at `1/144s` for 30 seconds after warm-up. Assert `ReadyBacklog <= 1`, no overflow, no fault, no repeated `1.0 -> 1.15 -> 1.0` tier cycles, and actual speed returns to `1.0` within the configured smoothing tolerance.

- [ ] **Step 7: Run cursor GREEN and review**

Run the focused cursor fixture and inspect the XML for zero failures/skips. Confirm no test accesses wall-clock time. Do not commit.

---

### Task 3: Implement long-frame, underflow, overflow, fault and rollback state

**Files:**

- Modify: `Project/Frame Synchronization/Assets/Scripts/Presentation/ConfirmedPresentationCursor.cs`
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/ConfirmedPresentationCursorTests.cs`

**Interfaces:**

- Produces: degradation fields in `ConfirmedPlaybackAdvance` consumed by Task 5.
- Preserves: active Confirmed cursor frames through Predicted rollback.

- [ ] **Step 1: Add failing long-render-frame tests**

```csharp
[Test]
public void LongRenderFrame_CrossesIntervalsSequentiallyAndReturnsFinalPhase()
{
    ConfirmedPresentationCursor cursor = CreateCursor(
        targetReadyBacklog: 0,
        catchUpGainPerFrame: 0f);
    cursor.Advance(0, 0f, 0.033f);

    ConfirmedPlaybackAdvance sample = cursor.Advance(4, 0.080f, 0.033f);

    Assert.AreEqual(2, sample.CrossedIntervalCount);
    Assert.AreEqual(2, sample.RenderFrameDropCount);
    Assert.AreEqual(2, sample.ActiveToFrame);
    Assert.That(sample.Alpha, Is.InRange(0f, 1f));
}
```

Advance remaining time in a bounded loop. Every completed active interval increments `CrossedIntervalCount`; because only one Transform sample will be submitted by GameController, each additional crossed interval increments cumulative `RenderFrameDropCount`. Add a guard derived from available backlog plus one, not an arbitrary unbounded loop.

- [ ] **Step 2: Add failing underflow tests**

```csharp
[Test]
public void Underflow_HoldsEndpointAndResumesFromNextContinuousInterval()
{
    ConfirmedPresentationCursor cursor = StartedCursorAt(0, 1f, 0);

    ConfirmedPlaybackAdvance held = cursor.Advance(0, 0.050f, 0.033f);
    ConfirmedPlaybackAdvance resumed = cursor.Advance(1, 0.010f, 0.033f);

    Assert.IsTrue(held.IsUnderflow);
    Assert.AreEqual(0, held.ActiveToFrame);
    Assert.AreEqual(1, resumed.ActiveToFrame);
    Assert.Greater(resumed.Alpha, 0f);
}
```

Underflow sets target speed to `1.0`, moves actual speed toward `1.0`, retains the endpoint, and consumes no time while no continuous interval is eligible.

- [ ] **Step 3: Add failing overflow tests for both waterlines**

```csharp
[TestCase(0, 12)]
[TestCase(1, 11)]
public void NinthReadyInterval_RebasesToHeadMinusTarget(
    int targetReadyBacklog,
    int expectedToFrame)
{
    ConfirmedPresentationCursor cursor = StartedCursorAt(
        activeToFrame: 3,
        alpha: 0.5f,
        targetReadyBacklog: targetReadyBacklog);

    ConfirmedPlaybackAdvance sample = cursor.Advance(12, 0f, 0.033f);

    Assert.IsTrue(sample.IsOverflowRebase);
    Assert.AreEqual(expectedToFrame, sample.ActiveToFrame);
    Assert.AreEqual(4, sample.DroppedFromFrame);
    Assert.AreEqual(expectedToFrame - 1, sample.DroppedToFrame);
    Assert.AreEqual(1f, sample.PlaybackSpeed);
}
```

Check overflow before normal time consumption. Rebase to `ConfirmedHead - TargetReadyBacklog`, set alpha to `1`, reset catch-up/target/actual speed to `1`, and report an empty dropped range when no intermediate interval exists.

- [ ] **Step 4: Add failing fault and rollback tests**

```csharp
[Test]
public void PresentationFault_PausesConfirmedTrackWithoutChangingLastGoodFrame()
{
    ConfirmedPresentationCursor cursor = StartedCursorAt(5, 0.5f, 0);

    cursor.EnterPresentationFault(4, 6);
    ConfirmedPlaybackAdvance sample = cursor.Advance(8, 1f, 0.033f);

    Assert.IsTrue(sample.IsFaulted);
    Assert.AreEqual(5, sample.ActiveToFrame);
    Assert.AreEqual(4, sample.MissingFromFrame);
    Assert.AreEqual(6, sample.MissingToFrame);
}

[Test]
public void Rollback_PreservesCursorAndRecalculatesSpeedFromCurrentBacklog()
{
    ConfirmedPresentationCursor cursor = StartedCursorAt(5, 0.5f, 0);
    int frameBefore = cursor.ActiveToFrame;
    float alphaBefore = cursor.InterpolationAlpha;

    cursor.RecalculateAfterRollback(6);

    Assert.AreEqual(frameBefore, cursor.ActiveToFrame);
    Assert.AreEqual(alphaBefore, cursor.InterpolationAlpha);
    Assert.AreEqual(1.075f, cursor.TargetSpeed, 0.0001f);
}
```

`EnterPresentationFault` is idempotent and preserves the first missing range. `RecalculateAfterRollback` changes only controller telemetry/state; it never changes active frames or alpha.

- [ ] **Step 5: Run the complete cursor fixture GREEN**

Expected: waterline, `0/1/8/9`, Alpha, speed, 30-second cadence, underflow, overflow, long-frame, fault and rollback tests all pass with zero skips.

- [ ] **Step 6: Review checkpoint**

Audit that capacity means `ReadyBacklog` only and no queue, snapshot store or world was introduced. Do not commit.

---

### Task 4: Preserve dual-world interpolation and atomic player-ball phase

**Files:**

- Modify: `Project/Frame Synchronization/Assets/Scripts/Presentation/ViewWorldBuilder.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationFrameInterpolator.cs`
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/ViewWorldBuilderTests.cs`
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationFrameInterpolatorTests.cs`
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/RemotePresentationCorrectionTests.cs`

**Interfaces:**

- Consumes: `ActiveToFrame` and `Alpha` from Task 3.
- Produces: one `ViewWorldState` whose remote player and Confirmed ball share the requested interval and alpha.

- [ ] **Step 1: Add failing exact-interval availability tests**

```csharp
[Test]
public void Build_RequestedConfirmedInterval_UsesSameFramesForRemoteAndConfirmedBall()
{
    FrameSyncCoordinator coordinator = CreateRemoteHeldCoordinatorThrough(4);

    Assert.IsTrue(ViewWorldBuilder.TryBuild(
        coordinator, 0, 3, out ViewWorldState view));

    Assert.AreEqual(2, view.Player1.FromCanonicalFrame);
    Assert.AreEqual(3, view.Player1.ToCanonicalFrame);
    Assert.AreEqual(2, view.Ball.FromCanonicalFrame);
    Assert.AreEqual(3, view.Ball.ToCanonicalFrame);
}

[Test]
public void Build_MissingRequestedFromEndpoint_ReturnsFalseWithoutSingleEndpointFallback()
{
    FrameSyncCoordinator coordinator = CreateCoordinator(snapshotCapacity: 1);
    AdvanceDualActual(coordinator, 0, Move(1), Move(2));
    AdvanceDualActual(coordinator, 1, Move(1), Move(2));

    Assert.IsFalse(ViewWorldBuilder.TryBuild(coordinator, 0, 1, out _));
}
```

Build the requested remote-held fixture explicitly with the existing helpers in `ViewWorldBuilderTests`:

```csharp
private static FrameSyncCoordinator CreateRemoteHeldCoordinatorThrough(
    int lastFrame)
{
    FrameSyncCoordinator coordinator = CreateCoordinator(
        initialBallFree: true);
    AdvanceDualActual(
        coordinator,
        0,
        new FrameInput(),
        Pickup());
    for (int frame = 1; frame <= lastFrame; frame++)
    {
        AdvanceDualActual(
            coordinator,
            frame,
            new FrameInput(),
            Move(1));
    }
    return coordinator;
}
```

Keep the current explicit `TryBuild(coordinator, localPlayerIndex, presentationFrame, out view)` signature. Do not weaken `requireConsecutiveConfirmedInterval`; the newest-head compatibility overload may retain its current single-endpoint fallback for non-cursor callers.

- [ ] **Step 2: Add failing source/phase transition tests**

Cover remote confirmed pickup, remote release, local predicted pickup, local predicted shot, holder change and overflow rebase. Evaluate with different `predictedAlpha` and `confirmedAlpha`; assert the remote player and Confirmed ball use `confirmedAlpha`, while the local player and local-predicted ball use `predictedAlpha`.

```csharp
interpolator.Evaluate(
    localPlayerIndex: 0,
    predictedAlpha: 0.75f,
    confirmedAlpha: 0.25f,
    playerPositions,
    out PresentationBallSample ball);
```

- [ ] **Step 3: Keep the dual-clock implementation minimal**

Retain `PresentationFrameInterpolator.Evaluate(int, float, float, Vector3[], out PresentationBallSample)`. The confirmed alpha must be applied uniformly to every `ViewSampleSource.Confirmed`, `InitialConfirmed` or `ConfirmedSingleEndpoint` sample. A holder-attached ball derives its offset from the same selected holder endpoints and same alpha; it must not be evaluated before the holder interval is replaced.

- [ ] **Step 4: Add overflow correction-from-screen tests**

Extend `RemotePresentationCorrectionTests` so an overflow jump first captures current displayed remote-player and ball positions, replaces the requested View interval, then starts player/ball correction from those captured positions. Assert final convergence within `0.20s`, no stale holder, and the existing `2`-unit snap threshold still applies.

- [ ] **Step 5: Run focused View/interpolator/correction GREEN**

Use a combined filter for `ViewWorldBuilderTests`, `PresentationFrameInterpolatorTests`, and `RemotePresentationCorrectionTests`. Expected: zero failures/skips and no changed `WorldHash` assertions.

- [ ] **Step 6: Review checkpoint**

Confirm no new mutable View entity, state machine or presentation-owned world exists. Do not commit.

---

### Task 5: Wire GameController without reintroducing a fixed frame wait

**Files:**

- Modify: `Project/Frame Synchronization/Assets/Scripts/GameController.cs`
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/P2CRuntimeOwnershipTests.cs`
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/ConfirmedPresentationCursorTests.cs`

**Interfaces:**

- Consumes: `ConfirmedPlaybackSettings`, `ConfirmedPlaybackAdvance`, explicit `ViewWorldBuilder.TryBuild`, and dual-alpha interpolator.
- Produces: one render-frame orchestration path for normal, underflow, overflow, fault and rollback.

- [ ] **Step 1: Add failing integration/ownership tests**

```csharp
[Test]
public void GameController_OwnsSerializedConfirmedPlaybackSettingsAndOneCursor()
{
    const BindingFlags Flags =
        BindingFlags.Instance | BindingFlags.NonPublic;

    Assert.AreEqual(
        typeof(ConfirmedPlaybackSettings),
        typeof(GameController).GetField(
            "_confirmedPlaybackSettings", Flags).FieldType);
    Assert.AreEqual(
        typeof(ConfirmedPresentationCursor),
        typeof(GameController).GetField(
            "_confirmedPresentationCursor", Flags).FieldType);
    Assert.IsNull(typeof(GameController).GetField(
        "_confirmedPresentationWorld", Flags));
}
```

Add a pure orchestration regression test or extracted internal adapter test proving head `0` with waterline `0` requests presentation frame `0` in the same render update; it must fail against `new ConfirmedPresentationCursor(1)`.

- [ ] **Step 2: Replace the fixed constructor with validated settings**

```csharp
[Header("P2-E Confirmed Playback")]
[SerializeField]
private ConfirmedPlaybackSettings _confirmedPlaybackSettings =
    new ConfirmedPlaybackSettings();
```

During initialization call `ValidateOrReset(Debug.LogWarning)` once and construct the cursor from the validated values. Remove the hard-coded startup buffer `1`.

- [ ] **Step 3: Replace push-then-advance with advance-then-final-sample**

Refactor `PushRealtimePresentationFrame` into one method that:

1. calls `cursor.Advance(ConfirmedFrame, deltaTime, frameDuration)`;
2. if faulted, holds screen state and emits no new View interval;
3. builds exactly `advance.ActiveToFrame` once, even if `CrossedIntervalCount > 1`;
4. on missing explicit interval, calls `EnterPresentationFault(from, to)` and logs `[RouteC][PresentationFault]` with the range;
5. pushes/replaces the View World so the latest Predicted local source still refreshes;
6. evaluates local with `FrameEngine.RenderInterpolationAlpha` and remote/Confirmed ball with `advance.Alpha`;
7. submits one final Transform sample.

Do not set the global `_paused` flag for a presentation-only missing-history fault; `_paused` halts logic and violates the design. Add a separate presentation-fault state owned by the cursor.

- [ ] **Step 4: Implement atomic overflow correction**

Before replacing the View World on `IsOverflowRebase`, capture current displayed player and ball positions. Replace remote player, Confirmed ball, holder/source, cursor alpha and controller state, then start correction using the captured screen positions and `OverflowCorrectionMaximumSeconds`. The first sample after rebase must use the new source/holder and speed `1.0`, never stale pre-overflow fields.

- [ ] **Step 5: Recalculate after rollback without moving the Confirmed cursor**

After successful `DoRollback` and before rebuilding presentation targets:

```csharp
_confirmedPresentationCursor.RecalculateAfterRollback(
    _frameSyncCoordinator.ConfirmedFrame);
```

Capture current screen positions, rebuild the local Predicted and ball-arbitrated View value, then start corrections. Assert cursor frames/alpha remain unchanged.

- [ ] **Step 6: Add GameController-level ball and long-frame tests**

Cover one render update crossing at least two Confirmed intervals, remote held-ball overflow, local predicted shot during a preserved Confirmed cursor, and missing-history fault. Assert only one final presentation submission, correct holder/source, no logic pause, and diagnostic counters receive the cursor result.

- [ ] **Step 7: Run focused integration GREEN**

Run `ConfirmedPresentationCursorTests`, `P2CRuntimeOwnershipTests`, View/interpolator/correction fixtures and any extracted GameController adapter fixture. Expected: zero failures/skips.

- [ ] **Step 8: Review checkpoint**

Search `GameController.cs` for `ConfirmedPresentationCursor(1)`, `TryGetNext`, `MarkPresented`, and the old “one frame per LateUpdate” flow; all must be gone. Do not commit.

---

### Task 6: Extend read-only diagnostics and Actual-to-visible correlation

**Files:**

- Modify: `Project/Frame Synchronization/Assets/Scripts/Presentation/RuntimeNetworkDiagnostics.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/GameController.cs`
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/RuntimeNetworkDiagnosticsTests.cs`
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/P2CRuntimeOwnershipTests.cs`

**Interfaces:**

- Consumes: mapped canonical Actual frames and `ConfirmedPlaybackAdvance`.
- Produces: JSONL `confirmedPlayback`, `underflow`, `overflow`, `presentationFault`, and `ballSourceSwitch` records.

- [ ] **Step 1: Add failing diagnostics tests**

```csharp
[Test]
public void ActualArrival_FirstVisibleSample_EmitsLatencyOnceUsingCanonicalFrame()
{
    var lines = new List<string>();
    var diagnostics = new RuntimeNetworkDiagnostics(true, lines.Add, 1000);
    diagnostics.RecordActualArrival(7, 100);

    diagnostics.RecordConfirmedPlayback(150, PlaybackSample(toFrame: 7, alpha: 0.2f));
    diagnostics.RecordConfirmedPlayback(160, PlaybackSample(toFrame: 7, alpha: 0.5f));

    Assert.AreEqual(1, lines.Count(line => line.Contains("actualToVisibleMs")));
    StringAssert.Contains("\"actualToVisibleMs\":50.000", lines[0]);
}

[Test]
public void PlaybackDiagnostics_EmitBacklogSpeedDropAndOverflowRange()
{
    RuntimeNetworkDiagnostics diagnostics = EnabledDiagnostics(out var lines);

    diagnostics.RecordConfirmedPlayback(200, OverflowSample(4, 11));

    StringAssert.Contains("\"readyBacklog\":", lines.Single());
    StringAssert.Contains("\"fractionalBacklog\":", lines.Single());
    StringAssert.Contains("\"targetSpeed\":", lines.Single());
    StringAssert.Contains("\"playbackSpeed\":", lines.Single());
    StringAssert.Contains("\"droppedFromFrame\":4", lines.Single());
    StringAssert.Contains("\"droppedToFrame\":11", lines.Single());
}
```

Use these exact test factories; they construct the public immutable result rather than reaching into cursor internals:

```csharp
private static RuntimeNetworkDiagnostics EnabledDiagnostics(
    out List<string> lines)
{
    lines = new List<string>();
    return new RuntimeNetworkDiagnostics(true, lines.Add, 1000);
}

private static ConfirmedPlaybackAdvance PlaybackSample(
    int toFrame,
    float alpha)
{
    return new ConfirmedPlaybackAdvance(
        activeFromFrame: toFrame - 1,
        activeToFrame: toFrame,
        alpha,
        readyBacklog: 0,
        fractionalBacklog: 1f - alpha,
        excessBacklog: 0f,
        targetSpeed: 1f,
        playbackSpeed: 1f,
        crossedIntervalCount: 0,
        renderFrameDropCount: 0,
        hasPresentationFrame: true,
        isUnderflow: false,
        isOverflowRebase: false,
        droppedFromFrame: -1,
        droppedToFrame: -1,
        isFaulted: false,
        missingFromFrame: -1,
        missingToFrame: -1);
}

private static ConfirmedPlaybackAdvance OverflowSample(
    int droppedFromFrame,
    int droppedToFrame)
{
    return new ConfirmedPlaybackAdvance(
        activeFromFrame: 11,
        activeToFrame: 12,
        alpha: 1f,
        readyBacklog: 0,
        fractionalBacklog: 0f,
        excessBacklog: 0f,
        targetSpeed: 1f,
        playbackSpeed: 1f,
        crossedIntervalCount: 0,
        renderFrameDropCount: 0,
        hasPresentationFrame: true,
        isUnderflow: false,
        isOverflowRebase: true,
        droppedFromFrame,
        droppedToFrame,
        isFaulted: false,
        missingFromFrame: -1,
        missingToFrame: -1);
}
```

- [ ] **Step 2: Save diagnostics RED evidence**

Run `RuntimeNetworkDiagnosticsTests` to `TestResults-p2e-low-latency-diagnostics-red.xml`. Expected: missing methods and JSON fields.

- [ ] **Step 3: Record mapped Actual arrivals after canonical conversion**

Keep `RecordReceive(NetworkPacketArrival)` unchanged for wire-arrival cadence. In `DrainRemoteInputsToLedger`, after `CanonicalFrame.TryFromLocal` succeeds, call:

```csharp
_runtimeNetworkDiagnostics.RecordActualArrival(
    canonicalFrame,
    packetArrival.ReceivedTimestamp);
```

The diagnostics class stores only the first timestamp per canonical frame in a bounded sidecar map and prunes entries after first visibility or after they are older than the presentation retention window. Duplicate packets do not overwrite the first arrival.

- [ ] **Step 4: Emit cursor and transition telemetry**

Add methods with these signatures:

```csharp
public void RecordActualArrival(int canonicalFrame, long receivedTimestamp);
public void RecordConfirmedPlayback(
    long observedTimestamp,
    in ConfirmedPlaybackAdvance sample);
public void RecordBallSourceSwitch(
    long observedTimestamp,
    int presentationFrame,
    ViewSampleSource source,
    int holderPlayerIndex);
```

`RecordConfirmedPlayback` reports active frames, alpha, ready/fractional/excess backlog, target/actual speed, speed-tier switch count, catch-up duration, crossed count, cumulative RenderFrameDropCount, underflow duration, overflow dropped range, fault range and Actual-to-visible latency. `RecordBallSourceSwitch` emits only when source or holder changes.

- [ ] **Step 5: Preserve disabled-sidecar behavior**

Extend `DisabledRecorder_ProducesNoLines` to call every new method and assert no line and no externally visible state. The ownership test must continue proving diagnostics fields are absent from `SimulationWorldState`, `FrameSnapshot`, `FrameInputLedger` and `WorldHash`.

- [ ] **Step 6: Run diagnostics GREEN and inspect invariant formatting**

Expected: all diagnostics tests pass under comma-decimal culture, JSON numbers still use invariant culture, and the first-visible latency appears exactly once per canonical frame.

- [ ] **Step 7: Review checkpoint**

Confirm diagnostics have bounded storage and are not consulted by cursor, View builder, simulation or rollback decisions. Do not commit.

---

### Task 7: Complete the automated acceptance matrix

**Files:**

- Modify: all P2-E focused test files listed in the file map
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/ConfirmedPlaybackRuntimeAdapterTests.cs` — extracted GameController presentation orchestration tests without starting sockets or a Unity player

**Interfaces:**

- Verifies all design requirements together; produces fresh XML/log evidence.

- [ ] **Step 1: Add parameterized FPS phase tests**

```csharp
[TestCase(30)]
[TestCase(60)]
[TestCase(144)]
public void StableThirtySecondTrace_RemainsContinuousAcrossRenderRates(int fps)
{
    const float confirmedIntervalSeconds = 0.033f;
    float renderDeltaSeconds = 1f / fps;
    float arrivalAccumulator = 0f;
    int confirmedHead = -1;
    int overflowCount = 0;
    int faultCount = 0;
    int speedTierSwitches = 0;
    bool wasFast = false;
    ConfirmedPresentationCursor cursor = CreateCursor(0);
    ConfirmedPlaybackAdvance sample = default;

    for (float elapsed = 0f;
         elapsed < 30f;
         elapsed += renderDeltaSeconds)
    {
        arrivalAccumulator += renderDeltaSeconds;
        while (arrivalAccumulator >= confirmedIntervalSeconds)
        {
            arrivalAccumulator -= confirmedIntervalSeconds;
            confirmedHead++;
        }

        sample = cursor.Advance(
            confirmedHead,
            renderDeltaSeconds,
            confirmedIntervalSeconds);
        if (sample.IsOverflowRebase)
            overflowCount++;
        if (sample.IsFaulted)
            faultCount++;

        bool isFast = sample.PlaybackSpeed > 1.001f;
        if (isFast != wasFast)
            speedTierSwitches++;
        wasFast = isFast;
    }

    Assert.AreEqual(confirmedHead, sample.ActiveToFrame);
    Assert.AreEqual(0, overflowCount);
    Assert.AreEqual(0, faultCount);
    Assert.LessOrEqual(speedTierSwitches, 2);
}
```

Also run target waterline `1` and assert its first visible frame is delayed by exactly one complete Confirmed interval relative to waterline `0` under identical arrivals.

- [ ] **Step 2: Add integrated capacity/fault/ball matrix**

Cover `ReadyBacklog 0/1/8/9`, Alpha `0/0.5/1`, underflow recovery, overflow rebase, snapshot expiry, Predicted rollback, remote pickup/release, local pickup/shot, holder transfer and long render frame. Each case asserts both player and ball source/phase.

- [ ] **Step 3: Add determinism exclusion assertions**

Capture Confirmed and Predicted `SimulationWorldState` plus `WorldHash` before and after cursor advancement, speed changes, underflow, overflow, diagnostics and presentation correction. Assert every canonical field and hash is unchanged.

- [ ] **Step 4: Run focused P2-E matrix**

```powershell
& 'C:\Unity\unity2022\Editor\Unity.exe' `
  -projectPath 'E:\帧同步_RouteC\Project\Frame Synchronization' `
  -batchmode -runTests -testPlatform EditMode `
  -testFilter 'FrameSyncDemo.Tests.ConfirmedPlaybackSettingsTests;FrameSyncDemo.Tests.ConfirmedPresentationCursorTests;FrameSyncDemo.Tests.ViewWorldBuilderTests;FrameSyncDemo.Tests.PresentationFrameInterpolatorTests;FrameSyncDemo.Tests.RemotePresentationCorrectionTests;FrameSyncDemo.Tests.RuntimeNetworkDiagnosticsTests;FrameSyncDemo.Tests.P2CRuntimeOwnershipTests' `
  -testResults 'E:\帧同步_RouteC\TestResults-p2e-low-latency-focused.xml' `
  -logFile 'E:\帧同步_RouteC\p2e-low-latency-focused.log' -quit
```

Expected: exit code `0`, XML reports zero failures and zero skipped tests.

- [ ] **Step 5: Run the complete EditMode suite**

```powershell
& 'C:\Unity\unity2022\Editor\Unity.exe' `
  -projectPath 'E:\帧同步_RouteC\Project\Frame Synchronization' `
  -batchmode -runTests -testPlatform EditMode `
  -testResults 'E:\帧同步_RouteC\TestResults-p2e-low-latency-full.xml' `
  -logFile 'E:\帧同步_RouteC\p2e-low-latency-full.log' -quit
```

Expected: fresh XML, zero failures/skips. Record the actual new count; do not reuse `368/368`.

- [ ] **Step 6: Run independent import/compile and Windows x64 build**

Create a unique temporary project copy so current `Library` state cannot mask compile errors. Keep the generated path in the evidence note; do not delete it during the verification turn.

```powershell
$verificationRoot = Join-Path `
  ([IO.Path]::GetTempPath()) `
  ("route-c-p2e-low-latency-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $verificationRoot | Out-Null
robocopy `
  'E:\帧同步_RouteC\Project\Frame Synchronization' `
  $verificationRoot `
  /E /XD Library Temp Logs obj Builds .idea `
  /XF '*.sln' '*.csproj' | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed: $LASTEXITCODE" }

& 'C:\Unity\unity2022\Editor\Unity.exe' `
  -projectPath $verificationRoot `
  -batchmode -quit `
  -logFile 'E:\帧同步_RouteC\p2e-low-latency-independent-compile.log'
if ($LASTEXITCODE -ne 0) { throw "independent compile failed" }

$playerPath = Join-Path `
  $verificationRoot `
  'Builds\Windows\FrameSyncDemo.exe'
& 'C:\Unity\unity2022\Editor\Unity.exe' `
  -projectPath $verificationRoot `
  -batchmode -quit `
  -buildWindows64Player $playerPath `
  -logFile 'E:\帧同步_RouteC\p2e-low-latency-build.log'
if ($LASTEXITCODE -ne 0) { throw "Windows x64 build failed" }
```

Expected: zero C# errors and `Build completed with a result of 'Succeeded'`.

- [ ] **Step 7: Run unchanged server gates**

```powershell
dotnet build 'E:\帧同步_RouteC\Project\NetworkServer\NetworkServer.csproj'
powershell -ExecutionPolicy Bypass -File 'E:\帧同步_RouteC\Project\NetworkServer\NetworkServerLabTests.ps1'
powershell -ExecutionPolicy Bypass -File 'E:\帧同步_RouteC\Project\NetworkServer\NetworkServerBarrierTests.ps1'
```

Expected: lab PASS, barrier/fragmented-frame PASS, 8-byte forwarding unchanged.

- [ ] **Step 8: Run workspace protection checks**

```powershell
git diff --check
git status --short
```

Inspect the diff. No server protocol, Ledger, world state, hash schema, gameplay or P2-F file may be modified by this implementation.

- [ ] **Step 9: Review checkpoint**

Report exact fresh counts and failures. Do not commit and do not start manual acceptance if any automated gate failed.

---

### Task 8: Perform dual-client acceptance and close documentation accurately

**Files:**

- Modify after results are observed: `docs/architecture/route-c-frame-sync.md`
- Modify after results are observed: `docs/architecture/streetball2-minimal-frame-sync-v2.md`
- Modify after results are observed: `Project/路线规划_街篮帧同步最小实现.md`
- Create: an evidence note under `docs/verification/` using the date and P2-E low-latency name

**Interfaces:**

- Consumes: fresh automated evidence and two-client diagnostics.
- Produces: pass/fail acceptance record without overstating StreetBall2 equivalence.

- [ ] **Step 1: Eliminate stale processes before each run**

Identify the exact `NetworkServer` and Unity player processes belonging to this workspace, stop only those processes, verify port `8888` is free, then start one server instance. Do not terminate unrelated Unity/editor/server processes.

- [ ] **Step 2: Label network semantics**

Record whether the selected server mode injects one-way delay or RTT. For the existing mode `1`, label the run as fixed `100ms` one-way injection; do not call it `100ms RTT`.

- [ ] **Step 3: Run the 60 FPS matrix with diagnostics enabled**

Run both clients for at least 10 seconds per case: movement start, sustained one-direction movement, stop, eight-direction changes, held-ball movement, steal/holder transfer and shot/release. Record local response, remote start latency, periodic stop-go, jump, reverse displacement, continuous fast-forward and player-ball relation.

- [ ] **Step 4: Repeat at 144 FPS**

Use the same input script and network mode. Compare Actual-to-visible p50/p95, maximum `ReadyBacklog`, speed-tier switches, maximum continuous catch-up duration, RenderFrameDropCount, maximum displacement and reverse displacement with the 60 FPS run.

- [ ] **Step 5: Run 0ms and 200ms comparison samples**

Use 0ms to detect accidental fixed playback delay and 200ms to confirm the controller degrades through bounded catch-up/underflow rather than changing authority source. These comparisons do not replace the fixed-100ms acceptance baseline.

- [ ] **Step 6: Decide pass/fail from both feel and data**

Pass requires: local immediate response; no extra complete-frame wait in target `0`; no periodic stop-go, forward jump, rollback backstep or persistent fast-forward; correct holder/ball relation; no unexpected PresentationFault/Overflow in stable fixed-100ms runs; and diagnostics consistent with observation.

- [ ] **Step 7: Update status documents using the observed result**

If manual acceptance fails, keep this exact text:

```text
P2-E 自动化门禁通过；固定 100ms 的远端表现人工验收未通过。低延迟 Confirmed 播放控制已实施，但双端体验或诊断仍未达到验收门，P2-E 保持未完成且不得进入 P2-F。
```

If and only if every automated and manual gate passes, record the exact test counts, build/server results, FPS/network matrix and diagnostics evidence, then state P2-E is accepted for Route C. Never claim the parameters are StreetBall2-authentic.

- [ ] **Step 8: Final review checkpoint**

Re-read the 16-item correction handoff and design spec line by line, run `git diff --check`, inspect all changed files and report any remaining gap. Do not commit, merge or push.

---

## Execution order and stop conditions

Execute Tasks 1 through 8 sequentially. Tasks 1–3 establish the pure controller; Task 4 locks source/phase integrity; Task 5 wires runtime behavior; Task 6 makes the result measurable; Task 7 proves automated safety; Task 8 is the only place where visual acceptance may be decided.

Stop and report to 帅老大 when any of these occurs:

- a requested Confirmed interval cannot be distinguished from snapshot expiry;
- speed control requires changing Confirmed/Predicted world timing;
- overflow correction cannot atomically keep player, ball and holder in one phase;
- Actual-to-visible cannot be correlated after canonical frame mapping;
- a server/protocol/Ledger/hash/gameplay change appears necessary;
- any fresh automated gate fails repeatedly after root-cause investigation;
- dual-client observation contradicts the diagnostic trace.

## Self-review checklist

| 16 项订正 | 实施任务 |
|---|---|
| 1. ReadyBacklog 容量边界 | Task 3、Task 7 |
| 2. 不创建第三世界/重复队列 | Task 1、Task 3、Task 5 |
| 3. 默认水位 0、水位 1 可选 | Task 1、Task 2、Task 7 |
| 4. Underflow 保持/恢复 | Task 3、Task 5 |
| 5. Overflow 原子重定位 | Task 3、Task 4、Task 5 |
| 6. 快照缺失 PresentationFault | Task 3、Task 5 |
| 7. 长渲染帧与 RenderFrameDrop | Task 3、Task 5、Task 7 |
| 8. 独立可序列化设置 | Task 1、Task 5 |
| 9. 完整只读诊断 | Task 6 |
| 10. 回滚保游标、重算速度 | Task 3、Task 5 |
| 11. 玩家—篮球—holder 同相位 | Task 4、Task 5 |
| 12. 自动化 14 类矩阵 | Task 7 |
| 13. 双端人工矩阵 | Task 8 |
| 14. 严格修改范围 | Global Constraints、Task 7 |
| 15. 精确状态文本 | Task 8 |
| 16. 新窗口 TDD、无自动 git | Global Constraints、全部 Task checkpoint |

- [ ] All 16 correction items map to at least one task.
- [ ] Waterline `0` is the default and waterline `1` is tested/configurable.
- [ ] Capacity is `ReadyBacklog`, not queue or snapshot capacity.
- [ ] Fractional backlog includes Alpha; target and actual speed are separate.
- [ ] Hysteresis, smoothing, underflow, overflow, fault, rollback and long render frames are explicit.
- [ ] Ball/holder/source transitions are atomic with the remote Confirmed phase.
- [ ] Diagnostics remain bounded, default off and logic-independent.
- [ ] Tests cover `30/60/144 FPS`, 30 seconds, Actual-to-visible and hash exclusion.
- [ ] Manual matrix distinguishes one-way delay from RTT and uses both 60/144 FPS.
- [ ] No P2-F, no success claim before manual evidence, and no automatic git action.

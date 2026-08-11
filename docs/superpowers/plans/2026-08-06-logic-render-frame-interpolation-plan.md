# Logic and Render Frame Interpolation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Separate 30FPS deterministic logic updates from Unity render frames by interpolating every displayed player and the basketball between the two latest completed logic frames.

**Architecture:** Add a fixed-allocation `PresentationFrameInterpolator` that owns previous/current presentation endpoints and render-alpha calculation. `FrameEngine` publishes a render-only alpha, while `GameController` pushes completed logic states and writes interpolated Transforms once in `LateUpdate`; existing correction smoothers remain an overlay for rollback discontinuities.

**Tech Stack:** Unity 2022.3.62f2, C#, UnityEngine `Vector3`, NUnit EditMode tests, existing `FrameSyncDemo.Runtime` and `FrameSyncDemo.Tests.EditMode` assemblies.

**Repository constraints:** Work only in `E:\帧同步_RouteC`; preserve all existing uncommitted P0–P1-D and presentation-smoothing work. Do not commit, merge, push, reset, clean, or overwrite with checkout. Do not operate the Unity Editor; provide manual engine verification steps to 帅老大.

---

## File map

- Create `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationFrameInterpolator.cs`: fixed-size previous/current position buffers, frame ordering, rollback replacement, alpha calculation, and interpolation.
- Create `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationFrameInterpolatorTests.cs`: endpoint, catch-up, rollback replacement, validation, allocation-independent, and alpha regression tests.
- Modify `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationTargetResolver.cs`: combine interpolated basketball base position with the displayed holder correction.
- Modify `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationTargetResolverTests.cs`: verify held-ball attachment on interpolated bases and state fallbacks.
- Modify `Project/Frame Synchronization/Assets/Scripts/FrameSync/FrameEngine.cs`: publish render-only interpolation alpha after each Unity `Update`.
- Modify `Project/Frame Synchronization/Assets/Scripts/GameController.cs`: capture complete logic endpoints, evaluate interpolation once per `LateUpdate`, reset lifecycle endpoints, and replace endpoints after successful rollback.
- Modify `Project/Frame Synchronization/Assets/Scripts/Gameplay/FrameReplaySystem.cs`: preflight the complete replay input range so a failed replay cannot partially mutate the logic world or leak into presentation state.
- Modify `Project/Frame Synchronization/Assets/Tests/EditMode/FrameReplaySystemTests.cs`: prove a later missing input returns before restore, reset, simulation, snapshot writes, or ball-state changes.

### Task 1: Specify the frame interpolator with failing tests

**Files:**
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationFrameInterpolatorTests.cs`

- [ ] **Step 1: Add initialization and interpolation tests**

Create a fixture that builds two `PlayerEntity` instances and one `BallEntity`, then asserts:

```csharp
var interpolator = new PresentationFrameInterpolator(2);
interpolator.Reset(10, players, ball);
interpolator.Evaluate(0.5f, outputPlayers, out Vector3 outputBall);
Assert.AreEqual(new Vector3(-3f, 0.5f, 0f), outputPlayers[0]);

players[0].position.x = FixedInt.FromInt(-1);
ball.position.x = FixedInt.FromInt(2);
interpolator.PushLogicFrame(11, players, ball);
interpolator.Evaluate(0.5f, outputPlayers, out outputBall);
Assert.AreEqual(new Vector3(-2f, 0.5f, 0f), outputPlayers[0]);
Assert.AreEqual(new Vector3(1f, 0f, 0f), outputBall);
```

Also assert exact values at alpha `0` and `1`.

- [ ] **Step 2: Add ordering, catch-up, rollback, and validation tests**

Cover these contracts:

```csharp
players[0].position.x = FixedInt.FromInt(11);
interpolator.PushLogicFrame(11, players, ball);
players[0].position.x = FixedInt.FromInt(12);
interpolator.PushLogicFrame(12, players, ball);
players[0].position.x = FixedInt.FromInt(13);
interpolator.PushLogicFrame(13, players, ball);
// alpha 0 returns frame 12, alpha 1 returns frame 13.

players[0].position.x = FixedInt.FromInt(9);
interpolator.ReplaceAfterRollback(13, players, ball);
// alpha 0, 0.5 and 1 all return correctedWorld.

Assert.Throws<ArgumentOutOfRangeException>(
    () => new PresentationFrameInterpolator(0));
var uninitialized = new PresentationFrameInterpolator(2);
Assert.Throws<InvalidOperationException>(
    () => uninitialized.Evaluate(0f, new Vector3[2], out _));
Assert.Throws<ArgumentException>(
    () => interpolator.Evaluate(0f, new Vector3[1], out _));
Assert.Throws<ArgumentOutOfRangeException>(
    () => interpolator.PushLogicFrame(
        interpolator.CurrentFrameID,
        players,
        ball));
```

Test alpha NaN/negative as `0`, positive infinity/greater than one as `1`, and `CalculateAlpha` for start/middle/end plus invalid frame interval.

- [ ] **Step 3: Compile the tests and verify RED**

Use Unity's Roslyn compiler without starting the editor:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' `
  'C:\Unity\unity2022\Editor\Data\DotNetSdkRoslyn\csc.dll' `
  '@Library\Bee\artifacts\1900b0aE.dag\FrameSyncDemo.Tests.EditMode.rsp'
```

Expected: compilation fails only because `PresentationFrameInterpolator` does not exist.

### Task 2: Implement the fixed-allocation interpolator

**Files:**
- Create: `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationFrameInterpolator.cs`
- Test: `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationFrameInterpolatorTests.cs`

- [ ] **Step 1: Implement construction, reset, and capture**

Implement one class with four preallocated player arrays: previous, current, and caller-owned output validation requires no additional array. The public contract is:

```csharp
public sealed class PresentationFrameInterpolator
{
    public PresentationFrameInterpolator(int playerCount);
    public bool IsReady { get; private set; }
    public int PreviousFrameID { get; private set; }
    public int CurrentFrameID { get; private set; }

    public void Reset(int frameID, PlayerEntity[] players, BallEntity ball);
    public void PushLogicFrame(int frameID, PlayerEntity[] players, BallEntity ball);
    public void ReplaceAfterRollback(
        int frameID,
        PlayerEntity[] players,
        BallEntity ball);
    public void Evaluate(
        float alpha,
        Vector3[] playerPositions,
        out Vector3 ballPosition);
    public static float CalculateAlpha(
        long elapsedMs,
        long lastLogicMs,
        int frameIntervalMs);
}
```

`Reset` validates the world, captures targets through `PresentationTargetResolver.ResolvePlayerTarget`, captures `ball.position.ToVector3()`, and copies current values to previous values.

- [ ] **Step 2: Implement ordered push and rollback replacement**

`PushLogicFrame` calls `Reset` when not ready. Otherwise it rejects `frameID <= CurrentFrameID`, copies current endpoints into previous endpoints, and captures the new endpoints. `ReplaceAfterRollback` accepts the corrected frame regardless of its relation to the cached prediction, captures it, and copies corrected current endpoints into previous endpoints.

- [ ] **Step 3: Implement evaluation and time alpha**

Normalize alpha with explicit finite handling:

```csharp
float safeAlpha = float.IsNaN(alpha) || alpha <= 0f
    ? 0f
    : float.IsPositiveInfinity(alpha) || alpha >= 1f
        ? 1f
        : alpha;
```

Use `Vector3.LerpUnclamped` into the caller-provided player array and for the ball. `CalculateAlpha` rejects a non-positive interval, returns zero when `elapsedMs <= lastLogicMs`, one when the remainder reaches the interval, otherwise returns the exact ratio.

- [ ] **Step 4: Compile runtime and tests, then run the focused fixture GREEN**

Compile both response files. Run the fixture with the existing temporary reflection runner pattern and Unity Mono through an ASCII substituted drive path. Expected: every `PresentationFrameInterpolatorTests` case passes. Remove the temporary runner immediately afterward.

### Task 3: Preserve held-ball attachment over interpolated bases

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationTargetResolverTests.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationTargetResolver.cs`

- [ ] **Step 1: Write the failing held-ball target tests**

Add tests for this new method:

```csharp
Vector3 result = PresentationTargetResolver.ResolveInterpolatedBallTarget(
    heldBall,
    interpolatedBallBase,
    interpolatedPlayerBases,
    displayedPlayers);
```

For a held ball, assert that the result equals `interpolatedBallBase + (displayedHolder - interpolatedHolderBase)`. For `Airborne`, `Free`, `Scored`, invalid holder, or invalid arrays, assert that the result equals `interpolatedBallBase`.

- [ ] **Step 2: Compile and verify RED**

Expected: test compilation fails only because `ResolveInterpolatedBallTarget` is absent.

- [ ] **Step 3: Implement the stateless target rule**

Add:

```csharp
public static Vector3 ResolveInterpolatedBallTarget(
    BallEntity ball,
    Vector3 interpolatedBallPosition,
    Vector3[] interpolatedPlayerPositions,
    Vector3[] displayedPlayerPositions)
```

Validate `ball` for null. Return the base unchanged unless Held has a valid index in both arrays. For valid Held state, add the holder's visual correction delta to the interpolated basketball base.

- [ ] **Step 4: Recompile and run resolver plus interpolator fixtures GREEN**

Expected: all new and existing presentation-target tests pass.

### Task 4: Publish render alpha from FrameEngine

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Scripts/FrameSync/FrameEngine.cs`
- Test: `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationFrameInterpolatorTests.cs`

- [ ] **Step 1: Add render-only engine state**

Add a non-synchronized private float and public getter:

```csharp
private float _renderInterpolationAlpha;
public float RenderInterpolationAlpha => _renderInterpolationAlpha;
```

Reset it to zero in `Initialize` and `StartEngine`.

- [ ] **Step 2: Calculate it after the catch-up loop**

At the end of `FrameEngine.Update`, after all `ExecuteOneFrame` calls and catch-up reporting, assign:

```csharp
_renderInterpolationAlpha =
    PresentationFrameInterpolator.CalculateAlpha(
        elapsedMs,
        _lastLogicMs,
        _frameIntervalMs);
```

Do not use this value in input, simulation, buffering, snapshots, replay, or Hash.

- [ ] **Step 3: Compile runtime and focused tests**

Expected: runtime and test assemblies compile and all interpolator time tests remain green.

### Task 5: Integrate interpolation and rollback correction in GameController

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Scripts/GameController.cs`

- [ ] **Step 1: Replace singular presentation state with frame-based state**

Add:

```csharp
private PresentationFrameInterpolator _presentationInterpolator;
private PresentationCorrectionSmoother[] _playerCorrectionSmoothers;
private Vector3[] _interpolatedPlayerPositions;
private Vector3[] _playerPresentationPositions;
```

Keep `_ballSmoother`. During presentation initialization, allocate all fixed arrays once, create one correction smoother per player, and reset the interpolator to the current complete logic world.

- [ ] **Step 2: Push one endpoint after each completed normal logic frame**

At the end of `OnPostFrameUpdate`, after the existing snapshot and WorldHash work, call:

```csharp
_presentationInterpolator?.PushLogicFrame(
    frameID,
    _playerEntities,
    _ballEntity);
```

No Transform write may occur in `OnFrameUpdate` or `OnPostFrameUpdate`.

- [ ] **Step 3: Evaluate the common base once per LateUpdate**

Replace direct logic target reads in `SyncPresentationFromLogic` with:

```csharp
_presentationInterpolator.Evaluate(
    _frameEngine.RenderInterpolationAlpha,
    _interpolatedPlayerPositions,
    out Vector3 interpolatedBallPosition);
```

Evaluate each player's correction smoother against its interpolated base, populate final display positions, then resolve the basketball target with `ResolveInterpolatedBallTarget`. Evaluate `_ballSmoother` against that target and write all Transforms exactly once.

- [ ] **Step 4: Reset lifecycle endpoints**

`SnapPresentationToLogic` must snap every player correction smoother and the ball smoother, reset the interpolator with `_frameEngine.CurrentFrame - 1`, and then render the identical endpoints. Initialization, Reset, Clear, playback entry/exit, and playback completion continue using this method.

- [ ] **Step 5: Replace endpoints and start corrections after successful rollback**

Before replay, capture every displayed player position and the basketball position in preallocated fields or fixed local values without allocating per frame. After successful replay:

```csharp
_presentationInterpolator.ReplaceAfterRollback(
    lastExecutedFrame,
    _playerEntities,
    _ballEntity);
```

Evaluate the corrected identical endpoints, begin each player correction from its captured display to its corrected base, derive the Held basketball target using the captured holder display correction, and begin the basketball correction from its captured display. A failed replay must not replace endpoints or start correction.

Before any restore or simulation, `FrameReplaySystem` must locate the safe snapshot read-only and preflight every frame from the replay start through `lastExecutedFrame`. If any frame or required input slot is missing, return `replayedFrameCount = 0` without mutating entities, prediction snapshots, or presentation attachment state.

- [ ] **Step 6: Compile both assemblies**

Expected: zero Roslyn errors. Inspect warnings and ensure no new presentation or deterministic-state warning exists.

### Task 6: Focused verification, regression checks, and independent review

**Files:**
- Inspect every file listed above.
- Do not modify `E:\帧同步`.

- [ ] **Step 1: Run all presentation fixtures outside Unity**

Run `PresentationFrameInterpolatorTests`, `PresentationCorrectionSmootherTests`, and `PresentationTargetResolverTests` with the temporary reflection runner. Expected: every parameterized case passes; delete the temporary runner source and binary afterward.

- [ ] **Step 2: Run repository safety checks**

Verify:

```text
RouteC branch = delivery/route-c
RouteC HEAD = 37260b437260c7712c658d5d0e05cbdd183accfb
git diff --check = clean
old branch = main
old HEAD = 346b9ed235523ee3cd5fa9bb55d7f405a12cb1a7
old status count = 3
old fingerprint = 2992895D404E0E3DA707992A6096957379219F8DA0C6050D503F814F34B484D6
```

- [ ] **Step 3: Request independent read-only review**

Review against the approved design, focusing on one-frame interpolation continuity, catch-up endpoint order, rollback replacement, Held basketball attachment, pause/lifecycle behavior, per-frame allocations, and isolation from deterministic state. Resolve every Critical or Important code issue and rerun affected tests.

- [ ] **Step 4: Hand off Unity verification without operating the editor**

Provide these manual checks to 帅老大:

1. Stop Play Mode and wait for compilation; Console must have zero red errors.
2. Run all EditMode tests; expected total is the previous 104 plus the new interpolator and resolver cases, with zero failures and zero skipped.
3. Run Editor and build clients together; verify smooth start, stop, continuous movement, eight-direction changes, held-ball attachment, airborne flight, rollback correction, pause, Reset, and playback.
4. Confirm same canonical-frame WorldHash and final logic positions remain identical.

Do not claim the stage complete and do not generate the new stage handoff until the full Unity suite and manual dual-client acceptance pass.

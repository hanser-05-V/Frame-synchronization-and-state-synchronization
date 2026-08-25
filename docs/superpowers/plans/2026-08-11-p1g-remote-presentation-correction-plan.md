# P1-G Remote Presentation Correction Implementation Plan

> **Execution rule:** Implement one task at a time with red-green verification. Stop at every blocking condition described below. This plan intentionally contains no commit, merge, or push step; the maintainer owns all Git publication.

**Goal:** Add one fixed logic frame to the remote presentation target waterline so fixed-100ms remote stop and direction corrections are normally repaired before they become visible, while preserving the existing local-player presentation timeline.

**Architecture:** Deepen `PresentationFrameInterpolator` from three complete endpoints to four (`N-3/N-2/N-1/N`). Local players continue sampling `N-1 -> N`; remote players and every non-local-attached ball state sample `N-3 -> N-2`. Rollback replaces all four endpoints atomically from corrected snapshots. The existing correction smoother remains unchanged unless the approved trajectory thresholds fail.

**Tech stack:** Unity 2022.3.62f2, C# 9, UnityEngine `Vector3`, NUnit EditMode tests, Unity Roslyn response files.

**Approved specification:** `docs/superpowers/specs/2026-08-11-p1g-remote-presentation-correction-design.md`

**Observed baseline:** `2bbb1193b81893b66ded937f52b8ad02197b43a4` at plan creation. Re-check before implementation; do not reset if it has moved.

**Repository constraints:** Work only in `E:/帧同步_RouteC`. Preserve all unrelated and pre-existing changes. Do not modify `E:/帧同步`. Do not alter synchronized simulation, input prediction, rollback replay, snapshot/hash schema, protocol, server behavior, or canonical-frame logic. Do not modify `PresentationCorrectionSmoother` in this pass. Do not operate the Unity UI or launch a second headless Unity instance while the project is already open.

---

## File map

- Modify `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationFrameInterpolator.cs`
  - Store the new deepest endpoint.
  - Keep local sampling on `previous/current`.
  - Move remote and non-local ball sampling to `deepest/oldest`.
  - Atomically replace four corrected rollback endpoints.
- Modify `Project/Frame Synchronization/Assets/Scripts/GameController.cs`
  - Fetch corrected `N-3` in addition to `N/N-1/N-2` and pass all four snapshots to the interpolator.
- Modify `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationFrameInterpolatorTests.cs`
  - Convert existing three-endpoint expectations to four endpoints and cover the deeper ball-ownership timeline.
- Create `Project/Frame Synchronization/Assets/Tests/EditMode/RemotePresentationCorrectionTests.cs`
  - Exercise the fixed-100ms trajectory contract and numeric acceptance thresholds.
- Create `Project/Frame Synchronization/Assets/Tests/EditMode/RemotePresentationCorrectionTests.cs.meta`
  - Use GUID `7c9e6a48b3f14d7ea12c08f964be3571`, after confirming it remains unused.

## Non-negotiable invariants

1. Buffer capacity and playback waterline change together: adding a field without sampling `N-3 -> N-2` is incomplete.
2. `Evaluate(localPlayerIndex, ...)` keeps the local player on `N-1 -> N` for both local indices 0 and 1.
3. Remote attachment state is selected from the new endpoint of the deep pair (`N-2`), never from logical head `N`.
4. Local Held is the only ball branch allowed to use `N-1 -> N` and attach immediately.
5. Local release detaches immediately, then the unbound ball reads deep absolute coordinates even if the deep endpoint still describes historical local Held.
6. Rollback validation completes before any endpoint array, ball endpoint, frame ID, or readiness flag mutates.
7. Missing history duplicates the nearest available corrected endpoint; no old predicted endpoint survives replacement.
8. The public non-mixed `Evaluate(float, ...)` behavior remains `N-1 -> N` for backward compatibility.
9. Evaluation remains allocation-free after warm-up.

---

## Task 0: Establish a clean execution baseline

**Files:** none

- [ ] **Step 1: Record current state without modifying it**

Run from `E:/帧同步_RouteC`:

```powershell
git rev-parse HEAD
git status --short
git diff -- AGENTS.md
```

Expected: existing user changes may be present. Record them and preserve them. Do not require a clean tree.

- [ ] **Step 2: Confirm the approved design and target files exist**

```powershell
$targets = @(
  'docs/superpowers/specs/2026-08-11-p1g-remote-presentation-correction-design.md',
  'Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationFrameInterpolator.cs',
  'Project/Frame Synchronization/Assets/Scripts/GameController.cs',
  'Project/Frame Synchronization/Assets/Tests/EditMode/PresentationFrameInterpolatorTests.cs',
  'Project/Frame Synchronization/Assets/Tests/EditMode/PresentationCorrectionSmootherTests.cs'
)
$targets | ForEach-Object { if (-not (Test-Path $_)) { throw "Missing required file: $_" } }
```

Expected: no output and exit code 0.

- [ ] **Step 3: Confirm the planned Unity GUID is unused**

```powershell
rg -n '7c9e6a48b3f14d7ea12c08f964be3571' 'Project/Frame Synchronization/Assets'
```

Expected before file creation: exit code 1 and no match. If a match exists, choose another 32-character lowercase hexadecimal GUID and update both this plan and the `.meta` file before continuing.

---

## Task 1: Specify the four-endpoint player timeline with failing tests

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationFrameInterpolatorTests.cs`
- Test: same file

- [ ] **Step 1: Extend reset assertions**

In `Reset_AnyAlpha_ReturnsCapturedWorld`, add:

```csharp
Assert.That(interpolator.DeepestFrameID, Is.EqualTo(10));
```

Keep the existing `OldestFrameID`, `PreviousFrameID`, and `CurrentFrameID` assertions.

- [ ] **Step 2: Replace the catch-up test with a four-frame contract**

Rename `PushLogicFrame_MultipleCatchupFrames_KeepsFinalTwoEndpoints` to:

```csharp
PushLogicFrame_MultipleCatchupFrames_KeepsFinalFourEndpoints
```

Reset at frame 10, then push frames 11, 12, 13, and 14 with distinct X coordinates. Assert:

```csharp
Assert.That(interpolator.DeepestFrameID, Is.EqualTo(11));
Assert.That(interpolator.OldestFrameID, Is.EqualTo(12));
Assert.That(interpolator.PreviousFrameID, Is.EqualTo(13));
Assert.That(interpolator.CurrentFrameID, Is.EqualTo(14));
```

Evaluate with local index 0 and `alpha = 0.5f`. Assert player 0 is the midpoint of frame 13/14, while player 1 is the midpoint of frame 11/12.

- [ ] **Step 3: Deepen both mixed-player timeline tests**

For `Evaluate_LocalZero_UsesCurrentPairAndRemoteUsesDelayedPair` and `Evaluate_LocalOne_ReversesTimelineOwnership`, build four distinct consecutive endpoints before evaluation. Use these exact endpoint X values:

```text
frame 10: player0=-3, player1=3
frame 11: player0=-2, player1=4
frame 12: player0=-1, player1=5
frame 13: player0= 0, player1=6
```

At `alpha = 0.5f` assert:

```text
local index 0: player0=-0.5, player1=3.5
local index 1: player0=-2.5, player1=5.5
```

This explicitly proves local uses 12/13 and remote uses 10/11.

- [ ] **Step 4: Extend the reset-collapse test**

In `Reset_AfterThreeFrames_CollapsesEveryTimelineToResetWorld`, retain the existing world-output assertions and add that all four frame IDs equal the reset frame.

- [ ] **Step 5: Run the focused fixture and prove RED**

If Unity is not already open, use:

```powershell
& 'C:/Unity/unity2022/Editor/Unity.exe' `
  -projectPath 'E:/帧同步_RouteC/Project/Frame Synchronization' `
  -batchmode -runTests -testPlatform EditMode `
  -testFilter 'FrameSyncDemo.Tests.PresentationFrameInterpolatorTests' `
  -testResults 'C:/tmp/p1g-four-endpoint-red.xml' `
  -logFile 'C:/tmp/p1g-four-endpoint-red.log' -quit
```

Expected: compile failure because `DeepestFrameID` does not exist, or assertion failures showing the remote still samples `N-2 -> N-1`. Confirm the failure is caused by the new contract, not unrelated compilation errors.

If Unity is already open, run the fixture in its EditMode Test Runner instead; do not launch the command above concurrently.

---

## Task 2: Implement four stored endpoints and the player waterline

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationFrameInterpolator.cs`
- Test: `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationFrameInterpolatorTests.cs`

- [ ] **Step 1: Add deepest storage and public frame identity**

Add the player array and ball endpoint next to the existing oldest fields:

```csharp
private readonly Vector3[] _deepestPlayerPositions;
private readonly Vector3[] _oldestPlayerPositions;
// existing previous/current arrays
private BallEndpoint _deepestBall;
private BallEndpoint _oldestBall;
// existing previous/current ball endpoints
```

Allocate it in the constructor:

```csharp
_deepestPlayerPositions = new Vector3[playerCount];
```

Add the frame property before `OldestFrameID`:

```csharp
public int DeepestFrameID { get; private set; }
```

- [ ] **Step 2: Add the endpoint copy operation**

Add:

```csharp
private void CopyOldestToDeepest()
{
    for (int i = 0; i < _playerCount; i++)
        _deepestPlayerPositions[i] = _oldestPlayerPositions[i];

    _deepestBall = _oldestBall;
}
```

- [ ] **Step 3: Collapse all four endpoints on reset and fallback replacement**

In both `Reset` and `ReplaceAfterRollback`, after `CopyPreviousToOldest()` call `CopyOldestToDeepest()`, then assign:

```csharp
DeepestFrameID = frameID;
OldestFrameID = frameID;
PreviousFrameID = frameID;
CurrentFrameID = frameID;
```

- [ ] **Step 4: Rotate in oldest-to-newest order on normal push**

Replace the copy/ID section of `PushLogicFrame` with:

```csharp
CopyOldestToDeepest();
DeepestFrameID = OldestFrameID;
CopyPreviousToOldest();
OldestFrameID = PreviousFrameID;
CopyCurrentToPrevious();
PreviousFrameID = CurrentFrameID;
CaptureCurrent(players, ball);
CurrentFrameID = frameID;
```

Do not require consecutive IDs for normal push; preserve the current public rule that they must only be strictly increasing. Catch-up simulation already pushes every completed logic frame individually.

- [ ] **Step 5: Change only the remote branch of mixed evaluation**

In `Evaluate(int localPlayerIndex, ...)`, retain local sources as `previous/current` and change remote sources to:

```csharp
Vector3 from = i == localPlayerIndex
    ? _previousPlayerPositions[i]
    : _deepestPlayerPositions[i];
Vector3 to = i == localPlayerIndex
    ? _currentPlayerPositions[i]
    : _oldestPlayerPositions[i];
```

Do not modify `Evaluate(float alpha, ...)`; it remains a compatibility overload using `previous/current`.

- [ ] **Step 6: Run the focused fixture and prove the player tests GREEN**

Use the Task 1 command with output names `p1g-four-endpoint-green.xml` and `.log`, or the already-open Unity Test Runner.

Expected: player timeline and reset/catch-up tests pass. Ball and rollback tests may still fail until Tasks 3 and 4.

---

## Task 3: Move basketball ownership and absolute motion to the deep timeline

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationFrameInterpolatorTests.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationFrameInterpolator.cs`

- [ ] **Step 1: Update existing non-held and remote-held tests to four endpoints**

For each existing ball test, construct four consecutive endpoints and preserve these exact decisions:

```text
Local Held: current N decides attachment; offset interpolates N-1 -> N.
Remote Held: oldest N-2 decides attachment; offset interpolates N-3 -> N-2.
Free/Airborne/Scored: absolute position interpolates N-3 -> N-2.
```

Rename assertions mentioning “delayed” to explicitly mention “deep timeline” where practical.

- [ ] **Step 2: Add two transition regressions before implementation**

Add:

```csharp
[Test]
public void Evaluate_RemoteAcquire_WaitsUntilOldestEndpointOwnsBall()
```

Arrange `N-3` Free, `N-2` Free, `N-1` remote Held, `N` remote Held. Evaluate with local index 0. Assert `AttachedPlayerIndex == -1` and ball position is the midpoint of the absolute `N-3/N-2` coordinates.

Add:

```csharp
[Test]
public void Evaluate_LocalRelease_DeepHistoryHeld_RemainsUnbound()
```

Arrange `N-3` and `N-2` local Held, `N-1` local Held, and `N` Airborne. Evaluate with local index 0. Assert `AttachedPlayerIndex == -1` and ball position is the midpoint of absolute `N-3/N-2` coordinates, not `playerPositions[0] + HeldOffset`.

- [ ] **Step 3: Run the focused fixture and prove RED**

Expected: the new remote-acquire/local-release tests or updated position expectations fail because `EvaluateBallSample` still reads `oldest/previous` and selects remote ownership from `previous`.

- [ ] **Step 4: Replace the remote/non-held branches in `EvaluateBallSample`**

Keep the current-local Held branch unchanged. Replace the remainder with:

```csharp
int remoteHolder = _oldestBall.HolderPlayerIndex;
if (remoteHolder != localPlayerIndex &&
    remoteHolder >= 0 &&
    remoteHolder < _playerCount &&
    IsHeldBy(_oldestBall, remoteHolder))
{
    Vector3 offset = InterpolateHeldOffset(
        _deepestBall,
        _oldestBall,
        remoteHolder,
        alpha);
    return new PresentationBallSample(
        playerPositions[remoteHolder] + offset,
        remoteHolder);
}

return new PresentationBallSample(
    Vector3.LerpUnclamped(
        _deepestBall.Position,
        _oldestBall.Position,
        alpha),
    -1);
```

The early local-Held branch is deliberately based on `_currentBall`. If it does not match, historical local Held data must fall through to unbound absolute interpolation.

- [ ] **Step 5: Run the focused fixture and prove GREEN**

Expected: all player and ball timeline tests pass, including local release, remote acquire, remote release, and Held offset assertions.

---

## Task 4: Atomically replace corrected four-frame rollback history

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationFrameInterpolatorTests.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationFrameInterpolator.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/GameController.cs`

- [ ] **Step 1: Change all rollback test calls to the four-snapshot API**

The target signature is:

```csharp
public void ReplaceHistoryAfterRollback(
    FrameSnapshot newest,
    FrameSnapshot? previous,
    FrameSnapshot? oldest,
    FrameSnapshot? deepest)
```

Update `ReplaceHistoryAfterRollback_ReplacesEveryPredictedEndpoint` to pass corrected frames 13/12/11/10. After replacement, assert the four frame IDs are 10/11/12/13 and mixed evaluation reads local 12/13 and remote 10/11.

- [ ] **Step 2: Cover every deterministic missing-history collapse**

Split the existing missing-history test into these cases:

```csharp
ReplaceHistoryAfterRollback_MissingPrevious_DuplicatesNewestIntoAllHistory
ReplaceHistoryAfterRollback_MissingOldest_DuplicatesPreviousIntoOlderHistory
ReplaceHistoryAfterRollback_MissingDeepest_DuplicatesOldest
```

Exact expected IDs:

```text
newest only (N):             N/N/N/N
newest + previous:           N-1/N-1/N-1/N
newest + previous + oldest:  N-2/N-2/N-1/N
all four:                    N-3/N-2/N-1/N
```

Listed oldest-to-newest as `deepest/oldest/previous/current`.

- [ ] **Step 3: Cover invalid deepest input and atomicity**

Add:

```csharp
[Test]
public void ReplaceHistoryAfterRollback_InvalidDeepest_ThrowsWithoutMutation()
```

Create a four-distinct-endpoint interpolator (frames 10/11/12/13), record evaluation plus every frame ID, then pass an invalid or non-consecutive deepest snapshot. Assert `ArgumentException` and assert the recorded state is unchanged.

Extend the existing invalid previous/oldest atomicity helper to assert `DeepestFrameID` and the remote `N-3/N-2` sample.

- [ ] **Step 4: Run the focused fixture and prove RED**

Expected: compile failure because the four-argument overload does not exist.

- [ ] **Step 5: Validate all optional snapshots before mutation**

At the start of `ReplaceHistoryAfterRollback`, preserve current validation and add:

```csharp
if (deepest.HasValue && !deepest.Value.IsValid)
{
    throw new ArgumentException(
        "The deepest rollback snapshot is invalid.",
        nameof(deepest));
}
if (deepest.HasValue &&
    (!oldest.HasValue ||
     deepest.Value.frameID != oldest.Value.frameID - 1))
{
    throw new ArgumentException(
        "The deepest rollback snapshot must immediately precede the oldest snapshot.",
        nameof(deepest));
}
```

Keep validation before the first `CaptureSnapshot`, copy call, frame-ID assignment, or `IsReady` assignment.

- [ ] **Step 6: Populate all four corrected endpoints**

After current/previous/oldest population, add:

```csharp
if (deepest.HasValue)
{
    CaptureSnapshot(
        deepest.Value,
        _deepestPlayerPositions,
        out _deepestBall);
    DeepestFrameID = deepest.Value.frameID;
}
else
{
    CopyOldestToDeepest();
    DeepestFrameID = OldestFrameID;
}
```

This produces the approved nearest-corrected-endpoint collapse recursively; it never reads a pre-call predicted endpoint.

- [ ] **Step 7: Fetch corrected `N-3` in `GameController`**

In `ReplacePresentationHistoryAfterRollback`, declare:

```csharp
FrameSnapshot? deepest = null;
```

Inside the successful `N-2` lookup, add the `N-3` lookup:

```csharp
if (_predictionSystem.TryGetWorldSnapshot(
    lastExecutedFrame - 3,
    out FrameSnapshot deepestValue))
{
    deepest = deepestValue;
}
```

Call:

```csharp
_presentationInterpolator.ReplaceHistoryAfterRollback(
    newest,
    previous,
    oldest,
    deepest);
```

Keep the existing missing-newest error and `ReplaceAfterRollback` fallback unchanged; after Task 2 it collapses all four endpoints to the corrected live world.

- [ ] **Step 8: Run the focused fixture and prove GREEN**

Expected: all rollback replacement tests pass, including invalid-input atomicity and every missing-history case.

---

## Task 5: Add fixed-100ms trajectory acceptance tests

**Files:**
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/RemotePresentationCorrectionTests.cs`
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/RemotePresentationCorrectionTests.cs.meta`
- Test: `Project/Frame Synchronization/Assets/Tests/EditMode/RemotePresentationCorrectionTests.cs`

- [ ] **Step 1: Create the Unity metadata**

Create the `.meta` file exactly as:

```yaml
fileFormatVersion: 2
guid: 7c9e6a48b3f14d7ea12c08f964be3571
MonoImporter:
  externalObjects: {}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {instanceID: 0}
  userData:
  assetBundleName:
  assetBundleVariant:
```

- [ ] **Step 2: Create the fixture and constants**

Use namespace `FrameSyncDemo.Tests` and these exact constants:

```csharp
private const float LogicStep = 0.165f;
private const float ReverseThreshold = LogicStep * 0.1f;
private const float FinalErrorThreshold = 0.0001f;
private const float HeldOffsetThreshold = 0.0001f;
private const float RenderDeltaTime = 1f / 60f;
private const float MaximumCorrectionSeconds = 0.2f;
```

Build snapshots with `FixedInt.FromFloat`, two valid player states, explicit ball state/holder, and a helper that replaces corrected frames `N/N-1/N-2/N-3` in newest-to-deepest argument order.

- [ ] **Step 3: Add the approved scenario tests**

Add these exact tests:

```csharp
Fixed100Ms_Stop_UnexpectedReverseDistance_DoesNotExceedThreshold
Fixed100Ms_SameDirection_UnexpectedReverseDistance_DoesNotExceedThreshold
Fixed100Ms_FastReverse_HasOneDirectionChangeAndNoSecondaryRebound
Fixed100Ms_Correction_ConvergesWithinMaximumDuration
Fixed100Ms_RemoteHeld_BallMaintainsDeepTimelineOffset
ZeroMs_LocalTimeline_RemainsPreviousToCurrent
```

For the first four, drive `PresentationFrameInterpolator` plus an unchanged `PresentationCorrectionSmoother` at 60Hz. At every render sample:

1. Evaluate the corrected target from the interpolator.
2. Evaluate the smoother with `RenderDeltaTime`.
3. Project movement onto the test X axis.
4. Accumulate displacement opposing the corrected deep-timeline direction.

Assertions:

```csharp
Assert.That(unexpectedReverseDistance,
    Is.LessThanOrEqualTo(ReverseThreshold + FinalErrorThreshold));
Assert.That(finalError,
    Is.LessThanOrEqualTo(FinalErrorThreshold));
Assert.That(elapsedSeconds,
    Is.LessThanOrEqualTo(MaximumCorrectionSeconds + RenderDeltaTime));
```

For fast reverse, ignore zero-length samples, count sign transitions, and assert exactly one authoritative direction switch and no subsequent transition. Also assert the displayed path's extra reverse deviation from the corrected deep timeline is at most `ReverseThreshold`.

For remote Held, use holder index 1, a non-zero offset such as `(0.4f, 0.8f, 0.6f)`, and assert:

```csharp
Assert.That(sample.AttachedPlayerIndex, Is.EqualTo(1));
Assert.That(
    Vector3.Distance(
        sample.Position,
        players[1] + expectedOffset),
    Is.LessThanOrEqualTo(HeldOffsetThreshold));
```

For the zero-ms/local invariant, make the deep pair visibly different and assert the local result still equals interpolation of `N-1/N` exactly.

- [ ] **Step 4: Run only the new fixture**

If Unity is not open:

```powershell
& 'C:/Unity/unity2022/Editor/Unity.exe' `
  -projectPath 'E:/帧同步_RouteC/Project/Frame Synchronization' `
  -batchmode -runTests -testPlatform EditMode `
  -testFilter 'FrameSyncDemo.Tests.RemotePresentationCorrectionTests' `
  -testResults 'C:/tmp/p1g-trajectory.xml' `
  -logFile 'C:/tmp/p1g-trajectory.log' -quit
```

Expected: all tests pass.

**Blocking rule:** If any trajectory test exceeds `0.0165`, requires more than `0.2s`, or fails exact convergence, stop P1-G implementation. Report the measured trace to the maintainer and create a separate smoother design. Do not weaken the assertion and do not modify `PresentationCorrectionSmoother` under this plan.

---

## Task 6: Compile and run the complete automated regression suite

**Files:** none unless a P1-G-caused failure requires an approved correction

- [ ] **Step 1: Let Unity import and compile**

If the project is already open, leave Play Mode and wait for compilation. Check Console for red errors. Do not open a second Unity process.

If the project is closed, run a compile-only import:

```powershell
& 'C:/Unity/unity2022/Editor/Unity.exe' `
  -projectPath 'E:/帧同步_RouteC/Project/Frame Synchronization' `
  -batchmode -quit -logFile 'C:/tmp/p1g-compile.log'
```

Expected: exit code 0 and no C# compiler error in the log.

- [ ] **Step 2: Run every EditMode test**

When Unity is closed:

```powershell
& 'C:/Unity/unity2022/Editor/Unity.exe' `
  -projectPath 'E:/帧同步_RouteC/Project/Frame Synchronization' `
  -batchmode -runTests -testPlatform EditMode `
  -testResults 'C:/tmp/p1g-editmode.xml' `
  -logFile 'C:/tmp/p1g-editmode.log' -quit
```

Expected: exit code 0, zero failed tests, and all P1-G fixtures discovered. If Unity is open, run all EditMode tests through the Test Runner instead and save the result count/log evidence.

- [ ] **Step 3: Confirm scope and forbidden-file integrity**

```powershell
git status --short
git diff --check
git diff --name-only
rg -n "DeepestFrameID|_deepestPlayerPositions|_deepestBall" `
  'Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationFrameInterpolator.cs' `
  'Project/Frame Synchronization/Assets/Tests/EditMode'
git diff -- `
  'Project/Frame Synchronization/Assets/Scripts/FrameSync' `
  'Project/Frame Synchronization/Assets/Scripts/Gameplay' `
  'Project/Frame Synchronization/Assets/Scripts/Network' `
  'Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationCorrectionSmoother.cs'
```

Expected:

- `git diff --check` has no whitespace error.
- Runtime/test changes are limited to the five files in the file map.
- The forbidden-area diff is empty.
- Existing unrelated changes remain untouched.

- [ ] **Step 4: Inspect the final diff manually**

```powershell
git diff -- `
  'Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationFrameInterpolator.cs' `
  'Project/Frame Synchronization/Assets/Scripts/GameController.cs' `
  'Project/Frame Synchronization/Assets/Tests/EditMode/PresentationFrameInterpolatorTests.cs' `
  'Project/Frame Synchronization/Assets/Tests/EditMode/RemotePresentationCorrectionTests.cs' `
  'Project/Frame Synchronization/Assets/Tests/EditMode/RemotePresentationCorrectionTests.cs.meta'
```

Review specifically for endpoint ordering, optional-snapshot validation before mutation, local-index symmetry, and accidental head-frame ball ownership.

---

## Task 7: Dual-client manual acceptance

**Files:** none

- [ ] **Step 1: Verify 0ms mode**

Run Editor and packaged client as the existing two endpoints. Check:

1. Local start, stop, and direction change have no added one-frame input/display delay.
2. Local Held follows the hand stably.
3. Throw, score, free-ball recovery, rollback, match end, and highlight replay have no new regression.
4. The two logical worlds and same canonical-frame WorldHash converge.

- [ ] **Step 2: Verify fixed 100ms mode**

Check:

1. Remote straight movement then stop has no obvious backward pull.
2. Same-direction continuation has no short reverse motion.
3. Rapid reversal shows one input-semantic turn and no second rebound.
4. Remote Held player and ball share one timeline; no early attach or early release.
5. Remote throw, landing, scoring, and re-acquisition keep ownership and position continuous.
6. The local player has no additional latency from the remote buffer.
7. The two logical worlds and same canonical-frame WorldHash converge.

- [ ] **Step 3: Preserve evidence and classify the result**

Record test count, Console status, 0ms observations, fixed-100ms observations, and Hash evidence. Do not claim P1-G complete if either automated thresholds or dual-client observation fails.

---

## Completion gate

P1-G is complete only when all are true:

- Four endpoints rotate and reset correctly.
- Local uses `N-1 -> N`; remote uses `N-3 -> N-2` for either local-player index.
- Ball ownership, attachment offset, and absolute position use the approved local/deep timelines.
- Rollback atomically replaces corrected `N/N-1/N-2/N-3`, including deterministic missing-history collapse.
- Existing and new EditMode tests pass, including allocation and mutation regressions.
- Fixed-100ms trajectory thresholds pass without changing the public smoother.
- 0ms and fixed-100ms dual-client manual checks pass.
- Synchronized logic, snapshot/hash schema, protocol, server, and canonical-frame behavior remain unchanged.
- `E:/帧同步` is untouched.
- No commit, merge, or push was performed automatically.


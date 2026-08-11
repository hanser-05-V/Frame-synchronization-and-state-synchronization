# P1-B Shared Frame Replay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make normal frames and rollback replay execute the same deterministic player-and-ball simulation, with complete snapshots saved only after the full logic frame.

**Architecture:** Add one deep `FrameSimulationSystem.Step` module that owns deterministic player movement, possession/shooting, and basketball physics. `GameController` becomes the adapter for input collection, complete snapshot storage/restoration, Unity presentation, and rollback orchestration.

**Tech Stack:** Unity 2022.3.62f2, C#, Unity Test Framework/NUnit, `FixedInt`, `FixedVector3`, existing `PredictionSystem` world snapshots.

**Global constraints:** Work only in `E:\帧同步_RouteC`; preserve P0 and P1-A changes; do not modify `E:\帧同步`; do not commit or push; do not change `MD5Checker`, network protocol, `NetworkClient`, or prediction-history rules; complete the whole P1-B prototype before requesting manual acceptance.

---

### Task 1: Specify the shared simulation seam

**Files:**
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/FrameSimulationSystemTests.cs`

- [ ] **Step 1: Write a failing one-frame shot test**

Create a fixture that builds two players, state machines, and a Held ball owned by P0. Call the wished-for interface:

```csharp
FrameSimulationResult result = FrameSimulationSystem.Step(
    players,
    stateMachines,
    ball,
    new[] { new FrameInput(0, 1), new FrameInput() },
    FixedInt.Zero,
    CourtConstant.LogicDeltaTime);
```

Assert:

```csharp
Assert.AreEqual(0, result.shooterPlayerIndex);
Assert.IsTrue(result.heldInvariantValid);
Assert.IsFalse(players[0].hasBall);
Assert.AreEqual(PlayerEntity.EState.Shooting, players[0].state);
Assert.AreEqual(-1, ball.holderPlayerIndex);
Assert.AreEqual(BallEntity.EState.Airborne, ball.state);
Assert.AreNotEqual(FixedVector3.Zero, ball.velocity);
Assert.Greater(ball.position.z._raw, players[0].position.z._raw);
```

- [ ] **Step 2: Write a failing corrected-replay parity test**

Build identical authoritative and predicted worlds with P1 holding the ball:

```text
frame 0: both paths use no-shoot input; predicted path saves world snapshot 0
frame 1: authoritative path shoots; predicted path incorrectly uses no-shoot
frame 2: both paths use no-shoot
restore predicted path to snapshot 0
replay frame 1 with shoot and frame 2 with no-shoot
```

Assert the initial divergence exists, then compare both players' position/facing/state/hasBall and the ball position/velocity/state/holder by exact `_raw` values after replay.

- [ ] **Step 3: Run the fixture and verify RED**

Run Unity EditMode with filter `FrameSyncDemo.Tests.FrameSimulationSystemTests`.

Expected: compilation fails only because `FrameSimulationSystem` and `FrameSimulationResult` do not exist.

### Task 2: Implement one deterministic frame module

**Files:**
- Create: `Project/Frame Synchronization/Assets/Scripts/Gameplay/FrameSimulationSystem.cs`
- Test: `Project/Frame Synchronization/Assets/Tests/EditMode/FrameSimulationSystemTests.cs`

- [ ] **Step 1: Add the event result value type**

```csharp
public readonly struct FrameSimulationResult
{
    public readonly BallEntity.EState previousBallState;
    public readonly FixedInt previousBallY;
    public readonly int shooterPlayerIndex;
    public readonly bool heldInvariantValid;

    public FrameSimulationResult(
        BallEntity.EState previousBallState,
        FixedInt previousBallY,
        int shooterPlayerIndex,
        bool heldInvariantValid)
    {
        this.previousBallState = previousBallState;
        this.previousBallY = previousBallY;
        this.shooterPlayerIndex = shooterPlayerIndex;
        this.heldInvariantValid = heldInvariantValid;
    }
}
```

- [ ] **Step 2: Implement `FrameSimulationSystem.Step`**

The public interface validates two players, two state machines, two inputs, and a non-null ball. Its implementation uses private direction arrays and performs this exact order:

```csharp
for each player:
    Shooting -> Idle
    direction present -> Run, otherwise Idle
    update FixedVector3 position and normalized facing

if ball is Held:
    validate holder index and hasBall/state/holder relationship
    holder shoot input -> BallShotSystem.TryShoot
    otherwise -> BallPossessionSystem.TryUpdateHeldBall

if ball is Airborne, Free, or Scored:
    BallPhysicsSystem.Update
```

Use `CourtConstant.HoopY`, `CourtConstant.HoopZ`, `CourtConstant.ShotFlightFrames`, and the provided `deltaTime` for shooting. Return the prior ball state/Y, successful shooter index or `-1`, and the Held invariant result. The module must not read Unity input, touch `Transform`, save snapshots, or log.

- [ ] **Step 3: Run the fixture and verify GREEN**

Expected: both `FrameSimulationSystemTests` pass. Then run all EditMode tests and require the existing `13` tests plus these `2` tests to pass.

### Task 3: Route the normal frame through the shared module

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Scripts/GameController.cs`

- [ ] **Step 1: Add a single GameController simulation adapter**

Add:

```csharp
private FrameSimulationResult SimulateLogicFrame(
    int frameID,
    FrameInput[] inputs,
    bool emitLogs)
{
    FixedInt moveDistance = FixedInt.FromFloat(_moveSpeed * 0.033f);
    FrameSimulationResult result = FrameSimulationSystem.Step(
        _playerEntities,
        _playerFSMs,
        _ballEntity,
        inputs,
        moveDistance,
        CourtConstant.LogicDeltaTime);

    SyncLegacyPositionsFromEntities();
    if (emitLogs)
        LogSimulationResult(frameID, result);
    return result;
}
```

- [ ] **Step 2: Replace duplicated normal-frame gameplay**

`OnFrameUpdate` calls `SimulateLogicFrame(frameID, inputs, true)` instead of directly updating players and calling the old `UpdateP0Ball`. Keep playback completion and `MD5Checker` after the simulation.

- [ ] **Step 3: Move presentation and snapshot to the full-frame end**

`OnPostFrameUpdate` no longer advances basketball physics. It must:

```csharp
SyncPresentationFromLogic();

if (_predictionSystem != null &&
    (FrameDebugger.Instance == null || !FrameDebugger.Instance.isPlayingBack))
{
    _predictionSystem.TakeWorldSnapshot(frameID, _playerEntities, _ballEntity);
}
```

`SyncPresentationFromLogic` updates all player transforms, `FrameDebugger` player render data, and the ball transform from entity state.

- [ ] **Step 4: Preserve concise state logs**

Move the current Held/Airborne/Scored/Free/Resting log decisions into `LogSimulationResult`. Use the returned prior state/Y and shooter index; reset or report the existing one-shot Held invariant diagnostic from `heldInvariantValid`.

- [ ] **Step 5: Compile and run all EditMode tests**

Expected: `15/15` pass, compiler errors zero. Open-scene behavior remains pending until the whole P1-B phase is complete.

### Task 4: Restore and replay complete worlds

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Scripts/GameController.cs`

- [ ] **Step 1: Replace position-only restoration**

In `DoRollback`, search backward from `errorFrame - 1` using:

```csharp
_predictionSystem.RestoreWorldSnapshot(
    frame,
    _playerEntities,
    _ballEntity)
```

If a snapshot is found, synchronize legacy XZ arrays and replay from `foundFrame + 1`.

- [ ] **Step 2: Add a complete pre-frame-zero fallback**

Replace `ResetPositions` with a complete reset that calls `PlayerEntity.Reset` for both players, restores P0 Held ownership, resets basketball position/velocity/state/holder, updates the Held hand point, and synchronizes legacy XZ. When no earlier snapshot exists, replay starts at frame `0`.

- [ ] **Step 3: Replay through `SimulateLogicFrame`**

Retain the existing local/remote input selection policy. For every replayed frame:

```csharp
SimulateLogicFrame(frame, inputs, false);
_predictionSystem.TakeWorldSnapshot(frame, _playerEntities, _ballEntity);
```

Do not update Unity transforms inside the replay loop.

- [ ] **Step 4: Synchronize presentation once and log a summary**

After replay completes, call `SyncPresentationFromLogic()` once and log:

```text
[RouteC][Rollback] errorFrame=... restoredFrame=... replayed=... ballState=...
```

- [ ] **Step 5: Remove obsolete adapters**

Delete `UpdateP0Ball`, `TakeExpandedSnapshot`, `UpdatePlayerEntitiesFromRollback`, and all `GameController` calls to legacy `TakeSnapshot/RestoreSnapshot`. Keep the legacy methods in `PredictionSystem` for compatibility until retrospective cleanup.

- [ ] **Step 6: Run targeted and full tests**

Run `FrameSimulationSystemTests` and `PredictionSystemSnapshotTests`, then all EditMode tests. Require `15/15`, no compiler errors.

### Task 5: Verify the P1-B stage

**Files:**
- Verify only; no additional production files.

- [ ] Run final full EditMode suite and require `15/15` passing.
- [ ] Run `git diff --check` and scan all new/untracked P1-B files for trailing whitespace.
- [ ] Confirm `GameController` references only `TakeWorldSnapshot/RestoreWorldSnapshot`, not legacy snapshot methods.
- [ ] Confirm `MD5Checker`, `NetworkClient`, network protocol, and prediction-history resolution received no P1-B edits.
- [ ] Request independent code review focused on frame ordering, frame-zero fallback, snapshot timing, state duplication, and replay parity.
- [ ] Recompute Route C branch/HEAD and the original `E:\帧同步` branch/HEAD/status fingerprint.
- [ ] Do not commit or push. Leave the completed P1-B stage for 帅老大 to validate in Unity.

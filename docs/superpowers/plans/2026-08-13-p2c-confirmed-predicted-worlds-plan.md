# P2-C Confirmed and Predicted Worlds Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a single `FrameSyncCoordinator` that advances isolated Confirmed and Predicted deterministic worlds from the P2-B ledger, reconciles prediction from confirmed history, and leaves Unity presentation-source arbitration to P2-D.

**Architecture:** `FrameSyncCoordinator` owns the sole `FrameInputLedger`, two independent `DeterministicWorld` instances, and two independent snapshot tracks. `GameController` records mapped Actual input through the coordinator, asks it to resolve/advance each canonical frame, and consumes result/snapshot APIs as an adapter; it no longer drives snapshots or replay itself. Confirmed catch-up is one coordinator operation but always a per-frame loop over dual Actual input and the sole `DeterministicWorld.Step`; prediction reconciliation restores the latest confirmed baseline and replays the complete unconfirmed suffix with one immutable Ledger plan.

**Tech Stack:** Unity 2022.3.62f2, C# runtime assembly, NUnit EditMode tests, existing fixed-point simulation and 8-byte input protocol.

---

## 1. Verified starting behavior

- `FrameInputLedger` is the only Actual/Predicted input fact source. Its continuous `ConfirmedThroughFrame` advances only over dual Actual slots; replay plans are immutable and commit mismatch/Predicted changes only after successful replay.
- `DeterministicWorld.Step` is the only gameplay-step entry and preserves the existing player movement, pickup, shot, held-ball, and ball-physics order.
- `SimulationWorldState` and its nested state structs are value-only. `WorldStateCodec` is the single runtime-entity Capture/Restore boundary.
- `PredictionSystem` is currently a single snapshot store despite its historical name. `FrameReplaySystem` currently uses that same store as both restore source and replay destination.
- `GameController` currently owns one live deterministic world, one ledger, one snapshot store, normal stepping, rollback planning, replay, Hash logging, stable-highlight snapshot reads, and presentation correction.
- P2-B evidence is `294/294` EditMode, standalone compile return code 0, Windows x64 build success, and server barrier PASS. P1-G/P2-A/P2-B changes are intentionally uncommitted and must remain intact.

## 2. Locked model, ownership, frame domains, and invariants

### World ownership

- `FrameSyncCoordinator` owns both simulation tracks after construction.
- Predicted uses the existing runtime `PlayerEntity[]`, `PlayerStateMachine[]`, and `BallEntity` that Unity presentation still reads during P2-C. Only the coordinator may step or restore them.
- Confirmed is constructed by cloning the initial `SimulationWorldState` into newly allocated players, state machines, and ball. It shares no mutable runtime object, array, state machine, or snapshot buffer with Predicted.
- Snapshot values may be copied between tracks because `SimulationWorldState` contains no mutable references. Snapshot buffers themselves are never shared.

### Frame semantics

- All coordinator, Ledger, snapshot, mismatch, replay, and Hash frame IDs are canonical.
- `ConfirmedFrame` is the last canonical frame actually stepped by Confirmed; it starts at `-1` and never exceeds `min(Ledger.ConfirmedThroughFrame, PredictedFrame)`.
- `PredictedFrame` is the last canonical frame stepped by Predicted; it starts at `-1` and advances exactly one sequential frame per normal call.
- The initial world is the state before canonical frame `0`. It is stored explicitly because `SnapshotBuffer` does not address negative IDs.
- A single call may catch Confirmed up over multiple frames, but each frame is read with `TryGetActualFrame`, stepped once, and snapshotted once.

### Reconciliation

- After stepping Predicted and catching Confirmed up, a pending mismatch triggers rebuild from `ConfirmedFrame + 1` through `PredictedFrame`.
- The restore baseline is the current Confirmed world at `ConfirmedFrame`, or the immutable initial world when `ConfirmedFrame == -1`.
- One Ledger replay plan covers the complete suffix, so out-of-order truth, one mismatch, and multiple mismatches use the same path.
- Replay reads only plan values, calls only `DeterministicWorld.Step`, writes only Predicted snapshots, and commits the plan only after all frames succeed.
- Preflight failures are atomic. Unexpected runtime exceptions pause/fail the session; P2-C does not claim transactional rollback of an already-mutated live world.

### Consumers and exclusions

- Stable highlight state and cross-client convergence Hash use Confirmed snapshots.
- Existing interpolation and correction continue to use Predicted snapshots during P2-C. No `ViewWorldBuilder`, basketball source arbitration, or new visual smoothing is added.
- No ECS, protocol, input-bit, gameplay, System-order, canonical mapping, or network transport changes.

## 3. File map

### Create

- `Project/Frame Synchronization/Assets/Scripts/FrameSync/WorldTrack.cs` + `.meta`: `Confirmed`/`Predicted` snapshot-track identity.
- `Project/Frame Synchronization/Assets/Scripts/FrameSync/WorldSnapshotStore.cs` + `.meta`: generic value-state snapshot ring with explicit initial-state handling.
- `Project/Frame Synchronization/Assets/Scripts/FrameSync/FrameAdvanceResult.cs` + `.meta`: immutable normal-step/reconcile result returned to the Unity adapter.
- `Project/Frame Synchronization/Assets/Scripts/FrameSync/ReconcileResult.cs` + `.meta`: mismatch, restore, replay, and failure diagnostics.
- `Project/Frame Synchronization/Assets/Scripts/FrameSync/FrameSyncCoordinator.cs` + `.meta`: sole Ledger/two-world/two-track owner and advancement seam.
- `Project/Frame Synchronization/Assets/Tests/EditMode/WorldSnapshotStoreTests.cs` + `.meta`: track isolation and boundary tests.
- `Project/Frame Synchronization/Assets/Tests/EditMode/FrameSyncCoordinatorTests.cs` + `.meta`: dual-world state-machine and replay matrix.
- `Project/Frame Synchronization/Assets/Tests/EditMode/P2CRuntimeOwnershipTests.cs` + `.meta`: runtime wiring/forbidden-owner audit.

### Modify

- `Project/Frame Synchronization/Assets/Scripts/Gameplay/DeterministicWorld.cs`: add an internal clone factory that allocates a complete runtime world from a value snapshot.
- `Project/Frame Synchronization/Assets/Scripts/Gameplay/FrameReplaySystem.cs`: separate restore-source and replay-destination snapshot stores; replay never owns track selection.
- `Project/Frame Synchronization/Assets/Scripts/FrameSync/PredictionSystem.cs`: keep compatibility snapshot APIs as a thin wrapper over `WorldSnapshotStore`; runtime `GameController` stops using it.
- `Project/Frame Synchronization/Assets/Scripts/Gameplay/PostGameTransitionSystem.cs`: restore the confirmed terminal state through coordinator APIs.
- `Project/Frame Synchronization/Assets/Scripts/GameController.cs`: become an adapter over coordinator input, advancement, snapshots, reconciliation diagnostics, stable highlight, pruning, terminal restore, and presentation history.
- `Project/Frame Synchronization/Assets/Tests/EditMode/FrameReplaySystemTests.cs`: use explicit restore and destination stores.
- `Project/Frame Synchronization/Assets/Tests/EditMode/PostGameTransitionSystemTests.cs`: assert confirmed terminal restore.
- `Project/Frame Synchronization/Assets/Tests/EditMode/Task6RuntimeOwnershipTests.cs`: preserve P2-B fact-source assertion while coordinator becomes the owner.
- `docs/architecture/route-c-frame-sync.md`, `docs/architecture/streetball2-minimal-frame-sync-v2.md`, `Project/路线规划_街篮帧同步最小实现.md`: record only verified P2-C behavior after tests pass and keep P2-D/P2-F explicit.

## 4. TDD tasks

### Task 1: Generic snapshot tracks and isolated world construction

**Files:** Create `WorldTrack.cs`, `WorldSnapshotStore.cs`, `WorldSnapshotStoreTests.cs`; modify `DeterministicWorld.cs` and `PredictionSystem.cs`.

- [ ] **RED:** Add tests proving a stored state is a value copy, Confirmed and Predicted stores can hold different states at the same frame, missing/overwritten frames return false, and initial state is returned only through the explicit initial API.
- [ ] **RED:** Add a deterministic-world clone test: mutate/step the clone and assert every original runtime field and Hash remain unchanged.
- [ ] **GREEN:** Implement:

```csharp
public enum WorldTrack { Confirmed, Predicted }

public sealed class WorldSnapshotStore
{
    public WorldSnapshotStore(in SimulationWorldState initialWorld, int capacity = 512);
    public SimulationWorldState InitialWorld { get; }
    public FrameSnapshot Store(in SimulationWorldState world);
    public bool TryGet(int canonicalFrame, out FrameSnapshot snapshot);
    public bool TryRestore(int canonicalFrame, DeterministicWorld world);
    public void RestoreInitial(DeterministicWorld world);
}
```

- [ ] **GREEN:** Add `DeterministicWorld.CreateIsolated(in SimulationWorldState initial, FixedInt moveDistance, FixedInt deltaTime)`. It allocates exactly two players/FSMs and one ball, restores all synchronized fields, and never reuses caller references.
- [ ] **GREEN:** Make `PredictionSystem` forward its legacy snapshot methods to one `WorldSnapshotStore`; do not add input or coordination state.
- [ ] **VERIFY:** Run `WorldSnapshotStoreTests`, `DeterministicWorldTests`, `PredictionSystemSnapshotTests`, and `WorldStateCodecTests`.

### Task 2: Coordinator normal dual-world advancement

**Files:** Create `FrameAdvanceResult.cs`, `ReconcileResult.cs`, `FrameSyncCoordinator.cs`, and `FrameSyncCoordinatorTests.cs`.

- [ ] **RED:** Initial heads are `-1`; Predicted frame 0 advances with one Actual plus one Predicted while Confirmed remains `-1`.
- [ ] **RED:** When frame 0 later has dual Actual, Confirmed advances exactly once and its snapshot inputs were both Actual.
- [ ] **RED:** Recording frames 0 and 2 but leaving frame 1 incomplete never lets Confirmed cross 0; filling frame 1 lets one coordinator call step Confirmed 1 then 2 in order.
- [ ] **RED:** A mutation/step on Predicted cannot alter Confirmed state before Confirmed is advanced.
- [ ] **GREEN:** Implement the coordinator seam:

```csharp
public sealed class FrameSyncCoordinator
{
    public FrameSyncCoordinator(DeterministicWorld predictedWorld,
        in SimulationWorldState initialWorld, FixedInt moveDistance,
        FixedInt deltaTime, int snapshotCapacity = 512);
    public int ConfirmedFrame { get; }
    public int PredictedFrame { get; }
    public SimulationWorldState ConfirmedWorld { get; }
    public SimulationWorldState PredictedWorld { get; }
    public int ConfirmedThroughFrame { get; }
    public FrameInputLedger.ActualArrival RecordActual(int canonicalFrame,
        int playerIndex, FrameInput input);
    public FrameInputLedger.ResolvedFrame ResolveForPrediction(int canonicalFrame);
    public FrameAdvanceResult Advance(int canonicalFrame,
        in FrameInputLedger.ResolvedFrame inputs);
    public bool TryGetSnapshot(WorldTrack track, int canonicalFrame,
        out SimulationWorldState world);
}
```

- [ ] **GREEN:** `Advance` rejects non-sequential Predicted frames, steps Predicted once, stores its snapshot, then loops `ConfirmedFrame + 1..min(ConfirmedThroughFrame, PredictedFrame)` and requires `TryGetActualFrame` before each Confirmed step.
- [ ] **VERIFY:** Run `FrameSyncCoordinatorTests` normal-advance fixture plus `DeterministicWorldTests`.

### Task 3: Reconcile one or many mismatches from the confirmed baseline

**Files:** Modify `FrameReplaySystem.cs`, `FrameSyncCoordinator.cs`, result structs, and related tests.

- [ ] **RED:** Wrong predicted frame diverges, late Actual creates mismatch, reconciliation restores the confirmed baseline, replays the entire suffix, and final Predicted state/Hash equals an authority world.
- [ ] **RED:** Two mismatches arriving out of order are both captured by one suffix plan; successful replay clears both and emits one result with the earliest mismatch.
- [ ] **RED:** A truth arrival that matches the predicted raw advances Confirmed without unnecessary replay.
- [ ] **RED:** Missing restore history or stale plan returns an explicit failure before restore/Step/commit; mismatch remains pending.
- [ ] **GREEN:** Change replay to:

```csharp
public static FrameReplayResult Replay(
    FrameInputLedger.ReplayInputPlan plan,
    FrameInputLedger ledger,
    WorldSnapshotStore restoreSource,
    WorldSnapshotStore replayDestination,
    DeterministicWorld predictedWorld,
    int restoreFrame);
```

`restoreFrame == -1` restores `restoreSource.InitialWorld`; otherwise it requires the exact Confirmed snapshot. Every replayed snapshot goes only to `replayDestination`.

- [ ] **GREEN:** Coordinator builds one plan from `ConfirmedFrame + 1` through `PredictedFrame`, restores from Confirmed, replays, commits, and publishes `ReconcileResult`. It never patches fields or runs another gameplay loop.
- [ ] **VERIFY:** Run coordinator mismatch matrix, `FrameReplaySystemTests`, Ledger replay tests, simulation tests, and WorldHash tests.

### Task 4: History, prune, Hash, and public read boundaries

**Files:** Modify `FrameSyncCoordinator.cs`, stores/results, and coordinator tests.

- [ ] **RED:** `TryGetSnapshot(Confirmed, frame)` never returns Predicted state, and vice versa.
- [ ] **RED:** Ledger `HistoryUnavailable`, `CapacityExceeded`, integrity fault, and prune rejection are surfaced without silently changing either frame head.
- [ ] **RED:** Two coordinators with identical initial state and Actual sequence but different local-player/arrival order produce field-equal Confirmed worlds and equal Hash for the same canonical frame.
- [ ] **RED:** Predicted states may temporarily differ while the final Confirmed Hash still converges.
- [ ] **GREEN:** Add coordinator wrappers for earliest mismatch, Actual reads, replay-plan retention floor, first-retained frame, integrity state, and safe prune. Do not expose the mutable Ledger or snapshot stores.
- [ ] **GREEN:** Add exact canonical-frame Hash helpers/log data for both tracks; only Confirmed Hash is used as convergence evidence.
- [ ] **VERIFY:** Run coordinator/ledger/hash boundary tests and `git diff --check`.

### Task 5: Migrate GameController to the coordinator adapter

**Files:** Modify `GameController.cs`, `PostGameTransitionSystem.cs`, runtime ownership tests, and postgame tests.

- [ ] **RED:** Reflection/source audit proves `GameController` has no `_inputLedger`, `_predictionSystem`, or direct `FrameReplaySystem.Replay` call after migration; it owns one `_frameSyncCoordinator`.
- [ ] **RED:** Network/local canonical mapping tests still record player/frame exactly as P2-B; the protocol and FrameBuffer execution values remain unchanged.
- [ ] **RED:** Stable highlight reads Confirmed snapshots and dual Actual inputs; Predicted snapshots are used only by the existing presentation path until P2-D.
- [ ] **GREEN:** Initialization captures the pre-frame-0 state, creates coordinator with the existing presentation-backed Predicted world, and transfers all stepping authority to it.
- [ ] **GREEN:** `ReadInputs` records Actual through coordinator and resolves one Predicted frame. `OnFrameUpdate` calls coordinator `Advance`; `OnPostFrameUpdate` pushes/logs the returned Predicted snapshot without stepping again.
- [ ] **GREEN:** A reconcile result replaces Predicted presentation history and begins the existing correction smoother. Normal presentation behavior is otherwise unchanged.
- [ ] **GREEN:** Highlight capture and postgame terminal restoration use Confirmed snapshots. Prune and fault diagnostics call coordinator wrappers.
- [ ] **VERIFY:** Run ownership, mapping, stable gate, highlight, postgame, presentation, rollback, and match-flow tests.

### Task 6: Full regression and documentation truth update

**Files:** All P2-C runtime/tests and three architecture/roadmap documents.

- [ ] **AUDIT:** Search runtime code for direct GameController Ledger/snapshot/replay ownership, a second input fact source, a second gameplay-step loop, shared Confirmed/Predicted mutable objects, Predicted snapshots in stable consumers, and claims that P2-D exists.
- [ ] **GREEN:** Update docs to state P2-C is implemented only after the complete automated matrix passes. Keep `ViewWorldBuilder`, basketball presentation arbitration, ECS, and fixed-100ms visual acceptance explicitly pending.
- [ ] **VERIFY:** Run full EditMode and require zero failures; record the actual count rather than assuming 294 plus a fixed number.
- [ ] **VERIFY:** Run a standalone Unity import/compile and require return code 0 with no compiler errors.
- [ ] **VERIFY:** Run `Project/NetworkServer/NetworkServerBarrierTests.ps1` and require PASS.
- [ ] **VERIFY:** Build Windows x64 and require success.
- [ ] **VERIFY:** Run `git diff --check`, inspect `git status --short`, and confirm every pre-existing P1-G/P2-A/P2-B file remains present. Do not commit, merge, reset, clean, or push.

## 5. Required scenario matrix

| Scenario | Confirmed | Predicted | Reconcile / failure |
|---|---|---|---|
| Initial | frame -1 initial value | frame -1 initial value | none |
| Frame 0 one Actual | stays -1 | steps 0 with Actual/Predicted | none |
| Frame 0 dual Actual before step | steps 0 once | steps 0 once | none if prediction was not previously executed |
| Out-of-order 0,2 then fill 1 | stops at 0, then loops 1→2 | remains sequential current head | no cross-hole jump |
| Predicted raw matches late Actual | catches up | state remains valid | no replay |
| One mismatch | catches up only over dual Actual | rebuilt from Confirmed baseline | plan commit clears captured mismatch |
| Multiple mismatch | catches up continuously | one complete suffix rebuild | earliest diagnostic, all captured cleared |
| Actual beyond an earlier hole | cannot cross hole | may remain ahead | suffix rebuild uses latest Confirmed baseline |
| Replay preflight failure | unchanged | unchanged | explicit, mismatch retained |
| Unexpected Step/store exception | no false publication | transaction not promised | session fault/pause |
| Snapshot same frame on both tracks | exact independent values | exact independent values | no alias |
| Ledger history expired/capacity fault | heads unchanged | heads unchanged | explicit failure, no fallback prediction |
| Two clients same Actual history | field-equal and same canonical Hash | may have differed earlier | convergence proven only for Confirmed |

## 6. Verification commands

Use Unity at `C:\Unity\unity2022\Editor\Unity.exe` and project `E:\帧同步_RouteC\Project\Frame Synchronization`.

```powershell
& 'C:\Unity\unity2022\Editor\Unity.exe' -projectPath 'E:\帧同步_RouteC\Project\Frame Synchronization' -batchmode -runTests -runSynchronously -testPlatform EditMode -testFilter 'FrameSyncDemo.Tests.FrameSyncCoordinatorTests' -testResults 'E:\帧同步_RouteC\TestResults-p2c-coordinator.xml' -quit -logFile 'E:\帧同步_RouteC\p2c-coordinator-tests.log'

& 'C:\Unity\unity2022\Editor\Unity.exe' -projectPath 'E:\帧同步_RouteC\Project\Frame Synchronization' -batchmode -runTests -runSynchronously -testPlatform EditMode -testResults 'E:\帧同步_RouteC\TestResults-p2c-final.xml' -quit -logFile 'E:\帧同步_RouteC\p2c-final-tests.log'

& 'C:\Unity\unity2022\Editor\Unity.exe' -projectPath 'E:\帧同步_RouteC\Project\Frame Synchronization' -batchmode -quit -logFile 'E:\帧同步_RouteC\p2c-compile.log'

& 'E:\帧同步_RouteC\Project\NetworkServer\NetworkServerBarrierTests.ps1'

& 'C:\Unity\unity2022\Editor\Unity.exe' -projectPath 'E:\帧同步_RouteC\Project\Frame Synchronization' -batchmode -quit -buildWindows64Player 'E:\帧同步_RouteC\Project\Frame Synchronization\Builds\RouteC-P2C-Smoke\FrameSynchronization.exe' -logFile 'E:\帧同步_RouteC\p2c-build.log'
```

## 7. Acceptance classification and stopping point

- Automated tests accept all pure code/state-machine/architecture behavior: isolation, frame heads, continuous confirmation, out-of-order holes, prediction divergence, single/multiple mismatch, restore/replay, commit atomicity, snapshot track identity, canonical Hash convergence, history boundaries, ownership, and P1-G/P2-A/P2-B regression.
- Compile, barrier, and build accept integration/protocol/package health.
- P2-C intentionally makes no new visible-source arbitration change. Existing Predicted presentation may still show the known fixed-100ms correction. Therefore no new P2-C manual visual acceptance is required, and test success must not be described as visual smoothness.
- If implementation accidentally changes visible behavior, stop after automated verification and give 帅老大 exact manual steps and expected visuals; do not call that behavior accepted without observation.
- Stop after P2-C implementation, verification, evidence summary, and documentation update. Do not begin P2-D, ECS, commit, merge, or push.

## 8. Self-review

- Spec coverage: all eight required audit questions map to ownership/frame rules, Tasks 1–5, the scenario matrix, and acceptance classification.
- Placeholder scan: no TBD/TODO/“similar to” implementation placeholders remain.
- Type consistency: `WorldTrack`, `WorldSnapshotStore`, `FrameAdvanceResult`, `ReconcileResult`, and coordinator APIs use canonical frames throughout; Predicted/Confirmed stores are explicit at every replay/read call.
- Scope check: no View World, basketball presentation policy, ECS, network protocol, gameplay, System-order, input-bit, or canonical-mapping change is included.

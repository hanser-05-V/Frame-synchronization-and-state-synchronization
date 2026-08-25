# P2-A Deterministic World Module Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Promote Route C's existing shared single-frame simulation into a deep deterministic-world module with one canonical state value, without changing gameplay, networking, prediction rules, or presentation behavior.

**Architecture:** Route C already makes normal execution and rollback replay call `FrameSimulationSystem.Step`; P2-A preserves that verified behavior instead of claiming to invent it. The change introduces `SimulationWorldState` as the canonical synchronized value, `WorldStateCodec` as the sole entity/state mapping owner, and `DeterministicWorld` as the small interface used to Step, Capture, and Restore the current basketball world. Confirmed/Predicted dual worlds are deliberately deferred to P2-C.

**Tech Stack:** Unity 2022.3.62f2, C#, NUnit EditMode tests, existing fixed-point math and basketball systems.

---

## File map

### Create

- `Project/Frame Synchronization/Assets/Scripts/FrameSync/SimulationWorldState.cs` — value-only synchronized state.
- `Project/Frame Synchronization/Assets/Scripts/FrameSync/WorldStateCodec.cs` — capture/restore mapping for the existing entities.
- `Project/Frame Synchronization/Assets/Scripts/Gameplay/DeterministicWorld.cs` — deep module exposing Step, Capture, and Restore.
- `Project/Frame Synchronization/Assets/Tests/EditMode/WorldStateCodecTests.cs` — complete field round-trip tests.
- `Project/Frame Synchronization/Assets/Tests/EditMode/DeterministicWorldTests.cs` — continuous/replay equivalence tests.
- Matching Unity `.meta` files for all new assets.

### Modify

- `Project/Frame Synchronization/Assets/Scripts/FrameSync/FrameSnapshot.cs` — store canonical world state and supply migration compatibility accessors.
- `Project/Frame Synchronization/Assets/Scripts/FrameSync/PredictionSystem.cs` — delegate snapshot mapping to `DeterministicWorld`/`WorldStateCodec`.
- `Project/Frame Synchronization/Assets/Scripts/FrameSync/WorldHash.cs` — hash canonical world fields in the existing order.
- `Project/Frame Synchronization/Assets/Scripts/Gameplay/FrameReplaySystem.cs` — consume `DeterministicWorld` instead of receiving five world dependencies.
- `Project/Frame Synchronization/Assets/Scripts/GameController.cs` — construct one `DeterministicWorld` and use it for normal execution and replay.
- Existing snapshot, simulation, replay, and hash tests — preserve compatibility and add regression coverage.

### Explicitly unchanged

- `FrameInput` wire layout and prediction policy;
- network transport and canonical-frame mapping;
- basketball rules and System execution order;
- presentation interpolation, waterline, correction, and ball attachment behavior;
- match flow, scoring, player state count, and highlight replay semantics.

## Non-negotiable invariants

1. P2-A reorganizes state ownership; it does not alter visible or synchronized behavior.
2. `SimulationWorldState` contains every field currently restored and hashed.
3. State values contain no entity, array, list, `Transform`, or other mutable reference.
4. Normal execution and replay call the same `DeterministicWorld.Step` method.
5. `WorldStateCodec` is the only owner of entity-to-state field mapping.
6. WorldHash's schema and output remain unchanged for equivalent state.
7. Existing uncommitted P1-G changes are preserved; implementation starts only after its baseline is accepted.

## Task 0: Protect and verify the baseline

**Files:** Inspect only.

- [ ] **Step 1: Record the working tree**

Run:

```powershell
git status --short
```

Expected: current P1-G edits are visible. Record them before touching P2-A.

- [ ] **Step 2: Compile with Unity 2022.3.62f2**

Run:

```powershell
& "D:\Unity2022\Editor\Unity.exe" -projectPath "$PWD\Project\Frame Synchronization" -batchmode -quit -logFile -
```

Expected: exit code 0 and no script compilation errors.

- [ ] **Step 3: Run the complete EditMode baseline**

Run:

```powershell
& "D:\Unity2022\Editor\Unity.exe" -projectPath "$PWD\Project\Frame Synchronization" -batchmode -runTests -testPlatform EditMode -testResults "$PWD\TestResults-p2a-baseline.xml" -quit -logFile -
```

Expected: zero failed tests. If not, stop; do not mix baseline repair into P2-A.

## Task 1: Define canonical synchronized state test-first

**Files:**

- Create: `Project/Frame Synchronization/Assets/Scripts/FrameSync/SimulationWorldState.cs`
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/WorldStateCodecTests.cs`

- [ ] **Step 1: Write a failing value-copy test**

```csharp
[Test]
public void SimulationWorldState_CopyThenMutate_DoesNotAliasOriginal()
{
    SimulationWorldState original = WorldStateTestData.Create(frameID: 12);
    SimulationWorldState copy = original;

    copy.player0.position.x = FixedInt.FromInt(99);
    copy.ball.holderPlayerIndex = 1;

    Assert.That(copy.player0.position.x, Is.Not.EqualTo(original.player0.position.x));
    Assert.That(copy.ball.holderPlayerIndex, Is.Not.EqualTo(original.ball.holderPlayerIndex));
}
```

Define `WorldStateTestData.Create` in the test file as a private helper that explicitly sets `frameID`, both players' position/facing/state/hasBall, and the ball's position/velocity/state/holder.

- [ ] **Step 2: Run the fixture and verify RED**

Expected: compilation fails because `SimulationWorldState` does not exist.

- [ ] **Step 3: Add value-only state types**

```csharp
namespace FrameSyncDemo
{
    public struct SimulationPlayerState
    {
        public FixedVector3 position;
        public FixedVector3 facing;
        public PlayerEntity.EState state;
        public bool hasBall;
    }

    public struct SimulationBallState
    {
        public FixedVector3 position;
        public FixedVector3 velocity;
        public BallEntity.EState state;
        public int holderPlayerIndex;
    }

    public struct SimulationWorldState
    {
        public int frameID;
        public SimulationPlayerState player0;
        public SimulationPlayerState player1;
        public SimulationBallState ball;
    }
}
```

- [ ] **Step 4: Run the fixture and verify GREEN**

Expected: copy mutation does not affect the original.

## Task 2: Centralize capture and restore test-first

**Files:**

- Create: `Project/Frame Synchronization/Assets/Scripts/FrameSync/WorldStateCodec.cs`
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/WorldStateCodecTests.cs`

- [ ] **Step 1: Write failing round-trip tests**

Add these tests. The capture test asserts `frameID`, both positions, both facings, both player states, both possession flags, ball position, velocity, state, and holder. The restore test mutates those same runtime fields before restoring and repeats the assertions.

```csharp
[Test]
public void Capture_DistinctRuntimeWorld_CopiesEverySynchronizedField()
```

```csharp
[Test]
public void Restore_MutatedRuntimeWorld_RestoresEverySynchronizedField()
```

```csharp
[Test]
public void Capture_PlayerCountIsNotTwo_ThrowsArgumentException()
```

- [ ] **Step 2: Run the fixture and verify RED**

Expected: compilation fails because `WorldStateCodec` does not exist.

- [ ] **Step 3: Implement the codec interface**

```csharp
public static class WorldStateCodec
{
    public static SimulationWorldState Capture(
        int frameID,
        PlayerEntity[] players,
        BallEntity ball);

    public static void Restore(
        in SimulationWorldState state,
        PlayerEntity[] players,
        BallEntity ball);
}
```

The implementation must validate exactly two non-null players and a non-null ball, then map every current snapshot field once. It must not read or write Unity presentation state.

- [ ] **Step 4: Run the fixture and verify GREEN**

Expected: all fields round-trip and invalid input fails explicitly.

## Task 3: Build the deterministic-world module

**Files:**

- Create: `Project/Frame Synchronization/Assets/Scripts/Gameplay/DeterministicWorld.cs`
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/DeterministicWorldTests.cs`

- [ ] **Step 1: Write a failing delegation test**

```csharp
[Test]
public void Step_MovementInput_ProducesCurrentFrameSimulationResult()
{
    TestRuntimeWorld runtime = TestRuntimeWorld.Create();
    var world = new DeterministicWorld(
        runtime.players,
        runtime.stateMachines,
        runtime.ball,
        FixedInt.FromFloat(5f * 0.033f),
        CourtConstant.LogicDeltaTime);

    FrameSimulationResult result = world.Step(new[]
    {
        new FrameInput(1, 0),
        default
    });

    Assert.That(runtime.players[0].position.z, Is.GreaterThan(FixedInt.Zero));
    Assert.That(result.heldInvariantValid, Is.True);
}
```

The move distance and delta time above match the current `GameController`: `FixedInt.FromFloat(_moveSpeed * 0.033f)` with the scene default `_moveSpeed = 5f`, and `CourtConstant.LogicDeltaTime`.

- [ ] **Step 2: Run the fixture and verify RED**

Expected: `DeterministicWorld` does not exist.

- [ ] **Step 3: Implement a small deep interface**

```csharp
public sealed class DeterministicWorld
{
    public FrameSimulationResult Step(FrameInput[] inputs);
    public SimulationWorldState Capture(int frameID);
    public void Restore(in SimulationWorldState state);
}
```

Its constructor accepts the current players, state machines, ball, move distance, and fixed delta time. `Step` delegates to the verified `FrameSimulationSystem.Step` without changing System order; Capture/Restore delegate to `WorldStateCodec`.

- [ ] **Step 4: Add validation tests**

Verify null input, input count other than two, invalid world construction, and restore of a complete state all fail or succeed explicitly.

- [ ] **Step 5: Run deterministic-world and existing simulation tests**

Expected: all pass with identical gameplay results.

## Task 4: Move snapshots and hashing onto the canonical state

**Files:**

- Modify: `Project/Frame Synchronization/Assets/Scripts/FrameSync/FrameSnapshot.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/FrameSync/PredictionSystem.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/FrameSync/WorldHash.cs`
- Modify: snapshot and hash test fixtures.

- [ ] **Step 1: Write failing state-parity tests**

```csharp
[Test]
public void FrameSnapshot_FromWorld_PreservesAllLegacyValues()
```

```csharp
[Test]
public void Compute_WorldAndEquivalentSnapshot_ReturnSameHash()
```

```csharp
[Test]
public void CaptureRestoreCapture_PreservesCanonicalHash()
```

- [ ] **Step 2: Run focused tests and verify RED**

Expected: snapshot and hash have no canonical-world path.

- [ ] **Step 3: Add migration-safe snapshot conversion**

Add `SimulationWorldState world` plus `FromWorld`/`ToWorld` conversion owned by `FrameSnapshot`. Preserve current scalar fields or compatible access until every current caller is migrated in an approved later cleanup.

- [ ] **Step 4: Replace manual mapping in PredictionSystem**

`TakeWorldSnapshot` must call `DeterministicWorld.Capture` or `WorldStateCodec.Capture`; restore must call the paired Restore path. Do not change `ResolveRemote` or prediction history in P2-A.

- [ ] **Step 5: Add canonical hash overload**

```csharp
public static ulong Compute(
    in SimulationWorldState world,
    int canonicalFrameID);
```

Hash fields in the exact current order. Make the snapshot overload delegate to it. If known-value tests change, stop rather than silently incrementing `SchemaVersion`.

- [ ] **Step 6: Run all snapshot and hash tests**

Expected: existing known hashes and new parity tests pass.

## Task 5: Make normal execution and replay use one world module

**Files:**

- Modify: `Project/Frame Synchronization/Assets/Scripts/Gameplay/FrameReplaySystem.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/GameController.cs`
- Modify: replay and integration tests.

- [ ] **Step 1: Write a failing replay-equivalence test**

Use a 20-frame script containing movement, pickup, held movement, shoot release, and airborne physics:

```csharp
[Test]
public void Replay_FromFrameSeven_MatchesTwentyFrameContinuousWorld()
```

Run frames 0–19 continuously in one world. In another, run 0–7, restore frame 7, replay 8–19, then compare every field and Hash.

- [ ] **Step 2: Run the test and verify RED**

Expected: replay still receives separate players/state machines/ball dependencies.

- [ ] **Step 3: Replace replay's world parameter cluster**

Change the replay interface to accept one `DeterministicWorld`. Inside the replay loop call only:

```csharp
world.Step(inputs);
SimulationWorldState state = world.Capture(frame);
```

Retain the existing public overload as a compatibility facade for the current tests. It constructs the same dependency cluster and immediately delegates to the new overload; it must not contain a second replay loop.

- [ ] **Step 4: Construct one world module in GameController**

After current entities and state machines are initialized, construct `DeterministicWorld`. Normal `OnFrameUpdate` and rollback replay must use this same module interface. Do not modify presentation sampling or network timing.

- [ ] **Step 5: Run simulation, replay, possession, shot, physics, hash, and P1-G tests**

Expected: all pass and replay equivalence is exact.

## Task 6: Full verification and documentation sync

**Files:** Documentation only after tests prove behavior.

- [ ] **Step 1: Scan for duplicate field mapping**

Run:

```powershell
rg -n "player1X|player2X|ballHolder|holderPlayerIndex" "Project\Frame Synchronization\Assets\Scripts\FrameSync"
```

Expected: mapping exists only in `WorldStateCodec` and documented temporary snapshot compatibility conversion.

- [ ] **Step 2: Run the complete EditMode suite**

Expected: zero failed tests.

- [ ] **Step 3: Compile independently**

Expected: exit code 0 and no Unity Console compile errors.

- [ ] **Step 4: Run local and dual-client smoke tests**

Verify movement, pickup, held-ball movement, shoot, free-ball reset, end-match request, rollback and highlight replay. P2-A must not create new timing, possession, Hash, or presentation differences.

- [ ] **Step 5: Check scope and preserve P1-G**

Run:

```powershell
git diff --check
git status --short
```

Expected: no whitespace errors and no unrelated file changes.

- [ ] **Step 6: Mark only P2-A as complete**

Update the route and current architecture document with verified facts. Do not describe InputLedger, dual worlds, ViewWorldBuilder, or ECS as implemented.

## Completion gate

- [ ] Canonical synchronized state is a value with no mutable aliases.
- [ ] Capture/Restore mapping has one owner.
- [ ] Normal execution and replay use one `DeterministicWorld.Step` interface.
- [ ] Hash output is unchanged for equivalent state.
- [ ] Gameplay, protocol, prediction, presentation and match flow are unchanged.
- [ ] Automated tests, compile verification and dual-client smoke tests pass.
- [ ] Existing P1-G edits remain intact.
- [ ] No automatic commit, merge, or push occurred.

After this gate, write and approve a separate P2-B plan for `FrameInputLedger`. Do not start dual-world migration inside P2-A.

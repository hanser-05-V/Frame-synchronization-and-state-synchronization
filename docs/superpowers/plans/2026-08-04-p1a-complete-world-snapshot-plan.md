# P1-A Complete World Snapshot Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an isolated, fully tested API that captures and restores every dynamic deterministic field of two players and the basketball without changing the current runtime frame flow.

**Architecture:** Extend the existing flat `FrameSnapshot` value type so it remains allocation-free and compatible with the legacy XZ API. `PredictionSystem` owns buffer access and exposes separate `TakeWorldSnapshot`/`RestoreWorldSnapshot` methods; `GameController`, rollback replay, networking, and hashing remain untouched until P1-B.

**Tech Stack:** Unity 2022.3.62f2, C#, Unity Test Framework/NUnit, `FixedInt`, `FixedVector3`.

**Global constraints:** Work only in `E:\帧同步_RouteC`; do not modify `E:\帧同步`; do not commit or push; preserve all P0 working-tree changes; use TDD; do not wire the new API into `GameController`.

---

### Task 1: Specify world snapshot round-trip behavior

**Files:**
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/PredictionSystemSnapshotTests.cs`

- [ ] **Step 1: Add round-trip and missing-frame tests**

Create the fixture below. It intentionally calls the wished-for API before production code exists.

```csharp
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class PredictionSystemSnapshotTests
    {
        [Test]
        public void RestoreWorldSnapshot_CapturedWorldWasMutated_RestoresEveryDynamicField()
        {
            var expectedPlayers = CreatePlayers();
            var livePlayers = CreatePlayers();
            var expectedBall = CreateBall();
            var liveBall = CreateBall();
            var prediction = new PredictionSystem();
            prediction.Init();

            prediction.TakeWorldSnapshot(37, livePlayers, liveBall);
            MutateWorld(livePlayers, liveBall);

            bool restored = prediction.RestoreWorldSnapshot(37, livePlayers, liveBall);

            Assert.IsTrue(restored);
            AssertPlayerDynamicState(expectedPlayers[0], livePlayers[0]);
            AssertPlayerDynamicState(expectedPlayers[1], livePlayers[1]);
            AssertBallState(expectedBall, liveBall);
            Assert.AreEqual(10, livePlayers[0].playerIndex);
            Assert.AreEqual(11, livePlayers[1].playerIndex);
        }

        [Test]
        public void RestoreWorldSnapshot_FrameDoesNotExist_ReturnsFalseWithoutMutation()
        {
            var expectedPlayers = CreatePlayers();
            var livePlayers = CreatePlayers();
            var expectedBall = CreateBall();
            var liveBall = CreateBall();
            var prediction = new PredictionSystem();
            prediction.Init();

            bool restored = prediction.RestoreWorldSnapshot(404, livePlayers, liveBall);

            Assert.IsFalse(restored);
            AssertPlayerDynamicState(expectedPlayers[0], livePlayers[0]);
            AssertPlayerDynamicState(expectedPlayers[1], livePlayers[1]);
            AssertBallState(expectedBall, liveBall);
            Assert.AreEqual(expectedPlayers[0].playerIndex, livePlayers[0].playerIndex);
            Assert.AreEqual(expectedPlayers[1].playerIndex, livePlayers[1].playerIndex);
        }

        private static PlayerEntity[] CreatePlayers()
        {
            var player0 = new PlayerEntity();
            player0.Reset(
                new FixedVector3(FixedInt.FromInt(-3), FixedInt.FromInt(1), FixedInt.FromInt(2)),
                0);
            player0.facing = new FixedVector3(FixedInt.One, FixedInt.Zero, FixedInt.Zero);
            player0.state = PlayerEntity.EState.Shooting;
            player0.hasBall = false;

            var player1 = new PlayerEntity();
            player1.Reset(
                new FixedVector3(FixedInt.FromInt(4), FixedInt.FromInt(2), FixedInt.FromInt(-5)),
                1);
            player1.facing = new FixedVector3(FixedInt.Zero, FixedInt.Zero, -FixedInt.One);
            player1.state = PlayerEntity.EState.Run;
            player1.hasBall = true;

            return new[] { player0, player1 };
        }

        private static BallEntity CreateBall()
        {
            return new BallEntity
            {
                position = new FixedVector3(
                    FixedInt.FromInt(2),
                    FixedInt.FromInt(3),
                    FixedInt.FromInt(7)),
                velocity = new FixedVector3(
                    FixedInt.FromInt(-1),
                    FixedInt.FromInt(6),
                    FixedInt.FromInt(2)),
                state = BallEntity.EState.Airborne,
                holderPlayerIndex = -1
            };
        }

        private static void MutateWorld(PlayerEntity[] players, BallEntity ball)
        {
            players[0].position = FixedVector3.Zero;
            players[0].facing = FixedVector3.Back;
            players[0].state = PlayerEntity.EState.Fall;
            players[0].hasBall = true;
            players[0].playerIndex = 10;

            players[1].position = FixedVector3.One;
            players[1].facing = FixedVector3.Right;
            players[1].state = PlayerEntity.EState.Idle;
            players[1].hasBall = false;
            players[1].playerIndex = 11;

            ball.position = FixedVector3.Zero;
            ball.velocity = FixedVector3.Zero;
            ball.state = BallEntity.EState.Free;
            ball.holderPlayerIndex = 1;
        }

        private static void AssertPlayerDynamicState(PlayerEntity expected, PlayerEntity actual)
        {
            AssertVectorRaw(expected.position, actual.position);
            AssertVectorRaw(expected.facing, actual.facing);
            Assert.AreEqual(expected.state, actual.state);
            Assert.AreEqual(expected.hasBall, actual.hasBall);
        }

        private static void AssertBallState(BallEntity expected, BallEntity actual)
        {
            AssertVectorRaw(expected.position, actual.position);
            AssertVectorRaw(expected.velocity, actual.velocity);
            Assert.AreEqual(expected.state, actual.state);
            Assert.AreEqual(expected.holderPlayerIndex, actual.holderPlayerIndex);
        }

        private static void AssertVectorRaw(FixedVector3 expected, FixedVector3 actual)
        {
            Assert.AreEqual(expected.x._raw, actual.x._raw);
            Assert.AreEqual(expected.y._raw, actual.y._raw);
            Assert.AreEqual(expected.z._raw, actual.z._raw);
        }
    }
}
```

- [ ] **Step 2: Run the fixture and verify RED**

Run Unity EditMode with filter `FrameSyncDemo.Tests.PredictionSystemSnapshotTests`.

Expected: compilation fails only because `PredictionSystem` has no `TakeWorldSnapshot` or `RestoreWorldSnapshot` methods. Record the Unity log as RED evidence.

### Task 2: Implement complete value capture and restore

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Scripts/FrameSync/FrameSnapshot.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/FrameSync/PredictionSystem.cs`
- Test: `Project/Frame Synchronization/Assets/Tests/EditMode/PredictionSystemSnapshotTests.cs`

- [ ] **Step 1: Complete player fields in `FrameSnapshot`**

Add Y position and complete facing fields beside the existing player fields:

```csharp
public FixedInt player1Y;
public FixedInt player2Y;

public FixedInt player1FacingX;
public FixedInt player1FacingY;
public FixedInt player1FacingZ;
public FixedInt player2FacingX;
public FixedInt player2FacingY;
public FixedInt player2FacingZ;
```

Retain the existing player XZ, state, ball ownership, basketball, and legacy buffer fields.

- [ ] **Step 2: Add `TakeWorldSnapshot`**

Add this method to `PredictionSystem` without changing the legacy `TakeSnapshot` method:

```csharp
public void TakeWorldSnapshot(int frameID, PlayerEntity[] players, BallEntity ball)
{
    PlayerEntity player0 = players[0];
    PlayerEntity player1 = players[1];
    var snapshot = new FrameSnapshot
    {
        frameID = frameID,
        player1X = player0.position.x,
        player1Y = player0.position.y,
        player1Z = player0.position.z,
        player2X = player1.position.x,
        player2Y = player1.position.y,
        player2Z = player1.position.z,
        player1FacingX = player0.facing.x,
        player1FacingY = player0.facing.y,
        player1FacingZ = player0.facing.z,
        player2FacingX = player1.facing.x,
        player2FacingY = player1.facing.y,
        player2FacingZ = player1.facing.z,
        player1State = (int)player0.state,
        player2State = (int)player1.state,
        player1HasBall = player0.hasBall,
        player2HasBall = player1.hasBall,
        ballPosX = ball.position.x,
        ballPosY = ball.position.y,
        ballPosZ = ball.position.z,
        ballVelX = ball.velocity.x,
        ballVelY = ball.velocity.y,
        ballVelZ = ball.velocity.z,
        ballState = (int)ball.state,
        ballHolder = ball.holderPlayerIndex
    };
    _snapshotBuffer.AddSnapshot(snapshot);
}
```

- [ ] **Step 3: Add `RestoreWorldSnapshot`**

Add this method beside `TakeWorldSnapshot`:

```csharp
public bool RestoreWorldSnapshot(int frameID, PlayerEntity[] players, BallEntity ball)
{
    if (!_snapshotBuffer.HasSnapshot(frameID))
        return false;

    FrameSnapshot snapshot = _snapshotBuffer.GetSnapshot(frameID);
    players[0].position = new FixedVector3(
        snapshot.player1X,
        snapshot.player1Y,
        snapshot.player1Z);
    players[1].position = new FixedVector3(
        snapshot.player2X,
        snapshot.player2Y,
        snapshot.player2Z);
    players[0].facing = new FixedVector3(
        snapshot.player1FacingX,
        snapshot.player1FacingY,
        snapshot.player1FacingZ);
    players[1].facing = new FixedVector3(
        snapshot.player2FacingX,
        snapshot.player2FacingY,
        snapshot.player2FacingZ);
    players[0].state = (PlayerEntity.EState)snapshot.player1State;
    players[1].state = (PlayerEntity.EState)snapshot.player2State;
    players[0].hasBall = snapshot.player1HasBall;
    players[1].hasBall = snapshot.player2HasBall;
    ball.position = new FixedVector3(snapshot.ballPosX, snapshot.ballPosY, snapshot.ballPosZ);
    ball.velocity = new FixedVector3(snapshot.ballVelX, snapshot.ballVelY, snapshot.ballVelZ);
    ball.state = (BallEntity.EState)snapshot.ballState;
    ball.holderPlayerIndex = snapshot.ballHolder;
    return true;
}
```

- [ ] **Step 4: Run the fixture and verify GREEN**

Run the same filtered EditMode fixture.

Expected: `2/2` tests pass. This proves value-copy isolation, complete field restoration, `playerIndex` exclusion, and missing-frame no-mutation behavior.

### Task 3: Enforce the fixed two-player world contract

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/PredictionSystemSnapshotTests.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/FrameSync/PredictionSystem.cs`

- [ ] **Step 1: Add invalid-world tests**

Add two tests to the existing fixture:

```csharp
[Test]
public void TakeWorldSnapshot_InvalidWorld_ThrowsArgumentException()
{
    var prediction = new PredictionSystem();
    prediction.Init();
    var players = CreatePlayers();
    var ball = CreateBall();

    Assert.Catch<System.ArgumentException>(
        () => prediction.TakeWorldSnapshot(1, new[] { players[0] }, ball));
    Assert.Catch<System.ArgumentException>(
        () => prediction.TakeWorldSnapshot(1, new PlayerEntity[] { players[0], null }, ball));
    Assert.Catch<System.ArgumentException>(
        () => prediction.TakeWorldSnapshot(1, players, null));
}

[Test]
public void RestoreWorldSnapshot_InvalidWorld_ThrowsArgumentException()
{
    var prediction = new PredictionSystem();
    prediction.Init();
    var players = CreatePlayers();
    var ball = CreateBall();

    Assert.Catch<System.ArgumentException>(
        () => prediction.RestoreWorldSnapshot(1, null, ball));
    Assert.Catch<System.ArgumentException>(
        () => prediction.RestoreWorldSnapshot(1, new[] { players[0] }, ball));
    Assert.Catch<System.ArgumentException>(
        () => prediction.RestoreWorldSnapshot(1, players, null));
}
```

- [ ] **Step 2: Run the fixture and verify RED**

Expected: the two new invalid-world tests fail because the current implementation throws `NullReferenceException` or `IndexOutOfRangeException`, or fails to throw for the missing-frame path.

- [ ] **Step 3: Add shared validation**

Add `using System;` to `PredictionSystem.cs`, then add:

```csharp
private static void ValidateWorldState(PlayerEntity[] players, BallEntity ball)
{
    if (players == null || players.Length != 2 || players[0] == null || players[1] == null)
    {
        throw new ArgumentException(
            "World snapshots require exactly two non-null players.",
            nameof(players));
    }

    if (ball == null)
        throw new ArgumentNullException(nameof(ball));
}
```

Call `ValidateWorldState(players, ball);` as the first statement in both complete-world methods.

- [ ] **Step 4: Run the fixture and verify GREEN**

Expected: `4/4` snapshot tests pass.

### Task 4: Verify P1-A without changing runtime integration

**Files:**
- Verify only; no additional production files.

- [ ] Run all EditMode tests and require `13/13` passing with zero compiler errors.
- [ ] Run `git diff --check` and require exit code zero.
- [ ] Confirm `GameController.cs`, rollback methods, `MD5Checker.cs`, networking files, and the P0 gameplay systems received no P1-A edits.
- [ ] Confirm Route C remains on `delivery/route-c` at the same HEAD because no commit was created.
- [ ] Recompute the `E:\帧同步` branch, HEAD, status-entry count, and status fingerprint; require the pre-P1 values to remain unchanged.
- [ ] Leave all Route C changes uncommitted and do not push.

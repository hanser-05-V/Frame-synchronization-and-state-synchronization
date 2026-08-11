# P0 Minimal Shot Loop Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a deterministic P0 held-ball-to-shot-to-result loop with repeatable EditMode coverage.

**Architecture:** Pure static gameplay systems own possession and shot calculations; `GameController` orchestrates them inside the existing frame callbacks. Runtime scripts move into one named assembly so an Editor-only NUnit assembly can test real production types.

**Tech Stack:** Unity 2022.3.62f2, C#, FixedInt/FixedVector3, Unity Test Framework/NUnit.

---

### Task 1: Establish test assembly boundaries

**Files:**
- Create: `Project/Frame Synchronization/Assets/Scripts/FrameSyncDemo.Runtime.asmdef`
- Create: `Project/Frame Synchronization/Assets/Scripts/Editor/FrameSyncDemo.Editor.asmdef`
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/FrameSyncDemo.Tests.EditMode.asmdef`

- [ ] Create `FrameSyncDemo.Runtime` for non-Editor scripts under `Assets/Scripts`.
- [ ] Isolate `Assets/Scripts/Editor` as `FrameSyncDemo.Editor`, Editor-only, referencing runtime.
- [ ] Create `FrameSyncDemo.Tests.EditMode`, Editor-only, referencing runtime and `TestAssemblies`.
- [ ] Import the project in batch mode and confirm assembly boundaries compile before production behavior changes.

### Task 2: Reproduce and fix score-plane regression

**Files:**
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/BallPhysicsSystemTests.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/Gameplay/BallPhysicsSystem.cs`

- [ ] Add `Update_BallCrossesHoopPlaneInsideRadius_SetsScored`, starting above the hoop with downward velocity.
- [ ] Run the single fixture and verify RED: current code remains `Airborne` because it reconstructs the previous position in the wrong direction.
- [ ] Save `previousPosition` before integration and pass it into score detection.
- [ ] Run the fixture and verify GREEN.
- [ ] Add `Update_OffTargetBallHitsFloor_SetsFree` and confirm it passes.

### Task 3: Implement Held possession test-first

**Files:**
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/BallPossessionSystemTests.cs`
- Create: `Project/Frame Synchronization/Assets/Scripts/Gameplay/BallPossessionSystem.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/Gameplay/CourtConstant.cs`

- [ ] Write `UpdateHeldBall_ConsistentPossession_UpdatesFixedHandPointAndClearsVelocity` against the wished-for API.
- [ ] Run the fixture and verify RED because `BallPossessionSystem` does not exist.
- [ ] Add fixed hand height/forward offset constants and the minimal `TryUpdateHeldBall` implementation.
- [ ] Run the fixture and verify GREEN.
- [ ] Write and verify `UpdateHeldBall_InconsistentPossession_ReturnsFalseWithoutMutation`.

### Task 4: Implement deterministic shot test-first

**Files:**
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/BallShotSystemTests.cs`
- Create: `Project/Frame Synchronization/Assets/Scripts/Gameplay/BallShotSystem.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/Gameplay/CourtConstant.cs`

- [ ] Write `TryShoot_ValidPossession_ReleasesBallAndBuildsDeterministicVelocity`.
- [ ] Run the fixture and verify RED because `BallShotSystem` does not exist.
- [ ] Add release offsets, fixed flight frames and logic delta-time constants.
- [ ] Implement validation, state transitions and the fixed-point velocity formula.
- [ ] Run the fixture and verify GREEN.
- [ ] Add `TryShoot_NoPossession_ReturnsFalseWithoutMutation` and the N-frame target-position assertion.

### Task 5: Integrate the P0 frame flow

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Scripts/GameController.cs`

- [ ] Initialize P0 and the ball with a consistent Held relationship.
- [ ] After player movement, dispatch the holder's shoot input or update the Held hand point.
- [ ] Recover `Shooting` to `Idle` at the start of the following frame before locomotion.
- [ ] Reuse the shared fixed logic delta time in post-frame ball physics.
- [ ] Keep snapshots and rollback unchanged and explicitly defer full basketball rollback to P1.

### Task 6: Verify the vertical slice

**Files:**
- Verify only; no additional production files.

- [ ] Run all EditMode tests and require zero failures.
- [ ] Run a batch-mode compile and require no compiler errors.
- [ ] Open `SampleScene.unity`, run locally for 60 seconds and check the Console.
- [ ] Exercise P0 movement, Held follow, shoot, Airborne and Scored/Free transitions.
- [ ] Review `git diff`, confirm no unrelated files and verify `E:\帧同步` branch/HEAD/status fingerprint is unchanged.
- [ ] Do not commit or push; leave the reviewed Route C changes for 帅老大.


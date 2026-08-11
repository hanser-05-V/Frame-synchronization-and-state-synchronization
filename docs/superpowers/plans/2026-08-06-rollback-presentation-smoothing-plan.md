# Rollback Presentation Smoothing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep deterministic rollback and WorldHash unchanged while making remote-player and basketball corrections visually continuous over 100ms.

**Architecture:** Add one stateful visual-offset decay primitive and one stateless presentation-policy resolver. `GameController` remains the orchestration boundary: logic frames only update deterministic state and snapshots, while `LateUpdate` writes Unity Transforms once per rendered frame.

**Tech Stack:** Unity 2022.3.62f2, C#, UnityEngine `Vector3`, NUnit EditMode tests, existing `FrameSyncDemo.Runtime` assembly.

**Repository constraints:** Work only in `E:\帧同步_RouteC`; preserve all existing uncommitted P0–P1-D changes; do not commit, merge, push, reset, clean, or overwrite with checkout. The required workspace is the existing `delivery/route-c` checkout, so no separate worktree is created.

---

## File map

- Create `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationCorrectionSmoother.cs`: own correction offset, elapsed render time, exact completion, restart, and snap behavior.
- Create `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationTargetResolver.cs`: derive player/ball render targets and identify which local/remote paths may smooth.
- Create `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationCorrectionSmootherTests.cs`: regression coverage for continuity, moving targets, exact completion, restart, snap, and invalid time values.
- Create `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationTargetResolverTests.cs`: regression coverage for local/remote policy and held-ball attachment to a smoothed holder.
- Modify `Project/Frame Synchronization/Assets/Scripts/GameController.cs`: move Transform writes to `LateUpdate`, begin corrections only after successful replay, and snap lifecycle transitions.

### Task 1: Build the visual correction primitive with TDD

**Files:**
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationCorrectionSmootherTests.cs`
- Create: `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationCorrectionSmoother.cs`

- [ ] **Step 1: Write the failing smoother tests**

Cover these concrete calls and assertions:

```csharp
var smoother = new PresentationCorrectionSmoother(0.1f);
Assert.AreEqual(target, smoother.Evaluate(target, 0.016f));

smoother.BeginCorrection(new Vector3(5f, 0f, 0f), Vector3.zero);
Assert.AreEqual(new Vector3(5f, 0f, 0f), smoother.Evaluate(Vector3.zero, 0.016f));
Assert.AreEqual(new Vector3(3f, 0f, 0f), smoother.Evaluate(new Vector3(1f, 0f, 0f), 0.05f));
Assert.AreEqual(new Vector3(2f, 0f, 0f), smoother.Evaluate(new Vector3(2f, 0f, 0f), 0.05f));
Assert.IsFalse(smoother.IsCorrecting);
```

Also test restarting from the current displayed point, `Snap`, constructor rejection of zero/negative/NaN/infinity, and negative/NaN/infinite `deltaTime` behaving as zero.

- [ ] **Step 2: Run only the new fixture and verify RED**

Run in the already-open RouteC Unity Editor:

```text
EditMode filter: FrameSyncDemo.Tests.PresentationCorrectionSmootherTests
```

Expected: compilation or test failure because `PresentationCorrectionSmoother` does not exist.

- [ ] **Step 3: Add the minimal smoother implementation**

Implement this exact public contract:

```csharp
public sealed class PresentationCorrectionSmoother
{
    public PresentationCorrectionSmoother(float durationSeconds);
    public bool IsCorrecting { get; }
    public void BeginCorrection(Vector3 currentDisplayPosition, Vector3 correctedTargetPosition);
    public Vector3 Evaluate(Vector3 currentTargetPosition, float deltaTime);
    public void Snap();
}
```

Use a stored start display for the first evaluation, then decay the original offset with `1 - t*t*(3 - 2*t)`. Clamp elapsed time to the duration and return the current target exactly at completion. Treat non-finite or negative `deltaTime` as zero.

- [ ] **Step 4: Re-run the new fixture and verify GREEN**

Expected: every `PresentationCorrectionSmootherTests` test passes with no Console errors.

### Task 2: Build presentation target and smoothing policy with TDD

**Files:**
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationTargetResolverTests.cs`
- Create: `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationTargetResolver.cs`

- [ ] **Step 1: Write failing policy and target tests**

Exercise these behaviors:

```csharp
Assert.IsFalse(PresentationTargetResolver.ShouldSmoothPlayer(0, 0));
Assert.IsTrue(PresentationTargetResolver.ShouldSmoothPlayer(1, 0));

ball.state = BallEntity.EState.Held;
ball.holderPlayerIndex = 1;
Assert.IsTrue(PresentationTargetResolver.ShouldSmoothBall(ball, 0));
Assert.IsFalse(PresentationTargetResolver.ShouldSmoothBall(ball, 1));
```

For a remotely held ball, provide a holder display position displaced from its logic render target and assert that `ResolveBallTarget` applies the identical displacement to the ball. Assert that `Free`, `Airborne`, and invalid-holder paths return `ball.position.ToVector3()`.

- [ ] **Step 2: Run only the resolver fixture and verify RED**

```text
EditMode filter: FrameSyncDemo.Tests.PresentationTargetResolverTests
```

Expected: compilation or test failure because `PresentationTargetResolver` does not exist.

- [ ] **Step 3: Add the resolver implementation**

Implement these methods without storing simulation state:

```csharp
public static Vector3 ResolvePlayerTarget(PlayerEntity player)
{
    return player.position.ToVector3() + Vector3.up * 0.5f;
}

public static bool ShouldSmoothPlayer(int playerIndex, int localPlayerIndex)
{
    return playerIndex != localPlayerIndex;
}

public static bool ShouldSmoothBall(BallEntity ball, int localPlayerIndex)
{
    return ball.state != BallEntity.EState.Held ||
        ball.holderPlayerIndex != localPlayerIndex;
}
```

`ResolveBallTarget` returns the logic ball position unless Held has a valid holder and display array entry. For valid Held state, calculate `holderDisplay + (logicBall - holderLogicRenderTarget)` so the ball inherits the holder's visual correction.

- [ ] **Step 4: Re-run both new fixtures and verify GREEN**

Expected: both presentation fixtures pass without warnings or errors.

### Task 3: Integrate once-per-render presentation updates

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Scripts/GameController.cs`

- [ ] **Step 1: Add presentation state and initialize it**

Add serialized duration and runtime-only state:

```csharp
[Header("回滚表现平滑")]
[SerializeField] private float _rollbackVisualSmoothingSeconds = 0.1f;

private PresentationCorrectionSmoother _remotePlayerSmoother;
private PresentationCorrectionSmoother _ballSmoother;
private Vector3[] _playerPresentationPositions;
```

Initialize the two smoothers and fixed-size display array after render objects are created. If the serialized value is not finite and positive, use `0.1f` locally without changing synchronized state.

- [ ] **Step 2: Stop writing Transforms from every logic frame**

Remove `SyncPresentationFromLogic()` from `OnPostFrameUpdate`. Keep snapshot capture, normal WorldHash timing, and all deterministic simulation calls untouched.

- [ ] **Step 3: Replace hard sync with a render-only evaluator**

Change the presentation method to accept render delta time. Resolve local identity from `NetworkClient.LocalPlayerIndex`; write the local player directly, evaluate only the remote player smoother, fill `_playerPresentationPositions`, then resolve and display the ball. If the ball is locally held, snap its smoother and use the target directly; otherwise evaluate the ball smoother.

- [ ] **Step 4: Render once at the end of `LateUpdate`**

After input draining and any pending rollback, call:

```csharp
SyncPresentationFromLogic(_paused ? 0f : Time.deltaTime);
```

This call must remain outside deterministic frame stepping and WorldHash construction.

- [ ] **Step 5: Let Unity compile and inspect the Console**

Expected: no C# compilation errors and no new warnings from the presentation files.

### Task 4: Start smoothing at the successful rollback boundary

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Scripts/GameController.cs`

- [ ] **Step 1: Capture current visuals before replay**

At the beginning of `DoRollback`, resolve local/remote indices and capture the current remote-player Transform and basketball Transform. Do not copy these values into entities, snapshots, or frame buffers.

- [ ] **Step 2: Remove the post-replay hard snap**

Keep `SyncLegacyPositionsFromEntities()` after replay, but remove the unconditional direct Transform sync. If replay fails, retain the current error log and return without starting a new correction.

- [ ] **Step 3: Begin both corrections only after replay succeeds**

Start the remote smoother from captured remote display to `ResolvePlayerTarget(correctedRemote)`. Populate the temporary player display array from current Transforms, derive the corrected ball presentation target, and either snap the ball when locally held or begin its correction from the captured ball display.

- [ ] **Step 4: Preserve rollback and hash ordering**

Keep the existing rollback result log and `LogRollbackWorldHash` after correction setup. Verify that snapshot lookup and `WorldHash.Compute` still use only `_playerEntities` and `_ballEntity`.

- [ ] **Step 5: Compile and run all presentation fixtures**

Expected: both new fixtures pass and the Unity Console contains no compiler errors.

### Task 5: Snap lifecycle transitions and protect local responsiveness

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Scripts/GameController.cs`

- [ ] **Step 1: Add one lifecycle snap method**

Create `SnapPresentationToLogic()` that calls `Snap()` on both smoothers and writes the latest logic targets with zero delta time.

- [ ] **Step 2: Use snap for world changes**

Call the lifecycle snap after initialization, `ResetPositions`, playback start-position restoration, explicit playback stop, and automatic playback completion. Do not smooth these transitions.

- [ ] **Step 3: Verify pause behavior**

Ensure paused `LateUpdate` passes zero delta time, so an in-progress correction remains at its current progress until resume.

- [ ] **Step 4: Run the complete EditMode suite**

Run all EditMode tests in the open RouteC editor and export results to a new Windows temporary XML file. Expected result: previous 76 tests plus all new presentation tests pass, with zero failures and zero skipped tests.

### Task 6: Final verification and independent review

**Files:**
- Inspect all files listed in this plan.
- Do not modify `E:\帧同步`.

- [ ] **Step 1: Verify repository invariants**

Confirm RouteC remains on `delivery/route-c` at base HEAD `37260b437260c7712c658d5d0e05cbdd183accfb`, with all prior uncommitted files still present. Recalculate the old-project branch, HEAD, three-entry status count, and status fingerprint and compare them to the handoff values.

- [ ] **Step 2: Run focused and complete automated verification again**

Run both presentation fixtures, then the entire EditMode suite, and inspect Unity Console errors. Fresh evidence is required before any completion claim.

- [ ] **Step 3: Request independent code review**

Ask a reviewer to compare the implementation against `docs/superpowers/specs/2026-08-06-rollback-presentation-smoothing-design.md`, focusing on deterministic-state isolation, local-player latency, held-ball behavior, continuous rollback, allocation risk, and lifecycle snap coverage. Resolve every Critical or Important finding and rerun affected tests.

- [ ] **Step 4: Hand off unified manual acceptance**

Ask 帅老大 to verify both clients under normal, 100ms, and 200ms server delay: same-frame WorldHash and final positions remain equal; local response remains immediate; remote player and basketball corrections have no visible hard snap; remote-held ball has no prolonged separation; reset and playback snap immediately.

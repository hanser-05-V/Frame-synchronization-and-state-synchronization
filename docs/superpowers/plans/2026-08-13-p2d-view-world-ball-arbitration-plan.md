# P2-D View World and Basketball Presentation Arbitration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a read-only View World that presents the local player from Predicted history, the remote player from Confirmed history, and the basketball through explicit possession-aware source arbitration without changing deterministic simulation.

**Architecture:** P2-D uses two stages. On each completed logic frame, `ViewWorldBuilder` reads value snapshots from the existing coordinator and produces one immutable `ViewWorldState` containing explicit interpolation endpoints, source frames, fallback reasons, and basketball attachment semantics. On each render frame, the existing Unity presentation adapter interpolates that value result and applies bounded correction; it never holds or mutates `PlayerEntity`, `BallEntity`, FSM, Ledger, or snapshot-store references.

**Tech Stack:** Unity 2022.3.62f2, C#, NUnit EditMode tests, existing `FrameSyncCoordinator`, fixed-point `SimulationWorldState`, Unity `Vector3` presentation adapter.

---

## 1. Verified starting behavior

- `GameController.OnFrameUpdate` advances the coordinator, then `OnPostFrameUpdate` pushes the live Predicted entities into `PresentationFrameInterpolator`; `LateUpdate` reconciles mismatches before `SyncPresentationFromLogic` samples the interpolator.
- The mixed interpolator delays the remote player by selecting its deepest/oldest endpoints, but all four endpoints originate from Predicted state. Buffering predicted history is not the same as reading Confirmed history.
- Rollback currently replaces all four presentation endpoints from Predicted snapshots, then starts player and detached-ball correction smoothers. This makes corrected Predicted remote positions directly enter the display window.
- The smoother is bounded by configured duration, speed, and snap distance. It operates only on Unity-space targets and does not participate in deterministic state or hashes.
- `FrameSyncCoordinator` exposes value snapshots by canonical frame. Confirmed snapshots are sequential when present; the initial pre-frame-0 world is stored explicitly, and Predicted future snapshots are truncated after terminal restoration.
- `FrameSimulationSystem` performs movement, pickup, shoot, held-ball update, and ball physics in one deterministic order. P2-D must consume the resulting `Held`, `Airborne`, `Free`, `Scored`, `holderPlayerIndex`, and `hasBall` values; it must not reproduce these transitions.
- `CanonicalFrame` converts only at the Unity/network boundary. Coordinator and View World history queries use canonical frame IDs; the local player index chooses ownership, not frame-domain conversion.
- Stable highlight and postgame presentation already consume Confirmed snapshots through a separate path and remain outside the realtime View World.

## 2. Fixed-100ms conclusion

**Verified cause:** the current remote display window contains delayed Predicted samples. When late Actual input causes replay, `ReplacePresentationHistoryAfterRollback` replaces those endpoints from the corrected Predicted timeline, so a logical correction can become a Transform correction even though the remote player is buffered.

**Not yet proven:** the visible 100ms symptom may also include normal packet cadence, render sampling cadence, large correction snap thresholds, or human perception of rapid direction reversals. P2-D removes the identified source-arbitration defect, but visual acceptance still requires the dual-client matrix in section 10.

## 3. Locked data model and invariants

Create immutable presentation values:

```csharp
public enum ViewSampleSource
{
    InitialConfirmed,
    Confirmed,
    Predicted,
    ConfirmedSingleEndpoint,
    PredictedSingleEndpoint
}

public readonly struct ViewPlayerState
{
    public int PlayerIndex { get; }
    public FixedVector3 FromPosition { get; }
    public FixedVector3 ToPosition { get; }
    public int FromCanonicalFrame { get; }
    public int ToCanonicalFrame { get; }
    public ViewSampleSource Source { get; }
}

public readonly struct ViewBallState
{
    public FixedVector3 FromPosition { get; }
    public FixedVector3 ToPosition { get; }
    public int FromCanonicalFrame { get; }
    public int ToCanonicalFrame { get; }
    public BallEntity.EState State { get; }
    public int HolderPlayerIndex { get; }
    public ViewSampleSource Source { get; }
}

public readonly struct ViewWorldState
{
    public int PredictedFrame { get; }
    public int ConfirmedFrame { get; }
    public int PresentationFrame { get; }
    public ViewPlayerState Player0 { get; }
    public ViewPlayerState Player1 { get; }
    public ViewBallState Ball { get; }
}
```

Invariants:

1. All frame IDs inside View World are canonical.
2. `PresentationFrame` is the newest confirmed endpoint used by realtime remote/neutral presentation; it is not snapshot capacity, playback delay, `ConfirmedFrame`, or `PredictedFrame` by definition, though it may equal `ConfirmedFrame` when the newest endpoint is usable.
3. Local player endpoints come only from Predicted snapshots; remote endpoints come only from Confirmed or explicit initial Confirmed state.
4. Missing previous history duplicates the newest valid endpoint and records a single-endpoint source. It never splices Predicted remote state into Confirmed interpolation.
5. `ViewWorldState` owns only values and cannot write back to either logic world.
6. Basketball source arbitration consumes existing deterministic state and never advances a possession state machine.
7. Realtime View World does not replace highlight replay or terminal presentation.

## 4. Source and fallback decision table

| Object | Primary endpoints | Missing previous endpoint | Missing newest endpoint |
|---|---|---|---|
| Local player | Predicted `P-1 -> P` | duplicate `P` and mark `PredictedSingleEndpoint` | fail build; current Predicted frame publication is inconsistent |
| Remote player | Confirmed `C-1 -> C` | duplicate `C` and mark `ConfirmedSingleEndpoint` | use coordinator's current Confirmed value at `C`; if `C == -1`, duplicate initial Confirmed |
| Ball held by confirmed remote | same Confirmed endpoints as remote | duplicate newest Confirmed ball | duplicate current Confirmed/initial ball |
| Ball held by predicted local and not confirmed remote | same Predicted endpoints as local | duplicate newest Predicted ball | fail build |
| Local predicted release while Confirmed still says local-held | Predicted endpoints for immediate release | duplicate newest Predicted ball | fail build |
| Remote predicted pickup not yet Confirmed | Confirmed/initial ball | duplicate newest Confirmed ball | duplicate initial Confirmed; never attach remotely |
| Airborne/Free/Scored without local-release exception | Confirmed endpoints | duplicate newest Confirmed ball | duplicate current Confirmed/initial ball |

## 5. Basketball transition table

| Confirmed semantic | Predicted semantic | View result |
|---|---|---|
| remote Held | any | attach to remote using Confirmed player/ball frame |
| not remote Held | local Held | attach to local using Predicted player/ball frame |
| local Held | predicted Airborne/Free/Scored with no holder | detach immediately using Predicted trajectory |
| not remote Held | predicted remote Held | preserve Confirmed/initial ball semantic; do not attach early |
| remote Held | predicted remote released | remain attached until Confirmed release |
| remote Airborne/Free/Scored | any non-local-held | use Confirmed detached trajectory |

On any attachment-key change, the Unity adapter begins bounded visual correction from the last displayed ball position to the new View target. It snaps only at the existing explicit snap-distance threshold and otherwise completes no later than `_rollbackVisualMaxSmoothingSeconds`.

## 6. File map

### Create

- `Assets/Scripts/Presentation/ViewSampleSource.cs`: source/fallback identity.
- `Assets/Scripts/Presentation/ViewPlayerState.cs`: immutable player interpolation endpoints.
- `Assets/Scripts/Presentation/ViewBallState.cs`: immutable ball endpoints and attachment semantic.
- `Assets/Scripts/Presentation/ViewWorldState.cs`: one immutable realtime presentation result.
- `Assets/Scripts/Presentation/ViewWorldBuilder.cs`: pure value selection and basketball policy.
- `Assets/Tests/EditMode/ViewWorldBuilderTests.cs`: source, mirror, history, possession, hash-isolation, rollback, and terminal matrix.
- Matching Unity `.meta` files for every new asset.

### Modify

- `Assets/Scripts/FrameSync/FrameSyncCoordinator.cs`: expose the initial world through a read-only property and keep exact track/frame snapshot reads; no new mutation authority.
- `Assets/Scripts/Presentation/PresentationFrameInterpolator.cs`: accept/replace immutable `ViewWorldState`, interpolate its explicit endpoints, and retain legacy APIs only for existing regression coverage.
- `Assets/Scripts/Presentation/PresentationBallSample.cs`: carry ball state/source diagnostics required by the adapter.
- `Assets/Scripts/Presentation/PresentationTargetResolver.cs`: treat attachment-key changes as bounded ball corrections without writing logic state.
- `Assets/Scripts/GameController.cs`: build View World after each completed logic frame, rebuild it after reconciliation/terminal changes, and feed only View values into realtime presentation.
- `Assets/Tests/EditMode/PresentationFrameInterpolatorTests.cs`, `PresentationTargetResolverTests.cs`, `RemotePresentationCorrectionTests.cs`, `P2CRuntimeOwnershipTests.cs`: migrate/add integration assertions without deleting earlier behavior coverage.
- `docs/architecture/route-c-frame-sync.md`, `docs/architecture/streetball2-minimal-frame-sync-v2.md`, `Project/路线规划_街篮帧同步最小实现.md`: mark only verified P2-D behavior after the final test/build gates pass.

## 7. RED -> GREEN tasks

### Task 1: Immutable View World values and source policy

- [ ] Add failing reflection/value tests proving View types contain no `PlayerEntity`, `BallEntity`, FSM, array, list, coordinator, Ledger, or snapshot-store references.
- [ ] Add failing tests with deliberately different Confirmed/Predicted coordinates proving local selects Predicted and remote selects Confirmed for local indices 0 and 1.
- [ ] Add failing tests for exact two-endpoint interpolation metadata and single-endpoint fallbacks at initial state, frame 0, a missing previous frame, and overwritten history.
- [ ] Implement the five small View files and the pure builder; validate local index and frame ordering before publishing a result.
- [ ] Run only `ViewWorldBuilderTests` and require all tests to pass.

### Task 2: Basketball arbitration

- [ ] Add failing table-driven tests for confirmed-remote pickup, unconfirmed-remote pickup, local predicted pickup, local immediate shot release, confirmed remote release, Free, Airborne, Scored, invalid holder, and old-holder cleanup.
- [ ] Implement source selection in one `SelectBall` method. Do not add gameplay timers or transition ownership.
- [ ] Assert a remote holder is never published unless the selected Confirmed ball is `Held` by that remote index.
- [ ] Assert an attached ball's selected holder matches exactly one selected player and stale holder data is rejected or emitted detached.
- [ ] Run View World and existing possession/shot/physics tests.

### Task 3: Render sampling and bounded correction

- [ ] Add failing tests proving `PresentationFrameInterpolator` evaluates local Predicted endpoints and remote Confirmed endpoints from one View value.
- [ ] Add failing tests proving duplicate fallback endpoints freeze instead of extrapolating or mixing tracks.
- [ ] Add failing tests proving attachment, detachment, and holder transfer enter bounded correction and converge by the configured maximum duration unless the snap threshold applies.
- [ ] Add `Reset(ViewWorldState)`, `PushViewWorld(ViewWorldState)`, and `ReplaceViewWorld(ViewWorldState)` while retaining legacy public methods for existing tests.
- [ ] Update `PresentationBallSample`/resolver and run all presentation test fixtures.

### Task 4: GameController migration and rollback history invalidation

- [ ] Add failing source/ownership tests proving realtime `OnPostFrameUpdate` no longer pushes `_playerEntities`/`_ballEntity` into the interpolator.
- [ ] Add failing tests proving View build occurs after coordinator publication and after successful reconcile, before render sampling.
- [ ] Replace the Predicted-entity push with `TryBuildRealtimeViewWorld`; use current/previous Predicted and Confirmed/initial values from the coordinator.
- [ ] On rollback, atomically replace the current View result. Because remote endpoints are Confirmed, revoked Predicted remote snapshots cannot be read; local and eligible ball corrections use the existing bounded smoother.
- [ ] On terminal restore, reset realtime View state before switching to the separate postgame replay controller.
- [ ] Keep overlay diagnostics logic-only unless explicitly labeled; do not use it as a realtime Transform source.
- [ ] Run controller ownership, coordinator, rollback, highlight, terminal, and match-flow tests.

### Task 5: Full verification and truth update

- [ ] Record RED evidence for the new tests, then GREEN evidence for each focused fixture.
- [ ] Run full EditMode and require zero failures/skips; record the actual count.
- [ ] Run standalone Unity import/compile and require return code 0 and no compiler errors.
- [ ] Run `Project/NetworkServer/NetworkServerBarrierTests.ps1` and require PASS.
- [ ] Build Windows x64 and require success with zero build errors.
- [ ] Run `git diff --check` and a read-only code audit for frame-domain mixing, logic writeback, a second gameplay state machine, remote early attachment, silent fallback, stale future samples, and P2-E/P2-F scope creep.
- [ ] Update architecture/roadmap documents only with behaviors proven by the gates above.
- [ ] Do not commit, merge, reset, clean, checkout, or push.

## 8. Automated scenario matrix

| Scenario | Expected source/result |
|---|---|
| initial world, no confirmed frame | local Predicted initial; remote and neutral ball InitialConfirmed |
| Predicted ahead, Confirmed continuous | local `P-1 -> P`; remote `C-1 -> C` |
| missing Confirmed previous | remote duplicates `C`, explicit single-endpoint fallback |
| history overwritten | newest current Confirmed value duplicated; no Predicted remote splice |
| client 0/client 1 mirror | ownership sources swap by index; canonical frames remain equal |
| remote predicted pickup | ball remains on Confirmed/initial semantic |
| remote confirmed pickup | ball attaches to remote at Confirmed endpoint |
| local predicted pickup | ball attaches immediately to local Predicted endpoint |
| local predicted shot | ball detaches immediately to Predicted trajectory |
| prediction rejected | attachment/source changes once and converges within bounded presentation time |
| rollback of predicted suffix | rebuilt View never reads revoked remote Predicted history |
| terminal truncation | realtime View is reset; postgame controller alone drives replay transforms |
| View build before/after | Confirmed and Predicted world hashes/fields are byte-for-byte unchanged |

## 9. Verification commands

Use Unity `C:\Unity\unity2022\Editor\Unity.exe` and project `E:\帧同步_RouteC\Project\Frame Synchronization`.

```powershell
& 'C:\Unity\unity2022\Editor\Unity.exe' -projectPath 'E:\帧同步_RouteC\Project\Frame Synchronization' -batchmode -runTests -runSynchronously -testPlatform EditMode -testFilter 'FrameSyncDemo.Tests.ViewWorldBuilderTests' -testResults 'E:\帧同步_RouteC\TestResults-p2d-view-world.xml' -quit -logFile 'E:\帧同步_RouteC\p2d-view-world-tests.log'

& 'C:\Unity\unity2022\Editor\Unity.exe' -projectPath 'E:\帧同步_RouteC\Project\Frame Synchronization' -batchmode -runTests -runSynchronously -testPlatform EditMode -testResults 'E:\帧同步_RouteC\TestResults-p2d-final.xml' -quit -logFile 'E:\帧同步_RouteC\p2d-final-tests.log'

& 'C:\Unity\unity2022\Editor\Unity.exe' -projectPath 'E:\帧同步_RouteC\Project\Frame Synchronization' -batchmode -quit -logFile 'E:\帧同步_RouteC\p2d-compile.log'

& 'E:\帧同步_RouteC\Project\NetworkServer\NetworkServerBarrierTests.ps1'

& 'C:\Unity\unity2022\Editor\Unity.exe' -projectPath 'E:\帧同步_RouteC\Project\Frame Synchronization' -batchmode -quit -buildWindows64Player 'E:\帧同步_RouteC\Project\Frame Synchronization\Builds\RouteC-P2D-Smoke\FrameSynchronization.exe' -logFile 'E:\帧同步_RouteC\p2d-build.log'

git diff --check
git status --short --branch
```

## 10. Required manual dual-client acceptance after automation

Automation completion is not visual acceptance. Stop and ask the user to run both clients through:

1. 0ms baseline: both players move, pick up, shoot, score, and enter replay without regression.
2. Fixed 100ms: each side alternates steady movement and sharp reversals; remote Transform must not visibly jump backward because of a Predicted rollback.
3. Forced 3-5 frame mismatch: local remains responsive; remote follows Confirmed history; any local/ball correction finishes within 0.2 seconds or explicitly snaps only beyond 2 units.
4. Remote pickup: observer must not see attachment before the pickup is Confirmed; after confirmation, ball attaches once without old-holder residue.
5. Local shot: ball leaves the local hand immediately; rejected prediction converges within the bounded correction window.
6. Remote shot: ball remains attached until Confirmed release, then follows Confirmed airborne history.
7. Highlight and terminal: entering postgame uses the existing stable Confirmed replay and does not resume realtime View sampling.

## 11. Worktree protection and stop conditions

- Preserve every existing P1-G/P2-A/P2-B/P2-C uncommitted file and log.
- Never run reset, checkout rollback, clean, automatic commit, merge, or push.
- Stop and report before changing P2-C Ledger, world advancement, confirmation, replay-plan, protocol, canonical mapping, gameplay rule, or deterministic System order.
- Do not start P2-E network laboratory work or P2-F ECS/DOTS/Jobs/Burst work.
- After automated verification, stop for the manual visual matrix. Do not claim visual success without the user's observation.

## 12. Implemented outcome (2026-08-13)

- Implemented immutable `ViewWorldState`, player/ball endpoint values, explicit source/fallback identity, and pure `ViewWorldBuilder` selection.
- Realtime local player uses Predicted endpoints; remote player uses Confirmed/initial endpoints. A stalled Confirmed frame freezes at its newest endpoint instead of replaying the same interval.
- Confirmed previous-endpoint expiry duplicates the newest Confirmed endpoint and marks `ConfirmedSingleEndpoint`; a missing current endpoint rejects the View build rather than splicing Predicted remote data.
- Remote pickup/release waits for confirmation. Local pickup and explicit release use Predicted history. Invalid holder indices cannot become attachments in either View values or final presentation samples.
- Attachment, detachment, and holder changes use the existing bounded correction smoother; it completes within 0.2 seconds unless the existing 2-unit threshold explicitly snaps.
- Rollback atomically replaces the View value. Stable highlight and postgame presentation remain on their existing Confirmed-only path.
- Final evidence: View World `15/15`, presentation `97/97`, full EditMode `344/344`, standalone compile with zero compiler errors, NetworkServer barrier PASS, and Windows x64 build Success.
- Manual dual-client visual acceptance in section 10 remains pending and is intentionally not marked complete.

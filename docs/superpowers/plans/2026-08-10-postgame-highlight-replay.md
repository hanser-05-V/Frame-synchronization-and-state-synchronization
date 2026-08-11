# Postgame Highlight Replay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace unsafe local R/P recording with synchronized F9 match ending, corrected postgame scoring clips, and a context-sensitive runtime controls overlay.

**Architecture:** Stable confirmed frames are scanned after rollback. A recorder detects shot/score transitions and copies short immutable `FrameSnapshot` clips; a separate controller plays those clips directly on presentation objects after the live engine pauses. F9 uses a transient bit inside the existing input raw value, so the 8-byte network packet stays unchanged.

**Tech Stack:** Unity 2022.3, C#, NUnit EditMode tests, existing fixed-point simulation and prediction snapshots.

**Repository constraint:** Do not commit or push. The maintainer performs Git integration manually.

---

### Task 1: Synchronized end-match input

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Scripts/FrameSync/FrameInput.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/Input/LocalFrameActionBuffer.cs`
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/FrameInputTests.cs`
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/LocalFrameActionBufferTests.cs`

- [ ] **Step 1: Write failing tests**

Add assertions that bit `0x40` round-trips through `endMatchPressed`, is removed by `ToPredictionInput`, and survives the local action buffer until one consume.

- [ ] **Step 2: Run focused tests and verify RED**

Run the reflection test runner for `FrameInputTests` and `LocalFrameActionBufferTests`; expect failures because `endMatchPressed` and the fourth capture argument do not exist.

- [ ] **Step 3: Implement the input bit**

Add:

```csharp
public bool endMatchPressed
{
    get { return (_raw & 0x40) != 0; }
    set { _raw = value ? (_raw | 0x40) : (_raw & ~0x40u); }
}
```

Change the transient mask to `0x61u`, add `_endMatchPressed` to `LocalFrameActionBuffer`, merge it in `Capture`, emit it in `Consume`, and clear it in `Clear`.

- [ ] **Step 4: Run focused tests and verify GREEN**

Expected: all focused input tests pass.

### Task 2: Stable frame cursor and highlight data

**Files:**
- Create: `Project/Frame Synchronization/Assets/Scripts/Gameplay/MatchPhase.cs`
- Create: `Project/Frame Synchronization/Assets/Scripts/Gameplay/StableFrameCursor.cs`
- Create: `Project/Frame Synchronization/Assets/Scripts/Gameplay/StableRemoteFrameGate.cs`
- Create: `Project/Frame Synchronization/Assets/Scripts/Gameplay/HighlightClip.cs`
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/StableFrameCursorTests.cs`
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/StableRemoteFrameGateTests.cs`

- [ ] **Step 1: Write failing cursor tests**

Cover connected stable upper bound `min(lastExecuted, latestRemote)`, offline upper bound `lastExecuted`, monotonic `TryGetNext`, and rejection of out-of-order `MarkProcessed`.

- [ ] **Step 2: Run focused tests and verify RED**

Expected: compile failure because the cursor and phase types do not exist.

- [ ] **Step 3: Implement minimal types**

`StableFrameCursor` starts at `-1`, returns only the next consecutive frame, and exposes:

```csharp
public int CalculateStableThrough(
    int lastExecutedFrame,
    int latestRemoteFrame,
    bool isConnected);
public bool TryGetNext(int stableThroughFrame, out int frameID);
public void MarkProcessed(int frameID);
```

`HighlightClip` validates non-null frames and exposes immutable metadata plus indexed snapshot access. `MatchPhase` contains `Playing` and `PostGameReplay`.

- [ ] **Step 4: Run focused tests and verify GREEN**

Expected: all cursor tests pass.

### Task 3: Stable highlight recorder

**Files:**
- Create: `Project/Frame Synchronization/Assets/Scripts/Gameplay/HighlightReplayRecorder.cs`
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/HighlightReplayRecorderTests.cs`

- [ ] **Step 1: Write failing recorder tests**

Build sequential snapshots and actual inputs to cover:

```text
Held -> Airborne at frame 70
Airborne -> Scored at frame 100
clip finalizes at frame 145
expected range [10, 145]
```

Also cover start clamping to zero, miss rejection, maximum three clips, missing-history rejection, F9 detection from either player, duplicate F9 suppression, and F9 waiting until pending post-roll completes.

- [ ] **Step 2: Run focused tests and verify RED**

Expected: compile failure because `HighlightReplayRecorder` does not exist.

- [ ] **Step 3: Implement recorder**

Use constants `PreRollFrames = 60`, `PostRollFrames = 45`, `MaximumClipCount = 3`, and `HistoryCapacity = 512`. `ProcessStableFrame` requires consecutive frames, stores a corrected snapshot, detects transitions against the previous corrected snapshot, finalizes ready clips, and scans both inputs for `endMatchPressed`.

Expose:

```csharp
public IReadOnlyList<HighlightClip> Clips { get; }
public bool EndMatchRequested { get; }
public int EndMatchRequestFrame { get; }
public bool CanEnterPostGame { get; }
public string LastCaptureError { get; }
public void ProcessStableFrame(
    int frameID,
    FrameSnapshot snapshot,
    FrameInput[] actualInputs);
```

- [ ] **Step 4: Run recorder tests and verify GREEN**

Expected: all recorder tests pass without Unity scene dependencies.

### Task 4: Independent postgame playback

**Files:**
- Create: `Project/Frame Synchronization/Assets/Scripts/Presentation/PostGameHighlightReplayController.cs`
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/PostGameHighlightReplayControllerTests.cs`

- [ ] **Step 1: Write failing playback tests**

Cover automatic first-clip playback, 33ms advancement, pause, restart, previous/next bounds, automatic next clip, stopping at the last clip, and snapshot interpolation sampling.

- [ ] **Step 2: Run focused tests and verify RED**

Expected: compile failure because the controller does not exist.

- [ ] **Step 3: Implement playback controller**

The controller owns only clip references and cursor state. It exposes `Update(float)`, `TogglePlaying()`, `Restart()`, `Previous()`, `Next()`, and `TryGetSample(out FrameSnapshot from, out FrameSnapshot to, out float alpha)`. It never accepts or mutates live entities.

- [ ] **Step 4: Run playback tests and verify GREEN**

Expected: all playback tests pass.

### Task 5: Context-sensitive controls overlay

**Files:**
- Create: `Project/Frame Synchronization/Assets/Scripts/Presentation/RuntimeControlOverlay.cs`
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/RuntimeControlOverlayTests.cs`

- [ ] **Step 1: Write failing text tests**

Verify playing text contains `WASD`, `E`, `Space`, `F9`, and `Esc` but not legacy `C`/`F10`; replay text contains `P`, `[ / ]`, `R`, clip metadata, and empty-state messaging.

- [ ] **Step 2: Run focused tests and verify RED**

Expected: compile failure because `RuntimeControlOverlay.BuildText` does not exist.

- [ ] **Step 3: Implement overlay**

Create a standalone `MonoBehaviour` with a pure static `BuildText` method and an `OnGUI` panel in the top-left. Expose state properties set by `GameController`; do not require scene edits.

- [ ] **Step 4: Run overlay tests and verify GREEN**

Expected: all overlay tests pass.

### Task 6: GameController integration and legacy control retirement

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Scripts/GameController.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/FrameSync/FrameDebugger.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/Editor/FrameSyncWindow.cs`
- Add focused integration assertions to existing EditMode fixtures where logic can remain pure.

- [ ] **Step 1: Add failing integration assertions**

Verify the local input buffer emits F9 once and pure snapshot-to-presentation sampling does not alter source snapshots. Verify no runtime control text advertises legacy recording.

- [ ] **Step 2: Integrate stable scanning**

After remote queue drain and rollback processing in `LateUpdate`, compute the stable upper bound, reconstruct each frame's two actual inputs, retrieve the corrected `PredictionSystem` snapshot, and feed the recorder in consecutive order. Perform remote input cleanup only after scanning.

- [ ] **Step 3: Integrate phase transition**

When `CanEnterPostGame` becomes true, pause `FrameEngine`, clear transient actions, create the postgame controller from recorder clips, switch `MatchPhase`, and stop live rollback processing.

- [ ] **Step 4: Integrate presentation and controls**

In replay phase, drive player and ball GameObjects from interpolated snapshot samples using `Time.unscaledDeltaTime`. Route P, brackets, and R to the replay controller. Capture F9 through `LocalFrameActionBuffer` only during `Playing`. Update overlay properties every frame.

- [ ] **Step 5: Retire unsafe legacy controls**

Remove keyboard R/P/C calls to `FrameDebugger` and remove reflection-based engine frame resets from legacy recording methods. Change the Editor replay tab to explain that runtime F9 starts postgame highlights instead of offering unsafe mutation buttons.

- [ ] **Step 6: Compile runtime and tests**

Use the existing Unity Bee response files plus any newly created sources not yet imported. Expected compiler exit code: 0.

### Task 7: Full verification

**Files:**
- Verify all modified and created files.

- [ ] **Step 1: Run all EditMode logic tests**

Run the reflection runner with `--all`. Expected: zero failures; the existing Unity native log callback test may remain skipped.

- [ ] **Step 2: Run server protocol tests**

Run `Project/NetworkServer/NetworkServerBarrierTests.ps1`. Expected: barrier and bidirectional fragmented frame-0 forwarding pass; the network payload remains eight bytes.

- [ ] **Step 3: Run repository checks**

Run `git diff --check`, inspect `git status --short`, and review only in-scope diffs. Expected: no whitespace errors and no edits outside the Route C workspace.

- [ ] **Step 4: Hand off manual Unity verification**

Verify two clients: make at least one basket, press F9 on either client, confirm matching clip frame ranges, test P/brackets/R, confirm no position offset and no rollback logs during replay, and confirm the on-screen controls switch with phase.

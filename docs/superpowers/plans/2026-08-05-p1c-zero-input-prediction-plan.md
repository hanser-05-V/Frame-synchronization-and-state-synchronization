# P1-C Zero Input Prediction Correction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve cold-start suppression while allowing established zero-input predictions to trigger rollback when the real remote input differs.

**Architecture:** Separate prediction eligibility from the input's raw value. `PredictionEntry` captures whether a prediction was created after a real-input baseline existed; `ResolveRemote` compares only eligible predictions and establishes the baseline after processing each real input.

**Tech Stack:** Unity 2022.3.62f2, C#, Unity Test Framework/NUnit, existing `PredictionSystem`.

---

### Task 1: Reproduce the zero-input correction gap

**Files:**
- Create: `Project/Frame Synchronization/Assets/Tests/EditMode/PredictionSystemResolveRemoteTests.cs`

- [ ] Add `ResolveRemote_ZeroPredictionAfterActualBaseline_ReportsMismatch`.
- [ ] Establish the baseline with a real zero input at frame 0.
- [ ] Record a missing frame 1, which predicts zero.
- [ ] Supply a real shoot input at frame 2.
- [ ] Assert `errorFrame == 1` and `correctRaw == shootRaw`.
- [ ] Run only this fixture and require the new mismatch test to fail because `errorFrame` is null.

### Task 2: Lock the cold-start and compatibility behavior

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/PredictionSystemResolveRemoteTests.cs`

- [ ] Add a cold-start test proving predictions created before the first actual input stay ineligible.
- [ ] Add an equal-zero test proving no rollback occurs when prediction and real input match.
- [ ] Add a nonzero mismatch test proving the existing correction path still works.
- [ ] Add a multi-mismatch test proving newest-frame selection and cleanup boundaries remain unchanged.

### Task 3: Implement explicit prediction eligibility

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Scripts/FrameSync/PredictionSystem.cs`
- Test: `Project/Frame Synchronization/Assets/Tests/EditMode/PredictionSystemResolveRemoteTests.cs`

- [ ] Add `canValidate` to `PredictionEntry`.
- [ ] Add `_hasActualBaseline` and reset it in `Init`.
- [ ] When recording a predicted input, copy the current baseline state into `canValidate`.
- [ ] Replace the `_raw != 0` guard with `canValidate`.
- [ ] Establish `_hasActualBaseline` only after processing a real input, so old cold-start entries remain ineligible.
- [ ] Run the targeted fixture and require all five tests to pass.

### Task 4: Verify and review

**Files:**
- Verify only.

- [ ] Run all EditMode tests and require 23 passing tests with zero compiler errors.
- [ ] Run `git diff --check`.
- [ ] Confirm no P1-C changes under `Network/`, `MD5Checker`, `GameController`, replay, snapshot, or basketball files.
- [ ] Request independent review focused on cold-start ordering, eligible zero predictions, history cleanup, and existing nonzero behavior.
- [ ] Recheck the original `E:\帧同步` branch, HEAD, status count, and status fingerprint.
- [ ] Do not commit or push; leave Route C for manual acceptance.

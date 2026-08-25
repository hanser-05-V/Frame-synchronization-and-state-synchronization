# P2-E Confirmed Presentation Cursor Plan

> 状态修订（2026-08-14）：本计划描述的固定一帧启动缓冲已完成自动化，但固定 100ms 双端人工体验暴露了额外完整帧等待，因此不再是后续执行依据。订正设计见 [P2-E 低延迟 Confirmed 表现播放控制设计](../specs/2026-08-14-p2e-low-latency-confirmed-playback-design.md)，新 TDD 计划见 [P2-E Low-Latency Confirmed Playback Implementation Plan](2026-08-14-p2e-low-latency-confirmed-playback-plan.md)。保留本文只用于追溯旧周期跳步如何被缓解。

## Goal

Remove periodic remote-player presentation skips when multiple remote packets
are drained in one client logic tick, without changing deterministic simulation,
prediction, rollback, or the network protocol.

## Root-cause evidence

- Fixed-100ms packet arrivals are normally paced near 33ms.
- The client still occasionally drains two or three arrivals together.
- `FrameSyncCoordinator.CatchUpConfirmed` advances every newly confirmed frame
  in one call.
- The previous presentation path rebuilt only the newest confirmed interval, so
  intermediate confirmed intervals could be replaced before they were rendered.
- The observed symptom is a forward jump without a reverse correction, which is
  consistent with a skipped presentation interval rather than rollback backstep.

## Locked design

1. Keep the latest Predicted endpoints for the local player.
2. Read remote-player and confirmed-ball endpoints from an explicit presentation
   frame, not necessarily the newest Confirmed head.
3. Start playback with one confirmed frame buffered.
4. Consume confirmed presentation frames consecutively; never skip a frame when
   the Confirmed head advances by more than one.
5. Drive the Predicted and Confirmed presentation tracks with separate alpha
   values: the local track follows the frame-engine clock, while each confirmed
   interval receives its own full 33ms playback window.
6. Consume at most one confirmed presentation frame per Unity `LateUpdate`, even
   if the frame engine executes multiple logic frames during one render frame.
7. When no confirmed interval is available, hold the last confirmed endpoint.
8. If the cursor falls behind retained snapshot history, pause with an explicit
   presentation fault instead of silently freezing or skipping the expired frame.
9. Rollback may replace Predicted presentation data, but it must not rebase the
   monotonic Confirmed presentation cursor.

## File map

- Create `Assets/Scripts/Presentation/ConfirmedPresentationCursor.cs`.
- Modify `Assets/Scripts/Presentation/ViewWorldBuilder.cs` with an explicit
  presentation-frame overload while retaining the legacy newest-head overload.
- Modify `Assets/Scripts/GameController.cs` to advance and push the cursor once
  per rendered frame.
- Modify `Assets/Scripts/Presentation/PresentationFrameInterpolator.cs` with a
  dual-clock evaluation overload while preserving the legacy overload.
- Modify `Assets/Tests/EditMode/ViewWorldBuilderTests.cs` and
  `PresentationFrameInterpolatorTests.cs` with cursor continuity, render-catchup,
  dual-clock, requested-interval, and initial-to-frame-zero regression coverage.

## Verification gates

- [x] RED compile proves the cursor type and explicit builder overload are absent.
- [x] Focused cursor/builder/interpolator tests: 71/71 passed.
- [x] Full Unity EditMode: 368/368 passed, zero failures/skips.
- [x] Unity standalone import/compile: zero C# errors.
- [x] Windows x64 player build: success.
- [x] Network server lab tests: pass.
- [x] Network barrier and fragmented frame-0 forwarding: pass.
- [x] Manual fixed-100ms sustained movement: old periodic forward skip was no
  longer observed and no rollback backstep was reported.
- [ ] Overall fixed-100ms visual acceptance: failed because the fixed startup
  buffer adds about one complete logic-frame wait after Actual arrival.

## Scope exclusions

- No change to `FrameInputLedger`, deterministic worlds, snapshots, prediction,
  rollback reconciliation, packet format, scheduler decisions, or server timing.
- No claim of final visual acceptance. The superseding low-latency correction
  must be implemented and the full fixed-100ms dual-client matrix rerun.

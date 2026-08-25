# Raw UDP Minimal Demo Implementation Plan

> **For the later implementation window:** REQUIRED WORKFLOW: route through cold-stored `implement`, then use cold-stored `tdd` at the seams named below. Execute inline in the existing worktree; do not create a subagent implementation, worktree, commit, merge, reset, clean, checkout, or push unless 帅老大 separately changes that authority. Every checkbox is a future implementation action, not authorization in this planning window.

**Goal:** Deliver an independent fixed-1v1 Raw UDP frame-input demo that reuses the 28-byte Route C envelope, sends immutable sliding input windows with default `N=6`, terminates deterministically when bounded redundancy is exhausted, and preserves TCP, the existing KCP client, `FrameInputLedger`, gameplay, rollback, and presentation semantics.

**Architecture:** Add a small shared Raw UDP protocol/state layer beside the existing KCP code, then place one `Socket` and all mutable client transport state on one client worker. Add one net48/C# 7.3 server worker that owns one UDP `Socket`, two endpoint/session slots, and two independent downlink sequence domains; it validates and rebuilds datagrams but never simulates gameplay. Unity continues to consume exactly one `NetworkPacketArrival` queue through `IFrameTransportClient`, with `FrameInputLedger.RecordActual` remaining the only Actual truth.

**Tech Stack:** Unity 2022.3.62f2, C# compatible with Unity and net48/C# 7.3, NUnit EditMode, `System.Net.Sockets`, `System.Threading`, `Stopwatch`, Unity-bundled Roslyn, PowerShell reflection/live-Socket probes.

---

## 0. Baseline, authority, and completion boundary

- Work only in `E:\帧同步_RouteC` on `delivery/route-c`.
- The worktree contains extensive modified and untracked P2-A through Task 7 results. There is no Task 7 Git fixed point. Preserve every existing file and never treat `HEAD` as the implementation baseline.
- The approved design is `docs/superpowers/specs/2026-08-20-raw-udp-minimal-demo-design.md`; it is the only complete protocol source. If code inspection exposes a contradiction with that design, stop and report it instead of inventing a protocol rule.
- This plan does not resume original P2-F Task 8. Raw UDP sequence windows, gap grace, relay opportunity counts, and Raw faults must not enter KCP.
- Every production behavior follows: valid RED, focused GREEN, relevant regression GREEN. A Unity exit code without a newly written `<test-run>` XML is not evidence.
- Unity test invocations use `-runTests -runSynchronously` and do not use `-quit`. Standalone import/compile uses `-quit`.
- Do not commit or push. At each task boundary, record `git status --short` and confirm that only the approved slice plus pre-existing dirty files are present.
- Automated probes do not replace 帅老大的 final dual-client visual acceptance.
- Completion means Tasks 1 through 10 are green, the delivery handoff truthfully calls this a “Raw UDP minimal Demo,” and work stops before KCP server/resume implementation.

## 1. Locked protocol and resource values

### 1.1 Envelope and message values

The existing Route C envelope remains exactly 28 bytes:

| Offset | Size | Field | Encoding |
|---:|---:|---|---|
| 0 | 4 | Magic | ASCII `RCF2` |
| 4 | 1 | Version | `1` |
| 5 | 1 | MessageType | byte |
| 6 | 2 | PayloadLength | unsigned big-endian |
| 8 | 16 | SessionID | raw 128-bit value |
| 24 | 4 | Generation | unsigned big-endian |
| 28 | N | Payload | exact declared length |

Append without renumbering existing values:

```csharp
RawHello = 13,
RawWelcome = 14,
RawReady = 15,
RawStart = 16,
RawInput = 17,
RawFault = 18
```

`RawHello` and `MatchFull` use zero SessionID and Generation `0`. Established Raw messages use a nonzero SessionID and Generation `1`.

### 1.2 Control payloads

| Message | Exact payload |
|---|---|
| RawHello | `ClientNonce[16]` |
| RawWelcome | `EchoNonce[16] + PlayerIndex:u8 + WindowSize:u8` |
| RawReady | empty |
| RawStart | `CanonicalStartFrame:i32(be)`, value `0` only |
| RawFault | `Reason:u16(be) + FaultPlayerIndex:u8 + Reserved:u8(0) + FrameID:i32(be) + ObservedLatestFrameID:i32(be)` |

Raw fault values are fixed at `HandshakeTimeout=1`, `ConnectionTimedOut=2`, `ProtocolViolation=3`, `ConflictingInput=4`, `UnrecoverableInputGap=5`, `CapacityExceeded=6`, `PeerFault=7`, and `MatchFull=8`.

### 1.3 RawInput payload

| Payload offset | Size | Field | Rule |
|---:|---:|---|---|
| 0 | 1 | PlayerIndex | `0` or `1` |
| 1 | 1 | InputCount | `1..16` |
| 2 | 2 | Reserved | zero |
| 4 | 4 | PacketSequence | uint32 big-endian |
| 8 | 4 | LatestFrameID | nonnegative int32 big-endian |
| 12 | `InputCount*8` | entries | consecutive FrameIDs |

Each entry remains `raw:uint32(le) + frameID:int32(le)`. `InputCount` equals `min(WindowSize, LatestFrameID + 1)`. Total datagram sizes are 48 bytes for `N=1`, 88 bytes for default `N=6`, and 168 bytes for maximum `N=16`. RawInput larger than 168 bytes is invalid even though the outer Route C maximum remains 1200 bytes.

PacketSequence scope is exactly `(receiver SessionID, direction, payload PlayerIndex)`. Client-to-server envelopes carry the sender's SessionID; server-to-client envelopes carry the receiver's SessionID while payload PlayerIndex remains the original input owner. A newer downlink PacketSequence may legally carry a lower LatestFrameID for late recovery, so receivers keep a separate monotonic `HighestObservedLatestFrameID` for gap proof.

The 64-sequence window contains the current highest Sequence and the preceding 63 serial values; a nonambiguous packet 64 or more positions behind is `PacketTooOld`. The 256-frame history contains the highest retained FrameID and the preceding 255 FrameIDs; older values are `InputTooOld`, while a new LatestFrameID that would require retaining more than this inclusive span is terminal `CapacityExceeded`.

### 1.4 Timing, capacity, and ownership

| Value | Locked setting |
|---|---:|
| Default / maximum Raw window | `6 / 16` |
| Generation | `1` |
| PacketSequence reorder/dedupe window | `64` unique sequences |
| Gap reorder grace | `16` newer unique sequences |
| Input history | `256` frames |
| Local command / pre-start / remote arrival / event queue | `256` each |
| Handshake retry / timeout | `50ms / 3000ms` |
| Running valid-traffic timeout | `3000ms` |
| Idle/tail RawInput cadence | `33ms`, matching the current 30Hz logic step |
| Fault repeat | `6` sends at `50ms` spacing |
| Maximum receive work per round | `64` datagrams |
| Maximum per-player business work per server round | `64` |
| Live worker slice | approximately `2ms` |
| Maximum `Socket.Select` wait | `10ms` |
| Normal `WaitForStop` bound | `260ms` |

There is no ACK field, delivery bitmap, selective resend, reconnect, endpoint migration, Resume, congestion control, or reliable-delivery claim. Downlink send-opportunity counts are diagnostic scheduling state, not ACKs.

## 2. File map

Every new Unity `.cs` asset below receives its Unity-generated paired `.meta` file in the same task. Do not copy GUIDs from another asset.

### 2.1 Shared Unity/net48 protocol and state

Create under `Project/Frame Synchronization/Assets/Scripts/Network/`:

- `RawUdpFaultReason.cs`: locked Raw fault enum.
- `RawUdpFault.cs`: immutable decoded fault value.
- `RawUdpInputEntry.cs`: immutable `{Raw, FrameID}` value.
- `RawUdpInputWindow.cs`: immutable decoded window and defensive entry copies.
- `RawUdpProtocolCodec.cs`: Raw-only control and input payload codecs.
- `RawUdpSerialOrder.cs`: older/equal/newer/ambiguous result enum.
- `RawUdpSerialNumber.cs`: uint32 half-range comparison.
- `RawUdpSequenceDisposition.cs`: accepted/duplicate/too-old/conflict/ambiguous outcomes.
- `RawUdpPacketSequenceWindow.cs`: 64-sequence immutable-payload dedupe/reorder state.
- `RawUdpInputDisposition.cs`: accepted/idempotent/conflict/too-old/capacity/gap outcomes.
- `RawUdpInputReceiver.cs`: 256-frame immutable receive history, contiguous frontier, highest observed latest, and 16-sequence gap grace.
- `RawUdpSessionFingerprint.cs`: first eight SHA-256 bytes rendered as 16 uppercase hex characters, or `none` for the zero SessionID.
- `RawUdpClientProtocolState.cs`: pure client Hello/Welcome/Ready/Start/timeout state machine.
- `RawUdpClientDiagnosticsSnapshot.cs`: immutable client protocol/queue/budget/stop counters.
- `RawUdpInputTransport.cs`: one-worker/one-Socket `IFrameTransportClient` implementation.
- `RawUdpRelayHistory.cs`: immutable server relay history and per-frame downlink opportunity counts.
- `RawUdpDatagramFaultProfile.cs`: validated Raw-only fault settings.
- `RawUdpDatagramFaultDecision.cs`: immutable drop/delay/reorder/duplicate result.
- `RawUdpDatagramFaultModel.cs`: deterministic salted decision function over complete Raw datagram identities.
- `NetworkConnectionOptions.cs`: pure command-line/Inspector/default connection resolution.

Modify narrowly:

- `RouteCMessageType.cs`: append values 13 through 18.
- `RouteCProtocolConstants.cs`: append Raw constants without changing KCP constants.
- `RouteCProtocolCodec.cs`: accept message values through RawFault; keep KCP short-header validation scoped only to KcpData.
- `RouteCProtocolDropReason.cs`: append Raw layout/drop classifications without renumbering current values.
- `NetworkTransportEventReason.cs`: append missing Raw terminal reasons; reuse existing `ConnectionTimedOut`.

Append diagnostic drop reasons exactly after the current value 13: `RawDatagramTooLarge=14`, `InvalidRawControlPayload=15`, `InvalidRawInputPayload=16`, `InvalidRawWindowSize=17`, `WrongPlayer=18`, `WrongMessageDirection=19`, `PacketTooOld=20`, `InputTooOld=21`, `PacketSequenceConflict=22`, and `PacketSequenceAmbiguous=23`. Append transport event reasons after current value 14: `HandshakeTimeout=15`, `ProtocolViolation=16`, `ConflictingInput=17`, `UnrecoverableInputGap=18`, `CapacityExceeded=19`, `PeerFault=20`, and `MatchFull=21`. These are local diagnostic/event values, not new wire fields.

### 2.2 Unity facade and tests

Modify:

- `Project/Frame Synchronization/Assets/Scripts/Network/NetworkTransportKind.cs`
- `Project/Frame Synchronization/Assets/Scripts/Network/NetworkConfig.cs`
- `Project/Frame Synchronization/Assets/Scripts/Network/NetworkClient.cs`
- `Project/Frame Synchronization/Assets/Scripts/GameController.cs`
- `Project/Frame Synchronization/Assets/Tests/EditMode/RouteCProtocolCodecTests.cs`
- `Project/Frame Synchronization/Assets/Tests/EditMode/NetworkClientFacadeAndSessionFlowTests.cs`
- `Project/Frame Synchronization/Assets/Tests/EditMode/P2FClientTransportOwnershipTests.cs`

Create under `Project/Frame Synchronization/Assets/Tests/EditMode/`:

- `RawUdpProtocolCodecTests.cs`
- `RawUdpProtocolStateTests.cs`
- `RawUdpInputTransportTests.cs`
- `RawUdpRelayHistoryTests.cs`
- `RawUdpFacadeWiringTests.cs`
- `RawUdpDatagramFaultModelTests.cs`
- `RawUdpLedgerIntegrationTests.cs`

### 2.3 Server and probe

Create under `Project/NetworkServer/`:

- `RawUdpServerPlayerSlot.cs`: endpoint, nonce, session, readiness, sequence/input state for one player.
- `RawUdpServerMatch.cs`: pure fixed-1v1 handshake, validation, relay, terminal-fault, and fairness decisions.
- `RawUdpServerDiagnosticsSnapshot.cs`: immutable per-player and worker metrics.
- `RawUdpRelayServer.cs`: one worker owning the only server UDP Socket.
- `RawUdpServerProtocolTests.ps1`: reflection tests for server match/session/rejection behavior.
- `RawUdpRelayServerLifecycleTests.ps1`: live worker ownership, budgets, and bounded-stop tests.
- `RawUdpLiveProbe.ps1`: two real UDP clients, at least 900 inputs each, deterministic fault scenarios, and evidence output.

Modify narrowly:

- `Project/NetworkServer/ServerOptions.cs`: add `RawWindowSize` and `raw-udp` parsing.
- `Project/NetworkServer/NetworkServer.cs`: add exactly one Raw server branch while preserving TCP and KCP fail-fast.
- `Project/NetworkServer/NetworkServer.csproj`: link every shared Raw protocol/state/relay/fault-model source used by the server assembly or live probe.
- `Project/NetworkServer/ServerOptionsTests.ps1`: add raw option matrix without weakening TCP/KCP assertions.
- `Project/NetworkServer/ServerEntryOwnershipTests.ps1`: assert thin entry and one server choice.

Do not mechanically rewrite `RouteCProtocolCodec.cs`, `NetworkClient.cs`, `GameController.cs`, `NetworkServer.cs`, `ServerOptions.cs`, or the csproj. Inspect their current dirty contents immediately before each patch and preserve unrelated changes.

### 2.4 Documentation and evidence

Modify:

- `docs/architecture/route-c-frame-sync.md`
- `docs/demo/windows-build-and-demo.md`

Create:

- `docs/handoffs/2026-08-20-raw-udp-minimal-demo-delivery.md`
- `docs/handoffs/2026-08-20-p2f-task8-resume-next-window-prompt.md`

Evidence names are locked in Section 14; evidence files are generated outputs, not production sources.

## 3. Test command contract

Use these fixed roots in every implementation task:

```powershell
$unity = 'C:\Unity\unity2022\Editor\Unity.exe'
$project = 'E:\帧同步_RouteC\Project\Frame Synchronization'
$evidence = 'E:\帧同步_RouteC\Project\Frame Synchronization'
```

A focused Unity command for the first protocol slice is:

```powershell
& $unity -projectPath $project -batchmode -runTests -runSynchronously `
  -testPlatform EditMode `
  -testFilter 'FrameSyncDemo.Tests.RawUdpProtocolCodecTests;FrameSyncDemo.Tests.RouteCProtocolCodecTests' `
  -testResults (Join-Path $evidence 'TestResults-raw-udp-task1-codec-red.xml') `
  -logFile (Join-Path $evidence 'raw-udp-task1-codec-red.log')
```

For later task filters, qualify every listed fixture with the `FrameSyncDemo.Tests` namespace, as shown for `FrameSyncDemo.Tests.RawUdpProtocolCodecTests`, and use the exact evidence name stated in that step; do not reuse an earlier XML.

After every run, parse the newly written XML and require `failed="0"` and `skipped="0"` for GREEN. RED must have a newly written XML and the named assertion failure; a compile-only import or stale XML is invalid.

Server scripts always receive the current task's freshly compiled servercheck path:

```powershell
& 'E:\帧同步_RouteC\Project\NetworkServer\RawUdpServerProtocolTests.ps1' `
  -ServerPath 'E:\帧同步_RouteC\Project\NetworkServer\obj\raw-udp-current-servercheck.exe'
```

## 4. Task 1 — Raw message values, constants, codec, and rejection matrix

**Files:** protocol files in Section 2.1; `RouteCProtocolCodecTests.cs`; new `RawUdpProtocolCodecTests.cs`.

**Public surface to lock:**

```csharp
public static class RawUdpProtocolCodec
{
    public static byte[] EncodeHello(byte[] clientNonce);
    public static bool TryDecodeHello(byte[] payload, out byte[] clientNonce);
    public static byte[] EncodeWelcome(byte[] echoNonce, byte playerIndex, byte windowSize);
    public static bool TryDecodeWelcome(byte[] payload, out byte[] echoNonce, out byte playerIndex, out byte windowSize);
    public static byte[] EncodeReady();
    public static bool TryDecodeReady(byte[] payload);
    public static byte[] EncodeStart(int canonicalStartFrame);
    public static bool TryDecodeStart(byte[] payload, out int canonicalStartFrame);
    public static byte[] EncodeInput(byte playerIndex, byte negotiatedWindowSize, uint packetSequence, int latestFrameID, RawUdpInputEntry[] entries);
    public static bool TryDecodeInput(byte[] payload, byte negotiatedWindowSize, out RawUdpInputWindow window, out RouteCProtocolDropReason reason);
    public static byte[] EncodeFault(in RawUdpFault fault);
    public static bool TryDecodeFault(byte[] payload, out RawUdpFault fault);
}
```

- [ ] **Step 1: Write envelope RED without referencing missing members.** Use `Enum.Parse(typeof(NetworkTransportKind), "RawUdp")` to assert the future value is 2, and cast bytes 13..18 to `RouteCMessageType` rather than naming absent enum members. Add `Envelope_RawMessageBytes13Through18_AreKnownAndByte19IsRejected` by patching byte 5 of an otherwise valid 28-byte envelope. Expected RED: `Enum.Parse` throws and byte 13 returns `UnknownMessageType`; the fixture still compiles.
- [ ] **Step 2: Run envelope RED.** Filter `FrameSyncDemo.Tests.RouteCProtocolCodecTests`; write `TestResults-raw-udp-task1-envelope-red.xml`. Stop if the failure is unrelated to the locked enum/message range.
- [ ] **Step 3: Add the compiling Raw value surface.** Append enum/constants/drop reasons and create immutable value types plus Raw codec signatures. Decoder outputs must be reset on failure; constructors clone arrays; returned arrays are copies.
- [ ] **Step 4: Write behavioral codec RED.** Add these exact tests:
  - `RawControlPayloads_UseExactLengthsAndBigEndianFields`
  - `RawInput_N1N6N16_UsesExactMixedEndianBytesAnd48_88_168ByteDatagrams`
  - `RawInput_EarlyFramesUseMinWindowAndEstablishedFramesCannotShortenIt`
  - `RawInput_ReturnedEntriesRemainImmutableAfterSourceAndResultMutation`
  - `RawInput_RejectsCountReservedLengthLatestAndNonContiguousFrames`
  - `RawControl_RejectsWrongLengthsPlayerWindowStartReasonAndReserved`
  - `OuterSessionGenerationAndMessageDirection_RejectionMatrixIsExplicit`
  - `RawInput_Above168Bytes_IsRawPayloadTooLargeEvenBelowOuter1200Limit`

  The N=6 expected payload starts `00 06 00 00`, contains PacketSequence and LatestFrameID in big-endian, then six exact existing 8-byte little-endian business inputs.
- [ ] **Step 5: Run behavioral RED.** Filter `RawUdpProtocolCodecTests;RouteCProtocolCodecTests`; write `TestResults-raw-udp-task1-codec-red.xml`. Expected failures must name incorrect bytes, lengths, or rejection reasons.
- [ ] **Step 6: Implement the minimum codec.** Keep outer envelope logic in `RouteCProtocolCodec`; put all Raw payload validation in `RawUdpProtocolCodec`. Do not reinterpret existing KCP Hello/Welcome/Ready/Start payloads as Raw payloads.
- [ ] **Step 7: Run focused GREEN.** Same filter; write `TestResults-raw-udp-task1-codec-green.xml`; require 0 failed and 0 skipped.
- [ ] **Step 8: Run relevant regression GREEN.** Filter `RouteCProtocolCodecTests;RouteCControlPayloadCodecTests;KcpUdpClientStateMachineTests`; write `TestResults-raw-udp-task1-regression.xml`.

**Stop condition:** stop on any need to renumber values 1..12, change the 28-byte envelope, change the existing 8-byte business input, or accept a Raw layout not present in the approved design.

## 5. Task 2 — Pure client handshake, serial arithmetic, and input-window state

**Files:** new `RawUdpSerialOrder.cs`, `RawUdpSerialNumber.cs`, `RawUdpSequenceDisposition.cs`, `RawUdpPacketSequenceWindow.cs`, `RawUdpInputDisposition.cs`, `RawUdpInputReceiver.cs`, `RawUdpSessionFingerprint.cs`, `RawUdpClientProtocolState.cs`; new `RawUdpProtocolStateTests.cs`.

**State contracts:**

```csharp
public enum RawUdpSerialOrder : byte { Older, Equal, Newer, Ambiguous }
public static class RawUdpSerialNumber
{
    public static RawUdpSerialOrder Compare(uint left, uint right);
}
public sealed class RawUdpPacketSequenceWindow
{
    public RawUdpSequenceDisposition Observe(uint sequence, byte[] immutablePayload);
}
public sealed class RawUdpInputReceiver
{
    public RawUdpInputReceiver(int windowSize, bool allowNewerSequenceLatestRegression);
    public RawUdpInputDisposition Accept(
        in RawUdpInputWindow window,
        out RawUdpInputEntry[] firstAcceptedEntries,
        out RawUdpFault terminalFault);
}
```

`RawUdpInputReceiver` owns 256 immutable frames, the highest observed LatestFrameID, the last contiguous frame, one pending oldest gap, and the count of newer unique sequences observed after the redundancy window crossed that gap. Lower LatestFrameID on a newer downlink sequence is legal and must not lower the highest-observed value.

- [ ] **Step 1: Write serial/sequence RED.** Add exact tests `SerialCompare_WrapAndHalfRange_ReturnLockedOrders`, `SequenceWindow_Lag63IsInsideAndLag64IsPacketTooOld`, `SequenceWindow_SameSequenceSameBytesIsDuplicateDifferentBytesIsConflict`, and `SequenceWindow_HalfRangeJumpIsAmbiguous`.
- [ ] **Step 2: Run RED.** Filter `RawUdpProtocolStateTests`; write `TestResults-raw-udp-task2-sequence-red.xml`. Missing surface is allowed for the first compile attempt; after adding signatures, rerun until the RED is an assertion failure on behavior.
- [ ] **Step 3: Implement serial and 64-entry sequence state.** Store defensive payload copies rather than a collision-prone content hash. Use `left != right && unchecked(left-right) < 0x80000000u` only for ordered differences; exactly `0x80000000u` is ambiguous.
- [ ] **Step 4: Write input/history/gap RED.** Add exact tests:
  - `InputReceiver_SameFrameSameRawIsIdempotentDifferentRawIsTerminalConflict`
  - `InputReceiver_OutOfOrderFirstValuesPublishOnceAndAdvanceContiguousFrontier`
  - `InputReceiver_DownlinkLowerLatestOnNewSequencePreservesHighestObservedLatest`
  - `InputReceiver_UpstreamNewerSequenceCannotLowerLatestButOlderReorderedSequenceMay`
  - `InputReceiver_GapRecoveredWithin16UniqueSequencesClearsPendingFault`
  - `InputReceiver_GapAfter16NewerUniqueSequencesFaultsSameFrame`
  - `InputReceiver_DuplicateSequenceDoesNotConsumeGapGrace`
  - `InputReceiver_256HistoryAndFutureSpanReturnTooOldOrCapacityExceeded`
- [ ] **Step 5: Write client handshake RED.** Add exact tests:
  - `ClientProtocol_HelloRetriesEvery50msAndHandshakeFaultsAt3000ms`
  - `ClientProtocol_MatchingWelcomePublishesReadyEvery50msUntilStart`
  - `ClientProtocol_DuplicateWelcomeMustBeByteIdenticalOrProtocolViolation`
  - `ClientProtocol_InvalidWelcomeWindowFromConfiguredServerIsProtocolViolation`
  - `ClientProtocol_StartFrame0TransitionsExactlyOnceAndOtherStartsFault`
  - `ClientProtocol_PreStartInputsBufferTo256AndPublishInFrameOrderAfterStart`
  - `ClientProtocol_RejectsWrongSessionGenerationPlayerAndDirection`
  - `ClientProtocol_RunningSilenceFaultsAt3000msWithoutReconnectOrResume`
  - `SessionFingerprint_UsesFirst8Sha256BytesWithoutExposingSessionBytes`
- [ ] **Step 6: Run combined RED.** Filter `RawUdpProtocolStateTests`; write `TestResults-raw-udp-task2-state-red.xml`.
- [ ] **Step 7: Implement the pure state machines.** Construct the server's upstream receiver with Latest regression disabled and the client's downlink receiver with it enabled; in both cases an older in-window PacketSequence may carry its historically older LatestFrameID. Expose actions as immutable send/event/arrival values. Do not reference `Socket`, `Thread`, `UnityEngine`, wall-clock time, or `FrameInputLedger`.
- [ ] **Step 8: Run focused GREEN.** Write `TestResults-raw-udp-task2-state-green.xml`; require 0 failed and 0 skipped.
- [ ] **Step 9: Run relevant regression GREEN.** Filter `RawUdpProtocolCodecTests;RouteCProtocolCodecTests;OutboundActualHistoryTests;KcpUdpClientStateMachineTests`; write `TestResults-raw-udp-task2-regression.xml`.

**Stop condition:** stop if half-range ordering, 16-sequence grace, 256-frame history, pre-start publication timing, or terminal reason cannot be represented without changing the approved design.

## 6. Task 3 — Client one-worker Socket ownership, bounded queues, diagnostics, and stop

**Files:** new `RawUdpClientDiagnosticsSnapshot.cs`, `RawUdpInputTransport.cs`, `RawUdpInputTransportTests.cs`; modify `P2FClientTransportOwnershipTests.cs` only to add Raw assertions.

**Worker boundary:** `RawUdpInputTransport` implements the existing `IFrameTransportClient` unchanged. Its injectable constructor accepts `IMonotonicClock` and `Func<byte[]> clientNonceFactory`; the live factory uses `StopwatchMonotonicClock` and cryptographic nonce generation. Window size is learned only from a valid RawWelcome and is then locked for the session. Public `SubmitResumeReadiness` is an intentional no-op and Raw never publishes `ResumeRequired`.

Worker round order is locked:

```text
check stop -> drain bounded commands -> send due control/input/tail work
-> Socket.Select(max 10ms or earlier deadline) -> receive at most 64 datagrams
-> decode/validate/dedupe -> atomically publish status/diagnostics/events/arrivals
```

- [ ] **Step 1: Write lifecycle/ownership RED.** Add exact tests:
  - `RawTransport_OwnsExactlyOneSocketAndOneRemoteArrivalQueue`
  - `RawTransport_MainThreadRequestStopDoesNotCloseWorkerSocket`
  - `RawTransport_StopBeforeStartHandshakeRunningFaultAndRepeatedStopFinishWithin260ms`
  - `RawTransport_UsesSelectTenMillisecondMaximumWithoutSleepOrUnityTiming`
  - `RawTransport_RoundReceivesAtMost64DatagramsAndReportsBudgetExhaustion`
- [ ] **Step 2: Run RED.** Filter `RawUdpInputTransportTests;P2FClientTransportOwnershipTests`; write `TestResults-raw-udp-task3-worker-red.xml`.
- [ ] **Step 3: Implement worker shell and bounded queues.** Use one local-command queue, one remote-arrival queue, and one event queue, each guarded by an atomic count capped at 256. Reserve the final event slot for one terminal event, so ordinary events can occupy at most 255 slots; a local-command overflow sets an atomic overflow request that the worker converts to `CapacityExceeded` without enqueueing another command. Worker-only send buffers and pre-start buffers are not exposed as second arrival truths. Only the worker creates, connects, receives from, sends through, and disposes the Socket.
- [ ] **Step 4: Write handshake/live-loop RED.** Add exact tests:
  - `RawTransport_StartReturnsImmediatelyAndSendsRawHello`
  - `RawTransport_FirstWelcomeReadyStartBeginsOnceAndPublishesRunningBeforeSessionStarted`
  - `RawTransport_LocalInputBeforeSessionStartIsRejectedWithoutQueueMutation`
  - `RawTransport_NewLocalFramesBuildNWindowsAndIdleTailUsesNewSequences`
  - `RawTransport_UpstreamPacketSequenceStartsAt0IncrementsPerDatagramAndWraps`
  - `RawTransport_IdleAndTailWindowsUse33msLogicCadenceAndProvideAtLeastNOpportunities`
  - `RawTransport_LocalFramesStartAt0RemainContiguousAndKeep256ImmutableHistory`
  - `RawTransport_LocalGapRollbackOrConflictPublishesOutboundHistoryFaultWithoutOverwrite`
  - `RawTransport_RemoteActualPublishesOnceWithMonotonicReceiveSequence`
  - `RawTransport_QueueCapacityFaultsWithoutOverwritingAcceptedValues`
  - `RawTransport_DownlinkGapSendsRawFaultAndPublishesExactTerminalReason`
- [ ] **Step 5: Run behavioral RED.** Same filter; write `TestResults-raw-udp-task3-live-red.xml`.
- [ ] **Step 6: Implement send/receive behavior and diagnostics.** The immutable snapshot includes state/player/window/session fingerprint, sequence high-water values, datagrams/bytes, classified drops, local/highest/contiguous frames, pending gap/grace, direct/redundancy/reorder recovery, queue counts/high-water marks, worker/select/budget metrics, and stop result. Compute the public session fingerprint with `RawUdpSessionFingerprint` as the first eight SHA-256 bytes rendered into 16 uppercase hexadecimal characters; never log the full SessionID, nonce, endpoint, datagram, or per-frame raw.
- [ ] **Step 7: Run focused GREEN.** Write `TestResults-raw-udp-task3-worker-green.xml`.
- [ ] **Step 8: Run relevant regression GREEN.** Filter `RawUdpProtocolStateTests;KcpUdpClientWorkerTests;TcpClientTransportTests;P2FClientTransportOwnershipTests`; write `TestResults-raw-udp-task3-regression.xml`.

**Stop condition:** stop if shutdown requires the main thread to close the Socket, if a fourth cross-thread mutable queue is introduced, or if Raw transport needs a second mutable Actual store.

## 7. Task 4 — Server options and one-worker fixed-1v1 slots

**Files:** create `RawUdpServerPlayerSlot.cs`, `RawUdpServerMatch.cs`, `RawUdpServerDiagnosticsSnapshot.cs`, `RawUdpRelayServer.cs`, `RawUdpServerProtocolTests.ps1`, `RawUdpRelayServerLifecycleTests.ps1`; modify `ServerOptions.cs`, `NetworkServer.cs`, `NetworkServer.csproj`, `ServerOptionsTests.ps1`, `ServerEntryOwnershipTests.ps1`.

**Option contract:**

```text
NetworkServer.exe --transport raw-udp --raw-window 6
```

`--raw-window` defaults to 6, accepts only 1..16, and is rejected unless the final selected transport is RawUdp. No arguments still enter legacy interactive TCP. Explicit arguments without `--transport` still select KCP and retain Task 7 fail-fast. TCP Lab/Barrier/framing/fault labels remain unchanged.

`RawUdpRelayServer` exposes `Run()`, `RequestStop()`, `WaitForStop(int millisecondsTimeout)`, `Dispose()`, and an atomically replaced immutable `Diagnostics` snapshot. `RequestStop` only sets a request; `Dispose` follows RequestStop plus the 260ms wait and never closes the worker-owned Socket from the caller thread.

- [ ] **Step 1: Add server option RED.** Extend `ServerOptionsTests.ps1` for `raw-udp`, default/1/6/16 windows, rejection of 0/17/noninteger, rejection on TCP/KCP, legacy TCP preservation, explicit-default KCP preservation, and unchanged KCP fail-fast.
- [ ] **Step 2: Compile and run option RED.** Build `obj/raw-udp-task4-red-servercheck.exe` with the Section 14.4 Roslyn command, then run `& 'E:\帧同步_RouteC\Project\NetworkServer\ServerOptionsTests.ps1' -ServerPath 'E:\帧同步_RouteC\Project\NetworkServer\obj\raw-udp-task4-red-servercheck.exe'`. Expected RED is missing Raw enum/option behavior, not a script parse failure.
- [ ] **Step 3: Implement option and thin-entry branch.** `Program` selects exactly one of `TcpRelayServer`, `RawUdpRelayServer`, or the preserved KCP fail-fast. It must not own a Socket, player slot, relay history, or worker loop.
- [ ] **Step 4: Add pure server match RED.** `RawUdpServerProtocolTests.ps1` loads the current assembly and tests:
  - first two unique Endpoint+Nonce pairs allocate stable players 0 then 1 with nonzero distinct sessions;
  - injected zero or colliding SessionIDs are retried until a nonzero distinct value is produced;
  - duplicate Endpoint+Nonce resends byte-identical Welcome;
  - same endpoint/new nonce is terminal ProtocolViolation;
  - same nonce/new endpoint and unknown session are silent drops;
  - a valid third endpoint receives one MatchFull datagram per received Hello without session allocation or retry state;
  - Ready/Start duplicate/reorder is idempotent and Start frame is zero;
  - pre-Start RawInput from a bound client is terminal;
  - session/generation/endpoint/player/direction rejection follows the locked matrix;
  - bad magic/version/message/outer length is always a classified silent drop, even from a bound endpoint;
  - a datagram above 1200 bytes or RawInput above 168 bytes is a silent drop from an unbound endpoint and ProtocolViolation from a bound endpoint;
  - PacketTooOld and InputTooOld are nonterminal drops, while sequence-content conflict/half-range ambiguity, invalid control/input layout, conflicting frame raw, capacity overflow, and exhausted gap grace preserve their exact terminal classifications;
  - both players Ready enables relay and Start retries stop only after that player's valid frame-0 window.
- [ ] **Step 5: Run server match RED.** Write transcript `raw-udp-task4-server-protocol-red.log` and require a named assertion failure.
- [ ] **Step 6: Implement slots and match state.** Inject `Func<RouteCSessionId>` for tests; live server uses `RandomNumberGenerator.Create()` and retries zero/collision. Endpoint comparison happens before any terminal classification so unbound traffic cannot kill the match.
- [ ] **Step 7: Add live worker RED.** Lifecycle script covers one Socket, one worker, stable player order 0 then 1, at most 64 received datagrams/round, at most 64 accepted-input-or-rebuilt-datagram work items/player/round, approximately 2ms fairness recheck, Select at most 10ms, and 260ms bounded idempotent stop while unstarted/waiting/starting/running/terminated. The worker owns one inbound work queue per player, each capped at 256 immutable decoded datagrams. Each first-accepted input entry and each generated outbound RawInput consumes one player work item; when the limit or time slice is reached, leave the remaining decoded work in that player's bounded queue for the next round. Queue overflow attributable to a bound player is terminal `CapacityExceeded`.
- [ ] **Step 8: Implement worker and run focused GREEN.** Compile `obj/raw-udp-task4-green-servercheck.exe`; run options, protocol, lifecycle, and entry ownership scripts. Save `raw-udp-task4-server-green.log`.
- [ ] **Step 9: Run TCP regression GREEN.** Run `NetworkServerLabTests.ps1` and `NetworkServerBarrierTests.ps1` against the same task4 servercheck; require existing PASS text and exact 8-byte TCP relay behavior.

**Stop condition:** stop if server work requires a second Socket, a worker per player, server gameplay/world state, endpoint migration, reconnect, or any change to legacy interactive TCP/KCP fail-fast semantics.

## 8. Task 5 — Immutable relay history, normal relay, late recovery, and tail behavior

**Files:** new `RawUdpRelayHistory.cs`, `RawUdpRelayHistoryTests.cs`; modify only the Raw server files and csproj links created in Task 4.

**Relay rule:** every accepted new upstream PacketSequence is one upstream send opportunity. A duplicate Sequence with identical bytes is ignored. A new Sequence containing an already-known window is a real tail/redundancy opportunity and is rebuilt once for the peer with the peer SessionID and a fresh downlink PacketSequence.

Late recovery uses this bounded algorithm:

```csharp
int relayCopies = 1;
foreach (RawUdpInputEntry lateRecovered in newlyLateRecoveredEntries)
{
    int needed = windowSize - history.GetDownlinkOpportunityCount(lateRecovered.FrameID);
    relayCopies = Math.Max(relayCopies, needed);
}
relayCopies = Math.Min(windowSize, relayCopies);
for (int copy = 0; copy < relayCopies; copy++)
{
    SendRebuiltWindowWithFreshDownlinkSequence();
    history.RecordDownlinkOpportunityForEveryEntryInWindow();
}
LateRecoveryRelayCopies += relayCopies - 1;
```

`newlyLateRecoveredEntries` contains only inputs first accepted after `HighestObservedLatestFrameID` had already proved their normal N-window opportunity range was crossed; a normal frontier advance is not late recovery. The count means “datagrams generated containing this frame,” never delivery or acknowledgement.

- [ ] **Step 1: Write relay history RED.** Add exact tests:
  - `RelayHistory_FirstValueIsImmutableAndConflictPreservesFirstRaw`
  - `RelayHistory_CleanSteadyStateCreatesOneDownlinkPerNewUpstreamSequence`
  - `RelayHistory_NormalAdvanceGivesEachFrameNWindowsBeforeItLeavesTheSlidingWindow`
  - `RelayHistory_LateFWindowAfterFPlus1ThroughFPlus6GivesFExactlyNBoundedOpportunities`
  - `RelayHistory_OneUpstreamWindowNeverCreatesMoreThanNDownlinkDatagrams`
  - `RelayHistory_NewSequenceSameWindowRelaysTailButDuplicateSequenceDoesNot`
  - `RelayHistory_DownlinkSequencesAreIndependentPerReceiverAndWrapSafely`
  - `RelayHistory_256CapacityReturnsTooOldOrCapacityExceededWithoutOverwrite`
- [ ] **Step 2: Run RED.** Filter `RawUdpRelayHistoryTests`; write `TestResults-raw-udp-task5-relay-red.xml`.
- [ ] **Step 3: Implement immutable history/opportunity state.** Preserve the first raw per player/frame. Store opportunity counts separately from input identity. Rebuild payloads from server history; never forward client datagram bytes or client PacketSequence.
- [ ] **Step 4: Add server relay RED.** Extend `RawUdpServerProtocolTests.ps1` with byte-level assertions for receiver SessionID, Generation 1, original payload PlayerIndex, receiver-specific PacketSequence, normal relay, late-recovery N copies, tail copies, conflict broadcast, gap-grace broadcast, original terminal reason/player/frame preserved to both players, and repeated identical RawFault remaining idempotent without reason downgrade.
- [ ] **Step 5: Run server RED.** Compile `obj/raw-udp-task5-red-servercheck.exe`; run the protocol script and capture `raw-udp-task5-server-relay-red.log`.
- [ ] **Step 6: Wire relay behavior and fault repeats.** Server sends terminal RawFault at most six times at 50ms spacing and then exits its match worker within the 260ms normal stop bound. `MatchFull` remains a handshake rejection, not an active-match terminal transition.
- [ ] **Step 7: Run focused GREEN.** Run `RawUdpRelayHistoryTests` to `TestResults-raw-udp-task5-relay-green.xml`; compile servercheck and run Raw server protocol/lifecycle scripts to `raw-udp-task5-server-green.log`.
- [ ] **Step 8: Run relevant regression GREEN.** Filter `RawUdpProtocolStateTests;RawUdpInputTransportTests;OutboundActualHistoryTests`; write `TestResults-raw-udp-task5-regression.xml`, then rerun TCP Lab/Barrier on the same servercheck.

**Stop condition:** stop if late recovery needs ACK state, selective resend, more than N outputs for one upstream datagram, mutable replacement of an accepted raw, or direct forwarding of the sender datagram.

## 9. Task 6 — Transport facade, Inspector/CLI configuration, and GameController wiring

**Files:** modify `NetworkTransportKind.cs`, `NetworkConfig.cs`, `NetworkClient.cs`, `NetworkTransportEventReason.cs`, `GameController.cs`, `NetworkClientFacadeAndSessionFlowTests.cs`, `P2FClientTransportOwnershipTests.cs`; create `RawUdpFacadeWiringTests.cs`.

**Configuration contract:** append `RawUdp=2`. Add `NetworkClient.ConnectConfigured()` with per-field priority command line, then serialized Inspector value, then `NetworkConfig` default. Accepted CLI forms include `--transport raw-udp --server-ip 127.0.0.1 --server-port 8888`; transport also accepts `tcp` and `kcp`, server IP accepts a literal or hostname, and port accepts 1..65535. Preserve `Connect(ip, port)` for tests and existing callers.

- [ ] **Step 1: Write facade/CLI RED.** Add exact tests:
  - `TransportEnum_PreservesKcp0Tcp1AndAppendsRawUdp2`
  - `ConnectConfigured_CommandLineOverridesInspectorAndDefaultsPerField`
  - `ConnectConfigured_InspectorOverridesNetworkConfigDefaults`
  - `ConnectConfigured_InvalidTransportHostOrPortFailsBeforeCreatingTransport`
  - `Factory_RawUdpCreatesOnlyRawTransportAndLeavesInterfaceUnchanged`
  - `RawTransport_SubmitResumeReadinessIsNoOpAndNeverPublishesResumeRequired`
- [ ] **Step 2: Run RED.** Filter `RawUdpFacadeWiringTests;NetworkClientFacadeAndSessionFlowTests`; write `TestResults-raw-udp-task6-facade-red.xml`.
- [ ] **Step 3: Implement minimal configuration/wiring.** Put parsing and precedence in `NetworkConnectionOptions.Resolve(string[] arguments, NetworkTransportKind inspectorTransport, string inspectorHost, int inspectorPort)`. Command-line values override supplied Inspector values; blank Inspector host and port `0` fall back to `NetworkConfig.DEFAULT_IP/DEFAULT_PORT`; serialized defaults are initialized from the same NetworkConfig values. `NetworkClient` still owns exactly one `IFrameTransportClient`; it must not own Socket, endpoint, UDP bytes, packet windows, or an arrival queue.
- [ ] **Step 4: Write GameController RED.** Assert its network initialization calls `ConnectConfigured()` exactly once, contains no RawUdp gameplay branch, starts FrameEngine only from the existing SessionStarted+Running path, and maps new terminal event reasons through the existing match-terminal fault path.
- [ ] **Step 5: Run GameController RED.** Filter `RawUdpFacadeWiringTests;NetworkClientFacadeAndSessionFlowTests;P2FClientTransportOwnershipTests`; write `TestResults-raw-udp-task6-gamecontroller-red.xml`.
- [ ] **Step 6: Replace only the fixed endpoint call.** Change `_networkClient.Connect(NetworkConfig.DEFAULT_IP, NetworkConfig.DEFAULT_PORT)` to `_networkClient.ConnectConfigured()`. Do not change Ledger, Canonical Frame, input mapping, prediction, rollback, presentation, or terminal pausing logic.
- [ ] **Step 7: Run focused GREEN.** Same filter; write `TestResults-raw-udp-task6-wiring-green.xml`.
- [ ] **Step 8: Run relevant regression GREEN.** Filter `KcpUdpClientWorkerTests;TcpClientTransportTests;NetworkLedgerWiringTests;FrameSyncCoordinatorTests;RuntimeNetworkDiagnosticsTests`; write `TestResults-raw-udp-task6-regression.xml`.

**Stop condition:** stop if Raw selection requires changing `IFrameTransportClient`, adding a gameplay branch, duplicating the remote arrival queue, or changing existing enum numeric values.

## 10. Task 7 — Deterministic Raw UDP datagram fault model

**Files:** create `RawUdpDatagramFaultProfile.cs`, `RawUdpDatagramFaultDecision.cs`, `RawUdpDatagramFaultModel.cs`, `RawUdpDatagramFaultModelTests.cs`, `RawUdpLedgerIntegrationTests.cs`.

**Decision identity:** direction, fixed session fingerprint, message type, copy index, and either RawInput PacketSequence or the virtual link's per-direction/per-message control send ordinal. Use separate stable salts for delay, jitter, drop, reorder, and duplicate. No wall clock, `System.Random`, or thread arrival order may affect a decision.

Canonical decision labels are exactly `raw-udp-datagram-delay`, `raw-udp-datagram-jitter`, `raw-udp-datagram-drop`, `raw-udp-datagram-reorder`, and `raw-udp-datagram-duplicate`. They must not reuse TCP `recovered-loss`/HOL/application-fault or KCP metric names.

Use seed `20260820` for the canonical deterministic trace and `20260821` for the selected divergence comparison. Scripted aligned-drop scenarios identify exact direction/message/PacketSequence opportunities and do not rely on percentage rolls.

- [ ] **Step 1: Write model RED.** Add exact tests:
  - `SameSeedScriptIdentity_ProducesByteIdenticalDecisionTrace`
  - `SelectedDifferentSeeds_DivergeAcrossSufficientSamples`
  - `ZeroPercentAndBoundaryValues_ProduceLockedResults`
  - `DropDeletesDatagramWithoutSyntheticRecovery`
  - `DuplicateProducesAtMostOneAdditionalCopy`
  - `ControlOrdinalAndRawPacketSequenceUseSeparateStableIdentities`
  - `ProfileRejectsPercentOutside0To100AndTimeOutside0To5000ms`
  - `TraceLabelsUseRawUdpDatagramPrefixAndExcludeTcpKcpRecoveryLabels`
- [ ] **Step 2: Run RED.** Filter `RawUdpDatagramFaultModelTests`; write `TestResults-raw-udp-task7-fault-red.xml`.
- [ ] **Step 3: Implement pure model.** Hash fixed-width identity bytes with explicit unchecked integer mixing and one distinct constant per decision axis. Decision output contains drop, effective due time offset, reorder extra, and copy count `1` or `2`; a dropped datagram has copy count `0`.
- [ ] **Step 4: Write deterministic integration RED.** `RawUdpLedgerIntegrationTests` creates two pure Raw client states, fixed nonces/sessions/lane identities, a virtual monotonic link, and real `FrameInputLedger`/`FrameSyncCoordinator` consumers. Add exact tests:
  - `N6_DropOneThroughFiveConsecutiveDatagrams_PublishesEachRemoteActualOnceAndConverges`
  - `N6_AlignedSixDropBurst_TerminatesBothWithSameGapFrameAndReason`
  - `DelayJitterReorderDuplicate_SameTraceConvergesWithoutClaimingNoPredictionFork`
  - `WrongSessionPlayerAndLaneInjection_CannotTerminateLegalMatch`
  - `SameFrameDifferentRawFromBoundLane_TerminatesBoth`
- [ ] **Step 5: Run integration RED.** Filter `RawUdpLedgerIntegrationTests`; write `TestResults-raw-udp-task7-ledger-red.xml`.
- [ ] **Step 6: Implement virtual-link test support inside the fixture.** It must pass complete encoded datagrams through the model, inject fixed identities, and deliver by virtual due time plus stable ordinal. It must not enter production gameplay, Ledger, prediction, rollback, or presentation code.
- [ ] **Step 7: Run focused GREEN.** Filter both new fixtures; write `TestResults-raw-udp-task7-fault-green.xml`.
- [ ] **Step 8: Run relevant regression GREEN.** Filter `DeterministicNetworkFaultModelTests;NetworkFaultTraceReplayTests;FrameInputLedgerTests;FrameSyncCoordinatorTests`; write `TestResults-raw-udp-task7-regression.xml`.

**Stop condition:** stop if a Raw fault decision depends on live arrival order, random process state, full secret values in logs, TCP recovered-loss/HOL labels, or synthetic recovery of a dropped datagram.

## 11. Task 8 — Live two-client Socket probe and acceptance matrix

**Files:** create `RawUdpLiveProbe.ps1`; complete the metric properties in `RawUdpServerDiagnosticsSnapshot.cs`; do not add test-only branches to gameplay.

The probe loads the freshly compiled servercheck assembly, constructs its real `RawUdpRelayServer` through reflection, runs it on its normal background worker, and reads only the atomically replaced immutable `Diagnostics` property. It then creates two real UDP client sockets, performs the exact Raw handshake, sends at least 900 logical inputs per client, validates every received immutable input, requests normal server stop, and writes separate protocol/metrics evidence. A separate option/entry test launches the executable to prove the `--transport raw-udp` CLI branch. Fault injection sits between encoded output and peer input; it never edits decoded inputs.

- [ ] **Step 1: Write clean live-probe RED.** The script must fail with a named stage until it observes Hello/Welcome/Ready/Start, Canonical Frame 0 on both clients, RemoteFrameOffset 0, bidirectional inputs, default 88-byte datagrams, and exactly-once acceptance of every remote input for 900 frames/client. `RawUdpLedgerIntegrationTests` remains the automated proof that accepted arrivals publish to Ledger exactly once.
- [ ] **Step 2: Run clean RED.** Compile `obj/raw-udp-task8-red-servercheck.exe`, then run:

```powershell
& 'E:\帧同步_RouteC\Project\NetworkServer\RawUdpLiveProbe.ps1' `
  -ServerPath 'E:\帧同步_RouteC\Project\NetworkServer\obj\raw-udp-task8-red-servercheck.exe' `
  -Scenario clean -WindowSize 6 -FramesPerClient 900 -Seed 20260820 `
  -EvidencePath 'E:\帧同步_RouteC\raw-udp-task8-clean-red.json'
```

- [ ] **Step 3: Implement clean probe and metrics.** Record per direction: datagrams, bytes, pps, sequence high water, drops by class, queue high water, worker budget exhaustion, direct/redundancy/reorder recovery, tail copies, and server `LateRecoveryRelayCopies`. Do not record full SessionID, nonce, endpoint, datagram bytes, or raw values.
- [ ] **Step 4: Add the exact automated scenario matrix.** Each scenario uses 900 inputs/client unless it intentionally terminates earlier:
  - `first-control-loss`: independently drop first Hello, Welcome, Ready, and Start and prove 50ms retry/idempotence;
  - `drop-1-through-5`: prove recovery at N=6;
  - `aligned-6-uplink` and `aligned-6-downlink`: prove both terminate with the same `UnrecoverableInputGap`, player, and frame;
  - `mixed-faults`: delay+jitter+reorder+duplicate with fixed seed and reproducible trace;
  - `foreign-injection`: wrong Session/Player/Endpoint cannot kill the legal match;
  - `bound-conflict`: same frame/different raw terminates both with `ConflictingInput`;
  - `endpoint-change-silence`: no reconnect/migration and terminal timeout at 3000ms;
  - `window-16`: 168-byte maximum and valid relay;
  - `bounded-stop`: handshaking, running, terminated, and repeated stop complete within 260ms.
- [ ] **Step 5: Run scenario RED before each server/probe behavior patch.** Execute the loop below; each run must fail at the named unmet gate and write its own evidence file.

```powershell
$scenarios = @(
    'first-control-loss',
    'drop-1-through-5',
    'aligned-6-uplink',
    'aligned-6-downlink',
    'mixed-faults',
    'foreign-injection',
    'bound-conflict',
    'endpoint-change-silence',
    'window-16',
    'bounded-stop')
foreach ($scenario in $scenarios)
{
    $windowSize = if ($scenario -eq 'window-16') { 16 } else { 6 }
    & 'E:\帧同步_RouteC\Project\NetworkServer\RawUdpLiveProbe.ps1' `
      -ServerPath 'E:\帧同步_RouteC\Project\NetworkServer\obj\raw-udp-task8-red-servercheck.exe' `
      -Scenario $scenario -WindowSize $windowSize -FramesPerClient 900 -Seed 20260820 `
      -EvidencePath ("E:\帧同步_RouteC\raw-udp-task8-{0}-red.json" -f $scenario)
}
```

- [ ] **Step 6: Implement only the minimum behavior for each RED and rerun the same loop with `raw-udp-task8-green-servercheck.exe` and `-green.json` evidence names.** The late recovery scenario must explicitly deliver `[F+1..F+6]` before `[F..F+5]`; the second-arriving window carries the older upstream PacketSequence that was generated first, while each rebuilt downlink copy receives a fresh newer downlink PacketSequence. Assert F appears in exactly N bounded downlink send opportunities.
- [ ] **Step 7: Run the full live matrix.** Use `-Scenario all -WindowSize 6 -FramesPerClient 900 -EvidencePath E:\帧同步_RouteC\raw-udp-task8-live-matrix.json`; require all scenario statuses PASS and separate Raw-only labels.
- [ ] **Step 8: Run focused Unity and server regression.** Filter all Raw fixtures to `TestResults-raw-udp-task8-focused.xml`; rerun Raw protocol/lifecycle scripts against the same servercheck.

**Stop condition:** stop if the probe must treat generated opportunities as acknowledgements, if it cannot distinguish automated protocol acceptance from visual acceptance, or if any scenario needs reconnect, endpoint migration, rooms, authentication, encryption, NAT traversal, or KCP.

## 12. Task 9 — TCP/KCP/relevant/full/compile/net48 regressions

**Files:** no production additions are expected; only narrowly fix a demonstrated regression, always with a new valid RED first.

- [ ] **Step 1: Run Raw focused GREEN.** Use this exact filter and write `TestResults-raw-udp-final-focused.xml`:

```text
FrameSyncDemo.Tests.RawUdpProtocolCodecTests;
FrameSyncDemo.Tests.RawUdpProtocolStateTests;
FrameSyncDemo.Tests.RawUdpInputTransportTests;
FrameSyncDemo.Tests.RawUdpRelayHistoryTests;
FrameSyncDemo.Tests.RawUdpFacadeWiringTests;
FrameSyncDemo.Tests.RawUdpDatagramFaultModelTests;
FrameSyncDemo.Tests.RawUdpLedgerIntegrationTests
```

- [ ] **Step 2: Run relevant Unity GREEN.** Use one semicolon-separated filter containing `RouteCProtocolCodecTests`, `RouteCControlPayloadCodecTests`, `OutboundActualHistoryTests`, `KcpUdpClientStateMachineTests`, `KcpUdpClientWorkerTests`, `RouteCKcpSessionTests`, `TcpClientTransportTests`, `NetworkClientFacadeAndSessionFlowTests`, `P2FClientTransportOwnershipTests`, `NetworkLedgerWiringTests`, `FrameInputLedgerTests`, `FrameSyncCoordinatorTests`, and `RuntimeNetworkDiagnosticsTests`. Write `TestResults-raw-udp-final-relevant.xml` and require 0 failed/0 skipped.
- [ ] **Step 3: Run full EditMode GREEN.** Run without `-testFilter`; write `TestResults-raw-udp-final-full.xml`. Require total tests at least the prior 535 baseline plus all new tests, with 0 failed and 0 skipped.
- [ ] **Step 4: Run standalone Unity import/compile.** Execute:

```powershell
& 'C:\Unity\unity2022\Editor\Unity.exe' `
  -projectPath 'E:\帧同步_RouteC\Project\Frame Synchronization' `
  -batchmode -quit `
  -logFile 'E:\帧同步_RouteC\raw-udp-final-unity-compile.log'
```

Require process exit 0 and zero `error CS` lines.

- [ ] **Step 5: Compile all server and linked shared sources as net48/C# 7.3.** Use Section 14.4 exactly, output `Project/NetworkServer/obj/raw-udp-final-servercheck.exe`, save `raw-udp-final-server-net48-compile.log`, record dynamic source count, exit code, and output bytes, and never describe this as `dotnet build` because the machine lacks the SDK.
- [ ] **Step 6: Run server suites against that exact artifact.** Require PASS from `ServerOptionsTests.ps1`, `ServerEntryOwnershipTests.ps1`, `TcpRelayServerLifecycleTests.ps1`, `RawUdpServerProtocolTests.ps1`, `RawUdpRelayServerLifecycleTests.ps1`, `NetworkServerLabTests.ps1`, and `NetworkServerBarrierTests.ps1`.
- [ ] **Step 7: Run TCP timing regressions.** Run legacy interactive mode 0, explicit TCP recovered-loss, and 100% duplicate cases with the current timing probe. Require unchanged TCP recovered-loss/HOL/application-fault labels and unique-frame completion.
- [ ] **Step 8: Run final Raw live matrix.** Use `raw-udp-final-servercheck.exe`, 900 frames/client, and save `raw-udp-final-live-matrix.json`.
- [ ] **Step 9: Preserve evidence on failure.** Do not clean XML, logs, serverchecks, or probe JSON. Add a focused regression test for the observed failure before changing production behavior.

**Stop condition:** no final claim is allowed with a missing XML, any failed/skipped test, Unity C# compile error, net48 compile failure, TCP label drift, KCP focused failure, live scenario failure, or reused stale server artifact.

## 13. Task 10 — Static audit, truthful docs, and delivery handoff

**Files:** documentation files in Section 2.4; inspect all changed production/test/server files.

- [ ] **Step 1: Run documentation RED.** Execute the following before editing docs; it must fail because the delivery/recovery handoffs do not yet exist or the current docs do not contain the exact Raw command/boundary language. Save the failure as `raw-udp-task10-docs-red.log`.

```powershell
$architecture = 'E:\帧同步_RouteC\docs\architecture\route-c-frame-sync.md'
$demo = 'E:\帧同步_RouteC\docs\demo\windows-build-and-demo.md'
$delivery = 'E:\帧同步_RouteC\docs\handoffs\2026-08-20-raw-udp-minimal-demo-delivery.md'
$recovery = 'E:\帧同步_RouteC\docs\handoffs\2026-08-20-p2f-task8-resume-next-window-prompt.md'
if (-not (Test-Path -LiteralPath $delivery) -or -not (Test-Path -LiteralPath $recovery))
{
    throw 'Raw UDP delivery or KCP recovery handoff is missing.'
}
$required = @(
    @{ Path = $architecture; Text = 'Raw UDP minimal Demo' },
    @{ Path = $demo; Text = '--transport raw-udp --raw-window 6' },
    @{ Path = $delivery; Text = 'not KCP/P2-F completion' },
    @{ Path = $recovery; Text = 'original P2-F Task 8' })
foreach ($entry in $required)
{
    if (-not (Select-String -LiteralPath $entry.Path -SimpleMatch $entry.Text -Quiet))
    {
        throw "Missing documentation contract '$($entry.Text)' in $($entry.Path)."
    }
}
```

- [ ] **Step 2: Audit ownership and truth sources.** Confirm by source inspection and focused tests:
  - `NetworkClient` has exactly one `IFrameTransportClient` and no Socket/endpoint/protocol history/arrival queue;
  - `GameController` has no Socket, endpoint, UDP bytes, Raw gameplay branch, or arrival queue;
  - `RawUdpInputTransport` has exactly one Socket and one remote-arrival queue;
  - `RawUdpRelayServer` has exactly one Socket and one worker;
  - server code contains no gameplay, world simulation, prediction, rollback, snapshot, presentation, or WorldHash execution;
  - remote Actuals still reach `FrameInputLedger.RecordActual` only through the existing coordinator/GameController path.
- [ ] **Step 3: Audit protocol boundaries.** Confirm no ACK/bitmap/selective resend/congestion/reconnect/migration/resume behavior, no Raw state in KCP, no KCP metrics claimed for Raw, no TCP labels reused by Raw, and no opportunity count named or described as an ACK.
- [ ] **Step 4: Audit bounded resources and secrets.** Confirm every named 256/64/16/N/1200/168/10ms/2ms/260ms limit has a test, and every new or modified Raw transport/server/probe log contains only short SHA-256 session fingerprints rather than full sessions, nonces, endpoints, datagrams, or raw inputs.
- [ ] **Step 5: Update architecture and demo docs.** Document the three independent transports; exact `raw-udp` server/client commands; N=6/N=16 limits; no-ACK/no-reconnect scope; automated evidence; and the distinction between Raw UDP, TCP, and future KCP claims.
- [ ] **Step 6: Write delivery handoff.** `2026-08-20-raw-udp-minimal-demo-delivery.md` records changed files, every RED/GREEN artifact, actual test counts, net48 source count/output size, live metrics, accepted risks, missing manual evidence, and the explicit statement that Raw UDP completion is not KCP/P2-F completion.
- [ ] **Step 7: Write KCP recovery prompt.** `2026-08-20-p2f-task8-resume-next-window-prompt.md` points back to original P2-F Task 8, preserves Tasks 1..7 and Raw UDP, and forbids importing Raw sliding-window/sequence/gap rules into KCP.
- [ ] **Step 8: Run documentation focused GREEN and plan-to-design audit.** Rerun the exact Step 1 command to `raw-udp-task10-docs-green.log`; then map every section of the approved Raw design to one task/evidence item, recheck names/signatures across tasks, scan delivery docs for unresolved placeholders, and resolve every scope contradiction.
- [ ] **Step 9: Run documentation regression.** Run `git diff --check` on the four docs, verify every final evidence path in Section 14.5 exists, and re-read the Task 9 XML roots without rerunning or relabeling stale results. If any production file changed during documentation correction, rerun Task 9 focused, relevant, full, compile, server, TCP, KCP, and live gates.
- [ ] **Step 10: Present manual acceptance instructions to 帅老大 and stop.** Do not start original Task 8 and do not call automated success final visual acceptance.

**Stop condition:** stop and report if documentation would need to claim unmeasured CPU/latency/stability, conceal a failed/skipped gate, call Raw UDP reliable, call Raw UDP KCP, or declare final visual acceptance without 帅老大的 observation.

## 14. Exact verification helpers and evidence names

### 14.1 Focused Unity command example

```powershell
& 'C:\Unity\unity2022\Editor\Unity.exe' `
  -projectPath 'E:\帧同步_RouteC\Project\Frame Synchronization' `
  -batchmode -runTests -runSynchronously -testPlatform EditMode `
  -testFilter 'FrameSyncDemo.Tests.RawUdpProtocolCodecTests;FrameSyncDemo.Tests.RawUdpProtocolStateTests' `
  -testResults 'E:\帧同步_RouteC\Project\Frame Synchronization\TestResults-raw-udp-focused.xml' `
  -logFile 'E:\帧同步_RouteC\Project\Frame Synchronization\raw-udp-focused.log'
```

### 14.2 Full EditMode command

```powershell
& 'C:\Unity\unity2022\Editor\Unity.exe' `
  -projectPath 'E:\帧同步_RouteC\Project\Frame Synchronization' `
  -batchmode -runTests -runSynchronously -testPlatform EditMode `
  -testResults 'E:\帧同步_RouteC\Project\Frame Synchronization\TestResults-raw-udp-final-full.xml' `
  -logFile 'E:\帧同步_RouteC\Project\Frame Synchronization\raw-udp-final-full.log'
```

### 14.3 XML evidence validation

```powershell
[xml]$result = Get-Content -Raw -LiteralPath 'E:\帧同步_RouteC\Project\Frame Synchronization\TestResults-raw-udp-final-full.xml'
if ($result.'test-run'.failed -ne '0' -or $result.'test-run'.skipped -ne '0')
{
    throw "Unity tests are not green: failed=$($result.'test-run'.failed) skipped=$($result.'test-run'.skipped)"
}
```

### 14.4 net48/C# 7.3 server compile

Run from `Project/NetworkServer` and change only the output/evidence names per task:

```powershell
$serverDir = 'E:\帧同步_RouteC\Project\NetworkServer'
[xml]$projectFile = Get-Content -LiteralPath (Join-Path $serverDir 'NetworkServer.csproj')
$shared = @($projectFile.Project.ItemGroup.Compile | ForEach-Object {
    [IO.Path]::GetFullPath((Join-Path $serverDir $_.Include))
})
$sources = @(Get-ChildItem -LiteralPath $serverDir -Filter '*.cs' -File |
    Sort-Object FullName | ForEach-Object FullName) + $shared
$outputPath = Join-Path $serverDir 'obj\raw-udp-current-servercheck.exe'
$arguments = @(
    'exec',
    'C:\Unity\unity2022\Editor\Data\DotNetSdkRoslyn\csc.dll',
    '/nologo',
    '/target:exe',
    '/langversion:7.3',
    '/deterministic+',
    '/nostdlib+',
    ('/out:' + $outputPath),
    '/reference:C:\Unity\unity2022\Editor\Data\MonoBleedingEdge\lib\mono\4.8-api\mscorlib.dll',
    '/reference:C:\Unity\unity2022\Editor\Data\MonoBleedingEdge\lib\mono\4.8-api\System.dll',
    '/reference:C:\Unity\unity2022\Editor\Data\MonoBleedingEdge\lib\mono\4.8-api\System.Core.dll'
) + $sources
"LANGVERSION=7.3"
"SOURCE_COUNT=$($sources.Count)"
& 'C:\Unity\unity2022\Editor\Data\NetCoreRuntime\dotnet.exe' @arguments
if ($LASTEXITCODE -ne 0)
{
    throw "net48/C# 7.3 compile failed with exit code $LASTEXITCODE"
}
Get-Item -LiteralPath $outputPath | Select-Object FullName, Length
```

### 14.5 Final evidence set

- `TestResults-raw-udp-final-focused.xml` and `.log`
- `TestResults-raw-udp-final-relevant.xml` and `.log`
- `TestResults-raw-udp-final-full.xml` and `.log`
- `raw-udp-final-unity-compile.log`
- `Project/NetworkServer/obj/raw-udp-final-servercheck.exe`
- `raw-udp-final-server-net48-compile.log`
- `raw-udp-final-server-options.log`
- `raw-udp-final-server-protocol.log`
- `raw-udp-final-server-lifecycle.log`
- `raw-udp-final-tcp-lab.log`
- `raw-udp-final-tcp-barrier.log`
- `raw-udp-final-tcp-timing-legacy.json`
- `raw-udp-final-tcp-timing-recovered-loss.json`
- `raw-udp-final-tcp-timing-duplicate.json`
- `raw-udp-final-live-matrix.json`
- `raw-udp-final-static-audit.md`

## 15. Manual dual-client acceptance instructions

After every automated gate is green, ask 帅老大 to perform this acceptance; automated implementation must stop while waiting for the result.

1. Compile the final server artifact and launch:

```powershell
& 'E:\帧同步_RouteC\Project\NetworkServer\obj\raw-udp-final-servercheck.exe' `
  --transport raw-udp --raw-window 6
```

2. In Unity `SampleScene`, set the `NetworkClient` Inspector transport to RawUdp, server IP `127.0.0.1`, port `8888`, then enter Play Mode as player 0.
3. Launch the Windows client as player 1 with `--transport raw-udp --server-ip 127.0.0.1 --server-port 8888`.
4. Keep both clients running for at least 900 logical frames. Verify both begin at Canonical Frame 0, respond bidirectionally, do not start twice, and show no terminal fault in the clean case.
5. Save each client's Raw diagnostics separately. Confirm default RawInput is 88 bytes, queue/budget values remain bounded, and no full session/nonce/endpoint/raw values appear in ordinary logs.
6. Compare final Confirmed and Predicted WorldHash values after the recoverable run. Equality proves convergence; it does not prove that no intermediate prediction fork occurred.
7. Review the automated `drop-1-through-5`, aligned-six-drop, mixed-fault, foreign-injection, conflict, endpoint-silence, N=16, and bounded-stop evidence. Re-run a visual fault case only if 帅老大 requests it.
8. Record 帅老大的 explicit PASS/FAIL plus observations in the delivery handoff. Without that statement, report automated gates as complete but manual acceptance as pending.

## 16. Final completion boundary

When all automated gates pass and the manual result is recorded:

- report Raw UDP focused/relevant/full counts, all with 0 failed and 0 skipped;
- report Unity compile and net48/C# 7.3 evidence without mislabeling the compiler path;
- report TCP and KCP regressions independently from Raw metrics;
- report live Raw pps/bytes/queue/budget/recovery measurements without extrapolating to internet reliability;
- state that `FrameInputLedger` remains the sole Actual truth and the server performs no gameplay simulation;
- state that this is a Raw UDP minimal Demo, not KCP or full P2-F completion;
- stop before original P2-F Task 8, commit, merge, or push.

# P2-E Deterministic Network Lab and Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Repository rules forbid automatic commits and pushes, so each task ends with a status checkpoint instead of a commit.

**Goal:** Replace the server's periodic batch-release delay with deterministic per-packet scheduling, make network faults reproducible, and produce enough sidecar diagnostics to explain the fixed-100ms remote-motion cadence without changing deterministic basketball simulation.

**Architecture:** A pure C# fault model shared by Unity tests and the standalone server maps `(seed, sender, sender-sequence, frame, raw)` to deterministic delay, jitter, application reorder, duplicate, and recovered-loss decisions. The server owns a monotonic due-time scheduler and writes two different traces: a deterministic decision trace and a non-deterministic timing trace. The Unity client records receive metadata, while a presentation-side diagnostics recorder observes FIFO drain size, Confirmed catch-up, frame heads, rollbacks, hashes, and remote render motion without writing into either logic world.

**Tech Stack:** Unity 2022.3.62f2, C# 7.3-compatible shared code, NUnit EditMode tests, .NET Framework 4.8 server, PowerShell socket probes, existing 8-byte TCP input protocol.

---

## 1. Verified starting behavior and locked boundaries

- Both accepted server sockets and the Unity client set `TcpClient.NoDelay = true`.
- Server modes `0`, `1`, and `2` currently wake every approximately `5`, `100`, and `200` milliseconds and drain the whole sender queue. They do not assign independent packet due-times.
- `NetworkClient.RecvLoop` reads exact 8-byte messages on a background thread and enqueues them in one FIFO without receive timestamps.
- `GameController.DrainRemoteInputsToLedger()` drains the full FIFO during one logical input request.
- `FrameSyncCoordinator.CatchUpConfirmed()` may advance several Confirmed frames in one call.
- `ViewWorldBuilder` publishes only the latest confirmed `C-1 -> C` interval, and `PresentationFrameInterpolator` replaces the current View value rather than queuing skipped Confirmed intervals.
- Existing evidence supports this mechanism chain, but its quantitative contribution to visible 100ms cadence is not yet verified.
- The 8-byte protocol, canonical frame mapping, Ledger ownership, deterministic Step order, basketball rules, P2-D source arbitration, highlight replay, and terminal flow remain unchanged.
- A real TCP packet loss is recovered by TCP. Permanent application-message deletion would leave a permanent Ledger confirmation hole, so this plan models **transport loss as deterministic loss-recovery delay plus sender-stream head-of-line blocking**. Application-level out-of-order and duplicate injection are explicitly labeled as such.

## 2. File map

### Create

- `Project/Frame Synchronization/Assets/Scripts/Network/NetworkLabProfile.cs`: validated immutable fault parameters.
- `Project/Frame Synchronization/Assets/Scripts/Network/DeterministicNetworkFaultModel.cs`: order-independent decision generation and canonical decision trace lines.
- `Project/Frame Synchronization/Assets/Scripts/Network/NetworkPacketArrival.cs`: client-side raw/frame/receive-sequence/receive-time value.
- `Project/Frame Synchronization/Assets/Scripts/Presentation/RuntimeNetworkDiagnostics.cs`: optional, bounded, sidecar-only diagnostic aggregation and structured output.
- `Project/Frame Synchronization/Assets/Tests/EditMode/DeterministicNetworkFaultModelTests.cs`: deterministic profile and combination rules.
- `Project/Frame Synchronization/Assets/Tests/EditMode/NetworkFaultTraceReplayTests.cs`: arrival replay, rollback sequence, and final Hash convergence.
- `Project/Frame Synchronization/Assets/Tests/EditMode/RuntimeNetworkDiagnosticsTests.cs`: disabled behavior, drain/head/motion metrics, and formatting.
- Matching Unity `.meta` files for each new runtime and test asset.
- `Project/NetworkServer/NetworkLabOptions.cs`: interactive presets plus validated command-line profile parsing.
- `Project/NetworkServer/DeterministicPacketScheduler.cs`: monotonic due-time queue, stable tie ordering, recovered-loss barrier, and duplicate copies.
- `Project/NetworkServer/NetworkTraceWriter.cs`: deterministic decision trace and observational timing trace separation.
- `Project/NetworkServer/NetworkServerLabTests.ps1`: fake-time scheduler and option tests loaded from the built server assembly.
- `Project/NetworkServer/NetworkServerTimingProbe.ps1`: two-socket live cadence probe for legacy and per-packet 0/100/200ms behavior.

### Modify

- `Project/NetworkServer/NetworkServer.cs`: replace per-client sleep-and-drain threads with one due-time scheduler and structured trace calls.
- `Project/NetworkServer/NetworkServer.csproj`: link the two pure shared fault-model source files.
- `Project/NetworkServer/NetworkServer.exe`: regenerate the versioned delivery executable after tests pass.
- `Project/Frame Synchronization/Assets/Scripts/Network/NetworkClient.cs`: queue `NetworkPacketArrival` values while retaining the legacy dequeue overload.
- `Project/Frame Synchronization/Assets/Scripts/GameController.cs`: opt-in diagnostics wiring and observations only.
- `Project/Frame Synchronization/Assets/Tests/EditMode/NetworkLedgerWiringTests.cs`: lock the single-FIFO ownership and arrival metadata boundary.
- `Project/Frame Synchronization/Assets/Tests/EditMode/P2CRuntimeOwnershipTests.cs`: prove diagnostics add no second world/Ledger owner.
- `docs/architecture/route-c-frame-sync.md`, `docs/architecture/streetball2-minimal-frame-sync-v2.md`, `Project/路线规划_街篮帧同步最小实现.md`, and `docs/demo/windows-build-and-demo.md`: update only verified outcomes after all gates.

### Explicitly not modified before the decision gate

- `FrameInputLedger.cs`, `FrameSyncCoordinator.cs`, `DeterministicWorld`, gameplay systems, `ViewWorldBuilder.cs`, `PresentationFrameInterpolator.cs`, and `ViewWorldState.cs`.
- If per-packet arrivals are quantitatively uniform but presentation still skips visibly, stop and create a separate approved playback-cursor plan. Do not silently expand this plan.

## 3. Locked public data shapes

The RED tests target these minimal APIs:

```csharp
public readonly struct NetworkLabProfile
{
    public NetworkLabProfile(
        int baseDelayMs,
        int jitterMs,
        int applicationReorderPercent,
        int reorderExtraDelayMs,
        int duplicatePercent,
        int recoveredLossPercent,
        int lossRecoveryDelayMs,
        uint seed);
}

public static class DeterministicNetworkFaultModel
{
    public readonly struct Decision
    {
        public int EffectiveDelayMs { get; }
        public bool ApplicationReordered { get; }
        public bool Duplicated { get; }
        public bool LossRecovered { get; }
        public string ToCanonicalTraceLine(
            int senderIndex,
            long senderSequence,
            int frameID,
            uint raw);
    }

    public static Decision Decide(
        in NetworkLabProfile profile,
        int senderIndex,
        long senderSequence,
        int frameID,
        uint raw);
}

public readonly struct NetworkPacketArrival
{
    public uint Raw { get; }
    public int RemoteFrameID { get; }
    public long ReceiveSequence { get; }
    public long ReceivedTimestamp { get; }
}
```

All probability rolls are derived from stable integer mixing with independent salts. `System.Random`, process time, thread order, and wall-clock timestamps must not influence a decision trace.

## 4. Task 1: Capture the legacy timing baseline

**Files:**
- Create: `Project/NetworkServer/NetworkServerTimingProbe.ps1`
- Evidence: `p2e-legacy-0ms-timing.json`, `p2e-legacy-100ms-timing.json`, `p2e-legacy-200ms-timing.json`

- [ ] **Step 1: Write the live socket probe**

The probe starts the existing server, supplies interactive mode `0`, `1`, or `2`, connects two low-latency clients, sends at least twelve 8-byte frames every 33ms, records client-send and peer-receive timestamps, and groups receive gaps of at most 10ms as one observed burst.

```powershell
param(
    [ValidateSet(0, 1, 2)][int]$Mode,
    [string]$ServerPath,
    [string]$OutputPath,
    [switch]$RequirePerPacketCadence
)
```

- [ ] **Step 2: Run the probe against the untouched delivery server**

Run all three modes. Mode 100ms is expected to fail `-RequirePerPacketCadence` because multiple 33ms frames are released in one wake-up; preserve the JSON as RED evidence rather than treating it as an infrastructure failure.

- [ ] **Step 3: Record the baseline interpretation**

Record per mode: median/p95 send-to-receive delay, median/p95 receive gap, maximum burst size, and frame order. Do not infer visual smoothness from this server-only probe.

## 5. Task 2: Pure deterministic fault decisions

**Files:**
- Create: `Assets/Scripts/Network/NetworkLabProfile.cs`
- Create: `Assets/Scripts/Network/DeterministicNetworkFaultModel.cs`
- Test: `Assets/Tests/EditMode/DeterministicNetworkFaultModelTests.cs`

- [ ] **Step 1: Add RED tests for validation and fixed delay**

```csharp
[Test]
public void Decide_Fixed100_AssignsExactlyOneHundredMilliseconds()
{
    NetworkLabProfile profile = new NetworkLabProfile(
        100, 0, 0, 0, 0, 0, 0, 17u);

    var decision = DeterministicNetworkFaultModel.Decide(
        profile, 0, 3, 42, 0x1234u);

    Assert.AreEqual(100, decision.EffectiveDelayMs);
    Assert.IsFalse(decision.ApplicationReordered);
    Assert.IsFalse(decision.Duplicated);
    Assert.IsFalse(decision.LossRecovered);
}
```

Reject negative delays, jitter larger than supported integer bounds, percentages outside `0..100`, negative recovery/reorder delays, and invalid sender indices.

- [ ] **Step 2: Run the focused fixture and verify RED**

Expected failure: missing `NetworkLabProfile` and `DeterministicNetworkFaultModel` runtime types.

- [ ] **Step 3: Implement the minimal fixed-delay path and verify GREEN**

- [ ] **Step 4: Add RED tests for repeatability and seed separation**

For the same seed and script, compare the full sequence of canonical decision trace lines byte-for-byte. For two selected seeds, require at least one differing decision over 512 frames.

- [ ] **Step 5: Add RED tests for composition rules**

Cover jitter clamping to non-negative delay, reorder extra delay, one duplicate copy, recovered-loss extra delay, and a profile where all percentages are 100. Assert canonical trace lines exclude timestamps.

- [ ] **Step 6: Implement stable integer mixing and verify all focused tests GREEN**

## 6. Task 3: Server per-packet due-time scheduler

**Files:**
- Create: `Project/NetworkServer/NetworkLabOptions.cs`
- Create: `Project/NetworkServer/DeterministicPacketScheduler.cs`
- Create: `Project/NetworkServer/NetworkTraceWriter.cs`
- Create: `Project/NetworkServer/NetworkServerLabTests.ps1`
- Modify: `Project/NetworkServer/NetworkServer.csproj`
- Modify: `Project/NetworkServer/NetworkServer.cs`

- [ ] **Step 1: Add shared-source links to the server project**

```xml
<Compile Include="..\Frame Synchronization\Assets\Scripts\Network\NetworkLabProfile.cs" Link="Shared\NetworkLabProfile.cs" />
<Compile Include="..\Frame Synchronization\Assets\Scripts\Network\DeterministicNetworkFaultModel.cs" Link="Shared\DeterministicNetworkFaultModel.cs" />
```

- [ ] **Step 2: Write RED scheduler tests using a fake monotonic timestamp**

Required assertions:

- enqueue timestamps `0, 33, 66` under fixed 100ms produce due timestamps `100, 133, 166`;
- dequeue at `99` returns none and at each exact due time returns only the matching item;
- same-due items use sender, sender-sequence, then copy-index ordering;
- duplicate creates exactly one second delivery;
- recovered loss extends the sender reliability barrier;
- an explicit application-reorder decision may pass the held message and is labeled in trace output.

- [ ] **Step 3: Build the server and verify scheduler RED**

Run `dotnet build Project/NetworkServer/NetworkServer.csproj -c Release`. Execute `NetworkServerLabTests.ps1` through `powershell.exe -ExecutionPolicy Bypass`. Expected RED is missing scheduler/option/trace types, not compilation syntax errors in the tests.

- [ ] **Step 4: Implement minimal scheduler, options, and trace writer**

Interactive `0/1/2` remains supported. Command-line automation accepts explicit delay, jitter, reorder, duplicate, recovered-loss, recovery-delay, seed, decision-trace path, and timing-trace path. The scheduler uses a monotonic clock and wakes for the earliest due item rather than sleeping for a fixed batch period.

- [ ] **Step 5: Replace the server send loops**

Receive threads only parse exact 8-byte messages and enqueue scheduled deliveries. One scheduler thread performs writes. Decision traces contain only stable identifiers and fault choices; timing traces additionally contain enqueue, due, actual send, batch ID, and send lateness.

- [ ] **Step 6: Verify focused server tests and the existing barrier GREEN**

Run the new lab script, then `NetworkServerBarrierTests.ps1`. Preserve the two-client barrier and fragmented-frame behavior.

## 7. Task 4: Client arrival metadata and runtime diagnostics

**Files:**
- Create: `Assets/Scripts/Network/NetworkPacketArrival.cs`
- Create: `Assets/Scripts/Presentation/RuntimeNetworkDiagnostics.cs`
- Create: `Assets/Tests/EditMode/RuntimeNetworkDiagnosticsTests.cs`
- Modify: `Assets/Scripts/Network/NetworkClient.cs`
- Modify: `Assets/Scripts/GameController.cs`
- Modify: `Assets/Tests/EditMode/NetworkLedgerWiringTests.cs`
- Modify: `Assets/Tests/EditMode/P2CRuntimeOwnershipTests.cs`

- [ ] **Step 1: Add RED arrival-boundary tests**

Require one `ConcurrentQueue<NetworkPacketArrival>`, a monotonic receive sequence, and an overload `TryGetRemoteInput(out NetworkPacketArrival arrival)`. Retain the old raw/frame overload as a wrapper so existing consumers and tests remain source-compatible.

- [ ] **Step 2: Add RED diagnostics tests**

```csharp
[Test]
public void DisabledRecorder_ProducesNoLines()
{
    var lines = new List<string>();
    var diagnostics = new RuntimeNetworkDiagnostics(false, lines.Add);

    diagnostics.RecordDrain(12, 3, 2, 5);

    Assert.IsEmpty(lines);
}
```

Also test receive interval, drain count, Confirmed delta, Predicted/Confirmed/View heads, rollback count, confirmed Hash, remote render displacement, reverse displacement, and stationary duration. Use an injected `Action<string>` sink and invariant formatting.

- [ ] **Step 3: Run the focused fixtures and verify RED**

- [ ] **Step 4: Implement arrival metadata and bounded diagnostics**

The recorder is off by default, can be enabled by serialized configuration or `-p2eDiagnostics`, and does not retain an unbounded event list. It never changes Ledger, worlds, frame heads, snapshots, View values, or Transforms.

- [ ] **Step 5: Wire observations at existing boundaries**

- receive: background `RecvLoop` timestamp and sequence;
- drain: `DrainRemoteInputsToLedger` count and first/last receive times;
- logic: Confirmed before/after `Advance`, Predicted head, and built View head;
- reconciliation: mismatch/restored/replayed plus confirmed Hash;
- render: remote displayed displacement, direction reversal, stationary interval, and current heads.

- [ ] **Step 6: Verify focused diagnostics and ownership fixtures GREEN**

## 8. Task 5: Deterministic trace replay and convergence

**Files:**
- Create: `Assets/Tests/EditMode/NetworkFaultTraceReplayTests.cs`
- Reuse: `FrameSyncCoordinator`, `WorldHash`, and the pure fault model

- [ ] **Step 1: Add RED same-seed replay test**

Generate a fixed two-player input script, produce delivery events from the fault model, advance prediction at 33ms logical cadence, deliver Actual inputs by due order, and record mismatch/rollback tuples. Two executions with the same profile and seed must have identical decision traces and rollback tuples.

- [ ] **Step 2: Add RED scenario matrix**

Cover normal 0ms, fixed 100ms, fixed 200ms, jitter, application reorder, duplicate, and recovered loss. Duplicate Actuals must be idempotent; reorder must not cross a confirmation hole; recovered loss must eventually fill the hole.

- [ ] **Step 3: Add RED final convergence assertions**

After the final delayed/recovered event arrives, require both inputs Actual through the terminal frame and require Confirmed/Predicted `WorldHash` equality at that canonical frame. Do not claim that intermediate Predicted worlds never diverged.

- [ ] **Step 4: Implement only missing test seams, if any, and verify GREEN**

No production mutation API may be added solely for tests. Prefer driving existing public coordinator APIs.

## 9. Task 6: Rebuild server and quantify 100ms behavior

**Files:**
- Modify generated artifact: `Project/NetworkServer/NetworkServer.exe`
- Evidence: `p2e-perpacket-0ms-timing.json`, `p2e-perpacket-100ms-timing.json`, `p2e-perpacket-200ms-timing.json`
- Evidence: deterministic decision and observational timing JSONL files for each fault profile

- [ ] **Step 1: Rebuild Release and copy the executable intentionally**

Use `dotnet build Project/NetworkServer/NetworkServer.csproj -c Release`. Copy only the resulting `NetworkServer.exe` to the versioned delivery path; do not copy `bin/` or `obj/` trees into version control.

- [ ] **Step 2: Re-run the exact baseline probe**

Fixed 100ms must now preserve the approximately 33ms sender cadence after pipeline warm-up instead of intentionally releasing three frames per 100ms wake. Compare median/p95 delay, receive gap, and maximum burst size against the legacy JSON.

- [ ] **Step 3: Run fault traces twice**

The deterministic decision trace must be byte-identical for the same seed/script. Timing traces are compared statistically and are not required to be byte-identical.

- [ ] **Step 4: Apply the presentation-cursor decision gate**

Proceed without a playback cursor if per-packet arrival cadence, client drain size, Confirmed advancement, and remote displacement improve consistently. If arrival/drain/Confirmed cadence is already uniform but remote render displacement still shows periodic multi-frame jumps, stop and request approval for a separate `ConfirmedPresentationCursor` plan.

## 10. Automated and manual acceptance matrix

| Scenario | Deterministic expectation | Runtime observation |
|---|---|---|
| 0ms | due equals enqueue; stable order | no intentional 5ms batch window |
| Fixed 100ms | each due equals own enqueue + 100ms | warmed receive cadence follows ~33ms sends |
| Fixed 200ms | each due equals own enqueue + 200ms | cadence preserved after longer pipeline fill |
| Jitter | same seed gives same offsets | timing follows chosen offsets within scheduler lateness |
| Application reorder | explicitly selected messages change delivery order | Ledger waits for gaps and then catches up |
| Duplicate | exactly one labeled copy | Ledger records idempotent duplicate only |
| Recovered loss | labeled recovery delay and HOL barrier | confirmation stalls temporarily, then converges |
| Fault end | no remaining due events | terminal Confirmed Hash equals Predicted Hash |

Manual dual-client fixed-100ms replay records:

1. steady remote movement for at least ten seconds;
2. deliberate direction reversals separated from the steady segment;
3. no return of old rollback backstep/jerk;
4. p50/p95/max remote render displacement and maximum reverse displacement;
5. p50/p95 Confirmed advancement and FIFO drain size;
6. observed presentation cadence before and after the scheduler change.

## 11. Task 7: Full verification and truthful documentation

- [ ] Run focused fault-model, diagnostics, trace-replay, network wiring, coordinator, View, and presentation fixtures.
- [ ] Run full EditMode and require a newly generated XML with zero failures/skips; an editor exit code without XML is not evidence.
- [ ] Run standalone Unity import/compile and require zero C# errors.
- [ ] Run `NetworkServerLabTests.ps1` and `NetworkServerBarrierTests.ps1` and require PASS.
- [ ] Run the live 0/100/200ms timing probe and preserve before/after JSON.
- [ ] Build Windows x64 and require success.
- [ ] Run `git diff --check` and inspect only the approved paths for protocol, simulation, gameplay, P2-D, P2-F, and worktree-safety scope creep.
- [ ] Update architecture, roadmap, and demo documentation only with verified outcomes.
- [ ] Preserve all pre-existing worktree changes. Do not commit, merge, reset, clean, checkout, or push.

## 12. Evidence names

- `TestResults-p2e-fault-model-red.xml`, `TestResults-p2e-fault-model-green.xml`
- `TestResults-p2e-diagnostics-red.xml`, `TestResults-p2e-diagnostics-green.xml`
- `TestResults-p2e-network-replay.xml`, `TestResults-p2e-full.xml`
- `p2e-server-lab-tests.log`, `p2e-server-barrier.log`
- `p2e-legacy-{0,100,200}ms-timing.json`
- `p2e-perpacket-{0,100,200}ms-timing.json`
- `p2e-seed-<seed>-decision-run{1,2}.jsonl`
- `p2e-seed-<seed>-timing-run{1,2}.jsonl`
- `p2e-compile.log`, `p2e-build.log`

The current baseline Unity rerun on 2026-08-14 did not generate an XML because of Unity Licensing/Test Runner environment behavior. The last valid pre-P2-E full evidence remains P2-D `344/344`; P2-E completion still requires a fresh XML.

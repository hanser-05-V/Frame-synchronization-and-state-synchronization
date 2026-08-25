# P2-F UDP/KCP Transport Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a portfolio-grade UDP/KCP transport with one-thread ownership, idempotent 1v1 sessions, bounded in-match resume, TCP fallback, deterministic protocol evidence, and live Socket interval evidence while preserving the existing 8-byte frame-input and Ledger semantics.

**Architecture:** `NetworkClient` becomes a Unity-facing facade over `IFrameTransportClient`; the KCP implementation owns UDP, KCP, session state, and shutdown on one worker thread, while the main thread exchanges immutable messages through concurrent queues. The server selects either the preserved TCP relay or one UDP worker that serially owns two KCP sessions. Shared protocol/KCP adapters are pure C# and linked into the net48 server so deterministic virtual-link tests exercise the same codecs and session logic as live sockets.

**Tech Stack:** Unity 2022.3.62f2, C# compatible with Unity/net48, NUnit EditMode, `System.Net.Sockets`, `System.Threading`, `Stopwatch`, vendored kcp2k core at commit `66efda6686f649838d42f078fbaabf56ac449de4`, PowerShell live probes.

---

## 0. Execution rules

- Work in `E:\帧同步_RouteC` on the existing `delivery/route-c` branch unless帅老大 explicitly changes it.
- The worktree already contains extensive P2-A through P2-E changes. Preserve them. Never reset, clean, checkout, merge, commit, or push.
- Every production change follows RED -> focused GREEN -> relevant regression GREEN.
- Do not modify `FrameInputLedger` semantics, Canonical Frame mapping, deterministic simulation, gameplay rules, P2-D view arbitration, P2-E confirmed playback, highlight replay, or terminal flow except for the explicitly listed resume-readiness query surface.
- Do not copy StreetBall2 networking files. Its only recorded parameter fact is `NoDelay(1, 1, 2, 1)`.
- Do not claim P2-F complete until both deterministic virtual-link and live Socket evidence pass. Final dual-client visual acceptance belongs to帅老大.

## 1. Locked constants and binary shapes

Use these names and values consistently in client, server, tests, and probes:

```csharp
public static class RouteCProtocolConstants
{
    public const byte Version = 1;
    public const int HeaderSize = 28;
    public const int MaximumDatagramSize = 1200;
    public const int KcpMtu = MaximumDatagramSize - HeaderSize; // 1172
    public const int KcpHeaderSize = 24;
    public const int BusinessInputSize = 8;
    public const int NonceSize = 16;
    public const int SessionIdSize = 16;
    public const int ReconnectTokenSize = 32;
    public const int InitialKcpIntervalMs = 10;
    public const int HandshakeRetryMs = 250;
    public const int HeartbeatSilenceMs = 1000;
    public const int DisconnectTimeoutMs = 3000;
    public const int ResumeGraceMs = 5000;
    public const int InputHistoryCapacity = 256;
    public const int MaximumDatagramsPerRound = 64;
    public const int MaximumMessagesPerSessionPerRound = 64;
    public const int MaximumWorkerSliceMs = 2;
}

public enum RouteCMessageType : byte
{
    Hello = 1,
    Welcome = 2,
    Ready = 3,
    Start = 4,
    KcpData = 5,
    Heartbeat = 6,
    Disconnect = 7,
    ResumeProbe = 8,
    ResumeState = 9,
    ResumeAccepted = 10,
    ResumeComplete = 11,
    ResumeRejected = 12
}

public enum NetworkTransportKind : byte
{
    KcpUdp = 0,
    Tcp = 1
}

public enum NetworkSessionState : byte
{
    Disconnected,
    Handshaking,
    AwaitingReady,
    Running,
    Reconnecting,
    Resuming,
    Terminated
}
```

Outer header bytes are exactly `RCF2 | version | type | payloadLength(be) | sessionId(16) | generation(be)`. KCP Conv is absent from the outer header and is parsed from the first four KCP payload bytes as little-endian.

## 2. File map

### Third-party files to create

- `Project/Frame Synchronization/Assets/ThirdParty/kcp2k/Core/AckItem.cs`
- `Project/Frame Synchronization/Assets/ThirdParty/kcp2k/Core/Kcp.cs`
- `Project/Frame Synchronization/Assets/ThirdParty/kcp2k/Core/Pool.cs`
- `Project/Frame Synchronization/Assets/ThirdParty/kcp2k/Core/Segment.cs`
- `Project/Frame Synchronization/Assets/ThirdParty/kcp2k/Core/Utils.cs`
- `Project/Frame Synchronization/Assets/ThirdParty/kcp2k/LICENSE`
- `Project/Frame Synchronization/Assets/ThirdParty/kcp2k/NOTICE.md`
- `Project/Frame Synchronization/Assets/ThirdParty/kcp2k/PATCHES.md`
- Unity `.meta` files for the imported directory and files.

### Shared runtime/network files to create

- `Project/Frame Synchronization/Assets/Scripts/Network/RouteCProtocolConstants.cs`: protocol constants and enums.
- `Project/Frame Synchronization/Assets/Scripts/Network/RouteCSessionId.cs`: immutable 128-bit ID with equality/hash and zero value.
- `Project/Frame Synchronization/Assets/Scripts/Network/RouteCProtocolMessage.cs`: parsed outer message value.
- `Project/Frame Synchronization/Assets/Scripts/Network/RouteCProtocolCodec.cs`: strict outer/control/business codecs.
- `Project/Frame Synchronization/Assets/Scripts/Network/RouteCKcpHeader.cs`: safe little-endian Conv read.
- `Project/Frame Synchronization/Assets/Scripts/Network/RouteCKcpSettings.cs`: validation and `Kcp` configuration.
- `Project/Frame Synchronization/Assets/Scripts/Network/RouteCKcpSession.cs`: one-thread KCP adapter and diagnostics counters.
- `Project/Frame Synchronization/Assets/Scripts/Network/IMonotonicClock.cs`: injectable millisecond clock.
- `Project/Frame Synchronization/Assets/Scripts/Network/StopwatchMonotonicClock.cs`: live clock.
- `Project/Frame Synchronization/Assets/Scripts/Network/ResumeReadiness.cs`: the three canonical resume fields.
- `Project/Frame Synchronization/Assets/Scripts/Network/OutboundActualHistory.cs`: bounded immutable 8-byte send copies.
- `Project/Frame Synchronization/Assets/Scripts/Network/IFrameTransportClient.cs`: Unity-independent client boundary.
- `Project/Frame Synchronization/Assets/Scripts/Network/KcpUdpClientTransport.cs`: client state machine and live worker.
- `Project/Frame Synchronization/Assets/Scripts/Network/TcpClientTransport.cs`: queue-owned TCP fallback.
- `Project/Frame Synchronization/Assets/Scripts/Network/NetworkTransportEvent.cs`: immutable main-thread state/fault event.
- Matching `.meta` files.

### Existing Unity files to modify

- `Project/Frame Synchronization/Assets/Scripts/Network/NetworkClient.cs`: facade; no direct Socket/KCP access on main thread.
- `Project/Frame Synchronization/Assets/Scripts/Network/NetworkConfig.cs`: default KCP and command-line override parsing.
- `Project/Frame Synchronization/Assets/Scripts/Network/NetworkTransportSettings.cs`: retain TCP `NoDelay`; add KCP settings factory only.
- `Project/Frame Synchronization/Assets/Scripts/FrameSync/WorldSnapshotStore.cs`: expose exact readable bounds.
- `Project/Frame Synchronization/Assets/Scripts/FrameSync/FrameSyncCoordinator.cs`: expose earliest safe canonical rollback frame.
- `Project/Frame Synchronization/Assets/Scripts/GameController.cs`: asynchronous Start, submit ResumeReadiness, preserve Ledger ownership.

### Server files to create

- `Project/NetworkServer/ServerOptions.cs`: `--transport tcp|kcp`, KCP interval and lab settings.
- `Project/NetworkServer/TcpRelayServer.cs`: current TCP/P2-E server behavior extracted intact.
- `Project/NetworkServer/KcpUdpRelayServer.cs`: one UDP Socket and one worker.
- `Project/NetworkServer/KcpServerSession.cs`: per-player KCP/session/generation/endpoint state.
- `Project/NetworkServer/ServerSessionRouter.cs`: strict Session+Generation+Endpoint+Conv routing.
- `Project/NetworkServer/ServerInputHistory.cs`: bounded player+frame 8-byte history.
- `Project/NetworkServer/CryptoRandomSource.cs`: SessionID/Nonce/Token/Conv generation.
- `Project/NetworkServer/KcpServerDiagnostics.cs`: counters and interval/wakeup metrics.
- `Project/NetworkServer/KcpServerProtocolTests.ps1`: reflection-based pure protocol/server tests.
- `Project/NetworkServer/KcpLiveSocketProbe.ps1`: two-client live KCP probe.
- `Project/NetworkServer/KcpIntervalMatrix.ps1`: repeatable 1/5/10/20ms evidence runner.

### Existing server files to modify

- `Project/NetworkServer/NetworkServer.cs`: thin Program selection and shutdown only.
- `Project/NetworkServer/NetworkServer.csproj`: link shared protocol/KCP source and new server files.
- `Project/NetworkServer/NetworkServerLabTests.ps1`: continue testing TCP lab semantics.
- `Project/NetworkServer/NetworkServerBarrierTests.ps1`: invoke TCP mode explicitly.
- `Project/NetworkServer/NetworkServer.exe`: regenerate only after all gates pass.

### Unity tests to create

- `Project/Frame Synchronization/Assets/Tests/EditMode/KcpVendorProvenanceTests.cs`
- `Project/Frame Synchronization/Assets/Tests/EditMode/RouteCProtocolCodecTests.cs`
- `Project/Frame Synchronization/Assets/Tests/EditMode/RouteCKcpSessionTests.cs`
- `Project/Frame Synchronization/Assets/Tests/EditMode/OutboundActualHistoryTests.cs`
- `Project/Frame Synchronization/Assets/Tests/EditMode/KcpUdpClientStateMachineTests.cs`
- `Project/Frame Synchronization/Assets/Tests/EditMode/DeterministicUdpLinkTests.cs`
- `Project/Frame Synchronization/Assets/Tests/EditMode/KcpResumeProtocolTests.cs`
- `Project/Frame Synchronization/Assets/Tests/EditMode/KcpTransportOwnershipTests.cs`
- Matching `.meta` files.

### Deterministic test support to create

- `Project/Frame Synchronization/Assets/Tests/EditMode/Support/VirtualMonotonicClock.cs`
- `Project/Frame Synchronization/Assets/Tests/EditMode/Support/UdpDatagramFaultProfile.cs`
- `Project/Frame Synchronization/Assets/Tests/EditMode/Support/DeterministicUdpLink.cs`
- Matching `.meta` files.

## 3. Task 1: Capture the TCP baseline and vendor the audited KCP core

**Files:** third-party files listed above; create `KcpVendorProvenanceTests.cs`.

- [ ] **Step 1: Preserve a fresh pre-P2-F baseline**

Run focused network/ledger/diagnostics tests, full EditMode, standalone Unity import/compile, `dotnet build Project/NetworkServer/NetworkServer.csproj -c Release`, `NetworkServerLabTests.ps1`, and `NetworkServerBarrierTests.ps1`. Save evidence with `p2f-baseline-*` names. A Unity exit code without a newly generated XML is not a passing test run.

- [ ] **Step 2: Download exactly the approved upstream files**

Source base:

```text
https://raw.githubusercontent.com/MirrorNetworking/kcp2k/66efda6686f649838d42f078fbaabf56ac449de4/
```

Expected original SHA-256 values:

```text
AckItem.cs 38202E24E2BF8D5D36A7478FDE42C1D90ED84210018720B005E5FA79BB02617A
Kcp.cs     3C493FAA7981074899EE0283174350004BB25FBE294F0BB2DA16E2D4C4DC7F0C
Pool.cs    8B7F44E2E7FCC4DE9C12964178545E3350532939C09101632982909B42ADF1D5
Segment.cs A62E0B9B5C824D4728F697A68DAA21816716623427EBFEB555B44BA82E87EBA7
Utils.cs   26853EC81350DA5402E9D281A977CC92DAC711BA9FB02A1BA9137FE60311E339
LICENSE    1A12B7192346C004F7C3E5CE90D652750AA0FE7A29DF67CAC5F6271433808FE0
```

Fail the task if any hash differs. Do not import `highlevel/` or `AssemblyInfo.cs`.

- [ ] **Step 3: Add the explicit interval patch**

In vendored `Kcp.cs`, change both occurrences of:

```csharp
// clamp interval between 10 and 5000
else if (interval < 10) interval = 10;
```

to:

```csharp
// Route C experiment patch: preserve 1/5/10/20ms as effective values.
// Upstream 66efda6 clamps below 10ms; see PATCHES.md.
else if (interval < 1) interval = 1;
```

Make no other source change. `PATCHES.md` records the two patched methods (`SetInterval`, `SetNoDelay`), upstream hash, patched hash and reason. `NOTICE.md` records repository URL, commit, imported file list, MIT license, and that high-level components were excluded.

- [ ] **Step 4: Add RED/GREEN provenance tests**

```csharp
[Test]
public void SetNoDelay_OneAndFiveMilliseconds_RemainEffective()
{
    var packets = new List<byte[]>();
    var one = new kcp2k.Kcp(1u, (bytes, count) =>
        packets.Add(bytes.Take(count).ToArray()));

    one.SetNoDelay(1u, 1u, 2, false);
    one.Update(0u);
    Assert.AreEqual(1u, one.Check(0u));

    var five = new kcp2k.Kcp(2u, (bytes, count) => { });
    five.SetNoDelay(1u, 5u, 2, false);
    five.Update(0u);
    Assert.AreEqual(5u, five.Check(0u));
}
```

Also reflect the runtime assembly and assert that no kcp2k high-level `KcpClient`, `KcpServer`, or `KcpConnection` type exists.

- [ ] **Step 5: Run focused tests and compile both consumers**

Require provenance tests GREEN, Unity compilation GREEN, and server net48 compilation GREEN with the five core files linked from the Unity third-party directory.

## 4. Task 2: Implement strict protocol values and codecs

**Files:** shared protocol files plus `RouteCProtocolCodecTests.cs`.

- [ ] **Step 1: Write RED header round-trip tests**

```csharp
[Test]
public void Envelope_RoundTrip_UsesExactTwentyEightByteHeader()
{
    var session = new RouteCSessionId(0x0102030405060708UL,
                                      0x1112131415161718UL);
    byte[] payload = { 0xAA, 0xBB };
    byte[] datagram = RouteCProtocolCodec.Encode(
        RouteCMessageType.Heartbeat, session, 0x21222324u, payload);

    Assert.AreEqual(30, datagram.Length);
    CollectionAssert.AreEqual(new byte[] { 0x52, 0x43, 0x46, 0x32 },
                              datagram.Take(4).ToArray());
    Assert.IsTrue(RouteCProtocolCodec.TryDecode(
        datagram, datagram.Length, out RouteCProtocolMessage decoded,
        out RouteCProtocolDropReason reason));
    Assert.AreEqual(RouteCProtocolDropReason.None, reason);
    Assert.AreEqual(session, decoded.SessionId);
    Assert.AreEqual(0x21222324u, decoded.Generation);
    CollectionAssert.AreEqual(payload, decoded.Payload);
}
```

- [ ] **Step 2: Add RED rejection matrix**

Test bad magic, unknown version, unknown type, declared/actual length mismatch, payload >1172, truncated header, KcpData shorter than 24, wrong outer maximum size, and integer boundary values. Each case returns one exact `RouteCProtocolDropReason` and never throws for untrusted bytes.

- [ ] **Step 3: Implement values and codec**

Use shift-based big-endian helpers compatible with both Unity and net48. `RouteCSessionId` contains two readonly `ulong` values, implements `IEquatable<RouteCSessionId>`, and has `IsZero`. `RouteCProtocolMessage` owns an immutable payload copy; no decoded message may reference a reused Socket buffer.

- [ ] **Step 4: Add control payload tests and implementation**

Provide exact encode/decode methods for Hello, Welcome, Ready, Start, ResumeProbe, ResumeState, ResumeAccepted, ResumeComplete and ResumeRejected. Reject every payload whose length is not exactly the table in the design spec.

- [ ] **Step 5: Lock the 8-byte business format**

```csharp
[Test]
public void BusinessInput_RoundTrip_PreservesExistingLittleEndianLayout()
{
    byte[] bytes = RouteCProtocolCodec.EncodeBusinessInput(0x12345678u, 42);
    CollectionAssert.AreEqual(
        new byte[] { 0x78, 0x56, 0x34, 0x12, 42, 0, 0, 0 }, bytes);
    Assert.IsTrue(RouteCProtocolCodec.TryDecodeBusinessInput(
        bytes, out uint raw, out int frame));
    Assert.AreEqual(0x12345678u, raw);
    Assert.AreEqual(42, frame);
}
```

- [ ] **Step 6: Add KCP Conv parser tests**

`RouteCKcpHeader.TryReadConversation` reads bytes 0..3 little-endian only when payload length is at least 24. Test zero, maximum uint, truncation, and a payload containing a different outer SessionID.

## 5. Task 3: Implement the one-thread KCP adapter and timing diagnostics

**Files:** `RouteCKcpSettings.cs`, `RouteCKcpSession.cs`, clocks, `RouteCKcpSessionTests.cs`.

- [ ] **Step 1: Add RED settings tests**

Accept only interval values `1`, `5`, `10`, `20`. Configure:

```csharp
kcp.SetMtu(RouteCProtocolConstants.KcpMtu);
kcp.SetWindowSize(32u, 128u);
kcp.SetNoDelay(1u, (uint)IntervalMs, 2, false);
```

Reject every other interval with `ArgumentOutOfRangeException`. Record the effective deadline returned after the first `Update` so tests prove requested and effective values agree.

- [ ] **Step 2: Define the KCP adapter surface**

```csharp
public sealed class RouteCKcpSession
{
    public RouteCKcpSession(uint conv, RouteCKcpSettings settings,
                            Action<byte[], int> output);
    public uint Conversation { get; }
    public int RequestedIntervalMs { get; }
    public uint NextUpdateAt(uint nowMs);
    public int SendBusinessInput(uint raw, int frameID);
    public int InputDatagram(byte[] payload, int offset, int count);
    public bool TryReceiveBusinessInput(out uint raw, out int frameID);
    public void Update(uint nowMs, long workerTimestamp);
    public RouteCKcpDiagnosticsSnapshot SnapshotDiagnostics();
}
```

The adapter is not thread-safe by design. Capture the creating worker managed-thread ID and throw `InvalidOperationException` if any KCP method is called from another thread. Tests may opt into a deterministic owner ID through an internal constructor.

- [ ] **Step 3: Add RED message and retransmission tests**

Connect two `RouteCKcpSession` instances through in-memory output queues. Verify exact 8-byte delivery, idempotent KCP duplicate handling, loss of the first data datagram followed by retransmission, and loss of the first ACK followed by duplicate data/ACK recovery.

- [ ] **Step 4: Implement adapter and diagnostics**

Diagnostics include requested interval, effective Check delta, last/mean/max actual update gap, last/mean/max wakeup error, Update count, output datagrams/bytes, input datagrams/bytes, Receive count, WaitSnd high water and nonzero Input error count. Use integer ticks and invariant formatting; do not mutate KCP timing based on diagnostics.

- [ ] **Step 5: Verify focused GREEN at 1/5/10/20ms**

For each interval, advance virtual time one millisecond at a time but call Update only at `Check` deadlines. Assert observed flush deadlines include the requested interval and never silently clamp 1 or 5 to 10.

## 6. Task 4: Implement immutable outbound history and resume safety queries

**Files:** `ResumeReadiness.cs`, `OutboundActualHistory.cs`, `WorldSnapshotStore.cs`, `FrameSyncCoordinator.cs`, related tests.

- [ ] **Step 1: Add RED outbound history tests**

```csharp
[Test]
public void Record_SameFrameDifferentRaw_ReturnsConflictWithoutOverwrite()
{
    var history = new OutboundActualHistory(4);
    Assert.AreEqual(OutboundHistoryDisposition.Accepted,
        history.Record(7, 0x100u));
    Assert.AreEqual(OutboundHistoryDisposition.ConflictingDuplicate,
        history.Record(7, 0x200u));
    Assert.IsTrue(history.TryGet(7, out uint raw));
    Assert.AreEqual(0x100u, raw);
}
```

Test same/same idempotence, 256-frame ring eviction, contiguous range enumeration, missing-range rejection, negative frame rejection, and immutable byte reproduction.

- [ ] **Step 2: Expose snapshot readable bounds**

Add `EarliestReadableFrame` and `LatestReadableFrame` to `WorldSnapshotStore`. Track capacity explicitly. Tests cover wrap, truncate-after, rewrite, and initial empty state.

- [ ] **Step 3: Expose the exact safe resume floor**

Add to `FrameSyncCoordinator`:

```csharp
public int EarliestRecoverableCanonicalFrame
{
    get
    {
        int ledgerFloor = FirstRetainedFrame;
        int snapshotFloor = _predictedSnapshots.EarliestReadableFrame < 0
            ? StartFrame
            : _predictedSnapshots.EarliestReadableFrame + 1;
        return Math.Max(ledgerFloor, snapshotFloor);
    }
}
```

Add tests proving frame 0 uses the retained initial world, a correction at F requires a snapshot at F-1, ring overwrite advances the floor, and Ledger prune can advance the floor further.

- [ ] **Step 4: Define `ResumeReadiness`**

The immutable struct validates `LastContiguousRemoteFrameID >= -1`, `EarliestRecoverableCanonicalFrame >= 0`, and `LatestLocalFrameID >= -1`. GameController builds it only from coordinator/Ledger-derived state plus the latest locally submitted frame; it does not inspect KCP internals.

## 7. Task 5: Build the pure client protocol state machine

**Files:** `IFrameTransportClient.cs`, `NetworkTransportEvent.cs`, pure portions of `KcpUdpClientTransport.cs`, `KcpUdpClientStateMachineTests.cs`.

- [ ] **Step 1: Define the transport boundary**

```csharp
public interface IFrameTransportClient : IDisposable
{
    NetworkSessionState State { get; }
    bool IsRunning { get; }
    bool HasStartedSession { get; }
    int LocalPlayerIndex { get; }
    int LatestRemoteFrameID { get; }
    void Start(string host, int port);
    bool TryEnqueueLocalInput(uint raw, int localFrameID);
    bool TryDequeueRemoteInput(out NetworkPacketArrival arrival);
    bool TryDequeueEvent(out NetworkTransportEvent transportEvent);
    void SubmitResumeReadiness(in ResumeReadiness readiness);
    void RequestStop();
    bool WaitForStop(int millisecondsTimeout);
}
```

No method exposes KCP, Conv, Token, Endpoint, UDP bytes or mutable queues.

- [ ] **Step 2: Add RED initial-handshake tests**

Use fixed injected nonces/random values. Cover Hello retry at 250ms, Welcome nonce mismatch drop, duplicate Welcome idempotence, Ready retry, duplicate Start idempotence, and transition to Running only on a matching Session/Generation Start.

- [ ] **Step 3: Add RED reconnect tests**

Cover timeout to Reconnecting, new nonce creation once, duplicate Welcome for the same reconnect nonce, immediate old Generation/Conv rejection, token secrecy in diagnostics, and transition to Resuming rather than Running when `ResumeRequired=1`.

- [ ] **Step 4: Implement state transitions**

Use explicit switch-based transitions. Every transition publishes one `NetworkTransportEvent` with state, reason, Session fingerprint and Generation, never the ReconnectToken. Unexpected but well-formed control messages are ignored and counted; they do not throw on the worker.

- [ ] **Step 5: Add heartbeat/timeout tests**

Business/control traffic suppresses Heartbeat until 1000ms of silence. Heartbeat refreshes valid activity. Disconnect only publishes an advisory event. At 3000ms without valid traffic, state leaves Running; at 5000ms since last valid traffic without successful resume, state becomes Terminated.

## 8. Task 6: Implement the live client worker, TCP fallback, and Unity facade

**Files:** live `KcpUdpClientTransport.cs`, `TcpClientTransport.cs`, `NetworkClient.cs`, `NetworkConfig.cs`, `GameController.cs`, ownership tests.

- [ ] **Step 1: Add RED reflection ownership tests**

Require `NetworkClient` to contain no `TcpClient`, `NetworkStream`, `Socket`, or `kcp2k.Kcp` fields. Require exactly one `IFrameTransportClient` field and exactly one `ConcurrentQueue<NetworkPacketArrival>` inside each concrete transport, not inside GameController.

- [ ] **Step 2: Implement KCP worker wait loop**

The worker owns one nonblocking UDP Socket. Each iteration computes the earliest handshake/heartbeat/KCP deadline, calls `Socket.Select` with that bounded timeout, drains at most 64 datagrams, processes player-independent client state, runs due KCP Update, drains at most 64 complete KCP messages, and publishes a diagnostics snapshot. It never calls `Thread.Sleep(1)` and never reads `Time.deltaTime`.

- [ ] **Step 3: Implement bounded stop**

`RequestStop` only sets a volatile flag. `WaitForStop` joins with `InitialKcpIntervalMs + 250ms` in normal conditions and reports a fault event on timeout. The worker releases Socket and KCP in `finally`. Main thread never closes the worker-owned Socket.

- [ ] **Step 4: Move TCP to the same queue boundary**

`TcpClientTransport` owns `TcpClient`, `NetworkStream`, both reads and writes on its worker. Preserve `NoDelay=true`, the server player-index byte, exact 8-byte stream framing and current P2-E behavior. This is a fallback/control adapter; do not apply UDP Session/Generation/Conv semantics to TCP.

- [ ] **Step 5: Convert `NetworkClient` into a facade**

`Connect` creates the configured concrete transport and returns immediately. Keep existing public `SendInput` and `TryGetRemoteInput` overloads as wrappers so callers remain source-compatible. `IsConnected` means transport Running, not merely Socket-created. `HasStartedSession` remains true in Running, Reconnecting and Resuming after the first Start, and becomes false only at Terminated/Disconnected. `OnDestroy` requests and waits for bounded stop.

- [ ] **Step 6: Make GameController start asynchronously**

Replace the immediate `Connect`/`IsConnected` gate with a pending network-start state. In `Update`, consume transport events; call `_frameEngine.StartEngine()` exactly once on the first Running/Start. While initial handshake is pending, do not advance logic. After Start, use `HasStartedSession`—not `IsConnected`—to select the network input path, so Reconnecting/Resuming continues recording local Ledger Actuals, predicting missing remote input and storing snapshots without falling into local two-player mode. On ResumeRejected or Terminated, pause and emit an explicit match-terminal fault.

- [ ] **Step 7: Verify existing Ledger and presentation regressions**

Run `NetworkLedgerWiringTests`, `FrameInputLedgerTests`, coordinator tests, P2-C/P2-D ownership tests, P2-E playback tests, highlight tests and terminal-flow tests. The only new mutable input collection is the bounded immutable transport send history.

## 9. Task 7: Refactor the server entry point and preserve TCP mode

**Files:** `ServerOptions.cs`, `TcpRelayServer.cs`, `NetworkServer.cs`, csproj, existing server scripts.

- [ ] **Step 1: Add RED option tests**

`--transport kcp` is the default for explicit command-line runs; `--transport tcp` selects the preserved server. `--kcp-interval-ms` accepts only 1/5/10/20. Interactive legacy `0/1/2` continues to select TCP with current P2-E delay profiles so existing demos do not silently change semantics.

- [ ] **Step 2: Extract current TCP code without behavior change**

Move listener, player barrier, receive threads, deterministic packet scheduler and TCP broadcast into `TcpRelayServer`. `Program.Main` parses options, constructs exactly one server implementation, runs it, and disposes it.

- [ ] **Step 3: Run existing TCP gates**

Build net48, run `NetworkServerLabTests.ps1`, `NetworkServerBarrierTests.ps1`, and the existing timing probe with explicit TCP selection. Require byte-for-byte 8-byte relay compatibility and unchanged recovered-loss/HOL labels.

## 10. Task 8: Implement the one-worker UDP/KCP server and idempotent handshake

**Files:** KCP server files, crypto source, diagnostics, protocol script.

- [ ] **Step 1: Add RED session-router tests**

Through reflection script and shared codec, cover unknown Session, stale Generation, wrong Endpoint, truncated KCP header, wrong Conv, correct route, and separate counters for every drop reason. Correct routing requires all of SessionID+Generation+Endpoint+inner Conv.

- [ ] **Step 2: Add RED first-handshake idempotence tests**

Given a fixed crypto source, the same Endpoint+Nonce produces byte-identical Welcome and one Session. A new nonce from the same Endpoint may allocate the other free player only; a third active allocation is rejected. Conv is nonzero and distinct across both active Sessions.

- [ ] **Step 3: Add RED reconnect generation tests**

The first valid SessionID+old Token+new Nonce atomically increments Generation and changes Conv, Token and Endpoint. The same new Nonce retry returns the same Welcome. Old Token, old Generation, old Conv and old Endpoint all fail immediately after the switch.

- [ ] **Step 4: Implement crypto values**

Use `RandomNumberGenerator.Create().GetBytes(byte[])` for net48 compatibility. SessionID and Nonce are 16 bytes, Token 32 bytes, Conv four bytes interpreted as uint; retry on zero or collision. Never call `System.Random` for these values.

- [ ] **Step 5: Implement one Socket/one worker loop**

The UDP Socket, both KCP sessions and all session state live on the worker thread. Use `Socket.Select` until readable or the minimum of session deadlines. Process same-deadline sessions in player order 0 then 1. Enforce 64 datagrams, 64 decoded messages per Session and 2ms live slice budgets; count every budget exhaustion.

- [ ] **Step 6: Implement decode-and-resend relay**

For each decoded business input, validate nonnegative frame, insert into sender history, and call the peer Session's `SendBusinessInput`. Never reuse or forward the sender's KCP payload. Add a test where sender/receiver Conv differ and prove the receiver output KCP header contains the receiver Conv.

- [ ] **Step 7: Add Start barrier and timeout behavior**

Both Ready messages are required before one stable Start payload is generated and resent idempotently. Valid traffic updates last activity. Disconnect is advisory. Timeout at 3000ms changes state; Session memory remains until 5000ms grace expires, then both players receive a terminal reason and the match closes.

- [ ] **Step 8: Run pure server protocol script GREEN**

Require PASS for routing, handshake, generation switch, player-order fairness, per-round budgets, decode-and-resend Conv separation and timeout semantics.

## 11. Task 9: Implement bounded in-match resume

**Files:** client/server resume state, histories, `KcpResumeProtocolTests.cs`, server script.

- [ ] **Step 1: Add RED history disposition tests**

Server history uses exactly `PlayerIndex + FrameID`. Assert Accepted, IdempotentDuplicate, ConflictingDuplicate, HistoryUnavailable and CapacityExceeded. ConflictingDuplicate must preserve the first raw and mark the match terminal.

- [ ] **Step 2: Add RED boundary-exchange tests**

After reconnect Welcome, the reconnecting client sends Ready with the three fields. Server sends ResumeProbe to the online peer; Heartbeat alone never satisfies the probe. A ResumeState with the wrong AttemptID, Session or Generation is ignored and counted.

- [ ] **Step 3: Add RED frozen-range tests**

Construct a disconnect where P0 has local Actual through 120, remote contiguous through 110, P1 has local through 123, and server histories cover 100..123. Assert ResumeAccepted freezes exact closed ranges. Add frame 124 after acceptance and assert it enters live tail, not the frozen batch.

- [ ] **Step 4: Implement safety validation**

Reject when grace expired, a required server/client history frame is missing, the oldest correction is before either `EarliestRecoverableCanonicalFrame`, a range exceeds 256, or a second generation change occurs during Resuming. Emit one ResumeRejected to each active endpoint and transition both Sessions to Terminated.

- [ ] **Step 5: Implement upload/replay/complete**

Reconnect client uploads the frozen local range in ascending FrameID. Server validates/deduplicates and sends it through the online peer's KCP. Server replays remote Actual to the reconnecting KCP in ascending FrameID. Each client sends ResumeComplete with uploaded and contiguous received upper bounds. Only after both satisfy frozen bounds does the server release ascending live tail and return both to Running.

- [ ] **Step 6: Prove Ledger remains the only mutable truth**

Reflection tests reject a second input dictionary in GameController/NetworkClient. End-to-end tests deliver replayed Actual through `NetworkPacketArrival` and existing `RecordLedgerActual`; they do not write world state or snapshots from network code.

## 12. Task 10: Build the deterministic UDP datagram fault lab

**Files:** virtual clock/link/profile and deterministic tests.

- [ ] **Step 1: Define the datagram profile**

```csharp
public readonly struct UdpDatagramFaultProfile
{
    public UdpDatagramFaultProfile(
        int delayMs, int jitterMs, int dropPercent,
        int reorderPercent, int reorderExtraDelayMs,
        int duplicatePercent, uint seed);
}
```

Validate nonnegative delays, percentages 0..100 and a bounded maximum delay. Stable integer mixing uses independent salts for delay, drop, reorder and duplicate. Never use wall time, `System.Random` or thread order.

- [ ] **Step 2: Add RED deterministic trace tests**

Same seed/script produces byte-identical decision traces; selected different seeds differ within 512 data/ACK datagrams. Trace identity includes direction, sender Session fingerprint, datagram sequence, KCP command/sequence when safely parseable, and decisions, but excludes live timestamps.

- [ ] **Step 3: Implement the virtual link**

The link accepts complete UDP envelopes from client/server output, applies decisions to the whole datagram, and releases immutable copies by `(dueTime, direction, sequence, copyIndex)`. Drop removes the datagram permanently; it never schedules a synthetic recovery event.

- [ ] **Step 4: Add the required scenario matrix**

Cover clean, fixed delay, jitter, data drop, ACK drop, reorder, duplicate, asymmetric directions, Hello/Welcome/Ready/Start loss, reconnect Welcome loss, and resume-control loss. Every scenario must terminate deterministically as Running or ResumeRejected, never hang.

- [ ] **Step 5: Prove KCP recovery and final convergence**

For accepted scenarios, require all 8-byte Actuals eventually arrive in ascending KCP message order, both Ledgers confirm through the terminal frame, and terminal Confirmed/Predicted `WorldHash` values converge. Do not claim intermediate predictions never diverged.

- [ ] **Step 6: Keep TCP fault semantics separate**

Run existing `DeterministicNetworkFaultModelTests` unchanged. New UDP tests must never use `RecoveredLossPercent` or the TCP sender HOL barrier. Report headings explicitly say `udp-datagram-drop` versus `tcp-recovered-loss-hol`.

## 13. Task 11: Build live Socket probes and the interval matrix

**Files:** `KcpLiveSocketProbe.ps1`, `KcpIntervalMatrix.ps1`, diagnostics outputs.

- [ ] **Step 1: Implement a two-client live probe**

For each matrix value, assign PowerShell `$IntervalMs` to `1`, `5`, `10`, or `20`, then start the server with `--transport kcp --kcp-interval-ms $IntervalMs`. Start two real UDP clients, complete Hello/Welcome/Ready/Start, send at least 900 8-byte inputs per client at the project's logical cadence, then stop cleanly. Record send/peer-receive timestamps, KCP outputs, bytes, retransmits, WaitSnd, worker CPU time, actual Update gaps and wakeup errors.

- [ ] **Step 2: Add real reconnect cases**

Change one client's UDP Endpoint, authenticate with old Token/new Nonce, resume a recoverable gap, and verify both peers return Running. Run separate grace-expired and history-expired cases and require ResumeRejected on both peers.

- [ ] **Step 3: Run 1/5/10/20ms three times each**

Use the same input script, duration, fault seed and machine. Save one JSONL trace per run and a summary JSON containing p50/p95/p99 relay latency, datagrams/bytes, retransmits, CPU, requested/effective/actual interval and p50/p95/p99/max wakeup error.

- [ ] **Step 4: Prove there is no fake 1/5ms result**

For 1ms and 5ms runs, require the effective core interval field to equal the requested value and the actual Update distribution to contain corresponding sub-10ms calls. A machine whose scheduler cannot realize the interval may report high wakeup error, but must not relabel 10ms as 1ms.

- [ ] **Step 5: Run TCP control cases**

Run the same business input script through `--transport tcp` and existing P2-E fault profiles. Label results as TCP recovered-loss/HOL/application faults. Do not compare a TCP recovery delay as though it were the same event as UDP datagram drop.

- [ ] **Step 6: Write the default-interval decision artifact**

Create `docs/verification/2026-08-17-p2f-kcp-interval-decision.md` with all candidates, correctness gates, Pareto analysis and chosen default. If no candidate passes correctness and live stability, P2-F remains incomplete; do not select a default by preference.

## 14. Task 12: Full verification, delivery build, and truthful documentation

- [ ] **Step 1: Run focused Unity fixtures**

Run vendor, codec, KCP adapter, history, client state, resume, deterministic UDP, ownership, NetworkLedgerWiring, coordinator and runtime diagnostics fixtures. Preserve fresh XML and logs.

- [ ] **Step 2: Run full Unity gates**

Run full EditMode with fresh XML and zero failures/skips, standalone import/compile with zero C# errors, and Windows x64 build. Unity licensing failure before test discovery is infrastructure failure, not a passing result.

- [ ] **Step 3: Run all server gates**

Require net48 Release build, existing TCP lab/barrier PASS, KCP pure protocol PASS, live clean/reconnect/reject PASS, and interval matrix evidence complete.

- [ ] **Step 4: Rebuild the versioned server intentionally**

Copy only the successful Release `NetworkServer.exe` to `Project/NetworkServer/NetworkServer.exe`. Do not version `bin/` or `obj/`.

- [ ] **Step 5: Inspect scope and worktree safety**

Run `git diff --check`, list files changed since the P2-F start timestamp, and inspect every change. Reject direct KCP access from Unity main thread, direct KCP datagram forwarding, Token logging, world-state networking, Protobuf, ECS work, or unrelated cleanup.

- [ ] **Step 6: Update documentation with verified outcomes only**

Update:

- `docs/architecture/route-c-frame-sync.md`
- `docs/architecture/streetball2-minimal-frame-sync-v2.md`
- `Project/路线规划_街篮帧同步最小实现.md`
- `docs/demo/windows-build-and-demo.md`

Record the chosen interval as Route C measured evidence, keep StreetBall2's source call separate, link LICENSE/NOTICE/PATCHES, and clearly distinguish virtual-link from live evidence.

- [ ] **Step 7: Prepare manual acceptance instructions**

Provide帅老大 exact commands for KCP default, TCP fallback, fixed test scenario, reconnect case and diagnostics locations. Do not mark P2-F finally accepted until帅老大 reports the manual result.

## 15. Evidence names

```text
TestResults-p2f-vendor-green.xml
TestResults-p2f-codec-green.xml
TestResults-p2f-kcp-session-green.xml
TestResults-p2f-client-state-green.xml
TestResults-p2f-resume-green.xml
TestResults-p2f-virtual-link-green.xml
TestResults-p2f-ownership-green.xml
TestResults-p2f-focused.xml
TestResults-p2f-full.xml
p2f-compile.log
p2f-build.log
p2f-server-build.log
p2f-tcp-lab.log
p2f-tcp-barrier.log
p2f-kcp-protocol.log
p2f-kcp-live-clean.log
p2f-kcp-live-reconnect.log
p2f-kcp-live-resume-rejected.log
p2f-kcp-interval-{1,5,10,20}ms-run{1,2,3}.jsonl
p2f-kcp-interval-summary.json
p2f-tcp-control-summary.json
```

## 16. Completion boundary

Automated completion requires every Task checkbox above, fresh evidence, and truthful documentation. Final acceptance remains a separate manual decision by帅老大. No commit or push is part of this plan because repository policy reserves integration and push to帅老大.

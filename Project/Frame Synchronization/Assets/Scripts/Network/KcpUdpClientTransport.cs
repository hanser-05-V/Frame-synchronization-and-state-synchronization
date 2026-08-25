using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace FrameSyncDemo
{
    public sealed class KcpUdpClientTransport : IFrameTransportClient
    {
        private readonly ConcurrentQueue<PendingLocalInput> _localInputs =
            new ConcurrentQueue<PendingLocalInput>();
        private readonly ConcurrentQueue<NetworkPacketArrival> _remoteInputs =
            new ConcurrentQueue<NetworkPacketArrival>();
        private readonly ConcurrentQueue<NetworkTransportEvent> _events =
            new ConcurrentQueue<NetworkTransportEvent>();
        private readonly ConcurrentQueue<RouteCProtocolMessage> _protocolSends =
            new ConcurrentQueue<RouteCProtocolMessage>();
        private readonly ConcurrentQueue<ResumeReadiness> _readinessCommands =
            new ConcurrentQueue<ResumeReadiness>();
        private readonly IMonotonicClock _clock;
        private readonly Func<byte[]> _nonceFactory;
        private readonly RouteCKcpSettings _settings;

        private ClientStatusSnapshot _status =
            ClientStatusSnapshot.Disconnected;
        private RouteCKcpDiagnosticsSnapshot _diagnostics;
        private KcpUdpClientWorkerDiagnosticsSnapshot _workerDiagnostics =
            KcpUdpClientWorkerDiagnosticsSnapshot.Empty;
        private WorkerRoundAccumulator _activeRound;
        private Thread _worker;
        private Socket _socket;
        private volatile bool _stopRequested;
        private int _started;
        private int _pendingLocalInputCount;
        private int _stopTimeoutReported;
        private bool _disposed;

        public KcpUdpClientTransport(
            IMonotonicClock clock,
            Func<byte[]> nonceFactory)
            : this(
                clock,
                nonceFactory,
                new RouteCKcpSettings(
                    RouteCProtocolConstants.InitialKcpIntervalMs))
        {
        }

        public KcpUdpClientTransport(
            IMonotonicClock clock,
            Func<byte[]> nonceFactory,
            RouteCKcpSettings settings)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _nonceFactory = nonceFactory ??
                throw new ArgumentNullException(nameof(nonceFactory));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public NetworkSessionState State =>
            Volatile.Read(ref _status).State;

        public bool IsRunning => State == NetworkSessionState.Running;

        public bool HasStartedSession =>
            Volatile.Read(ref _status).HasStartedSession;

        public int LocalPlayerIndex =>
            Volatile.Read(ref _status).LocalPlayerIndex;

        public int LatestRemoteFrameID =>
            Volatile.Read(ref _status).LatestRemoteFrameID;

        public RouteCKcpDiagnosticsSnapshot Diagnostics =>
            Volatile.Read(ref _diagnostics);

        public KcpUdpClientWorkerDiagnosticsSnapshot WorkerDiagnostics =>
            Volatile.Read(ref _workerDiagnostics);

        public void Start(string host, int port)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("Host is required.", nameof(host));
            if (port < 1 || port > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(port));
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
                throw new InvalidOperationException("Transport has already started.");

            _worker = new Thread(() => WorkerMain(host, port))
            {
                IsBackground = true,
                Name = "RouteC KCP Client"
            };
            _worker.Start();
        }

        public bool TryEnqueueLocalInput(uint raw, int localFrameID)
        {
            ThrowIfDisposed();
            if (!HasStartedSession || localFrameID < 0)
                return false;

            int count = Interlocked.Increment(ref _pendingLocalInputCount);
            if (count > RouteCProtocolConstants.InputHistoryCapacity)
            {
                Interlocked.Decrement(ref _pendingLocalInputCount);
                return false;
            }

            _localInputs.Enqueue(new PendingLocalInput(raw, localFrameID));
            return true;
        }

        public bool TryDequeueRemoteInput(out NetworkPacketArrival arrival)
        {
            return _remoteInputs.TryDequeue(out arrival);
        }

        public bool TryDequeueEvent(out NetworkTransportEvent transportEvent)
        {
            return _events.TryDequeue(out transportEvent);
        }

        public void SubmitResumeReadiness(in ResumeReadiness readiness)
        {
            ThrowIfDisposed();
            _readinessCommands.Enqueue(readiness);
        }

        public void RequestStop()
        {
            Volatile.Write(ref _stopRequested, true);
        }

        public bool WaitForStop(int millisecondsTimeout)
        {
            if (millisecondsTimeout < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));

            Thread worker = Volatile.Read(ref _worker);
            if (worker == null)
                return true;

            bool stopped = worker.Join(millisecondsTimeout);
            if (!stopped &&
                Interlocked.Exchange(ref _stopTimeoutReported, 1) == 0)
            {
                ClientStatusSnapshot status = Volatile.Read(ref _status);
                _events.Enqueue(new NetworkTransportEvent(
                    status.State,
                    NetworkTransportEventReason.WorkerStopTimedOut,
                    "none",
                    0u,
                    _clock.Milliseconds));
            }
            return stopped;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            RequestStop();
            WaitForStop(
                _settings.IntervalMs + NetworkConfig.WorkerStopMarginMs);
            _disposed = true;
        }

        private void WorkerMain(string host, int port)
        {
            KcpUdpClientStateMachine stateMachine = null;
            RouteCKcpSession kcp = null;
            var history = new OutboundActualHistory(
                RouteCProtocolConstants.InputHistoryCapacity);
            var resumeCoordinator = new KcpClientResumeCoordinator(history);
            long receiveSequence = -1;
            int latestRemoteFrameID = -1;
            var workerDiagnostics = new WorkerDiagnosticsAccumulator();
            var pendingEvents = new Queue<NetworkTransportEvent>();

            try
            {
                IPEndPoint endpoint = ResolveEndpoint(host, port);
                _socket = new Socket(
                    endpoint.AddressFamily,
                    SocketType.Dgram,
                    ProtocolType.Udp);
                _socket.Blocking = false;
                _socket.Connect(endpoint);

                stateMachine = new KcpUdpClientStateMachine(
                    _clock,
                    _nonceFactory,
                    _protocolSends.Enqueue,
                    pendingEvents.Enqueue);
                stateMachine.Start();
                PublishStatusAndFlushEvents(
                    stateMachine,
                    latestRemoteFrameID,
                    pendingEvents);

                while (!Volatile.Read(ref _stopRequested))
                {
                    _activeRound = workerDiagnostics.BeginRound();
                    ApplyReadinessCommands(stateMachine);
                    DrainProtocolSends();
                    DrainLocalInputs(
                        stateMachine,
                        kcp,
                        history,
                        latestRemoteFrameID,
                        pendingEvents);

                    uint nowMs = _clock.Milliseconds;
                    uint deadline = stateMachine.NextActionAt(nowMs);
                    if (kcp != null)
                    {
                        deadline = EarlierDeadline(
                            nowMs,
                            deadline,
                            kcp.NextUpdateAt(nowMs));
                    }

                    WaitForSocket(deadline, nowMs);
                    if (Volatile.Read(ref _stopRequested))
                        break;

                    DrainDatagrams(
                        stateMachine,
                        ref kcp,
                        resumeCoordinator,
                        ref receiveSequence,
                        ref latestRemoteFrameID,
                        pendingEvents);
                    PumpResumeUpload(
                        stateMachine,
                        kcp,
                        resumeCoordinator);

                    stateMachine.Tick();
                    PublishStatusAndFlushEvents(
                        stateMachine,
                        latestRemoteFrameID,
                        pendingEvents);
                    ApplyReadinessCommands(stateMachine);
                    DrainProtocolSends();
                    if (stateMachine.State == NetworkSessionState.Terminated)
                    {
                        CompleteWorkerRound(workerDiagnostics);
                        break;
                    }

                    DrainLocalInputs(
                        stateMachine,
                        kcp,
                        history,
                        latestRemoteFrameID,
                        pendingEvents);

                    nowMs = _clock.Milliseconds;
                    if (kcp != null && IsDue(nowMs, kcp.NextUpdateAt(nowMs)))
                    {
                        kcp.Update(nowMs, _clock.Timestamp);
                    }

                    DrainKcpMessages(
                        kcp,
                        stateMachine,
                        resumeCoordinator,
                        ref receiveSequence,
                        ref latestRemoteFrameID);
                    if (kcp != null)
                        Volatile.Write(ref _diagnostics, kcp.SnapshotDiagnostics());
                    PublishStatusAndFlushEvents(
                        stateMachine,
                        latestRemoteFrameID,
                        pendingEvents);
                    CompleteWorkerRound(workerDiagnostics);
                }
            }
            catch (Exception)
            {
                if (stateMachine != null)
                {
                    PublishStatusAndFlushEvents(
                        stateMachine,
                        latestRemoteFrameID,
                        pendingEvents);
                }

                pendingEvents.Enqueue(new NetworkTransportEvent(
                    NetworkSessionState.Terminated,
                    NetworkTransportEventReason.WorkerFault,
                    "none",
                    0u,
                    _clock.Milliseconds));
                Volatile.Write(
                    ref _status,
                    new ClientStatusSnapshot(
                        NetworkSessionState.Terminated,
                        false,
                        -1,
                        latestRemoteFrameID));
                FlushWorkerEvents(pendingEvents);
            }
            finally
            {
                _activeRound = null;
                if (stateMachine != null &&
                    stateMachine.State != NetworkSessionState.Terminated)
                {
                    stateMachine.RequestStop();
                    PublishStatusAndFlushEvents(
                        stateMachine,
                        latestRemoteFrameID,
                        pendingEvents);
                }
                else
                    FlushWorkerEvents(pendingEvents);

                kcp = null;
                Socket socket = _socket;
                _socket = null;
                socket?.Dispose();
            }
        }

        private void ApplyReadinessCommands(
            KcpUdpClientStateMachine stateMachine)
        {
            while (_readinessCommands.TryDequeue(out ResumeReadiness readiness))
                stateMachine.SubmitResumeReadiness(in readiness);
        }

        private void DrainProtocolSends()
        {
            while (_protocolSends.TryDequeue(out RouteCProtocolMessage message))
            {
                byte[] datagram = RouteCProtocolCodec.Encode(
                    message.MessageType,
                    message.SessionId,
                    message.Generation,
                    message.Payload);
                _socket.Send(datagram);
                _activeRound?.RecordProtocolSend(message.MessageType);
            }
        }

        private void DrainLocalInputs(
            KcpUdpClientStateMachine stateMachine,
            RouteCKcpSession kcp,
            OutboundActualHistory history,
            int latestRemoteFrameID,
            Queue<NetworkTransportEvent> pendingEvents)
        {
            if (kcp == null || !stateMachine.HasStartedSession)
                return;

            int processed = 0;
            while (processed < RouteCProtocolConstants.MaximumMessagesPerSessionPerRound &&
                   _localInputs.TryDequeue(out PendingLocalInput input))
            {
                Interlocked.Decrement(ref _pendingLocalInputCount);
                OutboundHistoryDisposition disposition =
                    history.Record(input.FrameID, input.Raw);
                if (disposition == OutboundHistoryDisposition.ConflictingDuplicate ||
                    disposition == OutboundHistoryDisposition.HistoryUnavailable ||
                    disposition == OutboundHistoryDisposition.CapacityExceeded)
                {
                    PublishTerminalStatus(latestRemoteFrameID);
                    if (stateMachine.State == NetworkSessionState.Resuming)
                    {
                        stateMachine.RejectResumeLocally(
                            ResumeRejectedReason.UnsafeResume);
                    }
                    else
                    {
                        stateMachine.Terminate(
                            NetworkTransportEventReason.OutboundHistoryFault);
                    }
                    return;
                }

                if (kcp.SendBusinessInput(input.Raw, input.FrameID) == 0)
                {
                    _activeRound?.RecordBusinessInputAccepted();
                    stateMachine.NotifyBusinessSent();
                }
                processed++;
            }
        }

        private void WaitForSocket(uint deadline, uint nowMs)
        {
            uint delayMs = IsDue(nowMs, deadline)
                ? 0u
                : unchecked(deadline - nowMs);
            uint maximumStopResponsiveWaitMs =
                (uint)(NetworkConfig.WorkerStopMarginMs / 2);
            if (delayMs > maximumStopResponsiveWaitMs)
                delayMs = maximumStopResponsiveWaitMs;
            int timeoutMicroseconds = delayMs > int.MaxValue / 1000u
                ? int.MaxValue
                : (int)delayMs * 1000;
            var readable = new List<Socket>(1) { _socket };
            Socket.Select(readable, null, null, timeoutMicroseconds);
        }

        private void DrainDatagrams(
            KcpUdpClientStateMachine stateMachine,
            ref RouteCKcpSession kcp,
            KcpClientResumeCoordinator resumeCoordinator,
            ref long receiveSequence,
            ref int latestRemoteFrameID,
            Queue<NetworkTransportEvent> pendingEvents)
        {
            var buffer = new byte[RouteCProtocolConstants.MaximumDatagramSize];
            for (int index = 0;
                 index < RouteCProtocolConstants.MaximumDatagramsPerRound;
                 index++)
            {
                int count;
                try
                {
                    count = _socket.Receive(buffer);
                    _activeRound?.RecordDatagramDrained();
                }
                catch (SocketException exception)
                    when (IsTransientUdpReceiveLoss(exception))
                {
                    break;
                }

                if (!RouteCProtocolCodec.TryDecode(
                        buffer,
                        count,
                        out RouteCProtocolMessage message,
                        out _))
                {
                    continue;
                }

                if (message.MessageType == RouteCMessageType.KcpData)
                {
                    if (kcp != null && stateMachine.AcceptKcpData(message))
                    {
                        byte[] payload = message.Payload;
                        kcp.InputDatagram(payload, 0, payload.Length);
                    }
                    continue;
                }

                uint previousGeneration = stateMachine.Generation;
                uint previousConversation = stateMachine.Conversation;
                stateMachine.HandleIncoming(message);
                ApplyResumePlan(
                    stateMachine,
                    kcp,
                    resumeCoordinator);
                if (stateMachine.State == NetworkSessionState.Running &&
                    resumeCoordinator.IsActive &&
                    !resumeCoordinator.TryReleaseLocalTail(out _))
                {
                    stateMachine.Terminate(
                        NetworkTransportEventReason.OutboundHistoryFault);
                }
                PublishStatusAndFlushEvents(
                    stateMachine,
                    latestRemoteFrameID,
                    pendingEvents);
                if (stateMachine.Conversation != 0u &&
                    (kcp == null ||
                     stateMachine.Generation != previousGeneration ||
                     stateMachine.Conversation != previousConversation))
                {
                    kcp = CreateKcp(stateMachine);
                }
            }
        }

        private RouteCKcpSession CreateKcp(
            KcpUdpClientStateMachine stateMachine)
        {
            return new RouteCKcpSession(
                stateMachine.Conversation,
                _settings,
                (bytes, count) =>
                {
                    byte[] payload = new byte[count];
                    Buffer.BlockCopy(bytes, 0, payload, 0, count);
                    byte[] datagram = RouteCProtocolCodec.Encode(
                        RouteCMessageType.KcpData,
                        stateMachine.SessionId,
                        stateMachine.Generation,
                        payload);
                    _socket.Send(datagram);
                    _activeRound?.RecordKcpDataSend();
                });
        }

        private void CompleteWorkerRound(
            WorkerDiagnosticsAccumulator diagnostics)
        {
            WorkerRoundAccumulator round = _activeRound;
            if (round == null)
                return;

            Volatile.Write(
                ref _workerDiagnostics,
                diagnostics.CompleteRound(round));
            _activeRound = null;
        }

        private void DrainKcpMessages(
            RouteCKcpSession kcp,
            KcpUdpClientStateMachine stateMachine,
            KcpClientResumeCoordinator resumeCoordinator,
            ref long receiveSequence,
            ref int latestRemoteFrameID)
        {
            if (kcp == null)
                return;

            for (int index = 0;
                 index < RouteCProtocolConstants.MaximumMessagesPerSessionPerRound &&
                 kcp.TryReceiveBusinessInput(out uint raw, out int frameID);
                 index++)
            {
                if (frameID < 0)
                    continue;

                receiveSequence++;
                latestRemoteFrameID = frameID;
                _remoteInputs.Enqueue(new NetworkPacketArrival(
                    raw,
                    frameID,
                    receiveSequence,
                    _clock.Timestamp));
                if (stateMachine.State == NetworkSessionState.Resuming ||
                    resumeCoordinator.IsActive)
                {
                    resumeCoordinator.ObserveRemoteFrame(frameID);
                    if (resumeCoordinator.IsActive &&
                        resumeCoordinator.IsCompleteReady)
                    {
                        stateMachine.SubmitResumeProgress(
                            resumeCoordinator.UploadedLocalThrough,
                            resumeCoordinator.ReceivedRemoteThrough);
                    }
                }
            }
        }

        private static void ApplyResumePlan(
            KcpUdpClientStateMachine stateMachine,
            RouteCKcpSession kcp,
            KcpClientResumeCoordinator resumeCoordinator)
        {
            if (!stateMachine.TryTakeResumePlan(out KcpClientResumePlan plan))
                return;
            if (kcp == null ||
                !resumeCoordinator.TryBegin(plan, out _))
            {
                stateMachine.RejectResumeLocally(
                    ResumeRejectedReason.UnsafeResume);
                return;
            }

            if (resumeCoordinator.PendingUploadCount == 0)
            {
                resumeCoordinator.MarkUploadQueued();
                SubmitReadyResumeProgress(stateMachine, resumeCoordinator);
            }
        }

        private static void PumpResumeUpload(
            KcpUdpClientStateMachine stateMachine,
            RouteCKcpSession kcp,
            KcpClientResumeCoordinator resumeCoordinator)
        {
            if (kcp == null ||
                !resumeCoordinator.IsActive ||
                resumeCoordinator.PendingUploadCount == 0)
            {
                return;
            }

            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            long budgetTicks =
                RouteCProtocolConstants.MaximumWorkerSliceMs *
                System.Diagnostics.Stopwatch.Frequency /
                1000L;
            int sentCount = 0;
            while (sentCount <
                       RouteCProtocolConstants.MaximumMessagesPerSessionPerRound &&
                   System.Diagnostics.Stopwatch.GetTimestamp() - startedAt <
                       budgetTicks &&
                   resumeCoordinator.TryTakeNextUploadPayload(
                       out byte[] payload))
            {
                if (!RouteCProtocolCodec.TryDecodeBusinessInput(
                        payload,
                        out uint raw,
                        out int frameID) ||
                    kcp.SendBusinessInput(raw, frameID) != 0)
                {
                    stateMachine.RejectResumeLocally(
                        ResumeRejectedReason.UnsafeResume);
                    return;
                }
                sentCount++;
            }

            if (resumeCoordinator.PendingUploadCount != 0)
                return;

            resumeCoordinator.MarkUploadQueued();
            SubmitReadyResumeProgress(stateMachine, resumeCoordinator);
        }

        private static void SubmitReadyResumeProgress(
            KcpUdpClientStateMachine stateMachine,
            KcpClientResumeCoordinator resumeCoordinator)
        {
            if (resumeCoordinator.IsCompleteReady)
            {
                stateMachine.SubmitResumeProgress(
                    resumeCoordinator.UploadedLocalThrough,
                    resumeCoordinator.ReceivedRemoteThrough);
            }
        }

        private void PublishStatus(
            KcpUdpClientStateMachine stateMachine,
            int latestRemoteFrameID)
        {
            Volatile.Write(
                ref _status,
                new ClientStatusSnapshot(
                    stateMachine.State,
                    stateMachine.HasStartedSession,
                    stateMachine.LocalPlayerIndex,
                    latestRemoteFrameID));
        }

        private void PublishTerminalStatus(int latestRemoteFrameID)
        {
            Volatile.Write(
                ref _status,
                new ClientStatusSnapshot(
                    NetworkSessionState.Terminated,
                    false,
                    -1,
                    latestRemoteFrameID));
        }

        private void PublishStatusAndFlushEvents(
            KcpUdpClientStateMachine stateMachine,
            int latestRemoteFrameID,
            Queue<NetworkTransportEvent> pendingEvents)
        {
            PublishStatus(stateMachine, latestRemoteFrameID);
            FlushWorkerEvents(pendingEvents);
        }

        private void FlushWorkerEvents(
            Queue<NetworkTransportEvent> pendingEvents)
        {
            while (pendingEvents.Count > 0)
                _events.Enqueue(pendingEvents.Dequeue());
        }

        private static IPEndPoint ResolveEndpoint(string host, int port)
        {
            if (IPAddress.TryParse(host, out IPAddress address))
                return new IPEndPoint(address, port);

            IPAddress[] addresses = Dns.GetHostAddresses(host);
            for (int index = 0; index < addresses.Length; index++)
            {
                if (addresses[index].AddressFamily == AddressFamily.InterNetwork)
                    return new IPEndPoint(addresses[index], port);
            }

            throw new SocketException((int)SocketError.HostNotFound);
        }

        private static uint EarlierDeadline(
            uint nowMs,
            uint left,
            uint right)
        {
            return unchecked(left - nowMs) <= unchecked(right - nowMs)
                ? left
                : right;
        }

        private static bool IsDue(uint nowMs, uint deadline)
        {
            return unchecked((int)(nowMs - deadline)) >= 0;
        }

        private static bool IsTransientUdpReceiveLoss(
            SocketException exception)
        {
            return exception.SocketErrorCode == SocketError.WouldBlock ||
                   exception.SocketErrorCode == SocketError.ConnectionReset ||
                   exception.SocketErrorCode == SocketError.ConnectionRefused;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(KcpUdpClientTransport));
        }

        private sealed class ClientStatusSnapshot
        {
            public static readonly ClientStatusSnapshot Disconnected =
                new ClientStatusSnapshot(
                    NetworkSessionState.Disconnected,
                    false,
                    -1,
                    -1);

            public ClientStatusSnapshot(
                NetworkSessionState state,
                bool hasStartedSession,
                int localPlayerIndex,
                int latestRemoteFrameID)
            {
                State = state;
                HasStartedSession = hasStartedSession;
                LocalPlayerIndex = localPlayerIndex;
                LatestRemoteFrameID = latestRemoteFrameID;
            }

            public NetworkSessionState State { get; }
            public bool HasStartedSession { get; }
            public int LocalPlayerIndex { get; }
            public int LatestRemoteFrameID { get; }
        }

        private sealed class WorkerDiagnosticsAccumulator
        {
            private long _completedRoundCount;
            private long _nonEmptyDatagramRoundCount;
            private long _totalDatagramsDrained;
            private int _maximumDatagramsDrainedPerRound;
            private long _lastReadySendRound;
            private int _lastReadySendOrder;
            private long _lastKcpDataSendRound;
            private int _lastKcpDataSendOrder;
            private long _lastBusinessInputRound;

            public WorkerRoundAccumulator BeginRound()
            {
                return new WorkerRoundAccumulator(
                    _completedRoundCount + 1L);
            }

            public KcpUdpClientWorkerDiagnosticsSnapshot CompleteRound(
                WorkerRoundAccumulator round)
            {
                _completedRoundCount = round.Sequence;
                _totalDatagramsDrained += round.DatagramsDrained;
                if (round.DatagramsDrained > 0)
                    _nonEmptyDatagramRoundCount++;
                if (round.DatagramsDrained >
                    _maximumDatagramsDrainedPerRound)
                {
                    _maximumDatagramsDrainedPerRound =
                        round.DatagramsDrained;
                }

                if (round.ReadySendOrder > 0)
                {
                    _lastReadySendRound = round.Sequence;
                    _lastReadySendOrder = round.ReadySendOrder;
                }
                if (round.KcpDataSendOrder > 0)
                {
                    _lastKcpDataSendRound = round.Sequence;
                    _lastKcpDataSendOrder = round.KcpDataSendOrder;
                }
                if (round.BusinessInputAccepted)
                    _lastBusinessInputRound = round.Sequence;

                return new KcpUdpClientWorkerDiagnosticsSnapshot(
                    _completedRoundCount,
                    _nonEmptyDatagramRoundCount,
                    _totalDatagramsDrained,
                    round.DatagramsDrained,
                    _maximumDatagramsDrainedPerRound,
                    _lastReadySendRound,
                    _lastReadySendOrder,
                    _lastKcpDataSendRound,
                    _lastKcpDataSendOrder,
                    _lastBusinessInputRound);
            }
        }

        private sealed class WorkerRoundAccumulator
        {
            private int _outputOrder;

            public WorkerRoundAccumulator(long sequence)
            {
                Sequence = sequence;
            }

            public long Sequence { get; }
            public int DatagramsDrained { get; private set; }
            public int ReadySendOrder { get; private set; }
            public int KcpDataSendOrder { get; private set; }
            public bool BusinessInputAccepted { get; private set; }

            public void RecordDatagramDrained()
            {
                DatagramsDrained++;
            }

            public void RecordProtocolSend(RouteCMessageType messageType)
            {
                _outputOrder++;
                if (messageType == RouteCMessageType.Ready &&
                    ReadySendOrder == 0)
                {
                    ReadySendOrder = _outputOrder;
                }
            }

            public void RecordKcpDataSend()
            {
                _outputOrder++;
                if (KcpDataSendOrder == 0)
                    KcpDataSendOrder = _outputOrder;
            }

            public void RecordBusinessInputAccepted()
            {
                BusinessInputAccepted = true;
            }
        }

        private readonly struct PendingLocalInput
        {
            public PendingLocalInput(uint raw, int frameID)
            {
                Raw = raw;
                FrameID = frameID;
            }

            public uint Raw { get; }
            public int FrameID { get; }
        }
    }
}

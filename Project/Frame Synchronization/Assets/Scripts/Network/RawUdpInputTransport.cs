using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace FrameSyncDemo
{
    public sealed class RawUdpInputTransport : IFrameTransportClient
    {
        private readonly ConcurrentQueue<PendingLocalInput> _localInputs =
            new ConcurrentQueue<PendingLocalInput>();
        private readonly ConcurrentQueue<NetworkPacketArrival> _remoteInputs =
            new ConcurrentQueue<NetworkPacketArrival>();
        private readonly ConcurrentQueue<NetworkTransportEvent> _events =
            new ConcurrentQueue<NetworkTransportEvent>();
        private readonly IMonotonicClock _clock;
        private readonly Func<byte[]> _clientNonceFactory;

        private ClientStatusSnapshot _status = ClientStatusSnapshot.Disconnected;
        private RawUdpClientDiagnosticsSnapshot _diagnostics =
            RawUdpClientDiagnosticsSnapshot.Empty;
        private Thread _worker;
        private Socket _socket;
        private volatile bool _stopRequested;
        private int _started;
        private int _localInputCount;
        private int _remoteInputCount;
        private int _eventCount;
        private int _localInputHighWater;
        private int _remoteInputHighWater;
        private int _eventHighWater;
        private int _localOverflowRequested;
        private int _remoteOverflowRequested;
        private int _stopTimeoutReported;
        private uint _nextPacketSequence;
        private bool _disposed;

        public RawUdpInputTransport(
            IMonotonicClock clock,
            Func<byte[]> clientNonceFactory)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _clientNonceFactory = clientNonceFactory ??
                throw new ArgumentNullException(nameof(clientNonceFactory));
        }

        public NetworkSessionState State => Volatile.Read(ref _status).State;
        public bool IsRunning => State == NetworkSessionState.Running;
        public bool HasStartedSession =>
            Volatile.Read(ref _status).HasStartedSession;
        public int LocalPlayerIndex =>
            Volatile.Read(ref _status).LocalPlayerIndex;
        public int LatestRemoteFrameID =>
            Volatile.Read(ref _status).LatestRemoteFrameID;
        public RawUdpClientDiagnosticsSnapshot Diagnostics =>
            Volatile.Read(ref _diagnostics);

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
                Name = "RouteC Raw UDP Client"
            };
            _worker.Start();
        }

        public bool TryEnqueueLocalInput(uint raw, int localFrameID)
        {
            ThrowIfDisposed();
            if (!HasStartedSession || localFrameID < 0)
                return false;

            int count = Interlocked.Increment(ref _localInputCount);
            if (count > RouteCProtocolConstants.RawQueueCapacity)
            {
                Interlocked.Decrement(ref _localInputCount);
                Interlocked.Exchange(ref _localOverflowRequested, 1);
                return false;
            }

            _localInputs.Enqueue(new PendingLocalInput(raw, localFrameID));
            UpdateHighWater(ref _localInputHighWater, count);
            return true;
        }

        public bool TryDequeueRemoteInput(out NetworkPacketArrival arrival)
        {
            if (_remoteInputs.TryDequeue(out arrival))
            {
                Interlocked.Decrement(ref _remoteInputCount);
                return true;
            }
            return false;
        }

        public bool TryDequeueEvent(out NetworkTransportEvent transportEvent)
        {
            if (_events.TryDequeue(out transportEvent))
            {
                Interlocked.Decrement(ref _eventCount);
                return true;
            }
            return false;
        }

        public void SubmitResumeReadiness(in ResumeReadiness readiness)
        {
            ThrowIfDisposed();
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
            if (!stopped && Interlocked.Exchange(ref _stopTimeoutReported, 1) == 0)
            {
                EnqueueEvent(new NetworkTransportEvent(
                    State,
                    NetworkTransportEventReason.WorkerStopTimedOut,
                    Diagnostics.SessionFingerprint,
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
            WaitForStop(RouteCProtocolConstants.RawWaitForStopMs);
            _disposed = true;
        }

        private void WorkerMain(string host, int port)
        {
            RawUdpClientProtocolState state = null;
            Socket socket = null;
            var pendingSends = new Queue<RouteCProtocolMessage>();
            var pendingEvents = new Queue<NetworkTransportEvent>();
            var accumulator = new DiagnosticsAccumulator();
            var outbound = new OutboundState();
            long receiveSequence = -1;

            try
            {
                IPEndPoint endpoint = ResolveEndpoint(host, port);
                socket = new Socket(
                    endpoint.AddressFamily,
                    SocketType.Dgram,
                    ProtocolType.Udp);
                socket.Blocking = false;
                socket.Connect(endpoint);
                _socket = socket;

                byte[] nonce = _clientNonceFactory();
                state = new RawUdpClientProtocolState(
                    _clock,
                    nonce,
                    pendingSends.Enqueue,
                    pendingEvents.Enqueue,
                    entry => PublishRemoteInput(entry, ref receiveSequence));
                state.Start();
                PublishStateThenEvents(state, pendingEvents, accumulator);
                DrainSends(socket, pendingSends, accumulator);

                while (!Volatile.Read(ref _stopRequested) &&
                       state.State != NetworkSessionState.Terminated)
                {
                    accumulator.BeginRound();
                    DrainLocalCommands(
                        state,
                        outbound,
                        pendingSends,
                        accumulator);
                    SendDueTail(
                        state,
                        outbound,
                        pendingSends,
                        accumulator);
                    state.Tick();
                    PublishStateThenEvents(state, pendingEvents, accumulator);
                    DrainSends(socket, pendingSends, accumulator);
                    if (state.State == NetworkSessionState.Terminated)
                        break;

                    WaitForSocket(socket, accumulator);
                    if (Volatile.Read(ref _stopRequested))
                        break;

                    int receivedThisRound = DrainDatagrams(
                        socket,
                        state,
                        pendingEvents,
                        pendingSends,
                        accumulator);
                    state.Tick();
                    PublishStateThenEvents(state, pendingEvents, accumulator);
                    DrainSends(socket, pendingSends, accumulator);
                    accumulator.CompleteRound(
                        receivedThisRound,
                        socket.Poll(0, SelectMode.SelectRead));
                    PublishDiagnostics(state, accumulator, false);
                }
            }
            catch (Exception)
            {
                Volatile.Write(
                    ref _status,
                    new ClientStatusSnapshot(
                        NetworkSessionState.Terminated,
                        false,
                        -1,
                        LatestRemoteFrameID));
                EnqueueEvent(new NetworkTransportEvent(
                    NetworkSessionState.Terminated,
                    NetworkTransportEventReason.WorkerFault,
                    "none",
                    0u,
                    _clock.Milliseconds));
            }
            finally
            {
                if (state != null && state.State != NetworkSessionState.Terminated)
                {
                    state.RequestStop();
                    PublishStateThenEvents(state, pendingEvents, accumulator);
                }
                else
                {
                    FlushEvents(pendingEvents);
                }

                if (state != null)
                    PublishDiagnostics(state, accumulator, true);
                _socket = null;
                if (socket != null)
                    socket.Dispose();
            }
        }

        private void DrainLocalCommands(
            RawUdpClientProtocolState state,
            OutboundState outbound,
            Queue<RouteCProtocolMessage> pendingSends,
            DiagnosticsAccumulator accumulator)
        {
            if (!state.HasStartedSession)
                return;

            if (Interlocked.Exchange(ref _localOverflowRequested, 0) != 0)
            {
                TerminateOutbound(
                    state,
                    RawUdpFaultReason.CapacityExceeded,
                    NetworkTransportEventReason.CapacityExceeded,
                    outbound.LatestFrameID + 1,
                    accumulator.LatestLocalFrameID);
                return;
            }

            int processed = 0;
            while (processed <
                   RouteCProtocolConstants.MaximumMessagesPerSessionPerRound &&
                   _localInputs.TryDequeue(out PendingLocalInput input))
            {
                Interlocked.Decrement(ref _localInputCount);
                if (input.FrameID != outbound.LatestFrameID + 1)
                {
                    TerminateOutbound(
                        state,
                        RawUdpFaultReason.PeerFault,
                        NetworkTransportEventReason.OutboundHistoryFault,
                        input.FrameID,
                        accumulator.LatestLocalFrameID);
                    return;
                }

                OutboundHistoryDisposition disposition =
                    outbound.History.Record(input.FrameID, input.Raw);
                if (disposition != OutboundHistoryDisposition.Accepted)
                {
                    TerminateOutbound(
                        state,
                        RawUdpFaultReason.PeerFault,
                        NetworkTransportEventReason.OutboundHistoryFault,
                        input.FrameID,
                        accumulator.LatestLocalFrameID);
                    return;
                }

                outbound.LatestFrameID = input.FrameID;
                accumulator.LatestLocalFrameID = input.FrameID;
                EnqueueInputWindow(
                    state,
                    outbound,
                    pendingSends,
                    accumulator);
                processed++;
            }
        }

        private void SendDueTail(
            RawUdpClientProtocolState state,
            OutboundState outbound,
            Queue<RouteCProtocolMessage> pendingSends,
            DiagnosticsAccumulator accumulator)
        {
            if (state.State != NetworkSessionState.Running ||
                outbound.LatestFrameID < 0)
                return;
            if (unchecked(_clock.Milliseconds - outbound.LastSendAt) <
                RouteCProtocolConstants.RawInputCadenceMs)
            {
                return;
            }

            EnqueueInputWindow(state, outbound, pendingSends, accumulator);
        }

        private void EnqueueInputWindow(
            RawUdpClientProtocolState state,
            OutboundState outbound,
            Queue<RouteCProtocolMessage> pendingSends,
            DiagnosticsAccumulator accumulator)
        {
            int count = Math.Min(
                state.NegotiatedWindowSize,
                outbound.LatestFrameID + 1);
            var entries = new RawUdpInputEntry[count];
            int firstFrameID = outbound.LatestFrameID - count + 1;
            for (int index = 0; index < entries.Length; index++)
            {
                int frameID = firstFrameID + index;
                if (!outbound.History.TryGet(frameID, out uint raw))
                {
                    TerminateOutbound(
                        state,
                        RawUdpFaultReason.PeerFault,
                        NetworkTransportEventReason.OutboundHistoryFault,
                        frameID,
                        accumulator.LatestLocalFrameID);
                    return;
                }
                entries[index] = new RawUdpInputEntry(raw, frameID);
            }

            uint sequence = _nextPacketSequence;
            _nextPacketSequence = unchecked(_nextPacketSequence + 1u);
            byte[] payload = RawUdpProtocolCodec.EncodeInput(
                (byte)state.LocalPlayerIndex,
                (byte)state.NegotiatedWindowSize,
                sequence,
                outbound.LatestFrameID,
                entries);
            pendingSends.Enqueue(new RouteCProtocolMessage(
                RouteCMessageType.RawInput,
                state.SessionId,
                RouteCProtocolConstants.RawGeneration,
                payload));
            outbound.LastSendAt = _clock.Milliseconds;
            accumulator.SentPacketSequenceHighWater = sequence;
        }

        private static void TerminateOutbound(
            RawUdpClientProtocolState state,
            RawUdpFaultReason faultReason,
            NetworkTransportEventReason eventReason,
            int frameID,
            int observedLatestFrameID)
        {
            var fault = new RawUdpFault(
                faultReason,
                state.LocalPlayerIndex < 0
                    ? (byte)255
                    : (byte)state.LocalPlayerIndex,
                frameID,
                observedLatestFrameID);
            state.TerminateLocalFault(in fault, eventReason);
        }

        private int DrainDatagrams(
            Socket socket,
            RawUdpClientProtocolState state,
            Queue<NetworkTransportEvent> pendingEvents,
            Queue<RouteCProtocolMessage> pendingSends,
            DiagnosticsAccumulator accumulator)
        {
            var buffer = new byte[RouteCProtocolConstants.MaximumDatagramSize + 1];
            int received = 0;
            while (received < RouteCProtocolConstants.MaximumDatagramsPerRound)
            {
                int count;
                try
                {
                    count = socket.Receive(buffer);
                }
                catch (SocketException exception)
                    when (IsTransientReceiveError(exception))
                {
                    break;
                }

                received++;
                accumulator.DatagramsReceived++;
                accumulator.BytesReceived += count;
                if (!RouteCProtocolCodec.TryDecode(
                    buffer,
                    count,
                    out RouteCProtocolMessage message,
                    out _))
                {
                    accumulator.ClassifiedDropCount++;
                    continue;
                }

                state.HandleIncoming(message);
                if (Interlocked.Exchange(ref _remoteOverflowRequested, 0) != 0 &&
                    state.State != NetworkSessionState.Terminated)
                {
                    var fault = new RawUdpFault(
                        RawUdpFaultReason.CapacityExceeded,
                        state.LocalPlayerIndex == 0 ? (byte)1 : (byte)0,
                        state.LatestRemoteFrameID,
                        state.LatestRemoteFrameID);
                    state.TerminateLocalFault(
                        in fault,
                        NetworkTransportEventReason.CapacityExceeded);
                }
                PublishStateThenEvents(state, pendingEvents, accumulator);
                DrainSends(socket, pendingSends, accumulator);
                if (state.State == NetworkSessionState.Terminated)
                    break;
            }
            return received;
        }

        private void DrainSends(
            Socket socket,
            Queue<RouteCProtocolMessage> pendingSends,
            DiagnosticsAccumulator accumulator)
        {
            while (pendingSends.Count > 0)
            {
                RouteCProtocolMessage message = pendingSends.Dequeue();
                byte[] datagram = RouteCProtocolCodec.Encode(
                    message.MessageType,
                    message.SessionId,
                    message.Generation,
                    message.Payload);
                socket.Send(datagram);
                accumulator.DatagramsSent++;
                accumulator.BytesSent += datagram.Length;
            }
        }

        private void WaitForSocket(
            Socket socket,
            DiagnosticsAccumulator accumulator)
        {
            var readable = new List<Socket>(1) { socket };
            int waitMilliseconds = RouteCProtocolConstants.RawMaximumSelectWaitMs;
            Socket.Select(
                readable,
                null,
                null,
                waitMilliseconds * 1000);
            accumulator.SelectCount++;
            accumulator.MaximumSelectWaitMilliseconds = Math.Max(
                accumulator.MaximumSelectWaitMilliseconds,
                waitMilliseconds);
        }

        private void PublishStateThenEvents(
            RawUdpClientProtocolState state,
            Queue<NetworkTransportEvent> pendingEvents,
            DiagnosticsAccumulator accumulator)
        {
            Volatile.Write(
                ref _status,
                new ClientStatusSnapshot(
                    state.State,
                    state.HasStartedSession,
                    state.LocalPlayerIndex,
                    state.LatestRemoteFrameID));
            PublishDiagnostics(state, accumulator, false);
            FlushEvents(pendingEvents);
        }

        private void PublishRemoteInput(
            RawUdpInputEntry entry,
            ref long receiveSequence)
        {
            int count = Interlocked.Increment(ref _remoteInputCount);
            if (count > RouteCProtocolConstants.RawQueueCapacity)
            {
                Interlocked.Decrement(ref _remoteInputCount);
                Interlocked.Exchange(ref _remoteOverflowRequested, 1);
                return;
            }

            receiveSequence++;
            _remoteInputs.Enqueue(new NetworkPacketArrival(
                entry.Raw,
                entry.FrameID,
                receiveSequence,
                _clock.Timestamp));
            UpdateHighWater(ref _remoteInputHighWater, count);
        }

        private void FlushEvents(Queue<NetworkTransportEvent> pendingEvents)
        {
            while (pendingEvents.Count > 0)
                EnqueueEvent(pendingEvents.Dequeue());
        }

        private void EnqueueEvent(NetworkTransportEvent transportEvent)
        {
            bool terminal = transportEvent.State == NetworkSessionState.Terminated;
            int limit = terminal
                ? RouteCProtocolConstants.RawQueueCapacity
                : RouteCProtocolConstants.RawQueueCapacity - 1;
            int count = Interlocked.Increment(ref _eventCount);
            if (count > limit)
            {
                Interlocked.Decrement(ref _eventCount);
                return;
            }

            _events.Enqueue(transportEvent);
            UpdateHighWater(ref _eventHighWater, count);
        }

        private void PublishDiagnostics(
            RawUdpClientProtocolState state,
            DiagnosticsAccumulator accumulator,
            bool stopCompleted)
        {
            Volatile.Write(
                ref _diagnostics,
                new RawUdpClientDiagnosticsSnapshot
                {
                    State = state.State,
                    PlayerIndex = state.LocalPlayerIndex,
                    WindowSize = state.NegotiatedWindowSize,
                    SessionFingerprint = RawUdpSessionFingerprint.Format(state.SessionId),
                    SentPacketSequenceHighWater =
                        accumulator.SentPacketSequenceHighWater,
                    ReceivedPacketSequenceHighWater =
                        accumulator.ReceivedPacketSequenceHighWater,
                    DatagramsSent = accumulator.DatagramsSent,
                    DatagramsReceived = accumulator.DatagramsReceived,
                    BytesSent = accumulator.BytesSent,
                    BytesReceived = accumulator.BytesReceived,
                    ClassifiedDropCount = accumulator.ClassifiedDropCount,
                    LatestLocalFrameID = accumulator.LatestLocalFrameID,
                    HighestRemoteFrameID = state.LatestRemoteFrameID,
                    LocalCommandQueueCount = Volatile.Read(ref _localInputCount),
                    LocalCommandQueueHighWater = Volatile.Read(ref _localInputHighWater),
                    RemoteArrivalQueueCount = Volatile.Read(ref _remoteInputCount),
                    RemoteArrivalQueueHighWater = Volatile.Read(ref _remoteInputHighWater),
                    EventQueueCount = Volatile.Read(ref _eventCount),
                    EventQueueHighWater = Volatile.Read(ref _eventHighWater),
                    WorkerRoundCount = accumulator.WorkerRoundCount,
                    MaximumDatagramsReceivedPerRound =
                        accumulator.MaximumDatagramsReceivedPerRound,
                    ReceiveBudgetExhaustionCount =
                        accumulator.ReceiveBudgetExhaustionCount,
                    SelectCount = accumulator.SelectCount,
                    MaximumSelectWaitMilliseconds =
                        accumulator.MaximumSelectWaitMilliseconds,
                    StopCompleted = stopCompleted
                });
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

        private static bool IsTransientReceiveError(SocketException exception)
        {
            return exception.SocketErrorCode == SocketError.WouldBlock ||
                   exception.SocketErrorCode == SocketError.ConnectionReset ||
                   exception.SocketErrorCode == SocketError.ConnectionRefused;
        }

        private static void UpdateHighWater(ref int target, int candidate)
        {
            int current = Volatile.Read(ref target);
            while (candidate > current)
            {
                int observed = Interlocked.CompareExchange(
                    ref target,
                    candidate,
                    current);
                if (observed == current)
                    return;
                current = observed;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(RawUdpInputTransport));
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

        private sealed class DiagnosticsAccumulator
        {
            private int _receivedThisRound;

            public long DatagramsSent;
            public long DatagramsReceived;
            public long BytesSent;
            public long BytesReceived;
            public long ClassifiedDropCount;
            public int LatestLocalFrameID = -1;
            public long SentPacketSequenceHighWater = -1;
            public long ReceivedPacketSequenceHighWater = -1;
            public long WorkerRoundCount;
            public int MaximumDatagramsReceivedPerRound;
            public long ReceiveBudgetExhaustionCount;
            public long SelectCount;
            public int MaximumSelectWaitMilliseconds;

            public void BeginRound()
            {
                _receivedThisRound = 0;
            }

            public void CompleteRound(int receivedThisRound, bool stillReadable)
            {
                _receivedThisRound = receivedThisRound;
                WorkerRoundCount++;
                MaximumDatagramsReceivedPerRound = Math.Max(
                    MaximumDatagramsReceivedPerRound,
                    _receivedThisRound);
                if (_receivedThisRound ==
                    RouteCProtocolConstants.MaximumDatagramsPerRound)
                {
                    ReceiveBudgetExhaustionCount++;
                }
            }
        }

        private sealed class OutboundState
        {
            public readonly OutboundActualHistory History =
                new OutboundActualHistory(
                    RouteCProtocolConstants.RawInputHistoryCapacity);
            public int LatestFrameID = -1;
            public uint LastSendAt;
        }
    }
}

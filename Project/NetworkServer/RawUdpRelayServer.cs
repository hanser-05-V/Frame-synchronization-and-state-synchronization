using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using FrameSyncDemo;

namespace FrameSyncServer
{
    public sealed class RawUdpRelayServer : IDisposable
    {
        private readonly int _port;
        private readonly int _windowSize;
        private Thread _worker;
        private Socket _socket;
        private RawUdpServerDiagnosticsSnapshot _diagnostics =
            RawUdpServerDiagnosticsSnapshot.Empty;
        private bool _stopRequested;
        private int _started;
        private int _stopTimeoutReported;
        private bool _disposed;

        public RawUdpRelayServer(int windowSize)
            : this(8888, windowSize)
        {
        }

        public RawUdpRelayServer(int port, int windowSize)
        {
            if (port < 0 || port > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(port));
            if (windowSize < RouteCProtocolConstants.RawMinimumWindowSize ||
                windowSize > RouteCProtocolConstants.RawMaximumWindowSize)
            {
                throw new ArgumentOutOfRangeException(nameof(windowSize));
            }
            _port = port;
            _windowSize = windowSize;
        }

        public RawUdpServerDiagnosticsSnapshot Diagnostics =>
            Volatile.Read(ref _diagnostics);

        public void Run()
        {
            ThrowIfDisposed();
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
                throw new InvalidOperationException("Raw UDP server has already started.");

            _worker = new Thread(WorkerMain)
            {
                IsBackground = true,
                Name = "RouteC Raw UDP Server"
            };
            _worker.Start();
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
            if (!stopped)
                Interlocked.Exchange(ref _stopTimeoutReported, 1);
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

        private void WorkerMain()
        {
            Socket socket = null;
            var match = new RawUdpServerMatch(_windowSize);
            var queues = new[]
            {
                new Queue<InboundDatagram>(),
                new Queue<InboundDatagram>()
            };
            var accumulator = new DiagnosticsAccumulator();
            var timeoutTracker = new TimeoutTracker();
            long lastStartRetryAt = -RouteCProtocolConstants.RawFaultRepeatIntervalMs;
            bool relayWasEnabled = false;

            try
            {
                socket = new Socket(
                    AddressFamily.InterNetwork,
                    SocketType.Dgram,
                    ProtocolType.Udp);
                socket.Blocking = false;
                socket.Bind(new IPEndPoint(IPAddress.Any, _port));
                _socket = socket;
                accumulator.BoundPort = ((IPEndPoint)socket.LocalEndPoint).Port;
                PublishDiagnostics(match, queues, accumulator, false);

                while (!Volatile.Read(ref _stopRequested) && !match.IsTerminal)
                {
                    accumulator.WorkerRoundCount++;
                    WaitForSocket(socket, accumulator);
                    if (Volatile.Read(ref _stopRequested))
                        break;

                    var playerWorkItems = new int[2];
                    ProcessPlayerQueues(
                        socket,
                        match,
                        queues,
                        accumulator,
                        timeoutTracker,
                        playerWorkItems);
                    if (match.RelayEnabled && !relayWasEnabled)
                    {
                        relayWasEnabled = true;
                        lastStartRetryAt = accumulator.ElapsedMilliseconds;
                    }
                    if (match.IsTerminal)
                    {
                        PublishDiagnostics(match, queues, accumulator, false);
                        break;
                    }

                    int receivedThisRound = ReceiveRound(
                        socket,
                        match,
                        queues,
                        accumulator,
                        timeoutTracker);
                    accumulator.MaximumDatagramsReceivedPerRound = Math.Max(
                        accumulator.MaximumDatagramsReceivedPerRound,
                        receivedThisRound);
                    if (receivedThisRound ==
                        RouteCProtocolConstants.MaximumDatagramsPerRound)
                    {
                        accumulator.ReceiveBudgetExhaustionCount++;
                    }

                    ProcessPlayerQueues(
                        socket,
                        match,
                        queues,
                        accumulator,
                        timeoutTracker,
                        playerWorkItems);

                    EvaluateTimeouts(
                        socket,
                        match,
                        timeoutTracker,
                        accumulator);

                    long nowMs = accumulator.ElapsedMilliseconds;
                    if (match.RelayEnabled && !relayWasEnabled)
                    {
                        relayWasEnabled = true;
                        lastStartRetryAt = nowMs;
                    }
                    if (match.RelayEnabled &&
                        nowMs - lastStartRetryAt >=
                        RouteCProtocolConstants.RawFaultRepeatIntervalMs)
                    {
                        SendActions(
                            socket,
                            match.CreateStartRetries(),
                            accumulator);
                        lastStartRetryAt = nowMs;
                    }
                    PublishDiagnostics(match, queues, accumulator, false);
                }

                if (match.IsTerminal && !Volatile.Read(ref _stopRequested))
                {
                    RepeatTerminalFaults(
                        socket,
                        match,
                        accumulator);
                }
            }
            catch (Exception)
            {
                accumulator.WorkerFault = true;
            }
            finally
            {
                _socket = null;
                if (socket != null)
                    socket.Dispose();
                PublishDiagnostics(match, queues, accumulator, true);
            }
        }

        private static int ReceiveRound(
            Socket socket,
            RawUdpServerMatch match,
            Queue<InboundDatagram>[] queues,
            DiagnosticsAccumulator accumulator,
            TimeoutTracker timeoutTracker)
        {
            var buffer = new byte[RouteCProtocolConstants.MaximumDatagramSize + 1];
            int received = 0;
            while (received < RouteCProtocolConstants.MaximumDatagramsPerRound)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int count;
                try
                {
                    count = socket.ReceiveFrom(buffer, ref remote);
                }
                catch (SocketException exception)
                    when (exception.SocketErrorCode == SocketError.WouldBlock ||
                          exception.SocketErrorCode == SocketError.ConnectionReset)
                {
                    break;
                }

                received++;
                accumulator.DatagramsReceived++;
                accumulator.BytesReceived += count;
                long receivedAtMs = accumulator.ElapsedMilliseconds;
                EvaluateTimeouts(
                    socket,
                    match,
                    timeoutTracker,
                    accumulator,
                    receivedAtMs);
                if (match.IsTerminal)
                    break;
                var endpoint = (IPEndPoint)remote;
                var datagram = new byte[count];
                Buffer.BlockCopy(buffer, 0, datagram, 0, count);
                if (match.TryGetPlayerIndex(endpoint, out byte playerIndex))
                {
                    Queue<InboundDatagram> queue = queues[playerIndex];
                    if (queue.Count >= RouteCProtocolConstants.RawQueueCapacity)
                    {
                        RawUdpServerMatch.Result terminal =
                            match.TerminateCapacityExceeded(playerIndex);
                        SendActions(socket, terminal.Actions, accumulator);
                        break;
                    }
                    queue.Enqueue(new InboundDatagram(
                        endpoint,
                        datagram,
                        receivedAtMs,
                        match.RelayEnabled));
                    accumulator.QueueHighWater[playerIndex] = Math.Max(
                        accumulator.QueueHighWater[playerIndex],
                        queue.Count);
                }
                else
                {
                    RawUdpServerMatch.Result result = match.Process(
                        endpoint,
                        datagram);
                    SendActions(socket, result.Actions, accumulator);
                    RecordValidTraffic(
                        match,
                        endpoint,
                        result,
                        timeoutTracker,
                        receivedAtMs);
                    if (result.IsTerminal)
                        break;
                }
            }
            return received;
        }

        private static void ProcessPlayerQueues(
            Socket socket,
            RawUdpServerMatch match,
            Queue<InboundDatagram>[] queues,
            DiagnosticsAccumulator accumulator,
            TimeoutTracker timeoutTracker,
            int[] playerWorkItems)
        {
            long sliceStarted = Stopwatch.GetTimestamp();
            for (int playerIndex = 0; playerIndex < queues.Length; playerIndex++)
            {
                int processed = playerWorkItems[playerIndex];
                Queue<InboundDatagram> queue = queues[playerIndex];
                while (processed <
                           RouteCProtocolConstants.MaximumMessagesPerSessionPerRound &&
                       queue.Count > 0 &&
                       !match.IsTerminal)
                {
                    if (processed > 0 &&
                        ElapsedMilliseconds(sliceStarted) >=
                        RouteCProtocolConstants.MaximumWorkerSliceMs)
                    {
                        accumulator.FairnessRecheckCount++;
                        break;
                    }

                    InboundDatagram pending = queue.Peek();
                    int maximumWorkItems = GetMaximumWorkItems(
                        pending.Datagram,
                        match.WindowSize);
                    if (processed + maximumWorkItems >
                        RouteCProtocolConstants.MaximumMessagesPerSessionPerRound)
                    {
                        break;
                    }

                    InboundDatagram inbound = queue.Dequeue();
                    RawUdpServerMatch.Result result = match.Process(
                        inbound.Endpoint,
                        inbound.Datagram,
                        inbound.RelayEnabledAtReceive);
                    SendActions(socket, result.Actions, accumulator);
                    RecordValidTraffic(
                        match,
                        inbound.Endpoint,
                        result,
                        timeoutTracker,
                        inbound.ReceivedAtMilliseconds);
                    processed += Math.Max(1, result.AcceptedWorkItems);
                }
                accumulator.MaximumPlayerWorkItemsPerRound = Math.Max(
                    accumulator.MaximumPlayerWorkItemsPerRound,
                    processed);
                playerWorkItems[playerIndex] = processed;
                sliceStarted = Stopwatch.GetTimestamp();
            }
        }

        private static void SendActions(
            Socket socket,
            RawUdpServerMatch.OutboundAction[] actions,
            DiagnosticsAccumulator accumulator)
        {
            for (int index = 0; index < actions.Length; index++)
            {
                byte[] datagram = actions[index].Datagram;
                socket.SendTo(datagram, actions[index].Endpoint);
                accumulator.DatagramsSent++;
                accumulator.BytesSent += datagram.Length;
            }
        }

        private void RepeatTerminalFaults(
            Socket socket,
            RawUdpServerMatch match,
            DiagnosticsAccumulator accumulator)
        {
            long started = Stopwatch.GetTimestamp();
            for (int repeat = 1;
                 repeat < RouteCProtocolConstants.RawFaultRepeatCount;
                 repeat++)
            {
                if (Volatile.Read(ref _stopRequested))
                    return;
                long dueAt =
                    repeat * RouteCProtocolConstants.RawFaultRepeatIntervalMs;
                while (ElapsedMilliseconds(started) < dueAt)
                {
                    if (Volatile.Read(ref _stopRequested))
                        return;
                    int remaining = (int)Math.Ceiling(
                        dueAt - ElapsedMilliseconds(started));
                    Thread.Sleep(Math.Min(
                        RouteCProtocolConstants.RawMaximumSelectWaitMs,
                        Math.Max(1, remaining)));
                }
                RawUdpServerMatch.OutboundAction[] actions =
                    match.CreateTerminalFaultRetries();
                SendActions(socket, actions, accumulator);
                accumulator.TerminalFaultRepeatDatagramsSent += actions.Length;
            }
        }

        private static void EvaluateTimeouts(
            Socket socket,
            RawUdpServerMatch match,
            TimeoutTracker tracker,
            DiagnosticsAccumulator accumulator)
        {
            EvaluateTimeouts(
                socket,
                match,
                tracker,
                accumulator,
                accumulator.ElapsedMilliseconds);
        }

        private static void EvaluateTimeouts(
            Socket socket,
            RawUdpServerMatch match,
            TimeoutTracker tracker,
            DiagnosticsAccumulator accumulator,
            long nowMs)
        {
            if (match.IsTerminal || match.PlayerCount == 0)
                return;

            if (match.RelayEnabled && !tracker.HasStartedRunning)
                tracker.StartRunning(nowMs);

            if (!match.RelayEnabled)
            {
                if (tracker.HandshakeStartedAt >= 0 &&
                    nowMs - tracker.HandshakeStartedAt >=
                    RouteCProtocolConstants.RawHandshakeTimeoutMs)
                {
                    RawUdpServerMatch.Result result = match.TerminateTimeout(
                        RawUdpFaultReason.HandshakeTimeout,
                        match.GetHandshakeTimeoutPlayerIndex());
                    SendActions(socket, result.Actions, accumulator);
                }
                return;
            }

            for (byte playerIndex = 0; playerIndex < 2; playerIndex++)
            {
                if (nowMs - tracker.GetLastValidTrafficAt(playerIndex) <
                    RouteCProtocolConstants.RawValidTrafficTimeoutMs)
                {
                    continue;
                }

                RawUdpServerMatch.Result result = match.TerminateTimeout(
                    RawUdpFaultReason.ConnectionTimedOut,
                    playerIndex);
                SendActions(socket, result.Actions, accumulator);
                return;
            }
        }

        private static void RecordValidTraffic(
            RawUdpServerMatch match,
            IPEndPoint endpoint,
            RawUdpServerMatch.Result result,
            TimeoutTracker tracker,
            long nowMs)
        {
            if (!result.IsValidTraffic ||
                !match.TryGetPlayerIndex(endpoint, out byte playerIndex))
            {
                return;
            }
            tracker.Observe(playerIndex, nowMs);
        }

        private static int GetMaximumWorkItems(
            byte[] datagram,
            int windowSize)
        {
            if (RouteCProtocolCodec.TryDecode(
                    datagram,
                    datagram.Length,
                    out RouteCProtocolMessage message,
                    out _) &&
                message.MessageType == RouteCMessageType.RawInput)
            {
                return windowSize * 2;
            }
            return 1;
        }

        private static void WaitForSocket(
            Socket socket,
            DiagnosticsAccumulator accumulator)
        {
            var readable = new List<Socket>(1) { socket };
            int waitMilliseconds = RouteCProtocolConstants.RawMaximumSelectWaitMs;
            Socket.Select(readable, null, null, waitMilliseconds * 1000);
            accumulator.SelectCount++;
            accumulator.MaximumSelectWaitMilliseconds = Math.Max(
                accumulator.MaximumSelectWaitMilliseconds,
                waitMilliseconds);
        }

        private void PublishDiagnostics(
            RawUdpServerMatch match,
            Queue<InboundDatagram>[] queues,
            DiagnosticsAccumulator accumulator,
            bool stopCompleted)
        {
            Volatile.Write(
                ref _diagnostics,
                new RawUdpServerDiagnosticsSnapshot
                {
                    BoundPort = accumulator.BoundPort,
                    PlayerCount = match.PlayerCount,
                    RelayEnabled = match.RelayEnabled,
                    IsTerminal = match.IsTerminal,
                    DatagramsReceived = accumulator.DatagramsReceived,
                    DatagramsSent = accumulator.DatagramsSent,
                    BytesReceived = accumulator.BytesReceived,
                    BytesSent = accumulator.BytesSent,
                    WorkerRoundCount = accumulator.WorkerRoundCount,
                    MaximumDatagramsReceivedPerRound =
                        accumulator.MaximumDatagramsReceivedPerRound,
                    ReceiveBudgetExhaustionCount =
                        accumulator.ReceiveBudgetExhaustionCount,
                    MaximumPlayerWorkItemsPerRound =
                        accumulator.MaximumPlayerWorkItemsPerRound,
                    FairnessRecheckCount = accumulator.FairnessRecheckCount,
                    Player0QueueCount = queues[0].Count,
                    Player1QueueCount = queues[1].Count,
                    Player0QueueHighWater = accumulator.QueueHighWater[0],
                    Player1QueueHighWater = accumulator.QueueHighWater[1],
                    SelectCount = accumulator.SelectCount,
                    MaximumSelectWaitMilliseconds =
                        accumulator.MaximumSelectWaitMilliseconds,
                    StopCompleted = stopCompleted,
                    LateRecoveryRelayCopies = match.LateRecoveryRelayCopies,
                    WorkerFault = accumulator.WorkerFault,
                    TerminalFaultRepeatDatagramsSent =
                        accumulator.TerminalFaultRepeatDatagramsSent,
                    TerminalReason = match.TerminalReason,
                    TerminalPlayerIndex = match.TerminalPlayerIndex,
                    TerminalFrameID = match.TerminalFrameID,
                    TerminalObservedLatestFrameID =
                        match.TerminalObservedLatestFrameID
                });
        }

        private static double ElapsedMilliseconds(long started)
        {
            return (Stopwatch.GetTimestamp() - started) * 1000.0 /
                   Stopwatch.Frequency;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(RawUdpRelayServer));
        }

        private sealed class InboundDatagram
        {
            private readonly byte[] _datagram;

            public InboundDatagram(
                IPEndPoint endpoint,
                byte[] datagram,
                long receivedAtMilliseconds,
                bool relayEnabledAtReceive)
            {
                Endpoint = endpoint;
                _datagram = (byte[])datagram.Clone();
                ReceivedAtMilliseconds = receivedAtMilliseconds;
                RelayEnabledAtReceive = relayEnabledAtReceive;
            }

            public IPEndPoint Endpoint { get; }
            public long ReceivedAtMilliseconds { get; }
            public bool RelayEnabledAtReceive { get; }
            public byte[] Datagram => (byte[])_datagram.Clone();
        }

        private sealed class DiagnosticsAccumulator
        {
            private readonly Stopwatch _lifetime = Stopwatch.StartNew();

            public int BoundPort;
            public long DatagramsReceived;
            public long DatagramsSent;
            public long BytesReceived;
            public long BytesSent;
            public long WorkerRoundCount;
            public int MaximumDatagramsReceivedPerRound;
            public long ReceiveBudgetExhaustionCount;
            public int MaximumPlayerWorkItemsPerRound;
            public long FairnessRecheckCount;
            public readonly int[] QueueHighWater = new int[2];
            public long SelectCount;
            public int MaximumSelectWaitMilliseconds;
            public bool WorkerFault;
            public long TerminalFaultRepeatDatagramsSent;
            public long ElapsedMilliseconds => _lifetime.ElapsedMilliseconds;
        }

        private sealed class TimeoutTracker
        {
            private readonly long[] _lastValidTrafficAt = { -1L, -1L };

            public long HandshakeStartedAt { get; private set; } = -1L;
            public bool HasStartedRunning { get; private set; }

            public void Observe(byte playerIndex, long nowMs)
            {
                if (HandshakeStartedAt < 0)
                    HandshakeStartedAt = nowMs;
                _lastValidTrafficAt[playerIndex] = nowMs;
            }

            public void StartRunning(long nowMs)
            {
                HasStartedRunning = true;
                _lastValidTrafficAt[0] = nowMs;
                _lastValidTrafficAt[1] = nowMs;
            }

            public long GetLastValidTrafficAt(byte playerIndex)
            {
                return _lastValidTrafficAt[playerIndex];
            }
        }
    }
}

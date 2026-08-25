using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;

namespace FrameSyncDemo
{
    public sealed class TcpClientTransport : IFrameTransportClient
    {
        private readonly ConcurrentQueue<PendingLocalInput> _localInputs =
            new ConcurrentQueue<PendingLocalInput>();
        private readonly ConcurrentQueue<NetworkPacketArrival> _remoteInputs =
            new ConcurrentQueue<NetworkPacketArrival>();
        private readonly ConcurrentQueue<NetworkTransportEvent> _events =
            new ConcurrentQueue<NetworkTransportEvent>();

        private TcpStatusSnapshot _status = TcpStatusSnapshot.Disconnected;
        private TcpClient _tcp;
        private NetworkStream _stream;
        private Thread _worker;
        private volatile bool _stopRequested;
        private int _started;
        private int _pendingLocalInputCount;
        private bool _disposed;

        public NetworkSessionState State => Volatile.Read(ref _status).State;

        public bool IsRunning => State == NetworkSessionState.Running;

        public bool HasStartedSession =>
            Volatile.Read(ref _status).HasStartedSession;

        public int LocalPlayerIndex =>
            Volatile.Read(ref _status).LocalPlayerIndex;

        public int LatestRemoteFrameID =>
            Volatile.Read(ref _status).LatestRemoteFrameID;

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
                Name = "RouteC TCP Client"
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
            {
                TcpStatusSnapshot status = Volatile.Read(ref _status);
                _events.Enqueue(new NetworkTransportEvent(
                    status.State,
                    NetworkTransportEventReason.WorkerStopTimedOut,
                    "tcp",
                    0u,
                    0u));
            }
            return stopped;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            RequestStop();
            WaitForStop(
                NetworkConfig.InitialKcpIntervalMs +
                NetworkConfig.WorkerStopMarginMs);
            _disposed = true;
        }

        private void WorkerMain(string host, int port)
        {
            int latestRemoteFrameID = -1;
            long receiveSequence = -1;
            var receiveBuffer = new byte[RouteCProtocolConstants.BusinessInputSize];
            int receiveOffset = 0;
            byte[] pendingWrite = null;
            int pendingWriteOffset = 0;
            bool terminalEventPublished = false;

            try
            {
                _tcp = new TcpClient();
                NetworkTransportSettings.ConfigureLowLatency(_tcp);
                IAsyncResult connect = _tcp.BeginConnect(host, port, null, null);
                while (!connect.AsyncWaitHandle.WaitOne(
                    NetworkConfig.InitialKcpIntervalMs))
                {
                    if (Volatile.Read(ref _stopRequested))
                        return;
                }
                _tcp.EndConnect(connect);
                if (Volatile.Read(ref _stopRequested))
                    return;

                _stream = _tcp.GetStream();
                int playerIndex = ReadPlayerIndex();
                if (playerIndex != 0 && playerIndex != 1)
                    throw new InvalidOperationException("Server returned an invalid player index.");
                _tcp.Client.Blocking = false;

                PublishStatus(
                    NetworkSessionState.Running,
                    true,
                    playerIndex,
                    latestRemoteFrameID);
                _events.Enqueue(new NetworkTransportEvent(
                    NetworkSessionState.Running,
                    NetworkTransportEventReason.SessionStarted,
                    "tcp",
                    0u,
                    0u));

                while (!Volatile.Read(ref _stopRequested))
                {
                    DrainWrites(
                        ref pendingWrite,
                        ref pendingWriteOffset);
                    if (!ReadAvailable(
                            receiveBuffer,
                            ref receiveOffset,
                            ref receiveSequence,
                            ref latestRemoteFrameID,
                            playerIndex))
                    {
                        break;
                    }
                }
            }
            catch (Exception)
            {
                if (!Volatile.Read(ref _stopRequested))
                {
                    _events.Enqueue(new NetworkTransportEvent(
                        NetworkSessionState.Terminated,
                        NetworkTransportEventReason.WorkerFault,
                        "tcp",
                        0u,
                        0u));
                    terminalEventPublished = true;
                }
            }
            finally
            {
                PublishStatus(
                    NetworkSessionState.Terminated,
                    false,
                    -1,
                    latestRemoteFrameID);
                if (Volatile.Read(ref _stopRequested))
                {
                    _events.Enqueue(new NetworkTransportEvent(
                        NetworkSessionState.Terminated,
                        NetworkTransportEventReason.StopRequested,
                        "tcp",
                        0u,
                        0u));
                }
                else if (!terminalEventPublished)
                {
                    _events.Enqueue(new NetworkTransportEvent(
                        NetworkSessionState.Terminated,
                        NetworkTransportEventReason.ConnectionClosed,
                        "tcp",
                        0u,
                        0u));
                }

                NetworkStream stream = _stream;
                _stream = null;
                stream?.Dispose();
                TcpClient tcp = _tcp;
                _tcp = null;
                tcp?.Close();
            }
        }

        private int ReadPlayerIndex()
        {
            while (!Volatile.Read(ref _stopRequested))
            {
                if (!_tcp.Client.Poll(
                        NetworkConfig.InitialKcpIntervalMs * 1000,
                        SelectMode.SelectRead))
                {
                    continue;
                }

                return _stream.ReadByte();
            }

            return -1;
        }

        private void DrainWrites(
            ref byte[] pendingWrite,
            ref int pendingWriteOffset)
        {
            int completedMessages = 0;
            while (completedMessages <
                   RouteCProtocolConstants.MaximumMessagesPerSessionPerRound &&
                   !Volatile.Read(ref _stopRequested))
            {
                if (pendingWrite == null)
                {
                    if (!_localInputs.TryDequeue(out PendingLocalInput input))
                        return;

                    Interlocked.Decrement(ref _pendingLocalInputCount);
                    pendingWrite = RouteCProtocolCodec.EncodeBusinessInput(
                        input.Raw,
                        input.FrameID);
                    pendingWriteOffset = 0;
                }

                Socket socket = _tcp.Client;
                if (!socket.Poll(
                        NetworkConfig.InitialKcpIntervalMs * 1000,
                        SelectMode.SelectWrite))
                {
                    return;
                }
                if (Volatile.Read(ref _stopRequested))
                    return;

                int sent;
                try
                {
                    sent = socket.Send(
                        pendingWrite,
                        pendingWriteOffset,
                        pendingWrite.Length - pendingWriteOffset,
                        SocketFlags.None);
                }
                catch (SocketException exception)
                    when (IsTransientWriteBackpressure(exception))
                {
                    return;
                }

                if (sent <= 0)
                    throw new SocketException((int)SocketError.ConnectionReset);

                pendingWriteOffset += sent;
                if (pendingWriteOffset != pendingWrite.Length)
                    continue;

                pendingWrite = null;
                pendingWriteOffset = 0;
                completedMessages++;
            }
        }

        private bool ReadAvailable(
            byte[] buffer,
            ref int offset,
            ref long receiveSequence,
            ref int latestRemoteFrameID,
            int playerIndex)
        {
            if (!_tcp.Client.Poll(
                    NetworkConfig.InitialKcpIntervalMs * 1000,
                    SelectMode.SelectRead))
            {
                return true;
            }

            int available = _tcp.Available;
            if (available == 0)
                return false;

            int messages = 0;
            while (available > 0 &&
                   messages < RouteCProtocolConstants.MaximumMessagesPerSessionPerRound)
            {
                int count = Math.Min(buffer.Length - offset, available);
                int read;
                try
                {
                    read = _tcp.Client.Receive(
                        buffer,
                        offset,
                        count,
                        SocketFlags.None);
                }
                catch (SocketException exception)
                    when (exception.SocketErrorCode == SocketError.WouldBlock)
                {
                    return true;
                }
                if (read <= 0)
                    return false;

                offset += read;
                available -= read;
                if (offset != buffer.Length)
                    continue;

                if (RouteCProtocolCodec.TryDecodeBusinessInput(
                        buffer,
                        out uint raw,
                        out int frameID) &&
                    frameID >= 0)
                {
                    receiveSequence++;
                    latestRemoteFrameID = frameID;
                    _remoteInputs.Enqueue(new NetworkPacketArrival(
                        raw,
                        frameID,
                        receiveSequence,
                        System.Diagnostics.Stopwatch.GetTimestamp()));
                    PublishStatus(
                        NetworkSessionState.Running,
                        true,
                        playerIndex,
                        latestRemoteFrameID);
                }

                offset = 0;
                messages++;
            }

            return true;
        }

        private static bool IsTransientWriteBackpressure(
            SocketException exception)
        {
            return exception.SocketErrorCode == SocketError.WouldBlock ||
                   exception.SocketErrorCode ==
                       SocketError.NoBufferSpaceAvailable ||
                   exception.SocketErrorCode == SocketError.IOPending;
        }

        private void PublishStatus(
            NetworkSessionState state,
            bool hasStartedSession,
            int localPlayerIndex,
            int latestRemoteFrameID)
        {
            Volatile.Write(
                ref _status,
                new TcpStatusSnapshot(
                    state,
                    hasStartedSession,
                    localPlayerIndex,
                    latestRemoteFrameID));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(TcpClientTransport));
        }

        private sealed class TcpStatusSnapshot
        {
            public static readonly TcpStatusSnapshot Disconnected =
                new TcpStatusSnapshot(
                    NetworkSessionState.Disconnected,
                    false,
                    -1,
                    -1);

            public TcpStatusSnapshot(
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

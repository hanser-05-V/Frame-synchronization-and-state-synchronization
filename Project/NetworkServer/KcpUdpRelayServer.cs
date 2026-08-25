using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using FrameSyncDemo;

namespace FrameSyncServer
{
    public sealed class KcpUdpRelayServer : IDisposable
    {
        private const int DefaultPort = 8888;
        private const int MaximumSelectWaitMs = 10;
        private const int DisposeWaitMs = 300;

        private readonly int _intervalMs;
        private readonly int _port;
        private readonly KcpServerDiagnostics _diagnostics =
            new KcpServerDiagnostics();
        private Socket _socket;
        private Thread _worker;
        private ServerSessionRouter _router;
        private int _started;
        private int _stopRequested;
        private int _boundPort;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileTime
        {
            public uint LowDateTime;
            public uint HighDateTime;

            public long Ticks =>
                unchecked(((long)HighDateTime << 32) | LowDateTime);
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadTimes(
            IntPtr thread,
            out NativeFileTime creationTime,
            out NativeFileTime exitTime,
            out NativeFileTime kernelTime,
            out NativeFileTime userTime);

        public KcpUdpRelayServer(int intervalMs)
            : this(intervalMs, DefaultPort)
        {
        }

        public KcpUdpRelayServer(int intervalMs, int port)
        {
            _intervalMs = new RouteCKcpSettings(intervalMs).IntervalMs;
            if (port < 0 || port > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }
            _port = port;
        }

        public int BoundPort => Volatile.Read(ref _boundPort);
        public KcpServerDiagnostics Diagnostics => _diagnostics;

        public void Run()
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "KCP UDP relay server has already started.");
            }

            _worker = new Thread(WorkerMain)
            {
                IsBackground = true,
                Name = "Route C KCP UDP Relay"
            };
            _worker.Start();
        }

        public void RequestStop()
        {
            Interlocked.Exchange(ref _stopRequested, 1);
        }

        public bool WaitForStop(int millisecondsTimeout)
        {
            if (millisecondsTimeout < -1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(millisecondsTimeout));
            }

            Thread worker = _worker;
            if (worker == null || !worker.IsAlive)
            {
                return true;
            }
            return worker.Join(millisecondsTimeout);
        }

        public void Dispose()
        {
            RequestStop();
            WaitForStop(DisposeWaitMs);
        }

        private void WorkerMain()
        {
            Socket socket = null;
            var clock = Stopwatch.StartNew();
            bool cpuStartAvailable = TryGetCurrentThreadCpuTimeTicks(
                out long cpuStartTicks);
            try
            {
                var random = new CryptoRandomSource();
                _router = new ServerSessionRouter(
                    _diagnostics,
                    random.CreateSessionId,
                    random.CreateReconnectToken,
                    random.CreateConversation,
                    random.CreateNonce,
                    _intervalMs);
                socket = new Socket(
                    AddressFamily.InterNetwork,
                    SocketType.Dgram,
                    ProtocolType.Udp);
                socket.ExclusiveAddressUse = true;
                socket.Bind(new IPEndPoint(IPAddress.Any, _port));
                socket.Blocking = false;
                _socket = socket;
                Volatile.Write(
                    ref _boundPort,
                    ((IPEndPoint)socket.LocalEndPoint).Port);
                Console.WriteLine(
                    "[Server] KCP UDP listening on port " +
                    BoundPort + ".");

                while (Volatile.Read(ref _stopRequested) == 0)
                {
                    uint waitNowMs = unchecked(
                        (uint)clock.ElapsedMilliseconds);
                    uint deadline = _router.NextActionAt(waitNowMs);
                    uint waitMs = unchecked(
                        (int)(waitNowMs - deadline)) >= 0
                        ? 0u
                        : unchecked(deadline - waitNowMs);
                    if (waitMs > MaximumSelectWaitMs)
                    {
                        waitMs = MaximumSelectWaitMs;
                    }
                    var readable = new List<Socket>(1) { socket };
                    Socket.Select(
                        readable,
                        null,
                        null,
                        (int)waitMs * 1000);

                    long sliceStartTimestamp = Stopwatch.GetTimestamp();
                    var budget = new KcpServerRoundBudget(
                        _diagnostics,
                        sliceStartTimestamp);
                    uint nowMs = unchecked((uint)clock.ElapsedMilliseconds);
                    DrainDatagrams(socket, nowMs, budget);
                    nowMs = unchecked((uint)clock.ElapsedMilliseconds);
                    _router.TickWithBudget(
                        nowMs,
                        Stopwatch.GetTimestamp(),
                        budget);
                    FlushActions(socket);
                }
            }
            catch (Exception exception)
            {
                var socketException = exception as SocketException;
                string category = socketException == null
                    ? exception.GetType().Name
                    : "SocketException-" + socketException.SocketErrorCode;
                _diagnostics.RecordWorkerFault(category);
            }
            finally
            {
                if (socket != null)
                {
                    socket.Dispose();
                }
                _socket = null;
                bool cpuEndAvailable = TryGetCurrentThreadCpuTimeTicks(
                    out long cpuEndTicks);
                bool cpuMeasurementAvailable =
                    cpuStartAvailable && cpuEndAvailable;
                long cpuElapsedTicks = cpuMeasurementAvailable
                    ? Math.Max(0L, cpuEndTicks - cpuStartTicks)
                    : 0L;
                _diagnostics.RecordWorkerCpuTime(
                    cpuElapsedTicks,
                    cpuMeasurementAvailable);
            }
        }

        private static bool TryGetCurrentThreadCpuTimeTicks(out long ticks)
        {
            ticks = 0L;
            try
            {
                if (!GetThreadTimes(
                        GetCurrentThread(),
                        out NativeFileTime creationTime,
                        out NativeFileTime exitTime,
                        out NativeFileTime kernelTime,
                        out NativeFileTime userTime))
                {
                    return false;
                }

                ticks = kernelTime.Ticks + userTime.Ticks;
                return ticks >= 0L;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        private void DrainDatagrams(
            Socket socket,
            uint nowMs,
            KcpServerRoundBudget budget)
        {
            var buffer = new byte[RouteCProtocolConstants.MaximumDatagramSize + 1];
            while (socket.Poll(0, SelectMode.SelectRead))
            {
                if (!budget.HasLiveSliceBudget(Stopwatch.GetTimestamp()) ||
                    !budget.TryTakeDatagram())
                {
                    break;
                }

                EndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
                int count;
                try
                {
                    count = socket.ReceiveFrom(buffer, ref remoteEndpoint);
                }
                catch (SocketException exception)
                    when (IsTransientReceive(exception.SocketErrorCode))
                {
                    break;
                }

                if (!(remoteEndpoint is IPEndPoint endpoint))
                {
                    continue;
                }
                if (!RouteCProtocolCodec.TryDecode(
                        buffer,
                        count,
                        out RouteCProtocolMessage message,
                        out RouteCProtocolDropReason reason))
                {
                    if (reason != RouteCProtocolDropReason.None)
                    {
                        _diagnostics.RecordDrop(reason);
                    }
                    continue;
                }

                DispatchDatagram(socket, endpoint, message, nowMs);
            }
        }

        private void DispatchDatagram(
            Socket socket,
            IPEndPoint endpoint,
            RouteCProtocolMessage message,
            uint nowMs)
        {
            switch (message.MessageType)
            {
                case RouteCMessageType.Hello:
                    HandleHello(socket, endpoint, message, nowMs);
                    break;
                case RouteCMessageType.Ready:
                    _router.HandleReady(message, endpoint, nowMs);
                    break;
                case RouteCMessageType.KcpData:
                    _router.HandleKcpData(message, endpoint, nowMs);
                    break;
                case RouteCMessageType.Heartbeat:
                    _router.HandleHeartbeat(message, endpoint, nowMs);
                    break;
                case RouteCMessageType.Disconnect:
                    _router.HandleDisconnect(message, endpoint, nowMs);
                    break;
                case RouteCMessageType.ResumeState:
                    _router.HandleResumeState(message, endpoint, nowMs);
                    break;
                case RouteCMessageType.ResumeComplete:
                    _router.HandleResumeComplete(message, endpoint, nowMs);
                    break;
                case RouteCMessageType.ResumeRejected:
                    _router.HandleResumeRejected(message, endpoint, nowMs);
                    break;
                case RouteCMessageType.ResumeProbe:
                case RouteCMessageType.ResumeAccepted:
                    _router.HandleServerOnlyResumeControl(message, endpoint);
                    break;
            }
        }

        private void HandleHello(
            Socket socket,
            IPEndPoint endpoint,
            RouteCProtocolMessage message,
            uint nowMs)
        {
            byte[] welcomeDatagram;
            KcpServerSession session;
            ServerHandshakeDisposition disposition;
            if (message.SessionId.IsZero &&
                message.Generation == 0u &&
                RouteCProtocolCodec.TryDecodeInitialHello(
                    message.Payload,
                    out byte[] clientNonce))
            {
                disposition = _router.HandleInitialHelloAt(
                    endpoint,
                    clientNonce,
                    nowMs,
                    out welcomeDatagram,
                    out session);
            }
            else if (!message.SessionId.IsZero &&
                     message.Generation != 0u &&
                     RouteCProtocolCodec.TryDecodeReconnectHello(
                         message.Payload,
                         out byte[] newClientNonce,
                         out byte[] oldReconnectToken))
            {
                disposition = _router.HandleReconnectHelloAt(
                    endpoint,
                    message.SessionId,
                    message.Generation,
                    oldReconnectToken,
                    newClientNonce,
                    nowMs,
                    out welcomeDatagram,
                    out session);
            }
            else
            {
                return;
            }

            if (disposition == ServerHandshakeDisposition.Allocated ||
                disposition == ServerHandshakeDisposition.IdempotentRetry ||
                disposition == ServerHandshakeDisposition.Reconnected)
            {
                socket.SendTo(welcomeDatagram, endpoint);
            }
        }

        private void FlushActions(Socket socket)
        {
            KcpServerDatagram[] actions = _router.DrainActions();
            for (int index = 0; index < actions.Length; index++)
            {
                KcpServerDatagram action = actions[index];
                socket.SendTo(action.Datagram, action.Endpoint);
            }
        }

        private static bool IsTransientReceive(SocketError error)
        {
            return error == SocketError.WouldBlock ||
                   error == SocketError.IOPending ||
                   error == SocketError.NoBufferSpaceAvailable ||
                   error == SocketError.Interrupted ||
                   error == SocketError.ConnectionReset;
        }
    }
}

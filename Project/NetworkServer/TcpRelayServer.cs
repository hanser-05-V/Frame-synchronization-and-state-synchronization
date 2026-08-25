using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace FrameSyncServer
{
    internal sealed class TcpRelayServer : IDisposable
    {
        private const int Port = 8888;

        private readonly NetworkLabOptions _options;
        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private readonly object _clientsLock = new object();
        private readonly object _schedulerLock = new object();
        private readonly object _lifecycleLock = new object();
        private readonly AutoResetEvent _schedulerSignal =
            new AutoResetEvent(false);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly long[] _senderSequences = { -1L, -1L };
        private readonly DeterministicPacketScheduler _scheduler;
        private readonly NetworkTraceWriter _traceWriter;
        private readonly Thread[] _receiveThreads = new Thread[2];

        private TcpListener _listener;
        private Thread _schedulerThread;
        private volatile bool _running;
        private volatile bool _stopRequested;
        private volatile bool _disposed;
        private bool _schedulerSignalDisposed;

        public TcpRelayServer(NetworkLabOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _scheduler = new DeterministicPacketScheduler(options.Profile);
            _traceWriter = new NetworkTraceWriter(
                options.DecisionTracePath,
                options.TimingTracePath);
        }

        public void Run()
        {
            ThrowIfDisposed();
            if (_stopRequested)
            {
                return;
            }
            if (_running)
            {
                throw new InvalidOperationException("The TCP relay server is already running.");
            }

            _running = true;
            Console.CancelKeyPress += OnCancelKeyPress;
            var listener = new TcpListener(IPAddress.Any, Port);
            lock (_lifecycleLock)
            {
                if (_stopRequested)
                {
                    _running = false;
                    return;
                }

                _listener = listener;
                listener.Start();
            }
            if (_stopRequested)
            {
                return;
            }
            Console.WriteLine(
                "[Server] Listening on port " + Port +
                " (base delay=" + _options.Profile.BaseDelayMs + "ms)");

            for (int index = 0; index < 2; index++)
            {
                Console.WriteLine("[Server] Waiting for client " + index + "...");
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (SocketException) when (!_running)
                {
                    return;
                }
                catch (ObjectDisposedException) when (!_running)
                {
                    return;
                }
                catch (InvalidOperationException) when (!_running)
                {
                    return;
                }
                if (!_running)
                {
                    client.Close();
                    return;
                }
                ConfigureLowLatency(client);
                lock (_clientsLock)
                {
                    _clients.Add(client);
                }
                Console.WriteLine("[Server] Client " + index + " connected.");
            }

            for (int index = 0; index < 2; index++)
            {
                NetworkStream stream = _clients[index].GetStream();
                stream.WriteByte((byte)index);
                stream.Flush();
            }

            _schedulerThread = new Thread(SchedulerLoop)
            {
                IsBackground = true,
                Name = "NetworkLabScheduler"
            };
            _schedulerThread.Start();

            for (int index = 0; index < 2; index++)
            {
                int senderIndex = index;
                TcpClient client = _clients[index];
                var receiveThread = new Thread(
                    () => ReceiveLoop(senderIndex, client))
                {
                    IsBackground = true,
                    Name = "ClientReceive" + senderIndex
                };
                _receiveThreads[index] = receiveThread;
                receiveThread.Start();
            }

            Console.WriteLine("[Server] Both clients released from frame 0.");
            Console.WriteLine("Press Ctrl+C to exit.");
            while (_running)
            {
                Thread.Sleep(100);
            }
        }

        public void Dispose()
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            Console.CancelKeyPress -= OnCancelKeyPress;
            RequestStop();

            lock (_clientsLock)
            {
                foreach (TcpClient client in _clients)
                {
                    client.Close();
                }
                _clients.Clear();
            }

            JoinThread(_schedulerThread);
            foreach (Thread receiveThread in _receiveThreads)
            {
                JoinThread(receiveThread);
            }

            _traceWriter.Dispose();
            lock (_lifecycleLock)
            {
                _schedulerSignal.Dispose();
                _schedulerSignalDisposed = true;
            }
            _clock.Stop();
        }

        private static void ConfigureLowLatency(TcpClient client)
        {
            if (client == null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            client.NoDelay = true;
        }

        private static int GetWaitMilliseconds(long nowMs, long nextDueMs)
        {
            if (nextDueMs < 0)
            {
                return Timeout.Infinite;
            }

            long remainingMs = nextDueMs - nowMs;
            if (remainingMs <= 0)
            {
                return 0;
            }
            if (remainingMs > int.MaxValue)
            {
                return int.MaxValue;
            }

            return (int)remainingMs;
        }

        private static bool TryReadExactly(NetworkStream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int bytesRead = stream.Read(
                    buffer,
                    offset,
                    buffer.Length - offset);
                if (bytesRead <= 0)
                {
                    return false;
                }
                offset += bytesRead;
            }

            return true;
        }

        private static void JoinThread(Thread thread)
        {
            if (thread == null || thread == Thread.CurrentThread)
            {
                return;
            }

            thread.Join();
        }

        private void ReceiveLoop(int senderIndex, TcpClient client)
        {
            var buffer = new byte[8];
            try
            {
                NetworkStream stream = client.GetStream();
                while (_running && TryReadExactly(stream, buffer))
                {
                    uint raw = BitConverter.ToUInt32(buffer, 0);
                    int frameID = BitConverter.ToInt32(buffer, 4);
                    long senderSequence = Interlocked.Increment(
                        ref _senderSequences[senderIndex]);
                    long enqueuedAtMs = _clock.ElapsedMilliseconds;

                    lock (_schedulerLock)
                    {
                        _scheduler.Schedule(
                            enqueuedAtMs,
                            senderIndex,
                            senderSequence,
                            raw,
                            frameID);
                    }
                    _schedulerSignal.Set();
                }
            }
            catch (Exception exception)
            {
                if (_running)
                {
                    Console.WriteLine(
                        "[Client" + senderIndex + "] disconnected: " +
                        exception.Message);
                }
            }
        }

        private void SchedulerLoop()
        {
            long batchId = 0;
            while (_running)
            {
                var dueDeliveries = new List<DeterministicPacketScheduler.Delivery>();
                int waitMilliseconds;
                lock (_schedulerLock)
                {
                    long nowMs = _clock.ElapsedMilliseconds;
                    DeterministicPacketScheduler.Delivery delivery;
                    while (_scheduler.TryDequeueDue(nowMs, out delivery))
                    {
                        dueDeliveries.Add(delivery);
                    }

                    waitMilliseconds = GetWaitMilliseconds(
                        nowMs,
                        _scheduler.NextDueTimestampMs);
                }

                if (dueDeliveries.Count == 0)
                {
                    _schedulerSignal.WaitOne(waitMilliseconds);
                    continue;
                }

                long currentBatchId = batchId++;
                foreach (DeterministicPacketScheduler.Delivery delivery in dueDeliveries)
                {
                    if (!_running)
                    {
                        break;
                    }

                    if (delivery.CopyIndex == 0)
                    {
                        _traceWriter.RecordDecision(
                            delivery.SenderIndex,
                            delivery.SenderSequence,
                            delivery.FrameID,
                            delivery.Raw,
                            delivery.Decision);
                    }

                    Broadcast(
                        delivery.Raw,
                        delivery.SenderIndex,
                        delivery.FrameID);
                    _traceWriter.RecordTiming(
                        delivery,
                        _clock.ElapsedMilliseconds,
                        currentBatchId);
                }
            }
        }

        private void Broadcast(uint raw, int senderIndex, int frameID)
        {
            var data = new byte[8];
            BitConverter.GetBytes(raw).CopyTo(data, 0);
            BitConverter.GetBytes(frameID).CopyTo(data, 4);

            TcpClient[] clients;
            TcpClient sender;
            lock (_clientsLock)
            {
                if (senderIndex < 0 || senderIndex >= _clients.Count)
                {
                    return;
                }

                clients = _clients.ToArray();
                sender = _clients[senderIndex];
            }

            foreach (TcpClient client in clients)
            {
                if (client == sender)
                {
                    continue;
                }

                try
                {
                    client.GetStream().Write(data, 0, data.Length);
                }
                catch (Exception exception)
                {
                    if (_running)
                    {
                        Console.WriteLine(
                            "[Server] Send failed: " + exception.Message);
                    }
                }
            }
        }

        private void OnCancelKeyPress(
            object sender,
            ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            RequestStop();
        }

        private void RequestStop()
        {
            _stopRequested = true;
            _running = false;
            TcpListener listener;
            lock (_lifecycleLock)
            {
                if (!_schedulerSignalDisposed)
                {
                    _schedulerSignal.Set();
                }

                listener = _listener;
                _listener = null;
            }
            if (listener != null)
            {
                listener.Stop();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(TcpRelayServer));
            }
        }
    }
}

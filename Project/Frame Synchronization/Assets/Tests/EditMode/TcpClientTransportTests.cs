using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class TcpClientTransportTests
    {
        [Test]
        public void Worker_PreservesPlayerIndexAndExactEightByteFraming()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            try
            {
                using (var transport = new TcpClientTransport())
                {
                    var stopwatch = Stopwatch.StartNew();
                    transport.Start("127.0.0.1", port);
                    stopwatch.Stop();
                    Assert.Less(stopwatch.ElapsedMilliseconds, 100L);

                    Assert.IsTrue(SpinWait.SpinUntil(listener.Pending, 1000));
                    using (TcpClient serverClient = listener.AcceptTcpClient())
                    using (NetworkStream serverStream = serverClient.GetStream())
                    {
                        serverClient.ReceiveTimeout = 1000;
                        serverStream.WriteByte(1);
                        Assert.IsTrue(SpinWait.SpinUntil(
                            () => transport.IsRunning,
                            1000));
                        Assert.AreEqual(1, transport.LocalPlayerIndex);
                        Assert.IsTrue(transport.HasStartedSession);

                        Assert.IsTrue(transport.TryEnqueueLocalInput(
                            0x12345678u,
                            42));
                        var outbound = new byte[8];
                        Assert.IsTrue(NetworkStreamReader.TryReadExactly(
                            serverStream,
                            outbound,
                            outbound.Length));
                        Assert.AreEqual(0x12345678u, BitConverter.ToUInt32(outbound, 0));
                        Assert.AreEqual(42, BitConverter.ToInt32(outbound, 4));

                        byte[] inbound = RouteCProtocolCodec.EncodeBusinessInput(
                            0x87654321u,
                            43);
                        serverStream.Write(inbound, 0, 3);
                        serverStream.Write(inbound, 3, inbound.Length - 3);
                        Assert.IsTrue(SpinWait.SpinUntil(
                            () => transport.TryDequeueRemoteInput(
                                out NetworkPacketArrival arrival) &&
                                arrival.Raw == 0x87654321u &&
                                arrival.RemoteFrameID == 43,
                            1000));
                    }

                    transport.RequestStop();
                    Assert.IsTrue(transport.WaitForStop(
                        NetworkConfig.InitialKcpIntervalMs +
                        NetworkConfig.WorkerStopMarginMs));
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        [Test]
        public void PeerEof_PublishesTerminalEventAndWorkerStops()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            try
            {
                using (var transport = new TcpClientTransport())
                {
                    transport.Start("127.0.0.1", port);
                    Assert.IsTrue(SpinWait.SpinUntil(listener.Pending, 1000));
                    using (TcpClient serverClient = listener.AcceptTcpClient())
                    {
                        serverClient.GetStream().WriteByte(0);
                        Assert.IsTrue(SpinWait.SpinUntil(
                            () => transport.IsRunning,
                            1000));
                        Assert.IsTrue(transport.TryDequeueEvent(out _));
                    }

                    Assert.IsTrue(SpinWait.SpinUntil(
                        () => transport.State == NetworkSessionState.Terminated,
                        1000));
                    Assert.IsTrue(transport.TryDequeueEvent(
                        out NetworkTransportEvent terminal));
                    Assert.AreEqual(
                        NetworkTransportEventReason.ConnectionClosed,
                        terminal.Reason);
                    Assert.IsTrue(transport.WaitForStop(
                        NetworkConfig.InitialKcpIntervalMs +
                        NetworkConfig.WorkerStopMarginMs));
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        [Test]
        [Timeout(10000)]
        public void Backpressure_PeerDoesNotRead_RequestStopJoinsAndCleansUpWithinBound()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Server.ReceiveBufferSize = 1024;
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var transport = new TcpClientTransport();
            TcpClient serverClient = null;
            Thread inboundFeeder = null;
            int stopInboundFeeder = 0;

            try
            {
                transport.Start("127.0.0.1", port);
                Assert.IsTrue(SpinWait.SpinUntil(listener.Pending, 1000));
                serverClient = listener.AcceptTcpClient();
                serverClient.ReceiveBufferSize = 1024;
                NetworkStream serverStream = serverClient.GetStream();
                serverStream.WriteByte(0);
                Assert.IsTrue(SpinWait.SpinUntil(
                    () => transport.IsRunning,
                    1000));
                var ownedClient = (TcpClient)GetPrivateField(
                    transport,
                    "_tcp");
                ownedClient.SendBufferSize = 1024;
                byte[] ignoredInbound =
                    RouteCProtocolCodec.EncodeBusinessInput(0u, -1);
                inboundFeeder = new Thread(() =>
                {
                    try
                    {
                        while (Volatile.Read(ref stopInboundFeeder) == 0)
                        {
                            serverStream.Write(
                                ignoredInbound,
                                0,
                                ignoredInbound.Length);
                        }
                    }
                    catch (Exception)
                    {
                    }
                })
                {
                    IsBackground = true,
                    Name = "TCP Backpressure Test Feeder"
                };
                inboundFeeder.Start();

                var total = Stopwatch.StartNew();
                long continuouslyFullSinceMs = -1L;
                int frameID = 0;
                while (total.ElapsedMilliseconds < 7000 &&
                       (continuouslyFullSinceMs < 0L ||
                        total.ElapsedMilliseconds - continuouslyFullSinceMs <
                        250L))
                {
                    if (transport.TryEnqueueLocalInput(
                            unchecked((uint)frameID),
                            frameID))
                    {
                        frameID++;
                        continuouslyFullSinceMs = -1L;
                    }
                    else
                    {
                        if (continuouslyFullSinceMs < 0L)
                            continuouslyFullSinceMs = total.ElapsedMilliseconds;
                        Thread.Yield();
                    }
                }

                Assert.GreaterOrEqual(
                    continuouslyFullSinceMs,
                    0L,
                    "The test must first establish real sustained backpressure.");
                Assert.GreaterOrEqual(
                    total.ElapsedMilliseconds - continuouslyFullSinceMs,
                    250L,
                    "The bounded input queue must remain full while the peer does not read.");
                Assert.AreEqual(
                    NetworkSessionState.Running,
                    transport.State,
                    "Queue rejection must come from backpressure, not early termination.");
                Assert.IsTrue(transport.HasStartedSession);
                Assert.That(transport.LocalPlayerIndex, Is.InRange(0, 1));
                var worker = (Thread)GetPrivateField(transport, "_worker");
                Assert.IsTrue(
                    worker.IsAlive,
                    "The worker must remain alive until RequestStop is issued.");
                while (transport.TryDequeueEvent(
                    out NetworkTransportEvent transportEvent))
                {
                    Assert.AreNotEqual(
                        NetworkTransportEventReason.WorkerFault,
                        transportEvent.Reason,
                        "Backpressure must not fault the worker.");
                    Assert.AreNotEqual(
                        NetworkSessionState.Terminated,
                        transportEvent.State,
                        "No terminal event may precede RequestStop.");
                }

                transport.RequestStop();
                var stop = Stopwatch.StartNew();
                Assert.IsTrue(
                    transport.WaitForStop(
                        NetworkConfig.InitialKcpIntervalMs +
                        NetworkConfig.WorkerStopMarginMs),
                    "Backpressure must not trap the worker inside a synchronous write.");
                Assert.LessOrEqual(
                    stop.ElapsedMilliseconds,
                    NetworkConfig.InitialKcpIntervalMs +
                    NetworkConfig.WorkerStopMarginMs +
                    100L);
                Assert.AreEqual(
                    NetworkSessionState.Terminated,
                    transport.State);
                Assert.IsNull(GetPrivateField(transport, "_stream"));
                Assert.IsNull(GetPrivateField(transport, "_tcp"));
            }
            finally
            {
                Interlocked.Exchange(ref stopInboundFeeder, 1);
                serverClient?.Close();
                inboundFeeder?.Join(1000);
                transport.RequestStop();
                transport.WaitForStop(1000);
                transport.Dispose();
                listener.Stop();
            }
        }

        private static object GetPrivateField(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "Missing field: " + name);
            return field.GetValue(target);
        }
    }
}

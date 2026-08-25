using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class KcpUdpClientWorkerTests
    {
        [Test]
        public void Start_ReturnsImmediatelyAndWorkerSendsInitialHello()
        {
            using (var server = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp))
            using (var transport = new KcpUdpClientTransport(
                new StopwatchMonotonicClock(),
                () => Sequence(0x20, RouteCProtocolConstants.NonceSize)))
            {
                server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                server.ReceiveTimeout = 1000;
                int port = ((IPEndPoint)server.LocalEndPoint).Port;
                var stopwatch = Stopwatch.StartNew();

                transport.Start("127.0.0.1", port);

                stopwatch.Stop();
                Assert.Less(stopwatch.ElapsedMilliseconds, 100L);
                var bytes = new byte[RouteCProtocolConstants.MaximumDatagramSize];
                int count = server.Receive(bytes);
                Assert.IsTrue(RouteCProtocolCodec.TryDecode(
                    bytes,
                    count,
                    out RouteCProtocolMessage message,
                    out RouteCProtocolDropReason reason),
                    reason.ToString());
                Assert.AreEqual(RouteCMessageType.Hello, message.MessageType);
                Assert.IsTrue(RouteCProtocolCodec.TryDecodeInitialHello(
                    message.Payload,
                    out byte[] nonce));
                CollectionAssert.AreEqual(
                    Sequence(0x20, RouteCProtocolConstants.NonceSize),
                    nonce);

                transport.RequestStop();
                Assert.IsTrue(transport.WaitForStop(
                    NetworkConfig.InitialKcpIntervalMs +
                    NetworkConfig.WorkerStopMarginMs));
            }
        }

        [Test]
        public void RequestStop_BeforeStartOnlyRecordsRequest()
        {
            using (var transport = new KcpUdpClientTransport(
                new StopwatchMonotonicClock(),
                () => Sequence(0x20, RouteCProtocolConstants.NonceSize)))
            {
                transport.RequestStop();

                Assert.AreEqual(NetworkSessionState.Disconnected, transport.State);
                Assert.IsTrue(transport.WaitForStop(
                    NetworkConfig.InitialKcpIntervalMs +
                    NetworkConfig.WorkerStopMarginMs));
            }
        }

        [Test]
        public void DelayedListener_AfterIcmpReset_ReceivesHelloRetryWithoutWorkerFault()
        {
            int port;
            using (var reservation = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp))
            {
                reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                port = ((IPEndPoint)reservation.LocalEndPoint).Port;
            }

            using (var transport = new KcpUdpClientTransport(
                new StopwatchMonotonicClock(),
                () => Sequence(0x20, RouteCProtocolConstants.NonceSize)))
            {
                transport.Start("127.0.0.1", port);
                SpinWait.SpinUntil(
                    () => transport.State == NetworkSessionState.Terminated,
                    100);

                using (var server = new Socket(
                    AddressFamily.InterNetwork,
                    SocketType.Dgram,
                    ProtocolType.Udp))
                {
                    server.Bind(new IPEndPoint(IPAddress.Loopback, port));
                    Assert.IsTrue(
                        server.Poll(1500000, SelectMode.SelectRead),
                        "The worker must retry Hello after reset/refused packet loss.");
                    var buffer = new byte[
                        RouteCProtocolConstants.MaximumDatagramSize];
                    int count = server.Receive(buffer);
                    Assert.IsTrue(RouteCProtocolCodec.TryDecode(
                        buffer,
                        count,
                        out RouteCProtocolMessage message,
                        out RouteCProtocolDropReason reason),
                        reason.ToString());
                    Assert.AreEqual(RouteCMessageType.Hello, message.MessageType);
                }

                Assert.AreEqual(
                    NetworkSessionState.Handshaking,
                    transport.State);
                while (transport.TryDequeueEvent(
                    out NetworkTransportEvent transportEvent))
                {
                    Assert.AreNotEqual(
                        NetworkTransportEventReason.WorkerFault,
                        transportEvent.Reason);
                }

                transport.RequestStop();
                Assert.IsTrue(transport.WaitForStop(
                    NetworkConfig.InitialKcpIntervalMs +
                    NetworkConfig.WorkerStopMarginMs));
            }
        }

        [Test]
        public void LiveWorker_CompletesHandshakeAndRelaysEightByteKcpMessages()
        {
            using (var server = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp))
            {
                server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                server.ReceiveTimeout = 1000;
                int port = ((IPEndPoint)server.LocalEndPoint).Port;
                var clock = new StopwatchMonotonicClock();
                using (var transport = new KcpUdpClientTransport(
                    clock,
                    () => Sequence(0x20, RouteCProtocolConstants.NonceSize)))
                {
                    transport.Start("127.0.0.1", port);

                    var receiveBuffer = new byte[
                        RouteCProtocolConstants.MaximumDatagramSize];
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int helloCount = server.ReceiveFrom(
                        receiveBuffer,
                        ref remote);
                    Assert.IsTrue(RouteCProtocolCodec.TryDecode(
                        receiveBuffer,
                        helloCount,
                        out RouteCProtocolMessage hello,
                        out RouteCProtocolDropReason reason),
                        reason.ToString());
                    Assert.IsTrue(RouteCProtocolCodec.TryDecodeInitialHello(
                        hello.Payload,
                        out byte[] nonce));

                    var session = new RouteCSessionId(3UL, 4UL);
                    const uint generation = 1u;
                    const uint conversation = 77u;
                    byte[] token = Sequence(
                        0x60,
                        RouteCProtocolConstants.ReconnectTokenSize);
                    SendEnvelope(
                        server,
                        remote,
                        RouteCMessageType.Welcome,
                        session,
                        generation,
                        RouteCProtocolCodec.EncodeWelcome(
                            nonce,
                            0,
                            conversation,
                            token,
                            RouteCProtocolConstants.HeartbeatSilenceMs,
                            RouteCProtocolConstants.DisconnectTimeoutMs,
                            false));
                    SendEnvelope(
                        server,
                        remote,
                        RouteCMessageType.Start,
                        session,
                        generation,
                        RouteCProtocolCodec.EncodeStart(0));
                    Assert.IsTrue(SpinWait.SpinUntil(
                        () => transport.IsRunning,
                        1000));

                    var serverKcp = new RouteCKcpSession(
                        conversation,
                        new RouteCKcpSettings(
                            RouteCProtocolConstants.InitialKcpIntervalMs),
                        (bytes, count) => SendEnvelope(
                            server,
                            remote,
                            RouteCMessageType.KcpData,
                            session,
                            generation,
                            Copy(bytes, count)));

                    Assert.IsTrue(transport.TryEnqueueLocalInput(
                        0x12345678u,
                        42));
                    uint receivedRaw = 0u;
                    int receivedFrame = -1;
                    Assert.IsTrue(PumpServerUntil(
                        server,
                        serverKcp,
                        session,
                        generation,
                        clock,
                        () => serverKcp.TryReceiveBusinessInput(
                            out receivedRaw,
                            out receivedFrame),
                        1500));
                    Assert.AreEqual(0x12345678u, receivedRaw);
                    Assert.AreEqual(42, receivedFrame);

                    Assert.AreEqual(0, serverKcp.SendBusinessInput(
                        0x87654321u,
                        43));
                    NetworkPacketArrival arrival = default;
                    Assert.IsTrue(PumpServerUntil(
                        server,
                        serverKcp,
                        session,
                        generation,
                        clock,
                        () => transport.TryDequeueRemoteInput(out arrival),
                        1500));
                    Assert.AreEqual(0x87654321u, arrival.Raw);
                    Assert.AreEqual(43, arrival.RemoteFrameID);
                    Assert.IsNotNull(transport.Diagnostics);

                    transport.RequestStop();
                    Assert.IsTrue(transport.WaitForStop(
                        NetworkConfig.InitialKcpIntervalMs +
                        NetworkConfig.WorkerStopMarginMs));
                }
            }
        }

        [Test]
        public void SessionStarted_WhenPubliclyDequeueable_StatusIsAlreadyRunning()
        {
            using (var server = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp))
            {
                server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                server.ReceiveTimeout = 1000;
                int port = ((IPEndPoint)server.LocalEndPoint).Port;
                var clock = new WorkerGateClock();
                using (var transport = new KcpUdpClientTransport(
                    clock,
                    () => Sequence(0x20, RouteCProtocolConstants.NonceSize)))
                {
                    transport.Start("127.0.0.1", port);
                    var buffer = new byte[
                        RouteCProtocolConstants.MaximumDatagramSize];
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int count = server.ReceiveFrom(buffer, ref remote);
                    Assert.IsTrue(RouteCProtocolCodec.TryDecode(
                        buffer,
                        count,
                        out RouteCProtocolMessage hello,
                        out _));
                    Assert.IsTrue(RouteCProtocolCodec.TryDecodeInitialHello(
                        hello.Payload,
                        out byte[] nonce));

                    var session = new RouteCSessionId(41UL, 42UL);
                    const uint generation = 1u;
                    SendEnvelope(
                        server,
                        remote,
                        RouteCMessageType.Welcome,
                        session,
                        generation,
                        RouteCProtocolCodec.EncodeWelcome(
                            nonce,
                            1,
                            91u,
                            Sequence(
                                0x60,
                                RouteCProtocolConstants.ReconnectTokenSize),
                            RouteCProtocolConstants.HeartbeatSilenceMs,
                            RouteCProtocolConstants.DisconnectTimeoutMs,
                            false));
                    Assert.IsTrue(SpinWait.SpinUntil(
                        () => transport.State ==
                              NetworkSessionState.AwaitingReady,
                        1000));
                    Assert.IsTrue(ReceiveEnvelopeTypeWithin(
                        server,
                        RouteCMessageType.Ready,
                        1000));
                    while (transport.TryDequeueEvent(out _))
                    {
                    }

                    clock.ObserveNextRead();
                    Assert.IsTrue(clock.WaitForObservedRead(1000));
                    clock.BlockAfterReads(3);
                    SendEnvelope(
                        server,
                        remote,
                        RouteCMessageType.Start,
                        session,
                        generation,
                        RouteCProtocolCodec.EncodeStart(0));
                    Assert.IsTrue(
                        clock.WaitUntilBlocked(1000),
                        "The worker must reach the controlled post-event scheduling point.");

                    try
                    {
                        bool dequeuedWhileBlocked = TryDequeueEventReason(
                            transport,
                            NetworkTransportEventReason.SessionStarted,
                            out _);
                        if (dequeuedWhileBlocked)
                            AssertRunningStatus(transport);

                        clock.Release();
                        Assert.IsTrue(SpinWait.SpinUntil(
                            () => dequeuedWhileBlocked ||
                                  TryDequeueEventReason(
                                      transport,
                                      NetworkTransportEventReason.SessionStarted,
                                      out _),
                            1000));
                        AssertRunningStatus(transport);
                    }
                    finally
                    {
                        clock.Release();
                    }

                    transport.RequestStop();
                    Assert.IsTrue(transport.WaitForStop(
                        NetworkConfig.InitialKcpIntervalMs +
                        NetworkConfig.WorkerStopMarginMs));
                }
            }
        }

        [Test]
        public void OutboundHistoryFault_WhenPublished_StatusIsAlreadyTerminated()
        {
            using (var server = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp))
            {
                server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                server.ReceiveTimeout = 1000;
                int port = ((IPEndPoint)server.LocalEndPoint).Port;
                var clock = new WorkerGateClock();
                using (var transport = new KcpUdpClientTransport(
                    clock,
                    () => Sequence(0x20, RouteCProtocolConstants.NonceSize)))
                {
                    transport.Start("127.0.0.1", port);
                    var buffer = new byte[
                        RouteCProtocolConstants.MaximumDatagramSize];
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int count = server.ReceiveFrom(buffer, ref remote);
                    Assert.IsTrue(RouteCProtocolCodec.TryDecode(
                        buffer,
                        count,
                        out RouteCProtocolMessage hello,
                        out _));
                    Assert.IsTrue(RouteCProtocolCodec.TryDecodeInitialHello(
                        hello.Payload,
                        out byte[] nonce));

                    var session = new RouteCSessionId(43UL, 44UL);
                    const uint generation = 1u;
                    SendEnvelope(
                        server,
                        remote,
                        RouteCMessageType.Welcome,
                        session,
                        generation,
                        RouteCProtocolCodec.EncodeWelcome(
                            nonce,
                            0,
                            92u,
                            Sequence(
                                0x60,
                                RouteCProtocolConstants.ReconnectTokenSize),
                            RouteCProtocolConstants.HeartbeatSilenceMs,
                            RouteCProtocolConstants.DisconnectTimeoutMs,
                            false));
                    Assert.IsTrue(SpinWait.SpinUntil(
                        () => transport.State ==
                              NetworkSessionState.AwaitingReady,
                        1000));
                    Assert.IsTrue(ReceiveEnvelopeTypeWithin(
                        server,
                        RouteCMessageType.Ready,
                        1000));
                    while (transport.TryDequeueEvent(out _))
                    {
                    }

                    clock.ObserveNextRead();
                    Assert.IsTrue(clock.WaitForObservedRead(1000));
                    clock.BlockAfterReads(3);
                    SendEnvelope(
                        server,
                        remote,
                        RouteCMessageType.Start,
                        session,
                        generation,
                        RouteCProtocolCodec.EncodeStart(0));
                    Assert.IsTrue(clock.WaitUntilBlocked(1000));

                    try
                    {
                        clock.ReleaseAndBlockAfterReads(2);
                        Assert.IsTrue(clock.WaitUntilBlocked(1000));
                        AssertRunningStatus(transport);
                        Assert.IsTrue(transport.TryEnqueueLocalInput(0x11u, 7));
                        Assert.IsTrue(transport.TryEnqueueLocalInput(0x22u, 7));

                        clock.ReleaseAndBlockAfterReads(2);
                        Assert.IsTrue(
                            clock.WaitUntilBlocked(1000),
                            "The worker must reach the outbound-history fault timestamp.");
                        Assert.AreEqual(
                            NetworkSessionState.Terminated,
                            transport.State,
                            "Terminal status must be published before the terminal event can be published.");
                        Assert.IsFalse(transport.HasStartedSession);

                        clock.Release();
                        Assert.IsTrue(SpinWait.SpinUntil(
                            () => TryDequeueEventReason(
                                transport,
                                NetworkTransportEventReason.OutboundHistoryFault,
                                out _),
                            1000));
                        Assert.AreEqual(
                            NetworkSessionState.Terminated,
                            transport.State);
                        Assert.IsFalse(transport.HasStartedSession);
                    }
                    finally
                    {
                        clock.Release();
                    }

                    Assert.IsTrue(transport.WaitForStop(
                        NetworkConfig.InitialKcpIntervalMs +
                        NetworkConfig.WorkerStopMarginMs));
                }
            }
        }

        [Test]
        public void ResumeRequired_WaitsForWorkerReadinessCommandBeforeReadyAndRetriesAfterTwoHundredFiftyMilliseconds()
        {
            using (var server = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp))
            {
                server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)server.LocalEndPoint).Port;
                var clock = new ManualClock();
                using (var transport = new KcpUdpClientTransport(
                    clock,
                    () => Sequence(0x20, RouteCProtocolConstants.NonceSize)))
                {
                    transport.Start("127.0.0.1", port);
                    var buffer = new byte[
                        RouteCProtocolConstants.MaximumDatagramSize];
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int count = server.ReceiveFrom(buffer, ref remote);
                    Assert.IsTrue(RouteCProtocolCodec.TryDecode(
                        buffer,
                        count,
                        out RouteCProtocolMessage initialHello,
                        out _));
                    Assert.IsTrue(RouteCProtocolCodec.TryDecodeInitialHello(
                        initialHello.Payload,
                        out byte[] initialNonce));

                    var session = new RouteCSessionId(11UL, 12UL);
                    byte[] oldToken = Sequence(
                        0x60,
                        RouteCProtocolConstants.ReconnectTokenSize);
                    SendEnvelope(
                        server,
                        remote,
                        RouteCMessageType.Welcome,
                        session,
                        1u,
                        RouteCProtocolCodec.EncodeWelcome(
                            initialNonce,
                            0,
                            77u,
                            oldToken,
                            RouteCProtocolConstants.HeartbeatSilenceMs,
                            RouteCProtocolConstants.DisconnectTimeoutMs,
                            false));
                    Assert.IsTrue(server.Poll(1000000, SelectMode.SelectRead));
                    count = server.ReceiveFrom(buffer, ref remote);
                    Assert.IsTrue(RouteCProtocolCodec.TryDecode(
                        buffer,
                        count,
                        out RouteCProtocolMessage initialReady,
                        out _));
                    Assert.AreEqual(
                        RouteCMessageType.Ready,
                        initialReady.MessageType);
                    SendEnvelope(
                        server,
                        remote,
                        RouteCMessageType.Start,
                        session,
                        1u,
                        RouteCProtocolCodec.EncodeStart(0));
                    Assert.IsTrue(SpinWait.SpinUntil(
                        () => transport.IsRunning,
                        1000));

                    clock.Advance(
                        (uint)RouteCProtocolConstants.DisconnectTimeoutMs);
                    Assert.IsTrue(SpinWait.SpinUntil(
                        () => transport.State == NetworkSessionState.Reconnecting,
                        1000));
                    Assert.IsTrue(server.Poll(1000000, SelectMode.SelectRead));
                    count = server.ReceiveFrom(buffer, ref remote);
                    Assert.IsTrue(RouteCProtocolCodec.TryDecode(
                        buffer,
                        count,
                        out RouteCProtocolMessage reconnectHello,
                        out _));
                    Assert.AreEqual(
                        RouteCMessageType.Hello,
                        reconnectHello.MessageType);
                    Assert.IsTrue(RouteCProtocolCodec.TryDecodeReconnectHello(
                        reconnectHello.Payload,
                        out byte[] reconnectNonce,
                        out _));

                    byte[] newToken = Sequence(
                        0x80,
                        RouteCProtocolConstants.ReconnectTokenSize);
                    SendEnvelope(
                        server,
                        remote,
                        RouteCMessageType.Welcome,
                        session,
                        2u,
                        RouteCProtocolCodec.EncodeWelcome(
                            reconnectNonce,
                            0,
                            99u,
                            newToken,
                            RouteCProtocolConstants.HeartbeatSilenceMs,
                            RouteCProtocolConstants.DisconnectTimeoutMs,
                            true));
                    Assert.IsTrue(SpinWait.SpinUntil(
                        () => transport.State == NetworkSessionState.Resuming,
                        1000));
                    Assert.IsFalse(
                        server.Poll(150000, SelectMode.SelectRead),
                        "ResumeRequired must not send default readiness.");

                    var readiness = new ResumeReadiness(8, 3, 10);
                    transport.SubmitResumeReadiness(in readiness);
                    Assert.IsTrue(server.Poll(1000000, SelectMode.SelectRead));
                    count = server.ReceiveFrom(buffer, ref remote);
                    Assert.IsTrue(RouteCProtocolCodec.TryDecode(
                        buffer,
                        count,
                        out RouteCProtocolMessage ready,
                        out _));
                    Assert.AreEqual(RouteCMessageType.Ready, ready.MessageType);
                    Assert.IsTrue(RouteCProtocolCodec.TryDecodeReady(
                        ready.Payload,
                        out int remoteFrame,
                        out int recoveryFloor,
                        out int localFrame));
                    Assert.AreEqual(8, remoteFrame);
                    Assert.AreEqual(3, recoveryFloor);
                    Assert.AreEqual(10, localFrame);

                    clock.Advance(249u);
                    Assert.IsFalse(server.Poll(150000, SelectMode.SelectRead));
                    clock.Advance(1u);
                    Assert.IsTrue(server.Poll(1000000, SelectMode.SelectRead));
                    count = server.ReceiveFrom(buffer, ref remote);
                    Assert.IsTrue(RouteCProtocolCodec.TryDecode(
                        buffer,
                        count,
                        out RouteCProtocolMessage retryReady,
                        out _));
                    Assert.AreEqual(
                        RouteCMessageType.Ready,
                        retryReady.MessageType);

                    transport.RequestStop();
                    Assert.IsTrue(transport.WaitForStop(
                        NetworkConfig.InitialKcpIntervalMs +
                        NetworkConfig.WorkerStopMarginMs));
                }
            }
        }

        [Test]
        public void ResumeRound_ReadinessCommandSendsReadyBeforeBusinessKcpDataInSameWorkerRound()
        {
            using (var server = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp))
            {
                server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)server.LocalEndPoint).Port;
                var clock = new ManualClock();
                using (var transport = new KcpUdpClientTransport(
                    clock,
                    () => Sequence(0x20, RouteCProtocolConstants.NonceSize)))
                {
                    transport.Start("127.0.0.1", port);
                    var buffer = new byte[
                        RouteCProtocolConstants.MaximumDatagramSize];
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int count = server.ReceiveFrom(buffer, ref remote);
                    Assert.IsTrue(RouteCProtocolCodec.TryDecode(
                        buffer,
                        count,
                        out RouteCProtocolMessage initialHello,
                        out _));
                    Assert.IsTrue(RouteCProtocolCodec.TryDecodeInitialHello(
                        initialHello.Payload,
                        out byte[] initialNonce));

                    var session = new RouteCSessionId(31UL, 32UL);
                    SendEnvelope(
                        server,
                        remote,
                        RouteCMessageType.Welcome,
                        session,
                        1u,
                        RouteCProtocolCodec.EncodeWelcome(
                            initialNonce,
                            0,
                            77u,
                            Sequence(
                                0x60,
                                RouteCProtocolConstants.ReconnectTokenSize),
                            RouteCProtocolConstants.HeartbeatSilenceMs,
                            RouteCProtocolConstants.DisconnectTimeoutMs,
                            false));
                    Assert.IsTrue(ReceiveEnvelopeTypeWithin(
                        server,
                        RouteCMessageType.Ready,
                        1000));
                    SendEnvelope(
                        server,
                        remote,
                        RouteCMessageType.Start,
                        session,
                        1u,
                        RouteCProtocolCodec.EncodeStart(0));
                    Assert.IsTrue(SpinWait.SpinUntil(
                        () => transport.IsRunning,
                        1000));

                    clock.Advance(
                        (uint)RouteCProtocolConstants.DisconnectTimeoutMs);
                    Assert.IsTrue(SpinWait.SpinUntil(
                        () => transport.State == NetworkSessionState.Reconnecting,
                        1000));
                    Assert.IsTrue(server.Poll(1000000, SelectMode.SelectRead));
                    count = server.ReceiveFrom(buffer, ref remote);
                    Assert.IsTrue(RouteCProtocolCodec.TryDecode(
                        buffer,
                        count,
                        out RouteCProtocolMessage reconnectHello,
                        out _));
                    Assert.AreEqual(
                        RouteCMessageType.Hello,
                        reconnectHello.MessageType);
                    Assert.IsTrue(RouteCProtocolCodec.TryDecodeReconnectHello(
                        reconnectHello.Payload,
                        out byte[] reconnectNonce,
                        out _));

                    SendEnvelope(
                        server,
                        remote,
                        RouteCMessageType.Welcome,
                        session,
                        2u,
                        RouteCProtocolCodec.EncodeWelcome(
                            reconnectNonce,
                            0,
                            99u,
                            Sequence(
                                0x80,
                                RouteCProtocolConstants.ReconnectTokenSize),
                            RouteCProtocolConstants.HeartbeatSilenceMs,
                            RouteCProtocolConstants.DisconnectTimeoutMs,
                            true));
                    Assert.IsTrue(SpinWait.SpinUntil(
                        () => transport.State == NetworkSessionState.Resuming,
                        1000));
                    Assert.IsFalse(
                        server.Poll(150000, SelectMode.SelectRead),
                        "ResumeRequired must wait for real readiness.");

                    PropertyInfo diagnosticsProperty =
                        RequireWorkerDiagnosticsProperty();
                    var readiness = new ResumeReadiness(8, 3, 10);
                    transport.SubmitResumeReadiness(in readiness);
                    Assert.IsTrue(transport.TryEnqueueLocalInput(
                        0x12345678u,
                        27));
                    clock.Advance(
                        (uint)RouteCProtocolConstants.InitialKcpIntervalMs);

                    var observed = new List<RouteCMessageType>(2);
                    var stopwatch = Stopwatch.StartNew();
                    while (observed.Count < 2 &&
                           stopwatch.ElapsedMilliseconds < 1500)
                    {
                        if (!server.Poll(10000, SelectMode.SelectRead))
                            continue;

                        count = server.ReceiveFrom(buffer, ref remote);
                        if (!RouteCProtocolCodec.TryDecode(
                                buffer,
                                count,
                                out RouteCProtocolMessage message,
                                out _))
                        {
                            continue;
                        }

                        if (message.MessageType == RouteCMessageType.Ready ||
                            message.MessageType == RouteCMessageType.KcpData)
                        {
                            observed.Add(message.MessageType);
                        }
                    }

                    CollectionAssert.AreEqual(
                        new[]
                        {
                            RouteCMessageType.Ready,
                            RouteCMessageType.KcpData
                        },
                        observed,
                        "The real UDP output must put Ready before business KcpData.");
                    Assert.IsTrue(SpinWait.SpinUntil(
                        () =>
                        {
                            object snapshot = diagnosticsProperty.GetValue(
                                transport,
                                null);
                            long businessRound = ReadInt64Property(
                                snapshot,
                                "LastBusinessInputRound");
                            return businessRound > 0 &&
                                   ReadInt64Property(
                                       snapshot,
                                       "LastReadySendRound") == businessRound &&
                                   ReadInt64Property(
                                       snapshot,
                                       "LastKcpDataSendRound") == businessRound;
                        },
                        1000),
                        "Ready, business acceptance, and KcpData output must share one worker round.");
                    object orderedSnapshot = diagnosticsProperty.GetValue(
                        transport,
                        null);
                    Assert.Less(
                        ReadInt32Property(
                            orderedSnapshot,
                            "LastReadySendOrder"),
                        ReadInt32Property(
                            orderedSnapshot,
                            "LastKcpDataSendOrder"));

                    transport.RequestStop();
                    Assert.IsTrue(transport.WaitForStop(
                        NetworkConfig.InitialKcpIntervalMs +
                        NetworkConfig.WorkerStopMarginMs));
                }
            }
        }

        [Test]
        public void ResumeRejected_TerminatesWorkerWithoutMainThreadClosingSocket()
        {
            using (var server = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp))
            using (var transport = new KcpUdpClientTransport(
                new StopwatchMonotonicClock(),
                () => Sequence(0x20, RouteCProtocolConstants.NonceSize)))
            {
                server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                server.ReceiveTimeout = 1000;
                int port = ((IPEndPoint)server.LocalEndPoint).Port;
                transport.Start("127.0.0.1", port);

                var buffer = new byte[
                    RouteCProtocolConstants.MaximumDatagramSize];
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int count = server.ReceiveFrom(buffer, ref remote);
                Assert.IsTrue(RouteCProtocolCodec.TryDecode(
                    buffer,
                    count,
                    out RouteCProtocolMessage hello,
                    out _));
                Assert.IsTrue(RouteCProtocolCodec.TryDecodeInitialHello(
                    hello.Payload,
                    out byte[] nonce));

                var session = new RouteCSessionId(5UL, 6UL);
                byte[] token = Sequence(
                    0x60,
                    RouteCProtocolConstants.ReconnectTokenSize);
                SendEnvelope(
                    server,
                    remote,
                    RouteCMessageType.Welcome,
                    session,
                    1u,
                    RouteCProtocolCodec.EncodeWelcome(
                        nonce,
                        0,
                        88u,
                        token,
                        RouteCProtocolConstants.HeartbeatSilenceMs,
                        RouteCProtocolConstants.DisconnectTimeoutMs,
                        false));
                SendEnvelope(
                    server,
                    remote,
                    RouteCMessageType.Start,
                    session,
                    1u,
                    RouteCProtocolCodec.EncodeStart(0));
                Assert.IsTrue(SpinWait.SpinUntil(
                    () => transport.IsRunning,
                    1000));

                SendEnvelope(
                    server,
                    remote,
                    RouteCMessageType.ResumeRejected,
                    session,
                    1u,
                    RouteCProtocolCodec.EncodeResumeRejected(
                        Sequence(0x70, RouteCProtocolConstants.NonceSize),
                        (ushort)ResumeRejectedReason.UnsafeResume));

                Assert.IsTrue(SpinWait.SpinUntil(
                    () => transport.State == NetworkSessionState.Terminated,
                    1000));
                Assert.IsTrue(transport.WaitForStop(
                    NetworkConfig.InitialKcpIntervalMs +
                    NetworkConfig.WorkerStopMarginMs));
            }
        }

        [Test]
        public void InvalidGenerationAndConversation_AreRejectedBeforeKcpInput()
        {
            using (var server = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp))
            using (var transport = new KcpUdpClientTransport(
                new StopwatchMonotonicClock(),
                () => Sequence(0x20, RouteCProtocolConstants.NonceSize)))
            {
                server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)server.LocalEndPoint).Port;
                transport.Start("127.0.0.1", port);

                var buffer = new byte[
                    RouteCProtocolConstants.MaximumDatagramSize];
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int count = server.ReceiveFrom(buffer, ref remote);
                Assert.IsTrue(RouteCProtocolCodec.TryDecode(
                    buffer,
                    count,
                    out RouteCProtocolMessage hello,
                    out _));
                Assert.IsTrue(RouteCProtocolCodec.TryDecodeInitialHello(
                    hello.Payload,
                    out byte[] nonce));

                var session = new RouteCSessionId(21UL, 22UL);
                const uint generation = 1u;
                const uint conversation = 77u;
                SendEnvelope(
                    server,
                    remote,
                    RouteCMessageType.Welcome,
                    session,
                    generation,
                    RouteCProtocolCodec.EncodeWelcome(
                        nonce,
                        0,
                        conversation,
                        Sequence(
                            0x60,
                            RouteCProtocolConstants.ReconnectTokenSize),
                        RouteCProtocolConstants.HeartbeatSilenceMs,
                        RouteCProtocolConstants.DisconnectTimeoutMs,
                        false));
                SendEnvelope(
                    server,
                    remote,
                    RouteCMessageType.Start,
                    session,
                    generation,
                    RouteCProtocolCodec.EncodeStart(0));
                Assert.IsTrue(SpinWait.SpinUntil(
                    () => transport.IsRunning,
                    1000));

                byte[] validPayload = CreateKcpBusinessDatagram(
                    conversation,
                    0x12345678u,
                    42);
                SendEnvelope(
                    server,
                    remote,
                    RouteCMessageType.KcpData,
                    session,
                    generation + 1u,
                    validPayload);
                NetworkPacketArrival arrival = default;
                Assert.IsFalse(SpinWait.SpinUntil(
                    () => transport.TryDequeueRemoteInput(out arrival),
                    150));

                byte[] wrongConversationPayload = CreateKcpBusinessDatagram(
                    conversation + 1u,
                    0x12345678u,
                    42);
                SendEnvelope(
                    server,
                    remote,
                    RouteCMessageType.KcpData,
                    session,
                    generation,
                    wrongConversationPayload);
                Assert.IsFalse(SpinWait.SpinUntil(
                    () => transport.TryDequeueRemoteInput(out arrival),
                    150));

                SendEnvelope(
                    server,
                    remote,
                    RouteCMessageType.KcpData,
                    session,
                    generation,
                    validPayload);
                Assert.IsTrue(SpinWait.SpinUntil(
                    () => transport.TryDequeueRemoteInput(out arrival),
                    1000));
                Assert.AreEqual(0x12345678u, arrival.Raw);
                Assert.AreEqual(42, arrival.RemoteFrameID);

                transport.RequestStop();
                Assert.IsTrue(transport.WaitForStop(
                    NetworkConfig.InitialKcpIntervalMs +
                    NetworkConfig.WorkerStopMarginMs));
            }
        }

        [Test]
        public void BusinessDequeuedByWorker_PostponesHeartbeatFromActualSendTime()
        {
            using (var server = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp))
            {
                server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)server.LocalEndPoint).Port;
                var clock = new ManualClock();
                using (var transport = new KcpUdpClientTransport(
                    clock,
                    () => Sequence(0x20, RouteCProtocolConstants.NonceSize)))
                {
                    transport.Start("127.0.0.1", port);
                    var buffer = new byte[
                        RouteCProtocolConstants.MaximumDatagramSize];
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int count = server.ReceiveFrom(buffer, ref remote);
                    Assert.IsTrue(RouteCProtocolCodec.TryDecode(
                        buffer,
                        count,
                        out RouteCProtocolMessage hello,
                        out _));
                    Assert.IsTrue(RouteCProtocolCodec.TryDecodeInitialHello(
                        hello.Payload,
                        out byte[] nonce));

                    var session = new RouteCSessionId(31UL, 32UL);
                    const uint generation = 1u;
                    const uint conversation = 88u;
                    SendEnvelope(
                        server,
                        remote,
                        RouteCMessageType.Welcome,
                        session,
                        generation,
                        RouteCProtocolCodec.EncodeWelcome(
                            nonce,
                            0,
                            conversation,
                            Sequence(
                                0x60,
                                RouteCProtocolConstants.ReconnectTokenSize),
                            RouteCProtocolConstants.HeartbeatSilenceMs,
                            RouteCProtocolConstants.DisconnectTimeoutMs,
                            false));
                    Assert.IsTrue(server.Poll(1000000, SelectMode.SelectRead));
                    server.ReceiveFrom(buffer, ref remote);
                    SendEnvelope(
                        server,
                        remote,
                        RouteCMessageType.Start,
                        session,
                        generation,
                        RouteCProtocolCodec.EncodeStart(0));
                    Assert.IsTrue(SpinWait.SpinUntil(
                        () => transport.IsRunning,
                        1000));

                    clock.Advance(999u);
                    Assert.IsTrue(transport.TryEnqueueLocalInput(0x55u, 7));
                    Assert.IsTrue(SpinWait.SpinUntil(
                        () => transport.Diagnostics != null &&
                              transport.Diagnostics.WaitSndHighWater > 0,
                        1000));
                    clock.Advance(
                        (uint)RouteCProtocolConstants.InitialKcpIntervalMs);
                    Assert.IsTrue(ReceiveEnvelopeTypeWithin(
                        server,
                        RouteCMessageType.KcpData,
                        1000));

                    Assert.IsFalse(ReceiveEnvelopeTypeWithin(
                        server,
                        RouteCMessageType.Heartbeat,
                        150));
                    clock.Advance((uint)(
                        RouteCProtocolConstants.HeartbeatSilenceMs -
                        RouteCProtocolConstants.InitialKcpIntervalMs -
                        1));
                    Assert.IsFalse(ReceiveEnvelopeTypeWithin(
                        server,
                        RouteCMessageType.Heartbeat,
                        150));
                    clock.Advance(1u);
                    Assert.IsTrue(ReceiveEnvelopeTypeWithin(
                        server,
                        RouteCMessageType.Heartbeat,
                        1000));

                    transport.RequestStop();
                    Assert.IsTrue(transport.WaitForStop(
                        NetworkConfig.InitialKcpIntervalMs +
                        NetworkConfig.WorkerStopMarginMs));
                }
            }
        }

        [Test]
        public void DatagramFloodBeyondRoundBudget_StopTimeoutReportsOnceAndBoundedStopCompletes()
        {
            using (var server = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp))
            using (var transport = new KcpUdpClientTransport(
                new StopwatchMonotonicClock(),
                () => Sequence(0x20, RouteCProtocolConstants.NonceSize)))
            {
                server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)server.LocalEndPoint).Port;
                transport.Start("127.0.0.1", port);
                var buffer = new byte[
                    RouteCProtocolConstants.MaximumDatagramSize];
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                server.ReceiveFrom(buffer, ref remote);

                byte[] advisory = RouteCProtocolCodec.Encode(
                    RouteCMessageType.Heartbeat,
                    RouteCSessionId.Zero,
                    0u,
                    Array.Empty<byte>());
                int floodCount =
                    RouteCProtocolConstants.MaximumDatagramsPerRound * 2 + 1;
                for (int index = 0; index < floodCount; index++)
                    server.SendTo(advisory, remote);

                Assert.IsFalse(transport.WaitForStop(0));
                Assert.IsFalse(transport.WaitForStop(0));
                int timeoutEvents = 0;
                while (transport.TryDequeueEvent(
                    out NetworkTransportEvent transportEvent))
                {
                    if (transportEvent.Reason ==
                        NetworkTransportEventReason.WorkerStopTimedOut)
                    {
                        timeoutEvents++;
                    }
                }
                Assert.AreEqual(
                    1,
                    timeoutEvents,
                    "One stop attempt must publish at most one timeout fault.");

                transport.RequestStop();
                Assert.IsTrue(transport.WaitForStop(
                    NetworkConfig.InitialKcpIntervalMs +
                    NetworkConfig.WorkerStopMarginMs));
            }
        }

        [Test]
        public void WorkerRoundDiagnostics_DatagramFloodIsSplitByMaximumBudget()
        {
            using (var server = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp))
            using (var transport = new KcpUdpClientTransport(
                new StopwatchMonotonicClock(),
                () => Sequence(0x20, RouteCProtocolConstants.NonceSize)))
            {
                server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)server.LocalEndPoint).Port;
                transport.Start("127.0.0.1", port);
                var buffer = new byte[
                    RouteCProtocolConstants.MaximumDatagramSize];
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                server.ReceiveFrom(buffer, ref remote);

                PropertyInfo diagnosticsProperty =
                    RequireWorkerDiagnosticsProperty();
                byte[] advisory = RouteCProtocolCodec.Encode(
                    RouteCMessageType.Heartbeat,
                    RouteCSessionId.Zero,
                    0u,
                    Array.Empty<byte>());
                int floodCount =
                    RouteCProtocolConstants.MaximumDatagramsPerRound * 2 + 1;
                for (int index = 0; index < floodCount; index++)
                    server.SendTo(advisory, remote);

                Assert.IsTrue(SpinWait.SpinUntil(
                    () => ReadInt64Property(
                              diagnosticsProperty.GetValue(transport, null),
                              "TotalDatagramsDrained") >= floodCount,
                    1500),
                    "The real worker must drain the complete loopback flood.");
                object snapshot = diagnosticsProperty.GetValue(
                    transport,
                    null);
                Assert.LessOrEqual(
                    ReadInt32Property(
                        snapshot,
                        "MaximumDatagramsDrainedPerRound"),
                    RouteCProtocolConstants.MaximumDatagramsPerRound);
                Assert.GreaterOrEqual(
                    ReadInt64Property(snapshot, "NonEmptyDatagramRoundCount"),
                    3L,
                    "A 2 * budget + 1 flood cannot complete in fewer than three rounds.");

                transport.RequestStop();
                Assert.IsTrue(transport.WaitForStop(
                    NetworkConfig.InitialKcpIntervalMs +
                    NetworkConfig.WorkerStopMarginMs));
            }
        }

        private static bool PumpServerUntil(
            Socket server,
            RouteCKcpSession serverKcp,
            RouteCSessionId session,
            uint generation,
            IMonotonicClock clock,
            Func<bool> condition,
            int timeoutMs)
        {
            var stopwatch = Stopwatch.StartNew();
            var buffer = new byte[RouteCProtocolConstants.MaximumDatagramSize];
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                while (server.Poll(0, SelectMode.SelectRead))
                {
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int count = server.ReceiveFrom(buffer, ref remote);
                    if (RouteCProtocolCodec.TryDecode(
                            buffer,
                            count,
                            out RouteCProtocolMessage message,
                            out _) &&
                        message.MessageType == RouteCMessageType.KcpData &&
                        message.SessionId == session &&
                        message.Generation == generation)
                    {
                        byte[] payload = message.Payload;
                        serverKcp.InputDatagram(payload, 0, payload.Length);
                    }
                }

                uint nowMs = clock.Milliseconds;
                if (unchecked((int)(nowMs - serverKcp.NextUpdateAt(nowMs))) >= 0)
                    serverKcp.Update(nowMs, clock.Timestamp);
                if (condition())
                    return true;
                Thread.Yield();
            }

            return false;
        }

        private static PropertyInfo RequireWorkerDiagnosticsProperty()
        {
            PropertyInfo property = typeof(KcpUdpClientTransport).GetProperty(
                "WorkerDiagnostics",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(
                property,
                "A public immutable worker-round diagnostic snapshot is required.");
            return property;
        }

        private static bool TryDequeueEventReason(
            KcpUdpClientTransport transport,
            NetworkTransportEventReason reason,
            out NetworkTransportEvent matchingEvent)
        {
            while (transport.TryDequeueEvent(
                out NetworkTransportEvent transportEvent))
            {
                if (transportEvent.Reason == reason)
                {
                    matchingEvent = transportEvent;
                    return true;
                }
            }

            matchingEvent = default;
            return false;
        }

        private static void AssertRunningStatus(
            KcpUdpClientTransport transport)
        {
            Assert.AreEqual(NetworkSessionState.Running, transport.State);
            Assert.IsTrue(transport.HasStartedSession);
            Assert.That(transport.LocalPlayerIndex, Is.InRange(0, 1));
        }

        private static long ReadInt64Property(object target, string name)
        {
            Assert.IsNotNull(target, "Worker diagnostics must always be available.");
            PropertyInfo property = target.GetType().GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(property, name + " must be publicly observable.");
            return Convert.ToInt64(property.GetValue(target, null));
        }

        private static int ReadInt32Property(object target, string name)
        {
            Assert.IsNotNull(target, "Worker diagnostics must always be available.");
            PropertyInfo property = target.GetType().GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(property, name + " must be publicly observable.");
            return Convert.ToInt32(property.GetValue(target, null));
        }

        private static byte[] CreateKcpBusinessDatagram(
            uint conversation,
            uint raw,
            int frameID)
        {
            byte[] datagram = null;
            var kcp = new RouteCKcpSession(
                conversation,
                new RouteCKcpSettings(
                    RouteCProtocolConstants.InitialKcpIntervalMs),
                (bytes, count) => datagram = Copy(bytes, count));
            Assert.AreEqual(0, kcp.SendBusinessInput(raw, frameID));
            for (uint nowMs = 0u;
                 datagram == null && nowMs <= 1000u;
                 nowMs += (uint)RouteCProtocolConstants.InitialKcpIntervalMs)
            {
                kcp.Update(nowMs, nowMs);
            }
            Assert.IsNotNull(datagram);
            return datagram;
        }

        private static bool ReceiveEnvelopeTypeWithin(
            Socket socket,
            RouteCMessageType expectedType,
            int timeoutMs)
        {
            var stopwatch = Stopwatch.StartNew();
            var buffer = new byte[RouteCProtocolConstants.MaximumDatagramSize];
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (!socket.Poll(10000, SelectMode.SelectRead))
                    continue;

                int count = socket.Receive(buffer);
                if (RouteCProtocolCodec.TryDecode(
                        buffer,
                        count,
                        out RouteCProtocolMessage message,
                        out _) &&
                    message.MessageType == expectedType)
                {
                    return true;
                }
            }

            return false;
        }

        private static void SendEnvelope(
            Socket socket,
            EndPoint remote,
            RouteCMessageType type,
            RouteCSessionId session,
            uint generation,
            byte[] payload)
        {
            byte[] datagram = RouteCProtocolCodec.Encode(
                type,
                session,
                generation,
                payload);
            socket.SendTo(datagram, remote);
        }

        private static byte[] Copy(byte[] source, int count)
        {
            var copy = new byte[count];
            Buffer.BlockCopy(source, 0, copy, 0, count);
            return copy;
        }

        private static byte[] Sequence(byte first, int count)
        {
            var result = new byte[count];
            for (int index = 0; index < result.Length; index++)
                result[index] = unchecked((byte)(first + index));
            return result;
        }

        private sealed class ManualClock : IMonotonicClock
        {
            private int _milliseconds;

            public uint Milliseconds => unchecked((uint)Volatile.Read(
                ref _milliseconds));

            public long Timestamp => Milliseconds;

            public void Advance(uint milliseconds)
            {
                Interlocked.Add(
                    ref _milliseconds,
                    unchecked((int)milliseconds));
            }
        }

        private sealed class WorkerGateClock : IMonotonicClock
        {
            private readonly ManualResetEventSlim _readObserved =
                new ManualResetEventSlim(false);
            private readonly ManualResetEventSlim _blocked =
                new ManualResetEventSlim(false);
            private readonly ManualResetEventSlim _release =
                new ManualResetEventSlim(false);
            private int _observeNextRead;
            private int _readsUntilBlock = int.MaxValue;

            public uint Milliseconds
            {
                get
                {
                    if (Interlocked.Exchange(ref _observeNextRead, 0) == 1)
                        _readObserved.Set();

                    int remaining = Interlocked.Decrement(
                        ref _readsUntilBlock);
                    if (remaining == 0)
                    {
                        _blocked.Set();
                        _release.Wait(2000);
                        _release.Reset();
                    }

                    return 0u;
                }
            }

            public long Timestamp => Milliseconds;

            public void ObserveNextRead()
            {
                _readObserved.Reset();
                Volatile.Write(ref _observeNextRead, 1);
            }

            public bool WaitForObservedRead(int millisecondsTimeout)
            {
                return _readObserved.Wait(millisecondsTimeout);
            }

            public void BlockAfterReads(int readCount)
            {
                _blocked.Reset();
                _release.Reset();
                Volatile.Write(ref _readsUntilBlock, readCount);
            }

            public bool WaitUntilBlocked(int millisecondsTimeout)
            {
                return _blocked.Wait(millisecondsTimeout);
            }

            public void Release()
            {
                _release.Set();
                Volatile.Write(ref _readsUntilBlock, int.MaxValue);
            }

            public void ReleaseAndBlockAfterReads(int readCount)
            {
                _blocked.Reset();
                Volatile.Write(ref _readsUntilBlock, readCount);
                _release.Set();
                Assert.IsTrue(SpinWait.SpinUntil(
                    () => !_release.IsSet,
                    1000));
            }
        }
    }
}

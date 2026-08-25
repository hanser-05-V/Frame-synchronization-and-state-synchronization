using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public sealed class KcpUdpClientStateMachineTests
    {
        [Test]
        public void BusinessTraffic_DoesNotPostponeReconnectHelloRetry()
        {
            var clock = new FakeClock();
            byte[] initialNonce = Sequence(0x10, RouteCProtocolConstants.NonceSize);
            byte[] reconnectNonce = Sequence(0x30, RouteCProtocolConstants.NonceSize);
            byte[] token = Sequence(0x50, RouteCProtocolConstants.ReconnectTokenSize);
            var nonces = new Queue<byte[]>(new[] { initialNonce, reconnectNonce });
            var session = new RouteCSessionId(1UL, 2UL);
            var sent = new List<RouteCProtocolMessage>();
            var machine = new KcpUdpClientStateMachine(
                clock,
                () => nonces.Dequeue(),
                sent.Add,
                _ => { });
            machine.Start();
            machine.HandleIncoming(CreateWelcome(
                session, 1u, initialNonce, 7u, token, false));
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.Start,
                session,
                1u,
                RouteCProtocolCodec.EncodeStart(0)));
            clock.Advance((uint)RouteCProtocolConstants.DisconnectTimeoutMs);
            machine.Tick();
            int afterFirstReconnectHello = sent.Count;

            clock.Advance(249u);
            machine.NotifyBusinessSent();
            clock.Advance(1u);
            machine.Tick();

            Assert.AreEqual(afterFirstReconnectHello + 1, sent.Count);
            Assert.AreEqual(RouteCMessageType.Hello, sent[sent.Count - 1].MessageType);
        }

        [Test]
        public void UnexpectedWellFormedControl_IsIgnoredAndCounted()
        {
            var clock = new FakeClock();
            byte[] nonce = Sequence(0x10, RouteCProtocolConstants.NonceSize);
            var events = new List<NetworkTransportEvent>();
            var machine = new KcpUdpClientStateMachine(
                clock,
                () => nonce,
                _ => { },
                events.Add);
            machine.Start();

            Assert.DoesNotThrow(() => machine.HandleIncoming(
                new RouteCProtocolMessage(
                    RouteCMessageType.Heartbeat,
                    RouteCSessionId.Zero,
                    0u,
                    Array.Empty<byte>())));

            Assert.AreEqual(NetworkSessionState.Handshaking, machine.State);
            Assert.AreEqual(1, machine.UnexpectedControlCount);
            Assert.AreEqual(1, events.Count);
        }

        [Test]
        public void Heartbeat_InResuming_RefreshesGraceWithoutCompletingResume()
        {
            var clock = new FakeClock();
            byte[] initialNonce = Sequence(0x10, RouteCProtocolConstants.NonceSize);
            byte[] reconnectNonce = Sequence(0x30, RouteCProtocolConstants.NonceSize);
            byte[] oldToken = Sequence(0x50, RouteCProtocolConstants.ReconnectTokenSize);
            byte[] newToken = Sequence(0x80, RouteCProtocolConstants.ReconnectTokenSize);
            var nonces = new Queue<byte[]>(new[] { initialNonce, reconnectNonce });
            var session = new RouteCSessionId(1UL, 2UL);
            var machine = new KcpUdpClientStateMachine(
                clock,
                () => nonces.Dequeue(),
                _ => { },
                _ => { });
            machine.Start();
            machine.HandleIncoming(CreateWelcome(
                session, 1u, initialNonce, 7u, oldToken, false));
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.Start,
                session,
                1u,
                RouteCProtocolCodec.EncodeStart(0)));
            clock.Advance((uint)RouteCProtocolConstants.DisconnectTimeoutMs);
            machine.Tick();
            machine.HandleIncoming(CreateWelcome(
                session, 2u, reconnectNonce, 9u, newToken, true));

            clock.Advance(4000u);
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.Heartbeat,
                session,
                2u,
                Array.Empty<byte>()));
            Assert.AreEqual(NetworkSessionState.Resuming, machine.State);
            clock.Advance(4999u);
            machine.Tick();
            Assert.AreEqual(NetworkSessionState.Resuming, machine.State);
            clock.Advance(1u);
            machine.Tick();
            Assert.AreEqual(NetworkSessionState.Terminated, machine.State);
        }

        [Test]
        public void Resuming_WithoutSuccessfulResume_TerminatesAtFiveSeconds()
        {
            var clock = new FakeClock();
            byte[] initialNonce = Sequence(0x10, RouteCProtocolConstants.NonceSize);
            byte[] reconnectNonce = Sequence(0x30, RouteCProtocolConstants.NonceSize);
            byte[] oldToken = Sequence(0x50, RouteCProtocolConstants.ReconnectTokenSize);
            byte[] newToken = Sequence(0x80, RouteCProtocolConstants.ReconnectTokenSize);
            var nonces = new Queue<byte[]>(new[] { initialNonce, reconnectNonce });
            var session = new RouteCSessionId(1UL, 2UL);
            var machine = new KcpUdpClientStateMachine(
                clock,
                () => nonces.Dequeue(),
                _ => { },
                _ => { });
            machine.Start();
            machine.HandleIncoming(CreateWelcome(
                session, 1u, initialNonce, 7u, oldToken, false));
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.Start,
                session,
                1u,
                RouteCProtocolCodec.EncodeStart(0)));
            clock.Advance((uint)RouteCProtocolConstants.DisconnectTimeoutMs);
            machine.Tick();
            machine.HandleIncoming(CreateWelcome(
                session, 2u, reconnectNonce, 9u, newToken, true));

            clock.Advance(4999u);
            machine.Tick();
            Assert.AreEqual(NetworkSessionState.Resuming, machine.State);
            clock.Advance(1u);
            machine.Tick();

            Assert.AreEqual(NetworkSessionState.Terminated, machine.State);
            Assert.IsFalse(machine.HasStartedSession);
        }

        [Test]
        public void Transport_StartAndStop_PublishImmutableBoundaryEvents()
        {
            byte[] nonce = Sequence(0x10, RouteCProtocolConstants.NonceSize);
            using (var server = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp))
            using (var transport = new KcpUdpClientTransport(
                new StopwatchMonotonicClock(),
                () => nonce))
            {
                server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)server.LocalEndPoint).Port;

                transport.Start("127.0.0.1", port);

                Assert.IsTrue(SpinWait.SpinUntil(
                    () => transport.State == NetworkSessionState.Handshaking,
                    1000));
                Assert.AreEqual(NetworkSessionState.Handshaking, transport.State);
                Assert.IsFalse(transport.IsRunning);
                Assert.IsFalse(transport.TryEnqueueLocalInput(0x10u, 0));
                Assert.IsTrue(transport.TryDequeueEvent(out NetworkTransportEvent started));
                Assert.AreEqual(NetworkTransportEventReason.StartRequested, started.Reason);

                transport.RequestStop();

                Assert.IsTrue(transport.WaitForStop(
                    NetworkConfig.InitialKcpIntervalMs +
                    NetworkConfig.WorkerStopMarginMs));
                Assert.AreEqual(NetworkSessionState.Terminated, transport.State);
                Assert.IsTrue(transport.TryDequeueEvent(out NetworkTransportEvent stopped));
                Assert.AreEqual(NetworkTransportEventReason.StopRequested, stopped.Reason);
                Assert.IsFalse(transport.TryDequeueRemoteInput(out _));
            }
        }

        [Test]
        public void Disconnect_IsAdvisoryAndFiveSecondGraceTerminatesSession()
        {
            var clock = new FakeClock();
            byte[] nonce = Sequence(0x10, RouteCProtocolConstants.NonceSize);
            byte[] reconnectNonce = Sequence(0x30, RouteCProtocolConstants.NonceSize);
            byte[] token = Sequence(0x50, RouteCProtocolConstants.ReconnectTokenSize);
            var nonces = new Queue<byte[]>(new[] { nonce, reconnectNonce });
            var session = new RouteCSessionId(1UL, 2UL);
            var events = new List<NetworkTransportEvent>();
            var machine = new KcpUdpClientStateMachine(
                clock,
                () => nonces.Dequeue(),
                _ => { },
                events.Add);
            machine.Start();
            machine.HandleIncoming(CreateWelcome(
                session,
                1u,
                nonce,
                7u,
                token,
                false));
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.Start,
                session,
                1u,
                RouteCProtocolCodec.EncodeStart(0)));

            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.Disconnect,
                session,
                1u,
                new byte[] { 0, 3 }));

            Assert.AreEqual(NetworkSessionState.Running, machine.State);
            Assert.AreEqual(NetworkTransportEventReason.DisconnectAdvisory, events[3].Reason);
            clock.Advance((uint)RouteCProtocolConstants.DisconnectTimeoutMs);
            machine.Tick();
            Assert.AreEqual(NetworkSessionState.Reconnecting, machine.State);
            clock.Advance(1999u);
            machine.Tick();
            Assert.AreEqual(NetworkSessionState.Reconnecting, machine.State);
            clock.Advance(1u);
            machine.Tick();
            Assert.AreEqual(NetworkSessionState.Terminated, machine.State);
            Assert.IsFalse(machine.HasStartedSession);
            Assert.AreEqual(NetworkTransportEventReason.ResumeGraceExpired, events[5].Reason);
        }

        [Test]
        public void Heartbeat_BusinessAndValidControlTrafficUseLockedSilenceAndTimeouts()
        {
            var clock = new FakeClock();
            byte[] nonce = Sequence(0x10, RouteCProtocolConstants.NonceSize);
            byte[] reconnectNonce = Sequence(0x30, RouteCProtocolConstants.NonceSize);
            byte[] token = Sequence(0x50, RouteCProtocolConstants.ReconnectTokenSize);
            var nonces = new Queue<byte[]>(new[] { nonce, reconnectNonce });
            var session = new RouteCSessionId(1UL, 2UL);
            var sent = new List<RouteCProtocolMessage>();
            var machine = new KcpUdpClientStateMachine(
                clock,
                () => nonces.Dequeue(),
                sent.Add,
                _ => { });
            machine.Start();
            machine.HandleIncoming(CreateWelcome(
                session,
                1u,
                nonce,
                7u,
                token,
                false));
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.Start,
                session,
                1u,
                RouteCProtocolCodec.EncodeStart(0)));
            int handshakeSendCount = sent.Count;

            clock.Advance(999u);
            machine.Tick();
            Assert.AreEqual(handshakeSendCount, sent.Count);
            machine.NotifyBusinessSent();
            clock.Advance(999u);
            machine.Tick();
            Assert.AreEqual(handshakeSendCount, sent.Count);
            clock.Advance(1u);
            machine.Tick();
            Assert.AreEqual(RouteCMessageType.Heartbeat, sent[sent.Count - 1].MessageType);

            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.Heartbeat,
                session,
                1u,
                Array.Empty<byte>()));
            clock.Advance(2999u);
            machine.Tick();
            Assert.AreEqual(NetworkSessionState.Running, machine.State);
            clock.Advance(1u);
            machine.Tick();
            Assert.AreEqual(NetworkSessionState.Reconnecting, machine.State);
        }

        [Test]
        public void ReconnectWelcome_WaitsForRealReadinessAndRejectsOldGenerationAndConversation()
        {
            var clock = new FakeClock();
            byte[] initialNonce = Sequence(0x10, RouteCProtocolConstants.NonceSize);
            byte[] reconnectNonce = Sequence(0x30, RouteCProtocolConstants.NonceSize);
            byte[] oldToken = Sequence(0x50, RouteCProtocolConstants.ReconnectTokenSize);
            byte[] newToken = Sequence(0x80, RouteCProtocolConstants.ReconnectTokenSize);
            var nonces = new Queue<byte[]>(new[] { initialNonce, reconnectNonce });
            var session = new RouteCSessionId(1UL, 2UL);
            var sent = new List<RouteCProtocolMessage>();
            var events = new List<NetworkTransportEvent>();
            var machine = new KcpUdpClientStateMachine(
                clock,
                () => nonces.Dequeue(),
                sent.Add,
                events.Add);
            machine.Start();
            machine.HandleIncoming(CreateWelcome(
                session,
                1u,
                initialNonce,
                7u,
                oldToken,
                false));
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.Start,
                session,
                1u,
                RouteCProtocolCodec.EncodeStart(0)));
            clock.Advance((uint)RouteCProtocolConstants.DisconnectTimeoutMs);
            machine.Tick();
            var reconnectWelcome = CreateWelcome(
                session,
                2u,
                reconnectNonce,
                9u,
                newToken,
                true);

            int sendCountBeforeResume = sent.Count;
            machine.HandleIncoming(reconnectWelcome);

            Assert.AreEqual(NetworkSessionState.Resuming, machine.State);
            Assert.IsTrue(machine.HasStartedSession);
            Assert.AreEqual(NetworkTransportEventReason.ResumeRequired, events[4].Reason);
            Assert.AreEqual(sendCountBeforeResume, sent.Count);

            clock.Advance(1000u);
            machine.Tick();
            Assert.AreEqual(
                sendCountBeforeResume,
                sent.Count,
                "No default Ready may be sent before real readiness is applied.");

            var readiness = new ResumeReadiness(8, 3, 10);
            machine.SubmitResumeReadiness(in readiness);
            AssertReady(
                sent[sent.Count - 1],
                session,
                2u,
                readiness);
            Assert.AreEqual(sendCountBeforeResume + 1, sent.Count);

            clock.Advance(249u);
            machine.Tick();
            Assert.AreEqual(sendCountBeforeResume + 1, sent.Count);
            clock.Advance(1u);
            machine.Tick();
            Assert.AreEqual(sendCountBeforeResume + 2, sent.Count);
            AssertReady(sent[sent.Count - 1], session, 2u, readiness);

            int eventCount = events.Count;
            machine.HandleIncoming(reconnectWelcome);
            Assert.AreEqual(eventCount, events.Count);
            AssertReady(
                sent[sent.Count - 1],
                session,
                2u,
                readiness);
            clock.Advance(4999u);
            machine.Tick();
            Assert.AreEqual(NetworkSessionState.Resuming, machine.State);

            Assert.IsFalse(machine.AcceptKcpData(CreateKcpData(session, 1u, 7u)));
            Assert.IsFalse(machine.AcceptKcpData(CreateKcpData(session, 2u, 7u)));
            Assert.IsTrue(machine.AcceptKcpData(CreateKcpData(session, 2u, 9u)));
            StringAssert.DoesNotContain(
                BitConverter.ToString(newToken),
                string.Join(" ", events.Select(item => item.ToString())));
            StringAssert.DoesNotContain(
                BitConverter.ToString(newToken),
                machine.DiagnosticSummary);
        }

        [Test]
        public void RunningTimeout_EntersReconnectingAndCreatesOneNewNonce()
        {
            var clock = new FakeClock();
            byte[] initialNonce = Sequence(0x10, RouteCProtocolConstants.NonceSize);
            byte[] reconnectNonce = Sequence(0x30, RouteCProtocolConstants.NonceSize);
            byte[] token = Sequence(0x50, RouteCProtocolConstants.ReconnectTokenSize);
            var nonces = new Queue<byte[]>(new[] { initialNonce, reconnectNonce });
            int nonceRequests = 0;
            var session = new RouteCSessionId(1UL, 2UL);
            var sent = new List<RouteCProtocolMessage>();
            var events = new List<NetworkTransportEvent>();
            var machine = new KcpUdpClientStateMachine(
                clock,
                () =>
                {
                    nonceRequests++;
                    return nonces.Dequeue();
                },
                sent.Add,
                events.Add);
            machine.Start();
            machine.HandleIncoming(CreateWelcome(
                session,
                1u,
                initialNonce,
                7u,
                token,
                false));
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.Start,
                session,
                1u,
                RouteCProtocolCodec.EncodeStart(0)));

            clock.Advance((uint)RouteCProtocolConstants.DisconnectTimeoutMs);
            machine.Tick();

            Assert.AreEqual(NetworkSessionState.Reconnecting, machine.State);
            Assert.IsTrue(machine.HasStartedSession);
            Assert.AreEqual(2, nonceRequests);
            Assert.AreEqual(
                NetworkTransportEventReason.ConnectionTimedOut,
                events[3].Reason);
            RouteCProtocolMessage reconnectHello = sent[sent.Count - 1];
            Assert.AreEqual(RouteCMessageType.Hello, reconnectHello.MessageType);
            Assert.AreEqual(session, reconnectHello.SessionId);
            Assert.AreEqual(1u, reconnectHello.Generation);
            Assert.IsTrue(RouteCProtocolCodec.TryDecodeReconnectHello(
                reconnectHello.Payload,
                out byte[] actualNonce,
                out byte[] actualToken));
            CollectionAssert.AreEqual(reconnectNonce, actualNonce);
            CollectionAssert.AreEqual(token, actualToken);

            clock.Advance((uint)RouteCProtocolConstants.HandshakeRetryMs);
            machine.Tick();
            Assert.AreEqual(2, nonceRequests);
            Assert.IsTrue(RouteCProtocolCodec.TryDecodeReconnectHello(
                sent[sent.Count - 1].Payload,
                out byte[] retryNonce,
                out _));
            CollectionAssert.AreEqual(reconnectNonce, retryNonce);
            StringAssert.DoesNotContain(
                BitConverter.ToString(token),
                string.Join(" ", events.Select(item => item.ToString())));
        }

        [Test]
        public void ResumeRejected_MatchingSessionTerminatesStartedSession()
        {
            var clock = new FakeClock();
            byte[] nonce = Sequence(0x10, RouteCProtocolConstants.NonceSize);
            byte[] token = Sequence(0x50, RouteCProtocolConstants.ReconnectTokenSize);
            byte[] attempt = Sequence(0x70, RouteCProtocolConstants.NonceSize);
            var session = new RouteCSessionId(1UL, 2UL);
            var events = new List<NetworkTransportEvent>();
            var machine = new KcpUdpClientStateMachine(
                clock,
                () => nonce,
                _ => { },
                events.Add);
            machine.Start();
            machine.HandleIncoming(CreateWelcome(
                session,
                1u,
                nonce,
                7u,
                token,
                false));
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.Start,
                session,
                1u,
                RouteCProtocolCodec.EncodeStart(0)));

            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.ResumeRejected,
                session,
                1u,
                RouteCProtocolCodec.EncodeResumeRejected(
                    attempt,
                    (ushort)ResumeRejectedReason.UnsafeResume)));

            Assert.AreEqual(NetworkSessionState.Terminated, machine.State);
            Assert.IsFalse(machine.HasStartedSession);
            Assert.AreEqual(
                NetworkTransportEventReason.ResumeRejected,
                events[events.Count - 1].Reason);
        }

        [Test]
        public void Start_MatchingSessionAndGeneration_TransitionsOnceToRunning()
        {
            var clock = new FakeClock();
            byte[] nonce = Sequence(0x10, RouteCProtocolConstants.NonceSize);
            byte[] token = Sequence(0x50, RouteCProtocolConstants.ReconnectTokenSize);
            var session = new RouteCSessionId(1UL, 2UL);
            var sent = new List<RouteCProtocolMessage>();
            var events = new List<NetworkTransportEvent>();
            var machine = new KcpUdpClientStateMachine(
                clock,
                () => nonce,
                sent.Add,
                events.Add);
            machine.Start();
            machine.HandleIncoming(CreateWelcome(
                session,
                1u,
                nonce,
                7u,
                token,
                false));

            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.Start,
                new RouteCSessionId(9UL, 9UL),
                1u,
                RouteCProtocolCodec.EncodeStart(0)));
            Assert.AreEqual(NetworkSessionState.AwaitingReady, machine.State);

            var start = new RouteCProtocolMessage(
                RouteCMessageType.Start,
                session,
                1u,
                RouteCProtocolCodec.EncodeStart(0));
            machine.HandleIncoming(start);

            Assert.AreEqual(NetworkSessionState.Running, machine.State);
            Assert.IsTrue(machine.IsRunning);
            Assert.IsTrue(machine.HasStartedSession);
            Assert.AreEqual(3, events.Count);
            Assert.AreEqual(NetworkTransportEventReason.SessionStarted, events[2].Reason);

            machine.HandleIncoming(start);
            Assert.AreEqual(NetworkSessionState.Running, machine.State);
            Assert.AreEqual(3, events.Count);
        }

        [Test]
        public void Welcome_MatchingAndDuplicate_TransitionsOnceAndRetriesReady()
        {
            var clock = new FakeClock();
            byte[] nonce = Sequence(0x10, RouteCProtocolConstants.NonceSize);
            byte[] token = Sequence(0x50, RouteCProtocolConstants.ReconnectTokenSize);
            var session = new RouteCSessionId(1UL, 2UL);
            var sent = new List<RouteCProtocolMessage>();
            var events = new List<NetworkTransportEvent>();
            var machine = new KcpUdpClientStateMachine(
                clock,
                () => nonce,
                sent.Add,
                events.Add);
            var readiness = new ResumeReadiness(8, 3, 10);
            machine.SubmitResumeReadiness(in readiness);
            machine.Start();
            var welcome = new RouteCProtocolMessage(
                RouteCMessageType.Welcome,
                session,
                1u,
                RouteCProtocolCodec.EncodeWelcome(
                    nonce,
                    1,
                    7u,
                    token,
                    RouteCProtocolConstants.HeartbeatSilenceMs,
                    RouteCProtocolConstants.DisconnectTimeoutMs,
                    false));

            machine.HandleIncoming(welcome);

            Assert.AreEqual(NetworkSessionState.AwaitingReady, machine.State);
            Assert.AreEqual(1, machine.LocalPlayerIndex);
            Assert.AreEqual(2, events.Count);
            Assert.AreEqual(NetworkTransportEventReason.WelcomeAccepted, events[1].Reason);
            Assert.AreNotEqual("none", events[1].SessionFingerprint);
            AssertReady(sent[1], session, 1u, readiness);

            machine.HandleIncoming(welcome);
            Assert.AreEqual(NetworkSessionState.AwaitingReady, machine.State);
            Assert.AreEqual(2, events.Count);
            AssertReady(sent[2], session, 1u, readiness);

            clock.Advance(249u);
            machine.Tick();
            Assert.AreEqual(3, sent.Count);
            clock.Advance(1u);
            machine.Tick();
            Assert.AreEqual(4, sent.Count);
            AssertReady(sent[3], session, 1u, readiness);
        }

        [Test]
        public void Welcome_NonceMismatch_IsDroppedWithoutStateChange()
        {
            var clock = new FakeClock();
            byte[] expectedNonce = Sequence(0x10, RouteCProtocolConstants.NonceSize);
            byte[] wrongNonce = Sequence(0x30, RouteCProtocolConstants.NonceSize);
            byte[] token = Sequence(0x50, RouteCProtocolConstants.ReconnectTokenSize);
            var sent = new List<RouteCProtocolMessage>();
            var events = new List<NetworkTransportEvent>();
            var machine = new KcpUdpClientStateMachine(
                clock,
                () => expectedNonce,
                sent.Add,
                events.Add);
            machine.Start();

            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.Welcome,
                new RouteCSessionId(1UL, 2UL),
                1u,
                RouteCProtocolCodec.EncodeWelcome(
                    wrongNonce,
                    0,
                    7u,
                    token,
                    RouteCProtocolConstants.HeartbeatSilenceMs,
                    RouteCProtocolConstants.DisconnectTimeoutMs,
                    false)));

            Assert.AreEqual(NetworkSessionState.Handshaking, machine.State);
            Assert.AreEqual(1, machine.RejectedEnvelopeCount);
            Assert.AreEqual(1, sent.Count);
            Assert.AreEqual(1, events.Count);
        }

        [Test]
        public void Start_InitialHelloRetriesAtTwoHundredFiftyMillisecondsWithSameNonce()
        {
            var clock = new FakeClock();
            byte[] nonce = Sequence(0x10, RouteCProtocolConstants.NonceSize);
            int nonceRequests = 0;
            var sent = new List<RouteCProtocolMessage>();
            var events = new List<NetworkTransportEvent>();
            var machine = new KcpUdpClientStateMachine(
                clock,
                () =>
                {
                    nonceRequests++;
                    return (byte[])nonce.Clone();
                },
                sent.Add,
                events.Add);

            machine.Start();

            Assert.AreEqual(NetworkSessionState.Handshaking, machine.State);
            Assert.AreEqual(1, nonceRequests);
            Assert.AreEqual(1, sent.Count);
            AssertInitialHello(sent[0], nonce);
            Assert.AreEqual(1, events.Count);
            Assert.AreEqual(
                NetworkTransportEventReason.StartRequested,
                events[0].Reason);

            clock.Advance(249u);
            machine.Tick();
            Assert.AreEqual(1, sent.Count);

            clock.Advance(1u);
            machine.Tick();
            Assert.AreEqual(2, sent.Count);
            AssertInitialHello(sent[1], nonce);
            Assert.AreEqual(1, nonceRequests);
            Assert.AreEqual(1, events.Count);
        }

        [Test]
        public void TransportBoundary_DoesNotExposeProtocolInternals()
        {
            Type boundary = typeof(IFrameTransportClient);

            CollectionAssert.AreEquivalent(
                new[]
                {
                    "get_HasStartedSession",
                    "get_IsRunning",
                    "get_LatestRemoteFrameID",
                    "get_LocalPlayerIndex",
                    "get_State",
                    "RequestStop",
                    "Start",
                    "SubmitResumeReadiness",
                    "TryDequeueEvent",
                    "TryDequeueRemoteInput",
                    "TryEnqueueLocalInput",
                    "WaitForStop"
                },
                boundary.GetMethods().Select(method => method.Name).ToArray());
            Assert.IsTrue(typeof(IDisposable).IsAssignableFrom(boundary));

            string surface = string.Join(
                " ",
                boundary.GetMethods()
                    .Select(method => method.ToString())
                    .ToArray());
            StringAssert.DoesNotContain("Kcp", surface);
            StringAssert.DoesNotContain("Conv", surface);
            StringAssert.DoesNotContain("Token", surface);
            StringAssert.DoesNotContain("Endpoint", surface);
            StringAssert.DoesNotContain("Socket", surface);
            StringAssert.DoesNotContain("Byte[]", surface);
        }

        private static void AssertInitialHello(
            RouteCProtocolMessage message,
            byte[] expectedNonce)
        {
            Assert.AreEqual(RouteCMessageType.Hello, message.MessageType);
            Assert.AreEqual(RouteCSessionId.Zero, message.SessionId);
            Assert.AreEqual(0u, message.Generation);
            Assert.IsTrue(RouteCProtocolCodec.TryDecodeInitialHello(
                message.Payload,
                out byte[] actualNonce));
            CollectionAssert.AreEqual(expectedNonce, actualNonce);
        }

        private static RouteCProtocolMessage CreateWelcome(
            RouteCSessionId session,
            uint generation,
            byte[] nonce,
            uint conversation,
            byte[] token,
            bool resumeRequired)
        {
            return new RouteCProtocolMessage(
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
                    resumeRequired));
        }

        private static RouteCProtocolMessage CreateKcpData(
            RouteCSessionId session,
            uint generation,
            uint conversation)
        {
            var payload = new byte[RouteCProtocolConstants.KcpHeaderSize];
            payload[0] = (byte)conversation;
            payload[1] = (byte)(conversation >> 8);
            payload[2] = (byte)(conversation >> 16);
            payload[3] = (byte)(conversation >> 24);
            return new RouteCProtocolMessage(
                RouteCMessageType.KcpData,
                session,
                generation,
                payload);
        }

        private static void AssertReady(
            RouteCProtocolMessage message,
            RouteCSessionId expectedSession,
            uint expectedGeneration,
            ResumeReadiness expectedReadiness)
        {
            Assert.AreEqual(RouteCMessageType.Ready, message.MessageType);
            Assert.AreEqual(expectedSession, message.SessionId);
            Assert.AreEqual(expectedGeneration, message.Generation);
            Assert.IsTrue(RouteCProtocolCodec.TryDecodeReady(
                message.Payload,
                out int remote,
                out int floor,
                out int local));
            Assert.AreEqual(expectedReadiness.LastContiguousRemoteFrameID, remote);
            Assert.AreEqual(expectedReadiness.EarliestRecoverableCanonicalFrame, floor);
            Assert.AreEqual(expectedReadiness.LatestLocalFrameID, local);
        }

        private static byte[] Sequence(byte first, int count)
        {
            return Enumerable.Range(first, count)
                .Select(value => (byte)value)
                .ToArray();
        }

        private sealed class FakeClock : IMonotonicClock
        {
            public uint Milliseconds { get; private set; }

            public long Timestamp => Milliseconds;

            public void Advance(uint milliseconds)
            {
                Milliseconds = unchecked(Milliseconds + milliseconds);
            }
        }
    }
}

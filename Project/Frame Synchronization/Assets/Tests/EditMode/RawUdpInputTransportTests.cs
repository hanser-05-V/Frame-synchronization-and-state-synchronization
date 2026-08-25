using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace FrameSyncDemo.Tests
{
    public class RawUdpInputTransportTests
    {
        private const BindingFlags InstanceFields =
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic;

        [Test]
        public void RawTransport_OwnsExactlyOneSocketAndOneRemoteArrivalQueue()
        {
            FieldInfo[] fields = typeof(RawUdpInputTransport).GetFields(
                InstanceFields);
            Assert.AreEqual(
                1,
                fields.Count(field => field.FieldType == typeof(Socket)));
            Assert.AreEqual(
                1,
                fields.Count(field => field.FieldType ==
                    typeof(ConcurrentQueue<NetworkPacketArrival>)));
            Assert.AreEqual(
                3,
                fields.Count(field =>
                    field.FieldType.IsGenericType &&
                    field.FieldType.GetGenericTypeDefinition() ==
                    typeof(ConcurrentQueue<>)),
                "Only local commands, remote arrivals, and transport events may cross threads.");
        }

        [Test]
        public void RawTransport_MainThreadRequestStopDoesNotCloseWorkerSocket()
        {
            string source = ReadTransportSource();
            string requestStop = SliceMethod(
                source,
                "public void RequestStop()",
                "public bool WaitForStop");

            StringAssert.DoesNotContain("_socket", requestStop);
            StringAssert.DoesNotContain(".Close", requestStop);
            StringAssert.DoesNotContain(".Dispose", requestStop);
            StringAssert.Contains("socket.Dispose()", source);
        }

        [Test]
        public void RawTransport_StopBeforeStartHandshakeRunningFaultAndRepeatedStopFinishWithin260ms()
        {
            using (var beforeStart = CreateTransport())
            {
                beforeStart.RequestStop();
                AssertStopsWithin(beforeStart);
                beforeStart.RequestStop();
                AssertStopsWithin(beforeStart);
            }

            using (Socket server = CreateServer())
            using (var handshaking = CreateTransport())
            {
                handshaking.Start("127.0.0.1", GetPort(server));
                Assert.IsTrue(SpinWait.SpinUntil(
                    () => handshaking.State == NetworkSessionState.Handshaking,
                    1000));
                handshaking.RequestStop();
                AssertStopsWithin(handshaking);
                handshaking.RequestStop();
                AssertStopsWithin(handshaking);
            }

            using (Socket server = CreateServer())
            using (var running = CreateTransport())
            {
                running.Start("127.0.0.1", GetPort(server));
                EndPoint client = StartRunning(server, running);
                Assert.AreEqual(NetworkSessionState.Running, running.State);
                running.RequestStop();
                AssertStopsWithin(running);
                running.RequestStop();
                AssertStopsWithin(running);
                Assert.IsNotNull(client);
            }

            using (Socket server = CreateServer())
            using (var faulted = CreateTransport())
            {
                faulted.Start("127.0.0.1", GetPort(server));
                EndPoint client = StartRunning(server, faulted);
                SendMessage(
                    server,
                    client,
                    new RouteCProtocolMessage(
                        RouteCMessageType.RawStart,
                        TestSession,
                        1u,
                        new byte[] { 0, 0, 0, 1 }));
                Assert.IsTrue(SpinWait.SpinUntil(
                    () => faulted.State == NetworkSessionState.Terminated,
                    1000));
                faulted.RequestStop();
                AssertStopsWithin(faulted);
                faulted.RequestStop();
                AssertStopsWithin(faulted);
            }
        }

        [Test]
        public void RawTransport_UsesSelectTenMillisecondMaximumWithoutSleepOrUnityTiming()
        {
            string source = ReadTransportSource();
            StringAssert.Contains("Socket.Select", source);
            StringAssert.Contains(
                "RouteCProtocolConstants.RawMaximumSelectWaitMs",
                source);
            StringAssert.DoesNotContain("Thread.Sleep", source);
            StringAssert.DoesNotContain("Time.deltaTime", source);
            StringAssert.DoesNotContain("Time.realtimeSinceStartup", source);
        }

        [Test]
        public void RawTransport_RoundReceivesAtMost64DatagramsAndReportsBudgetExhaustion()
        {
            using (Socket server = CreateServer())
            using (var transport = CreateTransport())
            {
                transport.Start("127.0.0.1", GetPort(server));
                EndPoint client = ReceiveUntil(server, RouteCMessageType.RawHello, out _);
                var pressure = Stopwatch.StartNew();
                while (transport.Diagnostics.ReceiveBudgetExhaustionCount < 1 &&
                    pressure.ElapsedMilliseconds < 2500)
                {
                    for (int index = 0; index < 512; index++)
                        server.SendTo(new byte[] { 0x52 }, client);
                    Thread.Yield();
                }

                Assert.IsTrue(SpinWait.SpinUntil(
                    () => transport.Diagnostics.ReceiveBudgetExhaustionCount >= 1,
                    3000));
                Assert.LessOrEqual(
                    transport.Diagnostics.MaximumDatagramsReceivedPerRound,
                    RouteCProtocolConstants.MaximumDatagramsPerRound);
                Assert.GreaterOrEqual(
                    transport.Diagnostics.ReceiveBudgetExhaustionCount,
                    1);
                transport.RequestStop();
                AssertStopsWithin(transport);
            }
        }

        [Test]
        public void RawTransport_StartReturnsImmediatelyAndSendsRawHello()
        {
            using (Socket server = CreateServer())
            using (var transport = CreateTransport())
            {
                var stopwatch = Stopwatch.StartNew();
                transport.Start("127.0.0.1", GetPort(server));
                stopwatch.Stop();
                Assert.Less(stopwatch.ElapsedMilliseconds, 100);

                ReceiveUntil(
                    server,
                    RouteCMessageType.RawHello,
                    out RouteCProtocolMessage hello);
                Assert.AreEqual(RouteCSessionId.Zero, hello.SessionId);
                Assert.AreEqual(0u, hello.Generation);
                Assert.IsTrue(RawUdpProtocolCodec.TryDecodeHello(
                    hello.Payload,
                    out byte[] nonce));
                CollectionAssert.AreEqual(CreateNonce(), nonce);
                transport.RequestStop();
                AssertStopsWithin(transport);
            }
        }

        [Test]
        public void RawTransport_FirstWelcomeReadyStartBeginsOnceAndPublishesRunningBeforeSessionStarted()
        {
            using (Socket server = CreateServer())
            using (var transport = CreateTransport())
            {
                transport.Start("127.0.0.1", GetPort(server));
                StartRunning(server, transport, 6);

                Assert.IsTrue(transport.IsRunning);
                Assert.IsTrue(transport.HasStartedSession);
                Assert.AreEqual(0, transport.LocalPlayerIndex);
                int sessionStartedCount = 0;
                while (transport.TryDequeueEvent(out NetworkTransportEvent item))
                {
                    if (item.Reason == NetworkTransportEventReason.SessionStarted)
                    {
                        Assert.AreEqual(NetworkSessionState.Running, transport.State);
                        sessionStartedCount++;
                    }
                }
                Assert.AreEqual(1, sessionStartedCount);
                transport.RequestStop();
                AssertStopsWithin(transport);
            }
        }

        [Test]
        public void RawTransport_LocalInputBeforeSessionStartIsRejectedWithoutQueueMutation()
        {
            using (Socket server = CreateServer())
            using (var transport = CreateTransport())
            {
                transport.Start("127.0.0.1", GetPort(server));
                EndPoint client = ReceiveUntil(
                    server,
                    RouteCMessageType.RawHello,
                    out RouteCProtocolMessage hello);
                Assert.IsFalse(transport.TryEnqueueLocalInput(1u, 0));
                Assert.AreEqual(0, transport.Diagnostics.LocalCommandQueueCount);

                Assert.IsTrue(RawUdpProtocolCodec.TryDecodeHello(
                    hello.Payload,
                    out byte[] nonce));
                SendMessage(server, client, new RouteCProtocolMessage(
                    RouteCMessageType.RawWelcome,
                    TestSession,
                    1u,
                    RawUdpProtocolCodec.EncodeWelcome(nonce, 0, 1)));
                ReceiveUntil(server, RouteCMessageType.RawReady, out _);
                Assert.IsFalse(transport.TryEnqueueLocalInput(2u, 0));
                Assert.AreEqual(0, transport.Diagnostics.LocalCommandQueueCount);
                transport.RequestStop();
                AssertStopsWithin(transport);
            }
        }

        [Test]
        public void RawTransport_NewLocalFramesBuildNWindowsAndIdleTailUsesNewSequences()
        {
            using (Socket server = CreateServer())
            using (var transport = CreateTransport())
            {
                transport.Start("127.0.0.1", GetPort(server));
                StartRunning(server, transport, 6);
                for (int frameID = 0; frameID <= 6; frameID++)
                    Assert.IsTrue(transport.TryEnqueueLocalInput((uint)(100 + frameID), frameID));

                RawUdpInputWindow last = null;
                for (int frameID = 0; frameID <= 6; frameID++)
                {
                    last = ReceiveInput(server, 6);
                    Assert.AreEqual((uint)frameID, last.PacketSequence);
                    Assert.AreEqual(frameID, last.LatestFrameID);
                    Assert.AreEqual(Math.Min(6, frameID + 1), last.InputCount);
                }

                RawUdpInputWindow tail = ReceiveInput(server, 6);
                Assert.AreEqual(7u, tail.PacketSequence);
                Assert.AreEqual(last.LatestFrameID, tail.LatestFrameID);
                CollectionAssert.AreEqual(
                    ToPairs(last.Entries),
                    ToPairs(tail.Entries));
                transport.RequestStop();
                AssertStopsWithin(transport);
            }
        }

        [Test]
        public void RawTransport_UpstreamPacketSequenceStartsAt0IncrementsPerDatagramAndWraps()
        {
            using (Socket server = CreateServer())
            using (var transport = CreateTransport())
            {
                transport.Start("127.0.0.1", GetPort(server));
                StartRunning(server, transport, 1);
                FieldInfo sequenceField = typeof(RawUdpInputTransport).GetField(
                    "_nextPacketSequence",
                    InstanceFields);
                Assert.IsNotNull(sequenceField);
                sequenceField.SetValue(transport, uint.MaxValue - 1u);

                for (int frameID = 0; frameID < 3; frameID++)
                    Assert.IsTrue(transport.TryEnqueueLocalInput((uint)(10 + frameID), frameID));

                Assert.AreEqual(uint.MaxValue - 1u, ReceiveInput(server, 1).PacketSequence);
                Assert.AreEqual(uint.MaxValue, ReceiveInput(server, 1).PacketSequence);
                Assert.AreEqual(0u, ReceiveInput(server, 1).PacketSequence);
                transport.RequestStop();
                AssertStopsWithin(transport);
            }
        }

        [Test]
        public void RawTransport_IdleAndTailWindowsUse33msLogicCadenceAndProvideAtLeastNOpportunities()
        {
            var clock = new ManualClock();
            using (Socket server = CreateServer())
            using (var transport = new RawUdpInputTransport(clock, CreateNonce))
            {
                transport.Start("127.0.0.1", GetPort(server));
                StartRunning(server, transport, 6);
                for (int frameID = 0; frameID < 6; frameID++)
                    Assert.IsTrue(transport.TryEnqueueLocalInput((uint)(20 + frameID), frameID));
                for (int index = 0; index < 6; index++)
                    ReceiveInput(server, 6);

                clock.Advance(32u);
                Assert.IsFalse(server.Poll(30000, SelectMode.SelectRead));
                for (int opportunity = 0; opportunity < 6; opportunity++)
                {
                    clock.Advance(opportunity == 0 ? 1u : 33u);
                    RawUdpInputWindow tail = ReceiveInput(server, 6);
                    Assert.AreEqual(5, tail.LatestFrameID);
                    Assert.AreEqual(6, tail.InputCount);
                }
                transport.RequestStop();
                AssertStopsWithin(transport);
            }
        }

        [Test]
        public void RawTransport_LocalFramesStartAt0RemainContiguousAndKeep256ImmutableHistory()
        {
            using (Socket server = CreateServer())
            using (var transport = CreateTransport())
            {
                transport.Start("127.0.0.1", GetPort(server));
                StartRunning(server, transport, 16);
                for (int frameID = 0; frameID < 256; frameID++)
                    Assert.IsTrue(transport.TryEnqueueLocalInput((uint)(5000 + frameID), frameID));

                RawUdpInputWindow final = null;
                for (int frameID = 0; frameID < 256; frameID++)
                    final = ReceiveInput(server, 16);
                Assert.AreEqual(255, final.LatestFrameID);
                Assert.AreEqual(16, final.InputCount);
                RawUdpInputEntry[] entries = final.Entries;
                Assert.AreEqual(240, entries[0].FrameID);
                Assert.AreEqual(255, entries[15].FrameID);
                Assert.AreEqual(5255u, entries[15].Raw);
                Assert.IsTrue(SpinWait.SpinUntil(
                    () => transport.Diagnostics.LatestLocalFrameID == 255,
                    1000));
                transport.RequestStop();
                AssertStopsWithin(transport);
            }
        }

        [Test]
        public void RawTransport_LocalGapRollbackOrConflictPublishesOutboundHistoryFaultWithoutOverwrite()
        {
            AssertOutboundHistoryFault(new[] { 1 }, new[] { 11u });
            AssertOutboundHistoryFault(new[] { 0, 1, 0 }, new[] { 10u, 11u, 10u });
            AssertOutboundHistoryFault(new[] { 0, 0 }, new[] { 10u, 99u });
        }

        [Test]
        public void RawTransport_RemoteActualPublishesOnceWithMonotonicReceiveSequence()
        {
            using (Socket server = CreateServer())
            using (var transport = CreateTransport())
            {
                transport.Start("127.0.0.1", GetPort(server));
                EndPoint client = StartRunning(server, transport, 1);
                SendInput(server, client, 1u, 0, 100u, 1);
                SendInput(server, client, 1u, 0, 100u, 1);
                SendInput(server, client, 2u, 0, 100u, 1);
                SendInput(server, client, 3u, 1, 101u, 1);

                Assert.IsTrue(SpinWait.SpinUntil(
                    () => transport.Diagnostics.RemoteArrivalQueueCount == 2,
                    1000));
                Assert.IsTrue(transport.TryDequeueRemoteInput(out NetworkPacketArrival first));
                Assert.IsTrue(transport.TryDequeueRemoteInput(out NetworkPacketArrival second));
                Assert.IsFalse(transport.TryDequeueRemoteInput(out _));
                Assert.AreEqual(0, first.RemoteFrameID);
                Assert.AreEqual(0L, first.ReceiveSequence);
                Assert.AreEqual(1, second.RemoteFrameID);
                Assert.AreEqual(1L, second.ReceiveSequence);
                transport.RequestStop();
                AssertStopsWithin(transport);
            }
        }

        [Test]
        public void RawTransport_QueueCapacityFaultsWithoutOverwritingAcceptedValues()
        {
            using (Socket server = CreateServer())
            using (var transport = CreateTransport())
            {
                transport.Start("127.0.0.1", GetPort(server));
                EndPoint client = StartRunning(server, transport, 1);
                for (int frameID = 0; frameID <= 256; frameID++)
                {
                    SendInput(
                        server,
                        client,
                        (uint)(frameID + 1),
                        frameID,
                        (uint)(7000 + frameID),
                        1);
                }

                Assert.IsTrue(SpinWait.SpinUntil(
                    () => transport.State == NetworkSessionState.Terminated,
                    2000));
                AssertEventReason(transport, NetworkTransportEventReason.CapacityExceeded);
                int count = 0;
                while (transport.TryDequeueRemoteInput(out NetworkPacketArrival arrival))
                {
                    Assert.AreEqual(count, arrival.RemoteFrameID);
                    Assert.AreEqual((uint)(7000 + count), arrival.Raw);
                    count++;
                }
                Assert.AreEqual(256, count);
                AssertStopsWithin(transport);
            }
        }

        [Test]
        public void RawTransport_DownlinkGapSendsRawFaultAndPublishesExactTerminalReason()
        {
            using (Socket server = CreateServer())
            using (var transport = CreateTransport())
            {
                transport.Start("127.0.0.1", GetPort(server));
                EndPoint client = StartRunning(server, transport, 1);
                for (int frameID = 1; frameID <= 17; frameID++)
                {
                    SendInput(
                        server,
                        client,
                        (uint)frameID,
                        frameID,
                        (uint)(8000 + frameID),
                        1);
                }

                ReceiveUntil(
                    server,
                    RouteCMessageType.RawFault,
                    out RouteCProtocolMessage faultMessage);
                Assert.IsTrue(RawUdpProtocolCodec.TryDecodeFault(
                    faultMessage.Payload,
                    out RawUdpFault fault));
                Assert.AreEqual(
                    RawUdpFaultReason.UnrecoverableInputGap,
                    fault.Reason);
                Assert.AreEqual(0, fault.FrameID);
                Assert.IsTrue(SpinWait.SpinUntil(
                    () => transport.State == NetworkSessionState.Terminated,
                    1000));
                AssertEventReason(
                    transport,
                    NetworkTransportEventReason.UnrecoverableInputGap);
                AssertStopsWithin(transport);
            }
        }

        private static readonly RouteCSessionId TestSession =
            new RouteCSessionId(1UL, 2UL);

        private static RawUdpInputTransport CreateTransport()
        {
            return new RawUdpInputTransport(
                new StopwatchMonotonicClock(),
                CreateNonce);
        }

        private static byte[] CreateNonce()
        {
            var nonce = new byte[16];
            for (byte index = 0; index < nonce.Length; index++)
                nonce[index] = index;
            return nonce;
        }

        private static Socket CreateServer()
        {
            var server = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp);
            server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            server.ReceiveTimeout = 2000;
            return server;
        }

        private static int GetPort(Socket server)
        {
            return ((IPEndPoint)server.LocalEndPoint).Port;
        }

        private static EndPoint StartRunning(
            Socket server,
            RawUdpInputTransport transport,
            byte windowSize = 1)
        {
            EndPoint client = ReceiveUntil(
                server,
                RouteCMessageType.RawHello,
                out RouteCProtocolMessage hello);
            Assert.IsTrue(RawUdpProtocolCodec.TryDecodeHello(
                hello.Payload,
                out byte[] nonce));
            SendMessage(
                server,
                client,
                new RouteCProtocolMessage(
                    RouteCMessageType.RawWelcome,
                    TestSession,
                    1u,
                    RawUdpProtocolCodec.EncodeWelcome(
                        nonce,
                        0,
                        windowSize)));
            ReceiveUntil(server, RouteCMessageType.RawReady, out _);
            SendMessage(
                server,
                client,
                new RouteCProtocolMessage(
                    RouteCMessageType.RawStart,
                    TestSession,
                    1u,
                    RawUdpProtocolCodec.EncodeStart(0)));
            Assert.IsTrue(SpinWait.SpinUntil(
                () => transport.State == NetworkSessionState.Running,
                1000));
            return client;
        }

        private static RawUdpInputWindow ReceiveInput(
            Socket server,
            byte windowSize)
        {
            ReceiveUntil(
                server,
                RouteCMessageType.RawInput,
                out RouteCProtocolMessage message);
            Assert.IsTrue(RawUdpProtocolCodec.TryDecodeInput(
                message.Payload,
                windowSize,
                out RawUdpInputWindow window,
                out RouteCProtocolDropReason reason));
            Assert.AreEqual(RouteCProtocolDropReason.None, reason);
            return window;
        }

        private static void SendInput(
            Socket server,
            EndPoint client,
            uint sequence,
            int frameID,
            uint raw,
            byte windowSize)
        {
            int count = Math.Min(windowSize, frameID + 1);
            var entries = new RawUdpInputEntry[count];
            int firstFrameID = frameID - count + 1;
            for (int index = 0; index < entries.Length; index++)
            {
                int entryFrameID = firstFrameID + index;
                entries[index] = new RawUdpInputEntry(
                    entryFrameID == frameID
                        ? raw
                        : (uint)(8000 + entryFrameID),
                    entryFrameID);
            }
            byte[] payload = RawUdpProtocolCodec.EncodeInput(
                1,
                windowSize,
                sequence,
                frameID,
                entries);
            SendMessage(
                server,
                client,
                new RouteCProtocolMessage(
                    RouteCMessageType.RawInput,
                    TestSession,
                    1u,
                    payload));
        }

        private static string[] ToPairs(RawUdpInputEntry[] entries)
        {
            var pairs = new string[entries.Length];
            for (int index = 0; index < entries.Length; index++)
            {
                pairs[index] = entries[index].FrameID + ":" + entries[index].Raw;
            }
            return pairs;
        }

        private static void AssertOutboundHistoryFault(
            int[] frameIDs,
            uint[] rawValues)
        {
            using (Socket server = CreateServer())
            using (var transport = CreateTransport())
            {
                transport.Start("127.0.0.1", GetPort(server));
                StartRunning(server, transport, 1);
                for (int index = 0; index < frameIDs.Length; index++)
                {
                    Assert.IsTrue(transport.TryEnqueueLocalInput(
                        rawValues[index],
                        frameIDs[index]));
                }

                Assert.IsTrue(SpinWait.SpinUntil(
                    () => transport.State == NetworkSessionState.Terminated,
                    1000));
                AssertEventReason(
                    transport,
                    NetworkTransportEventReason.OutboundHistoryFault);
                AssertStopsWithin(transport);
            }
        }

        private static void AssertEventReason(
            RawUdpInputTransport transport,
            NetworkTransportEventReason expectedReason)
        {
            var stopwatch = Stopwatch.StartNew();
            var spinner = new SpinWait();
            while (stopwatch.ElapsedMilliseconds < 1000)
            {
                while (transport.TryDequeueEvent(out NetworkTransportEvent item))
                {
                    if (item.Reason == expectedReason)
                        return;
                }
                spinner.SpinOnce();
            }
            Assert.Fail("Missing transport event: " + expectedReason);
        }

        private static EndPoint ReceiveUntil(
            Socket server,
            RouteCMessageType expectedType,
            out RouteCProtocolMessage message)
        {
            var buffer = new byte[RouteCProtocolConstants.MaximumDatagramSize];
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 2000)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int count = server.ReceiveFrom(buffer, ref remote);
                if (RouteCProtocolCodec.TryDecode(
                        buffer,
                        count,
                        out RouteCProtocolMessage decoded,
                        out _) &&
                    decoded.MessageType == expectedType)
                {
                    message = decoded;
                    return remote;
                }
            }

            throw new AssertionException("Timed out waiting for " + expectedType);
        }

        private static void SendMessage(
            Socket server,
            EndPoint client,
            RouteCProtocolMessage message)
        {
            byte[] datagram = RouteCProtocolCodec.Encode(
                message.MessageType,
                message.SessionId,
                message.Generation,
                message.Payload);
            server.SendTo(datagram, client);
        }

        private static void AssertStopsWithin(RawUdpInputTransport transport)
        {
            var stopwatch = Stopwatch.StartNew();
            Assert.IsTrue(transport.WaitForStop(
                RouteCProtocolConstants.RawWaitForStopMs));
            stopwatch.Stop();
            Assert.LessOrEqual(
                stopwatch.ElapsedMilliseconds,
                RouteCProtocolConstants.RawWaitForStopMs);
        }

        private static string ReadTransportSource()
        {
            return File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Network",
                "RawUdpInputTransport.cs"));
        }

        private static string SliceMethod(
            string source,
            string startMarker,
            string endMarker)
        {
            int start = source.IndexOf(startMarker, StringComparison.Ordinal);
            int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0);
            Assert.Greater(end, start);
            return source.Substring(start, end - start);
        }

        private sealed class ManualClock : IMonotonicClock
        {
            private int _milliseconds;

            public uint Milliseconds => unchecked((uint)Volatile.Read(
                ref _milliseconds));
            public long Timestamp => Milliseconds;

            public void Advance(uint milliseconds)
            {
                Interlocked.Add(ref _milliseconds, unchecked((int)milliseconds));
            }
        }
    }
}

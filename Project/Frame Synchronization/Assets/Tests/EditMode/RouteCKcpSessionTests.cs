using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class RouteCKcpSessionTests
    {
        [TestCase(1)]
        [TestCase(5)]
        [TestCase(10)]
        [TestCase(20)]
        public void Settings_SupportedInterval_IsPreserved(int intervalMs)
        {
            var settings = new RouteCKcpSettings(intervalMs);

            Assert.AreEqual(intervalMs, settings.IntervalMs);
        }

        [TestCase(0)]
        [TestCase(2)]
        [TestCase(4)]
        [TestCase(6)]
        [TestCase(21)]
        public void Settings_UnsupportedInterval_Throws(int intervalMs)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new RouteCKcpSettings(intervalMs));
        }

        [TestCase(1)]
        [TestCase(5)]
        [TestCase(10)]
        [TestCase(20)]
        public void FirstUpdate_ConfiguredInterval_IsEffectiveCheckDelta(int intervalMs)
        {
            var session = new RouteCKcpSession(
                0x01020304u,
                new RouteCKcpSettings(intervalMs),
                (bytes, count) => { });

            session.Update(100u, 100L);

            Assert.AreEqual(100u + (uint)intervalMs, session.NextUpdateAt(100u));
            Assert.AreEqual(
                intervalMs,
                session.SnapshotDiagnostics().EffectiveCheckDeltaMs);
        }

        [Test]
        public void SendBusinessInput_InMemoryPeer_DeliversExactEightByteMessage()
        {
            var harness = new SessionHarness(10);

            Assert.AreEqual(0, harness.Sender.SendBusinessInput(0x89ABCDEFu, -123));
            harness.Start();

            Assert.IsTrue(harness.HasReceivedBusinessInput);
            Assert.AreEqual(0x89ABCDEFu, harness.ReceivedRaw);
            Assert.AreEqual(-123, harness.ReceivedFrameID);
            Assert.IsFalse(harness.Receiver.TryReceiveBusinessInput(out _, out _));
            Assert.AreEqual(RouteCProtocolConstants.BusinessInputSize, harness.DeliveredBusinessBytes);
        }

        [Test]
        public void InputDatagram_DuplicateDataPacket_DeliversBusinessInputOnce()
        {
            var output = new Queue<byte[]>();
            var sender = CreateSession(bytes => output.Enqueue(bytes));
            var receiver = CreateSession(bytes => { });
            sender.SendBusinessInput(7u, 11);
            sender.Update(0u, 0L);
            uint firstDeadline = sender.NextUpdateAt(0u);
            sender.Update(firstDeadline, firstDeadline);
            byte[] packet = output.Dequeue();

            Assert.AreEqual(0, receiver.InputDatagram(packet, 0, packet.Length));
            Assert.AreEqual(0, receiver.InputDatagram(packet, 0, packet.Length));

            Assert.IsTrue(receiver.TryReceiveBusinessInput(out uint raw, out int frameID));
            Assert.AreEqual(7u, raw);
            Assert.AreEqual(11, frameID);
            Assert.IsFalse(receiver.TryReceiveBusinessInput(out _, out _));
        }

        [Test]
        public void HasPendingBusinessInput_BeforeAndAfterReceive_TracksQueueWithoutConsuming()
        {
            var output = new Queue<byte[]>();
            var sender = CreateSession(bytes => output.Enqueue(bytes));
            var receiver = CreateSession(bytes => { });
            sender.SendBusinessInput(9u, 13);
            sender.Update(0u, 0L);
            uint firstDeadline = sender.NextUpdateAt(0u);
            sender.Update(firstDeadline, firstDeadline);

            byte[] packet = output.Dequeue();
            Assert.AreEqual(0, receiver.InputDatagram(packet, 0, packet.Length));
            Assert.IsTrue(receiver.HasPendingBusinessInput);
            Assert.IsTrue(receiver.HasPendingBusinessInput);

            Assert.IsTrue(receiver.TryReceiveBusinessInput(out _, out _));
            Assert.IsFalse(receiver.HasPendingBusinessInput);
        }

        [Test]
        public void Retransmission_FirstDataDatagramLost_EventuallyDeliversOnce()
        {
            var harness = new SessionHarness(10)
            {
                DropFirstDataDatagram = true
            };
            harness.Sender.SendBusinessInput(0x10203040u, 55);
            harness.Start();

            harness.AdvanceUntil(500u, () => harness.HasReceivedBusinessInput);

            Assert.IsTrue(harness.HasReceivedBusinessInput);
            Assert.AreEqual(0x10203040u, harness.ReceivedRaw);
            Assert.AreEqual(55, harness.ReceivedFrameID);
            Assert.AreEqual(2, harness.SenderOutputCount);
            Assert.IsFalse(harness.Receiver.TryReceiveBusinessInput(out _, out _));
        }

        [Test]
        public void Retransmission_FirstAckLost_DuplicateDataAndAckRecover()
        {
            var harness = new SessionHarness(10)
            {
                DropFirstAckDatagram = true
            };
            harness.Sender.SendBusinessInput(0x55667788u, 91);
            harness.Start();

            harness.AdvanceUntil(650u, () => false);

            Assert.IsTrue(harness.HasReceivedBusinessInput);
            Assert.AreEqual(0x55667788u, harness.ReceivedRaw);
            Assert.AreEqual(91, harness.ReceivedFrameID);
            Assert.AreEqual(2, harness.SenderOutputCount);
            Assert.GreaterOrEqual(harness.ReceiverOutputCount, 2);
            Assert.IsFalse(harness.Receiver.TryReceiveBusinessInput(out _, out _));
        }

        [Test]
        public void KcpMethod_CalledFromAnotherThread_Throws()
        {
            RouteCKcpSession session = CreateSession(bytes => { });
            Exception thrown = null;
            var thread = new Thread(() =>
            {
                try
                {
                    session.NextUpdateAt(0u);
                }
                catch (Exception exception)
                {
                    thrown = exception;
                }
            });

            thread.Start();
            thread.Join();

            Assert.IsInstanceOf<InvalidOperationException>(thrown);
        }

        [TestCase(1)]
        [TestCase(5)]
        [TestCase(10)]
        [TestCase(20)]
        public void CheckDrivenUpdates_SupportedInterval_IsNeverClamped(int intervalMs)
        {
            var session = new RouteCKcpSession(
                0x01020304u,
                new RouteCKcpSettings(intervalMs),
                (bytes, count) => { });
            var observedGaps = new List<uint>();
            uint lastUpdateAt = 0u;
            session.Update(0u, TicksFromMilliseconds(0));
            uint nextUpdateAt = session.NextUpdateAt(0u);

            for (uint nowMs = 1u; nowMs <= 80u; nowMs++)
            {
                if (nowMs != nextUpdateAt)
                    continue;

                observedGaps.Add(nowMs - lastUpdateAt);
                session.Update(nowMs, TicksFromMilliseconds(nowMs));
                lastUpdateAt = nowMs;
                nextUpdateAt = session.NextUpdateAt(nowMs);
            }

            Assert.IsNotEmpty(observedGaps);
            Assert.That(observedGaps, Is.All.EqualTo((uint)intervalMs));
        }

        [Test]
        public void Diagnostics_LateUpdate_RecordsGapAndWakeupErrorInIntegerTicks()
        {
            var session = CreateSession(bytes => { });
            session.Update(100u, TicksFromMilliseconds(100));

            session.Update(113u, TicksFromMilliseconds(113));
            RouteCKcpDiagnosticsSnapshot snapshot = session.SnapshotDiagnostics();

            Assert.AreEqual(2L, snapshot.UpdateCount);
            Assert.AreEqual(TicksFromMilliseconds(13), snapshot.LastActualUpdateGapTicks);
            Assert.AreEqual(TicksFromMilliseconds(13), snapshot.MeanActualUpdateGapTicks);
            Assert.AreEqual(TicksFromMilliseconds(13), snapshot.MaxActualUpdateGapTicks);
            Assert.AreEqual(TicksFromMilliseconds(3), snapshot.LastWakeupErrorTicks);
            Assert.AreEqual(TicksFromMilliseconds(3), snapshot.MeanWakeupErrorTicks);
            Assert.AreEqual(TicksFromMilliseconds(3), snapshot.MaxWakeupErrorTicks);
            Assert.AreEqual(7, snapshot.EffectiveCheckDeltaMs);
            StringAssert.Contains("requestedIntervalMs=10", snapshot.ToString());
        }

        [Test]
        public void Diagnostics_SendAndInvalidInput_RecordsTrafficErrorsAndWaitSndHighWater()
        {
            var output = new List<byte[]>();
            RouteCKcpSession session = CreateSession(bytes => output.Add(bytes));

            Assert.AreEqual(0, session.SendBusinessInput(1u, 2));
            Assert.AreNotEqual(0, session.InputDatagram(new byte[24], 0, 24));
            session.Update(0u, TicksFromMilliseconds(0));
            session.Update(10u, TicksFromMilliseconds(10));
            RouteCKcpDiagnosticsSnapshot snapshot = session.SnapshotDiagnostics();

            Assert.AreEqual(1L, snapshot.OutputDatagramCount);
            Assert.AreEqual(32L, snapshot.OutputByteCount);
            Assert.AreEqual(1L, snapshot.InputDatagramCount);
            Assert.AreEqual(24L, snapshot.InputByteCount);
            Assert.AreEqual(1L, snapshot.InputErrorCount);
            Assert.AreEqual(0L, snapshot.ReceiveCount);
            Assert.AreEqual(1, snapshot.WaitSndHighWater);
        }

        [Test]
        public void Diagnostics_ReceivedBusinessInput_RecordsOneReceive()
        {
            var harness = new SessionHarness(10);
            harness.Sender.SendBusinessInput(3u, 4);

            harness.Start();
            RouteCKcpDiagnosticsSnapshot snapshot =
                harness.Receiver.SnapshotDiagnostics();

            Assert.AreEqual(1L, snapshot.InputDatagramCount);
            Assert.AreEqual(32L, snapshot.InputByteCount);
            Assert.AreEqual(1L, snapshot.ReceiveCount);
        }

        [Test]
        public void StopwatchClock_RepeatedReads_DoNotMoveBackward()
        {
            IMonotonicClock clock = new StopwatchMonotonicClock();
            long firstTimestamp = clock.Timestamp;
            uint firstMilliseconds = clock.Milliseconds;

            long secondTimestamp = clock.Timestamp;
            uint secondMilliseconds = clock.Milliseconds;

            Assert.GreaterOrEqual(secondTimestamp, firstTimestamp);
            Assert.GreaterOrEqual(secondMilliseconds, firstMilliseconds);
        }

        private static long TicksFromMilliseconds(long milliseconds)
        {
            return milliseconds * Stopwatch.Frequency / 1000L;
        }

        private static RouteCKcpSession CreateSession(Action<byte[]> output)
        {
            return new RouteCKcpSession(
                0x0A0B0C0Du,
                new RouteCKcpSettings(10),
                (bytes, count) =>
                {
                    Assert.AreEqual(bytes.Length, count);
                    output(bytes);
                });
        }

        private sealed class SessionHarness
        {
            private readonly Queue<byte[]> _senderOutput = new Queue<byte[]>();
            private readonly Queue<byte[]> _receiverOutput = new Queue<byte[]>();
            private uint _senderNextUpdateAt;
            private uint _receiverNextUpdateAt;

            public SessionHarness(int intervalMs)
            {
                var settings = new RouteCKcpSettings(intervalMs);
                Sender = new RouteCKcpSession(
                    0x0A0B0C0Du,
                    settings,
                    (bytes, count) => _senderOutput.Enqueue(bytes));
                Receiver = new RouteCKcpSession(
                    0x0A0B0C0Du,
                    settings,
                    (bytes, count) => _receiverOutput.Enqueue(bytes));
            }

            public RouteCKcpSession Sender { get; }

            public RouteCKcpSession Receiver { get; }

            public bool DropFirstDataDatagram { get; set; }

            public bool DropFirstAckDatagram { get; set; }

            public int SenderOutputCount { get; private set; }

            public int ReceiverOutputCount { get; private set; }

            public bool HasReceivedBusinessInput { get; private set; }

            public uint ReceivedRaw { get; private set; }

            public int ReceivedFrameID { get; private set; }

            public int DeliveredBusinessBytes { get; private set; }

            public void Start()
            {
                Sender.Update(0u, 0L);
                Receiver.Update(0u, 0L);
                DrainNetwork();
                ReceiveBusinessInput();
                _senderNextUpdateAt = Sender.NextUpdateAt(0u);
                _receiverNextUpdateAt = Receiver.NextUpdateAt(0u);
                AdvanceOneDeadline();
            }

            public void AdvanceUntil(uint maximumTimeMs, Func<bool> stop)
            {
                while (!stop())
                {
                    uint nowMs = Math.Min(_senderNextUpdateAt, _receiverNextUpdateAt);
                    if (nowMs > maximumTimeMs)
                        break;

                    AdvanceOneDeadline();
                }
            }

            private void AdvanceOneDeadline()
            {
                uint nowMs = Math.Min(_senderNextUpdateAt, _receiverNextUpdateAt);
                if (_senderNextUpdateAt == nowMs)
                    Sender.Update(nowMs, nowMs);
                if (_receiverNextUpdateAt == nowMs)
                    Receiver.Update(nowMs, nowMs);

                DrainNetwork();
                ReceiveBusinessInput();
                _senderNextUpdateAt = Sender.NextUpdateAt(nowMs);
                _receiverNextUpdateAt = Receiver.NextUpdateAt(nowMs);
            }

            private void DrainNetwork()
            {
                while (_senderOutput.Count > 0)
                {
                    byte[] datagram = _senderOutput.Dequeue();
                    SenderOutputCount++;
                    if (DropFirstDataDatagram && SenderOutputCount == 1)
                        continue;

                    DeliveredBusinessBytes = datagram.Length - RouteCProtocolConstants.KcpHeaderSize;
                    Assert.AreEqual(0, Receiver.InputDatagram(datagram, 0, datagram.Length));
                }

                while (_receiverOutput.Count > 0)
                {
                    byte[] datagram = _receiverOutput.Dequeue();
                    ReceiverOutputCount++;
                    if (DropFirstAckDatagram && ReceiverOutputCount == 1)
                        continue;

                    Assert.AreEqual(0, Sender.InputDatagram(datagram, 0, datagram.Length));
                }
            }

            private void ReceiveBusinessInput()
            {
                if (HasReceivedBusinessInput)
                    return;

                HasReceivedBusinessInput = Receiver.TryReceiveBusinessInput(
                    out uint raw,
                    out int frameID);
                if (HasReceivedBusinessInput)
                {
                    ReceivedRaw = raw;
                    ReceivedFrameID = frameID;
                }
            }
        }
    }
}

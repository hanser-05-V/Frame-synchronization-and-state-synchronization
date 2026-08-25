using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public sealed class KcpResumeProtocolTests
    {
        [Test]
        public void HistoryDisposition_DefinesCapacityExceeded()
        {
            Assert.IsTrue(
                Enum.IsDefined(
                    typeof(OutboundHistoryDisposition),
                    "CapacityExceeded"),
                "Task 9 requires a distinct CapacityExceeded disposition.");
        }

        [Test]
        public void Record_NormalRollingEviction_IsAcceptedNotCapacityExceeded()
        {
            var history = new OutboundActualHistory(4);
            for (int frameID = 0; frameID <= 4; frameID++)
            {
                Assert.AreEqual(
                    OutboundHistoryDisposition.Accepted,
                    history.Record(frameID, unchecked((uint)frameID)));
            }

            Assert.IsFalse(history.TryGet(0, out _));
            Assert.AreEqual(
                OutboundHistoryDisposition.HistoryUnavailable,
                history.Record(0, 100u));
        }

        [Test]
        public void Record_FrozenLiveTailBeyondCapacity_ReturnsCapacityExceededWithoutOverwrite()
        {
            var history = new OutboundActualHistory(4);
            Assert.AreEqual(
                OutboundHistoryDisposition.Accepted,
                history.Record(7, 0x70u));

            MethodInfo tryBeginFreeze = typeof(OutboundActualHistory).GetMethod(
                "TryBeginFreeze",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(
                tryBeginFreeze,
                "Task 9 requires an explicit production freeze boundary.");
            Assert.IsTrue((bool)tryBeginFreeze.Invoke(history, new object[] { 7 }));

            for (int frameID = 8; frameID <= 11; frameID++)
            {
                Assert.AreEqual(
                    OutboundHistoryDisposition.Accepted,
                    history.Record(frameID, unchecked((uint)frameID)));
            }

            OutboundHistoryDisposition capacityExceeded =
                (OutboundHistoryDisposition)Enum.Parse(
                    typeof(OutboundHistoryDisposition),
                    "CapacityExceeded");
            Assert.AreEqual(
                capacityExceeded,
                history.Record(12, 0x120u));
            Assert.IsTrue(history.TryGet(7, out uint frozenRaw));
            Assert.AreEqual(0x70u, frozenRaw);
            Assert.IsFalse(history.TryGet(12, out _));
        }

        [Test]
        public void Record_FrozenLiveTailDuplicate_PreservesFirstRaw()
        {
            var history = new OutboundActualHistory(4);
            history.Record(7, 0x70u);

            MethodInfo tryBeginFreeze = typeof(OutboundActualHistory).GetMethod(
                "TryBeginFreeze",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(tryBeginFreeze);
            Assert.IsTrue((bool)tryBeginFreeze.Invoke(history, new object[] { 7 }));

            Assert.AreEqual(
                OutboundHistoryDisposition.Accepted,
                history.Record(8, 0x80u));
            Assert.AreEqual(
                OutboundHistoryDisposition.IdempotentDuplicate,
                history.Record(8, 0x80u));
            Assert.AreEqual(
                OutboundHistoryDisposition.ConflictingDuplicate,
                history.Record(8, 0x81u));
            Assert.IsTrue(history.TryGet(8, out uint retainedRaw));
            Assert.AreEqual(0x80u, retainedRaw);
        }

        [Test]
        public void ReleaseLiveTail_EmitsAscendingImmutableBatchExactlyOnce()
        {
            var history = new OutboundActualHistory(4);
            history.Record(7, 0x70u);
            Assert.IsTrue(history.TryBeginFreeze(7));
            history.Record(10, 0xA0u);
            history.Record(8, 0x80u);
            history.Record(9, 0x90u);

            Assert.IsTrue(history.TryReleaseLiveTail(out byte[][] payloads));
            Assert.AreEqual(3, payloads.Length);
            for (int index = 0; index < payloads.Length; index++)
            {
                Assert.IsTrue(RouteCProtocolCodec.TryDecodeBusinessInput(
                    payloads[index],
                    out _,
                    out int frameID));
                Assert.AreEqual(8 + index, frameID);
            }
            Assert.IsFalse(history.TryReleaseLiveTail(out _));
            Assert.AreEqual(
                OutboundHistoryDisposition.Accepted,
                history.Record(11, 0xB0u));
        }

        [Test]
        public void BeginFreeze_MigratesAlreadyReceivedFramesAboveBoundaryIntoTail()
        {
            var history = new OutboundActualHistory(4);
            history.Record(7, 0x70u);
            history.Record(9, 0x90u);
            history.Record(8, 0x80u);

            Assert.IsTrue(history.TryBeginFreeze(7));
            Assert.IsTrue(history.TryReleaseLiveTail(out byte[][] payloads));
            Assert.AreEqual(2, payloads.Length);
            for (int index = 0; index < payloads.Length; index++)
            {
                Assert.IsTrue(RouteCProtocolCodec.TryDecodeBusinessInput(
                    payloads[index],
                    out _,
                    out int frameID));
                Assert.AreEqual(8 + index, frameID);
            }
        }

        [Test]
        public void OnlinePeer_ResumeProbe_WaitsForReadinessThenSendsMatchingResumeState()
        {
            var clock = new FakeClock();
            var sent = new List<RouteCProtocolMessage>();
            var events = new List<NetworkTransportEvent>();
            RouteCSessionId session = new RouteCSessionId(11UL, 12UL);
            KcpUdpClientStateMachine machine = CreateRunningMachine(
                clock,
                session,
                sent,
                events);
            byte[] attemptID = Sequence(0x70, RouteCProtocolConstants.NonceSize);
            int sentBeforeProbe = sent.Count;

            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.ResumeProbe,
                session,
                1u,
                RouteCProtocolCodec.EncodeResumeProbe(attemptID)));

            Assert.AreEqual(NetworkSessionState.Resuming, machine.State);
            Assert.AreEqual(
                NetworkTransportEventReason.ResumeRequired,
                events[events.Count - 1].Reason);
            Assert.AreEqual(sentBeforeProbe, sent.Count);

            var readiness = new ResumeReadiness(115, 100, 123);
            machine.SubmitResumeReadiness(in readiness);

            RouteCProtocolMessage state = sent[sent.Count - 1];
            Assert.AreEqual(RouteCMessageType.ResumeState, state.MessageType);
            Assert.IsTrue(RouteCProtocolCodec.TryDecodeResumeState(
                state.Payload,
                out byte[] actualAttemptID,
                out int remoteThrough,
                out int recoveryFloor,
                out int localThrough));
            CollectionAssert.AreEqual(attemptID, actualAttemptID);
            Assert.AreEqual(115, remoteThrough);
            Assert.AreEqual(100, recoveryFloor);
            Assert.AreEqual(123, localThrough);
        }

        [Test]
        public void OnlinePeer_ResumeState_RetriesAfterTwoHundredFiftyMillisecondsWithoutChangingAttempt()
        {
            var clock = new FakeClock();
            var sent = new List<RouteCProtocolMessage>();
            var events = new List<NetworkTransportEvent>();
            RouteCSessionId session = new RouteCSessionId(21UL, 22UL);
            KcpUdpClientStateMachine machine = CreateRunningMachine(
                clock,
                session,
                sent,
                events);
            byte[] attemptID = Sequence(0x40, RouteCProtocolConstants.NonceSize);
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.ResumeProbe,
                session,
                1u,
                RouteCProtocolCodec.EncodeResumeProbe(attemptID)));
            var readiness = new ResumeReadiness(15, 10, 20);
            machine.SubmitResumeReadiness(in readiness);
            byte[] firstPayload = sent[sent.Count - 1].Payload;

            clock.Advance(249u);
            machine.Tick();
            Assert.AreEqual(1, CountMessages(sent, RouteCMessageType.ResumeState));
            clock.Advance(1u);
            machine.Tick();

            Assert.AreEqual(2, CountMessages(sent, RouteCMessageType.ResumeState));
            CollectionAssert.AreEqual(firstPayload, sent[sent.Count - 1].Payload);
        }

        [Test]
        public void ResumeControl_WrongSessionGenerationAttemptAndDirection_AreSeparatelyCounted()
        {
            var clock = new FakeClock();
            var sent = new List<RouteCProtocolMessage>();
            var events = new List<NetworkTransportEvent>();
            RouteCSessionId session = new RouteCSessionId(31UL, 32UL);
            KcpUdpClientStateMachine machine = CreateRunningMachine(
                clock,
                session,
                sent,
                events);
            byte[] attemptID = Sequence(0x20, RouteCProtocolConstants.NonceSize);

            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.ResumeState,
                session,
                1u,
                RouteCProtocolCodec.EncodeResumeState(attemptID, 1, 0, 1)));
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.ResumeProbe,
                new RouteCSessionId(99UL, 99UL),
                1u,
                RouteCProtocolCodec.EncodeResumeProbe(attemptID)));
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.ResumeProbe,
                session,
                2u,
                RouteCProtocolCodec.EncodeResumeProbe(attemptID)));
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.ResumeProbe,
                session,
                1u,
                RouteCProtocolCodec.EncodeResumeProbe(attemptID)));
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.ResumeProbe,
                session,
                1u,
                RouteCProtocolCodec.EncodeResumeProbe(
                    Sequence(0x50, RouteCProtocolConstants.NonceSize))));

            AssertCounter(machine, "ResumeWrongDirectionCount", 1);
            AssertCounter(machine, "ResumeWrongSessionCount", 1);
            AssertCounter(machine, "ResumeWrongGenerationCount", 1);
            AssertCounter(machine, "ResumeWrongAttemptCount", 1);
        }

        [TestCase(ResumeRejectedReason.ResumeGraceExpired)]
        [TestCase(ResumeRejectedReason.UnsafeResume)]
        public void ResumeAccepted_PeerWaitsForWholeRangeThenCompletesAndReturnsRunning(
            ResumeRejectedReason terminalReason)
        {
            var clock = new FakeClock();
            var sent = new List<RouteCProtocolMessage>();
            var events = new List<NetworkTransportEvent>();
            RouteCSessionId session = new RouteCSessionId(41UL, 42UL);
            KcpUdpClientStateMachine machine = CreateRunningMachine(
                clock,
                session,
                sent,
                events);
            byte[] attemptID = Sequence(0x60, RouteCProtocolConstants.NonceSize);
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.ResumeProbe,
                session,
                1u,
                RouteCProtocolCodec.EncodeResumeProbe(attemptID)));
            var readiness = new ResumeReadiness(115, 100, 123);
            machine.SubmitResumeReadiness(in readiness);
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.ResumeAccepted,
                session,
                1u,
                RouteCProtocolCodec.EncodeResumeAccepted(
                    attemptID,
                    116,
                    120,
                    111,
                    123,
                    116,
                    120)));

            Assert.IsTrue(machine.TryTakeResumePlan(out KcpClientResumePlan plan));
            Assert.IsFalse(machine.TryTakeResumePlan(out _));
            var coordinator = new KcpClientResumeCoordinator(
                new OutboundActualHistory(256));
            for (int frameID = 116; frameID <= 120; frameID++)
                coordinator.ObserveRemoteFrame(frameID);
            Assert.IsTrue(coordinator.TryBegin(plan, out byte[][] uploads));
            Assert.AreEqual(0, uploads.Length);
            Assert.IsTrue(
                coordinator.IsCompleteReady,
                "KCP replay that arrived before unreliable Accepted was forgotten.");

            machine.SubmitResumeProgress(
                coordinator.UploadedLocalThrough,
                coordinator.ReceivedRemoteThrough);
            RouteCProtocolMessage complete = sent[sent.Count - 1];
            Assert.AreEqual(RouteCMessageType.ResumeComplete, complete.MessageType);
            byte[] firstComplete = complete.Payload;
            clock.Advance(RouteCProtocolConstants.HandshakeRetryMs);
            machine.Tick();
            CollectionAssert.AreEqual(firstComplete, sent[sent.Count - 1].Payload);

            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.ResumeComplete,
                session,
                1u,
                firstComplete));
            Assert.AreEqual(NetworkSessionState.Running, machine.State);
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.ResumeAccepted,
                session,
                1u,
                RouteCProtocolCodec.EncodeResumeAccepted(
                    attemptID,
                    116,
                    120,
                    111,
                    123,
                    116,
                    120)));
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.ResumeProbe,
                session,
                1u,
                RouteCProtocolCodec.EncodeResumeProbe(attemptID)));
            Assert.AreEqual(
                NetworkSessionState.Running,
                machine.State,
                "Delayed controls from a completed attempt must be idempotent.");
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.ResumeRejected,
                session,
                1u,
                RouteCProtocolCodec.EncodeResumeRejected(
                    new byte[RouteCProtocolConstants.NonceSize],
                    (ushort)terminalReason)));
            Assert.AreEqual(
                NetworkSessionState.Terminated,
                machine.State,
                "A completed AttemptID must not hide a later zero-ID terminal rejection.");
            Assert.IsTrue(coordinator.TryReleaseLocalTail(out _));
            Assert.IsFalse(coordinator.TryReleaseLocalTail(out _));
        }

        [Test]
        public void ResumeCoordinator_MissingReconnectActual_RejectsBeforeFreeze()
        {
            byte[] attemptID = Sequence(0x70, RouteCProtocolConstants.NonceSize);
            var plan = new KcpClientResumePlan(
                attemptID,
                false,
                116,
                120,
                111,
                123,
                116,
                120);
            var history = new OutboundActualHistory(256);
            for (int frameID = 117; frameID <= 120; frameID++)
                history.Record(frameID, unchecked((uint)frameID));
            var coordinator = new KcpClientResumeCoordinator(history);

            Assert.IsFalse(coordinator.TryBegin(plan, out byte[][] uploads));
            Assert.AreEqual(0, uploads.Length);
            Assert.IsFalse(coordinator.IsActive);
            Assert.AreEqual(
                OutboundHistoryDisposition.Accepted,
                history.Record(121, 121u));
        }

        [Test]
        public void ClientWorker_MissingReconnectActual_SendsAttemptBoundRejection()
        {
            var clock = new FakeClock();
            var sent = new List<RouteCProtocolMessage>();
            var events = new List<NetworkTransportEvent>();
            RouteCSessionId session = new RouteCSessionId(51UL, 52UL);
            KcpUdpClientStateMachine machine = CreateRunningMachine(
                clock,
                session,
                sent,
                events);
            clock.Advance(RouteCProtocolConstants.DisconnectTimeoutMs);
            machine.Tick();
            Assert.AreEqual(NetworkSessionState.Reconnecting, machine.State);

            byte[] nonce = Sequence(0x01, RouteCProtocolConstants.NonceSize);
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.Welcome,
                session,
                2u,
                RouteCProtocolCodec.EncodeWelcome(
                    nonce,
                    0,
                    78u,
                    Sequence(0x50, RouteCProtocolConstants.ReconnectTokenSize),
                    RouteCProtocolConstants.HeartbeatSilenceMs,
                    RouteCProtocolConstants.DisconnectTimeoutMs,
                    true)));
            var readiness = new ResumeReadiness(110, 100, 120);
            machine.SubmitResumeReadiness(in readiness);
            byte[] attemptID = Sequence(0x80, RouteCProtocolConstants.NonceSize);
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.ResumeAccepted,
                session,
                2u,
                RouteCProtocolCodec.EncodeResumeAccepted(
                    attemptID,
                    116,
                    120,
                    111,
                    123,
                    116,
                    120)));

            var history = new OutboundActualHistory(256);
            for (int frameID = 117; frameID <= 120; frameID++)
                history.Record(frameID, unchecked((uint)frameID));
            var coordinator = new KcpClientResumeCoordinator(history);
            var kcp = new RouteCKcpSession(
                78u,
                new RouteCKcpSettings(10),
                (bytes, count) => { });
            MethodInfo apply = typeof(KcpUdpClientTransport).GetMethod(
                "ApplyResumePlan",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(apply);

            apply.Invoke(null, new object[] { machine, kcp, coordinator });

            Assert.AreEqual(NetworkSessionState.Terminated, machine.State);
            RouteCProtocolMessage rejection = sent[sent.Count - 1];
            Assert.AreEqual(RouteCMessageType.ResumeRejected, rejection.MessageType);
            Assert.IsTrue(RouteCProtocolCodec.TryDecodeResumeRejected(
                rejection.Payload,
                out byte[] actualAttemptID,
                out ushort reason));
            CollectionAssert.AreEqual(attemptID, actualAttemptID);
            Assert.AreEqual((ushort)ResumeRejectedReason.UnsafeResume, reason);
        }

        private static KcpUdpClientStateMachine CreateRunningMachine(
            FakeClock clock,
            RouteCSessionId session,
            List<RouteCProtocolMessage> sent,
            List<NetworkTransportEvent> events)
        {
            byte[] nonce = Sequence(0x01, RouteCProtocolConstants.NonceSize);
            var machine = new KcpUdpClientStateMachine(
                clock,
                () => nonce,
                sent.Add,
                events.Add);
            machine.Start();
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.Welcome,
                session,
                1u,
                RouteCProtocolCodec.EncodeWelcome(
                    nonce,
                    0,
                    77u,
                    Sequence(0x30, RouteCProtocolConstants.ReconnectTokenSize),
                    RouteCProtocolConstants.HeartbeatSilenceMs,
                    RouteCProtocolConstants.DisconnectTimeoutMs,
                    false)));
            machine.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.Start,
                session,
                1u,
                RouteCProtocolCodec.EncodeStart(0)));
            Assert.AreEqual(NetworkSessionState.Running, machine.State);
            return machine;
        }

        private static int CountMessages(
            List<RouteCProtocolMessage> messages,
            RouteCMessageType messageType)
        {
            int count = 0;
            for (int index = 0; index < messages.Count; index++)
            {
                if (messages[index].MessageType == messageType)
                    count++;
            }
            return count;
        }

        private static void AssertCounter(
            KcpUdpClientStateMachine machine,
            string propertyName,
            int expected)
        {
            PropertyInfo property = typeof(KcpUdpClientStateMachine).GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(property, propertyName + " must be public.");
            Assert.AreEqual(expected, Convert.ToInt32(property.GetValue(machine)));
        }

        private static byte[] Sequence(byte first, int count)
        {
            var result = new byte[count];
            for (int index = 0; index < result.Length; index++)
                result[index] = unchecked((byte)(first + index));
            return result;
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

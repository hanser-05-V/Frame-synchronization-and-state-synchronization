using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public sealed class RawUdpLedgerIntegrationTests
    {
        private const int WindowSize = 6;
        private const int FrameCount = 80;
        private static readonly FixedInt MoveDistance = FixedInt.FromInt(1);
        private static readonly RouteCSessionId Session =
            new RouteCSessionId(0x0102030405060708UL, 0x1112131415161718UL);

        [Test]
        public void N6_DropOneThroughFiveConsecutiveDatagrams_PublishesEachRemoteActualOnceAndConverges()
        {
            for (int dropCount = 1; dropCount <= 5; dropCount++)
            {
                StreamResult result = RunStream(
                    20,
                    dropCount,
                    new RawUdpDatagramFaultProfile(
                        0, 0, 0, 0, 0, 0, 20260820u),
                    false);

                AssertNoTerminal(result);
                Assert.AreEqual(FrameCount - 1, result.Ledger.ConfirmedThroughFrame);
                for (int frameID = 0; frameID < FrameCount; frameID++)
                    Assert.AreEqual(1, result.PublicationCounts[frameID]);
            }
        }

        [Test]
        public void N6_AlignedSixDropBurst_TerminatesBothWithSameGapFrameAndReason()
        {
            RawUdpFault first = RunAlignedGap();
            RawUdpFault second = RunAlignedGap();

            Assert.AreEqual(RawUdpFaultReason.UnrecoverableInputGap, first.Reason);
            Assert.AreEqual(first.Reason, second.Reason);
            Assert.AreEqual(0, first.PlayerIndex);
            Assert.AreEqual(first.PlayerIndex, second.PlayerIndex);
            Assert.AreEqual(0, first.FrameID);
            Assert.AreEqual(first.FrameID, second.FrameID);
        }

        [Test]
        public void DelayJitterReorderDuplicate_SameTraceConvergesWithoutClaimingNoPredictionFork()
        {
            var profile = new RawUdpDatagramFaultProfile(
                70, 25, 0, 35, 60, 25, 20260820u);
            StreamResult first = RunStream(-1, 0, profile, true);
            StreamResult second = RunStream(-1, 0, profile, true);

            CollectionAssert.AreEqual(first.Trace, second.Trace);
            Assert.AreEqual(FrameCount - 1, first.Ledger.ConfirmedThroughFrame);
            Assert.AreEqual(FrameCount - 1, second.Ledger.ConfirmedThroughFrame);
            Assert.IsTrue(first.SawPredictionMismatch);
            Assert.IsTrue(second.SawPredictionMismatch);
            AssertNoTerminal(first);
            AssertNoTerminal(second);
        }

        [Test]
        public void WrongSessionPlayerAndLaneInjection_CannotTerminateLegalMatch()
        {
            var receiver = new RawUdpInputReceiver(WindowSize, true);
            byte[] legal = EncodeWindow(0u, 0);
            byte[] wrongSession = ReEnvelope(
                legal,
                new RouteCSessionId(9UL, 9UL));
            byte[] wrongPlayer = EncodeWindow(1u, 1, 1);

            Assert.IsFalse(TryDeliverBoundLane(
                receiver, wrongSession, Session, 7, 7, out _));
            Assert.IsFalse(TryDeliverBoundLane(
                receiver, wrongPlayer, Session, 7, 7, out _));
            Assert.IsFalse(TryDeliverBoundLane(
                receiver, legal, Session, 8, 7, out _));
            Assert.IsTrue(TryDeliverBoundLane(
                receiver, legal, Session, 7, 7, out RawUdpFault fault));
            Assert.AreEqual(0, (ushort)fault.Reason);
            Assert.AreEqual(0, receiver.LastContiguousFrameID);
        }

        [Test]
        public void SameFrameDifferentRawFromBoundLane_TerminatesBoth()
        {
            RawUdpFault first = RunBoundConflict();
            RawUdpFault second = RunBoundConflict();

            Assert.AreEqual(RawUdpFaultReason.ConflictingInput, first.Reason);
            Assert.AreEqual(first.Reason, second.Reason);
            Assert.AreEqual(0, first.PlayerIndex);
            Assert.AreEqual(first.PlayerIndex, second.PlayerIndex);
            Assert.AreEqual(0, first.FrameID);
            Assert.AreEqual(first.FrameID, second.FrameID);
        }

        [Test]
        public void TwoRawReceivers_DriveCoordinatorsToSameConfirmedWorldHash()
        {
            FrameSyncCoordinator first = CreateCoordinator();
            FrameSyncCoordinator second = CreateCoordinator();
            var firstRemote = new RawUdpInputReceiver(WindowSize, true);
            var secondRemote = new RawUdpInputReceiver(WindowSize, true);

            for (int frameID = 0; frameID < FrameCount; frameID++)
            {
                FrameInput playerZero = new FrameInput((uint)(5000 + frameID));
                FrameInput playerOne = new FrameInput((uint)(1000 + frameID));
                first.RecordActual(frameID, 0, playerZero);
                second.RecordActual(frameID, 1, playerOne);
                Assert.IsTrue(first.Advance(
                    frameID,
                    first.ResolveForPrediction(frameID)).Succeeded);
                Assert.IsTrue(second.Advance(
                    frameID,
                    second.ResolveForPrediction(frameID)).Succeeded);

                PublishWindow(
                    firstRemote,
                    CreatePlayerWindow(1, (uint)frameID, frameID, 1000u),
                    first);
                PublishWindow(
                    secondRemote,
                    CreatePlayerWindow(0, (uint)frameID, frameID, 5000u),
                    second);
            }

            Assert.IsTrue(first.Reconcile().Succeeded);
            Assert.IsTrue(second.Reconcile().Succeeded);
            Assert.AreEqual(FrameCount - 1, first.ConfirmedFrame);
            Assert.AreEqual(first.ConfirmedFrame, second.ConfirmedFrame);
            Assert.AreEqual(
                WorldHash.Compute(first.ConfirmedWorld, first.ConfirmedFrame),
                WorldHash.Compute(second.ConfirmedWorld, second.ConfirmedFrame));
        }

        private static StreamResult RunStream(
            int dropStart,
            int dropCount,
            RawUdpDatagramFaultProfile profile,
            bool createPredictionFork)
        {
            var scheduled = new List<ScheduledDatagram>();
            var trace = new List<string>();
            long stableOrdinal = 0;
            int totalWindows = FrameCount + WindowSize - 1;
            for (uint sequence = 0; sequence < totalWindows; sequence++)
            {
                byte[] datagram = EncodeWindow(
                    sequence,
                    Math.Min((int)sequence, FrameCount - 1));
                RawUdpDatagramFaultDecision decision =
                    RawUdpDatagramFaultModel.Decide(
                        in profile,
                        0,
                        0x8136E510176B1DFEUL,
                        RouteCMessageType.RawInput,
                        0,
                        sequence);
                trace.Add(decision.ToCanonicalTraceLine(
                    0,
                    0x8136E510176B1DFEUL,
                    RouteCMessageType.RawInput,
                    0,
                    sequence));

                bool scriptedDrop = dropStart >= 0 &&
                    sequence >= dropStart &&
                    sequence < dropStart + dropCount;
                if (scriptedDrop || decision.Dropped)
                    continue;

                for (int copy = 0; copy < decision.CopyCount; copy++)
                {
                    scheduled.Add(new ScheduledDatagram(
                        ((long)sequence * RouteCProtocolConstants.RawInputCadenceMs) +
                        decision.EffectiveDueTimeOffsetMs,
                        stableOrdinal++,
                        datagram));
                }
            }
            scheduled.Sort(ScheduledDatagram.Compare);

            var receiver = new RawUdpInputReceiver(WindowSize, true);
            var ledger = new FrameInputLedger();
            if (createPredictionFork)
            {
                for (int frameID = 0; frameID <= 20; frameID++)
                    ledger.ResolveForSimulation(frameID);
            }
            var counts = new int[FrameCount];
            RawUdpFault terminalFault = default;
            foreach (ScheduledDatagram scheduledDatagram in scheduled)
            {
                RouteCProtocolMessage message = DecodeEnvelope(
                    scheduledDatagram.Datagram);
                Assert.IsTrue(RawUdpProtocolCodec.TryDecodeInput(
                    message.Payload,
                    WindowSize,
                    out RawUdpInputWindow window,
                    out _));
                RawUdpInputDisposition disposition = receiver.Accept(
                    in window,
                    out RawUdpInputEntry[] accepted,
                    out terminalFault);
                if (disposition == RawUdpInputDisposition.Conflict ||
                    disposition == RawUdpInputDisposition.CapacityExceeded ||
                    disposition == RawUdpInputDisposition.UnrecoverableGap)
                {
                    break;
                }

                for (int index = 0; index < accepted.Length; index++)
                {
                    RawUdpInputEntry entry = accepted[index];
                    if (entry.FrameID >= FrameCount)
                        continue;
                    counts[entry.FrameID]++;
                    ledger.RecordActual(
                        entry.FrameID,
                        0,
                        new FrameInput((uint)(5000 + entry.FrameID)));
                    ledger.RecordActual(
                        entry.FrameID,
                        1,
                        new FrameInput(entry.Raw));
                }
            }

            return new StreamResult(
                ledger,
                counts,
                terminalFault,
                trace,
                ledger.TryGetEarliestMismatch(out _));
        }

        private static RawUdpFault RunAlignedGap()
        {
            var receiver = new RawUdpInputReceiver(WindowSize, true);
            RawUdpFault fault = default;
            for (uint sequence = 6; sequence <= 22; sequence++)
            {
                RouteCProtocolMessage message = DecodeEnvelope(
                    EncodeWindow(sequence, (int)sequence));
                Assert.IsTrue(RawUdpProtocolCodec.TryDecodeInput(
                    message.Payload,
                    WindowSize,
                    out RawUdpInputWindow window,
                    out _));
                RawUdpInputDisposition disposition = receiver.Accept(
                    in window,
                    out _,
                    out fault);
                if (disposition == RawUdpInputDisposition.UnrecoverableGap)
                    return fault;
            }
            Assert.Fail("Aligned six-datagram gap did not terminate.");
            return default;
        }

        private static void PublishWindow(
            RawUdpInputReceiver receiver,
            RawUdpInputWindow window,
            FrameSyncCoordinator coordinator)
        {
            RawUdpInputDisposition disposition = receiver.Accept(
                in window,
                out RawUdpInputEntry[] accepted,
                out RawUdpFault fault);
            Assert.AreNotEqual(RawUdpInputDisposition.Conflict, disposition);
            Assert.AreNotEqual(RawUdpInputDisposition.CapacityExceeded, disposition);
            Assert.AreNotEqual(RawUdpInputDisposition.UnrecoverableGap, disposition);
            Assert.AreEqual(0, (ushort)fault.Reason);
            for (int index = 0; index < accepted.Length; index++)
            {
                coordinator.RecordActual(
                    accepted[index].FrameID,
                    window.PlayerIndex,
                    new FrameInput(accepted[index].Raw));
            }
        }

        private static RawUdpInputWindow CreatePlayerWindow(
            byte playerIndex,
            uint sequence,
            int latestFrameID,
            uint rawBase)
        {
            int count = Math.Min(WindowSize, latestFrameID + 1);
            int firstFrameID = latestFrameID - count + 1;
            var entries = new RawUdpInputEntry[count];
            for (int index = 0; index < count; index++)
            {
                int frameID = firstFrameID + index;
                entries[index] = new RawUdpInputEntry(
                    rawBase + (uint)frameID,
                    frameID);
            }
            return new RawUdpInputWindow(
                playerIndex,
                sequence,
                latestFrameID,
                entries);
        }

        private static FrameSyncCoordinator CreateCoordinator()
        {
            var playerZero = new PlayerEntity();
            playerZero.Reset(
                new FixedVector3(
                    FixedInt.FromInt(-3),
                    FixedInt.Zero,
                    FixedInt.Zero),
                0);
            var playerOne = new PlayerEntity();
            playerOne.Reset(
                new FixedVector3(
                    FixedInt.FromInt(3),
                    FixedInt.Zero,
                    FixedInt.Zero),
                1);
            var players = new[] { playerZero, playerOne };
            var ball = new BallEntity();
            ball.Reset(FixedVector3.Zero);
            playerZero.hasBall = true;
            ball.state = BallEntity.EState.Held;
            ball.holderPlayerIndex = 0;
            Assert.IsTrue(BallPossessionSystem.TryUpdateHeldBall(playerZero, ball));
            var predicted = new DeterministicWorld(
                players,
                new[]
                {
                    new PlayerStateMachine(playerZero),
                    new PlayerStateMachine(playerOne)
                },
                ball,
                MoveDistance,
                CourtConstant.LogicDeltaTime);
            SimulationWorldState initial = predicted.Capture(-1);
            return new FrameSyncCoordinator(
                predicted,
                initial,
                MoveDistance,
                CourtConstant.LogicDeltaTime,
                512);
        }

        private static RawUdpFault RunBoundConflict()
        {
            var receiver = new RawUdpInputReceiver(1, true);
            RawUdpInputWindow first = new RawUdpInputWindow(
                0, 0u, 0, new[] { new RawUdpInputEntry(10u, 0) });
            receiver.Accept(in first, out _, out _);
            RawUdpInputWindow conflict = new RawUdpInputWindow(
                0, 1u, 0, new[] { new RawUdpInputEntry(11u, 0) });
            RawUdpInputDisposition disposition = receiver.Accept(
                in conflict,
                out _,
                out RawUdpFault fault);
            Assert.AreEqual(RawUdpInputDisposition.Conflict, disposition);
            return fault;
        }

        private static bool TryDeliverBoundLane(
            RawUdpInputReceiver receiver,
            byte[] datagram,
            RouteCSessionId expectedSession,
            int lane,
            int expectedLane,
            out RawUdpFault fault)
        {
            fault = default;
            RouteCProtocolMessage message = DecodeEnvelope(datagram);
            if (lane != expectedLane || message.SessionId != expectedSession ||
                message.Generation != RouteCProtocolConstants.RawGeneration ||
                !RawUdpProtocolCodec.TryDecodeInput(
                    message.Payload,
                    WindowSize,
                    out RawUdpInputWindow window,
                    out _) ||
                window.PlayerIndex != 0)
            {
                return false;
            }

            receiver.Accept(in window, out _, out fault);
            return true;
        }

        private static byte[] EncodeWindow(
            uint sequence,
            int latestFrameID,
            byte playerIndex = 0)
        {
            int count = Math.Min(WindowSize, latestFrameID + 1);
            int firstFrameID = latestFrameID - count + 1;
            var entries = new RawUdpInputEntry[count];
            for (int index = 0; index < count; index++)
            {
                int frameID = firstFrameID + index;
                entries[index] = new RawUdpInputEntry(
                    (uint)(1000 + frameID),
                    frameID);
            }
            byte[] payload = RawUdpProtocolCodec.EncodeInput(
                playerIndex,
                WindowSize,
                sequence,
                latestFrameID,
                entries);
            return RouteCProtocolCodec.Encode(
                RouteCMessageType.RawInput,
                Session,
                RouteCProtocolConstants.RawGeneration,
                payload);
        }

        private static byte[] ReEnvelope(
            byte[] datagram,
            RouteCSessionId session)
        {
            RouteCProtocolMessage message = DecodeEnvelope(datagram);
            return RouteCProtocolCodec.Encode(
                message.MessageType,
                session,
                message.Generation,
                message.Payload);
        }

        private static RouteCProtocolMessage DecodeEnvelope(byte[] datagram)
        {
            Assert.IsTrue(RouteCProtocolCodec.TryDecode(
                datagram,
                datagram.Length,
                out RouteCProtocolMessage message,
                out _));
            return message;
        }

        private static void AssertNoTerminal(StreamResult result)
        {
            Assert.AreEqual(0, (ushort)result.TerminalFault.Reason);
            Assert.IsFalse(result.Ledger.HasIntegrityFault);
        }

        private sealed class StreamResult
        {
            public StreamResult(
                FrameInputLedger ledger,
                int[] publicationCounts,
                RawUdpFault terminalFault,
                List<string> trace,
                bool sawPredictionMismatch)
            {
                Ledger = ledger;
                PublicationCounts = publicationCounts;
                TerminalFault = terminalFault;
                Trace = trace;
                SawPredictionMismatch = sawPredictionMismatch;
            }

            public FrameInputLedger Ledger { get; }
            public int[] PublicationCounts { get; }
            public RawUdpFault TerminalFault { get; }
            public List<string> Trace { get; }
            public bool SawPredictionMismatch { get; }
        }

        private sealed class ScheduledDatagram
        {
            private readonly byte[] _datagram;

            public ScheduledDatagram(long dueTime, long ordinal, byte[] datagram)
            {
                DueTime = dueTime;
                Ordinal = ordinal;
                _datagram = (byte[])datagram.Clone();
            }

            public long DueTime { get; }
            public long Ordinal { get; }
            public byte[] Datagram => (byte[])_datagram.Clone();

            public static int Compare(
                ScheduledDatagram left,
                ScheduledDatagram right)
            {
                int dueOrder = left.DueTime.CompareTo(right.DueTime);
                return dueOrder != 0
                    ? dueOrder
                    : left.Ordinal.CompareTo(right.Ordinal);
            }
        }
    }
}

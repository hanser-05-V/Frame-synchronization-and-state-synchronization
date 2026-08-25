using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class RawUdpProtocolStateTests
    {
        [Test]
        public void SerialCompare_WrapAndHalfRange_ReturnLockedOrders()
        {
            Assert.AreEqual(
                RawUdpSerialOrder.Equal,
                RawUdpSerialNumber.Compare(10u, 10u));
            Assert.AreEqual(
                RawUdpSerialOrder.Newer,
                RawUdpSerialNumber.Compare(1u, 0u));
            Assert.AreEqual(
                RawUdpSerialOrder.Older,
                RawUdpSerialNumber.Compare(0u, 1u));
            Assert.AreEqual(
                RawUdpSerialOrder.Newer,
                RawUdpSerialNumber.Compare(0u, uint.MaxValue));
            Assert.AreEqual(
                RawUdpSerialOrder.Older,
                RawUdpSerialNumber.Compare(uint.MaxValue, 0u));
            Assert.AreEqual(
                RawUdpSerialOrder.Ambiguous,
                RawUdpSerialNumber.Compare(0x80000000u, 0u));
            Assert.AreEqual(
                RawUdpSerialOrder.Ambiguous,
                RawUdpSerialNumber.Compare(0u, 0x80000000u));
        }

        [Test]
        public void SequenceWindow_Lag63IsInsideAndLag64IsPacketTooOld()
        {
            var window = new RawUdpPacketSequenceWindow();

            Assert.AreEqual(
                RawUdpSequenceDisposition.Accepted,
                window.Observe(100u, new byte[] { 100 }));
            Assert.AreEqual(
                RawUdpSequenceDisposition.Accepted,
                window.Observe(37u, new byte[] { 37 }));
            Assert.AreEqual(
                RawUdpSequenceDisposition.TooOld,
                window.Observe(36u, new byte[] { 36 }));
        }

        [Test]
        public void SequenceWindow_SameSequenceSameBytesIsDuplicateDifferentBytesIsConflict()
        {
            var window = new RawUdpPacketSequenceWindow();
            byte[] source = { 1, 2, 3 };

            Assert.AreEqual(
                RawUdpSequenceDisposition.Accepted,
                window.Observe(7u, source));
            source[0] = 9;
            Assert.AreEqual(
                RawUdpSequenceDisposition.Duplicate,
                window.Observe(7u, new byte[] { 1, 2, 3 }));
            Assert.AreEqual(
                RawUdpSequenceDisposition.Conflict,
                window.Observe(7u, new byte[] { 1, 2, 4 }));
        }

        [Test]
        public void SequenceWindow_HalfRangeJumpIsAmbiguous()
        {
            var window = new RawUdpPacketSequenceWindow();
            Assert.AreEqual(
                RawUdpSequenceDisposition.Accepted,
                window.Observe(0u, new byte[] { 0 }));
            Assert.AreEqual(
                RawUdpSequenceDisposition.Ambiguous,
                window.Observe(0x80000000u, new byte[] { 1 }));
        }

        [Test]
        public void InputReceiver_SameFrameSameRawIsIdempotentDifferentRawIsTerminalConflict()
        {
            var receiver = new RawUdpInputReceiver(1, true);

            AssertAccept(
                receiver,
                CreateWindow(0, 1u, 0, new RawUdpInputEntry(10u, 0)),
                RawUdpInputDisposition.Accepted,
                1,
                out RawUdpFault firstFault);
            AssertNoFault(firstFault);
            AssertAccept(
                receiver,
                CreateWindow(0, 2u, 0, new RawUdpInputEntry(10u, 0)),
                RawUdpInputDisposition.Idempotent,
                0,
                out RawUdpFault duplicateFault);
            AssertNoFault(duplicateFault);
            AssertAccept(
                receiver,
                CreateWindow(0, 3u, 0, new RawUdpInputEntry(11u, 0)),
                RawUdpInputDisposition.Conflict,
                0,
                out RawUdpFault conflictFault);
            Assert.AreEqual(RawUdpFaultReason.ConflictingInput, conflictFault.Reason);
            Assert.AreEqual(0, conflictFault.FrameID);
        }

        [Test]
        public void InputReceiver_OutOfOrderFirstValuesPublishOnceAndAdvanceContiguousFrontier()
        {
            var receiver = new RawUdpInputReceiver(3, true);

            AssertAccept(
                receiver,
                CreateWindow(
                    1,
                    10u,
                    5,
                    new RawUdpInputEntry(103u, 3),
                    new RawUdpInputEntry(104u, 4),
                    new RawUdpInputEntry(105u, 5)),
                RawUdpInputDisposition.Accepted,
                3,
                out _);
            Assert.AreEqual(-1, receiver.LastContiguousFrameID);

            RawUdpInputWindow recovered = CreateWindow(
                1,
                11u,
                2,
                new RawUdpInputEntry(100u, 0),
                new RawUdpInputEntry(101u, 1),
                new RawUdpInputEntry(102u, 2));
            AssertAccept(
                receiver,
                recovered,
                RawUdpInputDisposition.Accepted,
                3,
                out _);
            Assert.AreEqual(5, receiver.LastContiguousFrameID);

            AssertAccept(
                receiver,
                recovered,
                RawUdpInputDisposition.Idempotent,
                0,
                out _);
        }

        [Test]
        public void InputReceiver_DownlinkLowerLatestOnNewSequencePreservesHighestObservedLatest()
        {
            var receiver = new RawUdpInputReceiver(1, true);
            AssertAccept(
                receiver,
                CreateWindow(0, 20u, 5, new RawUdpInputEntry(105u, 5)),
                RawUdpInputDisposition.Accepted,
                1,
                out _);
            AssertAccept(
                receiver,
                CreateWindow(0, 21u, 2, new RawUdpInputEntry(102u, 2)),
                RawUdpInputDisposition.Accepted,
                1,
                out RawUdpFault fault);
            AssertNoFault(fault);
            Assert.AreEqual(5, receiver.HighestObservedLatestFrameID);
        }

        [Test]
        public void InputReceiver_UpstreamNewerSequenceCannotLowerLatestButOlderReorderedSequenceMay()
        {
            var receiver = new RawUdpInputReceiver(1, false);
            AssertAccept(
                receiver,
                CreateWindow(0, 20u, 5, new RawUdpInputEntry(105u, 5)),
                RawUdpInputDisposition.Accepted,
                1,
                out _);
            AssertAccept(
                receiver,
                CreateWindow(0, 21u, 2, new RawUdpInputEntry(102u, 2)),
                RawUdpInputDisposition.Conflict,
                0,
                out RawUdpFault regressionFault);
            Assert.AreEqual(
                RawUdpFaultReason.ProtocolViolation,
                regressionFault.Reason);

            var reorderedReceiver = new RawUdpInputReceiver(1, false);
            AssertAccept(
                reorderedReceiver,
                CreateWindow(0, 21u, 5, new RawUdpInputEntry(105u, 5)),
                RawUdpInputDisposition.Accepted,
                1,
                out _);
            AssertAccept(
                reorderedReceiver,
                CreateWindow(0, 20u, 2, new RawUdpInputEntry(102u, 2)),
                RawUdpInputDisposition.Accepted,
                1,
                out RawUdpFault reorderedFault);
            AssertNoFault(reorderedFault);
        }

        [Test]
        public void InputReceiver_GapRecoveredWithin16UniqueSequencesClearsPendingFault()
        {
            var receiver = new RawUdpInputReceiver(1, true);
            for (uint sequence = 1; sequence <= 15; sequence++)
            {
                int frameID = (int)sequence;
                AssertAccept(
                    receiver,
                    CreateWindow(
                        0,
                        sequence,
                        frameID,
                        new RawUdpInputEntry((uint)(100 + frameID), frameID)),
                    RawUdpInputDisposition.Accepted,
                    1,
                    out RawUdpFault pendingFault);
                AssertNoFault(pendingFault);
            }

            AssertAccept(
                receiver,
                CreateWindow(0, 16u, 0, new RawUdpInputEntry(100u, 0)),
                RawUdpInputDisposition.Accepted,
                1,
                out RawUdpFault recoveredFault);
            AssertNoFault(recoveredFault);
            Assert.AreEqual(15, receiver.LastContiguousFrameID);
        }

        [Test]
        public void InputReceiver_GapAfterEvidencePlus16NewerUniqueSequencesFaultsSameFrame()
        {
            var receiver = new RawUdpInputReceiver(1, true);
            for (uint sequence = 1; sequence <= 17; sequence++)
            {
                int frameID = (int)sequence;
                RawUdpInputDisposition disposition = Accept(
                    receiver,
                    CreateWindow(
                        0,
                        sequence,
                        frameID,
                        new RawUdpInputEntry((uint)(100 + frameID), frameID)),
                    out _,
                    out RawUdpFault fault);
                if (sequence < 17)
                {
                    Assert.AreEqual(RawUdpInputDisposition.Accepted, disposition);
                    AssertNoFault(fault);
                }
                else
                {
                    Assert.AreEqual(
                        RawUdpInputDisposition.UnrecoverableGap,
                        disposition);
                    Assert.AreEqual(
                        RawUdpFaultReason.UnrecoverableInputGap,
                        fault.Reason);
                    Assert.AreEqual(0, fault.FrameID);
                }
            }
        }

        [Test]
        public void InputReceiver_DuplicateSequenceDoesNotConsumeGapGrace()
        {
            var receiver = new RawUdpInputReceiver(1, true);
            RawUdpInputWindow first = CreateWindow(
                0,
                1u,
                1,
                new RawUdpInputEntry(101u, 1));
            Assert.AreEqual(
                RawUdpInputDisposition.Accepted,
                Accept(receiver, first, out _, out _));
            for (int repeat = 0; repeat < 32; repeat++)
            {
                Assert.AreEqual(
                    RawUdpInputDisposition.Idempotent,
                    Accept(receiver, first, out _, out RawUdpFault duplicateFault));
                AssertNoFault(duplicateFault);
            }

            for (uint sequence = 2; sequence <= 16; sequence++)
            {
                int frameID = (int)sequence;
                Assert.AreEqual(
                    RawUdpInputDisposition.Accepted,
                    Accept(
                        receiver,
                        CreateWindow(
                            0,
                            sequence,
                            frameID,
                            new RawUdpInputEntry((uint)(100 + frameID), frameID)),
                        out _,
                        out RawUdpFault pendingFault));
                AssertNoFault(pendingFault);
            }

            Assert.AreEqual(
                RawUdpInputDisposition.UnrecoverableGap,
                Accept(
                    receiver,
                    CreateWindow(0, 17u, 17, new RawUdpInputEntry(117u, 17)),
                    out _,
                    out RawUdpFault terminalFault));
            Assert.AreEqual(0, terminalFault.FrameID);
        }

        [Test]
        public void InputReceiver_256HistoryAndFutureSpanReturnTooOldOrCapacityExceeded()
        {
            var receiver = new RawUdpInputReceiver(1, true);
            for (int frameID = 0; frameID <= 256; frameID++)
            {
                Assert.AreEqual(
                    RawUdpInputDisposition.Accepted,
                    Accept(
                        receiver,
                        CreateWindow(
                            0,
                            (uint)(frameID + 1),
                            frameID,
                            new RawUdpInputEntry((uint)(1000 + frameID), frameID)),
                        out _,
                        out RawUdpFault fault));
                AssertNoFault(fault);
            }

            Assert.AreEqual(
                RawUdpInputDisposition.TooOld,
                Accept(
                    receiver,
                    CreateWindow(0, 258u, 0, new RawUdpInputEntry(1000u, 0)),
                    out RawUdpInputEntry[] oldEntries,
                    out RawUdpFault oldFault));
            Assert.AreEqual(0, oldEntries.Length);
            AssertNoFault(oldFault);

            var capacityReceiver = new RawUdpInputReceiver(1, true);
            Assert.AreEqual(
                RawUdpInputDisposition.Accepted,
                Accept(
                    capacityReceiver,
                    CreateWindow(0, 1u, 0, new RawUdpInputEntry(1000u, 0)),
                    out _,
                    out _));
            Assert.AreEqual(
                RawUdpInputDisposition.CapacityExceeded,
                Accept(
                    capacityReceiver,
                    CreateWindow(0, 2u, 257, new RawUdpInputEntry(1257u, 257)),
                    out _,
                    out RawUdpFault capacityFault));
            Assert.AreEqual(
                RawUdpFaultReason.CapacityExceeded,
                capacityFault.Reason);
            Assert.AreEqual(257, capacityFault.FrameID);
        }

        [Test]
        public void ClientProtocol_HelloRetriesEvery50msAndHandshakeFaultsAt3000ms()
        {
            var harness = new ProtocolHarness();
            Assert.AreEqual(1, CountMessages(harness.Sent, RouteCMessageType.RawHello));
            Assert.AreEqual(RouteCSessionId.Zero, harness.Sent[0].SessionId);
            Assert.AreEqual(0u, harness.Sent[0].Generation);

            for (int retry = 1; retry <= 59; retry++)
            {
                harness.Clock.Advance(50u);
                harness.State.Tick();
                Assert.AreEqual(
                    retry + 1,
                    CountMessages(harness.Sent, RouteCMessageType.RawHello));
                Assert.AreEqual(NetworkSessionState.Handshaking, harness.State.State);
            }

            harness.Clock.Advance(49u);
            harness.State.Tick();
            Assert.AreEqual(NetworkSessionState.Handshaking, harness.State.State);
            harness.Clock.Advance(1u);
            harness.State.Tick();

            Assert.AreEqual(NetworkSessionState.Terminated, harness.State.State);
            AssertLastReason(harness, NetworkTransportEventReason.HandshakeTimeout);
        }

        [Test]
        public void ClientProtocol_MatchingWelcomePublishesReadyEvery50msUntilStart()
        {
            var harness = new ProtocolHarness();
            harness.AcceptWelcome(6);

            Assert.AreEqual(NetworkSessionState.AwaitingReady, harness.State.State);
            Assert.AreEqual(0, harness.State.LocalPlayerIndex);
            Assert.AreEqual(6, harness.State.NegotiatedWindowSize);
            Assert.AreEqual(1, CountMessages(harness.Sent, RouteCMessageType.RawReady));

            harness.Clock.Advance(49u);
            harness.State.Tick();
            Assert.AreEqual(1, CountMessages(harness.Sent, RouteCMessageType.RawReady));
            harness.Clock.Advance(1u);
            harness.State.Tick();
            Assert.AreEqual(2, CountMessages(harness.Sent, RouteCMessageType.RawReady));
        }

        [Test]
        public void ClientProtocol_DuplicateWelcomeMustBeByteIdenticalOrProtocolViolation()
        {
            var harness = new ProtocolHarness();
            RouteCProtocolMessage welcome = harness.CreateWelcome(6);
            harness.State.HandleIncoming(welcome);
            harness.State.HandleIncoming(welcome);
            Assert.AreEqual(NetworkSessionState.AwaitingReady, harness.State.State);

            harness.State.HandleIncoming(harness.CreateWelcome(7));
            Assert.AreEqual(NetworkSessionState.Terminated, harness.State.State);
            AssertLastReason(harness, NetworkTransportEventReason.ProtocolViolation);
        }

        [Test]
        public void ClientProtocol_InvalidWelcomeWindowFromConfiguredServerIsProtocolViolation()
        {
            var harness = new ProtocolHarness();
            var invalidPayload = new byte[18];
            System.Buffer.BlockCopy(harness.Nonce, 0, invalidPayload, 0, 16);
            invalidPayload[16] = 0;
            invalidPayload[17] = 0;

            harness.State.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.RawWelcome,
                harness.Session,
                1u,
                invalidPayload));

            Assert.AreEqual(NetworkSessionState.Terminated, harness.State.State);
            AssertLastReason(harness, NetworkTransportEventReason.ProtocolViolation);
        }

        [Test]
        public void ClientProtocol_StartFrame0TransitionsExactlyOnceAndOtherStartsFault()
        {
            var harness = new ProtocolHarness();
            harness.AcceptWelcome(1);
            RouteCProtocolMessage start = harness.CreateStart(0);
            harness.State.HandleIncoming(start);

            Assert.AreEqual(NetworkSessionState.Running, harness.State.State);
            Assert.IsTrue(harness.State.HasStartedSession);
            Assert.AreEqual(
                1,
                CountEvents(harness.Events, NetworkTransportEventReason.SessionStarted));
            harness.State.HandleIncoming(start);
            Assert.AreEqual(
                1,
                CountEvents(harness.Events, NetworkTransportEventReason.SessionStarted));

            harness.State.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.RawStart,
                harness.Session,
                1u,
                new byte[] { 0, 0, 0, 1 }));
            Assert.AreEqual(NetworkSessionState.Terminated, harness.State.State);
            AssertLastReason(harness, NetworkTransportEventReason.ProtocolViolation);
        }

        [Test]
        public void ClientProtocol_PreStartInputsBufferTo256AndPublishInFrameOrderAfterStart()
        {
            var harness = new ProtocolHarness();
            harness.AcceptWelcome(1);
            for (int frameID = 0; frameID < 256; frameID++)
            {
                harness.State.HandleIncoming(harness.CreateInput(
                    1,
                    (uint)(frameID + 1),
                    frameID,
                    (uint)(1000 + frameID)));
            }

            Assert.AreEqual(0, harness.Arrivals.Count);
            Assert.AreEqual(NetworkSessionState.AwaitingReady, harness.State.State);
            harness.State.HandleIncoming(harness.CreateStart(0));

            Assert.AreEqual(256, harness.Arrivals.Count);
            for (int frameID = 0; frameID < harness.Arrivals.Count; frameID++)
            {
                Assert.AreEqual(frameID, harness.Arrivals[frameID].FrameID);
                Assert.AreEqual((uint)(1000 + frameID), harness.Arrivals[frameID].Raw);
            }
        }

        [Test]
        public void ClientProtocol_SilentlyDropsUnknownSessionAndStaleGeneration()
        {
            var wrongSession = new ProtocolHarness();
            wrongSession.AcceptWelcome(1);
            wrongSession.State.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.RawStart,
                new RouteCSessionId(9UL, 9UL),
                1u,
                RawUdpProtocolCodec.EncodeStart(0)));
            Assert.AreEqual(NetworkSessionState.AwaitingReady, wrongSession.State.State);

            var wrongGeneration = new ProtocolHarness();
            wrongGeneration.AcceptWelcome(1);
            wrongGeneration.State.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.RawStart,
                wrongGeneration.Session,
                2u,
                RawUdpProtocolCodec.EncodeStart(0)));
            Assert.AreEqual(NetworkSessionState.AwaitingReady, wrongGeneration.State.State);

            Assert.AreEqual(0, CountMessages(
                wrongSession.Sent,
                RouteCMessageType.RawFault));
            Assert.AreEqual(0, CountMessages(
                wrongGeneration.Sent,
                RouteCMessageType.RawFault));

            var delayedMatchFull = new ProtocolHarness();
            delayedMatchFull.AcceptWelcome(1);
            delayedMatchFull.State.HandleIncoming(CreateMatchFull());
            Assert.AreEqual(
                NetworkSessionState.AwaitingReady,
                delayedMatchFull.State.State);

            delayedMatchFull.State.HandleIncoming(delayedMatchFull.CreateStart(0));
            delayedMatchFull.State.HandleIncoming(CreateMatchFull());
            Assert.AreEqual(
                NetworkSessionState.Running,
                delayedMatchFull.State.State);

            var zeroSessionWrongDirection = new ProtocolHarness();
            zeroSessionWrongDirection.State.HandleIncoming(
                new RouteCProtocolMessage(
                    RouteCMessageType.RawReady,
                    RouteCSessionId.Zero,
                    0u,
                    RawUdpProtocolCodec.EncodeReady()));
            Assert.AreEqual(
                NetworkSessionState.Handshaking,
                zeroSessionWrongDirection.State.State);
        }

        [Test]
        public void ClientProtocol_RejectsCurrentSessionWrongPlayerAndDirection()
        {

            var wrongPlayer = new ProtocolHarness();
            wrongPlayer.AcceptWelcome(1);
            wrongPlayer.State.HandleIncoming(wrongPlayer.CreateInput(0, 1u, 0, 10u));
            AssertLastReason(wrongPlayer, NetworkTransportEventReason.ProtocolViolation);

            var wrongDirection = new ProtocolHarness();
            wrongDirection.AcceptWelcome(1);
            wrongDirection.State.HandleIncoming(new RouteCProtocolMessage(
                RouteCMessageType.RawReady,
                wrongDirection.Session,
                1u,
                RawUdpProtocolCodec.EncodeReady()));
            AssertLastReason(wrongDirection, NetworkTransportEventReason.ProtocolViolation);
        }

        [Test]
        public void InputReceiver_OutOfOrderUniqueSequencesNewerThanGapEvidenceConsumeGrace()
        {
            var receiver = new RawUdpInputReceiver(1, true);
            Assert.AreEqual(
                RawUdpInputDisposition.Accepted,
                Accept(
                    receiver,
                    CreateWindow(0, 10u, 1, new RawUdpInputEntry(101u, 1)),
                    out _,
                    out _));

            for (uint sequence = 26u; sequence >= 11u; sequence--)
            {
                int frameID = (int)sequence;
                RawUdpInputDisposition disposition = Accept(
                    receiver,
                    CreateWindow(
                        0,
                        sequence,
                        frameID,
                        new RawUdpInputEntry((uint)(100 + frameID), frameID)),
                    out _,
                    out RawUdpFault fault);
                if (sequence > 11u)
                {
                    Assert.AreNotEqual(
                        RawUdpInputDisposition.UnrecoverableGap,
                        disposition);
                    AssertNoFault(fault);
                }
                else
                {
                    Assert.AreEqual(
                        RawUdpInputDisposition.UnrecoverableGap,
                        disposition);
                    Assert.AreEqual(0, fault.FrameID);
                }
            }
        }

        [Test]
        public void ClientProtocol_RunningSilenceFaultsAt3000msWithoutReconnectOrResume()
        {
            var harness = new ProtocolHarness();
            harness.AcceptWelcome(1);
            harness.State.HandleIncoming(harness.CreateStart(0));

            harness.Clock.Advance(2999u);
            harness.State.Tick();
            Assert.AreEqual(NetworkSessionState.Running, harness.State.State);
            harness.Clock.Advance(1u);
            harness.State.Tick();

            Assert.AreEqual(NetworkSessionState.Terminated, harness.State.State);
            AssertLastReason(harness, NetworkTransportEventReason.ConnectionTimedOut);
            Assert.AreEqual(1, CountMessages(harness.Sent, RouteCMessageType.RawHello));
            Assert.AreNotEqual(NetworkSessionState.Reconnecting, harness.State.State);
            Assert.AreNotEqual(NetworkSessionState.Resuming, harness.State.State);
        }

        [Test]
        public void SessionFingerprint_UsesFirst8Sha256BytesWithoutExposingSessionBytes()
        {
            Assert.AreEqual(
                "none",
                RawUdpSessionFingerprint.Format(RouteCSessionId.Zero));
            string fingerprint = RawUdpSessionFingerprint.Format(
                new RouteCSessionId(
                    0x0102030405060708UL,
                    0x1112131415161718UL));

            Assert.AreEqual("8136E510176B1DFE", fingerprint);
            Assert.AreEqual(16, fingerprint.Length);
            Assert.IsFalse(fingerprint.Contains("01020304"));
            Assert.AreEqual(fingerprint.ToUpperInvariant(), fingerprint);
        }

        private static int CountMessages(
            System.Collections.Generic.List<RouteCProtocolMessage> messages,
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

        private static RouteCProtocolMessage CreateMatchFull()
        {
            var fault = new RawUdpFault(
                RawUdpFaultReason.MatchFull,
                255,
                -1,
                -1);
            return new RouteCProtocolMessage(
                RouteCMessageType.RawFault,
                RouteCSessionId.Zero,
                0u,
                RawUdpProtocolCodec.EncodeFault(in fault));
        }

        private static int CountEvents(
            System.Collections.Generic.List<NetworkTransportEvent> events,
            NetworkTransportEventReason reason)
        {
            int count = 0;
            for (int index = 0; index < events.Count; index++)
            {
                if (events[index].Reason == reason)
                    count++;
            }
            return count;
        }

        private static void AssertLastReason(
            ProtocolHarness harness,
            NetworkTransportEventReason expectedReason)
        {
            Assert.Greater(harness.Events.Count, 0);
            Assert.AreEqual(
                expectedReason,
                harness.Events[harness.Events.Count - 1].Reason);
            Assert.AreEqual(NetworkSessionState.Terminated, harness.State.State);
        }

        private static RawUdpInputWindow CreateWindow(
            byte playerIndex,
            uint sequence,
            int latestFrameID,
            params RawUdpInputEntry[] entries)
        {
            return new RawUdpInputWindow(
                playerIndex,
                sequence,
                latestFrameID,
                entries);
        }

        private static void AssertAccept(
            RawUdpInputReceiver receiver,
            RawUdpInputWindow window,
            RawUdpInputDisposition expectedDisposition,
            int expectedPublishedCount,
            out RawUdpFault terminalFault)
        {
            RawUdpInputDisposition disposition = Accept(
                receiver,
                window,
                out RawUdpInputEntry[] published,
                out terminalFault);
            Assert.AreEqual(expectedDisposition, disposition);
            Assert.AreEqual(expectedPublishedCount, published.Length);
        }

        private static RawUdpInputDisposition Accept(
            RawUdpInputReceiver receiver,
            RawUdpInputWindow window,
            out RawUdpInputEntry[] firstAcceptedEntries,
            out RawUdpFault terminalFault)
        {
            return receiver.Accept(
                in window,
                out firstAcceptedEntries,
                out terminalFault);
        }

        private static void AssertNoFault(RawUdpFault fault)
        {
            Assert.AreEqual(0, (ushort)fault.Reason);
        }

        private sealed class ProtocolHarness
        {
            public ProtocolHarness()
            {
                Clock = new FakeClock();
                Nonce = new byte[16];
                for (byte index = 0; index < Nonce.Length; index++)
                    Nonce[index] = index;
                Session = new RouteCSessionId(1UL, 2UL);
                Sent = new System.Collections.Generic.List<RouteCProtocolMessage>();
                Events = new System.Collections.Generic.List<NetworkTransportEvent>();
                Arrivals = new System.Collections.Generic.List<RawUdpInputEntry>();
                State = new RawUdpClientProtocolState(
                    Clock,
                    Nonce,
                    Sent.Add,
                    Events.Add,
                    Arrivals.Add);
                State.Start();
            }

            public FakeClock Clock { get; }
            public byte[] Nonce { get; }
            public RouteCSessionId Session { get; }
            public System.Collections.Generic.List<RouteCProtocolMessage> Sent { get; }
            public System.Collections.Generic.List<NetworkTransportEvent> Events { get; }
            public System.Collections.Generic.List<RawUdpInputEntry> Arrivals { get; }
            public RawUdpClientProtocolState State { get; }

            public void AcceptWelcome(byte windowSize)
            {
                State.HandleIncoming(CreateWelcome(windowSize));
            }

            public RouteCProtocolMessage CreateWelcome(byte windowSize)
            {
                return new RouteCProtocolMessage(
                    RouteCMessageType.RawWelcome,
                    Session,
                    1u,
                    RawUdpProtocolCodec.EncodeWelcome(
                        Nonce,
                        0,
                        windowSize));
            }

            public RouteCProtocolMessage CreateStart(int frameID)
            {
                return new RouteCProtocolMessage(
                    RouteCMessageType.RawStart,
                    Session,
                    1u,
                    RawUdpProtocolCodec.EncodeStart(frameID));
            }

            public RouteCProtocolMessage CreateInput(
                byte playerIndex,
                uint sequence,
                int frameID,
                uint raw)
            {
                byte[] payload = RawUdpProtocolCodec.EncodeInput(
                    playerIndex,
                    1,
                    sequence,
                    frameID,
                    new[] { new RawUdpInputEntry(raw, frameID) });
                return new RouteCProtocolMessage(
                    RouteCMessageType.RawInput,
                    Session,
                    1u,
                    payload);
            }
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

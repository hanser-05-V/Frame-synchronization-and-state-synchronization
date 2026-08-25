using System;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class RawUdpProtocolCodecTests
    {
        [Test]
        public void RawControlPayloads_UseExactLengthsAndBigEndianFields()
        {
            byte[] nonce = CreateNonce();

            byte[] hello = RawUdpProtocolCodec.EncodeHello(nonce);
            Assert.AreEqual(16, hello.Length);
            CollectionAssert.AreEqual(nonce, hello);
            Assert.IsTrue(RawUdpProtocolCodec.TryDecodeHello(
                hello,
                out byte[] decodedNonce));
            CollectionAssert.AreEqual(nonce, decodedNonce);

            byte[] welcome = RawUdpProtocolCodec.EncodeWelcome(
                nonce,
                1,
                RouteCProtocolConstants.RawDefaultWindowSize);
            Assert.AreEqual(18, welcome.Length);
            CollectionAssert.AreEqual(nonce, Slice(welcome, 0, 16));
            Assert.AreEqual(1, welcome[16]);
            Assert.AreEqual(6, welcome[17]);
            Assert.IsTrue(RawUdpProtocolCodec.TryDecodeWelcome(
                welcome,
                out byte[] echoNonce,
                out byte playerIndex,
                out byte windowSize));
            CollectionAssert.AreEqual(nonce, echoNonce);
            Assert.AreEqual(1, playerIndex);
            Assert.AreEqual(6, windowSize);

            byte[] ready = RawUdpProtocolCodec.EncodeReady();
            Assert.AreEqual(0, ready.Length);
            Assert.IsTrue(RawUdpProtocolCodec.TryDecodeReady(ready));

            byte[] start = RawUdpProtocolCodec.EncodeStart(0);
            CollectionAssert.AreEqual(
                new byte[] { 0, 0, 0, 0 },
                start);
            Assert.IsTrue(RawUdpProtocolCodec.TryDecodeStart(
                start,
                out int canonicalStartFrame));
            Assert.AreEqual(0, canonicalStartFrame);

            var expectedFault = new RawUdpFault(
                RawUdpFaultReason.ConflictingInput,
                1,
                0x01020304,
                -2);
            byte[] fault = RawUdpProtocolCodec.EncodeFault(in expectedFault);
            CollectionAssert.AreEqual(
                new byte[]
                {
                    0x00, 0x04, 0x01, 0x00,
                    0x01, 0x02, 0x03, 0x04,
                    0xFF, 0xFF, 0xFF, 0xFE
                },
                fault);
            Assert.IsTrue(RawUdpProtocolCodec.TryDecodeFault(
                fault,
                out RawUdpFault decodedFault));
            Assert.AreEqual(expectedFault.Reason, decodedFault.Reason);
            Assert.AreEqual(expectedFault.PlayerIndex, decodedFault.PlayerIndex);
            Assert.AreEqual(expectedFault.FrameID, decodedFault.FrameID);
            Assert.AreEqual(
                expectedFault.ObservedLatestFrameID,
                decodedFault.ObservedLatestFrameID);
        }

        [Test]
        public void RawInput_N1N6N16_UsesExactMixedEndianBytesAnd48_88_168ByteDatagrams()
        {
            AssertInputLayout(1, 0, 48);
            AssertInputLayout(6, 5, 88);
            AssertInputLayout(16, 15, 168);
        }

        [Test]
        public void RawInput_EarlyFramesUseMinWindowAndEstablishedFramesCannotShortenIt()
        {
            RawUdpInputEntry[] earlyEntries = CreateEntries(0, 2);
            byte[] earlyPayload = RawUdpProtocolCodec.EncodeInput(
                0,
                6,
                10u,
                2,
                earlyEntries);
            Assert.AreEqual(3, earlyPayload[1]);

            RawUdpInputEntry[] fullEntries = CreateEntries(1, 6);
            byte[] fullPayload = RawUdpProtocolCodec.EncodeInput(
                0,
                6,
                11u,
                6,
                fullEntries);
            Assert.AreEqual(6, fullPayload[1]);

            Assert.Throws<ArgumentException>(() =>
                RawUdpProtocolCodec.EncodeInput(
                    0,
                    6,
                    12u,
                    6,
                    CreateEntries(2, 6)));
        }

        [Test]
        public void RawInput_ReturnedEntriesRemainImmutableAfterSourceAndResultMutation()
        {
            RawUdpInputEntry[] source = CreateEntries(0, 5);
            byte[] payload = RawUdpProtocolCodec.EncodeInput(
                1,
                6,
                0x01020304u,
                5,
                source);
            Assert.IsTrue(RawUdpProtocolCodec.TryDecodeInput(
                payload,
                6,
                out RawUdpInputWindow window,
                out RouteCProtocolDropReason reason));
            Assert.AreEqual(RouteCProtocolDropReason.None, reason);

            source[0] = new RawUdpInputEntry(999u, 999);
            payload[12] ^= 0xFF;
            RawUdpInputEntry[] firstRead = window.Entries;
            firstRead[0] = new RawUdpInputEntry(888u, 888);
            RawUdpInputEntry[] secondRead = window.Entries;

            Assert.AreEqual(ExpectedRaw(0), secondRead[0].Raw);
            Assert.AreEqual(0, secondRead[0].FrameID);
            Assert.AreEqual(5, secondRead[5].FrameID);
        }

        [Test]
        public void RawInput_RejectsCountReservedLengthLatestAndNonContiguousFrames()
        {
            byte[] valid = RawUdpProtocolCodec.EncodeInput(
                0,
                6,
                7u,
                5,
                CreateEntries(0, 5));

            AssertInputRejected(Mutate(valid, 1, 0));
            AssertInputRejected(Mutate(valid, 1, 17));
            AssertInputRejected(Mutate(valid, 2, 1));
            AssertInputRejected(Slice(valid, 0, valid.Length - 1));

            byte[] negativeLatest = (byte[])valid.Clone();
            negativeLatest[8] = 0xFF;
            negativeLatest[9] = 0xFF;
            negativeLatest[10] = 0xFF;
            negativeLatest[11] = 0xFF;
            AssertInputRejected(negativeLatest);

            byte[] nonContiguous = (byte[])valid.Clone();
            WriteInt32LittleEndian(nonContiguous, 16, 1);
            WriteInt32LittleEndian(nonContiguous, 24, 3);
            AssertInputRejected(nonContiguous);

            Assert.IsFalse(RawUdpProtocolCodec.TryDecodeInput(
                valid,
                0,
                out RawUdpInputWindow invalidWindow,
                out RouteCProtocolDropReason invalidWindowReason));
            Assert.IsNull(invalidWindow);
            Assert.AreEqual(
                RouteCProtocolDropReason.InvalidRawWindowSize,
                invalidWindowReason);
        }

        [Test]
        public void RawControl_RejectsWrongLengthsPlayerWindowStartReasonAndReserved()
        {
            byte[] nonce = CreateNonce();
            Assert.IsFalse(RawUdpProtocolCodec.TryDecodeHello(
                new byte[15],
                out byte[] badHelloNonce));
            Assert.IsNull(badHelloNonce);

            byte[] welcome = RawUdpProtocolCodec.EncodeWelcome(nonce, 0, 6);
            AssertWelcomeRejected(Slice(welcome, 0, 17));
            AssertWelcomeRejected(Mutate(welcome, 16, 2));
            AssertWelcomeRejected(Mutate(welcome, 17, 0));
            AssertWelcomeRejected(Mutate(welcome, 17, 17));

            Assert.IsFalse(RawUdpProtocolCodec.TryDecodeReady(new byte[1]));
            Assert.IsFalse(RawUdpProtocolCodec.TryDecodeStart(
                new byte[] { 0, 0, 0, 1 },
                out int badStart));
            Assert.AreEqual(0, badStart);

            var faultValue = new RawUdpFault(
                RawUdpFaultReason.ProtocolViolation,
                0,
                -1,
                -1);
            byte[] fault = RawUdpProtocolCodec.EncodeFault(in faultValue);
            AssertFaultRejected(Slice(fault, 0, 11));
            AssertFaultRejected(Mutate(fault, 1, 0));
            AssertFaultRejected(Mutate(fault, 1, 9));
            AssertFaultRejected(Mutate(fault, 2, 2));
            AssertFaultRejected(Mutate(fault, 3, 1));
        }

        [Test]
        public void OuterSessionGenerationAndMessageDirection_RejectionMatrixIsExplicit()
        {
            var session = new RouteCSessionId(1UL, 2UL);
            byte[] nonce = CreateNonce();

            AssertOuterAccepted(
                RouteCMessageType.RawHello,
                RouteCSessionId.Zero,
                0u,
                RawUdpProtocolCodec.EncodeHello(nonce),
                true);
            AssertOuterRejected(
                RouteCMessageType.RawHello,
                RouteCSessionId.Zero,
                0u,
                RawUdpProtocolCodec.EncodeHello(nonce),
                false,
                RouteCProtocolDropReason.WrongMessageDirection);
            AssertOuterRejected(
                RouteCMessageType.RawHello,
                session,
                0u,
                RawUdpProtocolCodec.EncodeHello(nonce),
                true,
                RouteCProtocolDropReason.SessionNotFound);
            AssertOuterRejected(
                RouteCMessageType.RawHello,
                RouteCSessionId.Zero,
                1u,
                RawUdpProtocolCodec.EncodeHello(nonce),
                true,
                RouteCProtocolDropReason.StaleGeneration);

            AssertOuterAccepted(
                RouteCMessageType.RawWelcome,
                session,
                1u,
                RawUdpProtocolCodec.EncodeWelcome(nonce, 0, 6),
                false);
            AssertOuterRejected(
                RouteCMessageType.RawWelcome,
                session,
                1u,
                RawUdpProtocolCodec.EncodeWelcome(nonce, 0, 6),
                true,
                RouteCProtocolDropReason.WrongMessageDirection);
            AssertOuterAccepted(
                RouteCMessageType.RawReady,
                session,
                1u,
                RawUdpProtocolCodec.EncodeReady(),
                true);
            AssertOuterAccepted(
                RouteCMessageType.RawStart,
                session,
                1u,
                RawUdpProtocolCodec.EncodeStart(0),
                false);
            AssertOuterAccepted(
                RouteCMessageType.RawInput,
                session,
                1u,
                RawUdpProtocolCodec.EncodeInput(
                    0,
                    1,
                    0u,
                    0,
                    CreateEntries(0, 0)),
                true);
            AssertOuterAccepted(
                RouteCMessageType.RawInput,
                session,
                1u,
                RawUdpProtocolCodec.EncodeInput(
                    1,
                    1,
                    0u,
                    0,
                    CreateEntries(0, 0)),
                false);

            var matchFull = new RawUdpFault(
                RawUdpFaultReason.MatchFull,
                255,
                -1,
                -1);
            AssertOuterAccepted(
                RouteCMessageType.RawFault,
                RouteCSessionId.Zero,
                0u,
                RawUdpProtocolCodec.EncodeFault(in matchFull),
                false);
            AssertOuterRejected(
                RouteCMessageType.RawFault,
                RouteCSessionId.Zero,
                0u,
                RawUdpProtocolCodec.EncodeFault(in matchFull),
                true,
                RouteCProtocolDropReason.WrongMessageDirection);

            AssertOuterRejected(
                RouteCMessageType.RawReady,
                RouteCSessionId.Zero,
                1u,
                RawUdpProtocolCodec.EncodeReady(),
                true,
                RouteCProtocolDropReason.SessionNotFound);
            AssertOuterRejected(
                RouteCMessageType.RawReady,
                session,
                0u,
                RawUdpProtocolCodec.EncodeReady(),
                true,
                RouteCProtocolDropReason.StaleGeneration);
        }

        [Test]
        public void RawInput_Above168Bytes_IsRawPayloadTooLargeEvenBelowOuter1200Limit()
        {
            byte[] oversizedPayload = new byte[
                RouteCProtocolConstants.RawMaximumInputPayloadSize + 8];
            oversizedPayload[0] = 0;
            oversizedPayload[1] = 17;

            byte[] datagram = RouteCProtocolCodec.Encode(
                RouteCMessageType.RawInput,
                new RouteCSessionId(1UL, 2UL),
                1u,
                oversizedPayload);
            Assert.Less(datagram.Length, RouteCProtocolConstants.MaximumDatagramSize);
            Assert.Greater(
                datagram.Length,
                RouteCProtocolConstants.RawMaximumInputDatagramSize);
            Assert.IsTrue(RouteCProtocolCodec.TryDecode(
                datagram,
                datagram.Length,
                out RouteCProtocolMessage decoded,
                out RouteCProtocolDropReason envelopeReason));
            Assert.AreEqual(RouteCProtocolDropReason.None, envelopeReason);

            Assert.IsFalse(RawUdpProtocolCodec.TryDecodeInput(
                decoded.Payload,
                RouteCProtocolConstants.RawMaximumWindowSize,
                out RawUdpInputWindow window,
                out RouteCProtocolDropReason rawReason));
            Assert.IsNull(window);
            Assert.AreEqual(
                RouteCProtocolDropReason.RawDatagramTooLarge,
                rawReason);
        }

        private static void AssertInputLayout(
            byte inputCount,
            int latestFrameID,
            int expectedDatagramSize)
        {
            RawUdpInputEntry[] entries = CreateEntries(0, latestFrameID);
            byte[] payload = RawUdpProtocolCodec.EncodeInput(
                0,
                inputCount,
                0x01020304u,
                latestFrameID,
                entries);

            Assert.AreEqual(expectedDatagramSize - 28, payload.Length);
            CollectionAssert.AreEqual(
                new byte[]
                {
                    0x00, inputCount, 0x00, 0x00,
                    0x01, 0x02, 0x03, 0x04,
                    (byte)(latestFrameID >> 24),
                    (byte)(latestFrameID >> 16),
                    (byte)(latestFrameID >> 8),
                    (byte)latestFrameID
                },
                Slice(payload, 0, 12));

            for (int index = 0; index < entries.Length; index++)
            {
                byte[] expectedEntry = RouteCProtocolCodec.EncodeBusinessInput(
                    entries[index].Raw,
                    entries[index].FrameID);
                CollectionAssert.AreEqual(
                    expectedEntry,
                    Slice(payload, 12 + (index * 8), 8));
            }

            byte[] datagram = RouteCProtocolCodec.Encode(
                RouteCMessageType.RawInput,
                new RouteCSessionId(1UL, 2UL),
                1u,
                payload);
            Assert.AreEqual(expectedDatagramSize, datagram.Length);
        }

        private static void AssertInputRejected(byte[] payload)
        {
            Assert.IsFalse(RawUdpProtocolCodec.TryDecodeInput(
                payload,
                6,
                out RawUdpInputWindow window,
                out RouteCProtocolDropReason reason));
            Assert.IsNull(window);
            Assert.AreEqual(
                RouteCProtocolDropReason.InvalidRawInputPayload,
                reason);
        }

        private static void AssertWelcomeRejected(byte[] payload)
        {
            Assert.IsFalse(RawUdpProtocolCodec.TryDecodeWelcome(
                payload,
                out byte[] nonce,
                out byte playerIndex,
                out byte windowSize));
            Assert.IsNull(nonce);
            Assert.AreEqual(0, playerIndex);
            Assert.AreEqual(0, windowSize);
        }

        private static void AssertFaultRejected(byte[] payload)
        {
            Assert.IsFalse(RawUdpProtocolCodec.TryDecodeFault(
                payload,
                out RawUdpFault fault));
            Assert.AreEqual(0, (ushort)fault.Reason);
            Assert.AreEqual(0, fault.PlayerIndex);
            Assert.AreEqual(0, fault.FrameID);
            Assert.AreEqual(0, fault.ObservedLatestFrameID);
        }

        private static void AssertOuterAccepted(
            RouteCMessageType messageType,
            RouteCSessionId session,
            uint generation,
            byte[] payload,
            bool receivedByServer)
        {
            var message = new RouteCProtocolMessage(
                messageType,
                session,
                generation,
                payload);
            Assert.IsTrue(RawUdpProtocolCodec.TryValidateOuterMessage(
                message,
                receivedByServer,
                out RouteCProtocolDropReason reason));
            Assert.AreEqual(RouteCProtocolDropReason.None, reason);
        }

        private static void AssertOuterRejected(
            RouteCMessageType messageType,
            RouteCSessionId session,
            uint generation,
            byte[] payload,
            bool receivedByServer,
            RouteCProtocolDropReason expectedReason)
        {
            var message = new RouteCProtocolMessage(
                messageType,
                session,
                generation,
                payload);
            Assert.IsFalse(RawUdpProtocolCodec.TryValidateOuterMessage(
                message,
                receivedByServer,
                out RouteCProtocolDropReason reason));
            Assert.AreEqual(expectedReason, reason);
        }

        private static RawUdpInputEntry[] CreateEntries(
            int firstFrameID,
            int latestFrameID)
        {
            var entries = new RawUdpInputEntry[
                latestFrameID - firstFrameID + 1];
            for (int index = 0; index < entries.Length; index++)
            {
                int frameID = firstFrameID + index;
                entries[index] = new RawUdpInputEntry(
                    ExpectedRaw(frameID),
                    frameID);
            }

            return entries;
        }

        private static uint ExpectedRaw(int frameID)
        {
            return 0x11223300u + (uint)frameID;
        }

        private static byte[] CreateNonce()
        {
            var nonce = new byte[16];
            for (byte index = 0; index < nonce.Length; index++)
                nonce[index] = index;
            return nonce;
        }

        private static byte[] Mutate(byte[] source, int index, byte value)
        {
            byte[] copy = (byte[])source.Clone();
            copy[index] = value;
            return copy;
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var copy = new byte[count];
            Buffer.BlockCopy(source, offset, copy, 0, count);
            return copy;
        }

        private static void WriteInt32LittleEndian(
            byte[] destination,
            int offset,
            int value)
        {
            uint raw = unchecked((uint)value);
            destination[offset] = (byte)raw;
            destination[offset + 1] = (byte)(raw >> 8);
            destination[offset + 2] = (byte)(raw >> 16);
            destination[offset + 3] = (byte)(raw >> 24);
        }
    }
}

using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class RouteCProtocolCodecTests
    {
        [Test]
        public void Envelope_RoundTrip_UsesExactTwentyEightByteHeader()
        {
            var session = new RouteCSessionId(
                0x0102030405060708UL,
                0x1112131415161718UL);
            byte[] payload = { 0xAA, 0xBB };

            byte[] datagram = RouteCProtocolCodec.Encode(
                RouteCMessageType.Heartbeat,
                session,
                0x21222324u,
                payload);

            CollectionAssert.AreEqual(
                new byte[]
                {
                    0x52, 0x43, 0x46, 0x32,
                    0x01, 0x06, 0x00, 0x02,
                    0x01, 0x02, 0x03, 0x04,
                    0x05, 0x06, 0x07, 0x08,
                    0x11, 0x12, 0x13, 0x14,
                    0x15, 0x16, 0x17, 0x18,
                    0x21, 0x22, 0x23, 0x24,
                    0xAA, 0xBB
                },
                datagram);
            Assert.IsTrue(RouteCProtocolCodec.TryDecode(
                datagram,
                datagram.Length,
                out RouteCProtocolMessage decoded,
                out RouteCProtocolDropReason reason));
            Assert.AreEqual(RouteCProtocolDropReason.None, reason);
            Assert.AreEqual(RouteCMessageType.Heartbeat, decoded.MessageType);
            Assert.AreEqual(session, decoded.SessionId);
            Assert.AreEqual(0x21222324u, decoded.Generation);
            CollectionAssert.AreEqual(payload, decoded.Payload);
        }

        [Test]
        public void TryDecode_ReusedSocketBufferAndReturnedPayloadMutate_MessageStaysImmutable()
        {
            byte[] datagram = RouteCProtocolCodec.Encode(
                RouteCMessageType.Heartbeat,
                RouteCSessionId.Zero,
                1u,
                new byte[] { 0x10, 0x20 });
            Assert.IsTrue(RouteCProtocolCodec.TryDecode(
                datagram,
                datagram.Length,
                out RouteCProtocolMessage decoded,
                out RouteCProtocolDropReason reason));
            Assert.AreEqual(RouteCProtocolDropReason.None, reason);

            datagram[RouteCProtocolConstants.HeaderSize] = 0xEE;
            byte[] returnedPayload = decoded.Payload;
            returnedPayload[1] = 0xFF;

            CollectionAssert.AreEqual(
                new byte[] { 0x10, 0x20 },
                decoded.Payload);
        }

        [Test]
        public void TryDecode_HeaderShorterThanTwentyEightBytes_ReturnsHeaderTooShort()
        {
            AssertDropped(
                new byte[RouteCProtocolConstants.HeaderSize - 1],
                RouteCProtocolConstants.HeaderSize - 1,
                RouteCProtocolDropReason.HeaderTooShort);
        }

        [Test]
        public void TryDecode_CountBeyondSocketBuffer_ReturnsDatagramLengthOutOfRange()
        {
            byte[] datagram = CreateValidDatagram();

            AssertDropped(
                datagram,
                datagram.Length + 1,
                RouteCProtocolDropReason.DatagramLengthOutOfRange);
        }

        [Test]
        public void TryDecode_NegativeCount_ReturnsDatagramLengthOutOfRange()
        {
            AssertDropped(
                CreateValidDatagram(),
                -1,
                RouteCProtocolDropReason.DatagramLengthOutOfRange);
        }

        [Test]
        public void TryDecode_DatagramAboveTwelveHundredBytes_ReturnsDatagramTooLarge()
        {
            var datagram = new byte[RouteCProtocolConstants.MaximumDatagramSize + 1];

            AssertDropped(
                datagram,
                datagram.Length,
                RouteCProtocolDropReason.DatagramTooLarge);
        }

        [Test]
        public void TryDecode_BadMagic_ReturnsInvalidMagic()
        {
            byte[] datagram = CreateValidDatagram();
            datagram[0] = 0x00;

            AssertDropped(
                datagram,
                datagram.Length,
                RouteCProtocolDropReason.InvalidMagic);
        }

        [Test]
        public void TryDecode_UnknownVersion_ReturnsUnsupportedVersion()
        {
            byte[] datagram = CreateValidDatagram();
            datagram[4] = 0xFF;

            AssertDropped(
                datagram,
                datagram.Length,
                RouteCProtocolDropReason.UnsupportedVersion);
        }

        [Test]
        public void TryDecode_UnknownMessageType_ReturnsUnknownMessageType()
        {
            byte[] datagram = CreateValidDatagram();
            datagram[5] = 0xFF;

            AssertDropped(
                datagram,
                datagram.Length,
                RouteCProtocolDropReason.UnknownMessageType);
        }

        [Test]
        public void TryDecode_DeclaredPayloadAboveMaximum_ReturnsPayloadTooLarge()
        {
            byte[] datagram = CreateValidDatagram();
            datagram[6] = 0x04;
            datagram[7] = 0x95;

            AssertDropped(
                datagram,
                datagram.Length,
                RouteCProtocolDropReason.PayloadTooLarge);
        }

        [Test]
        public void TryDecode_DeclaredPayloadDoesNotMatchDatagram_ReturnsPayloadLengthMismatch()
        {
            byte[] datagram = RouteCProtocolCodec.Encode(
                RouteCMessageType.Heartbeat,
                RouteCSessionId.Zero,
                0u,
                new byte[] { 0x01, 0x02 });

            AssertDropped(
                datagram,
                datagram.Length - 1,
                RouteCProtocolDropReason.PayloadLengthMismatch);
        }

        [Test]
        public void TryDecode_KcpPayloadShorterThanHeader_ReturnsKcpPayloadTooShort()
        {
            byte[] datagram = RouteCProtocolCodec.Encode(
                RouteCMessageType.Heartbeat,
                RouteCSessionId.Zero,
                1u,
                new byte[RouteCProtocolConstants.KcpHeaderSize - 1]);
            datagram[5] = (byte)RouteCMessageType.KcpData;

            AssertDropped(
                datagram,
                datagram.Length,
                RouteCProtocolDropReason.KcpPayloadTooShort);
        }

        [Test]
        public void Encode_KcpPayloadShorterThanHeader_ThrowsArgumentException()
        {
            Assert.Throws<System.ArgumentException>(() =>
                RouteCProtocolCodec.Encode(
                    RouteCMessageType.KcpData,
                    RouteCSessionId.Zero,
                    1u,
                    new byte[RouteCProtocolConstants.KcpHeaderSize - 1]));
        }

        [Test]
        public void Encode_PayloadAboveMaximum_ThrowsArgumentOutOfRangeException()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                RouteCProtocolCodec.Encode(
                    RouteCMessageType.Heartbeat,
                    RouteCSessionId.Zero,
                    0u,
                    new byte[RouteCProtocolConstants.KcpMtu + 1]));
        }

        [Test]
        public void Encode_UnknownMessageType_ThrowsArgumentOutOfRangeException()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                RouteCProtocolCodec.Encode(
                    (RouteCMessageType)0xFF,
                    RouteCSessionId.Zero,
                    0u,
                    new byte[0]));
        }

        [Test]
        public void Envelope_MaximumIntegerFields_RoundTripWithoutSignLoss()
        {
            var session = new RouteCSessionId(ulong.MaxValue, ulong.MaxValue);
            byte[] datagram = RouteCProtocolCodec.Encode(
                RouteCMessageType.Heartbeat,
                session,
                uint.MaxValue,
                new byte[0]);

            Assert.IsTrue(RouteCProtocolCodec.TryDecode(
                datagram,
                datagram.Length,
                out RouteCProtocolMessage decoded,
                out RouteCProtocolDropReason reason));
            Assert.AreEqual(RouteCProtocolDropReason.None, reason);
            Assert.AreEqual(session, decoded.SessionId);
            Assert.AreEqual(uint.MaxValue, decoded.Generation);
        }

        [Test]
        public void SessionId_ZeroEqualityAndHashCode_UseBothHalves()
        {
            var left = new RouteCSessionId(1UL, 2UL);
            var equal = new RouteCSessionId(1UL, 2UL);
            var differentHigh = new RouteCSessionId(3UL, 2UL);
            var differentLow = new RouteCSessionId(1UL, 4UL);

            Assert.IsTrue(RouteCSessionId.Zero.IsZero);
            Assert.IsFalse(left.IsZero);
            Assert.AreEqual(left, equal);
            Assert.AreEqual(left.GetHashCode(), equal.GetHashCode());
            Assert.AreNotEqual(left, differentHigh);
            Assert.AreNotEqual(left, differentLow);
        }

        [Test]
        public void TransportAndSessionEnums_UseLockedProtocolValues()
        {
            Assert.AreEqual(0, (byte)NetworkTransportKind.KcpUdp);
            Assert.AreEqual(1, (byte)NetworkTransportKind.Tcp);
            object rawUdp = System.Enum.Parse(typeof(NetworkTransportKind), "RawUdp");
            Assert.AreEqual(2, (byte)(NetworkTransportKind)rawUdp);
            Assert.AreEqual(0, (byte)NetworkSessionState.Disconnected);
            Assert.AreEqual(1, (byte)NetworkSessionState.Handshaking);
            Assert.AreEqual(2, (byte)NetworkSessionState.AwaitingReady);
            Assert.AreEqual(3, (byte)NetworkSessionState.Running);
            Assert.AreEqual(4, (byte)NetworkSessionState.Reconnecting);
            Assert.AreEqual(5, (byte)NetworkSessionState.Resuming);
            Assert.AreEqual(6, (byte)NetworkSessionState.Terminated);
        }

        [Test]
        public void Envelope_RawMessageBytes13Through18_AreKnownAndByte19IsRejected()
        {
            byte[] datagram = RouteCProtocolCodec.Encode(
                RouteCMessageType.Heartbeat,
                RouteCSessionId.Zero,
                0u,
                new byte[0]);

            for (byte messageValue = 13; messageValue <= 18; messageValue++)
            {
                datagram[5] = messageValue;

                Assert.IsTrue(
                    RouteCProtocolCodec.TryDecode(
                        datagram,
                        datagram.Length,
                        out RouteCProtocolMessage decoded,
                        out RouteCProtocolDropReason reason),
                    "Raw message value should be known: " + messageValue);
                Assert.AreEqual(RouteCProtocolDropReason.None, reason);
                Assert.AreEqual(messageValue, (byte)decoded.MessageType);
            }

            datagram[5] = 19;

            Assert.IsFalse(RouteCProtocolCodec.TryDecode(
                datagram,
                datagram.Length,
                out _,
                out RouteCProtocolDropReason unknownReason));
            Assert.AreEqual(RouteCProtocolDropReason.UnknownMessageType, unknownReason);
        }

        [Test]
        public void BusinessInput_RoundTrip_PreservesExistingLittleEndianLayout()
        {
            byte[] bytes = RouteCProtocolCodec.EncodeBusinessInput(
                0x12345678u,
                42);

            CollectionAssert.AreEqual(
                new byte[] { 0x78, 0x56, 0x34, 0x12, 42, 0, 0, 0 },
                bytes);
            Assert.IsTrue(RouteCProtocolCodec.TryDecodeBusinessInput(
                bytes,
                out uint raw,
                out int frameID));
            Assert.AreEqual(0x12345678u, raw);
            Assert.AreEqual(42, frameID);
        }

        [Test]
        public void BusinessInput_MinimumSignedFrame_RoundTripsWithoutSignLoss()
        {
            byte[] bytes = RouteCProtocolCodec.EncodeBusinessInput(
                uint.MaxValue,
                int.MinValue);

            Assert.IsTrue(RouteCProtocolCodec.TryDecodeBusinessInput(
                bytes,
                out uint raw,
                out int frameID));
            Assert.AreEqual(uint.MaxValue, raw);
            Assert.AreEqual(int.MinValue, frameID);
        }

        [TestCase(0)]
        [TestCase(7)]
        [TestCase(9)]
        public void TryDecodeBusinessInput_NonEightBytePayload_ReturnsFalse(
            int length)
        {
            Assert.IsFalse(RouteCProtocolCodec.TryDecodeBusinessInput(
                new byte[length],
                out uint raw,
                out int frameID));
            Assert.AreEqual(0u, raw);
            Assert.AreEqual(0, frameID);
        }

        [Test]
        public void TryReadConversation_LittleEndianHeader_ReturnsConversation()
        {
            var payload = new byte[RouteCProtocolConstants.KcpHeaderSize];
            payload[0] = 0x78;
            payload[1] = 0x56;
            payload[2] = 0x34;
            payload[3] = 0x12;

            Assert.IsTrue(RouteCKcpHeader.TryReadConversation(
                payload,
                out uint conversation));
            Assert.AreEqual(0x12345678u, conversation);
        }

        [Test]
        public void TryReadConversation_MaximumConversation_RoundTrips()
        {
            var payload = new byte[RouteCProtocolConstants.KcpHeaderSize];
            payload[0] = 0xFF;
            payload[1] = 0xFF;
            payload[2] = 0xFF;
            payload[3] = 0xFF;

            Assert.IsTrue(RouteCKcpHeader.TryReadConversation(
                payload,
                out uint conversation));
            Assert.AreEqual(uint.MaxValue, conversation);
        }

        [Test]
        public void TryReadConversation_PayloadShorterThanKcpHeader_ReturnsFalse()
        {
            Assert.IsFalse(RouteCKcpHeader.TryReadConversation(
                new byte[RouteCProtocolConstants.KcpHeaderSize - 1],
                out uint conversation));
            Assert.AreEqual(0u, conversation);
        }

        [Test]
        public void TryReadConversation_TrailingSessionLikeBytes_DoNotAffectConversation()
        {
            var payload = new byte[RouteCProtocolConstants.KcpHeaderSize];
            payload[0] = 0x04;
            for (int index = 8; index < payload.Length; index++)
                payload[index] = 0xFF;

            Assert.IsTrue(RouteCKcpHeader.TryReadConversation(
                payload,
                out uint conversation));
            Assert.AreEqual(4u, conversation);
        }

        private static byte[] CreateValidDatagram()
        {
            return RouteCProtocolCodec.Encode(
                RouteCMessageType.Heartbeat,
                RouteCSessionId.Zero,
                0u,
                new byte[0]);
        }

        private static void AssertDropped(
            byte[] datagram,
            int count,
            RouteCProtocolDropReason expectedReason)
        {
            bool succeeded = RouteCProtocolCodec.TryDecode(
                datagram,
                count,
                out RouteCProtocolMessage message,
                out RouteCProtocolDropReason reason);

            Assert.IsFalse(succeeded);
            Assert.IsNull(message);
            Assert.AreEqual(expectedReason, reason);
        }
    }
}

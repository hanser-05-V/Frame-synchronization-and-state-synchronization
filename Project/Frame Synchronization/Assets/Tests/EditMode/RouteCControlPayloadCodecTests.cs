using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class RouteCControlPayloadCodecTests
    {
        [Test]
        public void HelloPayloads_UseKindNonceAndTokenLayout()
        {
            byte[] nonce = CreateSequence(0x10, 16);
            byte[] token = CreateSequence(0x40, 32);

            byte[] initial = RouteCProtocolCodec.EncodeInitialHello(nonce);
            Assert.AreEqual(17, initial.Length);
            Assert.AreEqual(0, initial[0]);
            CollectionAssert.AreEqual(nonce, Slice(initial, 1, 16));
            Assert.IsTrue(RouteCProtocolCodec.TryDecodeInitialHello(
                initial,
                out byte[] decodedInitialNonce));
            CollectionAssert.AreEqual(nonce, decodedInitialNonce);

            byte[] reconnect = RouteCProtocolCodec.EncodeReconnectHello(
                nonce,
                token);
            Assert.AreEqual(49, reconnect.Length);
            Assert.AreEqual(1, reconnect[0]);
            CollectionAssert.AreEqual(nonce, Slice(reconnect, 1, 16));
            CollectionAssert.AreEqual(token, Slice(reconnect, 17, 32));
            Assert.IsTrue(RouteCProtocolCodec.TryDecodeReconnectHello(
                reconnect,
                out byte[] decodedReconnectNonce,
                out byte[] decodedToken));
            CollectionAssert.AreEqual(nonce, decodedReconnectNonce);
            CollectionAssert.AreEqual(token, decodedToken);
        }

        [Test]
        public void Welcome_RoundTrip_UsesExactNetworkByteOrderAndCopiesSecrets()
        {
            byte[] nonce = CreateSequence(0x00, 16);
            byte[] token = CreateSequence(0x20, 32);

            byte[] payload = RouteCProtocolCodec.EncodeWelcome(
                nonce,
                1,
                0x01020304u,
                token,
                1000,
                3000,
                true);

            Assert.AreEqual(62, payload.Length);
            CollectionAssert.AreEqual(nonce, Slice(payload, 0, 16));
            Assert.AreEqual(1, payload[16]);
            CollectionAssert.AreEqual(
                new byte[] { 0x01, 0x02, 0x03, 0x04 },
                Slice(payload, 17, 4));
            CollectionAssert.AreEqual(token, Slice(payload, 21, 32));
            CollectionAssert.AreEqual(
                new byte[] { 0x00, 0x00, 0x03, 0xE8 },
                Slice(payload, 53, 4));
            CollectionAssert.AreEqual(
                new byte[] { 0x00, 0x00, 0x0B, 0xB8 },
                Slice(payload, 57, 4));
            Assert.AreEqual(1, payload[61]);
            Assert.IsTrue(RouteCProtocolCodec.TryDecodeWelcome(
                payload,
                out byte[] decodedNonce,
                out byte playerIndex,
                out uint conversation,
                out byte[] decodedToken,
                out int heartbeatMs,
                out int timeoutMs,
                out bool resumeRequired));

            payload[0] = 0xFF;
            payload[21] = 0xFF;
            Assert.AreEqual(1, playerIndex);
            Assert.AreEqual(0x01020304u, conversation);
            Assert.AreEqual(1000, heartbeatMs);
            Assert.AreEqual(3000, timeoutMs);
            Assert.IsTrue(resumeRequired);
            CollectionAssert.AreEqual(nonce, decodedNonce);
            CollectionAssert.AreEqual(token, decodedToken);
        }

        [Test]
        public void ReadyAndStart_RoundTripSignedFieldsInNetworkByteOrder()
        {
            byte[] ready = RouteCProtocolCodec.EncodeReady(
                -1,
                0x01020304,
                0x11223344);
            CollectionAssert.AreEqual(
                new byte[]
                {
                    0xFF, 0xFF, 0xFF, 0xFF,
                    0x01, 0x02, 0x03, 0x04,
                    0x11, 0x22, 0x33, 0x44
                },
                ready);
            Assert.IsTrue(RouteCProtocolCodec.TryDecodeReady(
                ready,
                out int lastContiguousRemoteFrameID,
                out int earliestRecoverableCanonicalFrame,
                out int latestLocalFrameID));
            Assert.AreEqual(-1, lastContiguousRemoteFrameID);
            Assert.AreEqual(0x01020304, earliestRecoverableCanonicalFrame);
            Assert.AreEqual(0x11223344, latestLocalFrameID);

            byte[] start = RouteCProtocolCodec.EncodeStart(0x12345678);
            CollectionAssert.AreEqual(
                new byte[] { 0x12, 0x34, 0x56, 0x78 },
                start);
            Assert.IsTrue(RouteCProtocolCodec.TryDecodeStart(
                start,
                out int canonicalStartFrame));
            Assert.AreEqual(0x12345678, canonicalStartFrame);
        }

        [Test]
        public void ResumePayloads_RoundTripAttemptAndFrozenBounds()
        {
            byte[] attemptID = CreateSequence(0x70, 16);

            byte[] probe = RouteCProtocolCodec.EncodeResumeProbe(attemptID);
            Assert.AreEqual(16, probe.Length);
            Assert.IsTrue(RouteCProtocolCodec.TryDecodeResumeProbe(
                probe,
                out byte[] decodedProbeID));
            CollectionAssert.AreEqual(attemptID, decodedProbeID);

            byte[] state = RouteCProtocolCodec.EncodeResumeState(
                attemptID,
                110,
                100,
                120);
            Assert.AreEqual(28, state.Length);
            CollectionAssert.AreEqual(
                new byte[]
                {
                    0x00, 0x00, 0x00, 0x6E,
                    0x00, 0x00, 0x00, 0x64,
                    0x00, 0x00, 0x00, 0x78
                },
                Slice(state, 16, 12));
            Assert.IsTrue(RouteCProtocolCodec.TryDecodeResumeState(
                state,
                out byte[] decodedStateID,
                out int stateRemote,
                out int stateEarliest,
                out int stateLocal));
            CollectionAssert.AreEqual(attemptID, decodedStateID);
            Assert.AreEqual(110, stateRemote);
            Assert.AreEqual(100, stateEarliest);
            Assert.AreEqual(120, stateLocal);

            byte[] accepted = RouteCProtocolCodec.EncodeResumeAccepted(
                attemptID,
                111,
                120,
                111,
                123,
                124,
                123);
            Assert.AreEqual(40, accepted.Length);
            CollectionAssert.AreEqual(
                new byte[]
                {
                    0x00, 0x00, 0x00, 0x6F,
                    0x00, 0x00, 0x00, 0x78,
                    0x00, 0x00, 0x00, 0x6F,
                    0x00, 0x00, 0x00, 0x7B,
                    0x00, 0x00, 0x00, 0x7C,
                    0x00, 0x00, 0x00, 0x7B
                },
                Slice(accepted, 16, 24));
            Assert.IsTrue(RouteCProtocolCodec.TryDecodeResumeAccepted(
                accepted,
                out byte[] decodedAcceptedID,
                out int uploadFrom,
                out int uploadThrough,
                out int replayFrom,
                out int replayThrough,
                out int peerFrom,
                out int peerThrough));
            CollectionAssert.AreEqual(attemptID, decodedAcceptedID);
            Assert.AreEqual(111, uploadFrom);
            Assert.AreEqual(120, uploadThrough);
            Assert.AreEqual(111, replayFrom);
            Assert.AreEqual(123, replayThrough);
            Assert.AreEqual(124, peerFrom);
            Assert.AreEqual(123, peerThrough);

            byte[] complete = RouteCProtocolCodec.EncodeResumeComplete(
                attemptID,
                120,
                123);
            Assert.AreEqual(24, complete.Length);
            CollectionAssert.AreEqual(
                new byte[]
                {
                    0x00, 0x00, 0x00, 0x78,
                    0x00, 0x00, 0x00, 0x7B
                },
                Slice(complete, 16, 8));
            Assert.IsTrue(RouteCProtocolCodec.TryDecodeResumeComplete(
                complete,
                out byte[] decodedCompleteID,
                out int uploadedLocalThrough,
                out int receivedRemoteThrough));
            CollectionAssert.AreEqual(attemptID, decodedCompleteID);
            Assert.AreEqual(120, uploadedLocalThrough);
            Assert.AreEqual(123, receivedRemoteThrough);

            byte[] rejected = RouteCProtocolCodec.EncodeResumeRejected(
                attemptID,
                0x1234);
            Assert.AreEqual(18, rejected.Length);
            Assert.AreEqual(0x12, rejected[16]);
            Assert.AreEqual(0x34, rejected[17]);
            Assert.IsTrue(RouteCProtocolCodec.TryDecodeResumeRejected(
                rejected,
                out byte[] decodedRejectedID,
                out ushort reason));
            CollectionAssert.AreEqual(attemptID, decodedRejectedID);
            Assert.AreEqual(0x1234, reason);
        }

        [Test]
        public void ControlPayloadDecoders_WrongExactLength_ReturnFalse()
        {
            Assert.IsFalse(RouteCProtocolCodec.TryDecodeInitialHello(
                new byte[16],
                out byte[] nonce));
            Assert.IsFalse(RouteCProtocolCodec.TryDecodeReconnectHello(
                new byte[48],
                out nonce,
                out byte[] token));
            Assert.IsFalse(RouteCProtocolCodec.TryDecodeWelcome(
                new byte[61],
                out nonce,
                out byte playerIndex,
                out uint conversation,
                out token,
                out int heartbeatMs,
                out int timeoutMs,
                out bool resumeRequired));
            Assert.IsFalse(RouteCProtocolCodec.TryDecodeReady(
                new byte[11],
                out int remote,
                out int earliest,
                out int local));
            Assert.IsFalse(RouteCProtocolCodec.TryDecodeStart(
                new byte[3],
                out int start));
            Assert.IsFalse(RouteCProtocolCodec.TryDecodeResumeProbe(
                new byte[15],
                out byte[] attemptID));
            Assert.IsFalse(RouteCProtocolCodec.TryDecodeResumeState(
                new byte[27],
                out attemptID,
                out remote,
                out earliest,
                out local));
            Assert.IsFalse(RouteCProtocolCodec.TryDecodeResumeAccepted(
                new byte[39],
                out attemptID,
                out int uploadFrom,
                out int uploadThrough,
                out int replayFrom,
                out int replayThrough,
                out int peerFrom,
                out int peerThrough));
            Assert.IsFalse(RouteCProtocolCodec.TryDecodeResumeComplete(
                new byte[23],
                out attemptID,
                out int uploaded,
                out int received));
            Assert.IsFalse(RouteCProtocolCodec.TryDecodeResumeRejected(
                new byte[17],
                out attemptID,
                out ushort reason));
        }

        [Test]
        public void HelloPayloadDecoders_WrongKind_ReturnFalse()
        {
            var initial = new byte[17];
            initial[0] = 1;
            Assert.IsFalse(RouteCProtocolCodec.TryDecodeInitialHello(
                initial,
                out byte[] nonce));

            var reconnect = new byte[49];
            reconnect[0] = 0;
            Assert.IsFalse(RouteCProtocolCodec.TryDecodeReconnectHello(
                reconnect,
                out nonce,
                out byte[] token));
        }

        [Test]
        public void TryDecodeWelcome_InvalidResumeRequiredByte_ReturnsFalse()
        {
            var payload = new byte[62];
            payload[61] = 2;

            Assert.IsFalse(RouteCProtocolCodec.TryDecodeWelcome(
                payload,
                out byte[] nonce,
                out byte playerIndex,
                out uint conversation,
                out byte[] token,
                out int heartbeatMs,
                out int timeoutMs,
                out bool resumeRequired));
        }

        private static byte[] CreateSequence(int first, int count)
        {
            var result = new byte[count];
            for (int index = 0; index < count; index++)
                result[index] = (byte)(first + index);
            return result;
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var result = new byte[count];
            System.Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }
    }
}

using System;
using FrameSyncDemo;

namespace FrameSyncServer
{
    public sealed class ServerResumeAttempt
    {
        private readonly byte[] _attemptID;
        private readonly KcpServerDatagram _probeDatagram;
        private ResumeReadiness? _peerReadiness;
        private KcpServerDatagram[] _acceptedDatagrams;
        private byte[] _acceptedPayload;
        private byte[][] _replayPayloads;
        private int _nextReplayPayloadIndex;
        private readonly bool[] _completedPlayers = new bool[2];
        private byte[][][] _tailPayloadsByReceiver;
        private readonly int[] _nextTailPayloadIndex = new int[2];
        private KcpServerDatagram[] _completeDatagrams;

        public ServerResumeAttempt(
            byte[] attemptID,
            byte reconnectingPlayerIndex,
            ResumeReadiness reconnectingReadiness,
            KcpServerDatagram probeDatagram,
            uint probeSentAt)
        {
            if (attemptID == null ||
                attemptID.Length != RouteCProtocolConstants.NonceSize)
            {
                throw new ArgumentException(
                    "Resume AttemptID has the wrong length.",
                    nameof(attemptID));
            }
            if (reconnectingPlayerIndex > 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(reconnectingPlayerIndex));
            }

            _attemptID = (byte[])attemptID.Clone();
            _probeDatagram = probeDatagram ??
                throw new ArgumentNullException(nameof(probeDatagram));
            ReconnectingPlayerIndex = reconnectingPlayerIndex;
            ReconnectingReadiness = reconnectingReadiness;
            LastProbeSentAt = probeSentAt;
        }

        public byte ReconnectingPlayerIndex { get; }

        public byte PeerPlayerIndex => (byte)(1 - ReconnectingPlayerIndex);

        public ResumeReadiness ReconnectingReadiness { get; }

        public bool HasPeerReadiness => _peerReadiness.HasValue;

        public bool IsFrozen => _acceptedDatagrams != null;

        public bool IsCompleted => _completeDatagrams != null;

        public ResumeReadiness PeerReadiness => _peerReadiness.Value;

        public uint LastProbeSentAt { get; private set; }

        public int UploadFrom { get; private set; }

        public int UploadThrough { get; private set; }

        public int ReplayFrom { get; private set; }

        public int ReplayThrough { get; private set; }

        public int PeerFrom { get; private set; }

        public int PeerThrough { get; private set; }

        public int PendingReplayCount => _replayPayloads == null
            ? 0
            : _replayPayloads.Length - _nextReplayPayloadIndex;

        public bool IsTailReleaseStarted => _tailPayloadsByReceiver != null;

        public int PendingTailCount => !IsTailReleaseStarted
            ? 0
            : PendingTailCountFor(0) + PendingTailCountFor(1);

        public bool BothPlayersCompleted =>
            _completedPlayers[0] && _completedPlayers[1];

        public byte[] AcceptedPayload => _acceptedPayload == null
            ? null
            : (byte[])_acceptedPayload.Clone();

        public bool MatchesAttemptID(byte[] attemptID)
        {
            return BytesEqual(_attemptID, attemptID);
        }

        public bool MatchesReconnectReadiness(
            byte playerIndex,
            ResumeReadiness readiness)
        {
            return playerIndex == ReconnectingPlayerIndex &&
                   ReadinessEqual(ReconnectingReadiness, readiness);
        }

        public bool MatchesReadiness(
            byte playerIndex,
            ResumeReadiness readiness)
        {
            if (playerIndex == ReconnectingPlayerIndex)
                return ReadinessEqual(ReconnectingReadiness, readiness);
            if (playerIndex == PeerPlayerIndex && _peerReadiness.HasValue)
                return ReadinessEqual(_peerReadiness.Value, readiness);
            return false;
        }

        public bool TrySetPeerReadiness(ResumeReadiness readiness)
        {
            if (!_peerReadiness.HasValue)
            {
                _peerReadiness = readiness;
                return true;
            }
            return ReadinessEqual(_peerReadiness.Value, readiness);
        }

        public byte[] CopyAttemptID()
        {
            return (byte[])_attemptID.Clone();
        }

        public KcpServerDatagram CopyProbeDatagram()
        {
            return new KcpServerDatagram(
                _probeDatagram.Endpoint,
                _probeDatagram.Datagram);
        }

        public bool IsProbeRetryDue(uint nowMs)
        {
            return !HasPeerReadiness &&
                   unchecked(nowMs - LastProbeSentAt) >=
                   RouteCProtocolConstants.HandshakeRetryMs;
        }

        public void MarkProbeSent(uint nowMs)
        {
            LastProbeSentAt = nowMs;
        }

        public void Freeze(
            byte[] acceptedPayload,
            KcpServerDatagram reconnectingDatagram,
            KcpServerDatagram peerDatagram,
            int uploadFrom,
            int uploadThrough,
            int replayFrom,
            int replayThrough,
            int peerFrom,
            int peerThrough,
            byte[][] replayPayloads)
        {
            if (IsFrozen)
                throw new InvalidOperationException("Resume attempt is already frozen.");
            if (acceptedPayload == null)
                throw new ArgumentNullException(nameof(acceptedPayload));
            if (reconnectingDatagram == null)
            {
                throw new ArgumentNullException(
                    nameof(reconnectingDatagram));
            }
            if (peerDatagram == null)
                throw new ArgumentNullException(nameof(peerDatagram));
            if (replayPayloads == null)
                throw new ArgumentNullException(nameof(replayPayloads));

            _acceptedPayload = (byte[])acceptedPayload.Clone();
            _acceptedDatagrams = new[]
            {
                reconnectingDatagram,
                peerDatagram
            };
            UploadFrom = uploadFrom;
            UploadThrough = uploadThrough;
            ReplayFrom = replayFrom;
            ReplayThrough = replayThrough;
            PeerFrom = peerFrom;
            PeerThrough = peerThrough;
            _replayPayloads = CopyPayloads(replayPayloads);
        }

        public bool TryTakeNextReplayPayload(out byte[] payload)
        {
            if (!IsFrozen || PendingReplayCount == 0)
            {
                payload = null;
                return false;
            }

            byte[] source = _replayPayloads[_nextReplayPayloadIndex++];
            payload = new byte[source.Length];
            Buffer.BlockCopy(source, 0, payload, 0, source.Length);
            return true;
        }

        public bool TryCompletePlayer(
            byte playerIndex,
            int uploadedLocalThrough,
            int receivedRemoteThrough)
        {
            if (!IsFrozen || playerIndex > 1)
                return false;

            int requiredUploaded = playerIndex == ReconnectingPlayerIndex
                ? UploadThrough
                : ReplayThrough;
            int requiredReceived = playerIndex == ReconnectingPlayerIndex
                ? ReplayThrough
                : PeerThrough;
            if (uploadedLocalThrough < requiredUploaded ||
                receivedRemoteThrough < requiredReceived)
            {
                return false;
            }

            _completedPlayers[playerIndex] = true;
            return true;
        }

        public void BeginTailRelease(byte[][][] payloadsByReceiver)
        {
            if (!BothPlayersCompleted || IsTailReleaseStarted ||
                payloadsByReceiver == null || payloadsByReceiver.Length != 2)
            {
                throw new InvalidOperationException(
                    "Resume tail release is not ready.");
            }

            _tailPayloadsByReceiver = new byte[2][][];
            for (int receiverIndex = 0; receiverIndex < 2; receiverIndex++)
            {
                if (payloadsByReceiver[receiverIndex] == null)
                {
                    throw new ArgumentException(
                        "Resume tail payloads cannot be null.",
                        nameof(payloadsByReceiver));
                }
                _tailPayloadsByReceiver[receiverIndex] =
                    CopyPayloads(payloadsByReceiver[receiverIndex]);
            }
        }

        public bool TryTakeNextTailPayload(
            byte receiverIndex,
            out byte[] payload)
        {
            if (receiverIndex > 1 || PendingTailCountFor(receiverIndex) == 0)
            {
                payload = null;
                return false;
            }

            byte[] source = _tailPayloadsByReceiver[receiverIndex]
                [_nextTailPayloadIndex[receiverIndex]++];
            payload = new byte[source.Length];
            Buffer.BlockCopy(source, 0, payload, 0, source.Length);
            return true;
        }

        public void Complete(
            KcpServerDatagram reconnectingDatagram,
            KcpServerDatagram peerDatagram)
        {
            if (!BothPlayersCompleted ||
                !IsTailReleaseStarted ||
                PendingTailCount != 0 ||
                IsCompleted)
            {
                throw new InvalidOperationException(
                    "Resume completion is not ready.");
            }

            _completeDatagrams = new[]
            {
                reconnectingDatagram ?? throw new ArgumentNullException(
                    nameof(reconnectingDatagram)),
                peerDatagram ?? throw new ArgumentNullException(
                    nameof(peerDatagram))
            };
        }

        public KcpServerDatagram[] CopyAcceptedDatagrams()
        {
            if (!IsFrozen)
                return Array.Empty<KcpServerDatagram>();

            var copies = new KcpServerDatagram[_acceptedDatagrams.Length];
            for (int index = 0; index < copies.Length; index++)
            {
                copies[index] = new KcpServerDatagram(
                    _acceptedDatagrams[index].Endpoint,
                    _acceptedDatagrams[index].Datagram);
            }
            return copies;
        }

        public KcpServerDatagram[] CopyCompleteDatagrams()
        {
            if (!IsCompleted)
                return Array.Empty<KcpServerDatagram>();

            var copies = new KcpServerDatagram[_completeDatagrams.Length];
            for (int index = 0; index < copies.Length; index++)
            {
                copies[index] = new KcpServerDatagram(
                    _completeDatagrams[index].Endpoint,
                    _completeDatagrams[index].Datagram);
            }
            return copies;
        }

        private static byte[][] CopyPayloads(byte[][] payloads)
        {
            var copies = new byte[payloads.Length][];
            for (int index = 0; index < payloads.Length; index++)
            {
                byte[] source = payloads[index];
                copies[index] = new byte[source.Length];
                Buffer.BlockCopy(
                    source,
                    0,
                    copies[index],
                    0,
                    source.Length);
            }
            return copies;
        }

        private int PendingTailCountFor(int receiverIndex)
        {
            return !IsTailReleaseStarted
                ? 0
                : _tailPayloadsByReceiver[receiverIndex].Length -
                  _nextTailPayloadIndex[receiverIndex];
        }

        private static bool ReadinessEqual(
            ResumeReadiness left,
            ResumeReadiness right)
        {
            return left.LastContiguousRemoteFrameID ==
                       right.LastContiguousRemoteFrameID &&
                   left.EarliestRecoverableCanonicalFrame ==
                       right.EarliestRecoverableCanonicalFrame &&
                   left.LatestLocalFrameID == right.LatestLocalFrameID;
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;

            int difference = 0;
            for (int index = 0; index < left.Length; index++)
                difference |= left[index] ^ right[index];
            return difference == 0;
        }
    }
}

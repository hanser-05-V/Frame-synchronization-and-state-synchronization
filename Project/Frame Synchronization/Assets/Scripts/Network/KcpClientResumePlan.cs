using System;

namespace FrameSyncDemo
{
    public sealed class KcpClientResumePlan
    {
        private readonly byte[] _attemptID;

        public KcpClientResumePlan(
            byte[] attemptID,
            bool isPeer,
            int uploadFrom,
            int uploadThrough,
            int replayFrom,
            int replayThrough,
            int peerFrom,
            int peerThrough)
        {
            if (attemptID == null ||
                attemptID.Length != RouteCProtocolConstants.NonceSize)
            {
                throw new ArgumentException(
                    "Resume AttemptID has the wrong length.",
                    nameof(attemptID));
            }
            ValidateRange(uploadFrom, uploadThrough, nameof(uploadFrom));
            ValidateRange(replayFrom, replayThrough, nameof(replayFrom));
            ValidateRange(peerFrom, peerThrough, nameof(peerFrom));

            _attemptID = (byte[])attemptID.Clone();
            IsPeer = isPeer;
            UploadFrom = uploadFrom;
            UploadThrough = uploadThrough;
            ReplayFrom = replayFrom;
            ReplayThrough = replayThrough;
            PeerFrom = peerFrom;
            PeerThrough = peerThrough;
        }

        public bool IsPeer { get; }

        public int UploadFrom { get; }

        public int UploadThrough { get; }

        public int ReplayFrom { get; }

        public int ReplayThrough { get; }

        public int PeerFrom { get; }

        public int PeerThrough { get; }

        public int LocalFrozenThrough =>
            IsPeer ? ReplayThrough : UploadThrough;

        public int ExpectedRemoteFrom =>
            IsPeer ? PeerFrom : ReplayFrom;

        public int ExpectedRemoteThrough =>
            IsPeer ? PeerThrough : ReplayThrough;

        public int RequiredUploadedThrough => LocalFrozenThrough;

        public byte[] CopyAttemptID()
        {
            return (byte[])_attemptID.Clone();
        }

        private static void ValidateRange(
            int fromFrameID,
            int throughFrameID,
            string parameterName)
        {
            long count = throughFrameID >= fromFrameID
                ? (long)throughFrameID - fromFrameID + 1L
                : 0L;
            if (fromFrameID < 0 ||
                throughFrameID < -1 ||
                count > RouteCProtocolConstants.InputHistoryCapacity)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
    }
}

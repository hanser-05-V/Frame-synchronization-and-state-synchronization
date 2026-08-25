using System;

namespace FrameSyncDemo
{
    public readonly struct ResumeReadiness
    {
        public ResumeReadiness(
            int lastContiguousRemoteFrameID,
            int earliestRecoverableCanonicalFrame,
            int latestLocalFrameID)
        {
            if (lastContiguousRemoteFrameID < -1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lastContiguousRemoteFrameID));
            }
            if (earliestRecoverableCanonicalFrame < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(earliestRecoverableCanonicalFrame));
            }
            if (latestLocalFrameID < -1)
                throw new ArgumentOutOfRangeException(nameof(latestLocalFrameID));

            LastContiguousRemoteFrameID = lastContiguousRemoteFrameID;
            EarliestRecoverableCanonicalFrame =
                earliestRecoverableCanonicalFrame;
            LatestLocalFrameID = latestLocalFrameID;
        }

        public int LastContiguousRemoteFrameID { get; }

        public int EarliestRecoverableCanonicalFrame { get; }

        public int LatestLocalFrameID { get; }
    }
}

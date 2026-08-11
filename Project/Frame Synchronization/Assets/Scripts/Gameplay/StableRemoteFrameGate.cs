using System;

namespace FrameSyncDemo
{
    public sealed class StableRemoteFrameGate
    {
        private int _stagedThroughFrame = -1;

        public int ValidatedThroughFrame { get; private set; } = -1;

        public void StageResolvedThrough(int frameID)
        {
            if (frameID < -1)
                throw new ArgumentOutOfRangeException(nameof(frameID));

            _stagedThroughFrame = Math.Max(_stagedThroughFrame, frameID);
        }

        public void CommitCorrection()
        {
            ValidatedThroughFrame = Math.Max(
                ValidatedThroughFrame,
                _stagedThroughFrame);
        }

        public void RejectCorrection()
        {
            _stagedThroughFrame = ValidatedThroughFrame;
        }
    }
}

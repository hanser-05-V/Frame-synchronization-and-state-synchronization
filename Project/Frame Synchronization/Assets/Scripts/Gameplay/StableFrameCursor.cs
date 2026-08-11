using System;

namespace FrameSyncDemo
{
    public sealed class StableFrameCursor
    {
        private int _lastProcessedFrame = -1;

        public int LastProcessedFrame => _lastProcessedFrame;

        public int CalculateStableThrough(
            int lastExecutedFrame,
            int latestRemoteFrame,
            bool isConnected)
        {
            if (!isConnected)
                return lastExecutedFrame;

            return Math.Min(lastExecutedFrame, latestRemoteFrame);
        }

        public bool TryGetNext(int stableThroughFrame, out int frameID)
        {
            frameID = _lastProcessedFrame + 1;
            return frameID <= stableThroughFrame;
        }

        public void MarkProcessed(int frameID)
        {
            int expectedFrame = _lastProcessedFrame + 1;
            if (frameID != expectedFrame)
            {
                throw new InvalidOperationException(
                    $"Stable frames must be processed consecutively. " +
                    $"Expected {expectedFrame}, got {frameID}.");
            }

            _lastProcessedFrame = frameID;
        }

        public void RebaseAt(int nextFrameID)
        {
            if (nextFrameID < 0)
                throw new ArgumentOutOfRangeException(nameof(nextFrameID));
            if (nextFrameID <= _lastProcessedFrame + 1)
            {
                throw new InvalidOperationException(
                    "Stable frame rebase must move forward.");
            }

            _lastProcessedFrame = nextFrameID - 1;
        }
    }
}

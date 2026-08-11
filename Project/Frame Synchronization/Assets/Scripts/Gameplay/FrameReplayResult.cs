namespace FrameSyncDemo
{
    /// <summary>一次完整回滚重演的确定性结果摘要。</summary>
    public readonly struct FrameReplayResult
    {
        public readonly bool succeeded;
        public readonly int restoredFrame;
        public readonly int replayedFrameCount;
        public readonly int missingInputFrame;

        public FrameReplayResult(
            bool succeeded,
            int restoredFrame,
            int replayedFrameCount,
            int missingInputFrame)
        {
            this.succeeded = succeeded;
            this.restoredFrame = restoredFrame;
            this.replayedFrameCount = replayedFrameCount;
            this.missingInputFrame = missingInputFrame;
        }
    }
}

namespace FrameSyncDemo
{
    public readonly struct ReconcileResult
    {
        public ReconcileResult(
            bool succeeded,
            bool replayed,
            int mismatchFrame,
            int restoredFrame,
            int replayedFrameCount,
            FrameInputLedger.ReplayPlanResult planResult,
            FrameReplayResult.Failure replayFailure)
        {
            Succeeded = succeeded;
            Replayed = replayed;
            MismatchFrame = mismatchFrame;
            RestoredFrame = restoredFrame;
            ReplayedFrameCount = replayedFrameCount;
            PlanResult = planResult;
            ReplayFailure = replayFailure;
        }

        public bool Succeeded { get; }
        public bool Replayed { get; }
        public int MismatchFrame { get; }
        public int RestoredFrame { get; }
        public int ReplayedFrameCount { get; }
        public FrameInputLedger.ReplayPlanResult PlanResult { get; }
        public FrameReplayResult.Failure ReplayFailure { get; }
    }
}

namespace FrameSyncServer
{
    public enum ServerResumeFailureKind : byte
    {
        GraceExpired = 0,
        ServerHistoryUnavailable = 1,
        ClientHistoryUnavailable = 2,
        CorrectionFloorUnavailable = 3,
        RangeCapacityExceeded = 4,
        GenerationChanged = 5,
        ConflictingInput = 6,
        LiveTailCapacityExceeded = 7,
        ProcessingBudgetExceeded = 8,
        InvalidResumeState = 9
    }
}

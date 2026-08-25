namespace FrameSyncDemo
{
    public enum OutboundHistoryDisposition
    {
        Accepted = 0,
        IdempotentDuplicate = 1,
        ConflictingDuplicate = 2,
        HistoryUnavailable = 3,
        CapacityExceeded = 4
    }
}

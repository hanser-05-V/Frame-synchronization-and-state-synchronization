namespace FrameSyncServer
{
    public enum ServerHandshakeDisposition : byte
    {
        Allocated = 0,
        IdempotentRetry = 1,
        MatchFull = 2,
        Rejected = 3,
        Reconnected = 4
    }
}

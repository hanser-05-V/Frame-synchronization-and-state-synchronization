namespace FrameSyncDemo
{
    public enum RawUdpInputDisposition : byte
    {
        Accepted = 0,
        Idempotent = 1,
        Conflict = 2,
        TooOld = 3,
        CapacityExceeded = 4,
        UnrecoverableGap = 5
    }
}

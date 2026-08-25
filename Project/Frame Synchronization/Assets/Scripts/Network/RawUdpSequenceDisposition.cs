namespace FrameSyncDemo
{
    public enum RawUdpSequenceDisposition : byte
    {
        Accepted = 0,
        Duplicate = 1,
        TooOld = 2,
        Conflict = 3,
        Ambiguous = 4
    }
}

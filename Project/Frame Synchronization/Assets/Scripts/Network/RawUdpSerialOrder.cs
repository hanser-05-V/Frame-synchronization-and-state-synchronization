namespace FrameSyncDemo
{
    public enum RawUdpSerialOrder : byte
    {
        Older = 0,
        Equal = 1,
        Newer = 2,
        Ambiguous = 3
    }
}

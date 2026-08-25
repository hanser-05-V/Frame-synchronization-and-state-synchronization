namespace FrameSyncDemo
{
    public readonly struct RawUdpInputEntry
    {
        public RawUdpInputEntry(uint raw, int frameID)
        {
            Raw = raw;
            FrameID = frameID;
        }

        public uint Raw { get; }
        public int FrameID { get; }
    }
}

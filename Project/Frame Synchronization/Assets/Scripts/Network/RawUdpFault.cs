namespace FrameSyncDemo
{
    public readonly struct RawUdpFault
    {
        public RawUdpFault(
            RawUdpFaultReason reason,
            byte playerIndex,
            int frameID,
            int observedLatestFrameID)
        {
            Reason = reason;
            PlayerIndex = playerIndex;
            FrameID = frameID;
            ObservedLatestFrameID = observedLatestFrameID;
        }

        public RawUdpFaultReason Reason { get; }
        public byte PlayerIndex { get; }
        public int FrameID { get; }
        public int ObservedLatestFrameID { get; }
    }
}

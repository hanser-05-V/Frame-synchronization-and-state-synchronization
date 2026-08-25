namespace FrameSyncDemo
{
    public readonly struct UdpDatagramFaultDecision
    {
        internal UdpDatagramFaultDecision(
            bool dropped,
            int effectiveDelayMs,
            int jitterOffsetMs,
            int reorderExtraDelayMs,
            int copyCount)
        {
            Dropped = dropped;
            EffectiveDelayMs = effectiveDelayMs;
            JitterOffsetMs = jitterOffsetMs;
            ReorderExtraDelayMs = reorderExtraDelayMs;
            CopyCount = copyCount;
        }

        public bool Dropped { get; }
        public int EffectiveDelayMs { get; }
        public int JitterOffsetMs { get; }
        public int ReorderExtraDelayMs { get; }
        public int CopyCount { get; }
    }
}

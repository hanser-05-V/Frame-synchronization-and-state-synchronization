using System.Globalization;

namespace FrameSyncDemo
{
    public readonly struct RawUdpDatagramFaultDecision
    {
        internal RawUdpDatagramFaultDecision(
            bool dropped,
            int effectiveDueTimeOffsetMs,
            int jitterOffsetMs,
            int reorderExtraDelayMs,
            int copyCount)
        {
            Dropped = dropped;
            EffectiveDueTimeOffsetMs = effectiveDueTimeOffsetMs;
            JitterOffsetMs = jitterOffsetMs;
            ReorderExtraDelayMs = reorderExtraDelayMs;
            CopyCount = copyCount;
        }

        public bool Dropped { get; }
        public int EffectiveDueTimeOffsetMs { get; }
        public int JitterOffsetMs { get; }
        public int ReorderExtraDelayMs { get; }
        public int CopyCount { get; }

        public string ToCanonicalTraceLine(
            byte direction,
            ulong sessionFingerprint,
            RouteCMessageType messageType,
            int copyIndex,
            uint sendIdentity)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "direction={0} fingerprint={1:X16} type={2} copy={3} " +
                "identity={4} raw-udp-datagram-delay={5} " +
                "raw-udp-datagram-jitter={6} raw-udp-datagram-drop={7} " +
                "raw-udp-datagram-reorder={8} " +
                "raw-udp-datagram-duplicate={9}",
                direction,
                sessionFingerprint,
                messageType,
                copyIndex,
                sendIdentity,
                EffectiveDueTimeOffsetMs,
                JitterOffsetMs,
                Dropped ? 1 : 0,
                ReorderExtraDelayMs,
                CopyCount == 2 ? 1 : 0);
        }
    }
}

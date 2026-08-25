using System;
using kcp2k;

namespace FrameSyncDemo
{
    public sealed class RouteCKcpSettings
    {
        public RouteCKcpSettings(int intervalMs)
        {
            if (intervalMs != 1 &&
                intervalMs != 5 &&
                intervalMs != 10 &&
                intervalMs != 20)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(intervalMs),
                    "Route C KCP interval must be 1, 5, 10, or 20 milliseconds.");
            }

            IntervalMs = intervalMs;
        }

        public int IntervalMs { get; }

        internal void Configure(Kcp kcp)
        {
            if (kcp == null)
                throw new ArgumentNullException(nameof(kcp));

            kcp.SetMtu(RouteCProtocolConstants.KcpMtu);
            kcp.SetWindowSize(32u, 128u);
            kcp.SetNoDelay(1u, (uint)IntervalMs, 2, false);
        }
    }
}

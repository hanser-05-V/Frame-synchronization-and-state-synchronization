using System.Diagnostics;

namespace FrameSyncDemo
{
    public sealed class StopwatchMonotonicClock : IMonotonicClock
    {
        private readonly long _originTimestamp = Stopwatch.GetTimestamp();

        public uint Milliseconds
        {
            get
            {
                long elapsedTicks = Stopwatch.GetTimestamp() - _originTimestamp;
                return unchecked((uint)(elapsedTicks * 1000L / Stopwatch.Frequency));
            }
        }

        public long Timestamp => Stopwatch.GetTimestamp();
    }
}

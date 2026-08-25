using System.Globalization;

namespace FrameSyncDemo
{
    public sealed class RouteCKcpDiagnosticsSnapshot
    {
        public RouteCKcpDiagnosticsSnapshot(
            int requestedIntervalMs,
            int effectiveCheckDeltaMs,
            long lastActualUpdateGapTicks,
            long meanActualUpdateGapTicks,
            long maxActualUpdateGapTicks,
            long lastWakeupErrorTicks,
            long meanWakeupErrorTicks,
            long maxWakeupErrorTicks,
            long updateCount,
            long outputDatagramCount,
            long outputByteCount,
            long inputDatagramCount,
            long inputByteCount,
            long receiveCount,
            int waitSndHighWater,
            long inputErrorCount)
        {
            RequestedIntervalMs = requestedIntervalMs;
            EffectiveCheckDeltaMs = effectiveCheckDeltaMs;
            LastActualUpdateGapTicks = lastActualUpdateGapTicks;
            MeanActualUpdateGapTicks = meanActualUpdateGapTicks;
            MaxActualUpdateGapTicks = maxActualUpdateGapTicks;
            LastWakeupErrorTicks = lastWakeupErrorTicks;
            MeanWakeupErrorTicks = meanWakeupErrorTicks;
            MaxWakeupErrorTicks = maxWakeupErrorTicks;
            UpdateCount = updateCount;
            OutputDatagramCount = outputDatagramCount;
            OutputByteCount = outputByteCount;
            InputDatagramCount = inputDatagramCount;
            InputByteCount = inputByteCount;
            ReceiveCount = receiveCount;
            WaitSndHighWater = waitSndHighWater;
            InputErrorCount = inputErrorCount;
        }

        public int RequestedIntervalMs { get; }

        public int EffectiveCheckDeltaMs { get; }

        public long LastActualUpdateGapTicks { get; }

        public long MeanActualUpdateGapTicks { get; }

        public long MaxActualUpdateGapTicks { get; }

        public long LastWakeupErrorTicks { get; }

        public long MeanWakeupErrorTicks { get; }

        public long MaxWakeupErrorTicks { get; }

        public long UpdateCount { get; }

        public long OutputDatagramCount { get; }

        public long OutputByteCount { get; }

        public long InputDatagramCount { get; }

        public long InputByteCount { get; }

        public long ReceiveCount { get; }

        public int WaitSndHighWater { get; }

        public long InputErrorCount { get; }

        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "requestedIntervalMs={0} effectiveCheckDeltaMs={1} " +
                "actualGapTicks(last/mean/max)={2}/{3}/{4} " +
                "wakeupErrorTicks(last/mean/max)={5}/{6}/{7} " +
                "updates={8} output={9}/{10} input={11}/{12} " +
                "receives={13} waitSndHighWater={14} inputErrors={15}",
                RequestedIntervalMs,
                EffectiveCheckDeltaMs,
                LastActualUpdateGapTicks,
                MeanActualUpdateGapTicks,
                MaxActualUpdateGapTicks,
                LastWakeupErrorTicks,
                MeanWakeupErrorTicks,
                MaxWakeupErrorTicks,
                UpdateCount,
                OutputDatagramCount,
                OutputByteCount,
                InputDatagramCount,
                InputByteCount,
                ReceiveCount,
                WaitSndHighWater,
                InputErrorCount);
        }
    }
}

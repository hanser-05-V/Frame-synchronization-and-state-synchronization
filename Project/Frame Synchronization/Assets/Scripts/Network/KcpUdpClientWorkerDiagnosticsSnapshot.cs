namespace FrameSyncDemo
{
    public sealed class KcpUdpClientWorkerDiagnosticsSnapshot
    {
        public static readonly KcpUdpClientWorkerDiagnosticsSnapshot Empty =
            new KcpUdpClientWorkerDiagnosticsSnapshot(
                0L,
                0L,
                0L,
                0,
                0,
                0L,
                0,
                0L,
                0,
                0L);

        public KcpUdpClientWorkerDiagnosticsSnapshot(
            long completedRoundCount,
            long nonEmptyDatagramRoundCount,
            long totalDatagramsDrained,
            int lastDatagramsDrained,
            int maximumDatagramsDrainedPerRound,
            long lastReadySendRound,
            int lastReadySendOrder,
            long lastKcpDataSendRound,
            int lastKcpDataSendOrder,
            long lastBusinessInputRound)
        {
            CompletedRoundCount = completedRoundCount;
            NonEmptyDatagramRoundCount = nonEmptyDatagramRoundCount;
            TotalDatagramsDrained = totalDatagramsDrained;
            LastDatagramsDrained = lastDatagramsDrained;
            MaximumDatagramsDrainedPerRound =
                maximumDatagramsDrainedPerRound;
            LastReadySendRound = lastReadySendRound;
            LastReadySendOrder = lastReadySendOrder;
            LastKcpDataSendRound = lastKcpDataSendRound;
            LastKcpDataSendOrder = lastKcpDataSendOrder;
            LastBusinessInputRound = lastBusinessInputRound;
        }

        public long CompletedRoundCount { get; }

        public long NonEmptyDatagramRoundCount { get; }

        public long TotalDatagramsDrained { get; }

        public int LastDatagramsDrained { get; }

        public int MaximumDatagramsDrainedPerRound { get; }

        public long LastReadySendRound { get; }

        public int LastReadySendOrder { get; }

        public long LastKcpDataSendRound { get; }

        public int LastKcpDataSendOrder { get; }

        public long LastBusinessInputRound { get; }
    }
}

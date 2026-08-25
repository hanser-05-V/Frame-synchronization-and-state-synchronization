namespace FrameSyncDemo
{
    public sealed class RawUdpClientDiagnosticsSnapshot
    {
        public static readonly RawUdpClientDiagnosticsSnapshot Empty =
            new RawUdpClientDiagnosticsSnapshot();

        public NetworkSessionState State { get; internal set; }
        public int PlayerIndex { get; internal set; } = -1;
        public int WindowSize { get; internal set; }
        public string SessionFingerprint { get; internal set; } = "none";
        public long SentPacketSequenceHighWater { get; internal set; } = -1;
        public long ReceivedPacketSequenceHighWater { get; internal set; } = -1;
        public long DatagramsSent { get; internal set; }
        public long DatagramsReceived { get; internal set; }
        public long BytesSent { get; internal set; }
        public long BytesReceived { get; internal set; }
        public long ClassifiedDropCount { get; internal set; }
        public int LatestLocalFrameID { get; internal set; } = -1;
        public int HighestRemoteFrameID { get; internal set; } = -1;
        public int ContiguousRemoteFrameID { get; internal set; } = -1;
        public int PendingGapFrameID { get; internal set; } = -1;
        public int GapGraceCount { get; internal set; }
        public long DirectRecoveryCount { get; internal set; }
        public long RedundancyRecoveryCount { get; internal set; }
        public long ReorderRecoveryCount { get; internal set; }
        public int LocalCommandQueueCount { get; internal set; }
        public int LocalCommandQueueHighWater { get; internal set; }
        public int RemoteArrivalQueueCount { get; internal set; }
        public int RemoteArrivalQueueHighWater { get; internal set; }
        public int EventQueueCount { get; internal set; }
        public int EventQueueHighWater { get; internal set; }
        public long WorkerRoundCount { get; internal set; }
        public int MaximumDatagramsReceivedPerRound { get; internal set; }
        public long ReceiveBudgetExhaustionCount { get; internal set; }
        public long SelectCount { get; internal set; }
        public int MaximumSelectWaitMilliseconds { get; internal set; }
        public bool StopCompleted { get; internal set; }
    }
}

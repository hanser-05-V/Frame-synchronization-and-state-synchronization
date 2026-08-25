namespace FrameSyncServer
{
    public sealed class RawUdpServerDiagnosticsSnapshot
    {
        public static readonly RawUdpServerDiagnosticsSnapshot Empty =
            new RawUdpServerDiagnosticsSnapshot();

        public int BoundPort { get; internal set; }
        public int PlayerCount { get; internal set; }
        public bool RelayEnabled { get; internal set; }
        public bool IsTerminal { get; internal set; }
        public long DatagramsReceived { get; internal set; }
        public long DatagramsSent { get; internal set; }
        public long BytesReceived { get; internal set; }
        public long BytesSent { get; internal set; }
        public long WorkerRoundCount { get; internal set; }
        public int MaximumDatagramsReceivedPerRound { get; internal set; }
        public long ReceiveBudgetExhaustionCount { get; internal set; }
        public int MaximumPlayerWorkItemsPerRound { get; internal set; }
        public long FairnessRecheckCount { get; internal set; }
        public int Player0QueueCount { get; internal set; }
        public int Player1QueueCount { get; internal set; }
        public int Player0QueueHighWater { get; internal set; }
        public int Player1QueueHighWater { get; internal set; }
        public long SelectCount { get; internal set; }
        public int MaximumSelectWaitMilliseconds { get; internal set; }
        public bool StopCompleted { get; internal set; }
        public long LateRecoveryRelayCopies { get; internal set; }
        public bool WorkerFault { get; internal set; }
        public long TerminalFaultRepeatDatagramsSent { get; internal set; }
        public FrameSyncDemo.RawUdpFaultReason TerminalReason { get; internal set; }
        public int TerminalPlayerIndex { get; internal set; } = 255;
        public int TerminalFrameID { get; internal set; } = -1;
        public int TerminalObservedLatestFrameID { get; internal set; } = -1;
    }
}

namespace FrameSyncDemo
{
    public static class RouteCProtocolConstants
    {
        public const byte Version = 1;
        public const int HeaderSize = 28;
        public const int MaximumDatagramSize = 1200;
        public const int KcpMtu = MaximumDatagramSize - HeaderSize;
        public const int KcpHeaderSize = 24;
        public const int BusinessInputSize = 8;
        public const int NonceSize = 16;
        public const int SessionIdSize = 16;
        public const int ReconnectTokenSize = 32;
        public const int InitialKcpIntervalMs = 10;
        public const int HandshakeRetryMs = 250;
        public const int HeartbeatSilenceMs = 1000;
        public const int DisconnectTimeoutMs = 3000;
        public const int ResumeGraceMs = 5000;
        public const int InputHistoryCapacity = 256;
        public const int MaximumDatagramsPerRound = 64;
        public const int MaximumMessagesPerSessionPerRound = 64;
        public const int MaximumWorkerSliceMs = 2;
        public const byte RawGeneration = 1;
        public const byte RawMinimumWindowSize = 1;
        public const byte RawDefaultWindowSize = 6;
        public const byte RawMaximumWindowSize = 16;
        public const int RawInputHeaderSize = 12;
        public const int RawMaximumInputPayloadSize =
            RawInputHeaderSize + (RawMaximumWindowSize * BusinessInputSize);
        public const int RawMaximumInputDatagramSize =
            HeaderSize + RawMaximumInputPayloadSize;
        public const int RawPacketSequenceWindowSize = 64;
        public const int RawGapReorderGrace = 16;
        public const int RawInputHistoryCapacity = 256;
        public const int RawQueueCapacity = 256;
        public const int RawHandshakeRetryMs = 50;
        public const int RawHandshakeTimeoutMs = 3000;
        public const int RawValidTrafficTimeoutMs = 3000;
        public const int RawInputCadenceMs = 33;
        public const int RawFaultRepeatCount = 6;
        public const int RawFaultRepeatIntervalMs = 50;
        public const int RawMaximumSelectWaitMs = 10;
        public const int RawWaitForStopMs = 260;
    }
}

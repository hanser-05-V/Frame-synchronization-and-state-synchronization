namespace FrameSyncDemo
{
    public enum NetworkTransportEventReason : byte
    {
        StartRequested = 0,
        WelcomeAccepted = 1,
        SessionStarted = 2,
        ConnectionTimedOut = 3,
        ReconnectAccepted = 4,
        ResumeRequired = 5,
        ResumeGraceExpired = 6,
        DisconnectAdvisory = 7,
        StopRequested = 8,
        WorkerFault = 9,
        WorkerStopTimedOut = 10,
        OutboundHistoryFault = 11,
        ResumeRejected = 12,
        ConnectionClosed = 13,
        LocalInputQueueFull = 14,
        HandshakeTimeout = 15,
        ProtocolViolation = 16,
        ConflictingInput = 17,
        UnrecoverableInputGap = 18,
        CapacityExceeded = 19,
        PeerFault = 20,
        MatchFull = 21
    }
}

namespace FrameSyncDemo
{
    public enum RawUdpFaultReason : ushort
    {
        HandshakeTimeout = 1,
        ConnectionTimedOut = 2,
        ProtocolViolation = 3,
        ConflictingInput = 4,
        UnrecoverableInputGap = 5,
        CapacityExceeded = 6,
        PeerFault = 7,
        MatchFull = 8
    }
}

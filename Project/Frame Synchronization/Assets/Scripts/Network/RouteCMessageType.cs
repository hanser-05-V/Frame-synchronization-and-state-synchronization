namespace FrameSyncDemo
{
    public enum RouteCMessageType : byte
    {
        Hello = 1,
        Welcome = 2,
        Ready = 3,
        Start = 4,
        KcpData = 5,
        Heartbeat = 6,
        Disconnect = 7,
        ResumeProbe = 8,
        ResumeState = 9,
        ResumeAccepted = 10,
        ResumeComplete = 11,
        ResumeRejected = 12,
        RawHello = 13,
        RawWelcome = 14,
        RawReady = 15,
        RawStart = 16,
        RawInput = 17,
        RawFault = 18
    }
}

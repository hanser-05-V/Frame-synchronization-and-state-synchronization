namespace FrameSyncDemo
{
    public enum NetworkSessionState : byte
    {
        Disconnected = 0,
        Handshaking = 1,
        AwaitingReady = 2,
        Running = 3,
        Reconnecting = 4,
        Resuming = 5,
        Terminated = 6
    }
}

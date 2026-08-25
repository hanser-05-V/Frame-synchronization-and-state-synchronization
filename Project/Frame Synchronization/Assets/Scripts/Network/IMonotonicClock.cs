namespace FrameSyncDemo
{
    public interface IMonotonicClock
    {
        uint Milliseconds { get; }

        long Timestamp { get; }
    }
}

using FrameSyncDemo;

namespace FrameSyncServer
{
    public sealed class ServerInputHistory
    {
        private readonly OutboundActualHistory _history;

        public ServerInputHistory(int capacity)
        {
            _history = new OutboundActualHistory(capacity);
        }

        public OutboundHistoryDisposition Record(int frameID, uint raw)
        {
            return _history.Record(frameID, raw);
        }

        public bool TryBeginFreeze(int throughFrameID)
        {
            return _history.TryBeginFreeze(throughFrameID);
        }

        public bool TryGet(int frameID, out uint raw)
        {
            return _history.TryGet(frameID, out raw);
        }

        public bool TryCopyRange(
            int fromFrameID,
            int throughFrameID,
            out byte[][] payloads)
        {
            return _history.TryCopyRange(
                fromFrameID,
                throughFrameID,
                out payloads);
        }

        public bool TryReleaseLiveTail(out byte[][] payloads)
        {
            return _history.TryReleaseLiveTail(out payloads);
        }
    }
}

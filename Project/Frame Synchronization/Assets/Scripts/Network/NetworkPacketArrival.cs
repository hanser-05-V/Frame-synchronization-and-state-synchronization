using System;

namespace FrameSyncDemo
{
    public readonly struct NetworkPacketArrival
    {
        public NetworkPacketArrival(
            uint raw,
            int remoteFrameID,
            long receiveSequence,
            long receivedTimestamp)
        {
            if (remoteFrameID < 0)
                throw new ArgumentOutOfRangeException(nameof(remoteFrameID));
            if (receiveSequence < 0)
                throw new ArgumentOutOfRangeException(nameof(receiveSequence));
            if (receivedTimestamp < 0)
                throw new ArgumentOutOfRangeException(nameof(receivedTimestamp));

            Raw = raw;
            RemoteFrameID = remoteFrameID;
            ReceiveSequence = receiveSequence;
            ReceivedTimestamp = receivedTimestamp;
        }

        public uint Raw { get; }
        public int RemoteFrameID { get; }
        public long ReceiveSequence { get; }
        public long ReceivedTimestamp { get; }
    }
}

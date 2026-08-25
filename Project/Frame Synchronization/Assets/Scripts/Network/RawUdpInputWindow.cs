using System;

namespace FrameSyncDemo
{
    public sealed class RawUdpInputWindow
    {
        private readonly RawUdpInputEntry[] _entries;

        public RawUdpInputWindow(
            byte playerIndex,
            uint packetSequence,
            int latestFrameID,
            RawUdpInputEntry[] entries)
        {
            if (entries == null)
                throw new ArgumentNullException(nameof(entries));

            PlayerIndex = playerIndex;
            PacketSequence = packetSequence;
            LatestFrameID = latestFrameID;
            _entries = (RawUdpInputEntry[])entries.Clone();
        }

        public byte PlayerIndex { get; }
        public byte InputCount => (byte)_entries.Length;
        public uint PacketSequence { get; }
        public int LatestFrameID { get; }
        public RawUdpInputEntry[] Entries =>
            (RawUdpInputEntry[])_entries.Clone();
    }
}

using System;
using System.Collections.Generic;

namespace FrameSyncDemo
{
    public sealed class RawUdpRelayHistory
    {
        private readonly int _windowSize;
        private readonly Dictionary<int, uint> _rawByFrame =
            new Dictionary<int, uint>();
        private readonly Dictionary<int, int> _opportunitiesByFrame =
            new Dictionary<int, int>();
        private readonly uint[] _nextDownlinkSequences;
        private int _highestFrameID = -1;

        public RawUdpRelayHistory(int windowSize)
            : this(windowSize, 0u, 0u)
        {
        }

        public RawUdpRelayHistory(
            int windowSize,
            uint receiverZeroInitialSequence,
            uint receiverOneInitialSequence)
        {
            if (windowSize < RouteCProtocolConstants.RawMinimumWindowSize ||
                windowSize > RouteCProtocolConstants.RawMaximumWindowSize)
            {
                throw new ArgumentOutOfRangeException(nameof(windowSize));
            }

            _windowSize = windowSize;
            _nextDownlinkSequences = new[]
            {
                receiverZeroInitialSequence,
                receiverOneInitialSequence
            };
        }

        public RawUdpInputDisposition Remember(in RawUdpInputEntry entry)
        {
            if (entry.FrameID < 0)
                return RawUdpInputDisposition.Conflict;

            int retainedFloor = GetRetainedFloor();
            if (entry.FrameID < retainedFloor)
                return RawUdpInputDisposition.TooOld;

            if (_rawByFrame.TryGetValue(entry.FrameID, out uint existingRaw))
            {
                return existingRaw == entry.Raw
                    ? RawUdpInputDisposition.Idempotent
                    : RawUdpInputDisposition.Conflict;
            }

            if ((long)entry.FrameID - _highestFrameID >
                RouteCProtocolConstants.RawInputHistoryCapacity)
            {
                return RawUdpInputDisposition.CapacityExceeded;
            }

            _rawByFrame.Add(entry.FrameID, entry.Raw);
            if (entry.FrameID > _highestFrameID)
            {
                _highestFrameID = entry.FrameID;
                PurgeExpiredFrames();
            }
            return RawUdpInputDisposition.Accepted;
        }

        public bool TryGetRaw(int frameID, out uint raw)
        {
            return _rawByFrame.TryGetValue(frameID, out raw);
        }

        public RawUdpInputEntry[] RebuildWindow(RawUdpInputWindow source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            RawUdpInputEntry[] sourceEntries = source.Entries;
            var rebuilt = new RawUdpInputEntry[sourceEntries.Length];
            for (int index = 0; index < sourceEntries.Length; index++)
            {
                int frameID = sourceEntries[index].FrameID;
                if (!_rawByFrame.TryGetValue(frameID, out uint raw))
                {
                    if (frameID >= GetRetainedFloor())
                    {
                        throw new InvalidOperationException(
                            "Cannot relay a retained frame that is absent from immutable history.");
                    }
                    raw = sourceEntries[index].Raw;
                }
                rebuilt[index] = new RawUdpInputEntry(raw, frameID);
            }
            return rebuilt;
        }

        public int GetRelayCopyCount(
            RawUdpInputEntry[] newlyAcceptedEntries,
            int highestObservedLatestBeforeWindow)
        {
            if (newlyAcceptedEntries == null)
                throw new ArgumentNullException(nameof(newlyAcceptedEntries));

            int relayCopies = 1;
            for (int index = 0; index < newlyAcceptedEntries.Length; index++)
            {
                RawUdpInputEntry entry = newlyAcceptedEntries[index];
                bool isLateRecovery =
                    (long)highestObservedLatestBeforeWindow - entry.FrameID >=
                    _windowSize;
                if (!isLateRecovery)
                    continue;

                int needed = _windowSize -
                    GetDownlinkOpportunityCount(entry.FrameID);
                relayCopies = Math.Max(relayCopies, needed);
            }

            return Math.Min(_windowSize, relayCopies);
        }

        public int GetDownlinkOpportunityCount(int frameID)
        {
            return _opportunitiesByFrame.TryGetValue(frameID, out int count)
                ? count
                : 0;
        }

        public void RecordDownlinkOpportunityForEveryEntryInWindow(
            RawUdpInputEntry[] entries)
        {
            if (entries == null)
                throw new ArgumentNullException(nameof(entries));

            for (int index = 0; index < entries.Length; index++)
            {
                int frameID = entries[index].FrameID;
                int count = GetDownlinkOpportunityCount(frameID);
                _opportunitiesByFrame[frameID] = count == int.MaxValue
                    ? int.MaxValue
                    : count + 1;
            }
        }

        public uint TakeNextDownlinkSequence(byte receiverPlayerIndex)
        {
            if (receiverPlayerIndex > 1)
                throw new ArgumentOutOfRangeException(nameof(receiverPlayerIndex));

            uint sequence = _nextDownlinkSequences[receiverPlayerIndex];
            _nextDownlinkSequences[receiverPlayerIndex] =
                unchecked(sequence + 1u);
            return sequence;
        }

        private int GetRetainedFloor()
        {
            if (_highestFrameID < 0)
                return 0;
            return Math.Max(
                0,
                _highestFrameID -
                RouteCProtocolConstants.RawInputHistoryCapacity + 1);
        }

        private void PurgeExpiredFrames()
        {
            int retainedFloor = GetRetainedFloor();
            var expired = new List<int>();
            foreach (int frameID in _rawByFrame.Keys)
            {
                if (frameID < retainedFloor)
                    expired.Add(frameID);
            }

            for (int index = 0; index < expired.Count; index++)
            {
                int frameID = expired[index];
                _rawByFrame.Remove(frameID);
                _opportunitiesByFrame.Remove(frameID);
            }
        }
    }
}

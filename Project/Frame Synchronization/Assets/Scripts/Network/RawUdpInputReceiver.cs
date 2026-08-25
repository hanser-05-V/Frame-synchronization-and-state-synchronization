using System;
using System.Collections.Generic;

namespace FrameSyncDemo
{
    public sealed class RawUdpInputReceiver
    {
        private readonly int _windowSize;
        private readonly bool _allowNewerSequenceLatestRegression;
        private readonly RawUdpPacketSequenceWindow _sequenceWindow =
            new RawUdpPacketSequenceWindow();
        private readonly Dictionary<int, uint> _history =
            new Dictionary<int, uint>();

        private bool _hasHighestSequence;
        private uint _highestSequence;
        private int _highestRetainedFrameID = -1;
        private int _pendingGapFrameID = -1;
        private uint _pendingGapEvidenceSequence;
        private int _pendingGapNewerSequenceCount;

        public RawUdpInputReceiver(
            int windowSize,
            bool allowNewerSequenceLatestRegression)
        {
            if (windowSize < RouteCProtocolConstants.RawMinimumWindowSize ||
                windowSize > RouteCProtocolConstants.RawMaximumWindowSize)
            {
                throw new ArgumentOutOfRangeException(nameof(windowSize));
            }

            _windowSize = windowSize;
            _allowNewerSequenceLatestRegression =
                allowNewerSequenceLatestRegression;
        }

        public int HighestObservedLatestFrameID { get; private set; } = -1;
        public int LastContiguousFrameID { get; private set; } = -1;
        public RawUdpSequenceDisposition LastSequenceDisposition
        {
            get;
            private set;
        } = RawUdpSequenceDisposition.Accepted;

        public RawUdpInputDisposition Accept(
            in RawUdpInputWindow window,
            out RawUdpInputEntry[] firstAcceptedEntries,
            out RawUdpFault terminalFault)
        {
            firstAcceptedEntries = new RawUdpInputEntry[0];
            terminalFault = default;
            if (window == null)
            {
                terminalFault = CreateFault(
                    RawUdpFaultReason.ProtocolViolation,
                    255,
                    -1);
                return RawUdpInputDisposition.Conflict;
            }

            RawUdpInputEntry[] entries = window.Entries;
            byte[] immutablePayload;
            try
            {
                immutablePayload = RawUdpProtocolCodec.EncodeInput(
                    window.PlayerIndex,
                    (byte)_windowSize,
                    window.PacketSequence,
                    window.LatestFrameID,
                    entries);
            }
            catch (ArgumentException)
            {
                terminalFault = CreateFault(
                    RawUdpFaultReason.ProtocolViolation,
                    window.PlayerIndex,
                    window.LatestFrameID);
                return RawUdpInputDisposition.Conflict;
            }

            RawUdpSerialOrder sequenceOrder = _hasHighestSequence
                ? RawUdpSerialNumber.Compare(
                    window.PacketSequence,
                    _highestSequence)
                : RawUdpSerialOrder.Newer;
            RawUdpSequenceDisposition sequenceDisposition =
                _sequenceWindow.Observe(
                    window.PacketSequence,
                    immutablePayload);
            LastSequenceDisposition = sequenceDisposition;
            switch (sequenceDisposition)
            {
                case RawUdpSequenceDisposition.Duplicate:
                    return RawUdpInputDisposition.Idempotent;
                case RawUdpSequenceDisposition.TooOld:
                    return RawUdpInputDisposition.TooOld;
                case RawUdpSequenceDisposition.Conflict:
                case RawUdpSequenceDisposition.Ambiguous:
                    terminalFault = CreateFault(
                        RawUdpFaultReason.ProtocolViolation,
                        window.PlayerIndex,
                        window.LatestFrameID);
                    return RawUdpInputDisposition.Conflict;
            }

            bool isNewHighestSequence =
                !_hasHighestSequence ||
                sequenceOrder == RawUdpSerialOrder.Newer;
            if (isNewHighestSequence)
            {
                _hasHighestSequence = true;
                _highestSequence = window.PacketSequence;
            }

            if (!_allowNewerSequenceLatestRegression &&
                isNewHighestSequence &&
                HighestObservedLatestFrameID >= 0 &&
                window.LatestFrameID < HighestObservedLatestFrameID)
            {
                terminalFault = CreateFault(
                    RawUdpFaultReason.ProtocolViolation,
                    window.PlayerIndex,
                    window.LatestFrameID);
                return RawUdpInputDisposition.Conflict;
            }

            int observedLatest = Math.Max(
                HighestObservedLatestFrameID,
                window.LatestFrameID);
            if ((long)observedLatest - LastContiguousFrameID >
                RouteCProtocolConstants.RawInputHistoryCapacity)
            {
                HighestObservedLatestFrameID = observedLatest;
                terminalFault = CreateFault(
                    RawUdpFaultReason.CapacityExceeded,
                    window.PlayerIndex,
                    window.LatestFrameID);
                return RawUdpInputDisposition.CapacityExceeded;
            }

            HighestObservedLatestFrameID = observedLatest;
            int retainedFloor = GetRetainedFloor();
            var accepted = new List<RawUdpInputEntry>(entries.Length);
            bool sawTooOld = false;
            for (int index = 0; index < entries.Length; index++)
            {
                RawUdpInputEntry entry = entries[index];
                if (entry.FrameID < retainedFloor)
                {
                    sawTooOld = true;
                    continue;
                }

                if (_history.TryGetValue(entry.FrameID, out uint existingRaw))
                {
                    if (existingRaw != entry.Raw)
                    {
                        terminalFault = CreateFault(
                            RawUdpFaultReason.ConflictingInput,
                            window.PlayerIndex,
                            entry.FrameID);
                        return RawUdpInputDisposition.Conflict;
                    }
                    continue;
                }

                accepted.Add(entry);
            }

            for (int index = 0; index < accepted.Count; index++)
            {
                RawUdpInputEntry entry = accepted[index];
                _history.Add(entry.FrameID, entry.Raw);
                _highestRetainedFrameID = Math.Max(
                    _highestRetainedFrameID,
                    entry.FrameID);
            }

            PurgeExpiredHistory();
            AdvanceContiguousFrontier();
            firstAcceptedEntries = accepted.ToArray();

            if (UpdateGapGrace(
                window.PacketSequence,
                window.PlayerIndex,
                out terminalFault))
            {
                firstAcceptedEntries = new RawUdpInputEntry[0];
                return RawUdpInputDisposition.UnrecoverableGap;
            }

            if (accepted.Count > 0)
                return RawUdpInputDisposition.Accepted;
            return sawTooOld
                ? RawUdpInputDisposition.TooOld
                : RawUdpInputDisposition.Idempotent;
        }

        private bool UpdateGapGrace(
            uint packetSequence,
            byte playerIndex,
            out RawUdpFault terminalFault)
        {
            terminalFault = default;
            int oldestMissingFrameID = LastContiguousFrameID + 1;
            bool crossedRedundancyWindow =
                (long)HighestObservedLatestFrameID - oldestMissingFrameID >=
                _windowSize;
            if (!crossedRedundancyWindow)
            {
                ClearPendingGap();
                return false;
            }

            if (_pendingGapFrameID != oldestMissingFrameID)
            {
                _pendingGapFrameID = oldestMissingFrameID;
                _pendingGapEvidenceSequence = packetSequence;
                _pendingGapNewerSequenceCount = 0;
                return false;
            }

            if (RawUdpSerialNumber.Compare(
                    packetSequence,
                    _pendingGapEvidenceSequence) == RawUdpSerialOrder.Newer)
            {
                _pendingGapNewerSequenceCount++;
            }

            if (_pendingGapNewerSequenceCount <
                RouteCProtocolConstants.RawGapReorderGrace)
            {
                return false;
            }

            terminalFault = CreateFault(
                RawUdpFaultReason.UnrecoverableInputGap,
                playerIndex,
                oldestMissingFrameID);
            return true;
        }

        private void AdvanceContiguousFrontier()
        {
            while (_history.ContainsKey(LastContiguousFrameID + 1))
                LastContiguousFrameID++;

            if (_pendingGapFrameID <= LastContiguousFrameID)
                ClearPendingGap();
        }

        private void PurgeExpiredHistory()
        {
            int retainedFloor = GetRetainedFloor();
            var expired = new List<int>();
            foreach (int frameID in _history.Keys)
            {
                if (frameID < retainedFloor)
                    expired.Add(frameID);
            }

            for (int index = 0; index < expired.Count; index++)
                _history.Remove(expired[index]);
        }

        private int GetRetainedFloor()
        {
            if (_highestRetainedFrameID < 0)
                return 0;
            return Math.Max(
                0,
                _highestRetainedFrameID -
                RouteCProtocolConstants.RawInputHistoryCapacity + 1);
        }

        private void ClearPendingGap()
        {
            _pendingGapFrameID = -1;
            _pendingGapEvidenceSequence = 0u;
            _pendingGapNewerSequenceCount = 0;
        }

        private RawUdpFault CreateFault(
            RawUdpFaultReason reason,
            byte playerIndex,
            int frameID)
        {
            return new RawUdpFault(
                reason,
                playerIndex,
                frameID,
                HighestObservedLatestFrameID);
        }
    }
}

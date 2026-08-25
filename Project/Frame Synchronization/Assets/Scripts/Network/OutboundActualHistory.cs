using System;

namespace FrameSyncDemo
{
    public sealed class OutboundActualHistory
    {
        private readonly int[] _frameIDs;
        private readonly uint[] _rawValues;
        private readonly byte[][] _payloads;
        private readonly int[] _liveTailFrameIDs;
        private readonly uint[] _liveTailRawValues;
        private readonly byte[][] _liveTailPayloads;
        private int _latestFrameID = -1;
        private int _liveTailCount;
        private int _frozenThroughFrameID = -1;
        private bool _isFrozen;

        public OutboundActualHistory(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _frameIDs = new int[capacity];
            _rawValues = new uint[capacity];
            _payloads = new byte[capacity][];
            _liveTailFrameIDs = new int[capacity];
            _liveTailRawValues = new uint[capacity];
            _liveTailPayloads = new byte[capacity][];
            for (int index = 0; index < capacity; index++)
            {
                _frameIDs[index] = -1;
                _liveTailFrameIDs[index] = -1;
            }
        }

        public bool TryBeginFreeze(int throughFrameID)
        {
            if (throughFrameID < -1)
                throw new ArgumentOutOfRangeException(nameof(throughFrameID));
            if (_isFrozen)
                return _frozenThroughFrameID == throughFrameID;

            _liveTailCount = 0;
            for (int index = 0; index < _liveTailFrameIDs.Length; index++)
            {
                _liveTailFrameIDs[index] = -1;
                _liveTailPayloads[index] = null;
            }

            int previousLatestFrameID = _latestFrameID;
            int earliestRetainedFrame = previousLatestFrameID < 0
                ? 0
                : Math.Max(
                    0,
                    previousLatestFrameID - _frameIDs.Length + 1);
            int retainedLatestAtOrBelowBoundary = -1;
            for (int index = 0; index < _frameIDs.Length; index++)
            {
                int frameID = _frameIDs[index];
                bool retained = frameID >= earliestRetainedFrame &&
                                frameID <= previousLatestFrameID;
                if (!retained)
                    continue;
                if (frameID <= throughFrameID)
                {
                    if (frameID > retainedLatestAtOrBelowBoundary)
                        retainedLatestAtOrBelowBoundary = frameID;
                    continue;
                }

                InsertExistingLiveTail(
                    frameID,
                    _rawValues[index],
                    _payloads[index]);
                _frameIDs[index] = -1;
                _payloads[index] = null;
            }

            _latestFrameID = retainedLatestAtOrBelowBoundary;
            _isFrozen = true;
            _frozenThroughFrameID = throughFrameID;
            return true;
        }

        public OutboundHistoryDisposition Record(int frameID, uint raw)
        {
            if (frameID < 0)
                throw new ArgumentOutOfRangeException(nameof(frameID));
            if (_isFrozen && frameID > _frozenThroughFrameID)
                return RecordLiveTail(frameID, raw);
            if (_latestFrameID >= 0 &&
                frameID <= _latestFrameID - _frameIDs.Length)
            {
                return OutboundHistoryDisposition.HistoryUnavailable;
            }

            int index = frameID % _frameIDs.Length;
            if (_frameIDs[index] == frameID)
            {
                return _rawValues[index] == raw
                    ? OutboundHistoryDisposition.IdempotentDuplicate
                    : OutboundHistoryDisposition.ConflictingDuplicate;
            }

            _frameIDs[index] = frameID;
            _rawValues[index] = raw;
            _payloads[index] = RouteCProtocolCodec.EncodeBusinessInput(
                raw,
                frameID);
            if (frameID > _latestFrameID)
                _latestFrameID = frameID;
            return OutboundHistoryDisposition.Accepted;
        }

        public bool TryGet(int frameID, out uint raw)
        {
            int liveTailIndex = FindLiveTailIndex(frameID);
            if (liveTailIndex >= 0)
            {
                raw = _liveTailRawValues[liveTailIndex];
                return true;
            }

            if (!IsRetained(frameID))
            {
                raw = 0u;
                return false;
            }

            int index = frameID % _frameIDs.Length;
            if (_frameIDs[index] != frameID)
            {
                raw = 0u;
                return false;
            }

            raw = _rawValues[index];
            return true;
        }

        public bool TryCopyRange(
            int fromFrameID,
            int throughFrameID,
            out byte[][] payloads)
        {
            long count = (long)throughFrameID - fromFrameID + 1L;
            if (fromFrameID < 0 ||
                throughFrameID < fromFrameID ||
                count > _frameIDs.Length)
            {
                payloads = Array.Empty<byte[]>();
                return false;
            }

            var copies = new byte[(int)count][];
            for (int offset = 0; offset < copies.Length; offset++)
            {
                int frameID = checked((int)((long)fromFrameID + offset));
                if (!TryCopyPayload(frameID, out byte[] copy))
                {
                    payloads = Array.Empty<byte[]>();
                    return false;
                }
                copies[offset] = copy;
            }

            payloads = copies;
            return true;
        }

        public bool TryReleaseLiveTail(out byte[][] payloads)
        {
            if (!_isFrozen)
            {
                payloads = Array.Empty<byte[]>();
                return false;
            }

            payloads = new byte[_liveTailCount][];
            for (int index = 0; index < _liveTailCount; index++)
            {
                byte[] source = _liveTailPayloads[index];
                payloads[index] = new byte[source.Length];
                Buffer.BlockCopy(
                    source,
                    0,
                    payloads[index],
                    0,
                    source.Length);
            }

            _isFrozen = false;
            _frozenThroughFrameID = -1;
            int releasedCount = _liveTailCount;
            _liveTailCount = 0;
            for (int index = 0; index < releasedCount; index++)
            {
                int frameID = _liveTailFrameIDs[index];
                uint raw = _liveTailRawValues[index];
                _liveTailFrameIDs[index] = -1;
                _liveTailPayloads[index] = null;
                Record(frameID, raw);
            }
            return true;
        }

        private OutboundHistoryDisposition RecordLiveTail(
            int frameID,
            uint raw)
        {
            int existingIndex = FindLiveTailIndex(frameID);
            if (existingIndex >= 0)
            {
                return _liveTailRawValues[existingIndex] == raw
                    ? OutboundHistoryDisposition.IdempotentDuplicate
                    : OutboundHistoryDisposition.ConflictingDuplicate;
            }
            if (_liveTailCount >= _liveTailFrameIDs.Length)
                return OutboundHistoryDisposition.CapacityExceeded;

            int insertionIndex = _liveTailCount;
            while (insertionIndex > 0 &&
                   _liveTailFrameIDs[insertionIndex - 1] > frameID)
            {
                _liveTailFrameIDs[insertionIndex] =
                    _liveTailFrameIDs[insertionIndex - 1];
                _liveTailRawValues[insertionIndex] =
                    _liveTailRawValues[insertionIndex - 1];
                _liveTailPayloads[insertionIndex] =
                    _liveTailPayloads[insertionIndex - 1];
                insertionIndex--;
            }

            _liveTailFrameIDs[insertionIndex] = frameID;
            _liveTailRawValues[insertionIndex] = raw;
            _liveTailPayloads[insertionIndex] =
                RouteCProtocolCodec.EncodeBusinessInput(raw, frameID);
            _liveTailCount++;
            return OutboundHistoryDisposition.Accepted;
        }

        private void InsertExistingLiveTail(
            int frameID,
            uint raw,
            byte[] payload)
        {
            int insertionIndex = _liveTailCount;
            while (insertionIndex > 0 &&
                   _liveTailFrameIDs[insertionIndex - 1] > frameID)
            {
                _liveTailFrameIDs[insertionIndex] =
                    _liveTailFrameIDs[insertionIndex - 1];
                _liveTailRawValues[insertionIndex] =
                    _liveTailRawValues[insertionIndex - 1];
                _liveTailPayloads[insertionIndex] =
                    _liveTailPayloads[insertionIndex - 1];
                insertionIndex--;
            }

            _liveTailFrameIDs[insertionIndex] = frameID;
            _liveTailRawValues[insertionIndex] = raw;
            _liveTailPayloads[insertionIndex] = payload;
            _liveTailCount++;
        }

        private int FindLiveTailIndex(int frameID)
        {
            for (int index = 0; index < _liveTailCount; index++)
            {
                if (_liveTailFrameIDs[index] == frameID)
                    return index;
                if (_liveTailFrameIDs[index] > frameID)
                    break;
            }
            return -1;
        }

        private bool TryCopyPayload(int frameID, out byte[] payload)
        {
            int liveTailIndex = FindLiveTailIndex(frameID);
            byte[] source;
            if (liveTailIndex >= 0)
            {
                source = _liveTailPayloads[liveTailIndex];
            }
            else
            {
                int index = frameID < 0 ? -1 : frameID % _frameIDs.Length;
                if (index < 0 ||
                    !IsRetained(frameID) ||
                    _frameIDs[index] != frameID ||
                    _payloads[index] == null)
                {
                    payload = null;
                    return false;
                }
                source = _payloads[index];
            }

            payload = new byte[source.Length];
            Buffer.BlockCopy(source, 0, payload, 0, source.Length);
            return true;
        }

        private bool IsRetained(int frameID)
        {
            if (frameID < 0 || _latestFrameID < 0 || frameID > _latestFrameID)
                return false;

            int earliestRetainedFrame = Math.Max(
                0,
                _latestFrameID - _frameIDs.Length + 1);
            return frameID >= earliestRetainedFrame;
        }
    }
}

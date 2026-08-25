using System;
using System.Collections.Generic;

namespace FrameSyncDemo
{
    public sealed class RawUdpPacketSequenceWindow
    {
        private readonly Dictionary<uint, byte[]> _payloads =
            new Dictionary<uint, byte[]>();
        private bool _hasHighest;
        private uint _highest;

        public RawUdpSequenceDisposition Observe(
            uint sequence,
            byte[] immutablePayload)
        {
            if (immutablePayload == null)
                throw new ArgumentNullException(nameof(immutablePayload));

            if (!_hasHighest)
            {
                _hasHighest = true;
                _highest = sequence;
                Store(sequence, immutablePayload);
                return RawUdpSequenceDisposition.Accepted;
            }

            RawUdpSerialOrder order = RawUdpSerialNumber.Compare(
                sequence,
                _highest);
            if (order == RawUdpSerialOrder.Ambiguous)
                return RawUdpSequenceDisposition.Ambiguous;
            if (order == RawUdpSerialOrder.Equal)
                return CompareStored(sequence, immutablePayload);
            if (order == RawUdpSerialOrder.Newer)
            {
                _highest = sequence;
                PurgeOutsideWindow();
                Store(sequence, immutablePayload);
                return RawUdpSequenceDisposition.Accepted;
            }

            uint lag = unchecked(_highest - sequence);
            if (lag >= RouteCProtocolConstants.RawPacketSequenceWindowSize)
                return RawUdpSequenceDisposition.TooOld;
            if (_payloads.ContainsKey(sequence))
                return CompareStored(sequence, immutablePayload);

            Store(sequence, immutablePayload);
            return RawUdpSequenceDisposition.Accepted;
        }

        private RawUdpSequenceDisposition CompareStored(
            uint sequence,
            byte[] payload)
        {
            byte[] stored = _payloads[sequence];
            if (stored.Length != payload.Length)
                return RawUdpSequenceDisposition.Conflict;
            for (int index = 0; index < stored.Length; index++)
            {
                if (stored[index] != payload[index])
                    return RawUdpSequenceDisposition.Conflict;
            }

            return RawUdpSequenceDisposition.Duplicate;
        }

        private void Store(uint sequence, byte[] payload)
        {
            _payloads[sequence] = (byte[])payload.Clone();
        }

        private void PurgeOutsideWindow()
        {
            var expired = new List<uint>();
            foreach (uint sequence in _payloads.Keys)
            {
                RawUdpSerialOrder order = RawUdpSerialNumber.Compare(
                    sequence,
                    _highest);
                if (order != RawUdpSerialOrder.Equal &&
                    (order != RawUdpSerialOrder.Older ||
                     unchecked(_highest - sequence) >=
                     RouteCProtocolConstants.RawPacketSequenceWindowSize))
                {
                    expired.Add(sequence);
                }
            }

            for (int index = 0; index < expired.Count; index++)
                _payloads.Remove(expired[index]);
        }
    }
}

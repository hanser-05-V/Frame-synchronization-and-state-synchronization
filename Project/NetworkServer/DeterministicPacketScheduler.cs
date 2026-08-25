using System;
using System.Collections.Generic;
using FrameSyncDemo;

namespace FrameSyncServer
{
    public sealed class DeterministicPacketScheduler
    {
        public readonly struct Delivery
        {
            internal Delivery(
                uint raw,
                int frameID,
                int senderIndex,
                long senderSequence,
                int copyIndex,
                long enqueuedAtMs,
                long dueAtMs,
                DeterministicNetworkFaultModel.Decision decision)
            {
                Raw = raw;
                FrameID = frameID;
                SenderIndex = senderIndex;
                SenderSequence = senderSequence;
                CopyIndex = copyIndex;
                EnqueuedAtMs = enqueuedAtMs;
                DueAtMs = dueAtMs;
                Decision = decision;
            }

            public uint Raw { get; }
            public int FrameID { get; }
            public int SenderIndex { get; }
            public long SenderSequence { get; }
            public int CopyIndex { get; }
            public long EnqueuedAtMs { get; }
            public long DueAtMs { get; }
            public DeterministicNetworkFaultModel.Decision Decision { get; }
        }

        private sealed class DeliveryComparer : IComparer<Delivery>
        {
            public int Compare(Delivery left, Delivery right)
            {
                int comparison = left.DueAtMs.CompareTo(right.DueAtMs);
                if (comparison != 0)
                    return comparison;

                comparison = left.SenderIndex.CompareTo(right.SenderIndex);
                if (comparison != 0)
                    return comparison;

                comparison = left.SenderSequence.CompareTo(right.SenderSequence);
                if (comparison != 0)
                    return comparison;

                comparison = left.CopyIndex.CompareTo(right.CopyIndex);
                if (comparison != 0)
                    return comparison;

                comparison = left.FrameID.CompareTo(right.FrameID);
                if (comparison != 0)
                    return comparison;

                return left.Raw.CompareTo(right.Raw);
            }
        }

        private readonly NetworkLabProfile _profile;
        private readonly SortedSet<Delivery> _deliveries =
            new SortedSet<Delivery>(new DeliveryComparer());
        private readonly long[] _reliableBarrierDueMs =
            { long.MinValue, long.MinValue };

        public DeterministicPacketScheduler(NetworkLabProfile profile)
        {
            _profile = profile;
        }

        public int Count => _deliveries.Count;
        public long NextDueTimestampMs =>
            _deliveries.Count == 0 ? -1L : _deliveries.Min.DueAtMs;

        public DeterministicNetworkFaultModel.Decision Schedule(
            long enqueuedAtMs,
            int senderIndex,
            long senderSequence,
            uint raw,
            int frameID)
        {
            if (enqueuedAtMs < 0)
                throw new ArgumentOutOfRangeException(nameof(enqueuedAtMs));

            DeterministicNetworkFaultModel.Decision decision =
                DeterministicNetworkFaultModel.Decide(
                    _profile,
                    senderIndex,
                    senderSequence,
                    frameID,
                    raw);
            long dueAtMs = checked(enqueuedAtMs + decision.EffectiveDelayMs);
            if (!decision.ApplicationReordered)
            {
                dueAtMs = Math.Max(
                    dueAtMs,
                    _reliableBarrierDueMs[senderIndex]);
                _reliableBarrierDueMs[senderIndex] = Math.Max(
                    _reliableBarrierDueMs[senderIndex],
                    dueAtMs);
            }

            AddDelivery(
                raw,
                frameID,
                senderIndex,
                senderSequence,
                0,
                enqueuedAtMs,
                dueAtMs,
                decision);
            if (decision.Duplicated)
            {
                AddDelivery(
                    raw,
                    frameID,
                    senderIndex,
                    senderSequence,
                    1,
                    enqueuedAtMs,
                    dueAtMs,
                    decision);
            }

            return decision;
        }

        public bool TryDequeueDue(long nowMs, out Delivery delivery)
        {
            if (_deliveries.Count == 0 || _deliveries.Min.DueAtMs > nowMs)
            {
                delivery = default;
                return false;
            }

            delivery = _deliveries.Min;
            _deliveries.Remove(delivery);
            return true;
        }

        private void AddDelivery(
            uint raw,
            int frameID,
            int senderIndex,
            long senderSequence,
            int copyIndex,
            long enqueuedAtMs,
            long dueAtMs,
            DeterministicNetworkFaultModel.Decision decision)
        {
            var delivery = new Delivery(
                raw,
                frameID,
                senderIndex,
                senderSequence,
                copyIndex,
                enqueuedAtMs,
                dueAtMs,
                decision);
            if (!_deliveries.Add(delivery))
            {
                throw new InvalidOperationException(
                    "Duplicate scheduler identity for sender sequence and copy index.");
            }
        }
    }
}

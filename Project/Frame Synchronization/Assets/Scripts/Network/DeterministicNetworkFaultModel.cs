using System;
using System.Globalization;

namespace FrameSyncDemo
{
    public static class DeterministicNetworkFaultModel
    {
        public readonly struct Decision
        {
            internal Decision(
                int effectiveDelayMs,
                int jitterOffsetMs,
                bool applicationReordered,
                bool duplicated,
                bool lossRecovered)
            {
                EffectiveDelayMs = effectiveDelayMs;
                JitterOffsetMs = jitterOffsetMs;
                ApplicationReordered = applicationReordered;
                Duplicated = duplicated;
                LossRecovered = lossRecovered;
            }

            public int EffectiveDelayMs { get; }
            public int JitterOffsetMs { get; }
            public bool ApplicationReordered { get; }
            public bool Duplicated { get; }
            public bool LossRecovered { get; }

            public string ToCanonicalTraceLine(
                int senderIndex,
                long senderSequence,
                int frameID,
                uint raw)
            {
                ValidatePacketIdentity(
                    senderIndex,
                    senderSequence,
                    frameID);
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "sender={0} sequence={1} frame={2} raw=0x{3:X8} " +
                    "delayMs={4} jitterMs={5} reorder={6} duplicate={7} " +
                    "lossRecovered={8}",
                    senderIndex,
                    senderSequence,
                    frameID,
                    raw,
                    EffectiveDelayMs,
                    JitterOffsetMs,
                    ApplicationReordered ? 1 : 0,
                    Duplicated ? 1 : 0,
                    LossRecovered ? 1 : 0);
            }
        }

        public static Decision Decide(
            in NetworkLabProfile profile,
            int senderIndex,
            long senderSequence,
            int frameID,
            uint raw)
        {
            ValidatePacketIdentity(senderIndex, senderSequence, frameID);

            int jitterOffset = CalculateJitter(
                profile,
                senderIndex,
                senderSequence,
                frameID,
                raw);
            bool reordered = RollPercent(
                profile.ApplicationReorderPercent,
                Mix(profile, senderIndex, senderSequence, frameID, raw, 0xA341316Cu));
            bool duplicated = RollPercent(
                profile.DuplicatePercent,
                Mix(profile, senderIndex, senderSequence, frameID, raw, 0xC8013EA4u));
            bool lossRecovered = RollPercent(
                profile.RecoveredLossPercent,
                Mix(profile, senderIndex, senderSequence, frameID, raw, 0xAD90777Du));

            long effectiveDelay = Math.Max(
                0L,
                (long)profile.BaseDelayMs + jitterOffset);
            if (reordered)
                effectiveDelay += profile.ReorderExtraDelayMs;
            if (lossRecovered)
                effectiveDelay += profile.LossRecoveryDelayMs;
            if (effectiveDelay > int.MaxValue)
                throw new OverflowException("Network fault delay exceeded Int32 range.");

            return new Decision(
                (int)effectiveDelay,
                jitterOffset,
                reordered,
                duplicated,
                lossRecovered);
        }

        private static void ValidatePacketIdentity(
            int senderIndex,
            long senderSequence,
            int frameID)
        {
            if (senderIndex < 0 || senderIndex > 1)
                throw new ArgumentOutOfRangeException(nameof(senderIndex));
            if (senderSequence < 0)
                throw new ArgumentOutOfRangeException(nameof(senderSequence));
            if (frameID < 0)
                throw new ArgumentOutOfRangeException(nameof(frameID));
        }

        private static int CalculateJitter(
            in NetworkLabProfile profile,
            int senderIndex,
            long senderSequence,
            int frameID,
            uint raw)
        {
            if (profile.JitterMs == 0)
                return 0;

            uint span = checked((uint)(profile.JitterMs * 2 + 1));
            uint roll = Mix(
                profile,
                senderIndex,
                senderSequence,
                frameID,
                raw,
                0x9E3779B9u);
            return (int)(roll % span) - profile.JitterMs;
        }

        private static bool RollPercent(int percent, uint roll)
        {
            if (percent <= 0)
                return false;
            if (percent >= 100)
                return true;

            return roll % 100u < percent;
        }

        private static uint Mix(
            in NetworkLabProfile profile,
            int senderIndex,
            long senderSequence,
            int frameID,
            uint raw,
            uint salt)
        {
            unchecked
            {
                uint hash = 2166136261u;
                Add(ref hash, profile.Seed);
                Add(ref hash, (uint)senderIndex);
                Add(ref hash, (uint)senderSequence);
                Add(ref hash, (uint)(senderSequence >> 32));
                Add(ref hash, (uint)frameID);
                Add(ref hash, raw);
                Add(ref hash, salt);
                hash ^= hash >> 16;
                hash *= 0x7FEB352Du;
                hash ^= hash >> 15;
                hash *= 0x846CA68Bu;
                hash ^= hash >> 16;
                return hash;
            }
        }

        private static void Add(ref uint hash, uint value)
        {
            unchecked
            {
                hash ^= value;
                hash *= 16777619u;
            }
        }
    }
}

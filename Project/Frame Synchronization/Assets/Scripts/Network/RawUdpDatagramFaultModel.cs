using System;

namespace FrameSyncDemo
{
    public static class RawUdpDatagramFaultModel
    {
        private const uint DelaySalt = 0xD1A5E123u;
        private const uint JitterSalt = 0x91E10DA5u;
        private const uint DropSalt = 0xD40F19B3u;
        private const uint ReorderSalt = 0xA341316Cu;
        private const uint DuplicateSalt = 0xC8013EA4u;

        public static RawUdpDatagramFaultDecision Decide(
            in RawUdpDatagramFaultProfile profile,
            byte direction,
            ulong sessionFingerprint,
            RouteCMessageType messageType,
            int copyIndex,
            uint sendIdentity)
        {
            ValidateIdentity(direction, messageType, copyIndex);

            uint delayIdentity = Mix(
                in profile,
                direction,
                sessionFingerprint,
                messageType,
                copyIndex,
                sendIdentity,
                DelaySalt);
            int jitterOffset = CalculateJitter(
                in profile,
                direction,
                sessionFingerprint,
                messageType,
                copyIndex,
                sendIdentity);
            bool dropped = RollPercent(
                profile.DropPercent,
                Mix(in profile, direction, sessionFingerprint, messageType,
                    copyIndex, sendIdentity, DropSalt));
            bool reordered = RollPercent(
                profile.ReorderPercent,
                Mix(in profile, direction, sessionFingerprint, messageType,
                    copyIndex, sendIdentity, ReorderSalt));
            bool duplicated = RollPercent(
                profile.DuplicatePercent,
                Mix(in profile, direction, sessionFingerprint, messageType,
                    copyIndex, sendIdentity, DuplicateSalt));

            int reorderExtra = reordered
                ? profile.ReorderExtraDelayMs
                : 0;
            long effectiveDueTimeOffset = Math.Max(
                0L,
                (long)profile.DelayMs + jitterOffset + reorderExtra);
            if (effectiveDueTimeOffset > int.MaxValue)
                throw new OverflowException("Raw UDP due-time offset overflowed.");

            GC.KeepAlive(delayIdentity);
            return new RawUdpDatagramFaultDecision(
                dropped,
                (int)effectiveDueTimeOffset,
                jitterOffset,
                reorderExtra,
                dropped ? 0 : duplicated ? 2 : 1);
        }

        private static int CalculateJitter(
            in RawUdpDatagramFaultProfile profile,
            byte direction,
            ulong sessionFingerprint,
            RouteCMessageType messageType,
            int copyIndex,
            uint sendIdentity)
        {
            if (profile.JitterMs == 0)
                return 0;

            uint span = checked((uint)(profile.JitterMs * 2 + 1));
            uint roll = Mix(
                in profile,
                direction,
                sessionFingerprint,
                messageType,
                copyIndex,
                sendIdentity,
                JitterSalt);
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
            in RawUdpDatagramFaultProfile profile,
            byte direction,
            ulong sessionFingerprint,
            RouteCMessageType messageType,
            int copyIndex,
            uint sendIdentity,
            uint salt)
        {
            unchecked
            {
                uint hash = 2166136261u;
                Add(ref hash, profile.Seed);
                Add(ref hash, direction);
                Add(ref hash, (uint)sessionFingerprint);
                Add(ref hash, (uint)(sessionFingerprint >> 32));
                Add(ref hash, (uint)messageType);
                Add(ref hash, (uint)copyIndex);
                Add(ref hash, sendIdentity);
                Add(ref hash,
                    messageType == RouteCMessageType.RawInput
                        ? 0x49504B54u
                        : 0x4354524Cu);
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

        private static void ValidateIdentity(
            byte direction,
            RouteCMessageType messageType,
            int copyIndex)
        {
            if (direction > 1)
                throw new ArgumentOutOfRangeException(nameof(direction));
            if (messageType < RouteCMessageType.RawHello ||
                messageType > RouteCMessageType.RawFault)
            {
                throw new ArgumentOutOfRangeException(nameof(messageType));
            }
            if (copyIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(copyIndex));
        }
    }
}

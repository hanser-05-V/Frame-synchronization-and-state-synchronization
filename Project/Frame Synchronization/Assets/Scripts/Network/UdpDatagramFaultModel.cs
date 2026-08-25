using System;

namespace FrameSyncDemo
{
    public static class UdpDatagramFaultModel
    {
        private const uint DelaySalt = 0xD1A5E123u;
        private const uint DropSalt = 0xD40F19B3u;
        private const uint ReorderSalt = 0xA341316Cu;
        private const uint DuplicateSalt = 0xC8013EA4u;

        public static UdpDatagramFaultDecision Decide(
            in UdpDatagramFaultProfile profile,
            UdpDatagramDirection direction,
            RouteCSessionId senderSessionId,
            ulong datagramSequence,
            byte[] datagram,
            out UdpDatagramDecisionTraceEntry traceEntry)
        {
            ValidateDirection(direction);
            TryReadTraceIdentity(
                datagram,
                out RouteCMessageType? messageType,
                out byte? kcpCommand,
                out uint? kcpSequence);

            uint delayRoll = Mix(
                in profile,
                direction,
                senderSessionId,
                datagramSequence,
                messageType,
                kcpCommand,
                kcpSequence,
                DelaySalt);
            int jitterOffset = CalculateJitter(in profile, delayRoll);
            bool dropped = RollPercent(
                profile.DropPercent,
                Mix(
                    in profile,
                    direction,
                    senderSessionId,
                    datagramSequence,
                    messageType,
                    kcpCommand,
                    kcpSequence,
                    DropSalt));
            bool reordered = RollPercent(
                profile.ReorderPercent,
                Mix(
                    in profile,
                    direction,
                    senderSessionId,
                    datagramSequence,
                    messageType,
                    kcpCommand,
                    kcpSequence,
                    ReorderSalt));
            bool duplicated = RollPercent(
                profile.DuplicatePercent,
                Mix(
                    in profile,
                    direction,
                    senderSessionId,
                    datagramSequence,
                    messageType,
                    kcpCommand,
                    kcpSequence,
                    DuplicateSalt));
            int reorderExtraDelayMs = reordered
                ? profile.ReorderExtraDelayMs
                : 0;
            long effectiveDelayMs = Math.Max(
                0L,
                (long)profile.DelayMs + jitterOffset + reorderExtraDelayMs);
            if (effectiveDelayMs > int.MaxValue)
                throw new OverflowException("UDP datagram due-time offset overflowed.");

            var decision = new UdpDatagramFaultDecision(
                dropped,
                (int)effectiveDelayMs,
                jitterOffset,
                reorderExtraDelayMs,
                dropped ? 0 : duplicated ? 2 : 1);
            traceEntry = new UdpDatagramDecisionTraceEntry(
                direction,
                RawUdpSessionFingerprint.Format(senderSessionId),
                datagramSequence,
                messageType,
                kcpCommand,
                kcpSequence,
                in decision);
            return decision;
        }

        private static void TryReadTraceIdentity(
            byte[] datagram,
            out RouteCMessageType? messageType,
            out byte? kcpCommand,
            out uint? kcpSequence)
        {
            int count = datagram == null ? 0 : datagram.Length;
            if (!RouteCProtocolCodec.TryDecode(
                    datagram,
                    count,
                    out RouteCProtocolMessage message,
                    out _))
            {
                messageType = null;
                kcpCommand = null;
                kcpSequence = null;
                return;
            }

            messageType = message.MessageType;
            if (message.MessageType == RouteCMessageType.KcpData &&
                RouteCKcpHeader.TryReadCommandAndSequence(
                    message.Payload,
                    out byte command,
                    out uint sequence))
            {
                kcpCommand = command;
                kcpSequence = sequence;
            }
            else
            {
                kcpCommand = null;
                kcpSequence = null;
            }
        }

        private static int CalculateJitter(
            in UdpDatagramFaultProfile profile,
            uint roll)
        {
            if (profile.JitterMs == 0)
                return 0;

            uint span = checked((uint)(profile.JitterMs * 2 + 1));
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
            in UdpDatagramFaultProfile profile,
            UdpDatagramDirection direction,
            RouteCSessionId sessionId,
            ulong datagramSequence,
            RouteCMessageType? messageType,
            byte? kcpCommand,
            uint? kcpSequence,
            uint salt)
        {
            unchecked
            {
                uint hash = 2166136261u;
                Add(ref hash, profile.Seed);
                Add(ref hash, (uint)direction);
                Add(ref hash, (uint)sessionId.High);
                Add(ref hash, (uint)(sessionId.High >> 32));
                Add(ref hash, (uint)sessionId.Low);
                Add(ref hash, (uint)(sessionId.Low >> 32));
                Add(ref hash, (uint)datagramSequence);
                Add(ref hash, (uint)(datagramSequence >> 32));
                Add(ref hash, messageType.HasValue ? (uint)messageType.Value : 0u);
                Add(ref hash, kcpCommand.HasValue ? kcpCommand.Value : 0u);
                Add(ref hash, kcpSequence.HasValue ? kcpSequence.Value : 0u);
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

        private static void ValidateDirection(UdpDatagramDirection direction)
        {
            if (direction != UdpDatagramDirection.ClientToServer &&
                direction != UdpDatagramDirection.ServerToClient)
            {
                throw new ArgumentOutOfRangeException(nameof(direction));
            }
        }
    }
}

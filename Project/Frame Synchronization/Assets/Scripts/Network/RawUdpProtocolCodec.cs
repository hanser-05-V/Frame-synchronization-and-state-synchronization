using System;

namespace FrameSyncDemo
{
    public static class RawUdpProtocolCodec
    {
        private const byte UnassignedPlayerIndex = 255;

        public static byte[] EncodeHello(byte[] clientNonce)
        {
            RequireNonce(clientNonce, nameof(clientNonce));
            return (byte[])clientNonce.Clone();
        }

        public static bool TryDecodeHello(byte[] payload, out byte[] clientNonce)
        {
            clientNonce = null;
            if (payload == null || payload.Length != RouteCProtocolConstants.NonceSize)
                return false;
            clientNonce = (byte[])payload.Clone();
            return true;
        }

        public static byte[] EncodeWelcome(
            byte[] echoNonce,
            byte playerIndex,
            byte windowSize)
        {
            RequireNonce(echoNonce, nameof(echoNonce));
            RequirePlayerIndex(playerIndex, nameof(playerIndex), false);
            RequireWindowSize(windowSize, nameof(windowSize));

            var payload = new byte[RouteCProtocolConstants.NonceSize + 2];
            Buffer.BlockCopy(echoNonce, 0, payload, 0, echoNonce.Length);
            payload[16] = playerIndex;
            payload[17] = windowSize;
            return payload;
        }

        public static bool TryDecodeWelcome(
            byte[] payload,
            out byte[] echoNonce,
            out byte playerIndex,
            out byte windowSize)
        {
            echoNonce = null;
            playerIndex = 0;
            windowSize = 0;
            if (payload == null || payload.Length != 18)
                return false;

            byte decodedPlayerIndex = payload[16];
            byte decodedWindowSize = payload[17];
            if (!IsPlayerIndex(decodedPlayerIndex, false) ||
                !IsWindowSize(decodedWindowSize))
            {
                return false;
            }

            var decodedNonce = new byte[RouteCProtocolConstants.NonceSize];
            Buffer.BlockCopy(payload, 0, decodedNonce, 0, decodedNonce.Length);
            echoNonce = decodedNonce;
            playerIndex = decodedPlayerIndex;
            windowSize = decodedWindowSize;
            return true;
        }

        public static byte[] EncodeReady()
        {
            return new byte[0];
        }

        public static bool TryDecodeReady(byte[] payload)
        {
            return payload != null && payload.Length == 0;
        }

        public static byte[] EncodeStart(int canonicalStartFrame)
        {
            if (canonicalStartFrame != 0)
                throw new ArgumentOutOfRangeException(nameof(canonicalStartFrame));

            var payload = new byte[4];
            WriteInt32BigEndian(payload, 0, canonicalStartFrame);
            return payload;
        }

        public static bool TryDecodeStart(
            byte[] payload,
            out int canonicalStartFrame)
        {
            canonicalStartFrame = 0;
            if (payload == null || payload.Length != 4)
                return false;

            int decodedStartFrame = ReadInt32BigEndian(payload, 0);
            if (decodedStartFrame != 0)
                return false;

            canonicalStartFrame = decodedStartFrame;
            return true;
        }

        public static byte[] EncodeInput(
            byte playerIndex,
            byte negotiatedWindowSize,
            uint packetSequence,
            int latestFrameID,
            RawUdpInputEntry[] entries)
        {
            RequirePlayerIndex(playerIndex, nameof(playerIndex), false);
            RequireWindowSize(negotiatedWindowSize, nameof(negotiatedWindowSize));
            if (latestFrameID < 0)
                throw new ArgumentOutOfRangeException(nameof(latestFrameID));
            if (entries == null)
                throw new ArgumentNullException(nameof(entries));

            int expectedCount = GetExpectedInputCount(
                negotiatedWindowSize,
                latestFrameID);
            if (entries.Length != expectedCount)
            {
                throw new ArgumentException(
                    "Raw input entries must contain the full negotiated window.",
                    nameof(entries));
            }

            int firstFrameID = latestFrameID - expectedCount + 1;
            for (int index = 0; index < entries.Length; index++)
            {
                if (entries[index].FrameID != firstFrameID + index)
                {
                    throw new ArgumentException(
                        "Raw input entries must be consecutive and end at LatestFrameID.",
                        nameof(entries));
                }
            }

            var payload = new byte[
                RouteCProtocolConstants.RawInputHeaderSize +
                (entries.Length * RouteCProtocolConstants.BusinessInputSize)];
            payload[0] = playerIndex;
            payload[1] = (byte)entries.Length;
            WriteUInt32BigEndian(payload, 4, packetSequence);
            WriteInt32BigEndian(payload, 8, latestFrameID);
            for (int index = 0; index < entries.Length; index++)
            {
                int offset = RouteCProtocolConstants.RawInputHeaderSize +
                             (index * RouteCProtocolConstants.BusinessInputSize);
                WriteUInt32LittleEndian(payload, offset, entries[index].Raw);
                WriteInt32LittleEndian(payload, offset + 4, entries[index].FrameID);
            }

            return payload;
        }

        public static bool TryDecodeInput(
            byte[] payload,
            byte negotiatedWindowSize,
            out RawUdpInputWindow window,
            out RouteCProtocolDropReason reason)
        {
            window = null;
            reason = RouteCProtocolDropReason.InvalidRawInputPayload;
            if (!IsWindowSize(negotiatedWindowSize))
            {
                reason = RouteCProtocolDropReason.InvalidRawWindowSize;
                return false;
            }
            if (payload == null)
                return false;
            if (payload.Length > RouteCProtocolConstants.RawMaximumInputPayloadSize)
            {
                reason = RouteCProtocolDropReason.RawDatagramTooLarge;
                return false;
            }
            if (payload.Length <
                RouteCProtocolConstants.RawInputHeaderSize +
                RouteCProtocolConstants.BusinessInputSize)
            {
                return false;
            }

            byte playerIndex = payload[0];
            int inputCount = payload[1];
            if (!IsPlayerIndex(playerIndex, false) ||
                inputCount < RouteCProtocolConstants.RawMinimumWindowSize ||
                inputCount > RouteCProtocolConstants.RawMaximumWindowSize ||
                payload[2] != 0 || payload[3] != 0)
            {
                return false;
            }

            int expectedLength = RouteCProtocolConstants.RawInputHeaderSize +
                                 (inputCount * RouteCProtocolConstants.BusinessInputSize);
            if (payload.Length != expectedLength)
                return false;

            uint packetSequence = ReadUInt32BigEndian(payload, 4);
            int latestFrameID = ReadInt32BigEndian(payload, 8);
            if (latestFrameID < 0 ||
                inputCount != GetExpectedInputCount(negotiatedWindowSize, latestFrameID))
            {
                return false;
            }

            int firstFrameID = latestFrameID - inputCount + 1;
            var entries = new RawUdpInputEntry[inputCount];
            for (int index = 0; index < inputCount; index++)
            {
                int offset = RouteCProtocolConstants.RawInputHeaderSize +
                             (index * RouteCProtocolConstants.BusinessInputSize);
                uint raw = ReadUInt32LittleEndian(payload, offset);
                int frameID = ReadInt32LittleEndian(payload, offset + 4);
                if (frameID != firstFrameID + index)
                    return false;
                entries[index] = new RawUdpInputEntry(raw, frameID);
            }

            window = new RawUdpInputWindow(
                playerIndex,
                packetSequence,
                latestFrameID,
                entries);
            reason = RouteCProtocolDropReason.None;
            return true;
        }

        public static byte[] EncodeFault(in RawUdpFault fault)
        {
            RequireFault(fault, nameof(fault));
            var payload = new byte[12];
            WriteUInt16BigEndian(payload, 0, (ushort)fault.Reason);
            payload[2] = fault.PlayerIndex;
            WriteInt32BigEndian(payload, 4, fault.FrameID);
            WriteInt32BigEndian(payload, 8, fault.ObservedLatestFrameID);
            return payload;
        }

        public static bool TryDecodeFault(byte[] payload, out RawUdpFault fault)
        {
            fault = default;
            if (payload == null || payload.Length != 12 || payload[3] != 0)
                return false;

            ushort reasonValue = ReadUInt16BigEndian(payload, 0);
            byte playerIndex = payload[2];
            if (!IsFaultReason(reasonValue) || !IsPlayerIndex(playerIndex, true))
                return false;

            var decodedFault = new RawUdpFault(
                (RawUdpFaultReason)reasonValue,
                playerIndex,
                ReadInt32BigEndian(payload, 4),
                ReadInt32BigEndian(payload, 8));
            if (!IsFaultShapeValid(decodedFault))
                return false;

            fault = decodedFault;
            return true;
        }

        public static bool TryValidateOuterMessage(
            RouteCProtocolMessage message,
            bool receivedByServer,
            out RouteCProtocolDropReason reason)
        {
            reason = RouteCProtocolDropReason.WrongMessageDirection;
            if (message == null)
                return false;

            bool isMatchFull = false;
            if (message.MessageType == RouteCMessageType.RawFault)
            {
                if (!TryDecodeFault(message.Payload, out RawUdpFault fault))
                {
                    reason = RouteCProtocolDropReason.InvalidRawControlPayload;
                    return false;
                }
                isMatchFull = fault.Reason == RawUdpFaultReason.MatchFull;
            }

            if (!IsAllowedDirection(message.MessageType, receivedByServer, isMatchFull))
                return false;

            bool requiresZeroSession =
                message.MessageType == RouteCMessageType.RawHello || isMatchFull;
            if (requiresZeroSession)
            {
                if (!message.SessionId.IsZero)
                {
                    reason = RouteCProtocolDropReason.SessionNotFound;
                    return false;
                }
                if (message.Generation != 0u)
                {
                    reason = RouteCProtocolDropReason.StaleGeneration;
                    return false;
                }
            }
            else
            {
                if (message.SessionId.IsZero)
                {
                    reason = RouteCProtocolDropReason.SessionNotFound;
                    return false;
                }
                if (message.Generation != RouteCProtocolConstants.RawGeneration)
                {
                    reason = RouteCProtocolDropReason.StaleGeneration;
                    return false;
                }
            }

            reason = RouteCProtocolDropReason.None;
            return true;
        }

        private static bool IsAllowedDirection(
            RouteCMessageType messageType,
            bool receivedByServer,
            bool isMatchFull)
        {
            switch (messageType)
            {
                case RouteCMessageType.RawHello:
                case RouteCMessageType.RawReady:
                    return receivedByServer;
                case RouteCMessageType.RawWelcome:
                case RouteCMessageType.RawStart:
                    return !receivedByServer;
                case RouteCMessageType.RawInput:
                    return true;
                case RouteCMessageType.RawFault:
                    return !isMatchFull || !receivedByServer;
                default:
                    return false;
            }
        }

        private static int GetExpectedInputCount(byte windowSize, int latestFrameID)
        {
            long availableFrames = (long)latestFrameID + 1L;
            return (int)Math.Min(windowSize, availableFrames);
        }

        private static void RequireNonce(byte[] nonce, string parameterName)
        {
            if (nonce == null)
                throw new ArgumentNullException(parameterName);
            if (nonce.Length != RouteCProtocolConstants.NonceSize)
                throw new ArgumentException("Nonce must be exactly 16 bytes.", parameterName);
        }

        private static void RequireWindowSize(byte windowSize, string parameterName)
        {
            if (!IsWindowSize(windowSize))
                throw new ArgumentOutOfRangeException(parameterName);
        }

        private static void RequirePlayerIndex(
            byte playerIndex,
            string parameterName,
            bool allowUnassigned)
        {
            if (!IsPlayerIndex(playerIndex, allowUnassigned))
                throw new ArgumentOutOfRangeException(parameterName);
        }

        private static void RequireFault(RawUdpFault fault, string parameterName)
        {
            if (!IsFaultReason((ushort)fault.Reason) ||
                !IsPlayerIndex(fault.PlayerIndex, true) ||
                !IsFaultShapeValid(fault))
            {
                throw new ArgumentException("Invalid Raw UDP fault.", parameterName);
            }
        }

        private static bool IsWindowSize(byte windowSize)
        {
            return windowSize >= RouteCProtocolConstants.RawMinimumWindowSize &&
                   windowSize <= RouteCProtocolConstants.RawMaximumWindowSize;
        }

        private static bool IsPlayerIndex(byte playerIndex, bool allowUnassigned)
        {
            return playerIndex <= 1 ||
                   (allowUnassigned && playerIndex == UnassignedPlayerIndex);
        }

        private static bool IsFaultReason(ushort reason)
        {
            return reason >= (ushort)RawUdpFaultReason.HandshakeTimeout &&
                   reason <= (ushort)RawUdpFaultReason.MatchFull;
        }

        private static bool IsFaultShapeValid(RawUdpFault fault)
        {
            if (fault.Reason != RawUdpFaultReason.MatchFull)
                return true;
            return fault.PlayerIndex == UnassignedPlayerIndex &&
                   fault.FrameID == -1 &&
                   fault.ObservedLatestFrameID == -1;
        }

        private static ushort ReadUInt16BigEndian(byte[] value, int offset)
        {
            return (ushort)((value[offset] << 8) | value[offset + 1]);
        }

        private static uint ReadUInt32BigEndian(byte[] value, int offset)
        {
            return ((uint)value[offset] << 24) |
                   ((uint)value[offset + 1] << 16) |
                   ((uint)value[offset + 2] << 8) |
                   value[offset + 3];
        }

        private static int ReadInt32BigEndian(byte[] value, int offset)
        {
            return unchecked((int)ReadUInt32BigEndian(value, offset));
        }

        private static uint ReadUInt32LittleEndian(byte[] value, int offset)
        {
            return value[offset] |
                   ((uint)value[offset + 1] << 8) |
                   ((uint)value[offset + 2] << 16) |
                   ((uint)value[offset + 3] << 24);
        }

        private static int ReadInt32LittleEndian(byte[] value, int offset)
        {
            return unchecked((int)ReadUInt32LittleEndian(value, offset));
        }

        private static void WriteUInt16BigEndian(byte[] value, int offset, ushort number)
        {
            value[offset] = (byte)(number >> 8);
            value[offset + 1] = (byte)number;
        }

        private static void WriteUInt32BigEndian(byte[] value, int offset, uint number)
        {
            value[offset] = (byte)(number >> 24);
            value[offset + 1] = (byte)(number >> 16);
            value[offset + 2] = (byte)(number >> 8);
            value[offset + 3] = (byte)number;
        }

        private static void WriteInt32BigEndian(byte[] value, int offset, int number)
        {
            WriteUInt32BigEndian(value, offset, unchecked((uint)number));
        }

        private static void WriteUInt32LittleEndian(byte[] value, int offset, uint number)
        {
            value[offset] = (byte)number;
            value[offset + 1] = (byte)(number >> 8);
            value[offset + 2] = (byte)(number >> 16);
            value[offset + 3] = (byte)(number >> 24);
        }

        private static void WriteInt32LittleEndian(byte[] value, int offset, int number)
        {
            WriteUInt32LittleEndian(value, offset, unchecked((uint)number));
        }
    }
}

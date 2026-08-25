using System;

namespace FrameSyncDemo
{
    public static class RouteCProtocolCodec
    {
        private static readonly byte[] Magic = { 0x52, 0x43, 0x46, 0x32 };

        public static byte[] Encode(
            RouteCMessageType messageType,
            RouteCSessionId sessionId,
            uint generation,
            byte[] payload)
        {
            if (!IsKnownMessageType((byte)messageType))
                throw new ArgumentOutOfRangeException(nameof(messageType));
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));
            if (payload.Length > RouteCProtocolConstants.KcpMtu)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(payload),
                    "Route C payload exceeds the UDP envelope limit.");
            }
            if (messageType == RouteCMessageType.KcpData &&
                payload.Length < RouteCProtocolConstants.KcpHeaderSize)
            {
                throw new ArgumentException(
                    "KCP payload is shorter than the fixed KCP header.",
                    nameof(payload));
            }

            var datagram = new byte[RouteCProtocolConstants.HeaderSize + payload.Length];
            Buffer.BlockCopy(Magic, 0, datagram, 0, Magic.Length);
            datagram[4] = RouteCProtocolConstants.Version;
            datagram[5] = (byte)messageType;
            WriteUInt16BigEndian(datagram, 6, checked((ushort)payload.Length));
            WriteUInt64BigEndian(datagram, 8, sessionId.High);
            WriteUInt64BigEndian(datagram, 16, sessionId.Low);
            WriteUInt32BigEndian(datagram, 24, generation);
            Buffer.BlockCopy(
                payload,
                0,
                datagram,
                RouteCProtocolConstants.HeaderSize,
                payload.Length);
            return datagram;
        }

        public static bool TryDecode(
            byte[] datagram,
            int count,
            out RouteCProtocolMessage message,
            out RouteCProtocolDropReason reason)
        {
            message = null;
            if (count < 0 || (datagram != null && count > datagram.Length))
            {
                reason = RouteCProtocolDropReason.DatagramLengthOutOfRange;
                return false;
            }
            if (datagram == null || count < RouteCProtocolConstants.HeaderSize)
            {
                reason = RouteCProtocolDropReason.HeaderTooShort;
                return false;
            }
            if (count > RouteCProtocolConstants.MaximumDatagramSize)
            {
                reason = RouteCProtocolDropReason.DatagramTooLarge;
                return false;
            }
            for (int index = 0; index < Magic.Length; index++)
            {
                if (datagram[index] != Magic[index])
                {
                    reason = RouteCProtocolDropReason.InvalidMagic;
                    return false;
                }
            }
            if (datagram[4] != RouteCProtocolConstants.Version)
            {
                reason = RouteCProtocolDropReason.UnsupportedVersion;
                return false;
            }
            if (!IsKnownMessageType(datagram[5]))
            {
                reason = RouteCProtocolDropReason.UnknownMessageType;
                return false;
            }

            int payloadLength = ReadUInt16BigEndian(datagram, 6);
            if (payloadLength > RouteCProtocolConstants.KcpMtu)
            {
                reason = RouteCProtocolDropReason.PayloadTooLarge;
                return false;
            }
            if (count != RouteCProtocolConstants.HeaderSize + payloadLength)
            {
                reason = RouteCProtocolDropReason.PayloadLengthMismatch;
                return false;
            }
            if ((RouteCMessageType)datagram[5] == RouteCMessageType.KcpData &&
                payloadLength < RouteCProtocolConstants.KcpHeaderSize)
            {
                reason = RouteCProtocolDropReason.KcpPayloadTooShort;
                return false;
            }

            var payload = new byte[payloadLength];
            Buffer.BlockCopy(
                datagram,
                RouteCProtocolConstants.HeaderSize,
                payload,
                0,
                payloadLength);
            message = new RouteCProtocolMessage(
                (RouteCMessageType)datagram[5],
                new RouteCSessionId(
                    ReadUInt64BigEndian(datagram, 8),
                    ReadUInt64BigEndian(datagram, 16)),
                ReadUInt32BigEndian(datagram, 24),
                payload);
            reason = RouteCProtocolDropReason.None;
            return true;
        }

        public static byte[] EncodeBusinessInput(uint raw, int frameID)
        {
            var bytes = new byte[RouteCProtocolConstants.BusinessInputSize];
            WriteUInt32LittleEndian(bytes, 0, raw);
            WriteUInt32LittleEndian(bytes, 4, unchecked((uint)frameID));
            return bytes;
        }

        public static bool TryDecodeBusinessInput(
            byte[] bytes,
            out uint raw,
            out int frameID)
        {
            if (bytes == null ||
                bytes.Length != RouteCProtocolConstants.BusinessInputSize)
            {
                raw = 0u;
                frameID = 0;
                return false;
            }

            raw = ReadUInt32LittleEndian(bytes, 0);
            frameID = unchecked((int)ReadUInt32LittleEndian(bytes, 4));
            return true;
        }

        public static byte[] EncodeInitialHello(byte[] clientNonce)
        {
            RequireExactLength(
                clientNonce,
                RouteCProtocolConstants.NonceSize,
                nameof(clientNonce));
            var payload = new byte[1 + RouteCProtocolConstants.NonceSize];
            payload[0] = 0;
            Buffer.BlockCopy(clientNonce, 0, payload, 1, clientNonce.Length);
            return payload;
        }

        public static bool TryDecodeInitialHello(
            byte[] payload,
            out byte[] clientNonce)
        {
            if (payload == null ||
                payload.Length != 1 + RouteCProtocolConstants.NonceSize ||
                payload[0] != 0)
            {
                clientNonce = null;
                return false;
            }

            clientNonce = CopyRange(
                payload,
                1,
                RouteCProtocolConstants.NonceSize);
            return true;
        }

        public static byte[] EncodeReconnectHello(
            byte[] newClientNonce,
            byte[] oldReconnectToken)
        {
            RequireExactLength(
                newClientNonce,
                RouteCProtocolConstants.NonceSize,
                nameof(newClientNonce));
            RequireExactLength(
                oldReconnectToken,
                RouteCProtocolConstants.ReconnectTokenSize,
                nameof(oldReconnectToken));
            var payload = new byte[
                1 +
                RouteCProtocolConstants.NonceSize +
                RouteCProtocolConstants.ReconnectTokenSize];
            payload[0] = 1;
            Buffer.BlockCopy(
                newClientNonce,
                0,
                payload,
                1,
                newClientNonce.Length);
            Buffer.BlockCopy(
                oldReconnectToken,
                0,
                payload,
                1 + RouteCProtocolConstants.NonceSize,
                oldReconnectToken.Length);
            return payload;
        }

        public static bool TryDecodeReconnectHello(
            byte[] payload,
            out byte[] newClientNonce,
            out byte[] oldReconnectToken)
        {
            int expectedLength =
                1 +
                RouteCProtocolConstants.NonceSize +
                RouteCProtocolConstants.ReconnectTokenSize;
            if (payload == null ||
                payload.Length != expectedLength ||
                payload[0] != 1)
            {
                newClientNonce = null;
                oldReconnectToken = null;
                return false;
            }

            newClientNonce = CopyRange(
                payload,
                1,
                RouteCProtocolConstants.NonceSize);
            oldReconnectToken = CopyRange(
                payload,
                1 + RouteCProtocolConstants.NonceSize,
                RouteCProtocolConstants.ReconnectTokenSize);
            return true;
        }

        public static byte[] EncodeWelcome(
            byte[] echoNonce,
            byte playerIndex,
            uint conversation,
            byte[] newReconnectToken,
            int heartbeatMs,
            int timeoutMs,
            bool resumeRequired)
        {
            RequireExactLength(
                echoNonce,
                RouteCProtocolConstants.NonceSize,
                nameof(echoNonce));
            RequireExactLength(
                newReconnectToken,
                RouteCProtocolConstants.ReconnectTokenSize,
                nameof(newReconnectToken));
            var payload = new byte[62];
            Buffer.BlockCopy(echoNonce, 0, payload, 0, echoNonce.Length);
            payload[16] = playerIndex;
            WriteUInt32BigEndian(payload, 17, conversation);
            Buffer.BlockCopy(
                newReconnectToken,
                0,
                payload,
                21,
                newReconnectToken.Length);
            WriteInt32BigEndian(payload, 53, heartbeatMs);
            WriteInt32BigEndian(payload, 57, timeoutMs);
            payload[61] = resumeRequired ? (byte)1 : (byte)0;
            return payload;
        }

        public static bool TryDecodeWelcome(
            byte[] payload,
            out byte[] echoNonce,
            out byte playerIndex,
            out uint conversation,
            out byte[] newReconnectToken,
            out int heartbeatMs,
            out int timeoutMs,
            out bool resumeRequired)
        {
            if (payload == null ||
                payload.Length != 62 ||
                payload[61] > 1)
            {
                echoNonce = null;
                playerIndex = 0;
                conversation = 0u;
                newReconnectToken = null;
                heartbeatMs = 0;
                timeoutMs = 0;
                resumeRequired = false;
                return false;
            }

            echoNonce = CopyRange(payload, 0, RouteCProtocolConstants.NonceSize);
            playerIndex = payload[16];
            conversation = ReadUInt32BigEndian(payload, 17);
            newReconnectToken = CopyRange(
                payload,
                21,
                RouteCProtocolConstants.ReconnectTokenSize);
            heartbeatMs = ReadInt32BigEndian(payload, 53);
            timeoutMs = ReadInt32BigEndian(payload, 57);
            resumeRequired = payload[61] == 1;
            return true;
        }

        public static byte[] EncodeReady(
            int lastContiguousRemoteFrameID,
            int earliestRecoverableCanonicalFrame,
            int latestLocalFrameID)
        {
            var payload = new byte[12];
            WriteInt32BigEndian(payload, 0, lastContiguousRemoteFrameID);
            WriteInt32BigEndian(payload, 4, earliestRecoverableCanonicalFrame);
            WriteInt32BigEndian(payload, 8, latestLocalFrameID);
            return payload;
        }

        public static bool TryDecodeReady(
            byte[] payload,
            out int lastContiguousRemoteFrameID,
            out int earliestRecoverableCanonicalFrame,
            out int latestLocalFrameID)
        {
            if (payload == null || payload.Length != 12)
            {
                lastContiguousRemoteFrameID = 0;
                earliestRecoverableCanonicalFrame = 0;
                latestLocalFrameID = 0;
                return false;
            }

            lastContiguousRemoteFrameID = ReadInt32BigEndian(payload, 0);
            earliestRecoverableCanonicalFrame = ReadInt32BigEndian(payload, 4);
            latestLocalFrameID = ReadInt32BigEndian(payload, 8);
            return true;
        }

        public static byte[] EncodeStart(int canonicalStartFrame)
        {
            var payload = new byte[4];
            WriteInt32BigEndian(payload, 0, canonicalStartFrame);
            return payload;
        }

        public static bool TryDecodeStart(
            byte[] payload,
            out int canonicalStartFrame)
        {
            if (payload == null || payload.Length != 4)
            {
                canonicalStartFrame = 0;
                return false;
            }

            canonicalStartFrame = ReadInt32BigEndian(payload, 0);
            return true;
        }

        public static byte[] EncodeResumeProbe(byte[] resumeAttemptID)
        {
            RequireExactLength(
                resumeAttemptID,
                RouteCProtocolConstants.NonceSize,
                nameof(resumeAttemptID));
            return (byte[])resumeAttemptID.Clone();
        }

        public static bool TryDecodeResumeProbe(
            byte[] payload,
            out byte[] resumeAttemptID)
        {
            if (payload == null ||
                payload.Length != RouteCProtocolConstants.NonceSize)
            {
                resumeAttemptID = null;
                return false;
            }

            resumeAttemptID = (byte[])payload.Clone();
            return true;
        }

        public static byte[] EncodeResumeState(
            byte[] resumeAttemptID,
            int lastContiguousRemoteFrameID,
            int earliestRecoverableCanonicalFrame,
            int latestLocalFrameID)
        {
            RequireExactLength(
                resumeAttemptID,
                RouteCProtocolConstants.NonceSize,
                nameof(resumeAttemptID));
            var payload = new byte[28];
            Buffer.BlockCopy(resumeAttemptID, 0, payload, 0, 16);
            WriteInt32BigEndian(payload, 16, lastContiguousRemoteFrameID);
            WriteInt32BigEndian(payload, 20, earliestRecoverableCanonicalFrame);
            WriteInt32BigEndian(payload, 24, latestLocalFrameID);
            return payload;
        }

        public static bool TryDecodeResumeState(
            byte[] payload,
            out byte[] resumeAttemptID,
            out int lastContiguousRemoteFrameID,
            out int earliestRecoverableCanonicalFrame,
            out int latestLocalFrameID)
        {
            if (payload == null || payload.Length != 28)
            {
                resumeAttemptID = null;
                lastContiguousRemoteFrameID = 0;
                earliestRecoverableCanonicalFrame = 0;
                latestLocalFrameID = 0;
                return false;
            }

            resumeAttemptID = CopyRange(payload, 0, 16);
            lastContiguousRemoteFrameID = ReadInt32BigEndian(payload, 16);
            earliestRecoverableCanonicalFrame = ReadInt32BigEndian(payload, 20);
            latestLocalFrameID = ReadInt32BigEndian(payload, 24);
            return true;
        }

        public static byte[] EncodeResumeAccepted(
            byte[] resumeAttemptID,
            int uploadFrom,
            int uploadThrough,
            int replayFrom,
            int replayThrough,
            int peerFrom,
            int peerThrough)
        {
            RequireExactLength(
                resumeAttemptID,
                RouteCProtocolConstants.NonceSize,
                nameof(resumeAttemptID));
            var payload = new byte[40];
            Buffer.BlockCopy(resumeAttemptID, 0, payload, 0, 16);
            WriteInt32BigEndian(payload, 16, uploadFrom);
            WriteInt32BigEndian(payload, 20, uploadThrough);
            WriteInt32BigEndian(payload, 24, replayFrom);
            WriteInt32BigEndian(payload, 28, replayThrough);
            WriteInt32BigEndian(payload, 32, peerFrom);
            WriteInt32BigEndian(payload, 36, peerThrough);
            return payload;
        }

        public static bool TryDecodeResumeAccepted(
            byte[] payload,
            out byte[] resumeAttemptID,
            out int uploadFrom,
            out int uploadThrough,
            out int replayFrom,
            out int replayThrough,
            out int peerFrom,
            out int peerThrough)
        {
            if (payload == null || payload.Length != 40)
            {
                resumeAttemptID = null;
                uploadFrom = 0;
                uploadThrough = 0;
                replayFrom = 0;
                replayThrough = 0;
                peerFrom = 0;
                peerThrough = 0;
                return false;
            }

            resumeAttemptID = CopyRange(payload, 0, 16);
            uploadFrom = ReadInt32BigEndian(payload, 16);
            uploadThrough = ReadInt32BigEndian(payload, 20);
            replayFrom = ReadInt32BigEndian(payload, 24);
            replayThrough = ReadInt32BigEndian(payload, 28);
            peerFrom = ReadInt32BigEndian(payload, 32);
            peerThrough = ReadInt32BigEndian(payload, 36);
            return true;
        }

        public static byte[] EncodeResumeComplete(
            byte[] resumeAttemptID,
            int uploadedLocalThrough,
            int receivedRemoteThrough)
        {
            RequireExactLength(
                resumeAttemptID,
                RouteCProtocolConstants.NonceSize,
                nameof(resumeAttemptID));
            var payload = new byte[24];
            Buffer.BlockCopy(resumeAttemptID, 0, payload, 0, 16);
            WriteInt32BigEndian(payload, 16, uploadedLocalThrough);
            WriteInt32BigEndian(payload, 20, receivedRemoteThrough);
            return payload;
        }

        public static bool TryDecodeResumeComplete(
            byte[] payload,
            out byte[] resumeAttemptID,
            out int uploadedLocalThrough,
            out int receivedRemoteThrough)
        {
            if (payload == null || payload.Length != 24)
            {
                resumeAttemptID = null;
                uploadedLocalThrough = 0;
                receivedRemoteThrough = 0;
                return false;
            }

            resumeAttemptID = CopyRange(payload, 0, 16);
            uploadedLocalThrough = ReadInt32BigEndian(payload, 16);
            receivedRemoteThrough = ReadInt32BigEndian(payload, 20);
            return true;
        }

        public static byte[] EncodeResumeRejected(
            byte[] resumeAttemptID,
            ushort reason)
        {
            RequireExactLength(
                resumeAttemptID,
                RouteCProtocolConstants.NonceSize,
                nameof(resumeAttemptID));
            var payload = new byte[18];
            Buffer.BlockCopy(resumeAttemptID, 0, payload, 0, 16);
            WriteUInt16BigEndian(payload, 16, reason);
            return payload;
        }

        public static bool TryDecodeResumeRejected(
            byte[] payload,
            out byte[] resumeAttemptID,
            out ushort reason)
        {
            if (payload == null || payload.Length != 18)
            {
                resumeAttemptID = null;
                reason = 0;
                return false;
            }

            resumeAttemptID = CopyRange(payload, 0, 16);
            reason = (ushort)ReadUInt16BigEndian(payload, 16);
            return true;
        }

        private static bool IsKnownMessageType(byte value)
        {
            return value >= (byte)RouteCMessageType.Hello &&
                   value <= (byte)RouteCMessageType.RawFault;
        }

        private static void RequireExactLength(
            byte[] value,
            int expectedLength,
            string parameterName)
        {
            if (value == null)
                throw new ArgumentNullException(parameterName);
            if (value.Length != expectedLength)
            {
                throw new ArgumentException(
                    "Control value has an invalid fixed length.",
                    parameterName);
            }
        }

        private static byte[] CopyRange(byte[] source, int offset, int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }

        private static void WriteUInt16BigEndian(
            byte[] buffer,
            int offset,
            ushort value)
        {
            buffer[offset] = (byte)(value >> 8);
            buffer[offset + 1] = (byte)value;
        }

        private static int ReadUInt16BigEndian(byte[] buffer, int offset)
        {
            return (buffer[offset] << 8) | buffer[offset + 1];
        }

        private static void WriteUInt32BigEndian(
            byte[] buffer,
            int offset,
            uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static uint ReadUInt32BigEndian(byte[] buffer, int offset)
        {
            return ((uint)buffer[offset] << 24) |
                   ((uint)buffer[offset + 1] << 16) |
                   ((uint)buffer[offset + 2] << 8) |
                   buffer[offset + 3];
        }

        private static void WriteInt32BigEndian(
            byte[] buffer,
            int offset,
            int value)
        {
            WriteUInt32BigEndian(buffer, offset, unchecked((uint)value));
        }

        private static int ReadInt32BigEndian(byte[] buffer, int offset)
        {
            return unchecked((int)ReadUInt32BigEndian(buffer, offset));
        }

        private static void WriteUInt32LittleEndian(
            byte[] buffer,
            int offset,
            uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static uint ReadUInt32LittleEndian(byte[] buffer, int offset)
        {
            return buffer[offset] |
                   ((uint)buffer[offset + 1] << 8) |
                   ((uint)buffer[offset + 2] << 16) |
                   ((uint)buffer[offset + 3] << 24);
        }

        private static void WriteUInt64BigEndian(
            byte[] buffer,
            int offset,
            ulong value)
        {
            for (int index = 0; index < 8; index++)
                buffer[offset + index] = (byte)(value >> (56 - (index * 8)));
        }

        private static ulong ReadUInt64BigEndian(byte[] buffer, int offset)
        {
            ulong value = 0UL;
            for (int index = 0; index < 8; index++)
                value = (value << 8) | buffer[offset + index];
            return value;
        }
    }
}

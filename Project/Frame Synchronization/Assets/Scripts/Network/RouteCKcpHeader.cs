namespace FrameSyncDemo
{
    public static class RouteCKcpHeader
    {
        public static bool TryReadConversation(
            byte[] payload,
            out uint conversation)
        {
            if (payload == null ||
                payload.Length < RouteCProtocolConstants.KcpHeaderSize)
            {
                conversation = 0u;
                return false;
            }

            conversation = payload[0] |
                           ((uint)payload[1] << 8) |
                           ((uint)payload[2] << 16) |
                           ((uint)payload[3] << 24);
            return true;
        }

        public static bool TryReadCommandAndSequence(
            byte[] payload,
            out byte command,
            out uint sequence)
        {
            command = 0;
            sequence = 0u;
            if (payload == null ||
                payload.Length < RouteCProtocolConstants.KcpHeaderSize)
            {
                return false;
            }

            byte parsedCommand = payload[4];
            if (parsedCommand != 0x51 &&
                parsedCommand != 0x52 &&
                parsedCommand != 0x53 &&
                parsedCommand != 0x54)
            {
                return false;
            }

            uint declaredPayloadLength = payload[20] |
                                         ((uint)payload[21] << 8) |
                                         ((uint)payload[22] << 16) |
                                         ((uint)payload[23] << 24);
            if (declaredPayloadLength >
                (uint)(payload.Length - RouteCProtocolConstants.KcpHeaderSize))
            {
                return false;
            }

            command = parsedCommand;
            sequence = payload[12] |
                       ((uint)payload[13] << 8) |
                       ((uint)payload[14] << 16) |
                       ((uint)payload[15] << 24);
            return true;
        }
    }
}

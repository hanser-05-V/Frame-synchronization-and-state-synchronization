using System.Security.Cryptography;
using FrameSyncDemo;

namespace FrameSyncServer
{
    public sealed class CryptoRandomSource
    {
        public RouteCSessionId CreateSessionId()
        {
            while (true)
            {
                byte[] bytes = CreateBytes(RouteCProtocolConstants.SessionIdSize);
                var sessionId = new RouteCSessionId(
                    ReadUInt64(bytes, 0),
                    ReadUInt64(bytes, 8));
                if (!sessionId.IsZero)
                {
                    return sessionId;
                }
            }
        }

        public byte[] CreateNonce()
        {
            return CreateBytes(RouteCProtocolConstants.NonceSize);
        }

        public byte[] CreateReconnectToken()
        {
            return CreateBytes(RouteCProtocolConstants.ReconnectTokenSize);
        }

        public uint CreateConversation()
        {
            while (true)
            {
                byte[] bytes = CreateBytes(sizeof(uint));
                uint conversation = bytes[0] |
                                    ((uint)bytes[1] << 8) |
                                    ((uint)bytes[2] << 16) |
                                    ((uint)bytes[3] << 24);
                if (conversation != 0u)
                {
                    return conversation;
                }
            }
        }

        private static byte[] CreateBytes(int count)
        {
            var bytes = new byte[count];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }
            return bytes;
        }

        private static ulong ReadUInt64(byte[] bytes, int offset)
        {
            ulong value = 0UL;
            for (int index = 0; index < sizeof(ulong); index++)
            {
                value = (value << 8) | bytes[offset + index];
            }
            return value;
        }
    }
}

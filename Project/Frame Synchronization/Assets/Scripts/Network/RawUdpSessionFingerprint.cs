using System.Security.Cryptography;

namespace FrameSyncDemo
{
    public static class RawUdpSessionFingerprint
    {
        public static string Format(RouteCSessionId sessionId)
        {
            if (sessionId.IsZero)
                return "none";

            var sessionBytes = new byte[16];
            WriteUInt64BigEndian(sessionBytes, 0, sessionId.High);
            WriteUInt64BigEndian(sessionBytes, 8, sessionId.Low);
            byte[] hash;
            using (SHA256 sha256 = SHA256.Create())
                hash = sha256.ComputeHash(sessionBytes);

            var characters = new char[16];
            for (int index = 0; index < 8; index++)
            {
                byte value = hash[index];
                characters[index * 2] = ToHex(value >> 4);
                characters[(index * 2) + 1] = ToHex(value & 0x0F);
            }
            return new string(characters);
        }

        private static void WriteUInt64BigEndian(
            byte[] destination,
            int offset,
            ulong value)
        {
            for (int index = 0; index < 8; index++)
            {
                destination[offset + index] =
                    (byte)(value >> ((7 - index) * 8));
            }
        }

        private static char ToHex(int value)
        {
            return (char)(value < 10 ? '0' + value : 'A' + value - 10);
        }
    }
}

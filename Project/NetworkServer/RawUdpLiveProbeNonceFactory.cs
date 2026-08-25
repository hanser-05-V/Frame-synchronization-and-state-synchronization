namespace FrameSyncServer
{
    public static class RawUdpLiveProbeNonceFactory
    {
        public static byte[] CreatePlayerZero()
        {
            return Create(16);
        }

        public static byte[] CreatePlayerOne()
        {
            return Create(48);
        }

        private static byte[] Create(byte first)
        {
            var nonce = new byte[16];
            for (int index = 0; index < nonce.Length; index++)
                nonce[index] = (byte)(first + index);
            return nonce;
        }
    }
}

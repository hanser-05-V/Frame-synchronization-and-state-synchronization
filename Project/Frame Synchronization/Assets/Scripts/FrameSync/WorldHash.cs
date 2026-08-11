namespace FrameSyncDemo
{
    /// <summary>
    /// 完整同步世界的稳定 Hash。输入顺序与 FrameSnapshot 字段顺序保持一致。
    /// </summary>
    public static class WorldHash
    {
        public const int SchemaVersion = 2;
        public const string AlgorithmName = "FNV1A64";

        private const ulong OffsetBasis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        public static ulong Compute(FrameSnapshot snapshot)
        {
            return Compute(snapshot, snapshot.frameID);
        }

        public static ulong Compute(FrameSnapshot snapshot, int canonicalFrameID)
        {
            ulong hash = OffsetBasis;
            AddInt32(ref hash, SchemaVersion);
            AddInt32(ref hash, canonicalFrameID);

            AddInt32(ref hash, snapshot.player1X._raw);
            AddInt32(ref hash, snapshot.player1Y._raw);
            AddInt32(ref hash, snapshot.player1Z._raw);
            AddInt32(ref hash, snapshot.player2X._raw);
            AddInt32(ref hash, snapshot.player2Y._raw);
            AddInt32(ref hash, snapshot.player2Z._raw);

            AddInt32(ref hash, snapshot.player1FacingX._raw);
            AddInt32(ref hash, snapshot.player1FacingY._raw);
            AddInt32(ref hash, snapshot.player1FacingZ._raw);
            AddInt32(ref hash, snapshot.player2FacingX._raw);
            AddInt32(ref hash, snapshot.player2FacingY._raw);
            AddInt32(ref hash, snapshot.player2FacingZ._raw);

            AddInt32(ref hash, snapshot.player1State);
            AddInt32(ref hash, snapshot.player2State);
            AddInt32(ref hash, snapshot.player1HasBall ? 1 : 0);
            AddInt32(ref hash, snapshot.player2HasBall ? 1 : 0);

            AddInt32(ref hash, snapshot.ballPosX._raw);
            AddInt32(ref hash, snapshot.ballPosY._raw);
            AddInt32(ref hash, snapshot.ballPosZ._raw);
            AddInt32(ref hash, snapshot.ballVelX._raw);
            AddInt32(ref hash, snapshot.ballVelY._raw);
            AddInt32(ref hash, snapshot.ballVelZ._raw);
            AddInt32(ref hash, snapshot.ballState);
            AddInt32(ref hash, snapshot.ballHolder);
            return hash;
        }

        private static void AddInt32(ref ulong hash, int value)
        {
            uint raw = unchecked((uint)value);
            unchecked
            {
                for (int shift = 0; shift < 32; shift += 8)
                {
                    hash ^= (byte)(raw >> shift);
                    hash *= Prime;
                }
            }
        }
    }
}

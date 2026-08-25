namespace FrameSyncDemo
{
    /// <summary>
    /// 完整同步世界的稳定 Hash。字段顺序与 SchemaVersion 2 保持一致。
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
            SimulationWorldState world = snapshot.ToWorld();
            return Compute(world, canonicalFrameID);
        }

        public static ulong Compute(
            in SimulationWorldState world,
            int canonicalFrameID)
        {
            ulong hash = OffsetBasis;
            AddInt32(ref hash, SchemaVersion);
            AddInt32(ref hash, canonicalFrameID);

            AddInt32(ref hash, world.player0.position.x._raw);
            AddInt32(ref hash, world.player0.position.y._raw);
            AddInt32(ref hash, world.player0.position.z._raw);
            AddInt32(ref hash, world.player1.position.x._raw);
            AddInt32(ref hash, world.player1.position.y._raw);
            AddInt32(ref hash, world.player1.position.z._raw);

            AddInt32(ref hash, world.player0.facing.x._raw);
            AddInt32(ref hash, world.player0.facing.y._raw);
            AddInt32(ref hash, world.player0.facing.z._raw);
            AddInt32(ref hash, world.player1.facing.x._raw);
            AddInt32(ref hash, world.player1.facing.y._raw);
            AddInt32(ref hash, world.player1.facing.z._raw);

            AddInt32(ref hash, world.player0.state);
            AddInt32(ref hash, world.player1.state);
            AddInt32(ref hash, world.player0.hasBall ? 1 : 0);
            AddInt32(ref hash, world.player1.hasBall ? 1 : 0);

            AddInt32(ref hash, world.ball.position.x._raw);
            AddInt32(ref hash, world.ball.position.y._raw);
            AddInt32(ref hash, world.ball.position.z._raw);
            AddInt32(ref hash, world.ball.velocity.x._raw);
            AddInt32(ref hash, world.ball.velocity.y._raw);
            AddInt32(ref hash, world.ball.velocity.z._raw);
            AddInt32(ref hash, world.ball.state);
            AddInt32(ref hash, world.ball.holderPlayerIndex);
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

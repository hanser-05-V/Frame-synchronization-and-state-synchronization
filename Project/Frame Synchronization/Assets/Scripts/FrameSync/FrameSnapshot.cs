namespace FrameSyncDemo
{
    /// <summary>
    /// 帧快照 — 记录某一帧所有玩家的位置+球状态，用于回滚恢复
    /// 阶段1扩展：球员位置增加Y轴 + 球数据 + 玩家状态
    /// </summary>
    public struct FrameSnapshot
    {
        public int frameID;

        // 玩家位置（XZ平面）
        public FixedInt player1X;
        public FixedInt player1Z;
        public FixedInt player2X;
        public FixedInt player2Z;

        // 玩家状态（阶段1扩展）
        public int player1State;        // PlayerEntity.EState
        public int player2State;
        public bool player1HasBall;
        public bool player2HasBall;

        // 球数据（阶段1扩展）
        public FixedInt ballPosX;
        public FixedInt ballPosY;
        public FixedInt ballPosZ;
        public FixedInt ballVelX;
        public FixedInt ballVelY;
        public FixedInt ballVelZ;
        public int ballState;           // BallEntity.EState
        public int ballHolder;          // 持有者索引

        public bool IsValid => frameID >= 0;
    }

    /// <summary>
    /// 快照环形缓冲区 — 用 frameID % capacity 定位
    /// </summary>
    public class SnapshotBuffer
    {
        private FrameSnapshot[] _snapshots;
        private int _capacity;

        public SnapshotBuffer(int capacity = 256)
        {
            _capacity = capacity;
            _snapshots = new FrameSnapshot[capacity];
            for (int i = 0; i < capacity; i++)
            {
                _snapshots[i] = new FrameSnapshot { frameID = -1 };
            }
        }

        public void AddSnapshot(FrameSnapshot snapshot)
        {
            int index = snapshot.frameID % _capacity;
            _snapshots[index] = snapshot;
        }

        public FrameSnapshot GetSnapshot(int frameID)
        {
            return _snapshots[frameID % _capacity];
        }

        public bool HasSnapshot(int frameID)
        {
            if (frameID < 0) return false;
            return _snapshots[frameID % _capacity].frameID == frameID;
        }
    }
}

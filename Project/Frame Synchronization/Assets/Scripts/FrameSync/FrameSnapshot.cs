namespace FrameSyncDemo
{
    /// <summary>
    /// 帧快照 — 记录某一帧所有玩家的位置，用于回滚恢复
    /// </summary>
    public struct FrameSnapshot
    {
        public int frameID;
        public FixedInt player1X;
        public FixedInt player1Y; // Y = Z轴（Unity中Z为纵深）
        public FixedInt player2X;
        public FixedInt player2Y;
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

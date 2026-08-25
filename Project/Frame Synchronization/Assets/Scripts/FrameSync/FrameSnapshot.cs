namespace FrameSyncDemo
{
    /// <summary>
    /// 某一逻辑帧的规范同步世界。旧字段名称仅作为同一份 world 数据的兼容访问器。
    /// </summary>
    public struct FrameSnapshot
    {
        public SimulationWorldState world;

        public int frameID
        {
            get => world.frameID;
            set => world.frameID = value;
        }

        public FixedInt player1X
        {
            get => world.player0.position.x;
            set => world.player0.position.x = value;
        }

        public FixedInt player1Y
        {
            get => world.player0.position.y;
            set => world.player0.position.y = value;
        }

        public FixedInt player1Z
        {
            get => world.player0.position.z;
            set => world.player0.position.z = value;
        }

        public FixedInt player2X
        {
            get => world.player1.position.x;
            set => world.player1.position.x = value;
        }

        public FixedInt player2Y
        {
            get => world.player1.position.y;
            set => world.player1.position.y = value;
        }

        public FixedInt player2Z
        {
            get => world.player1.position.z;
            set => world.player1.position.z = value;
        }

        public FixedInt player1FacingX
        {
            get => world.player0.facing.x;
            set => world.player0.facing.x = value;
        }

        public FixedInt player1FacingY
        {
            get => world.player0.facing.y;
            set => world.player0.facing.y = value;
        }

        public FixedInt player1FacingZ
        {
            get => world.player0.facing.z;
            set => world.player0.facing.z = value;
        }

        public FixedInt player2FacingX
        {
            get => world.player1.facing.x;
            set => world.player1.facing.x = value;
        }

        public FixedInt player2FacingY
        {
            get => world.player1.facing.y;
            set => world.player1.facing.y = value;
        }

        public FixedInt player2FacingZ
        {
            get => world.player1.facing.z;
            set => world.player1.facing.z = value;
        }

        public int player1State
        {
            get => world.player0.state;
            set => world.player0.state = value;
        }

        public int player2State
        {
            get => world.player1.state;
            set => world.player1.state = value;
        }

        public bool player1HasBall
        {
            get => world.player0.hasBall;
            set => world.player0.hasBall = value;
        }

        public bool player2HasBall
        {
            get => world.player1.hasBall;
            set => world.player1.hasBall = value;
        }

        public FixedInt ballPosX
        {
            get => world.ball.position.x;
            set => world.ball.position.x = value;
        }

        public FixedInt ballPosY
        {
            get => world.ball.position.y;
            set => world.ball.position.y = value;
        }

        public FixedInt ballPosZ
        {
            get => world.ball.position.z;
            set => world.ball.position.z = value;
        }

        public FixedInt ballVelX
        {
            get => world.ball.velocity.x;
            set => world.ball.velocity.x = value;
        }

        public FixedInt ballVelY
        {
            get => world.ball.velocity.y;
            set => world.ball.velocity.y = value;
        }

        public FixedInt ballVelZ
        {
            get => world.ball.velocity.z;
            set => world.ball.velocity.z = value;
        }

        public int ballState
        {
            get => world.ball.state;
            set => world.ball.state = value;
        }

        public int ballHolder
        {
            get => world.ball.holderPlayerIndex;
            set => world.ball.holderPlayerIndex = value;
        }

        public bool IsValid => world.frameID >= 0;

        public static FrameSnapshot FromWorld(in SimulationWorldState state)
        {
            return new FrameSnapshot { world = state };
        }

        public SimulationWorldState ToWorld()
        {
            return world;
        }
    }

    /// <summary>
    /// 快照环形缓冲区 — 用 frameID % capacity 定位。
    /// </summary>
    public class SnapshotBuffer
    {
        private readonly FrameSnapshot[] _snapshots;
        private readonly int _capacity;

        public SnapshotBuffer(int capacity = 256)
        {
            _capacity = capacity;
            _snapshots = new FrameSnapshot[capacity];
            for (int i = 0; i < capacity; i++)
                _snapshots[i] = new FrameSnapshot { frameID = -1 };
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
            if (frameID < 0)
                return false;
            return _snapshots[frameID % _capacity].frameID == frameID;
        }
    }
}

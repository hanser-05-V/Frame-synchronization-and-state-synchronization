using UnityEngine;

namespace FrameSyncDemo
{
    /// <summary>
    /// Ring-buffered execution log. It records the values the frame engine ran
    /// for diagnostics and presentation only; FrameInputLedger remains the sole
    /// authority for input truth, replay, and stable actual-input reads.
    /// </summary>
    public class FrameBuffer
    {
        public const int CAPACITY = 256;

        /// <summary>一帧中所有玩家的输入集合</summary>  帧ID 玩家数量 玩家输入录入
        [System.Serializable]
        public struct Frame
        {
            public int frameID;
            public int playerCount;
            public FrameInput[] inputs;

            public Frame(int id, int count)
            {
                frameID = id;
                playerCount = count;
                inputs = new FrameInput[count];
            }

            public override string ToString()
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"Frame#{frameID} players={playerCount}");
                for (int i = 0; i < playerCount; i++)
                {
                    sb.Append($" [{i}]{inputs[i]}");
                }
                return sb.ToString();
            }
        }

        private Frame[] _buffer;
        private int _maxWrittenFrame = -1;
        private int _lastReadFrame = -1;

        public int MaxWrittenFrame => _maxWrittenFrame; //写指针（写到了第几帧）
        public int LastReadFrame => _lastReadFrame;   //读指针（消费到了第几帧）
        public int UsedCount => System.Math.Max(0, _maxWrittenFrame - _lastReadFrame + 1); //以缓冲帧
        public int FreeCount => CAPACITY - UsedCount; // 待分配帧

        public FrameBuffer()
        {
            _buffer = new Frame[CAPACITY];
            for (int i = 0; i < CAPACITY; i++)
            {
                _buffer[i] = new Frame(-1, 0); // -1 表示空槽
            }
        }

        /// <summary>写入一帧。返回 false 表示帧号不连续或已满。</summary>
        public bool AddFrame(int frameID, FrameInput[] inputs)
        {
            // 连续性检测
            if (_maxWrittenFrame >= 0 && frameID != _maxWrittenFrame + 1)
            {
                Debug.LogWarning($"[FrameBuffer] 帧号不连续: 期望{_maxWrittenFrame + 1}, 得到{frameID}");
                return false;
            }

            int index = frameID % CAPACITY;
            var frame = new Frame(frameID, inputs.Length);
            frame.inputs = inputs;
            _buffer[index] = frame;
            _maxWrittenFrame = frameID;
            return true;
        }

        /// <summary>读取指定帧。返回 false 表示帧不存在。</summary>
        public bool GetFrame(int frameID, out Frame frame)
        {
            frame = default;
            if (frameID < 0 || frameID > _maxWrittenFrame) return false;

            int index = frameID % CAPACITY;
            var stored = _buffer[index];
            if (stored.frameID != frameID) return false;

            frame = stored;
            _lastReadFrame = frameID;
            return true;
        }

        /// <summary>仅查看不消费（用于对比/回放）</summary>
        // Execution-log inspection only; never use this as replay or input truth.
        public bool PeekFrame(int frameID, out Frame frame)
        {
            frame = default;
            int index = frameID % CAPACITY;
            var stored = _buffer[index];
            if (stored.frameID != frameID) return false;
            frame = stored;
            return true;
        }

        /// <summary>标记一帧已被消费（不访问数据，仅更新读指针）</summary>
        public void MarkRead(int frameID)
        {
            if (frameID > _lastReadFrame)
                _lastReadFrame = frameID;
        }

        /// <summary>标记已消费帧号（外部控制，用于回放等场景）</summary>
        public void SetLastReadFrame(int frameID)
        {
            if (frameID > _lastReadFrame)
                _lastReadFrame = frameID;
        }

        /// <summary>检查某帧是否存在</summary>
        public bool HasFrame(int frameID)
        {
            if (frameID < 0 || frameID > _maxWrittenFrame) return false;
            return _buffer[frameID % CAPACITY].frameID == frameID;
        }

        /// <summary>获取缓冲区状态（用于可视化）</summary>
        public BufferSnapshot GetSnapshot()
        {
            var snapshot = new BufferSnapshot();
            snapshot.capacity = CAPACITY;
            snapshot.maxWritten = _maxWrittenFrame;
            snapshot.lastRead = _lastReadFrame;
            snapshot.slotStates = new byte[CAPACITY]; // 0=空闲, 1=已消费, 2=待消费

            for (int i = 0; i < CAPACITY; i++)
            {
                int fid = _buffer[i].frameID;
                if (fid < 0) continue;
                snapshot.slotStates[i] = (byte)(fid <= _lastReadFrame ? 1 : 2);
            }
            return snapshot;
        }

        /// <summary>清空缓冲区</summary>
        public void Clear()
        {
            for (int i = 0; i < CAPACITY; i++)
            {
                _buffer[i] = new Frame(-1, 0);
            }
            _maxWrittenFrame = -1;
            _lastReadFrame = -1;
        }
    }

    /// <summary>缓冲区状态快照（只读，供可视化模块使用）</summary>
    public struct BufferSnapshot
    {
        public int capacity;
        public int maxWritten;
        public int lastRead;
        public byte[] slotStates; // 0=空闲, 1=已消费, 2=待消费
    }
}

using UnityEngine;

namespace FrameSyncDemo
{
    /// <summary>
    /// 本地预测 + 回滚纠正系统（v5 — 简化正确版）
    ///
    /// 核心逻辑：
    ///   - 无真实数据时 → 用上一次已知的真实Input预测
    ///   - 有真实数据时 → 更新预测基准，不尝试验证（因为对端帧号≠本地帧号）
    ///   - 回滚由 GameController 在数据错位时触发
    ///
    /// 为什么不做帧号匹配验证：
    ///   对端帧号和本地帧号是独立序列，在 200ms 延迟下对端帧0到达时本地已在帧6，
    ///   _predictionHistory 用本地帧号索引，remoteFrameID 查不到对应项。
    ///   正确帧号映射需要双向帧号跟踪，超出了 Demo 范围。
    /// </summary>
    public class PredictionSystem
    {
        private SnapshotBuffer _snapshotBuffer;
        private FrameInput _lastRemoteInput = default;

        public void Init(int capacity = 256)
        {
            _snapshotBuffer = new SnapshotBuffer(capacity);
            _lastRemoteInput = new FrameInput();
        }

        /// <summary>
        /// 预测对手输入
        /// </summary>
        public FrameInput PredictRemote(bool hasRealData, uint realDataRaw)
        {
            if (hasRealData)
            {
                _lastRemoteInput = FrameInput.FromRaw(realDataRaw);
                return _lastRemoteInput;
            }
            else
            {
                return _lastRemoteInput;
            }
        }

        /// <summary>
        /// 为当前帧拍快照
        /// </summary>
        public void TakeSnapshot(int frameID, FixedInt[] posX, FixedInt[] posZ)
        {
            var snap = new FrameSnapshot
            {
                frameID = frameID,
                player1X = posX[0],
                player1Y = posZ[0],
                player2X = posX[1],
                player2Y = posZ[1]
            };
            _snapshotBuffer.AddSnapshot(snap);
        }

        /// <summary>
        /// 从快照恢复方块位置
        /// </summary>
        public bool RestoreSnapshot(int frameID, ref FixedInt[] posX, ref FixedInt[] posZ)
        {
            if (!_snapshotBuffer.HasSnapshot(frameID)) return false;
            var snap = _snapshotBuffer.GetSnapshot(frameID);
            posX[0] = snap.player1X;
            posZ[0] = snap.player1Y;
            posX[1] = snap.player2X;
            posZ[1] = snap.player2Y;
            return true;
        }
    }
}

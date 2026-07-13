using System.Collections.Generic;
using UnityEngine;

namespace FrameSyncDemo
{
    /// <summary>
    /// 本地预测 + 回滚纠正系统（最终版 — FIFO 顺序匹配）
    ///
    /// 由于对方的帧号序列和本地不同，无法做帧号匹配。
    /// 改用 FIFO 顺序匹配：
    ///   - 每帧无数据时 → 记录"帧N用了预测值X"
    ///   - 真实数据 FIFO 到达时 → 弹出最早的未验证预测帧，比对
    ///   - 不同 → 回滚
    ///
    /// 假设：服务器保持顺序转发，FIFO中第N个数据包对应第N个预测帧
    /// 在 33ms 级延迟和固定延迟模式下，这个近似足够精确。
    /// </summary>
    public class PredictionSystem
    {
        private SnapshotBuffer _snapshotBuffer;
        private FrameInput _lastRemoteInput = default;

        // 记录"哪些帧用了预测"，按帧号递增顺序排队
        // 入：ReadInputs 无数据时 Enqueue(frameID)
        // 出：真实数据到达时 Dequeue()，验证对应帧的预测
        private Queue<int> _predictedFrameQueue;
        private Dictionary<int, FrameInput> _predictionHistory; // [frameID → 预测值]

        // 待处理的回滚（在 OnFrameUpdate 末尾消费）
        private int _pendingErrorFrame = -1;
        private uint _correctRemoteRaw;

        // 跳过初始帧验证（首次连接时双方的初始 raw=0 vs 实际输入必然不同）
        private int _framesSkipped;
        private const int SKIP_INITIAL_FRAMES = 5;

        public bool HasPendingRollback => _pendingErrorFrame >= 0;

        public void Init(int capacity = 256)
        {
            _snapshotBuffer = new SnapshotBuffer(capacity);
            _lastRemoteInput = new FrameInput();
            _predictedFrameQueue = new Queue<int>(64);
            _predictionHistory = new Dictionary<int, FrameInput>(capacity);
            _pendingErrorFrame = -1;
            _framesSkipped = 0;
        }

        /// <summary>
        /// 预测对手输入（在 ReadInputs 中调用）
        /// </summary>
        public FrameInput PredictRemote(bool hasRealData, uint realDataRaw, int frameID)
        {
            if (hasRealData)
            {
                FrameInput realInput = FrameInput.FromRaw(realDataRaw);

                // ── 真实数据到达 → 尝试验证最早的未验证预测帧 ──
                if (_framesSkipped >= SKIP_INITIAL_FRAMES && _predictedFrameQueue.Count > 0)
                {
                    int earliestPredictedFrame = _predictedFrameQueue.Dequeue();

                    if (_predictionHistory.TryGetValue(earliestPredictedFrame, out var predictedValue))
                    {
                        _predictionHistory.Remove(earliestPredictedFrame);

                        if (predictedValue._raw != realDataRaw)
                        {
                            //预测错了
                            _pendingErrorFrame = earliestPredictedFrame;
                            _correctRemoteRaw = realDataRaw;
                            Debug.Log($"[PredictionSystem]  帧{earliestPredictedFrame}预测错误: " +
                                $"预测={predictedValue._raw:X8}, 实际={realDataRaw:X8}");
                        }
                        else
                        {
                            Debug.Log($"[PredictionSystem] ✅ 帧{earliestPredictedFrame}预测正确");
                        }
                    }
                }

                _lastRemoteInput = realInput;
                return realInput;
            }
            else
            {
                // ── 无真实数据 → 预测：假设对方和上一帧一样 ──
                _predictionHistory[frameID] = _lastRemoteInput;
                _predictedFrameQueue.Enqueue(frameID);
                _framesSkipped++;
                return _lastRemoteInput;
            }
        }

        /// <summary>
        /// 消费待处理的回滚（在 OnFrameUpdate 末尾调用）
        /// </summary>
        public bool TryConsumeRollback(out int errorFrame, out uint correctRemoteRaw)
        {
            if (_pendingErrorFrame >= 0)
            {
                errorFrame = _pendingErrorFrame;
                correctRemoteRaw = _correctRemoteRaw;
                _pendingErrorFrame = -1;
                return true;
            }
            errorFrame = -1;
            correctRemoteRaw = 0;
            return false;
        }

        /// <summary>
        /// 为当前帧拍快照（供回滚时恢复）
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

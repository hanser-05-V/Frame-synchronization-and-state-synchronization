using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FrameSyncDemo
{
    /// <summary>
    /// 本地预测 + 回滚纠正系统（v8 — FIFO逐个验证）
    ///
    /// 核心逻辑：
    ///   1. 无数据 → 记录预测帧到队列尾部
    ///   2. 有数据 → 弹出队列头部的最早预测帧，对比验证
    ///   3. 错 → 回滚（最早错误帧-1 → 重跑后续全部帧）
    ///   4. 对 → 移除该条预测，下一条真实数据验证下一个预测
    ///
    /// 关键原则：一条真实数据 = 验证一条预测（FIFO 1:1）
    /// </summary>
    public class PredictionSystem
    {
        private SnapshotBuffer _snapshotBuffer;
        private FrameInput _lastRemoteInput = default;

        // 未验证预测帧队列 [localFrameID]
        private Queue<int> _predictedFrameQueue;
        // 预测值存储 [localFrameID → 预测值]
        private Dictionary<int, FrameInput> _predictionHistory;

        // 待处理回滚
        private int _pendingErrorFrame = -1;
        private uint _correctRemoteRaw;

        // 冷启动保护
        private int _realDataArrived;
        private const int SKIP_INITIAL_FRAMES = 5;

        public bool HasPendingRollback => _pendingErrorFrame >= 0;

        public void Init(int capacity = 256)
        {
            _snapshotBuffer = new SnapshotBuffer(capacity);
            _lastRemoteInput = new FrameInput();
            _predictedFrameQueue = new Queue<int>(64);
            _predictionHistory = new Dictionary<int, FrameInput>(capacity);
            _pendingErrorFrame = -1;
            _realDataArrived = 0;
        }

        /// <summary>
        /// 预测对手输入（在 ReadInputs 中调用）
        /// </summary>
        public FrameInput PredictRemote(bool hasRealData, uint realDataRaw, int frameID)
        {
            if (hasRealData)
            {
                FrameInput realInput = FrameInput.FromRaw(realDataRaw);
                _realDataArrived++;

                // FIFO 逐个验证：每条真实数据只验证最早的一条未验证预测
                if (_realDataArrived > SKIP_INITIAL_FRAMES && _predictedFrameQueue.Count > 0)
                {
                    int predictedFrame = _predictedFrameQueue.Dequeue();

                    if (_predictionHistory.TryGetValue(predictedFrame, out var predictedValue))
                    {
                        _predictionHistory.Remove(predictedFrame);

                        if (predictedValue._raw != realDataRaw)
                        {
                            // ❌ 预测错了 → 这个帧及之后的所有预测全部作废
                            _pendingErrorFrame = predictedFrame;

                            // 不是 _lastPredictedFrame 的值，是触发这轮回滚的值
                            _correctRemoteRaw = realDataRaw;

                            // 清空整个队列+字典（后续预测全部被污染）
                            int cleared = _predictedFrameQueue.Count + _predictionHistory.Count;
                            _predictedFrameQueue.Clear();
                            _predictionHistory.Clear();

                            Debug.Log($"[PredictionSystem] ❌ 帧{predictedFrame}预测错误: " +
                                $"预测={predictedValue._raw:X8}, 实际={realDataRaw:X8}, " +
                                $"已清除{cleared}条被污染的预测");
                        }
                        else
                        {
                            Debug.Log($"[PredictionSystem] ✅ 帧{predictedFrame}预测正确");
                        }
                    }
                }

                _lastRemoteInput = realInput;
                return realInput;
            }
            else
            {
                // ── 无数据 → 预测：假设对方和上一帧一样 ──
                _predictionHistory[frameID] = _lastRemoteInput;

                // 只有收到过真实数据后（_lastRemoteInput != 默认值）才入队验证
                // 避免初始 0x00000000 的预测和真实非零数据对比导致必然失败
                if (_realDataArrived > 0)
                    _predictedFrameQueue.Enqueue(frameID);

                return _lastRemoteInput;
            }
        }

        /// <summary>
        /// 消费待处理的回滚
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

using System.Collections.Generic;
using UnityEngine;

namespace FrameSyncDemo
{
    /// <summary>
    /// 本地预测 + 回滚纠正系统（v6 — 真正的预测验证 + 全量回滚）
    ///
    /// 核心逻辑：
    ///   1. 无真实数据时 → 用上一次已知的真实Input预测，记录预测帧号
    ///   2. 真实数据到达时 → 对比最近一次预测，错则标记回滚
    ///   3. 回滚时 → 恢复快照（errorFrame-1），重跑 errorFrame~currentFrame 全部帧
    ///   4. 回滚完成后 → 清空被污染的预测记录，继续正常预测
    ///
    /// 关键设计决策：
    ///   - 只验证最近一次预测（因为后续预测基于同一错误假设，全清）
    ///   - 跳过初始N帧（避免初始值 0x00000000 vs 真实非零值的必然误判）
    ///   - DoRollback 重跑 safeFrame+1..currentFrame 全部区间
    /// </summary>
    public class PredictionSystem
    {
        private SnapshotBuffer _snapshotBuffer;
        private FrameInput _lastRemoteInput = default;

        // 预测记录 [localFrameID → 预测值]
        private Dictionary<int, FrameInput> _predictionHistory;

        // 最近的预测帧（用于验证）
        private int _lastPredictedFrame = -1;
        private FrameInput _lastPredictedValue;

        // 待处理回滚
        private int _pendingErrorFrame = -1;
        private uint _correctRemoteRaw;

        // 跳过初始帧（避免冷启动误判）
        private int _totalFrames;
        private int _realDataArrived;
        private const int SKIP_INITIAL_FRAMES = 10;

        public bool HasPendingRollback => _pendingErrorFrame >= 0;

        public void Init(int capacity = 256)
        {
            _snapshotBuffer = new SnapshotBuffer(capacity);
            _lastRemoteInput = new FrameInput();
            _predictionHistory = new Dictionary<int, FrameInput>(capacity);
            _lastPredictedFrame = -1;
            _pendingErrorFrame = -1;
            _totalFrames = 0;
            _realDataArrived = 0;
        }

        /// <summary>
        /// 预测对手输入（在 ReadInputs 中调用）
        /// </summary>
        /// <param name="hasRealData">是否有真实远程输入</param>
        /// <param name="realDataRaw">真实输入的 raw 值</param>
        /// <param name="frameID">当前正执行的本地帧号</param>
        /// <returns>本轮使用的对手Input（预测值或真实值）</returns>
        public FrameInput PredictRemote(bool hasRealData, uint realDataRaw, int frameID)
        {
            if (hasRealData)
            {
                // ── 真实数据到达 ──
                FrameInput realInput = FrameInput.FromRaw(realDataRaw);
                _realDataArrived++;

                // 验证：最近一次预测帧的值是否和真实数据一致？
                if (_realDataArrived >= SKIP_INITIAL_FRAMES && _lastPredictedFrame >= 0)
                {
                    if (_lastPredictedValue._raw != realDataRaw)
                    {
                        // ❌ 预测错了 → 标记回滚，清空此帧起所有预测记录
                        _pendingErrorFrame = _lastPredictedFrame;
                        _correctRemoteRaw = realDataRaw;

                        // 清除 errorFrame 及之后的所有预测（已经被污染）
                        var toRemove = new List<int>();
                        foreach (var kv in _predictionHistory)
                            if (kv.Key >= _lastPredictedFrame)
                                toRemove.Add(kv.Key);
                        foreach (var key in toRemove)
                            _predictionHistory.Remove(key);

                        Debug.Log($"[PredictionSystem] ❌ 帧{_lastPredictedFrame}预测错误: " +
                            $"预测={_lastPredictedValue._raw:X8}, 实际={realDataRaw:X8}, " +
                            $"已清除{toRemove.Count}条被污染的预测记录");
                    }
                    else
                    {
                        Debug.Log($"[PredictionSystem] ✅ 帧{_lastPredictedFrame}预测正确");
                    }
                    _lastPredictedFrame = -1;
                }

                _lastRemoteInput = realInput;
                return realInput;
            }
            else
            {
                // ── 无数据 → 预测：假设对方输入和上一帧一样 ──
                _predictionHistory[frameID] = _lastRemoteInput;
                _lastPredictedFrame = frameID;
                _lastPredictedValue = _lastRemoteInput;
                _totalFrames++;
                return _lastRemoteInput;
            }
        }

        /// <summary>
        /// 消费待处理的回滚（在 OnFrameUpdate 调用）
        /// </summary>
        public bool TryConsumeRollback(out int errorFrame, out uint correctRemoteRaw)
        {
            if (_pendingErrorFrame >= 0)
            {
                errorFrame = _pendingErrorFrame;
                correctRemoteRaw = _correctRemoteRaw;
                _pendingErrorFrame = -1;
                _lastPredictedFrame = -1;
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

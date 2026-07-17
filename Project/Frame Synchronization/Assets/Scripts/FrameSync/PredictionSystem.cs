using System.Collections.Generic;
using UnityEngine;

namespace FrameSyncDemo
{
    /// <summary>
    /// 本地预测 + 回滚纠正系统（v7 — 全量预测验证）
    ///
    /// 核心逻辑：
    ///   1. 每帧无数据时 → 记录"本帧用了预测值X"
    ///   2. 真实数据到达时 → 遍历所有未验证预测帧，任意一个预测值 ≠ 真实值 → 回滚
    ///   3. 回滚 → 恢复快照（最早错误帧-1），重跑全部后续帧
    ///   4. 跳过前3帧（避免初始 0x00000000 误判）
    ///
    /// v7 改动（vs v6）：
    ///   - 不再只验证 _lastPredictedFrame，改为验证全部 _predictionHistory
    ///   - 找到最早的不匹配预测帧触发回滚
    ///   - SKIP_INITIAL_FRAMES 从 10 降到 3
    ///   - 验证后不移除已验证的预测记录（留给后续帧继续验证）
    /// </summary>
    public class PredictionSystem
    {
        private SnapshotBuffer _snapshotBuffer;
        private FrameInput _lastRemoteInput = default;

        // 所有未验证的预测帧 [localFrameID → 预测值]
        private Dictionary<int, FrameInput> _predictionHistory;

        // 待处理回滚
        private int _pendingErrorFrame = -1;
        private uint _correctRemoteRaw;

        // 冷启动保护
        private int _totalFrames;
        private int _realDataArrived;
        private const int SKIP_INITIAL_FRAMES = 3;

        public bool HasPendingRollback => _pendingErrorFrame >= 0;

        public void Init(int capacity = 256)
        {
            _snapshotBuffer = new SnapshotBuffer(capacity);
            _lastRemoteInput = new FrameInput();
            _predictionHistory = new Dictionary<int, FrameInput>(capacity);
            _pendingErrorFrame = -1;
            _totalFrames = 0;
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

                // 验证：真实数据是否和已经记录的任何预测值不同？
                if (_realDataArrived > SKIP_INITIAL_FRAMES && _predictionHistory.Count > 0)
                {
                    int earliestWrong = int.MaxValue;
                    foreach (var kv in _predictionHistory)
                    {
                        if (kv.Value._raw != realDataRaw)
                        {
                            if (kv.Key < earliestWrong)
                                earliestWrong = kv.Key;
                        }
                    }

                    if (earliestWrong < int.MaxValue)
                    {
                        // ❌ 找到最早预测错误的帧 → 回滚
                        _pendingErrorFrame = earliestWrong;
                        _correctRemoteRaw = realDataRaw;

                        // 清除 earliestWrong 及之后所有预测（被污染了）
                        var toRemove = new List<int>();
                        foreach (var kv in _predictionHistory)
                            if (kv.Key >= earliestWrong)
                                toRemove.Add(kv.Key);
                        foreach (var key in toRemove)
                            _predictionHistory.Remove(key);

                        Debug.Log($"[PredictionSystem] ❌ 帧{earliestWrong}预测错误: " +
                            $"预测={_predictionHistory.Count + toRemove.Count}条记录, 实际={realDataRaw:X8}");
                    }
                    else
                    {
                        // ✅ 所有预测都正确 → 移除已验证的早于当前帧的记录
                        var verified = new List<int>();
                        foreach (var kv in _predictionHistory)
                            if (kv.Key < frameID)
                                verified.Add(kv.Key);
                        foreach (var key in verified)
                            _predictionHistory.Remove(key);

                        if (verified.Count > 0)
                            Debug.Log($"[PredictionSystem] ✅ {verified.Count}条预测验证通过");
                    }
                }

                _lastRemoteInput = realInput;
                return realInput;
            }
            else
            {
                // ── 无数据 → 预测 ──
                _predictionHistory[frameID] = _lastRemoteInput;
                _totalFrames++;
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

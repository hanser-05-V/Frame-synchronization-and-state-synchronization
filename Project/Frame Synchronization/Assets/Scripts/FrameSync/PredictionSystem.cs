using System.Collections.Generic;
using UnityEngine;

namespace FrameSyncDemo
{
    /// <summary>
    /// 本地预测 + 回滚纠正系统（v10 — 延迟偏移回查验证）
    ///
    /// 核心逻辑：
    ///   1. 每帧都存预测值到 _predictionHistory[frameID]
    ///   2. 真实数据到达时 → 回查 frameID-LATENCY 帧的预测 → 比对
    ///   3. 不同 → 回滚（恢复快照 → 重跑后续全部帧）
    ///
    /// 为什么用延迟偏移：
    ///   TCP 30fps下 hasRealData 几乎永远 true，队列法永远没预测入队。
    ///   改用"每帧存预测 + 延迟回查"：用 200ms/33ms≈6帧的偏移，
    ///   查对应延迟帧的预测值是否和当前真实数据一致。
    /// </summary>
    public class PredictionSystem
    {
        private SnapshotBuffer _snapshotBuffer;
        private FrameInput _lastRemoteInput = default;

        // 预测历史 [localFrameID → 当时预测的值]
        private Dictionary<int, FrameInput> _predictionHistory;

        // 待处理回滚
        private int _pendingErrorFrame = -1;
        private uint _correctRemoteRaw;

        // 冷启动 + 延迟偏移
        private int _realDataArrived;
        private const int SKIP_INITIAL_FRAMES = 10;
        private const int LATENCY_FRAMES = 6;   // 200ms / 33ms ≈ 6帧

        public bool HasPendingRollback => _pendingErrorFrame >= 0;

        public void Init(int capacity = 512)
        {
            _snapshotBuffer = new SnapshotBuffer(capacity);
            _lastRemoteInput = new FrameInput();
            _predictionHistory = new Dictionary<int, FrameInput>(capacity);
            _pendingErrorFrame = -1;
            _realDataArrived = 0;
        }

        /// <summary>
        /// 预测对手输入（在 ReadInputs 中调用）
        /// </summary>
        public FrameInput PredictRemote(bool hasRealData, uint realDataRaw, int frameID)
        {
            // ① 每帧都存预测值（不管有没有真实数据）
            _predictionHistory[frameID] = _lastRemoteInput;

            if (hasRealData)
            {
                FrameInput realInput = FrameInput.FromRaw(realDataRaw);
                _realDataArrived++;

                // ② 回查验证：看 LATENCY_FRAMES 帧前的预测是否和当前真实数据一致
                if (_realDataArrived > SKIP_INITIAL_FRAMES)
                {
                    int validationFrame = frameID - LATENCY_FRAMES;
                    if (_predictionHistory.TryGetValue(validationFrame, out var predicted))
                    {
                        _predictionHistory.Remove(validationFrame);

                        if (predicted._raw != 0 && predicted._raw != realDataRaw)
                        {
                            // ❌ 预测错了 → 回滚
                            _pendingErrorFrame = validationFrame;
                            _correctRemoteRaw = realDataRaw;

                            // 清除 validationFrame 起所有后续预测（被污染了）
                            var toRemove = new List<int>();
                            foreach (var kv in _predictionHistory)
                                if (kv.Key >= validationFrame)
                                    toRemove.Add(kv.Key);
                            foreach (var k in toRemove)
                                _predictionHistory.Remove(k);

                            Debug.Log($"[PredictionSystem] ❌ 帧{validationFrame}预测错误: " +
                                $"预测={predicted._raw:X8}, 实际={realDataRaw:X8}, " +
                                $"清除{toRemove.Count}条");
                        }
                        else if (predicted._raw != 0 &&
                                 (_realDataArrived - SKIP_INITIAL_FRAMES) % 30 == 1)
                        {
                            Debug.Log($"[PredictionSystem] ✅ 帧{validationFrame}预测正确 " +
                                $"(已验{_realDataArrived - SKIP_INITIAL_FRAMES}条)");
                        }
                    }

                    // 定期清理过期预测（超过 LATENCY*2 帧前的）
                    if (frameID % 60 == 0)
                    {
                        int threshold = frameID - LATENCY_FRAMES * 2;
                        var expired = new List<int>();
                        foreach (var kv in _predictionHistory)
                            if (kv.Key < threshold) expired.Add(kv.Key);
                        foreach (var k in expired) _predictionHistory.Remove(k);
                    }
                }

                _lastRemoteInput = realInput;
                return realInput;
            }
            else
            {
                return _lastRemoteInput;
            }
        }

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

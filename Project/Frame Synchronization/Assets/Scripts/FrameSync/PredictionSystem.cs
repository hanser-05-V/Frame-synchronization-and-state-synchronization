using System.Collections.Generic;
using UnityEngine;

namespace FrameSyncDemo
{
    /// <summary>
    /// 本地预测 + 回滚纠正系统（v11 — 帧对齐同帧验证）
    ///
    /// 核心逻辑：
    ///   1. 每帧通过 TryGetRemoteInputAt 取该帧的真实远端输入（帧对齐）
    ///   2. 有真实数据 → 存储 (used=realInput, isPredicted=false)
    ///   3. 无真实数据 → 预测 = _lastRemoteInput → 存储 (used=预测, isPredicted=true)
    ///   4. 真实数据到达时 → 向后查找第一个预测错误帧 → 标记回滚
    /// </summary>
    public class PredictionSystem
    {
        /// <summary>预测历史条目 — 记录每帧使用的输入值和是否预测</summary>
        private struct PredictionEntry
        {
            public FrameInput used;       // 该帧实际使用的输入值
            public bool isPredicted;      // 该帧是否用的是预测值（真值缺失时为 true）
        }

        private SnapshotBuffer _snapshotBuffer;
        private FrameInput _lastRemoteInput = default;

        // 预测历史 [localFrameID → PredictionEntry]
        private Dictionary<int, PredictionEntry> _predictionHistory;

        // 定期正确验证计数（用于日志节流）
        private int _correctCount = 0;

        public void Init(int capacity = 512)
        {
            _snapshotBuffer = new SnapshotBuffer(capacity);
            _lastRemoteInput = new FrameInput();
            _predictionHistory = new Dictionary<int, PredictionEntry>(capacity);
            _correctCount = 0;
        }

        /// <summary>
        /// 帧对齐解析远程输入（替代原 PredictRemote）
        /// 入参：localFrame=本地帧号, actualRaw=该帧真实远端输入(0=无数据), hasActual=是否有真实数据
        /// 出参：errorFrame=预测错误的帧号(null=无错误), correctRaw=纠正值
        /// 返回：该帧应使用的 FrameInput
        /// </summary>
        public FrameInput ResolveRemote(int localFrame, uint actualRaw, bool hasActual, out int? errorFrame, out uint correctRaw)
        {
            errorFrame = null;
            correctRaw = 0;
            FrameInput result;

            if (hasActual)
            {
                FrameInput realInput = FrameInput.FromRaw(actualRaw);

                // ① 存储：该帧有真实数据
                _predictionHistory[localFrame] = new PredictionEntry { used = realInput, isPredicted = false };

                // ② 向后查找：最近一个标记为"预测"且预测值与当前真实值不同的帧
                int worstFrame = -1;
                foreach (var kv in _predictionHistory)
                {
                    // 注：用当前帧刚到达的真实值比对历史预测值（而非该历史帧自己的真实值）。
                    // 在 TCP 有序交付下是合理近似——当前帧的真实值到达意味着之前所有帧的真实值也都到了，
                    // 如果预测值 ≠ 当前值，那该预测帧的真实值也必然 ≠ 预测值。
                    if (kv.Key < localFrame && kv.Value.isPredicted && kv.Value.used._raw != 0 && kv.Value.used._raw != actualRaw)
                    {
                        if (kv.Key > worstFrame)
                            worstFrame = kv.Key;
                    }
                }

                if (worstFrame >= 0)
                {
                    var wrongEntry = _predictionHistory[worstFrame];
                    errorFrame = worstFrame;
                    correctRaw = actualRaw;

                    // 清除 worstFrame 起所有后续预测（被污染了）
                    var toRemove = new List<int>();
                    foreach (var kv in _predictionHistory)
                        if (kv.Key >= worstFrame)
                            toRemove.Add(kv.Key);
                    foreach (var k in toRemove)
                        _predictionHistory.Remove(k);

                    Debug.Log($"[PredictionSystem] ❌ 帧{worstFrame}预测错误: " +
                        $"预测={wrongEntry.used._raw:X8}, 实际={actualRaw:X8}, " +
                        $"清除{toRemove.Count}条");
                }
                else
                {
                    _correctCount++;
                    if (_correctCount % 30 == 1)
                    {
                        Debug.Log($"[PredictionSystem] ✅ 帧{localFrame}预测正确 (累计{_correctCount}次)");
                    }
                }

                _lastRemoteInput = realInput;
                result = realInput;
            }
            else
            {
                // ③ 无真实数据 → 预测 = 最近已知值
                FrameInput predicted = _lastRemoteInput;
                _predictionHistory[localFrame] = new PredictionEntry { used = predicted, isPredicted = true };
                result = predicted;
            }

            // 定期清理过期预测（超过 120 帧前的）—— 无论 hasActual 都执行，防止对手断连时无限增长
            if (localFrame % 60 == 0)
            {
                int threshold = localFrame - 120;
                var expired = new List<int>();
                foreach (var kv in _predictionHistory)
                    if (kv.Key < threshold) expired.Add(kv.Key);
                foreach (var k in expired) _predictionHistory.Remove(k);
            }

            return result;
        }

        public void TakeSnapshot(int frameID, FixedInt[] posX, FixedInt[] posZ)
        {
            var snap = new FrameSnapshot
            {
                frameID = frameID,
                player1X = posX[0],
                player1Z = posZ[0],
                player2X = posX[1],
                player2Z = posZ[1]
            };
            _snapshotBuffer.AddSnapshot(snap);
        }

        public bool RestoreSnapshot(int frameID, ref FixedInt[] posX, ref FixedInt[] posZ)
        {
            if (!_snapshotBuffer.HasSnapshot(frameID)) return false;
            var snap = _snapshotBuffer.GetSnapshot(frameID);
            posX[0] = snap.player1X;
            posZ[0] = snap.player1Z;
            posX[1] = snap.player2X;
            posZ[1] = snap.player2Z;
            return true;
        }
    }
}

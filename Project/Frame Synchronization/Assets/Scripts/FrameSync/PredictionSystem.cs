using System;
using System.Collections.Generic;
using UnityEngine;

namespace FrameSyncDemo
{
    /// <summary>
    /// 本地预测 + 回滚纠正系统（v12 — 历史帧精确验证）
    ///
    /// 核心逻辑：
    ///   1. 每帧通过查询函数取该本地帧所对应的真实远端输入
    ///   2. 有真实数据 → 存储 (used=realInput, isPredicted=false)
    ///   3. 无真实数据 → 预测 = _lastRemoteInput → 存储 (used=预测, isPredicted=true)
    ///   4. 真实数据到达时 → 用每个历史帧自己的真值找最早错误帧 → 标记一次回滚
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
        /// 帧对齐解析远程输入。
        /// 入参：localFrame=本地帧号，getActualRawForLocalFrame=按本地帧查询其对应的远端真值
        /// 出参：errorFrame=预测错误的帧号(null=无错误), correctRaw=纠正值
        /// 返回：该帧应使用的 FrameInput
        /// </summary>
        public FrameInput ResolveRemote(
            int localFrame,
            Func<int, uint?> getActualRawForLocalFrame,
            out int? errorFrame,
            out uint correctRaw)
        {
            if (getActualRawForLocalFrame == null)
                throw new ArgumentNullException(nameof(getActualRawForLocalFrame));

            errorFrame = null;
            correctRaw = 0;
            uint? currentActualRaw = getActualRawForLocalFrame(localFrame);
            int earliestErrorFrame = int.MaxValue;
            uint earliestCorrectRaw = 0;
            int latestHistoricalActualFrame = int.MinValue;
            uint latestHistoricalActualRaw = 0;
            var validatedFrames = new List<int>();

            foreach (var kv in _predictionHistory)
            {
                if (kv.Key >= localFrame || !kv.Value.isPredicted)
                    continue;

                uint? historicalActualRaw = getActualRawForLocalFrame(kv.Key);
                if (!historicalActualRaw.HasValue)
                    continue;

                validatedFrames.Add(kv.Key);
                if (kv.Key > latestHistoricalActualFrame)
                {
                    latestHistoricalActualFrame = kv.Key;
                    latestHistoricalActualRaw = historicalActualRaw.Value;
                }

                if (kv.Value.used._raw != historicalActualRaw.Value &&
                    kv.Key < earliestErrorFrame)
                {
                    earliestErrorFrame = kv.Key;
                    earliestCorrectRaw = historicalActualRaw.Value;
                }
            }

            FrameInput result;

            if (currentActualRaw.HasValue)
            {
                FrameInput realInput = FrameInput.FromRaw(currentActualRaw.Value);
                _lastRemoteInput = realInput;
                result = realInput;
            }
            else
            {
                if (latestHistoricalActualFrame != int.MinValue)
                {
                    _lastRemoteInput = FrameInput.FromRaw(latestHistoricalActualRaw);
                }

                FrameInput predicted = _lastRemoteInput.ToPredictionInput();
                _predictionHistory[localFrame] = new PredictionEntry
                {
                    used = predicted,
                    isPredicted = true
                };
                result = predicted;
            }

            if (earliestErrorFrame != int.MaxValue)
            {
                PredictionEntry wrongEntry = _predictionHistory[earliestErrorFrame];
                errorFrame = earliestErrorFrame;
                correctRaw = earliestCorrectRaw;

                var toRemove = new List<int>();
                foreach (var kv in _predictionHistory)
                {
                    if (kv.Key >= earliestErrorFrame || validatedFrames.Contains(kv.Key))
                        toRemove.Add(kv.Key);
                }
                foreach (int frame in toRemove)
                    _predictionHistory.Remove(frame);

                Debug.Log($"[PredictionSystem] ❌ 帧{earliestErrorFrame}预测错误: " +
                    $"预测={wrongEntry.used._raw:X8}, 实际={earliestCorrectRaw:X8}, " +
                    $"清除{toRemove.Count}条，等待回滚重建");
            }
            else
            {
                foreach (int frame in validatedFrames)
                    _predictionHistory.Remove(frame);

                if (currentActualRaw.HasValue || validatedFrames.Count > 0)
                {
                    _correctCount++;
                    if (_correctCount % 30 == 1)
                    {
                        Debug.Log($"[PredictionSystem] ✅ 帧{localFrame}预测正确 (累计{_correctCount}次)");
                    }
                }
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

        public void RecordReplayRemote(int localFrame, uint usedRaw, bool isPredicted)
        {
            FrameInput used = FrameInput.FromRaw(usedRaw);
            _lastRemoteInput = used;

            if (isPredicted)
            {
                _predictionHistory[localFrame] = new PredictionEntry
                {
                    used = used,
                    isPredicted = true
                };
            }
            else
            {
                _predictionHistory.Remove(localFrame);
            }
        }

        public FrameSnapshot TakeWorldSnapshot(int frameID, PlayerEntity[] players, BallEntity ball)
        {
            ValidateWorldState(players, ball);

            PlayerEntity player0 = players[0];
            PlayerEntity player1 = players[1];
            var snapshot = new FrameSnapshot
            {
                frameID = frameID,
                player1X = player0.position.x,
                player1Y = player0.position.y,
                player1Z = player0.position.z,
                player2X = player1.position.x,
                player2Y = player1.position.y,
                player2Z = player1.position.z,
                player1FacingX = player0.facing.x,
                player1FacingY = player0.facing.y,
                player1FacingZ = player0.facing.z,
                player2FacingX = player1.facing.x,
                player2FacingY = player1.facing.y,
                player2FacingZ = player1.facing.z,
                player1State = (int)player0.state,
                player2State = (int)player1.state,
                player1HasBall = player0.hasBall,
                player2HasBall = player1.hasBall,
                ballPosX = ball.position.x,
                ballPosY = ball.position.y,
                ballPosZ = ball.position.z,
                ballVelX = ball.velocity.x,
                ballVelY = ball.velocity.y,
                ballVelZ = ball.velocity.z,
                ballState = (int)ball.state,
                ballHolder = ball.holderPlayerIndex
            };
            _snapshotBuffer.AddSnapshot(snapshot);
            return snapshot;
        }

        public bool TryGetWorldSnapshot(int frameID, out FrameSnapshot snapshot)
        {
            if (!_snapshotBuffer.HasSnapshot(frameID))
            {
                snapshot = new FrameSnapshot { frameID = -1 };
                return false;
            }

            snapshot = _snapshotBuffer.GetSnapshot(frameID);
            return true;
        }

        public bool RestoreWorldSnapshot(int frameID, PlayerEntity[] players, BallEntity ball)
        {
            ValidateWorldState(players, ball);

            if (!_snapshotBuffer.HasSnapshot(frameID))
                return false;

            FrameSnapshot snapshot = _snapshotBuffer.GetSnapshot(frameID);
            players[0].position = new FixedVector3(
                snapshot.player1X,
                snapshot.player1Y,
                snapshot.player1Z);
            players[1].position = new FixedVector3(
                snapshot.player2X,
                snapshot.player2Y,
                snapshot.player2Z);
            players[0].facing = new FixedVector3(
                snapshot.player1FacingX,
                snapshot.player1FacingY,
                snapshot.player1FacingZ);
            players[1].facing = new FixedVector3(
                snapshot.player2FacingX,
                snapshot.player2FacingY,
                snapshot.player2FacingZ);
            players[0].state = (PlayerEntity.EState)snapshot.player1State;
            players[1].state = (PlayerEntity.EState)snapshot.player2State;
            players[0].hasBall = snapshot.player1HasBall;
            players[1].hasBall = snapshot.player2HasBall;
            ball.position = new FixedVector3(snapshot.ballPosX, snapshot.ballPosY, snapshot.ballPosZ);
            ball.velocity = new FixedVector3(snapshot.ballVelX, snapshot.ballVelY, snapshot.ballVelZ);
            ball.state = (BallEntity.EState)snapshot.ballState;
            ball.holderPlayerIndex = snapshot.ballHolder;
            return true;
        }

        private static void ValidateWorldState(PlayerEntity[] players, BallEntity ball)
        {
            if (players == null || players.Length != 2 || players[0] == null || players[1] == null)
            {
                throw new ArgumentException(
                    "World snapshots require exactly two non-null players.",
                    nameof(players));
            }

            if (ball == null)
                throw new ArgumentNullException(nameof(ball));
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

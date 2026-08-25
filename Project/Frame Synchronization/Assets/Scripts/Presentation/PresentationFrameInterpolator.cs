using System;
using UnityEngine;

namespace FrameSyncDemo
{
    /// <summary>
    /// 仅保存表现坐标，在四个完整逻辑帧端点间计算混合时间线。
    /// </summary>
    public sealed class PresentationFrameInterpolator
    {
        private struct BallEndpoint
        {
            public Vector3 Position;
            public BallEntity.EState State;
            public int HolderPlayerIndex;
            public Vector3 HeldOffset;
        }

        private readonly int _playerCount;
        private readonly Vector3[] _deepestPlayerPositions;
        private readonly Vector3[] _oldestPlayerPositions;
        private readonly Vector3[] _previousPlayerPositions;
        private readonly Vector3[] _currentPlayerPositions;
        private BallEndpoint _deepestBall;
        private BallEndpoint _oldestBall;
        private BallEndpoint _previousBall;
        private BallEndpoint _currentBall;
        private ViewWorldState _viewWorld;
        private bool _usesViewWorld;
        private bool _hasFreshConfirmedInterval;

        public PresentationFrameInterpolator(int playerCount)
        {
            if (playerCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(playerCount),
                    "球员数量必须大于零。");
            }

            _playerCount = playerCount;
            _deepestPlayerPositions = new Vector3[playerCount];
            _oldestPlayerPositions = new Vector3[playerCount];
            _previousPlayerPositions = new Vector3[playerCount];
            _currentPlayerPositions = new Vector3[playerCount];
        }

        public bool IsReady { get; private set; }
        public int DeepestFrameID { get; private set; }
        public int OldestFrameID { get; private set; }
        public int PreviousFrameID { get; private set; }
        public int CurrentFrameID { get; private set; }

        public bool CanPushLogicFrame(int frameID)
        {
            return !IsReady || frameID > CurrentFrameID;
        }

        public void Reset(in ViewWorldState viewWorld)
        {
            SetViewWorld(viewWorld, true);
        }

        public void PushViewWorld(in ViewWorldState viewWorld)
        {
            if (IsReady && viewWorld.PredictedFrame <= CurrentFrameID)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(viewWorld),
                    "正常表现世界的预测帧头必须严格递增。");
            }

            bool confirmedAdvanced = !_usesViewWorld ||
                viewWorld.PresentationFrame >
                _viewWorld.PresentationFrame;
            SetViewWorld(viewWorld, confirmedAdvanced);
        }

        public void ReplaceViewWorld(in ViewWorldState viewWorld)
        {
            bool confirmedAdvanced = !_usesViewWorld ||
                viewWorld.PresentationFrame >
                _viewWorld.PresentationFrame;
            SetViewWorld(viewWorld, confirmedAdvanced);
        }

        public void Reset(
            int frameID,
            PlayerEntity[] players,
            BallEntity ball)
        {
            _usesViewWorld = false;
            _hasFreshConfirmedInterval = false;
            ValidateWorld(players, ball);
            CaptureCurrent(players, ball);
            CopyCurrentToPrevious();
            CopyPreviousToOldest();
            CopyOldestToDeepest();
            DeepestFrameID = frameID;
            OldestFrameID = frameID;
            PreviousFrameID = frameID;
            CurrentFrameID = frameID;
            IsReady = true;
        }

        public void PushLogicFrame(
            int frameID,
            PlayerEntity[] players,
            BallEntity ball)
        {
            _usesViewWorld = false;
            _hasFreshConfirmedInterval = false;
            if (!IsReady)
            {
                Reset(frameID, players, ball);
                return;
            }

            if (!CanPushLogicFrame(frameID))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frameID),
                    "正常逻辑帧必须严格递增。");
            }

            ValidateWorld(players, ball);
            CopyOldestToDeepest();
            DeepestFrameID = OldestFrameID;
            CopyPreviousToOldest();
            OldestFrameID = PreviousFrameID;
            CopyCurrentToPrevious();
            PreviousFrameID = CurrentFrameID;
            CaptureCurrent(players, ball);
            CurrentFrameID = frameID;
        }

        public void ReplaceAfterRollback(
            int frameID,
            PlayerEntity[] players,
            BallEntity ball)
        {
            _usesViewWorld = false;
            _hasFreshConfirmedInterval = false;
            ValidateWorld(players, ball);
            CaptureCurrent(players, ball);
            CopyCurrentToPrevious();
            CopyPreviousToOldest();
            CopyOldestToDeepest();
            DeepestFrameID = frameID;
            OldestFrameID = frameID;
            PreviousFrameID = frameID;
            CurrentFrameID = frameID;
            IsReady = true;
        }

        public void ReplaceHistoryAfterRollback(
            FrameSnapshot newest,
            FrameSnapshot? previous,
            FrameSnapshot? oldest,
            FrameSnapshot? deepest)
        {
            _usesViewWorld = false;
            _hasFreshConfirmedInterval = false;
            if (_playerCount != 2)
            {
                throw new InvalidOperationException(
                    "当前完整世界快照只支持两名球员。");
            }
            if (!newest.IsValid)
                throw new ArgumentException("最新回滚快照无效。", nameof(newest));
            if (previous.HasValue && !previous.Value.IsValid)
            {
                throw new ArgumentException(
                    "前一回滚快照无效。",
                    nameof(previous));
            }
            if (oldest.HasValue && !oldest.Value.IsValid)
            {
                throw new ArgumentException(
                    "最旧回滚快照无效。",
                    nameof(oldest));
            }
            if (deepest.HasValue && !deepest.Value.IsValid)
            {
                throw new ArgumentException(
                    "最深回滚快照无效。",
                    nameof(deepest));
            }
            if (previous.HasValue &&
                previous.Value.frameID != newest.frameID - 1)
            {
                throw new ArgumentException(
                    "前一回滚快照必须紧邻最新快照。",
                    nameof(previous));
            }
            if (oldest.HasValue &&
                (!previous.HasValue ||
                 oldest.Value.frameID != previous.Value.frameID - 1))
            {
                throw new ArgumentException(
                    "最旧回滚快照必须紧邻前一快照。",
                    nameof(oldest));
            }
            if (deepest.HasValue &&
                (!oldest.HasValue ||
                 deepest.Value.frameID != oldest.Value.frameID - 1))
            {
                throw new ArgumentException(
                    "最深回滚快照必须紧邻最旧快照。",
                    nameof(deepest));
            }

            CaptureSnapshot(newest, _currentPlayerPositions, out _currentBall);
            CurrentFrameID = newest.frameID;
            if (previous.HasValue)
            {
                CaptureSnapshot(
                    previous.Value,
                    _previousPlayerPositions,
                    out _previousBall);
                PreviousFrameID = previous.Value.frameID;
            }
            else
            {
                CopyCurrentToPrevious();
                PreviousFrameID = CurrentFrameID;
            }

            if (oldest.HasValue)
            {
                CaptureSnapshot(
                    oldest.Value,
                    _oldestPlayerPositions,
                    out _oldestBall);
                OldestFrameID = oldest.Value.frameID;
            }
            else
            {
                CopyPreviousToOldest();
                OldestFrameID = PreviousFrameID;
            }

            if (deepest.HasValue)
            {
                CaptureSnapshot(
                    deepest.Value,
                    _deepestPlayerPositions,
                    out _deepestBall);
                DeepestFrameID = deepest.Value.frameID;
            }
            else
            {
                CopyOldestToDeepest();
                DeepestFrameID = OldestFrameID;
            }
            IsReady = true;
        }

        public void Evaluate(
            float alpha,
            Vector3[] playerPositions,
            out Vector3 ballPosition)
        {
            if (!IsReady)
                throw new InvalidOperationException("表现帧插值器尚未初始化。");

            if (playerPositions == null)
                throw new ArgumentNullException(nameof(playerPositions));

            if (playerPositions.Length != _playerCount)
            {
                throw new ArgumentException(
                    "输出球员数组长度必须与构造时的球员数量一致。",
                    nameof(playerPositions));
            }

            float safeAlpha = NormalizeAlpha(alpha);
            for (int i = 0; i < _playerCount; i++)
            {
                playerPositions[i] = Vector3.LerpUnclamped(
                    _previousPlayerPositions[i],
                    _currentPlayerPositions[i],
                    safeAlpha);
            }

            ballPosition = Vector3.LerpUnclamped(
                _previousBall.Position,
                _currentBall.Position,
                safeAlpha);
        }

        public void Evaluate(
            int localPlayerIndex,
            float alpha,
            Vector3[] playerPositions,
            out PresentationBallSample ballSample)
        {
            ValidateMixedEvaluation(localPlayerIndex, playerPositions);
            float safeAlpha = NormalizeAlpha(alpha);
            if (_usesViewWorld)
            {
                EvaluateViewWorld(
                    safeAlpha,
                    safeAlpha,
                    false,
                    playerPositions,
                    out ballSample);
                return;
            }

            for (int i = 0; i < _playerCount; i++)
            {
                Vector3 from = i == localPlayerIndex
                    ? _previousPlayerPositions[i]
                    : _deepestPlayerPositions[i];
                Vector3 to = i == localPlayerIndex
                    ? _currentPlayerPositions[i]
                    : _oldestPlayerPositions[i];
                playerPositions[i] = Vector3.LerpUnclamped(
                    from,
                    to,
                    safeAlpha);
            }

            ballSample = EvaluateBallSample(
                localPlayerIndex,
                safeAlpha,
                playerPositions);
        }

        public void Evaluate(
            int localPlayerIndex,
            float predictedAlpha,
            float confirmedAlpha,
            Vector3[] playerPositions,
            out PresentationBallSample ballSample)
        {
            ValidateMixedEvaluation(localPlayerIndex, playerPositions);
            float safePredictedAlpha = NormalizeAlpha(predictedAlpha);
            float safeConfirmedAlpha = NormalizeAlpha(confirmedAlpha);
            if (_usesViewWorld)
            {
                EvaluateViewWorld(
                    safePredictedAlpha,
                    safeConfirmedAlpha,
                    true,
                    playerPositions,
                    out ballSample);
                return;
            }

            Evaluate(
                localPlayerIndex,
                safePredictedAlpha,
                playerPositions,
                out ballSample);
        }

        private void SetViewWorld(
            in ViewWorldState viewWorld,
            bool hasFreshConfirmedInterval)
        {
            if (viewWorld.PredictedFrame < -1 ||
                viewWorld.ConfirmedFrame < -1 ||
                viewWorld.ConfirmedFrame > viewWorld.PredictedFrame)
            {
                throw new ArgumentOutOfRangeException(nameof(viewWorld));
            }

            _viewWorld = viewWorld;
            _usesViewWorld = true;
            _hasFreshConfirmedInterval = hasFreshConfirmedInterval;
            DeepestFrameID = viewWorld.PresentationFrame;
            OldestFrameID = viewWorld.PresentationFrame;
            PreviousFrameID = viewWorld.PresentationFrame;
            CurrentFrameID = viewWorld.PredictedFrame;
            IsReady = true;
        }

        private void EvaluateViewWorld(
            float predictedAlpha,
            float confirmedAlpha,
            bool useIndependentConfirmedClock,
            Vector3[] playerPositions,
            out PresentationBallSample ballSample)
        {
            for (int i = 0; i < _playerCount; i++)
            {
                ViewPlayerState player = _viewWorld.GetPlayer(i);
                bool isConfirmed = IsConfirmedSource(player.Source);
                bool interpolate = !isConfirmed ||
                    useIndependentConfirmedClock ||
                    _hasFreshConfirmedInterval;
                float playerAlpha = isConfirmed
                    ? confirmedAlpha
                    : predictedAlpha;
                playerPositions[i] = Vector3.LerpUnclamped(
                    ToPlayerVisualPosition(
                        interpolate
                            ? player.FromPosition
                            : player.ToPosition),
                    ToPlayerVisualPosition(player.ToPosition),
                    playerAlpha);
            }

            ViewBallState ball = _viewWorld.Ball;
            bool ballIsConfirmed = IsConfirmedSource(ball.Source);
            bool interpolateBall = !ballIsConfirmed ||
                useIndependentConfirmedClock ||
                _hasFreshConfirmedInterval;
            float ballAlpha = ballIsConfirmed
                ? confirmedAlpha
                : predictedAlpha;
            Vector3 ballPosition = Vector3.LerpUnclamped(
                (interpolateBall
                    ? ball.FromPosition
                    : ball.ToPosition).ToVector3(),
                ball.ToPosition.ToVector3(),
                ballAlpha);
            int holderIndex = ball.IsAttached
                ? ball.ToHolderPlayerIndex
                : -1;
            if (holderIndex >= 0 && holderIndex < _playerCount)
            {
                ViewPlayerState holder = _viewWorld.GetPlayer(holderIndex);
                Vector3 fromOffset = (interpolateBall
                    ? ball.FromPosition
                    : ball.ToPosition).ToVector3() -
                    ToPlayerVisualPosition(
                        interpolateBall
                            ? holder.FromPosition
                            : holder.ToPosition);
                Vector3 toOffset = ball.ToPosition.ToVector3() -
                    ToPlayerVisualPosition(holder.ToPosition);
                if (ball.FromState != BallEntity.EState.Held ||
                    ball.FromHolderPlayerIndex != holderIndex)
                {
                    fromOffset = toOffset;
                }

                ballPosition = playerPositions[holderIndex] +
                    Vector3.LerpUnclamped(
                        fromOffset,
                        toOffset,
                        ballAlpha);
            }

            ballSample = new PresentationBallSample(
                ballPosition,
                holderIndex);
        }

        private static Vector3 ToPlayerVisualPosition(
            FixedVector3 position)
        {
            return position.ToVector3() + Vector3.up * 0.5f;
        }

        private static bool IsConfirmedSource(ViewSampleSource source)
        {
            return source == ViewSampleSource.InitialConfirmed ||
                source == ViewSampleSource.Confirmed ||
                source == ViewSampleSource.ConfirmedSingleEndpoint;
        }

        public static float CalculateAlpha(
            long elapsedMs,
            long lastLogicMs,
            int frameIntervalMs)
        {
            if (frameIntervalMs <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frameIntervalMs),
                    "逻辑帧间隔必须大于零。");
            }

            if (elapsedMs <= lastLogicMs)
                return 0f;

            long remainderMs = elapsedMs - lastLogicMs;
            if (remainderMs >= frameIntervalMs)
                return 1f;

            return (float)remainderMs / frameIntervalMs;
        }

        private static float NormalizeAlpha(float alpha)
        {
            if (float.IsNaN(alpha) || alpha <= 0f)
                return 0f;

            if (float.IsPositiveInfinity(alpha) || alpha >= 1f)
                return 1f;

            return alpha;
        }

        private void ValidateMixedEvaluation(
            int localPlayerIndex,
            Vector3[] playerPositions)
        {
            if (!IsReady)
                throw new InvalidOperationException("表现帧插值器尚未初始化。");
            if (localPlayerIndex < 0 || localPlayerIndex >= _playerCount)
                throw new ArgumentOutOfRangeException(nameof(localPlayerIndex));
            if (playerPositions == null)
                throw new ArgumentNullException(nameof(playerPositions));
            if (playerPositions.Length != _playerCount)
            {
                throw new ArgumentException(
                    "输出球员数组长度必须与构造时的球员数量一致。",
                    nameof(playerPositions));
            }
        }

        private void ValidateWorld(PlayerEntity[] players, BallEntity ball)
        {
            if (players == null)
                throw new ArgumentNullException(nameof(players));

            if (players.Length != _playerCount)
            {
                throw new ArgumentException(
                    "逻辑球员数组长度必须与构造时的球员数量一致。",
                    nameof(players));
            }

            for (int i = 0; i < players.Length; i++)
            {
                if (players[i] == null)
                {
                    throw new ArgumentException(
                        $"逻辑球员数组第 {i} 项为空。",
                        nameof(players));
                }
            }

            if (ball == null)
                throw new ArgumentNullException(nameof(ball));
        }

        private void CaptureCurrent(PlayerEntity[] players, BallEntity ball)
        {
            for (int i = 0; i < _playerCount; i++)
            {
                _currentPlayerPositions[i] =
                    PresentationTargetResolver.ResolvePlayerTarget(players[i]);
            }

            Vector3 ballPosition = ball.position.ToVector3();
            int holderIndex = ball.holderPlayerIndex;
            Vector3 heldOffset = Vector3.zero;
            if (ball.state == BallEntity.EState.Held &&
                holderIndex >= 0 &&
                holderIndex < _playerCount)
            {
                heldOffset =
                    ballPosition - _currentPlayerPositions[holderIndex];
            }

            _currentBall = new BallEndpoint
            {
                Position = ballPosition,
                State = ball.state,
                HolderPlayerIndex = holderIndex,
                HeldOffset = heldOffset
            };
        }

        private void CopyCurrentToPrevious()
        {
            for (int i = 0; i < _playerCount; i++)
                _previousPlayerPositions[i] = _currentPlayerPositions[i];

            _previousBall = _currentBall;
        }

        private void CopyPreviousToOldest()
        {
            for (int i = 0; i < _playerCount; i++)
                _oldestPlayerPositions[i] = _previousPlayerPositions[i];

            _oldestBall = _previousBall;
        }

        private void CopyOldestToDeepest()
        {
            for (int i = 0; i < _playerCount; i++)
                _deepestPlayerPositions[i] = _oldestPlayerPositions[i];

            _deepestBall = _oldestBall;
        }

        private static void CaptureSnapshot(
            FrameSnapshot snapshot,
            Vector3[] players,
            out BallEndpoint ballEndpoint)
        {
            players[0] = new Vector3(
                snapshot.player1X.ToFloat(),
                snapshot.player1Y.ToFloat() + 0.5f,
                snapshot.player1Z.ToFloat());
            players[1] = new Vector3(
                snapshot.player2X.ToFloat(),
                snapshot.player2Y.ToFloat() + 0.5f,
                snapshot.player2Z.ToFloat());
            Vector3 position = new Vector3(
                snapshot.ballPosX.ToFloat(),
                snapshot.ballPosY.ToFloat(),
                snapshot.ballPosZ.ToFloat());
            var state = (BallEntity.EState)snapshot.ballState;
            int holder = snapshot.ballHolder;
            Vector3 offset = Vector3.zero;
            if (state == BallEntity.EState.Held &&
                holder >= 0 &&
                holder < players.Length)
            {
                offset = position - players[holder];
            }

            ballEndpoint = new BallEndpoint
            {
                Position = position,
                State = state,
                HolderPlayerIndex = holder,
                HeldOffset = offset
            };
        }

        private PresentationBallSample EvaluateBallSample(
            int localPlayerIndex,
            float alpha,
            Vector3[] playerPositions)
        {
            if (IsHeldBy(_currentBall, localPlayerIndex))
            {
                Vector3 offset = InterpolateHeldOffset(
                    _previousBall,
                    _currentBall,
                    localPlayerIndex,
                    alpha);
                return new PresentationBallSample(
                    playerPositions[localPlayerIndex] + offset,
                    localPlayerIndex);
            }

            int remoteHolder = _oldestBall.HolderPlayerIndex;
            if (remoteHolder != localPlayerIndex &&
                remoteHolder >= 0 &&
                remoteHolder < _playerCount &&
                IsHeldBy(_oldestBall, remoteHolder))
            {
                Vector3 offset = InterpolateHeldOffset(
                    _deepestBall,
                    _oldestBall,
                    remoteHolder,
                    alpha);
                return new PresentationBallSample(
                    playerPositions[remoteHolder] + offset,
                    remoteHolder);
            }

            return new PresentationBallSample(
                Vector3.LerpUnclamped(
                    _deepestBall.Position,
                    _oldestBall.Position,
                    alpha),
                -1);
        }

        private static bool IsHeldBy(
            BallEndpoint endpoint,
            int playerIndex)
        {
            return playerIndex >= 0 &&
                endpoint.State == BallEntity.EState.Held &&
                endpoint.HolderPlayerIndex == playerIndex;
        }

        private static Vector3 InterpolateHeldOffset(
            BallEndpoint from,
            BallEndpoint to,
            int holderIndex,
            float alpha)
        {
            return IsHeldBy(from, holderIndex)
                ? Vector3.LerpUnclamped(
                    from.HeldOffset,
                    to.HeldOffset,
                    alpha)
                : to.HeldOffset;
        }
    }
}

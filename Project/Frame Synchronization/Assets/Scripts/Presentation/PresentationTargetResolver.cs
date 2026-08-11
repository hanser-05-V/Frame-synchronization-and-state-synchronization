using System;
using UnityEngine;

namespace FrameSyncDemo
{
    /// <summary>
    /// 将确定性逻辑坐标转换为 Unity 表现目标，不保存或修改同步状态。
    /// </summary>
    public static class PresentationTargetResolver
    {
        private static readonly Vector3 PlayerVisualOffset = Vector3.up * 0.5f;

        public static Vector3 ResolvePlayerTarget(PlayerEntity player)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            return player.position.ToVector3() + PlayerVisualOffset;
        }

        public static bool ShouldSmoothPlayer(
            int playerIndex,
            int localPlayerIndex)
        {
            return playerIndex != localPlayerIndex;
        }

        public static bool ShouldSmoothBall(
            BallEntity ball,
            int localPlayerIndex)
        {
            if (ball == null)
                throw new ArgumentNullException(nameof(ball));

            return ball.state != BallEntity.EState.Held ||
                ball.holderPlayerIndex != localPlayerIndex;
        }

        public static bool ShouldSnapForPlaybackTransition(
            bool wasPlayingBack,
            bool isPlayingBack)
        {
            return wasPlayingBack != isPlayingBack;
        }

        public static bool ShouldTransferBallVisualCorrection(
            BallEntity.EState previousState,
            int previousHolderIndex,
            BallEntity.EState currentState,
            int currentHolderIndex)
        {
            bool wasHeld = previousState == BallEntity.EState.Held;
            bool isHeld = currentState == BallEntity.EState.Held;
            if (wasHeld != isHeld)
                return true;

            return wasHeld && previousHolderIndex != currentHolderIndex;
        }

        public static bool ShouldTransferBallVisualCorrection(
            int previousAttachment,
            int currentAttachment)
        {
            return previousAttachment != currentAttachment;
        }

        public static bool ShouldUseIndependentBallSmoother(
            PresentationBallSample sample)
        {
            return !sample.IsAttached;
        }

        public static bool ShouldProcessRollback(
            bool isPaused,
            bool isPlayingBack)
        {
            return !isPaused && !isPlayingBack;
        }

        public static Vector3 ResolveBallTarget(
            BallEntity ball,
            PlayerEntity[] players,
            Vector3[] playerPresentationPositions)
        {
            if (ball == null)
                throw new ArgumentNullException(nameof(ball));

            Vector3 logicBallPosition = ball.position.ToVector3();
            int holderIndex = ball.holderPlayerIndex;
            if (ball.state != BallEntity.EState.Held ||
                players == null ||
                playerPresentationPositions == null ||
                holderIndex < 0 ||
                holderIndex >= players.Length ||
                holderIndex >= playerPresentationPositions.Length ||
                players[holderIndex] == null)
            {
                return logicBallPosition;
            }

            Vector3 holderLogicTarget = ResolvePlayerTarget(players[holderIndex]);
            Vector3 heldOffset = logicBallPosition - holderLogicTarget;
            return playerPresentationPositions[holderIndex] + heldOffset;
        }

        public static Vector3 ResolveInterpolatedBallTarget(
            BallEntity ball,
            Vector3 interpolatedBallPosition,
            Vector3[] interpolatedPlayerPositions,
            Vector3[] displayedPlayerPositions)
        {
            if (ball == null)
                throw new ArgumentNullException(nameof(ball));

            int holderIndex = ball.holderPlayerIndex;
            if (ball.state != BallEntity.EState.Held ||
                interpolatedPlayerPositions == null ||
                displayedPlayerPositions == null ||
                holderIndex < 0 ||
                holderIndex >= interpolatedPlayerPositions.Length ||
                holderIndex >= displayedPlayerPositions.Length)
            {
                return interpolatedBallPosition;
            }

            Vector3 holderVisualCorrection =
                displayedPlayerPositions[holderIndex] -
                interpolatedPlayerPositions[holderIndex];
            return interpolatedBallPosition + holderVisualCorrection;
        }

        public static Vector3 ResolveBufferedBallTarget(
            PresentationBallSample sample,
            Vector3[] playerBasePositions,
            Vector3[] displayedPlayerPositions)
        {
            if (!sample.IsAttached)
                return sample.BasePosition;

            int holderIndex = sample.AttachedPlayerIndex;
            if (playerBasePositions == null ||
                displayedPlayerPositions == null ||
                holderIndex >= playerBasePositions.Length ||
                holderIndex >= displayedPlayerPositions.Length)
            {
                return sample.BasePosition;
            }

            return sample.BasePosition +
                displayedPlayerPositions[holderIndex] -
                playerBasePositions[holderIndex];
        }
    }
}

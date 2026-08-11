using System;

namespace FrameSyncDemo
{
    /// <summary>
    /// 单帧确定性世界推进。正常执行与回滚重演必须共同调用此入口。
    /// </summary>
    public static class FrameSimulationSystem
    {
        private static readonly int[] DirX = { 0, 0, 1, 1, 1, 0, -1, -1, -1 };
        private static readonly int[] DirZ = { 0, 1, 1, 0, -1, -1, -1, 0, 1 };
        private static readonly FixedInt DiagonalScale = new FixedInt(707);

        public static FrameSimulationResult Step(
            PlayerEntity[] players,
            PlayerStateMachine[] stateMachines,
            BallEntity ball,
            FrameInput[] inputs,
            FixedInt moveDistance,
            FixedInt deltaTime)
        {
            ValidateWorld(players, stateMachines, ball, inputs);

            BallEntity.EState previousBallState = ball.state;
            FixedInt previousBallY = ball.position.y;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerEntity player = players[i];
                PlayerStateMachine stateMachine = stateMachines[i];
                byte direction = inputs[i].moveDir;

                if (player.state == PlayerEntity.EState.Shooting)
                    stateMachine.TryChangeStateSilently(PlayerEntity.EState.Idle);

                bool isMoving = direction > 0 && direction <= 8;
                stateMachine.TryChangeStateSilently(
                    isMoving ? PlayerEntity.EState.Run : PlayerEntity.EState.Idle);

                if (!isMoving)
                    continue;

                FixedInt directionScale =
                    DirX[direction] != 0 && DirZ[direction] != 0
                        ? DiagonalScale
                        : FixedInt.One;
                FixedInt scaledMoveDistance = moveDistance * directionScale;
                FixedInt moveX =
                    scaledMoveDistance * FixedInt.FromInt(DirX[direction]);
                FixedInt moveZ =
                    scaledMoveDistance * FixedInt.FromInt(DirZ[direction]);
                player.position.x += moveX;
                player.position.z += moveZ;

                if (moveX._raw != 0 || moveZ._raw != 0)
                {
                    player.facing = new FixedVector3(
                        moveX,
                        FixedInt.Zero,
                        moveZ).normalized;
                }
            }

            int pickupArrayIndex = BallPossessionSystem.TryPickupFreeBall(
                players,
                ball,
                inputs,
                CourtConstant.PickupRadius);

            int shooterPlayerIndex = -1;
            bool heldInvariantValid = true;
            if (ball.state == BallEntity.EState.Held)
            {
                int holderIndex = ball.holderPlayerIndex;
                if (holderIndex < 0 || holderIndex >= players.Length)
                {
                    heldInvariantValid = false;
                }
                else
                {
                    PlayerEntity holder = players[holderIndex];
                    if (holderIndex != pickupArrayIndex && inputs[holderIndex].shoot)
                    {
                        FixedVector3 target = new FixedVector3(
                            FixedInt.Zero,
                            CourtConstant.HoopY,
                            CourtConstant.HoopZ);
                        if (BallShotSystem.TryShootSilently(
                            holder,
                            stateMachines[holderIndex],
                            ball,
                            target,
                            CourtConstant.ShotFlightFrames,
                            deltaTime))
                        {
                            shooterPlayerIndex = holderIndex;
                        }
                    }

                    if (ball.state == BallEntity.EState.Held)
                        heldInvariantValid = BallPossessionSystem.TryUpdateHeldBall(holder, ball);
                }
            }

            if (ball.state == BallEntity.EState.Airborne ||
                ball.state == BallEntity.EState.Free ||
                ball.state == BallEntity.EState.Scored)
            {
                BallPhysicsSystem.Update(ball, deltaTime);
            }

            return new FrameSimulationResult(
                previousBallState,
                previousBallY,
                shooterPlayerIndex,
                heldInvariantValid);
        }

        private static void ValidateWorld(
            PlayerEntity[] players,
            PlayerStateMachine[] stateMachines,
            BallEntity ball,
            FrameInput[] inputs)
        {
            if (players == null)
                throw new ArgumentNullException(nameof(players));
            if (stateMachines == null)
                throw new ArgumentNullException(nameof(stateMachines));
            if (ball == null)
                throw new ArgumentNullException(nameof(ball));
            if (inputs == null)
                throw new ArgumentNullException(nameof(inputs));
            if (players.Length != 2 || stateMachines.Length != 2 || inputs.Length != 2)
                throw new ArgumentException("P1-B 最小世界必须恰好包含两名球员及两路输入。");

            for (int i = 0; i < players.Length; i++)
            {
                if (players[i] == null || stateMachines[i] == null)
                    throw new ArgumentException($"球员世界在索引 {i} 处不完整。");
            }
        }
    }
}

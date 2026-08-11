namespace FrameSyncDemo
{
    /// <summary>
    /// 投篮系统 — 使用固定飞行帧数反算确定性初速度，并原子释放球权。
    /// </summary>
    public static class BallShotSystem
    {
        public static bool TryShoot(
            PlayerEntity player,
            PlayerStateMachine stateMachine,
            BallEntity ball,
            FixedVector3 target,
            int flightFrames,
            FixedInt deltaTime)
        {
            return TryShoot(
                player,
                stateMachine,
                ball,
                target,
                flightFrames,
                deltaTime,
                true);
        }

        internal static bool TryShootSilently(
            PlayerEntity player,
            PlayerStateMachine stateMachine,
            BallEntity ball,
            FixedVector3 target,
            int flightFrames,
            FixedInt deltaTime)
        {
            return TryShoot(
                player,
                stateMachine,
                ball,
                target,
                flightFrames,
                deltaTime,
                false);
        }

        private static bool TryShoot(
            PlayerEntity player,
            PlayerStateMachine stateMachine,
            BallEntity ball,
            FixedVector3 target,
            int flightFrames,
            FixedInt deltaTime,
            bool emitStateLogs)
        {
            if (player == null || stateMachine == null || ball == null)
                return false;

            if (!player.hasBall ||
                ball.state != BallEntity.EState.Held ||
                ball.holderPlayerIndex != player.playerIndex ||
                flightFrames <= 0 ||
                deltaTime._raw <= 0)
                return false;

            FixedVector3 facing = new FixedVector3(
                player.facing.x,
                FixedInt.Zero,
                player.facing.z).normalized;
            if (facing == FixedVector3.Zero)
                facing = FixedVector3.Forward;

            FixedVector3 releasePosition = player.position
                + FixedVector3.Up * CourtConstant.ShotReleaseHeight
                + facing * CourtConstant.ShotForwardOffset;
            FixedInt frameCount = FixedInt.FromInt(flightFrames);
            FixedInt totalTime = frameCount * deltaTime;
            FixedVector3 delta = target - releasePosition;
            FixedInt gravityCorrection = FixedInt.Half
                * CourtConstant.Gravity
                * deltaTime
                * FixedInt.FromInt(flightFrames + 1);
            FixedVector3 initialVelocity = new FixedVector3(
                delta.x / totalTime,
                delta.y / totalTime - gravityCorrection,
                delta.z / totalTime);

            PlayerEntity.EState previousState = player.state;
            bool changedToReady = emitStateLogs
                ? stateMachine.TryChangeState(PlayerEntity.EState.ShootReady)
                : stateMachine.TryChangeStateSilently(PlayerEntity.EState.ShootReady);
            if (!changedToReady)
                return false;

            bool changedToShooting = emitStateLogs
                ? stateMachine.TryChangeState(PlayerEntity.EState.Shooting)
                : stateMachine.TryChangeStateSilently(PlayerEntity.EState.Shooting);
            if (!changedToShooting)
            {
                player.state = previousState;
                return false;
            }

            ball.position = releasePosition;
            ball.velocity = initialVelocity;
            player.hasBall = false;
            ball.holderPlayerIndex = -1;
            ball.state = BallEntity.EState.Airborne;
            return true;
        }
    }
}

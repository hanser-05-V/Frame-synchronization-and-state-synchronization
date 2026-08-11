namespace FrameSyncDemo
{
    /// <summary>
    /// 持球系统 — 校验球权不变量并更新 Held 状态的固定逻辑挂点。
    /// </summary>
    public static class BallPossessionSystem
    {
        public static int TryPickupFreeBall(
            PlayerEntity[] players,
            BallEntity ball,
            FrameInput[] inputs,
            FixedInt pickupRadius)
        {
            if (players == null || ball == null || inputs == null ||
                ball.state != BallEntity.EState.Free)
                return -1;

            FixedInt pickupRadiusSquared = pickupRadius * pickupRadius;
            int winnerArrayIndex = -1;
            int winnerPlayerIndex = int.MaxValue;
            FixedInt winnerDistanceSquared = FixedInt.Zero;

            for (int i = 0; i < players.Length && i < inputs.Length; i++)
            {
                PlayerEntity player = players[i];
                if (player == null || !inputs[i].pickupPressed)
                    continue;

                FixedInt deltaX = player.position.x - ball.position.x;
                FixedInt deltaZ = player.position.z - ball.position.z;
                FixedInt distanceSquared = deltaX * deltaX + deltaZ * deltaZ;
                if (distanceSquared._raw > pickupRadiusSquared._raw)
                    continue;

                bool isBetterCandidate = winnerArrayIndex < 0 ||
                    distanceSquared._raw < winnerDistanceSquared._raw ||
                    (distanceSquared._raw == winnerDistanceSquared._raw &&
                     player.playerIndex < winnerPlayerIndex);
                if (!isBetterCandidate)
                    continue;

                winnerArrayIndex = i;
                winnerPlayerIndex = player.playerIndex;
                winnerDistanceSquared = distanceSquared;
            }

            if (winnerArrayIndex < 0)
                return -1;

            for (int i = 0; i < players.Length; i++)
                players[i].hasBall = i == winnerArrayIndex;

            ball.state = BallEntity.EState.Held;
            ball.holderPlayerIndex = winnerPlayerIndex;
            ball.velocity = FixedVector3.Zero;
            TryUpdateHeldBall(players[winnerArrayIndex], ball);
            return winnerArrayIndex;
        }

        public static bool TryUpdateHeldBall(PlayerEntity player, BallEntity ball)
        {
            if (player == null || ball == null)
                return false;

            if (!player.hasBall ||
                ball.state != BallEntity.EState.Held ||
                ball.holderPlayerIndex != player.playerIndex)
                return false;

            FixedVector3 facing = new FixedVector3(
                player.facing.x,
                FixedInt.Zero,
                player.facing.z).normalized;
            if (facing == FixedVector3.Zero)
                facing = FixedVector3.Forward;

            ball.position = player.position
                + FixedVector3.Up * CourtConstant.HeldBallHeight
                + facing * CourtConstant.HeldBallForwardOffset;
            ball.velocity = FixedVector3.Zero;
            return true;
        }
    }
}

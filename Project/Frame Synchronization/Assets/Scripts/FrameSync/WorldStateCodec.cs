using System;

namespace FrameSyncDemo
{
    public static class WorldStateCodec
    {
        public static SimulationWorldState Capture(
            int frameID,
            PlayerEntity[] players,
            BallEntity ball)
        {
            ValidateRuntimeWorld(players, ball);

            return new SimulationWorldState
            {
                frameID = frameID,
                player0 = CapturePlayer(players[0]),
                player1 = CapturePlayer(players[1]),
                ball = new SimulationBallState
                {
                    position = ball.position,
                    velocity = ball.velocity,
                    state = (int)ball.state,
                    holderPlayerIndex = ball.holderPlayerIndex
                }
            };
        }

        public static void Restore(
            in SimulationWorldState state,
            PlayerEntity[] players,
            BallEntity ball)
        {
            ValidateRuntimeWorld(players, ball);

            RestorePlayer(state.player0, players[0]);
            RestorePlayer(state.player1, players[1]);
            ball.position = state.ball.position;
            ball.velocity = state.ball.velocity;
            ball.state = (BallEntity.EState)state.ball.state;
            ball.holderPlayerIndex = state.ball.holderPlayerIndex;
        }

        public static void ValidateRuntimeWorld(
            PlayerEntity[] players,
            BallEntity ball)
        {
            if (players == null || players.Length != 2 ||
                players[0] == null || players[1] == null)
            {
                throw new ArgumentException(
                    "A deterministic world requires exactly two non-null players.",
                    nameof(players));
            }

            if (ball == null)
                throw new ArgumentNullException(nameof(ball));
        }

        private static SimulationPlayerState CapturePlayer(PlayerEntity player)
        {
            return new SimulationPlayerState
            {
                position = player.position,
                facing = player.facing,
                state = (int)player.state,
                hasBall = player.hasBall
            };
        }

        private static void RestorePlayer(
            in SimulationPlayerState state,
            PlayerEntity player)
        {
            player.position = state.position;
            player.facing = state.facing;
            player.state = (PlayerEntity.EState)state.state;
            player.hasBall = state.hasBall;
        }
    }
}

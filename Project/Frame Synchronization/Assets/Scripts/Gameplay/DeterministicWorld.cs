using System;

namespace FrameSyncDemo
{
    public sealed class DeterministicWorld
    {
        private readonly PlayerEntity[] _players;
        private readonly PlayerStateMachine[] _stateMachines;
        private readonly BallEntity _ball;
        private FixedInt _moveDistance;
        private readonly FixedInt _deltaTime;

        public DeterministicWorld(
            PlayerEntity[] players,
            PlayerStateMachine[] stateMachines,
            BallEntity ball,
            FixedInt moveDistance,
            FixedInt deltaTime)
        {
            WorldStateCodec.ValidateRuntimeWorld(players, ball);
            if (stateMachines == null || stateMachines.Length != 2 ||
                stateMachines[0] == null || stateMachines[1] == null)
            {
                throw new ArgumentException(
                    "A deterministic world requires exactly two non-null state machines.",
                    nameof(stateMachines));
            }

            _players = players;
            _stateMachines = stateMachines;
            _ball = ball;
            _moveDistance = moveDistance;
            _deltaTime = deltaTime;
        }

        public FrameSimulationResult Step(FrameInput[] inputs)
        {
            return FrameSimulationSystem.Step(
                _players,
                _stateMachines,
                _ball,
                inputs,
                _moveDistance,
                _deltaTime);
        }

        public void SetMoveDistance(FixedInt moveDistance)
        {
            _moveDistance = moveDistance;
        }

        public SimulationWorldState Capture(int frameID)
        {
            return WorldStateCodec.Capture(frameID, _players, _ball);
        }

        public void Restore(in SimulationWorldState state)
        {
            WorldStateCodec.Restore(state, _players, _ball);
        }

        public static DeterministicWorld CreateIsolated(
            in SimulationWorldState initialWorld,
            FixedInt moveDistance,
            FixedInt deltaTime)
        {
            var playerZero = new PlayerEntity();
            playerZero.Reset(FixedVector3.Zero, 0);
            var playerOne = new PlayerEntity();
            playerOne.Reset(FixedVector3.Zero, 1);
            var players = new[] { playerZero, playerOne };
            var stateMachines = new[]
            {
                new PlayerStateMachine(playerZero),
                new PlayerStateMachine(playerOne)
            };
            var ball = new BallEntity();
            ball.Reset(FixedVector3.Zero);

            var world = new DeterministicWorld(
                players,
                stateMachines,
                ball,
                moveDistance,
                deltaTime);
            world.Restore(initialWorld);
            return world;
        }
    }
}

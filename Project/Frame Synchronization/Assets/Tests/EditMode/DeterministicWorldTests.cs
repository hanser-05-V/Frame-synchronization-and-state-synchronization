using System;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class DeterministicWorldTests
    {
        private static readonly FixedInt MoveDistance = FixedInt.FromFloat(0.165f);

        [Test]
        public void Step_MovementInput_ProducesCurrentFrameSimulationResult()
        {
            RuntimeWorld runtime = CreateRuntimeWorld();
            var world = CreateWorld(runtime);

            FrameSimulationResult result = world.Step(new[]
            {
                new FrameInput(1, 0),
                default
            });

            Assert.Greater(runtime.players[0].position.z._raw, FixedInt.Zero._raw);
            Assert.IsTrue(result.heldInvariantValid);
        }

        [Test]
        public void SetMoveDistance_RuntimeValueChanges_NextStepUsesLatestValue()
        {
            RuntimeWorld runtime = CreateRuntimeWorld();
            var world = CreateWorld(runtime);
            FixedInt initialZ = runtime.players[0].position.z;
            FixedInt updatedDistance = FixedInt.FromInt(2);

            world.SetMoveDistance(updatedDistance);
            world.Step(new[]
            {
                new FrameInput(1, 0),
                default
            });

            Assert.AreEqual(initialZ + updatedDistance, runtime.players[0].position.z);
        }

        [Test]
        public void CaptureRestore_CompleteState_RoundTripsThroughWorldModule()
        {
            RuntimeWorld runtime = CreateRuntimeWorld();
            var world = CreateWorld(runtime);
            SimulationWorldState expected = world.Capture(7);
            world.Step(new[] { new FrameInput(3, 0), new FrameInput(7, 0) });

            world.Restore(expected);

            SimulationWorldState actual = world.Capture(7);
            Assert.AreEqual(expected.player0.position, actual.player0.position);
            Assert.AreEqual(expected.player1.position, actual.player1.position);
            Assert.AreEqual(expected.ball.position, actual.ball.position);
            Assert.AreEqual(expected.ball.holderPlayerIndex, actual.ball.holderPlayerIndex);
        }

        [Test]
        public void Step_InvalidInputCount_ThrowsExplicitly()
        {
            RuntimeWorld runtime = CreateRuntimeWorld();
            var world = CreateWorld(runtime);

            Assert.Throws<ArgumentNullException>(() => world.Step(null));
            Assert.Throws<ArgumentException>(
                () => world.Step(new[] { default(FrameInput) }));
        }

        [Test]
        public void Constructor_InvalidWorld_ThrowsExplicitly()
        {
            RuntimeWorld runtime = CreateRuntimeWorld();

            Assert.Throws<ArgumentException>(() => new DeterministicWorld(
                new[] { runtime.players[0] },
                runtime.stateMachines,
                runtime.ball,
                MoveDistance,
                CourtConstant.LogicDeltaTime));
            Assert.Throws<ArgumentException>(() => new DeterministicWorld(
                runtime.players,
                new[] { runtime.stateMachines[0] },
                runtime.ball,
                MoveDistance,
                CourtConstant.LogicDeltaTime));
            Assert.Throws<ArgumentNullException>(() => new DeterministicWorld(
                runtime.players,
                runtime.stateMachines,
                null,
                MoveDistance,
                CourtConstant.LogicDeltaTime));
        }

        private static DeterministicWorld CreateWorld(RuntimeWorld runtime)
        {
            return new DeterministicWorld(
                runtime.players,
                runtime.stateMachines,
                runtime.ball,
                MoveDistance,
                CourtConstant.LogicDeltaTime);
        }

        private static RuntimeWorld CreateRuntimeWorld()
        {
            var player0 = new PlayerEntity();
            player0.Reset(
                new FixedVector3(FixedInt.FromInt(-3), FixedInt.Zero, FixedInt.Zero),
                0);
            var player1 = new PlayerEntity();
            player1.Reset(
                new FixedVector3(FixedInt.FromInt(3), FixedInt.Zero, FixedInt.Zero),
                1);
            var players = new[] { player0, player1 };
            var ball = new BallEntity();
            ball.Reset(FixedVector3.Zero);
            player1.hasBall = true;
            ball.state = BallEntity.EState.Held;
            ball.holderPlayerIndex = 1;
            BallPossessionSystem.TryUpdateHeldBall(player1, ball);

            return new RuntimeWorld(
                players,
                new[]
                {
                    new PlayerStateMachine(player0),
                    new PlayerStateMachine(player1)
                },
                ball);
        }

        private sealed class RuntimeWorld
        {
            public readonly PlayerEntity[] players;
            public readonly PlayerStateMachine[] stateMachines;
            public readonly BallEntity ball;

            public RuntimeWorld(
                PlayerEntity[] players,
                PlayerStateMachine[] stateMachines,
                BallEntity ball)
            {
                this.players = players;
                this.stateMachines = stateMachines;
                this.ball = ball;
            }
        }
    }
}

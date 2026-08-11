using NUnit.Framework;
using UnityEngine;

namespace FrameSyncDemo.Tests
{
    public class FrameSimulationSystemTests
    {
        [Test]
        public void Step_FreeBallPickupAtRadius_TransfersPossessionAtomically()
        {
            World world = CreateFreeBallWorld(
                player0X: FixedInt.FromFloat(1.25f),
                player1X: FixedInt.FromInt(3));
            FrameInput[] inputs = CreateInputs(player0PicksUp: true);

            FrameSimulationSystem.Step(
                world.players,
                world.stateMachines,
                world.ball,
                inputs,
                FixedInt.Zero,
                CourtConstant.LogicDeltaTime);

            Assert.IsTrue(world.players[0].hasBall);
            Assert.IsFalse(world.players[1].hasBall);
            Assert.AreEqual(BallEntity.EState.Held, world.ball.state);
            Assert.AreEqual(0, world.ball.holderPlayerIndex);
            Assert.AreEqual(FixedVector3.Zero, world.ball.velocity);
        }

        [Test]
        public void Step_FreeBallPickupOutsideRadius_DoesNotTransferPossession()
        {
            World world = CreateFreeBallWorld(
                player0X: FixedInt.FromFloat(1.251f),
                player1X: FixedInt.FromInt(3));
            FrameInput[] inputs = CreateInputs(player0PicksUp: true);

            FrameSimulationSystem.Step(
                world.players,
                world.stateMachines,
                world.ball,
                inputs,
                FixedInt.Zero,
                FixedInt.Zero);

            Assert.IsFalse(world.players[0].hasBall);
            Assert.IsFalse(world.players[1].hasBall);
            Assert.AreEqual(BallEntity.EState.Free, world.ball.state);
            Assert.AreEqual(-1, world.ball.holderPlayerIndex);
        }

        [Test]
        public void Step_SimultaneousPickupCandidates_SelectsNearestPlayer()
        {
            World world = CreateFreeBallWorld(
                player0X: FixedInt.FromInt(1),
                player1X: FixedInt.FromFloat(0.5f));
            FrameInput[] inputs = CreateInputs(
                player0PicksUp: true,
                player1PicksUp: true);

            FrameSimulationSystem.Step(
                world.players,
                world.stateMachines,
                world.ball,
                inputs,
                FixedInt.Zero,
                CourtConstant.LogicDeltaTime);

            Assert.IsFalse(world.players[0].hasBall);
            Assert.IsTrue(world.players[1].hasBall);
            Assert.AreEqual(1, world.ball.holderPlayerIndex);
        }

        [Test]
        public void Step_EquidistantPickupCandidates_SelectsLowerPlayerIndex()
        {
            World world = CreateFreeBallWorld(
                player0X: FixedInt.FromInt(-1),
                player1X: FixedInt.FromInt(1));
            FrameInput[] inputs = CreateInputs(
                player0PicksUp: true,
                player1PicksUp: true);

            FrameSimulationSystem.Step(
                world.players,
                world.stateMachines,
                world.ball,
                inputs,
                FixedInt.Zero,
                CourtConstant.LogicDeltaTime);

            Assert.IsTrue(world.players[0].hasBall);
            Assert.IsFalse(world.players[1].hasBall);
            Assert.AreEqual(0, world.ball.holderPlayerIndex);
        }

        [Test]
        public void Step_PickupAndShootReleasedInSameFrame_KeepsBallHeld()
        {
            World world = CreateFreeBallWorld(
                player0X: FixedInt.FromInt(1),
                player1X: FixedInt.FromInt(3));
            FrameInput[] inputs = CreateInputs(
                player0Shoots: true,
                player0PicksUp: true);

            FrameSimulationResult result = FrameSimulationSystem.Step(
                world.players,
                world.stateMachines,
                world.ball,
                inputs,
                FixedInt.Zero,
                CourtConstant.LogicDeltaTime);

            Assert.AreEqual(-1, result.shooterPlayerIndex);
            Assert.IsTrue(world.players[0].hasBall);
            Assert.AreEqual(BallEntity.EState.Held, world.ball.state);
            Assert.AreEqual(0, world.ball.holderPlayerIndex);
        }

        [Test]
        public void Step_HolderShoots_ReleasesAndAdvancesBallInSameFrame()
        {
            World world = CreateWorld(1);

            FrameSimulationResult result = FrameSimulationSystem.Step(
                world.players,
                world.stateMachines,
                world.ball,
                CreateInputs(player1Shoots: true),
                FixedInt.Zero,
                CourtConstant.LogicDeltaTime);

            Assert.AreEqual(BallEntity.EState.Held, result.previousBallState);
            Assert.AreEqual(1, result.shooterPlayerIndex);
            Assert.IsTrue(result.heldInvariantValid);
            Assert.IsFalse(world.players[1].hasBall);
            Assert.AreEqual(PlayerEntity.EState.Shooting, world.players[1].state);
            Assert.AreEqual(-1, world.ball.holderPlayerIndex);
            Assert.AreEqual(BallEntity.EState.Airborne, world.ball.state);
            Assert.AreNotEqual(0, world.ball.velocity.y._raw);
            Assert.Greater(world.ball.position.z._raw, world.players[1].position.z._raw);
        }

        [Test]
        public void Step_StateChanges_DoesNotEmitFsmLogs()
        {
            World world = CreateWorld(1);
            int fsmLogCount = 0;
            Application.LogCallback callback = (condition, stackTrace, type) =>
            {
                if (condition.StartsWith("[FSM]"))
                    fsmLogCount++;
            };

            Application.logMessageReceived += callback;
            try
            {
                FrameSimulationSystem.Step(
                    world.players,
                    world.stateMachines,
                    world.ball,
                    CreateInputs(player1Shoots: true, player1Direction: 1),
                    FixedInt.FromFloat(0.165f),
                    CourtConstant.LogicDeltaTime);
            }
            finally
            {
                Application.logMessageReceived -= callback;
            }

            Assert.AreEqual(0, fsmLogCount);
        }

        [TestCase(1000, 707)]
        [TestCase(165, 116)]
        public void Step_DiagonalInput_HasSameNominalSpeedAsCardinalInput(
            int moveDistanceRaw,
            int expectedDiagonalComponentRaw)
        {
            World cardinal = CreateWorld(1);
            World diagonal = CreateWorld(1);
            var moveDistance = new FixedInt(moveDistanceRaw);
            int cardinalStartX = cardinal.players[0].position.x._raw;
            int diagonalStartX = diagonal.players[0].position.x._raw;
            int diagonalStartZ = diagonal.players[0].position.z._raw;

            FrameSimulationSystem.Step(
                cardinal.players,
                cardinal.stateMachines,
                cardinal.ball,
                CreateInputs(player0Direction: 3),
                moveDistance,
                CourtConstant.LogicDeltaTime);
            FrameSimulationSystem.Step(
                diagonal.players,
                diagonal.stateMachines,
                diagonal.ball,
                CreateInputs(player0Direction: 2),
                moveDistance,
                CourtConstant.LogicDeltaTime);

            int cardinalX = cardinal.players[0].position.x._raw - cardinalStartX;
            int diagonalX = diagonal.players[0].position.x._raw - diagonalStartX;
            int diagonalZ = diagonal.players[0].position.z._raw - diagonalStartZ;
            long diagonalSquared = (long)diagonalX * diagonalX +
                (long)diagonalZ * diagonalZ;
            double diagonalDistance = System.Math.Sqrt(diagonalSquared);

            Assert.AreEqual(moveDistance._raw, cardinalX);
            Assert.AreEqual(expectedDiagonalComponentRaw, diagonalX);
            Assert.AreEqual(expectedDiagonalComponentRaw, diagonalZ);
            Assert.LessOrEqual(
                System.Math.Abs(diagonalDistance - cardinalX),
                1d);
        }

        [Test]
        public void RestoreAndReplay_PredictedPickupRejected_RestoresFreeWorld()
        {
            World authoritative = CreateFreeBallWorld(FixedInt.FromInt(-1), FixedInt.FromInt(1));
            World predicted = CreateFreeBallWorld(FixedInt.FromInt(-1), FixedInt.FromInt(1));
            var prediction = new PredictionSystem();
            prediction.Init();
            prediction.TakeWorldSnapshot(0, predicted.players, predicted.ball);

            Step(authoritative, CreateInputs());
            Step(predicted, CreateInputs(player0PicksUp: true));
            Assert.AreEqual(BallEntity.EState.Held, predicted.ball.state);

            Assert.IsTrue(prediction.RestoreWorldSnapshot(
                0, predicted.players, predicted.ball));
            Step(predicted, CreateInputs());

            AssertWorldState(authoritative, predicted);
            Assert.AreEqual(BallEntity.EState.Free, predicted.ball.state);
            Assert.IsFalse(predicted.players[0].hasBall);
            Assert.IsFalse(predicted.players[1].hasBall);
        }

        [Test]
        public void RestoreAndReplay_DifferentPredictedHolders_ConvergeToAuthoritativeHash()
        {
            World first = CreateFreeBallWorld(FixedInt.FromInt(-1), FixedInt.FromInt(1));
            World second = CreateFreeBallWorld(FixedInt.FromInt(-1), FixedInt.FromInt(1));
            var firstPrediction = new PredictionSystem();
            var secondPrediction = new PredictionSystem();
            firstPrediction.Init();
            secondPrediction.Init();
            firstPrediction.TakeWorldSnapshot(0, first.players, first.ball);
            secondPrediction.TakeWorldSnapshot(0, second.players, second.ball);

            Step(first, CreateInputs(player0PicksUp: true));
            Step(second, CreateInputs(player1PicksUp: true));
            Assert.AreEqual(0, first.ball.holderPlayerIndex);
            Assert.AreEqual(1, second.ball.holderPlayerIndex);

            Assert.IsTrue(firstPrediction.RestoreWorldSnapshot(0, first.players, first.ball));
            Assert.IsTrue(secondPrediction.RestoreWorldSnapshot(0, second.players, second.ball));
            FrameInput[] authoritative = CreateInputs(
                player0PicksUp: true,
                player1PicksUp: true);
            Step(first, authoritative);
            Step(second, authoritative);
            FrameSnapshot firstSnapshot = firstPrediction.TakeWorldSnapshot(
                1, first.players, first.ball);
            FrameSnapshot secondSnapshot = secondPrediction.TakeWorldSnapshot(
                1, second.players, second.ball);

            AssertWorldState(first, second);
            Assert.AreEqual(0, first.ball.holderPlayerIndex);
            Assert.AreEqual(
                WorldHash.Compute(firstSnapshot, 1),
                WorldHash.Compute(secondSnapshot, 1));
        }

        [Test]
        public void RestoreAndReplay_CorrectedShotInput_MatchesAuthoritativeWorld()
        {
            World authoritative = CreateWorld(1);
            World predicted = CreateWorld(1);
            var prediction = new PredictionSystem();
            prediction.Init();

            Step(authoritative, CreateInputs(player0Direction: 3));
            Step(predicted, CreateInputs(player0Direction: 3));
            prediction.TakeWorldSnapshot(0, predicted.players, predicted.ball);

            Step(authoritative, CreateInputs(player1Shoots: true, player1Direction: 1));
            Step(predicted, CreateInputs(player1Direction: 1));
            Step(authoritative, CreateInputs(player0Direction: 8, player1Direction: 7));
            Step(predicted, CreateInputs(player0Direction: 8, player1Direction: 7));

            Assert.AreNotEqual(authoritative.ball.state, predicted.ball.state);
            Assert.IsTrue(prediction.RestoreWorldSnapshot(0, predicted.players, predicted.ball));

            Step(predicted, CreateInputs(player1Shoots: true, player1Direction: 1));
            prediction.TakeWorldSnapshot(1, predicted.players, predicted.ball);
            Step(predicted, CreateInputs(player0Direction: 8, player1Direction: 7));
            prediction.TakeWorldSnapshot(2, predicted.players, predicted.ball);

            AssertWorldState(authoritative, predicted);
        }

        private static void Step(World world, FrameInput[] inputs)
        {
            FrameSimulationSystem.Step(
                world.players,
                world.stateMachines,
                world.ball,
                inputs,
                FixedInt.FromFloat(0.165f),
                CourtConstant.LogicDeltaTime);
        }

        private static World CreateWorld(int holderPlayerIndex)
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
            var stateMachines = new[]
            {
                new PlayerStateMachine(player0),
                new PlayerStateMachine(player1)
            };
            var ball = new BallEntity
            {
                position = FixedVector3.Zero,
                velocity = FixedVector3.Zero,
                state = BallEntity.EState.Held,
                holderPlayerIndex = holderPlayerIndex
            };
            players[holderPlayerIndex].hasBall = true;
            Assert.IsTrue(BallPossessionSystem.TryUpdateHeldBall(players[holderPlayerIndex], ball));
            return new World(players, stateMachines, ball);
        }

        private static World CreateFreeBallWorld(
            FixedInt player0X,
            FixedInt player1X)
        {
            var player0 = new PlayerEntity();
            player0.Reset(
                new FixedVector3(player0X, FixedInt.Zero, FixedInt.Zero),
                0);

            var player1 = new PlayerEntity();
            player1.Reset(
                new FixedVector3(player1X, FixedInt.Zero, FixedInt.Zero),
                1);

            var players = new[] { player0, player1 };
            var stateMachines = new[]
            {
                new PlayerStateMachine(player0),
                new PlayerStateMachine(player1)
            };
            var ball = new BallEntity
            {
                position = FixedVector3.Zero,
                velocity = new FixedVector3(
                    FixedInt.FromInt(1),
                    FixedInt.FromInt(1),
                    FixedInt.FromInt(1)),
                state = BallEntity.EState.Free,
                holderPlayerIndex = -1
            };
            return new World(players, stateMachines, ball);
        }

        private static FrameInput[] CreateInputs(
            bool player0Shoots = false,
            bool player1Shoots = false,
            byte player0Direction = 0,
            byte player1Direction = 0,
            bool player0PicksUp = false,
            bool player1PicksUp = false)
        {
            var inputs = new[]
            {
                new FrameInput(player0Direction, player0Shoots ? (byte)1 : (byte)0),
                new FrameInput(player1Direction, player1Shoots ? (byte)1 : (byte)0)
            };
            inputs[0].pickupPressed = player0PicksUp;
            inputs[1].pickupPressed = player1PicksUp;
            return inputs;
        }

        private static void AssertWorldState(World expected, World actual)
        {
            for (int i = 0; i < expected.players.Length; i++)
            {
                AssertVectorRaw(expected.players[i].position, actual.players[i].position);
                AssertVectorRaw(expected.players[i].facing, actual.players[i].facing);
                Assert.AreEqual(expected.players[i].state, actual.players[i].state);
                Assert.AreEqual(expected.players[i].hasBall, actual.players[i].hasBall);
            }

            AssertVectorRaw(expected.ball.position, actual.ball.position);
            AssertVectorRaw(expected.ball.velocity, actual.ball.velocity);
            Assert.AreEqual(expected.ball.state, actual.ball.state);
            Assert.AreEqual(expected.ball.holderPlayerIndex, actual.ball.holderPlayerIndex);
        }

        private static void AssertVectorRaw(FixedVector3 expected, FixedVector3 actual)
        {
            Assert.AreEqual(expected.x._raw, actual.x._raw);
            Assert.AreEqual(expected.y._raw, actual.y._raw);
            Assert.AreEqual(expected.z._raw, actual.z._raw);
        }

        private sealed class World
        {
            public readonly PlayerEntity[] players;
            public readonly PlayerStateMachine[] stateMachines;
            public readonly BallEntity ball;

            public World(
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

using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class FrameReplaySystemTests
    {
        private static readonly FixedInt MoveDistance = FixedInt.FromFloat(0.165f);

        [Test]
        public void Replay_ErrorFrameZeroWithoutSnapshot_ResetsAndReplaysFrameZero()
        {
            World world = CreateWorld(1);
            var prediction = new PredictionSystem();
            prediction.Init();
            var frameBuffer = new FrameBuffer();
            frameBuffer.AddFrame(0, CreateInputs());
            world.players[0].position = FixedVector3.One;
            world.players[1].position = FixedVector3.One;
            world.ball.state = BallEntity.EState.Free;
            int resetCount = 0;

            FrameReplayResult result = FrameReplaySystem.Replay(
                0,
                0,
                CreateInput(direction: 1, shoot: true)._raw,
                0,
                0,
                1,
                frameBuffer,
                remoteFrame => null,
                prediction,
                world.players,
                world.stateMachines,
                world.ball,
                MoveDistance,
                CourtConstant.LogicDeltaTime,
                () =>
                {
                    resetCount++;
                    ResetWorld(world, 1);
                });

            Assert.IsTrue(result.succeeded);
            Assert.AreEqual(-1, result.restoredFrame);
            Assert.AreEqual(1, result.replayedFrameCount);
            Assert.AreEqual(-1, result.missingInputFrame);
            Assert.AreEqual(1, resetCount);
            Assert.AreEqual(PlayerEntity.EState.Shooting, world.players[1].state);
            Assert.AreEqual(BallEntity.EState.Airborne, world.ball.state);

            World restoredFrameZero = CreateWorld(0);
            Assert.IsTrue(prediction.RestoreWorldSnapshot(
                0,
                restoredFrameZero.players,
                restoredFrameZero.ball));
            AssertWorldState(world, restoredFrameZero);
        }

        [Test]
        public void Replay_PreviousSnapshot_UsesActualThenCorrectionAndSnapshotsEveryFrame()
        {
            World authoritative = CreateWorld(1);
            World predicted = CreateWorld(1);
            var prediction = new PredictionSystem();
            prediction.Init();
            var frameBuffer = new FrameBuffer();
            FrameInput[] frameZeroInputs = CreateInputs(player0Direction: 3);
            FrameInput[] frameOneBufferedInputs = CreateInputs(player0Direction: 8);
            FrameInput[] frameTwoBufferedInputs = CreateInputs(player0Direction: 5);
            frameBuffer.AddFrame(0, frameZeroInputs);
            frameBuffer.AddFrame(1, frameOneBufferedInputs);
            frameBuffer.AddFrame(2, frameTwoBufferedInputs);

            Step(authoritative, frameZeroInputs);
            Step(predicted, frameZeroInputs);
            prediction.TakeWorldSnapshot(0, predicted.players, predicted.ball);

            FrameInput actualFrameOne = CreateInput(direction: 1, shoot: true);
            FrameInput correctedInput = actualFrameOne;
            Step(authoritative, new[] { frameOneBufferedInputs[0], actualFrameOne });
            World expectedFrameOne = CloneWorld(authoritative);
            Step(authoritative, new[] { frameTwoBufferedInputs[0], correctedInput });

            Step(predicted, frameOneBufferedInputs);
            Step(predicted, frameTwoBufferedInputs);
            var requestedRemoteFrames = new List<int>();
            int resetCount = 0;

            FrameReplayResult result = FrameReplaySystem.Replay(
                1,
                2,
                correctedInput._raw,
                10,
                0,
                1,
                frameBuffer,
                remoteFrame =>
                {
                    requestedRemoteFrames.Add(remoteFrame);
                    return remoteFrame == -9 ? actualFrameOne._raw : (uint?)null;
                },
                prediction,
                predicted.players,
                predicted.stateMachines,
                predicted.ball,
                MoveDistance,
                CourtConstant.LogicDeltaTime,
                () =>
                {
                    resetCount++;
                    ResetWorld(predicted, 1);
                });

            Assert.IsTrue(result.succeeded);
            Assert.AreEqual(0, result.restoredFrame);
            Assert.AreEqual(2, result.replayedFrameCount);
            Assert.AreEqual(-1, result.missingInputFrame);
            Assert.AreEqual(0, resetCount);
            CollectionAssert.AreEqual(new[] { -9, -8 }, requestedRemoteFrames);
            AssertWorldState(authoritative, predicted);

            World restoredFrameOne = CreateWorld(0);
            Assert.IsTrue(prediction.RestoreWorldSnapshot(
                1,
                restoredFrameOne.players,
                restoredFrameOne.ball));
            AssertWorldState(expectedFrameOne, restoredFrameOne);

            World restoredFrameTwo = CreateWorld(0);
            Assert.IsTrue(prediction.RestoreWorldSnapshot(
                2,
                restoredFrameTwo.players,
                restoredFrameTwo.ball));
            AssertWorldState(authoritative, restoredFrameTwo);
        }

        [Test]
        public void Replay_PredictedWorldDiverged_FinalSnapshotHashMatchesAuthoritative()
        {
            World authoritative = CreateWorld(1);
            World predicted = CreateWorld(1);
            var authoritativeSnapshots = new PredictionSystem();
            authoritativeSnapshots.Init();
            var predictedSnapshots = new PredictionSystem();
            predictedSnapshots.Init();
            var frameBuffer = new FrameBuffer();
            FrameInput[] frameZeroInputs = CreateInputs(player0Direction: 3);
            FrameInput[] frameOnePredictedInputs = CreateInputs(player0Direction: 8);
            FrameInput[] frameTwoPredictedInputs = CreateInputs(player0Direction: 5);
            frameBuffer.AddFrame(0, frameZeroInputs);
            frameBuffer.AddFrame(1, frameOnePredictedInputs);
            frameBuffer.AddFrame(2, frameTwoPredictedInputs);

            Step(authoritative, frameZeroInputs);
            Step(predicted, frameZeroInputs);
            predictedSnapshots.TakeWorldSnapshot(0, predicted.players, predicted.ball);

            FrameInput actualFrameOne = CreateInput(direction: 1, shoot: true);
            FrameInput correctedInput = actualFrameOne;
            Step(authoritative, new[] { frameOnePredictedInputs[0], actualFrameOne });
            Step(authoritative, new[] { frameTwoPredictedInputs[0], correctedInput });
            FrameSnapshot authoritativeFinal = authoritativeSnapshots.TakeWorldSnapshot(
                2,
                authoritative.players,
                authoritative.ball);

            Step(predicted, frameOnePredictedInputs);
            Step(predicted, frameTwoPredictedInputs);
            FrameSnapshot predictedFinal = predictedSnapshots.TakeWorldSnapshot(
                2,
                predicted.players,
                predicted.ball);
            Assert.AreNotEqual(
                WorldHash.Compute(authoritativeFinal),
                WorldHash.Compute(predictedFinal),
                "错误预测在回滚前必须形成可检测的完整世界差异");

            FrameReplayResult result = FrameReplaySystem.Replay(
                1,
                2,
                correctedInput._raw,
                10,
                0,
                1,
                frameBuffer,
                remoteFrame => remoteFrame == -9 ? actualFrameOne._raw : (uint?)null,
                predictedSnapshots,
                predicted.players,
                predicted.stateMachines,
                predicted.ball,
                MoveDistance,
                CourtConstant.LogicDeltaTime,
                () => ResetWorld(predicted, 1));

            Assert.IsTrue(result.succeeded);
            Assert.IsTrue(predictedSnapshots.TryGetWorldSnapshot(
                2,
                out FrameSnapshot correctedFinal));
            Assert.AreEqual(
                WorldHash.Compute(authoritativeFinal),
                WorldHash.Compute(correctedFinal));
        }

        [Test]
        public void Replay_RebuiltPredictedSuffix_ReleaseTruthTriggersNextCorrection()
        {
            World world = CreateWorld(1);
            var prediction = new PredictionSystem();
            prediction.Init();
            var frameBuffer = new FrameBuffer();
            var actualByFrame = new Dictionary<int, uint> { [0] = 0 };
            uint moveRaw = CreateInput(direction: 3)._raw;

            prediction.ResolveRemote(0, Resolve(actualByFrame), out _, out _);
            prediction.ResolveRemote(1, Resolve(actualByFrame), out _, out _);
            prediction.ResolveRemote(2, Resolve(actualByFrame), out _, out _);

            FrameInput[] idleInputs = CreateInputs();
            frameBuffer.AddFrame(0, idleInputs);
            frameBuffer.AddFrame(1, idleInputs);
            frameBuffer.AddFrame(2, idleInputs);
            prediction.TakeWorldSnapshot(0, world.players, world.ball);
            actualByFrame[1] = moveRaw;

            FrameReplayResult result = FrameReplaySystem.Replay(
                1,
                2,
                moveRaw,
                0,
                0,
                1,
                frameBuffer,
                Resolve(actualByFrame),
                prediction,
                world.players,
                world.stateMachines,
                world.ball,
                MoveDistance,
                CourtConstant.LogicDeltaTime,
                () => ResetWorld(world, 1));

            actualByFrame[2] = 0;
            actualByFrame[3] = 0;
            prediction.ResolveRemote(
                3,
                Resolve(actualByFrame),
                out int? releaseErrorFrame,
                out uint releaseCorrectRaw);

            Assert.IsTrue(result.succeeded);
            Assert.AreEqual(2, releaseErrorFrame);
            Assert.AreEqual(0u, releaseCorrectRaw);
        }

        [Test]
        public void Replay_TransientCorrectionWithMissingSuffix_DoesNotRepeatActions()
        {
            World world = CreateWorld(1);
            var prediction = new PredictionSystem();
            prediction.Init();
            var frameBuffer = new FrameBuffer();
            frameBuffer.AddFrame(0, CreateInputs());
            frameBuffer.AddFrame(1, CreateInputs());
            var actualByFrame = new Dictionary<int, uint>();
            FrameInput corrected = CreateInput(direction: 3, shoot: true);
            corrected.pickupPressed = true;
            uint sustainedRaw = CreateInput(direction: 3)._raw;

            FrameReplayResult result = FrameReplaySystem.Replay(
                0,
                1,
                corrected._raw,
                0,
                0,
                1,
                frameBuffer,
                Resolve(actualByFrame),
                prediction,
                world.players,
                world.stateMachines,
                world.ball,
                MoveDistance,
                CourtConstant.LogicDeltaTime,
                () => ResetWorld(world, 1));
            actualByFrame[1] = sustainedRaw;
            actualByFrame[2] = sustainedRaw;

            prediction.ResolveRemote(
                2,
                Resolve(actualByFrame),
                out int? repeatedErrorFrame,
                out uint repeatedCorrectRaw);

            Assert.IsTrue(result.succeeded);
            Assert.AreEqual(BallEntity.EState.Airborne, world.ball.state);
            Assert.IsNull(repeatedErrorFrame);
            Assert.AreEqual(0u, repeatedCorrectRaw);
        }

        [Test]
        public void Replay_PreErrorFrameMissingRemoteSlot_ReturnsMissingInputInsteadOfThrowing()
        {
            World world = CreateWorld(1);
            var prediction = new PredictionSystem();
            prediction.Init();
            var frameBuffer = new FrameBuffer();
            frameBuffer.AddFrame(0, new[] { CreateInput() });
            frameBuffer.AddFrame(1, CreateInputs());

            FrameReplayResult result = FrameReplaySystem.Replay(
                1,
                1,
                CreateInput(direction: 3)._raw,
                0,
                0,
                1,
                frameBuffer,
                _ => null,
                prediction,
                world.players,
                world.stateMachines,
                world.ball,
                MoveDistance,
                CourtConstant.LogicDeltaTime,
                () => ResetWorld(world, 1));

            Assert.IsFalse(result.succeeded);
            Assert.AreEqual(0, result.missingInputFrame);
            Assert.AreEqual(0, result.replayedFrameCount);
        }

        [Test]
        public void Replay_LaterInputMissing_DoesNotPartiallyMutateCurrentWorld()
        {
            World world = CreateWorld(1);
            var prediction = new PredictionSystem();
            prediction.Init();
            prediction.TakeWorldSnapshot(0, world.players, world.ball);

            world.players[0].position.x = FixedInt.FromInt(9);
            Assert.IsTrue(BallPossessionSystem.TryUpdateHeldBall(
                world.players[1],
                world.ball));
            World originalCurrentWorld = CloneWorld(world);

            var frameBuffer = new FrameBuffer();
            frameBuffer.AddFrame(1, CreateInputs());
            FrameInput correctedShoot = CreateInput(direction: 1, shoot: true);

            FrameReplayResult result = FrameReplaySystem.Replay(
                1,
                2,
                correctedShoot._raw,
                0,
                0,
                1,
                frameBuffer,
                _ => null,
                prediction,
                world.players,
                world.stateMachines,
                world.ball,
                MoveDistance,
                CourtConstant.LogicDeltaTime,
                () => ResetWorld(world, 1));

            Assert.IsFalse(result.succeeded);
            Assert.AreEqual(0, result.restoredFrame);
            Assert.AreEqual(0, result.replayedFrameCount);
            Assert.AreEqual(2, result.missingInputFrame);
            AssertWorldState(originalCurrentWorld, world);
        }

        private static void Step(World world, FrameInput[] inputs)
        {
            FrameSimulationSystem.Step(
                world.players,
                world.stateMachines,
                world.ball,
                inputs,
                MoveDistance,
                CourtConstant.LogicDeltaTime);
        }

        private static World CreateWorld(int holderPlayerIndex)
        {
            var player0 = new PlayerEntity();
            var player1 = new PlayerEntity();
            var players = new[] { player0, player1 };
            var world = new World(
                players,
                new[]
                {
                    new PlayerStateMachine(player0),
                    new PlayerStateMachine(player1)
                },
                new BallEntity());
            ResetWorld(world, holderPlayerIndex);
            return world;
        }

        private static void ResetWorld(World world, int holderPlayerIndex)
        {
            world.players[0].Reset(
                new FixedVector3(FixedInt.FromInt(-3), FixedInt.Zero, FixedInt.Zero),
                0);
            world.players[1].Reset(
                new FixedVector3(FixedInt.FromInt(3), FixedInt.Zero, FixedInt.Zero),
                1);
            world.ball.Reset(FixedVector3.Zero);
            world.players[holderPlayerIndex].hasBall = true;
            world.ball.state = BallEntity.EState.Held;
            world.ball.holderPlayerIndex = holderPlayerIndex;
            Assert.IsTrue(BallPossessionSystem.TryUpdateHeldBall(
                world.players[holderPlayerIndex],
                world.ball));
        }

        private static World CloneWorld(World source)
        {
            World clone = CreateWorld(0);
            for (int i = 0; i < source.players.Length; i++)
            {
                clone.players[i].position = source.players[i].position;
                clone.players[i].facing = source.players[i].facing;
                clone.players[i].state = source.players[i].state;
                clone.players[i].hasBall = source.players[i].hasBall;
            }

            clone.ball.position = source.ball.position;
            clone.ball.velocity = source.ball.velocity;
            clone.ball.state = source.ball.state;
            clone.ball.holderPlayerIndex = source.ball.holderPlayerIndex;
            return clone;
        }

        private static FrameInput[] CreateInputs(
            byte player0Direction = 0,
            byte player1Direction = 0)
        {
            return new[]
            {
                CreateInput(player0Direction),
                CreateInput(player1Direction)
            };
        }

        private static FrameInput CreateInput(byte direction = 0, bool shoot = false)
        {
            return new FrameInput(direction, shoot ? (byte)1 : (byte)0);
        }

        private static Func<int, uint?> Resolve(
            IReadOnlyDictionary<int, uint> actualByFrame)
        {
            return frame => actualByFrame.TryGetValue(frame, out uint raw)
                ? (uint?)raw
                : null;
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

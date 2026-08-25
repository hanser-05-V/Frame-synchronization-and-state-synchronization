using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class PostGameTransitionSystemTests
    {
        [Test]
        public void TryRestoreTerminalWorld_DifferentCurrentHeads_RestoresSameWorld()
        {
            World first = CreateWorld(-2, 2);
            World second = CreateWorld(-2, 2);
            var firstPrediction = new PredictionSystem();
            var secondPrediction = new PredictionSystem();
            firstPrediction.Init();
            secondPrediction.Init();
            firstPrediction.TakeWorldSnapshot(0, first.players, first.ball);
            secondPrediction.TakeWorldSnapshot(0, second.players, second.ball);

            first.players[0].position.x = FixedInt.FromInt(20);
            second.players[0].position.x = FixedInt.FromInt(40);
            HighlightReplayRecorder firstRecorder = CreateEndedRecorder();
            HighlightReplayRecorder secondRecorder = CreateEndedRecorder();

            Assert.IsTrue(PostGameTransitionSystem.TryRestoreTerminalWorld(
                firstRecorder, firstPrediction, first.players, first.ball, out int firstFrame));
            Assert.IsTrue(PostGameTransitionSystem.TryRestoreTerminalWorld(
                secondRecorder, secondPrediction, second.players, second.ball, out int secondFrame));

            Assert.AreEqual(0, firstFrame);
            Assert.AreEqual(firstFrame, secondFrame);
            FrameSnapshot firstSnapshot = firstPrediction.TakeWorldSnapshot(
                1, first.players, first.ball);
            FrameSnapshot secondSnapshot = secondPrediction.TakeWorldSnapshot(
                1, second.players, second.ball);
            Assert.AreEqual(
                WorldHash.Compute(firstSnapshot, firstFrame),
                WorldHash.Compute(secondSnapshot, secondFrame));
        }

        [Test]
        public void TryRestoreTerminalWorld_MissingSnapshot_DoesNotMutateWorld()
        {
            World world = CreateWorld(-2, 2);
            var prediction = new PredictionSystem();
            prediction.Init();
            HighlightReplayRecorder recorder = CreateEndedRecorder();
            FixedVector3 before = world.players[0].position;

            bool restored = PostGameTransitionSystem.TryRestoreTerminalWorld(
                recorder, prediction, world.players, world.ball, out int terminalFrame);

            Assert.IsFalse(restored);
            Assert.AreEqual(0, terminalFrame);
            Assert.AreEqual(before, world.players[0].position);
        }

        [Test]
        public void TryRestoreTerminalWorld_Coordinator_RestoresConfirmedTrackIntoPredictedRuntime()
        {
            RuntimeWorld runtime = CreateRuntimeWorld();
            var predicted = new DeterministicWorld(
                runtime.players,
                runtime.stateMachines,
                runtime.ball,
                FixedInt.FromInt(1),
                CourtConstant.LogicDeltaTime);
            var coordinator = new FrameSyncCoordinator(
                predicted,
                predicted.Capture(-1),
                FixedInt.FromInt(1),
                CourtConstant.LogicDeltaTime,
                8);
            coordinator.RecordActual(0, 0, new FrameInput(1, 0));
            coordinator.RecordActual(0, 1, default);
            FrameInputLedger.ResolvedFrame inputs = coordinator.ResolveForPrediction(0);
            Assert.IsTrue(coordinator.Advance(0, inputs).Succeeded);
            FrameInputLedger.ResolvedFrame predictedOnly =
                coordinator.ResolveForPrediction(1);
            Assert.IsTrue(coordinator.Advance(1, predictedOnly).Succeeded);
            Assert.AreEqual(1, coordinator.PredictedFrame);
            runtime.players[0].position.x = FixedInt.FromInt(99);
            HighlightReplayRecorder recorder = CreateEndedRecorder();

            bool restored = PostGameTransitionSystem.TryRestoreTerminalWorld(
                recorder,
                coordinator,
                recorder.PostGameTerminalFrame,
                out int terminalFrame);

            Assert.IsTrue(restored);
            Assert.AreEqual(0, terminalFrame);
            Assert.AreEqual(terminalFrame, coordinator.PredictedFrame);
            Assert.AreEqual(
                WorldHash.Compute(coordinator.ConfirmedWorld, 0),
                WorldHash.Compute(coordinator.PredictedWorld, 0));
            Assert.IsFalse(coordinator.TryGetSnapshot(
                WorldTrack.Predicted,
                1,
                out _));
        }

        [Test]
        public void TryRestoreTerminalWorld_ConfirmedHeadPastRecordedTerminal_UsesLatestConfirmedFrame()
        {
            RuntimeWorld runtime = CreateRuntimeWorld();
            var predicted = new DeterministicWorld(
                runtime.players,
                runtime.stateMachines,
                runtime.ball,
                FixedInt.FromInt(1),
                CourtConstant.LogicDeltaTime);
            var coordinator = new FrameSyncCoordinator(
                predicted,
                predicted.Capture(-1),
                FixedInt.FromInt(1),
                CourtConstant.LogicDeltaTime,
                8);
            for (int frame = 0; frame <= 2; frame++)
            {
                coordinator.RecordActual(frame, 0, new FrameInput(1, 0));
                coordinator.RecordActual(frame, 1, default);
                FrameInputLedger.ResolvedFrame inputs =
                    coordinator.ResolveForPrediction(frame);
                Assert.IsTrue(coordinator.Advance(frame, inputs).Succeeded);
            }
            HighlightReplayRecorder recorder = CreateEndedRecorder();

            bool restored = PostGameTransitionSystem.TryRestoreTerminalWorld(
                recorder,
                coordinator,
                0,
                out int terminalFrame);

            Assert.IsTrue(restored);
            Assert.AreEqual(2, terminalFrame);
            Assert.AreEqual(2, coordinator.ConfirmedFrame);
            Assert.AreEqual(2, coordinator.PredictedFrame);
            Assert.AreEqual(
                WorldHash.Compute(coordinator.ConfirmedWorld, 2),
                WorldHash.Compute(coordinator.PredictedWorld, 2));
            Assert.IsTrue(coordinator.TryGetSnapshot(
                WorldTrack.Predicted,
                2,
                out SimulationWorldState predictedSnapshot));
            Assert.AreEqual(
                WorldHash.Compute(coordinator.ConfirmedWorld, 2),
                WorldHash.Compute(predictedSnapshot, 2));
        }

        private static RuntimeWorld CreateRuntimeWorld()
        {
            var player0 = new PlayerEntity();
            var player1 = new PlayerEntity();
            player0.Reset(
                new FixedVector3(FixedInt.FromInt(-3), FixedInt.Zero, FixedInt.Zero),
                0);
            player1.Reset(
                new FixedVector3(FixedInt.FromInt(3), FixedInt.Zero, FixedInt.Zero),
                1);
            var players = new[] { player0, player1 };
            var ball = new BallEntity();
            ball.Reset(FixedVector3.Zero);
            player0.hasBall = true;
            ball.state = BallEntity.EState.Held;
            ball.holderPlayerIndex = 0;
            Assert.IsTrue(BallPossessionSystem.TryUpdateHeldBall(player0, ball));
            return new RuntimeWorld(
                players,
                new[]
                {
                    new PlayerStateMachine(player0),
                    new PlayerStateMachine(player1)
                },
                ball);
        }

        private static HighlightReplayRecorder CreateEndedRecorder()
        {
            var recorder = new HighlightReplayRecorder();
            FrameInput end = new FrameInput();
            end.endMatchPressed = true;
            recorder.ProcessStableFrame(
                0,
                new FrameSnapshot
                {
                    frameID = 0,
                    ballState = (int)BallEntity.EState.Free,
                    ballHolder = -1
                },
                new[] { end, new FrameInput() });
            return recorder;
        }

        private static World CreateWorld(int player0X, int player1X)
        {
            var player0 = new PlayerEntity();
            var player1 = new PlayerEntity();
            player0.Reset(new FixedVector3(FixedInt.FromInt(player0X), FixedInt.Zero, FixedInt.Zero), 0);
            player1.Reset(new FixedVector3(FixedInt.FromInt(player1X), FixedInt.Zero, FixedInt.Zero), 1);
            var ball = new BallEntity
            {
                position = FixedVector3.Zero,
                velocity = FixedVector3.Zero,
                state = BallEntity.EState.Free,
                holderPlayerIndex = -1
            };
            return new World(new[] { player0, player1 }, ball);
        }

        private sealed class World
        {
            public readonly PlayerEntity[] players;
            public readonly BallEntity ball;

            public World(PlayerEntity[] players, BallEntity ball)
            {
                this.players = players;
                this.ball = ball;
            }
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

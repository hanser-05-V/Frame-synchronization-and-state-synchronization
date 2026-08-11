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
    }
}

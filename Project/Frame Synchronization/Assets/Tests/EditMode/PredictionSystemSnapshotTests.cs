using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class PredictionSystemSnapshotTests
    {
        [Test]
        public void TakeWorldSnapshot_ValidWorld_ReturnsTheStoredCompleteSnapshot()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall();
            var prediction = new PredictionSystem();
            prediction.Init();

            FrameSnapshot captured = prediction.TakeWorldSnapshot(37, players, ball);
            bool found = prediction.TryGetWorldSnapshot(37, out FrameSnapshot stored);

            Assert.IsTrue(found);
            Assert.AreEqual(37, captured.frameID);
            Assert.AreEqual(WorldHash.Compute(captured), WorldHash.Compute(stored));
        }

        [Test]
        public void TryGetWorldSnapshot_FrameDoesNotExist_ReturnsFalseWithInvalidSnapshot()
        {
            var prediction = new PredictionSystem();
            prediction.Init();

            bool found = prediction.TryGetWorldSnapshot(404, out FrameSnapshot snapshot);

            Assert.IsFalse(found);
            Assert.IsFalse(snapshot.IsValid);
        }

        [Test]
        public void RestoreWorldSnapshot_CapturedWorldWasMutated_RestoresEveryDynamicField()
        {
            var expectedPlayers = CreatePlayers();
            var livePlayers = CreatePlayers();
            var expectedBall = CreateBall();
            var liveBall = CreateBall();
            var prediction = new PredictionSystem();
            prediction.Init();

            prediction.TakeWorldSnapshot(37, livePlayers, liveBall);
            MutateWorld(livePlayers, liveBall);

            bool restored = prediction.RestoreWorldSnapshot(37, livePlayers, liveBall);

            Assert.IsTrue(restored);
            AssertPlayerDynamicState(expectedPlayers[0], livePlayers[0]);
            AssertPlayerDynamicState(expectedPlayers[1], livePlayers[1]);
            AssertBallState(expectedBall, liveBall);
            Assert.AreEqual(10, livePlayers[0].playerIndex);
            Assert.AreEqual(11, livePlayers[1].playerIndex);
        }

        [Test]
        public void RestoreWorldSnapshot_FrameDoesNotExist_ReturnsFalseWithoutMutation()
        {
            var expectedPlayers = CreatePlayers();
            var livePlayers = CreatePlayers();
            var expectedBall = CreateBall();
            var liveBall = CreateBall();
            var prediction = new PredictionSystem();
            prediction.Init();

            bool restored = prediction.RestoreWorldSnapshot(404, livePlayers, liveBall);

            Assert.IsFalse(restored);
            AssertPlayerDynamicState(expectedPlayers[0], livePlayers[0]);
            AssertPlayerDynamicState(expectedPlayers[1], livePlayers[1]);
            AssertBallState(expectedBall, liveBall);
            Assert.AreEqual(expectedPlayers[0].playerIndex, livePlayers[0].playerIndex);
            Assert.AreEqual(expectedPlayers[1].playerIndex, livePlayers[1].playerIndex);
        }

        [Test]
        public void TakeWorldSnapshot_InvalidWorld_ThrowsArgumentException()
        {
            var prediction = new PredictionSystem();
            prediction.Init();
            var players = CreatePlayers();
            var ball = CreateBall();

            Assert.Catch<System.ArgumentException>(
                () => prediction.TakeWorldSnapshot(1, new[] { players[0] }, ball));
            Assert.Catch<System.ArgumentException>(
                () => prediction.TakeWorldSnapshot(1, new PlayerEntity[] { players[0], null }, ball));
            Assert.Catch<System.ArgumentException>(
                () => prediction.TakeWorldSnapshot(1, players, null));
        }

        [Test]
        public void RestoreWorldSnapshot_InvalidWorld_ThrowsArgumentException()
        {
            var prediction = new PredictionSystem();
            prediction.Init();
            var players = CreatePlayers();
            var ball = CreateBall();

            Assert.Catch<System.ArgumentException>(
                () => prediction.RestoreWorldSnapshot(1, null, ball));
            Assert.Catch<System.ArgumentException>(
                () => prediction.RestoreWorldSnapshot(1, new[] { players[0] }, ball));
            Assert.Catch<System.ArgumentException>(
                () => prediction.RestoreWorldSnapshot(1, players, null));
        }

        private static PlayerEntity[] CreatePlayers()
        {
            var player0 = new PlayerEntity();
            player0.Reset(
                new FixedVector3(FixedInt.FromInt(-3), FixedInt.FromInt(1), FixedInt.FromInt(2)),
                0);
            player0.facing = new FixedVector3(FixedInt.One, FixedInt.Zero, FixedInt.Zero);
            player0.state = PlayerEntity.EState.Shooting;
            player0.hasBall = false;

            var player1 = new PlayerEntity();
            player1.Reset(
                new FixedVector3(FixedInt.FromInt(4), FixedInt.FromInt(2), FixedInt.FromInt(-5)),
                1);
            player1.facing = new FixedVector3(FixedInt.Zero, FixedInt.Zero, -FixedInt.One);
            player1.state = PlayerEntity.EState.Run;
            player1.hasBall = true;

            return new[] { player0, player1 };
        }

        private static BallEntity CreateBall()
        {
            return new BallEntity
            {
                position = new FixedVector3(
                    FixedInt.FromInt(2),
                    FixedInt.FromInt(3),
                    FixedInt.FromInt(7)),
                velocity = new FixedVector3(
                    FixedInt.FromInt(-1),
                    FixedInt.FromInt(6),
                    FixedInt.FromInt(2)),
                state = BallEntity.EState.Airborne,
                holderPlayerIndex = -1
            };
        }

        private static void MutateWorld(PlayerEntity[] players, BallEntity ball)
        {
            players[0].position = FixedVector3.Zero;
            players[0].facing = FixedVector3.Back;
            players[0].state = PlayerEntity.EState.Fall;
            players[0].hasBall = true;
            players[0].playerIndex = 10;

            players[1].position = FixedVector3.One;
            players[1].facing = FixedVector3.Right;
            players[1].state = PlayerEntity.EState.Idle;
            players[1].hasBall = false;
            players[1].playerIndex = 11;

            ball.position = FixedVector3.Zero;
            ball.velocity = FixedVector3.Zero;
            ball.state = BallEntity.EState.Free;
            ball.holderPlayerIndex = 1;
        }

        private static void AssertPlayerDynamicState(PlayerEntity expected, PlayerEntity actual)
        {
            AssertVectorRaw(expected.position, actual.position);
            AssertVectorRaw(expected.facing, actual.facing);
            Assert.AreEqual(expected.state, actual.state);
            Assert.AreEqual(expected.hasBall, actual.hasBall);
        }

        private static void AssertBallState(BallEntity expected, BallEntity actual)
        {
            AssertVectorRaw(expected.position, actual.position);
            AssertVectorRaw(expected.velocity, actual.velocity);
            Assert.AreEqual(expected.state, actual.state);
            Assert.AreEqual(expected.holderPlayerIndex, actual.holderPlayerIndex);
        }

        private static void AssertVectorRaw(FixedVector3 expected, FixedVector3 actual)
        {
            Assert.AreEqual(expected.x._raw, actual.x._raw);
            Assert.AreEqual(expected.y._raw, actual.y._raw);
            Assert.AreEqual(expected.z._raw, actual.z._raw);
        }
    }
}

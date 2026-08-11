using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class BallPossessionSystemTests
    {
        [Test]
        public void TryUpdateHeldBall_ConsistentPossession_UpdatesFixedHandPointAndClearsVelocity()
        {
            var player = CreatePlayer(hasBall: true);
            var ball = CreateHeldBall();

            bool updated = BallPossessionSystem.TryUpdateHeldBall(player, ball);

            FixedVector3 expectedPosition = player.position
                + FixedVector3.Up * CourtConstant.HeldBallHeight
                + FixedVector3.Right * CourtConstant.HeldBallForwardOffset;
            Assert.IsTrue(updated);
            Assert.AreEqual(expectedPosition, ball.position);
            Assert.AreEqual(FixedVector3.Zero, ball.velocity);
            Assert.AreEqual(BallEntity.EState.Held, ball.state);
            Assert.AreEqual(player.playerIndex, ball.holderPlayerIndex);
        }

        [Test]
        public void TryUpdateHeldBall_InconsistentPossession_ReturnsFalseWithoutMutation()
        {
            var player = CreatePlayer(hasBall: false);
            var ball = CreateHeldBall();
            FixedVector3 originalPosition = ball.position;
            FixedVector3 originalVelocity = ball.velocity;

            bool updated = BallPossessionSystem.TryUpdateHeldBall(player, ball);

            Assert.IsFalse(updated);
            Assert.AreEqual(originalPosition, ball.position);
            Assert.AreEqual(originalVelocity, ball.velocity);
            Assert.AreEqual(BallEntity.EState.Held, ball.state);
            Assert.AreEqual(0, ball.holderPlayerIndex);
        }

        private static PlayerEntity CreatePlayer(bool hasBall)
        {
            var player = new PlayerEntity();
            player.Reset(
                new FixedVector3(
                    FixedInt.FromInt(1),
                    FixedInt.Zero,
                    FixedInt.FromInt(2)),
                0);
            player.facing = FixedVector3.Right;
            player.hasBall = hasBall;
            return player;
        }

        private static BallEntity CreateHeldBall()
        {
            return new BallEntity
            {
                position = new FixedVector3(
                    FixedInt.FromInt(5),
                    FixedInt.FromInt(5),
                    FixedInt.FromInt(5)),
                velocity = FixedVector3.One,
                state = BallEntity.EState.Held,
                holderPlayerIndex = 0
            };
        }
    }
}

using System;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class BallShotSystemTests
    {
        [Test]
        public void TryShoot_ValidPossession_ReleasesBallAndReachesTargetAtFixedFrame()
        {
            var player = CreatePlayer(hasBall: true);
            var stateMachine = new PlayerStateMachine(player);
            var ball = CreateHeldBall();
            var target = new FixedVector3(
                FixedInt.Zero,
                CourtConstant.HoopY,
                CourtConstant.HoopZ);

            bool shot = BallShotSystem.TryShoot(
                player,
                stateMachine,
                ball,
                target,
                CourtConstant.ShotFlightFrames,
                CourtConstant.LogicDeltaTime);

            Assert.IsTrue(shot);
            Assert.IsFalse(player.hasBall);
            Assert.AreEqual(PlayerEntity.EState.Shooting, player.state);
            Assert.AreEqual(-1, ball.holderPlayerIndex);
            Assert.AreEqual(BallEntity.EState.Airborne, ball.state);

            for (int i = 0; i < CourtConstant.ShotFlightFrames; i++)
            {
                BallPhysicsSystem.Update(ball, CourtConstant.LogicDeltaTime);
            }

            AssertFixedNear(target.x, ball.position.x, 50);
            AssertFixedNear(target.y, ball.position.y, 50);
            AssertFixedNear(target.z, ball.position.z, 50);
        }

        [Test]
        public void TryShoot_NoPossession_ReturnsFalseWithoutMutation()
        {
            var player = CreatePlayer(hasBall: false);
            var stateMachine = new PlayerStateMachine(player);
            var ball = CreateHeldBall();
            FixedVector3 originalPosition = ball.position;
            FixedVector3 originalVelocity = ball.velocity;

            bool shot = BallShotSystem.TryShoot(
                player,
                stateMachine,
                ball,
                new FixedVector3(FixedInt.Zero, CourtConstant.HoopY, CourtConstant.HoopZ),
                CourtConstant.ShotFlightFrames,
                CourtConstant.LogicDeltaTime);

            Assert.IsFalse(shot);
            Assert.AreEqual(PlayerEntity.EState.Idle, player.state);
            Assert.IsFalse(player.hasBall);
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
                    FixedInt.Zero),
                0);
            player.facing = FixedVector3.Forward;
            player.hasBall = hasBall;
            return player;
        }

        private static BallEntity CreateHeldBall()
        {
            return new BallEntity
            {
                position = new FixedVector3(FixedInt.Zero, FixedInt.One, FixedInt.Zero),
                velocity = FixedVector3.Zero,
                state = BallEntity.EState.Held,
                holderPlayerIndex = 0
            };
        }

        private static void AssertFixedNear(FixedInt expected, FixedInt actual, int toleranceRaw)
        {
            Assert.LessOrEqual(
                Math.Abs(expected._raw - actual._raw),
                toleranceRaw,
                $"Expected {expected}, but was {actual}.");
        }
    }
}

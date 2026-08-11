using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class BallPhysicsSystemTests
    {
        [Test]
        public void Update_BallCrossesHoopPlaneInsideRadius_SetsScored()
        {
            var ball = new BallEntity
            {
                position = new FixedVector3(
                    FixedInt.Zero,
                    CourtConstant.HoopY + FixedInt.FromFloat(0.05f),
                    CourtConstant.HoopZ),
                velocity = new FixedVector3(
                    FixedInt.Zero,
                    FixedInt.FromInt(-2),
                    FixedInt.Zero),
                state = BallEntity.EState.Airborne,
                holderPlayerIndex = -1
            };

            BallPhysicsSystem.Update(ball, FixedInt.FromFloat(0.033f));

            Assert.AreEqual(BallEntity.EState.Scored, ball.state);
        }

        [Test]
        public void Update_OffTargetBallHitsFloor_SetsFree()
        {
            var ball = new BallEntity
            {
                position = new FixedVector3(
                    CourtConstant.HoopRadius + FixedInt.FromInt(1),
                    FixedInt.FromFloat(0.01f),
                    CourtConstant.HoopZ),
                velocity = new FixedVector3(
                    FixedInt.Zero,
                    FixedInt.FromInt(-1),
                    FixedInt.Zero),
                state = BallEntity.EState.Airborne,
                holderPlayerIndex = -1
            };

            BallPhysicsSystem.Update(ball, CourtConstant.LogicDeltaTime);

            Assert.AreEqual(BallEntity.EState.Free, ball.state);
            Assert.AreEqual(FixedInt.Zero, ball.position.y);
            Assert.AreEqual(FixedVector3.Zero, ball.velocity);
        }

        [Test]
        public void Update_ScoredBallAboveFloor_ContinuesFalling()
        {
            var ball = new BallEntity
            {
                position = new FixedVector3(
                    FixedInt.Zero,
                    FixedInt.FromInt(2),
                    CourtConstant.HoopZ),
                velocity = new FixedVector3(
                    FixedInt.Zero,
                    FixedInt.FromInt(-1),
                    FixedInt.Zero),
                state = BallEntity.EState.Scored,
                holderPlayerIndex = -1
            };
            FixedInt previousY = ball.position.y;

            BallPhysicsSystem.Update(ball, CourtConstant.LogicDeltaTime);

            Assert.AreEqual(BallEntity.EState.Scored, ball.state);
            Assert.Less(ball.position.y._raw, previousY._raw);
        }

        [Test]
        public void Update_ScoredBallReachesFloor_BecomesFree()
        {
            var ball = new BallEntity
            {
                position = new FixedVector3(
                    FixedInt.Zero,
                    FixedInt.FromFloat(0.01f),
                    CourtConstant.HoopZ),
                velocity = new FixedVector3(
                    FixedInt.Zero,
                    FixedInt.FromInt(-1),
                    FixedInt.Zero),
                state = BallEntity.EState.Scored,
                holderPlayerIndex = -1
            };

            BallPhysicsSystem.Update(ball, CourtConstant.LogicDeltaTime);

            Assert.AreEqual(BallEntity.EState.Free, ball.state);
            Assert.AreEqual(FixedInt.Zero, ball.position.y);
            Assert.AreEqual(FixedVector3.Zero, ball.velocity);
        }

        [Test]
        public void Update_ShotScoresThenFallsToFloor_BecomesFree()
        {
            var ball = new BallEntity
            {
                position = new FixedVector3(
                    FixedInt.Zero,
                    CourtConstant.HoopY + FixedInt.FromFloat(0.05f),
                    CourtConstant.HoopZ),
                velocity = new FixedVector3(
                    FixedInt.Zero,
                    FixedInt.FromInt(-2),
                    FixedInt.Zero),
                state = BallEntity.EState.Airborne,
                holderPlayerIndex = -1
            };

            BallPhysicsSystem.Update(ball, CourtConstant.LogicDeltaTime);

            Assert.AreEqual(BallEntity.EState.Scored, ball.state);
            FixedInt scoredY = ball.position.y;

            BallPhysicsSystem.Update(ball, CourtConstant.LogicDeltaTime);

            Assert.Less(ball.position.y._raw, scoredY._raw);

            int remainingFrames = 180;
            while (ball.position.y._raw > 0 && remainingFrames-- > 0)
            {
                BallPhysicsSystem.Update(ball, CourtConstant.LogicDeltaTime);
            }

            Assert.Greater(remainingFrames, 0, "Scored ball did not reach the floor in time.");
            Assert.AreEqual(BallEntity.EState.Free, ball.state);
            Assert.AreEqual(FixedInt.Zero, ball.position.y);
            Assert.AreEqual(FixedVector3.Zero, ball.velocity);
        }
    }
}

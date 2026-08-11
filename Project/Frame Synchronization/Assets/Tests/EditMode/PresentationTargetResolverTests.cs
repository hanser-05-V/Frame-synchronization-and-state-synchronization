using NUnit.Framework;
using UnityEngine;

namespace FrameSyncDemo.Tests
{
    public class PresentationTargetResolverTests
    {
        [Test]
        public void ResolvePlayerTarget_AddsCapsuleVisualOffset()
        {
            var player = new PlayerEntity
            {
                position = new FixedVector3(
                    FixedInt.FromInt(1),
                    FixedInt.Zero,
                    FixedInt.FromInt(2))
            };

            Vector3 result = PresentationTargetResolver.ResolvePlayerTarget(player);

            Assert.AreEqual(new Vector3(1f, 0.5f, 2f), result);
        }

        [Test]
        public void ShouldSmoothPlayer_LocalIsImmediateAndRemoteIsSmoothed()
        {
            Assert.IsFalse(PresentationTargetResolver.ShouldSmoothPlayer(0, 0));
            Assert.IsTrue(PresentationTargetResolver.ShouldSmoothPlayer(1, 0));
            Assert.IsTrue(PresentationTargetResolver.ShouldSmoothPlayer(0, 1));
            Assert.IsFalse(PresentationTargetResolver.ShouldSmoothPlayer(1, 1));
        }

        [Test]
        public void ShouldSmoothBall_LocalHeldIsImmediateAndOtherStatesAreSmoothed()
        {
            var ball = new BallEntity
            {
                state = BallEntity.EState.Held,
                holderPlayerIndex = 1
            };

            Assert.IsTrue(PresentationTargetResolver.ShouldSmoothBall(ball, 0));
            Assert.IsFalse(PresentationTargetResolver.ShouldSmoothBall(ball, 1));

            ball.state = BallEntity.EState.Airborne;
            ball.holderPlayerIndex = -1;
            Assert.IsTrue(PresentationTargetResolver.ShouldSmoothBall(ball, 0));
            Assert.IsTrue(PresentationTargetResolver.ShouldSmoothBall(ball, 1));
        }

        [TestCase(false, false, false)]
        [TestCase(false, true, true)]
        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        public void ShouldSnapForPlaybackTransition_OnlyStateChangesSnap(
            bool wasPlayingBack,
            bool isPlayingBack,
            bool expected)
        {
            Assert.AreEqual(
                expected,
                PresentationTargetResolver.ShouldSnapForPlaybackTransition(
                    wasPlayingBack,
                    isPlayingBack));
        }

        [Test]
        public void ResolveBallTarget_RemoteHeld_InheritsHolderDisplayOffset()
        {
            PlayerEntity[] players = CreatePlayers();
            var ball = new BallEntity
            {
                state = BallEntity.EState.Held,
                holderPlayerIndex = 1,
                position = new FixedVector3(
                    FixedInt.FromInt(4),
                    FixedInt.FromFloat(1.3f),
                    FixedInt.FromFloat(0.8f))
            };
            Vector3[] playerDisplays =
            {
                PresentationTargetResolver.ResolvePlayerTarget(players[0]),
                new Vector3(2.25f, 0.5f, 0f)
            };

            Vector3 result = PresentationTargetResolver.ResolveBallTarget(
                ball,
                players,
                playerDisplays);

            Assert.AreEqual(new Vector3(3.25f, 1.3f, 0.8f), result);
        }

        [TestCase(BallEntity.EState.Free)]
        [TestCase(BallEntity.EState.Airborne)]
        [TestCase(BallEntity.EState.Scored)]
        public void ResolveBallTarget_NotHeld_ReturnsLogicPosition(
            BallEntity.EState state)
        {
            PlayerEntity[] players = CreatePlayers();
            var ball = new BallEntity
            {
                state = state,
                holderPlayerIndex = -1,
                position = new FixedVector3(
                    FixedInt.FromFloat(1.5f),
                    FixedInt.FromFloat(2.25f),
                    FixedInt.FromFloat(-3.5f))
            };

            Vector3 result = PresentationTargetResolver.ResolveBallTarget(
                ball,
                players,
                new[] { Vector3.one, Vector3.one * 2f });

            Assert.AreEqual(ball.position.ToVector3(), result);
        }

        [Test]
        public void ResolveBallTarget_InvalidHeldOwner_ReturnsLogicPosition()
        {
            PlayerEntity[] players = CreatePlayers();
            var ball = new BallEntity
            {
                state = BallEntity.EState.Held,
                holderPlayerIndex = 5,
                position = new FixedVector3(
                    FixedInt.FromInt(1),
                    FixedInt.FromInt(2),
                    FixedInt.FromInt(3))
            };

            Vector3 result = PresentationTargetResolver.ResolveBallTarget(
                ball,
                players,
                new[] { Vector3.zero, Vector3.zero });

            Assert.AreEqual(ball.position.ToVector3(), result);
        }

        [Test]
        public void ResolveInterpolatedBallTarget_Held_InheritsHolderVisualCorrection()
        {
            var ball = new BallEntity
            {
                state = BallEntity.EState.Held,
                holderPlayerIndex = 1
            };
            var interpolatedBallPosition = new Vector3(4f, 1.3f, 0.8f);
            Vector3[] interpolatedPlayerPositions =
            {
                new Vector3(-3f, 0.5f, 0f),
                new Vector3(3f, 0.5f, 0f)
            };
            Vector3[] displayedPlayerPositions =
            {
                new Vector3(-3f, 0.5f, 0f),
                new Vector3(2.25f, 0.5f, 0f)
            };

            Vector3 result =
                PresentationTargetResolver.ResolveInterpolatedBallTarget(
                    ball,
                    interpolatedBallPosition,
                    interpolatedPlayerPositions,
                    displayedPlayerPositions);

            Assert.AreEqual(new Vector3(3.25f, 1.3f, 0.8f), result);
        }

        [TestCase(BallEntity.EState.Free)]
        [TestCase(BallEntity.EState.Airborne)]
        [TestCase(BallEntity.EState.Scored)]
        public void ResolveInterpolatedBallTarget_NotHeld_ReturnsInterpolatedBase(
            BallEntity.EState state)
        {
            var ball = new BallEntity
            {
                state = state,
                holderPlayerIndex = -1
            };
            var interpolatedBallPosition = new Vector3(1.5f, 2.25f, -3.5f);

            Vector3 result =
                PresentationTargetResolver.ResolveInterpolatedBallTarget(
                    ball,
                    interpolatedBallPosition,
                    new[] { Vector3.zero, Vector3.one },
                    new[] { Vector3.one, Vector3.one * 2f });

            Assert.AreEqual(interpolatedBallPosition, result);
        }

        [Test]
        public void ResolveInterpolatedBallTarget_InvalidHeldData_ReturnsInterpolatedBase()
        {
            var ball = new BallEntity
            {
                state = BallEntity.EState.Held,
                holderPlayerIndex = 2
            };
            var interpolatedBallPosition = new Vector3(1f, 2f, 3f);

            Assert.AreEqual(
                interpolatedBallPosition,
                PresentationTargetResolver.ResolveInterpolatedBallTarget(
                    ball,
                    interpolatedBallPosition,
                    new[] { Vector3.zero, Vector3.one },
                    new[] { Vector3.zero, Vector3.one }));
            Assert.AreEqual(
                interpolatedBallPosition,
                PresentationTargetResolver.ResolveInterpolatedBallTarget(
                    ball,
                    interpolatedBallPosition,
                    null,
                    null));
        }

        [Test]
        public void ResolveInterpolatedBallTarget_NullBall_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(
                () => PresentationTargetResolver.ResolveInterpolatedBallTarget(
                    null,
                    Vector3.zero,
                    new Vector3[2],
                    new Vector3[2]));
        }

        [Test]
        public void ResolveBufferedBallTarget_Unattached_ReturnsBase()
        {
            var sample = new PresentationBallSample(
                new Vector3(1f, 2f, 3f),
                -1);

            Vector3 result = PresentationTargetResolver.ResolveBufferedBallTarget(
                sample,
                new[] { Vector3.zero, Vector3.one },
                new[] { Vector3.one * 2f, Vector3.one * 3f });

            Assert.AreEqual(sample.BasePosition, result);
        }

        [Test]
        public void ResolveBufferedBallTarget_Attached_InheritsHolderCorrection()
        {
            var sample = new PresentationBallSample(
                new Vector3(4f, 1.3f, 0.8f),
                1);
            Vector3[] bases =
            {
                new Vector3(-3f, 0.5f, 0f),
                new Vector3(3f, 0.5f, 0f)
            };
            Vector3[] displays =
            {
                new Vector3(-3f, 0.5f, 0f),
                new Vector3(2.25f, 0.5f, 0f)
            };

            Vector3 result = PresentationTargetResolver.ResolveBufferedBallTarget(
                sample,
                bases,
                displays);

            Assert.AreEqual(new Vector3(3.25f, 1.3f, 0.8f), result);
        }

        [Test]
        public void ResolveBufferedBallTarget_InvalidAttachment_ReturnsBase()
        {
            var sample = new PresentationBallSample(
                new Vector3(1f, 2f, 3f),
                2);

            Assert.AreEqual(
                sample.BasePosition,
                PresentationTargetResolver.ResolveBufferedBallTarget(
                    sample,
                    new Vector3[2],
                    new Vector3[2]));
            Assert.AreEqual(
                sample.BasePosition,
                PresentationTargetResolver.ResolveBufferedBallTarget(
                    sample,
                    null,
                    null));
        }

        [TestCase(
            BallEntity.EState.Held,
            0,
            BallEntity.EState.Airborne,
            -1,
            true)]
        [TestCase(
            BallEntity.EState.Airborne,
            -1,
            BallEntity.EState.Held,
            0,
            true)]
        [TestCase(
            BallEntity.EState.Held,
            0,
            BallEntity.EState.Held,
            1,
            true)]
        [TestCase(
            BallEntity.EState.Held,
            0,
            BallEntity.EState.Held,
            0,
            false)]
        [TestCase(
            BallEntity.EState.Airborne,
            -1,
            BallEntity.EState.Free,
            -1,
            false)]
        public void ShouldTransferBallVisualCorrection_AttachmentChangesOnly(
            BallEntity.EState previousState,
            int previousHolderIndex,
            BallEntity.EState currentState,
            int currentHolderIndex,
            bool expected)
        {
            Assert.AreEqual(
                expected,
                PresentationTargetResolver.ShouldTransferBallVisualCorrection(
                    previousState,
                    previousHolderIndex,
                    currentState,
                    currentHolderIndex));
        }

        [TestCase(-1, -1, false)]
        [TestCase(-1, 0, true)]
        [TestCase(0, -1, true)]
        [TestCase(0, 1, true)]
        [TestCase(1, 1, false)]
        public void ShouldTransferBallVisualCorrection_AttachmentChangesOnly(
            int previous,
            int current,
            bool expected)
        {
            Assert.AreEqual(
                expected,
                PresentationTargetResolver.ShouldTransferBallVisualCorrection(
                    previous,
                    current));
        }

        [Test]
        public void ShouldUseIndependentBallSmoother_OnlyWhenDetached()
        {
            Assert.IsTrue(
                PresentationTargetResolver.ShouldUseIndependentBallSmoother(
                    new PresentationBallSample(Vector3.zero, -1)));
            Assert.IsFalse(
                PresentationTargetResolver.ShouldUseIndependentBallSmoother(
                    new PresentationBallSample(Vector3.zero, 0)));
        }

        [TestCase(false, false, true)]
        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        public void ShouldProcessRollback_OnlyWhileRunningOutsidePlayback(
            bool isPaused,
            bool isPlayingBack,
            bool expected)
        {
            Assert.AreEqual(
                expected,
                PresentationTargetResolver.ShouldProcessRollback(
                    isPaused,
                    isPlayingBack));
        }

        private static PlayerEntity[] CreatePlayers()
        {
            var player0 = new PlayerEntity();
            player0.Reset(
                new FixedVector3(
                    FixedInt.FromInt(-3),
                    FixedInt.Zero,
                    FixedInt.Zero),
                0);

            var player1 = new PlayerEntity();
            player1.Reset(
                new FixedVector3(
                    FixedInt.FromInt(3),
                    FixedInt.Zero,
                    FixedInt.Zero),
                1);

            return new[] { player0, player1 };
        }
    }
}

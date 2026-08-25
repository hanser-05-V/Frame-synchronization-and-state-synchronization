using System;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEngine;

namespace FrameSyncDemo.Tests
{
    public class PresentationFrameInterpolatorTests
    {
        [Test]
        public void Evaluate_ViewWorld_InterpolatesExplicitTrackEndpoints()
        {
            var interpolator = new PresentationFrameInterpolator(2);
            ViewWorldState view = CreateViewWorld(
                predictedFrame: 11,
                confirmedFrame: 8,
                localFromX: -2,
                localToX: 0,
                remoteFromX: 4,
                remoteToX: 2,
                ballFromX: 5,
                ballToX: 3,
                ballState: BallEntity.EState.Airborne,
                holderPlayerIndex: -1);
            interpolator.Reset(view);
            var positions = new Vector3[2];

            interpolator.Evaluate(
                0,
                0.5f,
                positions,
                out PresentationBallSample ball);

            Assert.AreEqual(new Vector3(-1f, 0.5f, 0f), positions[0]);
            Assert.AreEqual(new Vector3(3f, 0.5f, 0f), positions[1]);
            Assert.AreEqual(new Vector3(4f, 0f, 0f), ball.BasePosition);
            Assert.AreEqual(-1, ball.AttachedPlayerIndex);
        }

        [Test]
        public void Evaluate_ViewWorldIndependentAlphas_UseEachTrackClock()
        {
            var interpolator = new PresentationFrameInterpolator(2);
            ViewWorldState view = CreateViewWorld(
                predictedFrame: 11,
                confirmedFrame: 8,
                localFromX: -2,
                localToX: 0,
                remoteFromX: 4,
                remoteToX: 2,
                ballFromX: 5,
                ballToX: 3,
                ballState: BallEntity.EState.Airborne,
                holderPlayerIndex: -1);
            interpolator.Reset(view);
            var positions = new Vector3[2];

            interpolator.Evaluate(
                0,
                0.75f,
                0.25f,
                positions,
                out PresentationBallSample ball);

            Assert.AreEqual(new Vector3(-0.5f, 0.5f, 0f), positions[0]);
            Assert.AreEqual(new Vector3(3.5f, 0.5f, 0f), positions[1]);
            Assert.AreEqual(new Vector3(4.5f, 0f, 0f), ball.BasePosition);
        }

        [Test]
        public void Evaluate_ViewWorldHeldBall_InheritsSelectedHolderEndpoint()
        {
            var interpolator = new PresentationFrameInterpolator(2);
            ViewWorldState view = CreateViewWorld(
                predictedFrame: 11,
                confirmedFrame: 8,
                localFromX: -2,
                localToX: 0,
                remoteFromX: 4,
                remoteToX: 2,
                ballFromX: 4,
                ballToX: 2,
                ballState: BallEntity.EState.Held,
                holderPlayerIndex: 1);
            interpolator.Reset(view);
            var positions = new Vector3[2];

            interpolator.Evaluate(
                0,
                0.5f,
                positions,
                out PresentationBallSample ball);

            Assert.AreEqual(1, ball.AttachedPlayerIndex);
            Assert.AreEqual(3f, ball.BasePosition.x, 0.0001f);
        }

        [Test]
        public void Evaluate_ViewWorldRemoteHeldBall_UsesConfirmedClockForHolderAndBall()
        {
            var interpolator = new PresentationFrameInterpolator(2);
            ViewWorldState view = CreateViewWorld(
                predictedFrame: 11,
                confirmedFrame: 8,
                localFromX: -2,
                localToX: 0,
                remoteFromX: 4,
                remoteToX: 2,
                ballFromX: 4,
                ballToX: 2,
                ballState: BallEntity.EState.Held,
                holderPlayerIndex: 1);
            interpolator.Reset(view);
            var positions = new Vector3[2];

            interpolator.Evaluate(
                0,
                0.75f,
                0.25f,
                positions,
                out PresentationBallSample ball);

            Assert.AreEqual(new Vector3(-0.5f, 0.5f, 0f), positions[0]);
            Assert.AreEqual(new Vector3(3.5f, 0.5f, 0f), positions[1]);
            Assert.AreEqual(1, ball.AttachedPlayerIndex);
            Assert.AreEqual(new Vector3(3.5f, 0f, 0f), ball.BasePosition);
        }

        [Test]
        public void Evaluate_ViewWorldLocalHeldBall_UsesPredictedClockForHolderAndBall()
        {
            var interpolator = new PresentationFrameInterpolator(2);
            ViewWorldState view = CreateViewWorld(
                predictedFrame: 11,
                confirmedFrame: 8,
                localFromX: -2,
                localToX: 0,
                remoteFromX: 4,
                remoteToX: 2,
                ballFromX: -2,
                ballToX: 0,
                ballState: BallEntity.EState.Held,
                holderPlayerIndex: 0,
                ballSource: ViewSampleSource.Predicted);
            interpolator.Reset(view);
            var positions = new Vector3[2];

            interpolator.Evaluate(
                0,
                0.75f,
                0.25f,
                positions,
                out PresentationBallSample ball);

            Assert.AreEqual(new Vector3(-0.5f, 0.5f, 0f), positions[0]);
            Assert.AreEqual(new Vector3(3.5f, 0.5f, 0f), positions[1]);
            Assert.AreEqual(0, ball.AttachedPlayerIndex);
            Assert.AreEqual(new Vector3(-0.5f, 0f, 0f), ball.BasePosition);
        }

        [Test]
        public void Evaluate_ViewWorldInvalidHolder_DoesNotPublishAttachment()
        {
            var interpolator = new PresentationFrameInterpolator(2);
            ViewWorldState view = CreateViewWorld(
                predictedFrame: 11,
                confirmedFrame: 8,
                localFromX: -2,
                localToX: 0,
                remoteFromX: 4,
                remoteToX: 2,
                ballFromX: 4,
                ballToX: 2,
                ballState: BallEntity.EState.Held,
                holderPlayerIndex: 5);
            interpolator.Reset(view);
            var positions = new Vector3[2];

            interpolator.Evaluate(
                0,
                0.5f,
                positions,
                out PresentationBallSample ball);

            Assert.AreEqual(-1, ball.AttachedPlayerIndex);
            Assert.IsFalse(ball.IsAttached);
        }

        [Test]
        public void PushViewWorld_ConfirmedFrameDidNotAdvance_FreezesRemoteAndConfirmedBall()
        {
            var interpolator = new PresentationFrameInterpolator(2);
            ViewWorldState first = CreateViewWorld(
                predictedFrame: 11,
                confirmedFrame: 8,
                localFromX: -2,
                localToX: 0,
                remoteFromX: 4,
                remoteToX: 2,
                ballFromX: 5,
                ballToX: 3,
                ballState: BallEntity.EState.Airborne,
                holderPlayerIndex: -1);
            ViewWorldState stalledConfirmed = CreateViewWorld(
                predictedFrame: 12,
                confirmedFrame: 8,
                localFromX: 0,
                localToX: 2,
                remoteFromX: 4,
                remoteToX: 2,
                ballFromX: 5,
                ballToX: 3,
                ballState: BallEntity.EState.Airborne,
                holderPlayerIndex: -1);
            interpolator.Reset(first);
            interpolator.PushViewWorld(stalledConfirmed);
            var positions = new Vector3[2];

            interpolator.Evaluate(
                0,
                0f,
                positions,
                out PresentationBallSample atStart);
            interpolator.Evaluate(
                0,
                0.5f,
                positions,
                out PresentationBallSample atHalf);

            Assert.AreEqual(new Vector3(1f, 0.5f, 0f), positions[0]);
            Assert.AreEqual(new Vector3(2f, 0.5f, 0f), positions[1]);
            Assert.AreEqual(new Vector3(3f, 0f, 0f), atStart.BasePosition);
            Assert.AreEqual(new Vector3(3f, 0f, 0f), atHalf.BasePosition);
        }

        [Test]
        public void PushViewWorld_ConfirmedFrameAdvanced_PlaysNewRemoteIntervalOnce()
        {
            var interpolator = new PresentationFrameInterpolator(2);
            ViewWorldState first = CreateViewWorld(
                predictedFrame: 11,
                confirmedFrame: 8,
                localFromX: -2,
                localToX: 0,
                remoteFromX: 4,
                remoteToX: 2,
                ballFromX: 5,
                ballToX: 3,
                ballState: BallEntity.EState.Airborne,
                holderPlayerIndex: -1);
            ViewWorldState advanced = CreateViewWorld(
                predictedFrame: 12,
                confirmedFrame: 9,
                localFromX: 0,
                localToX: 2,
                remoteFromX: 2,
                remoteToX: 0,
                ballFromX: 3,
                ballToX: 1,
                ballState: BallEntity.EState.Airborne,
                holderPlayerIndex: -1);
            interpolator.Reset(first);
            interpolator.PushViewWorld(advanced);
            var positions = new Vector3[2];

            interpolator.Evaluate(
                0,
                0.5f,
                positions,
                out PresentationBallSample ball);

            Assert.AreEqual(new Vector3(1f, 0.5f, 0f), positions[1]);
            Assert.AreEqual(new Vector3(2f, 0f, 0f), ball.BasePosition);
        }

        [Test]
        public void Reset_AnyAlpha_ReturnsCapturedWorld()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.FromInt(2));
            var interpolator = new PresentationFrameInterpolator(2);
            var playerPositions = new Vector3[2];

            interpolator.Reset(10, players, ball);
            interpolator.Evaluate(0.5f, playerPositions, out Vector3 ballPosition);

            Assert.AreEqual(new Vector3(-3f, 0.5f, 0f), playerPositions[0]);
            Assert.AreEqual(new Vector3(3f, 0.5f, 0f), playerPositions[1]);
            Assert.AreEqual(new Vector3(2f, 0f, 0f), ballPosition);
            Assert.AreEqual(10, interpolator.DeepestFrameID);
            Assert.AreEqual(10, interpolator.OldestFrameID);
            Assert.AreEqual(10, interpolator.PreviousFrameID);
            Assert.AreEqual(10, interpolator.CurrentFrameID);
            Assert.IsTrue(interpolator.IsReady);
        }

        [Test]
        public void PushLogicFrame_AlphaZeroHalfAndOne_InterpolatesEndpoints()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            var interpolator = new PresentationFrameInterpolator(2);
            var playerPositions = new Vector3[2];
            interpolator.Reset(10, players, ball);

            players[0].position.x = FixedInt.FromInt(-1);
            ball.position.x = FixedInt.FromInt(2);
            interpolator.PushLogicFrame(11, players, ball);

            interpolator.Evaluate(0f, playerPositions, out Vector3 ballAtStart);
            Assert.AreEqual(new Vector3(-3f, 0.5f, 0f), playerPositions[0]);
            Assert.AreEqual(Vector3.zero, ballAtStart);

            interpolator.Evaluate(0.5f, playerPositions, out Vector3 ballAtHalf);
            Assert.AreEqual(new Vector3(-2f, 0.5f, 0f), playerPositions[0]);
            Assert.AreEqual(new Vector3(1f, 0f, 0f), ballAtHalf);

            interpolator.Evaluate(1f, playerPositions, out Vector3 ballAtEnd);
            Assert.AreEqual(new Vector3(-1f, 0.5f, 0f), playerPositions[0]);
            Assert.AreEqual(new Vector3(2f, 0f, 0f), ballAtEnd);
        }

        [Test]
        public void PushLogicFrame_MultipleCatchupFrames_KeepsFinalFourEndpoints()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            var interpolator = new PresentationFrameInterpolator(2);
            var playerPositions = new Vector3[2];
            interpolator.Reset(10, players, ball);

            players[0].position.x = FixedInt.FromInt(-2);
            players[1].position.x = FixedInt.FromInt(4);
            interpolator.PushLogicFrame(11, players, ball);
            players[0].position.x = FixedInt.FromInt(-1);
            players[1].position.x = FixedInt.FromInt(5);
            interpolator.PushLogicFrame(12, players, ball);
            players[0].position.x = FixedInt.Zero;
            players[1].position.x = FixedInt.FromInt(6);
            interpolator.PushLogicFrame(13, players, ball);
            players[0].position.x = FixedInt.FromInt(1);
            players[1].position.x = FixedInt.FromInt(7);
            interpolator.PushLogicFrame(14, players, ball);

            interpolator.Evaluate(0, 0.5f, playerPositions, out _);

            Assert.AreEqual(new Vector3(0.5f, 0.5f, 0f), playerPositions[0]);
            Assert.AreEqual(new Vector3(4.5f, 0.5f, 0f), playerPositions[1]);
            Assert.AreEqual(11, interpolator.DeepestFrameID);
            Assert.AreEqual(12, interpolator.OldestFrameID);
            Assert.AreEqual(13, interpolator.PreviousFrameID);
            Assert.AreEqual(14, interpolator.CurrentFrameID);
        }

        [Test]
        public void Evaluate_LocalZero_UsesCurrentPairAndRemoteUsesDelayedPair()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            var interpolator = new PresentationFrameInterpolator(2);
            var output = new Vector3[2];
            interpolator.Reset(10, players, ball);
            players[0].position.x = FixedInt.FromInt(-2);
            players[1].position.x = FixedInt.FromInt(4);
            interpolator.PushLogicFrame(11, players, ball);
            players[0].position.x = FixedInt.FromInt(-1);
            players[1].position.x = FixedInt.FromInt(5);
            interpolator.PushLogicFrame(12, players, ball);
            players[0].position.x = FixedInt.Zero;
            players[1].position.x = FixedInt.FromInt(6);
            interpolator.PushLogicFrame(13, players, ball);

            interpolator.Evaluate(0, 0.5f, output, out _);

            Assert.AreEqual(new Vector3(-0.5f, 0.5f, 0f), output[0]);
            Assert.AreEqual(new Vector3(3.5f, 0.5f, 0f), output[1]);
            Assert.AreEqual(10, interpolator.DeepestFrameID);
            Assert.AreEqual(11, interpolator.OldestFrameID);
            Assert.AreEqual(12, interpolator.PreviousFrameID);
            Assert.AreEqual(13, interpolator.CurrentFrameID);
        }

        [Test]
        public void Evaluate_LocalOne_ReversesTimelineOwnership()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            var interpolator = new PresentationFrameInterpolator(2);
            var output = new Vector3[2];
            interpolator.Reset(10, players, ball);
            players[0].position.x = FixedInt.FromInt(-2);
            players[1].position.x = FixedInt.FromInt(4);
            interpolator.PushLogicFrame(11, players, ball);
            players[0].position.x = FixedInt.FromInt(-1);
            players[1].position.x = FixedInt.FromInt(5);
            interpolator.PushLogicFrame(12, players, ball);
            players[0].position.x = FixedInt.Zero;
            players[1].position.x = FixedInt.FromInt(6);
            interpolator.PushLogicFrame(13, players, ball);

            interpolator.Evaluate(1, 0.5f, output, out _);

            Assert.AreEqual(new Vector3(-2.5f, 0.5f, 0f), output[0]);
            Assert.AreEqual(new Vector3(5.5f, 0.5f, 0f), output[1]);
        }

        [Test]
        public void Evaluate_InvalidLocalPlayerIndex_Throws()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            var interpolator = new PresentationFrameInterpolator(2);
            interpolator.Reset(10, players, ball);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => interpolator.Evaluate(-1, 0f, new Vector3[2], out _));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => interpolator.Evaluate(2, 0f, new Vector3[2], out _));
        }

        [Test]
        public void Reset_AfterThreeFrames_CollapsesEveryTimelineToResetWorld()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            var interpolator = new PresentationFrameInterpolator(2);
            var output = new Vector3[2];
            interpolator.Reset(10, players, ball);
            players[0].position.x = FixedInt.FromInt(-2);
            interpolator.PushLogicFrame(11, players, ball);
            players[0].position.x = FixedInt.FromInt(-1);
            interpolator.PushLogicFrame(12, players, ball);
            players[0].position.x = FixedInt.FromInt(7);

            interpolator.Reset(20, players, ball);
            interpolator.Evaluate(0, 0.5f, output, out _);

            Assert.AreEqual(new Vector3(7f, 0.5f, 0f), output[0]);
            Assert.AreEqual(20, interpolator.DeepestFrameID);
            Assert.AreEqual(20, interpolator.OldestFrameID);
            Assert.AreEqual(20, interpolator.PreviousFrameID);
            Assert.AreEqual(20, interpolator.CurrentFrameID);
        }

        [Test]
        public void Evaluate_LocalHeld_UsesLocalHolderTimeline()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            var interpolator = new PresentationFrameInterpolator(2);
            var output = new Vector3[2];
            interpolator.Reset(10, players, ball);
            players[0].position.x = FixedInt.FromInt(-2);
            SetHeldBall(players, ball, 0, new Vector3(0.4f, 0.8f, 0.6f));
            interpolator.PushLogicFrame(11, players, ball);
            players[0].position.x = FixedInt.FromInt(-1);
            SetHeldBall(players, ball, 0, new Vector3(0.4f, 0.8f, 0.6f));
            interpolator.PushLogicFrame(12, players, ball);
            players[0].position.x = FixedInt.Zero;
            SetHeldBall(players, ball, 0, new Vector3(0.4f, 0.8f, 0.6f));
            interpolator.PushLogicFrame(13, players, ball);

            interpolator.Evaluate(0, 0.5f, output, out PresentationBallSample sample);

            Assert.AreEqual(0, sample.AttachedPlayerIndex);
            Assert.That(
                Vector3.Distance(
                    output[0] + new Vector3(0.4f, 0.8f, 0.6f),
                    sample.BasePosition),
                Is.LessThanOrEqualTo(0.0001f));
        }

        [TestCase(BallEntity.EState.Free)]
        [TestCase(BallEntity.EState.Airborne)]
        [TestCase(BallEntity.EState.Scored)]
        public void Evaluate_NonHeldBall_UsesDeepTimeline(
            BallEntity.EState state)
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            ball.state = state;
            var interpolator = new PresentationFrameInterpolator(2);
            var output = new Vector3[2];
            interpolator.Reset(10, players, ball);
            ball.position.x = FixedInt.FromInt(2);
            interpolator.PushLogicFrame(11, players, ball);
            ball.position.x = FixedInt.FromInt(100);
            interpolator.PushLogicFrame(12, players, ball);
            ball.position.x = FixedInt.FromInt(200);
            interpolator.PushLogicFrame(13, players, ball);

            interpolator.Evaluate(0, 0.5f, output, out PresentationBallSample sample);

            Assert.AreEqual(-1, sample.AttachedPlayerIndex);
            Assert.AreEqual(new Vector3(1f, 0f, 0f), sample.BasePosition);
        }

        [Test]
        public void Evaluate_RemoteAcquire_WaitsUntilOldestEndpointOwnsBall()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            var interpolator = new PresentationFrameInterpolator(2);
            var output = new Vector3[2];
            interpolator.Reset(10, players, ball);
            players[1].position.x = FixedInt.FromInt(4);
            ball.position.x = FixedInt.FromInt(1);
            interpolator.PushLogicFrame(11, players, ball);
            players[1].position.x = FixedInt.FromInt(5);
            SetHeldBall(players, ball, 1, new Vector3(-0.4f, 0.8f, 0.6f));
            interpolator.PushLogicFrame(12, players, ball);
            interpolator.Evaluate(0, 0.5f, output, out PresentationBallSample early);
            Assert.AreEqual(-1, early.AttachedPlayerIndex);

            players[1].position.x = FixedInt.FromInt(6);
            SetHeldBall(players, ball, 1, new Vector3(-0.4f, 0.8f, 0.6f));
            interpolator.PushLogicFrame(13, players, ball);
            interpolator.Evaluate(0, 0.5f, output, out PresentationBallSample delayed);

            Assert.AreEqual(-1, delayed.AttachedPlayerIndex);
            Assert.AreEqual(new Vector3(0.5f, 0f, 0f), delayed.BasePosition);

            players[1].position.x = FixedInt.FromInt(7);
            SetHeldBall(players, ball, 1, new Vector3(-0.4f, 0.8f, 0.6f));
            interpolator.PushLogicFrame(14, players, ball);
            interpolator.Evaluate(0, 0.5f, output, out PresentationBallSample attached);

            Assert.AreEqual(1, attached.AttachedPlayerIndex);
            Assert.AreEqual(
                output[1] + new Vector3(-0.4f, 0.8f, 0.6f),
                attached.BasePosition);
        }

        [Test]
        public void Evaluate_LocalRelease_DeepHistoryHeld_RemainsUnbound()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            SetHeldBall(players, ball, 0, new Vector3(0.4f, 0.8f, 0.6f));
            var interpolator = new PresentationFrameInterpolator(2);
            var output = new Vector3[2];
            interpolator.Reset(10, players, ball);
            players[0].position.x = FixedInt.FromInt(-2);
            SetHeldBall(players, ball, 0, new Vector3(0.4f, 0.8f, 0.6f));
            interpolator.PushLogicFrame(11, players, ball);
            players[0].position.x = FixedInt.FromInt(-1);
            SetHeldBall(players, ball, 0, new Vector3(0.4f, 0.8f, 0.6f));
            interpolator.PushLogicFrame(12, players, ball);
            ball.state = BallEntity.EState.Airborne;
            ball.holderPlayerIndex = -1;
            ball.position.x = FixedInt.FromInt(8);
            interpolator.PushLogicFrame(13, players, ball);

            interpolator.Evaluate(0, 0.5f, output, out PresentationBallSample sample);

            Assert.AreEqual(-1, sample.AttachedPlayerIndex);
            Assert.AreEqual(new Vector3(-2.1f, 1.3f, 0.6f), sample.BasePosition);
        }

        [Test]
        public void Evaluate_RemoteRelease_WaitsForDelayedEndpoint()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            SetHeldBall(players, ball, 1, new Vector3(-0.4f, 0.8f, 0.6f));
            var interpolator = new PresentationFrameInterpolator(2);
            var output = new Vector3[2];
            interpolator.Reset(10, players, ball);
            players[1].position.x = FixedInt.FromInt(4);
            SetHeldBall(players, ball, 1, new Vector3(-0.4f, 0.8f, 0.6f));
            interpolator.PushLogicFrame(11, players, ball);
            ball.state = BallEntity.EState.Airborne;
            ball.holderPlayerIndex = -1;
            ball.position.x = FixedInt.FromInt(8);
            interpolator.PushLogicFrame(12, players, ball);

            interpolator.Evaluate(0, 0.5f, output, out PresentationBallSample delayedHeld);
            Assert.AreEqual(1, delayedHeld.AttachedPlayerIndex);

            ball.position.x = FixedInt.FromInt(9);
            interpolator.PushLogicFrame(13, players, ball);
            interpolator.Evaluate(0, 0.5f, output, out PresentationBallSample stillHeld);

            Assert.AreEqual(1, stillHeld.AttachedPlayerIndex);

            ball.position.x = FixedInt.FromInt(10);
            interpolator.PushLogicFrame(14, players, ball);
            interpolator.Evaluate(0, 0.5f, output, out PresentationBallSample released);

            Assert.AreEqual(-1, released.AttachedPlayerIndex);
        }

        [Test]
        public void ReplaceAfterRollback_AllAlphaValues_ReturnCorrectedWorld()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            var interpolator = new PresentationFrameInterpolator(2);
            var playerPositions = new Vector3[2];
            interpolator.Reset(10, players, ball);
            players[0].position.x = FixedInt.FromInt(-1);
            interpolator.PushLogicFrame(11, players, ball);

            players[0].position.x = FixedInt.FromInt(5);
            ball.position.x = FixedInt.FromInt(7);
            interpolator.ReplaceAfterRollback(11, players, ball);

            foreach (float alpha in new[] { 0f, 0.5f, 1f })
            {
                interpolator.Evaluate(alpha, playerPositions, out Vector3 ballPosition);
                Assert.AreEqual(new Vector3(5f, 0.5f, 0f), playerPositions[0]);
                Assert.AreEqual(new Vector3(7f, 0f, 0f), ballPosition);
            }
        }

        [Test]
        public void ReplaceHistoryAfterRollback_ReplacesEveryPredictedEndpoint()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            var interpolator = new PresentationFrameInterpolator(2);
            var output = new Vector3[2];
            interpolator.Reset(100, players, ball);
            players[0].position.x = FixedInt.FromInt(101);
            players[1].position.x = FixedInt.FromInt(101);
            interpolator.PushLogicFrame(101, players, ball);
            players[0].position.x = FixedInt.FromInt(102);
            players[1].position.x = FixedInt.FromInt(102);
            interpolator.PushLogicFrame(102, players, ball);

            FrameSnapshot deepest = CreateSnapshot(10, -3, 3, 0);
            FrameSnapshot oldest = CreateSnapshot(11, -2, 4, 2);
            FrameSnapshot previous = CreateSnapshot(12, -1, 5, 4);
            FrameSnapshot newest = CreateSnapshot(13, 0, 6, 6);
            interpolator.ReplaceHistoryAfterRollback(
                newest,
                previous,
                oldest,
                deepest);
            interpolator.Evaluate(0, 0.5f, output, out PresentationBallSample sample);

            Assert.AreEqual(new Vector3(-0.5f, 0.5f, 0f), output[0]);
            Assert.AreEqual(new Vector3(3.5f, 0.5f, 0f), output[1]);
            Assert.AreEqual(new Vector3(1f, 0f, 0f), sample.BasePosition);
            Assert.AreEqual(10, interpolator.DeepestFrameID);
            Assert.AreEqual(11, interpolator.OldestFrameID);
            Assert.AreEqual(12, interpolator.PreviousFrameID);
            Assert.AreEqual(13, interpolator.CurrentFrameID);
        }

        [Test]
        public void ReplaceHistoryAfterRollback_MissingPrevious_DuplicatesNewestIntoAllHistory()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            var interpolator = new PresentationFrameInterpolator(2);
            var output = new Vector3[2];
            interpolator.Reset(10, players, ball);
            FrameSnapshot newest = CreateSnapshot(12, -1, 5, 4);

            interpolator.ReplaceHistoryAfterRollback(
                newest,
                null,
                null,
                null);
            interpolator.Evaluate(0, 0.5f, output, out PresentationBallSample sample);

            Assert.AreEqual(new Vector3(-1f, 0.5f, 0f), output[0]);
            Assert.AreEqual(new Vector3(5f, 0.5f, 0f), output[1]);
            Assert.AreEqual(new Vector3(4f, 0f, 0f), sample.BasePosition);
            Assert.AreEqual(12, interpolator.DeepestFrameID);
            Assert.AreEqual(12, interpolator.OldestFrameID);
            Assert.AreEqual(12, interpolator.PreviousFrameID);
            Assert.AreEqual(12, interpolator.CurrentFrameID);
        }

        [Test]
        public void ReplaceHistoryAfterRollback_MissingOldest_DuplicatesPreviousIntoOlderHistory()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            var interpolator = new PresentationFrameInterpolator(2);
            var output = new Vector3[2];
            interpolator.Reset(10, players, ball);

            interpolator.ReplaceHistoryAfterRollback(
                CreateSnapshot(12, -1, 5, 4),
                CreateSnapshot(11, -2, 4, 2),
                null,
                null);
            interpolator.Evaluate(0, 0.5f, output, out PresentationBallSample sample);

            Assert.AreEqual(new Vector3(-1.5f, 0.5f, 0f), output[0]);
            Assert.AreEqual(new Vector3(4f, 0.5f, 0f), output[1]);
            Assert.AreEqual(new Vector3(2f, 0f, 0f), sample.BasePosition);
            Assert.AreEqual(11, interpolator.DeepestFrameID);
            Assert.AreEqual(11, interpolator.OldestFrameID);
            Assert.AreEqual(11, interpolator.PreviousFrameID);
            Assert.AreEqual(12, interpolator.CurrentFrameID);
        }

        [Test]
        public void ReplaceHistoryAfterRollback_MissingDeepest_DuplicatesOldest()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            var interpolator = new PresentationFrameInterpolator(2);
            var output = new Vector3[2];
            interpolator.Reset(10, players, ball);

            interpolator.ReplaceHistoryAfterRollback(
                CreateSnapshot(13, 0, 6, 6),
                CreateSnapshot(12, -1, 5, 4),
                CreateSnapshot(11, -2, 4, 2),
                null);
            interpolator.Evaluate(0, 0.5f, output, out PresentationBallSample sample);

            Assert.AreEqual(new Vector3(-0.5f, 0.5f, 0f), output[0]);
            Assert.AreEqual(new Vector3(4f, 0.5f, 0f), output[1]);
            Assert.AreEqual(new Vector3(2f, 0f, 0f), sample.BasePosition);
            Assert.AreEqual(11, interpolator.DeepestFrameID);
            Assert.AreEqual(11, interpolator.OldestFrameID);
            Assert.AreEqual(12, interpolator.PreviousFrameID);
            Assert.AreEqual(13, interpolator.CurrentFrameID);
        }

        [Test]
        public void ReplaceHistoryAfterRollback_NonConsecutive_Throws()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            var interpolator = new PresentationFrameInterpolator(2);
            interpolator.Reset(10, players, ball);

            Assert.Throws<ArgumentException>(
                () => interpolator.ReplaceHistoryAfterRollback(
                    CreateSnapshot(12, -1, 5, 4),
                    CreateSnapshot(10, -2, 4, 2),
                    null,
                    null));

            Assert.Throws<ArgumentException>(
                () => interpolator.ReplaceHistoryAfterRollback(
                    CreateSnapshot(13, 0, 6, 6),
                    CreateSnapshot(12, -1, 5, 4),
                    CreateSnapshot(11, -2, 4, 2),
                    CreateSnapshot(9, -3, 3, 0)));
        }

        [Test]
        public void ReplaceHistoryAfterRollback_InvalidPrevious_ThrowsWithoutMutation()
        {
            PresentationFrameInterpolator interpolator =
                CreateDistinctHistoryInterpolator();

            Assert.Throws<ArgumentException>(
                () => interpolator.ReplaceHistoryAfterRollback(
                    CreateSnapshot(0, 100, 100, 100),
                    CreateSnapshot(-1, 200, 200, 200),
                    null,
                    null));

            AssertDistinctHistoryUnchanged(interpolator);
        }

        [Test]
        public void ReplaceHistoryAfterRollback_InvalidOldest_ThrowsWithoutMutation()
        {
            PresentationFrameInterpolator interpolator =
                CreateDistinctHistoryInterpolator();

            Assert.Throws<ArgumentException>(
                () => interpolator.ReplaceHistoryAfterRollback(
                    CreateSnapshot(1, 100, 100, 100),
                    CreateSnapshot(0, 200, 200, 200),
                    CreateSnapshot(-1, 300, 300, 300),
                    null));

            AssertDistinctHistoryUnchanged(interpolator);
        }

        [Test]
        public void ReplaceHistoryAfterRollback_InvalidDeepest_ThrowsWithoutMutation()
        {
            PresentationFrameInterpolator interpolator =
                CreateDistinctHistoryInterpolator();

            Assert.Throws<ArgumentException>(
                () => interpolator.ReplaceHistoryAfterRollback(
                    CreateSnapshot(2, 100, 100, 100),
                    CreateSnapshot(1, 200, 200, 200),
                    CreateSnapshot(0, 300, 300, 300),
                    CreateSnapshot(-1, 400, 400, 400)));

            AssertDistinctHistoryUnchanged(interpolator);
        }

        [TestCase(float.NaN, -3f)]
        [TestCase(float.NegativeInfinity, -3f)]
        [TestCase(-1f, -3f)]
        [TestCase(2f, -1f)]
        [TestCase(float.PositiveInfinity, -1f)]
        public void Evaluate_InvalidOrOutOfRangeAlpha_ClampsToEndpoint(
            float alpha,
            float expectedX)
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            var interpolator = new PresentationFrameInterpolator(2);
            var playerPositions = new Vector3[2];
            interpolator.Reset(10, players, ball);
            players[0].position.x = FixedInt.FromInt(-1);
            interpolator.PushLogicFrame(11, players, ball);

            interpolator.Evaluate(alpha, playerPositions, out _);

            Assert.AreEqual(expectedX, playerPositions[0].x);
        }

        [TestCase(100L, 100L, 33, 0f)]
        [TestCase(99L, 100L, 33, 0f)]
        [TestCase(116L, 100L, 32, 0.5f)]
        [TestCase(132L, 100L, 32, 1f)]
        [TestCase(150L, 100L, 32, 1f)]
        public void CalculateAlpha_ElapsedRemainder_ReturnsClampedRatio(
            long elapsedMs,
            long lastLogicMs,
            int frameIntervalMs,
            float expected)
        {
            Assert.AreEqual(
                expected,
                PresentationFrameInterpolator.CalculateAlpha(
                    elapsedMs,
                    lastLogicMs,
                    frameIntervalMs));
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void Constructor_InvalidPlayerCount_Throws(int playerCount)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new PresentationFrameInterpolator(playerCount));
        }

        [Test]
        public void CalculateAlpha_InvalidFrameInterval_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => PresentationFrameInterpolator.CalculateAlpha(1L, 0L, 0));
        }

        [Test]
        public void FrameEngine_Initialize_RenderInterpolationAlphaStartsAtZero()
        {
            var engine = (FrameEngine)FormatterServices.GetUninitializedObject(
                typeof(FrameEngine));

            engine.Initialize(2, 33);

            Assert.AreEqual(0f, engine.RenderInterpolationAlpha);
        }

        [Test]
        public void Evaluate_BeforeReset_Throws()
        {
            var interpolator = new PresentationFrameInterpolator(2);

            Assert.Throws<InvalidOperationException>(
                () => interpolator.Evaluate(0f, new Vector3[2], out _));
        }

        [Test]
        public void Evaluate_WrongOutputLength_Throws()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            var interpolator = new PresentationFrameInterpolator(2);
            interpolator.Reset(10, players, ball);

            Assert.Throws<ArgumentException>(
                () => interpolator.Evaluate(0f, new Vector3[1], out _));
        }

        [Test]
        public void PushLogicFrame_NonIncreasingFrameID_Throws()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            var interpolator = new PresentationFrameInterpolator(2);
            interpolator.Reset(10, players, ball);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => interpolator.PushLogicFrame(10, players, ball));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => interpolator.PushLogicFrame(9, players, ball));
        }

        [Test]
        public void CanPushLogicFrame_OnlyAllowsFramesAfterCurrentEndpoint()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            var interpolator = new PresentationFrameInterpolator(2);

            Assert.IsTrue(interpolator.CanPushLogicFrame(0));

            interpolator.Reset(10, players, ball);

            Assert.IsFalse(interpolator.CanPushLogicFrame(9));
            Assert.IsFalse(interpolator.CanPushLogicFrame(10));
            Assert.IsTrue(interpolator.CanPushLogicFrame(11));
        }

        [Test]
        public void Reset_InvalidWorld_Throws()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            var interpolator = new PresentationFrameInterpolator(2);

            Assert.Throws<ArgumentNullException>(
                () => interpolator.Reset(0, null, ball));
            Assert.Throws<ArgumentException>(
                () => interpolator.Reset(0, new PlayerEntity[1], ball));
            Assert.Throws<ArgumentNullException>(
                () => interpolator.Reset(0, players, null));
        }

        [Test]
        public void Evaluate_AfterWarmup_DoesNotAllocateManagedMemory()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            var interpolator = new PresentationFrameInterpolator(2);
            var output = new Vector3[2];
            interpolator.Reset(10, players, ball);
            players[0].position.x = FixedInt.FromInt(-2);
            interpolator.PushLogicFrame(11, players, ball);
            players[0].position.x = FixedInt.FromInt(-1);
            interpolator.PushLogicFrame(12, players, ball);
            for (int i = 0; i < 32; i++)
                interpolator.Evaluate(0, 0.5f, output, out _);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
                interpolator.Evaluate(0, 0.5f, output, out _);
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(before, after);
        }

        [Test]
        public void Evaluate_DoesNotMutateLogicWorld()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.FromInt(2));
            var interpolator = new PresentationFrameInterpolator(2);
            var output = new Vector3[2];
            interpolator.Reset(10, players, ball);
            FixedVector3 player0Before = players[0].position;
            FixedVector3 player1Before = players[1].position;
            FixedVector3 ballBefore = ball.position;
            BallEntity.EState stateBefore = ball.state;
            int holderBefore = ball.holderPlayerIndex;

            for (int i = 0; i < 100; i++)
                interpolator.Evaluate(0, 0.5f, output, out _);

            Assert.AreEqual(player0Before, players[0].position);
            Assert.AreEqual(player1Before, players[1].position);
            Assert.AreEqual(ballBefore, ball.position);
            Assert.AreEqual(stateBefore, ball.state);
            Assert.AreEqual(holderBefore, ball.holderPlayerIndex);
        }

        [Test]
        public void ReplaceHistoryAndEvaluate_DoNotChangeWorldHash()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            var interpolator = new PresentationFrameInterpolator(2);
            var output = new Vector3[2];
            interpolator.Reset(10, players, ball);
            FrameSnapshot deepest = CreateSnapshot(9, -4, 2, -2);
            FrameSnapshot oldest = CreateSnapshot(10, -3, 3, 0);
            FrameSnapshot previous = CreateSnapshot(11, -2, 4, 2);
            FrameSnapshot newest = CreateSnapshot(12, -1, 5, 4);
            ulong before = WorldHash.Compute(newest, 12);

            interpolator.ReplaceHistoryAfterRollback(
                newest,
                previous,
                oldest,
                deepest);
            interpolator.Evaluate(0, 0.5f, output, out _);
            ulong after = WorldHash.Compute(newest, 12);

            Assert.AreEqual(before, after);
            Assert.AreEqual(12, newest.frameID);
            Assert.AreEqual(FixedInt.FromInt(4), newest.ballPosX);
            Assert.AreEqual((int)BallEntity.EState.Free, newest.ballState);
            Assert.AreEqual(-1, newest.ballHolder);
        }

        private static ViewWorldState CreateViewWorld(
            int predictedFrame,
            int confirmedFrame,
            int localFromX,
            int localToX,
            int remoteFromX,
            int remoteToX,
            int ballFromX,
            int ballToX,
            BallEntity.EState ballState,
            int holderPlayerIndex,
            ViewSampleSource ballSource = ViewSampleSource.Confirmed)
        {
            var player0From = new SimulationPlayerState
            {
                position = new FixedVector3(
                    FixedInt.FromInt(localFromX),
                    FixedInt.Zero,
                    FixedInt.Zero)
            };
            var player0To = new SimulationPlayerState
            {
                position = new FixedVector3(
                    FixedInt.FromInt(localToX),
                    FixedInt.Zero,
                    FixedInt.Zero)
            };
            var player1From = new SimulationPlayerState
            {
                position = new FixedVector3(
                    FixedInt.FromInt(remoteFromX),
                    FixedInt.Zero,
                    FixedInt.Zero)
            };
            var player1To = new SimulationPlayerState
            {
                position = new FixedVector3(
                    FixedInt.FromInt(remoteToX),
                    FixedInt.Zero,
                    FixedInt.Zero)
            };
            var ballFrom = new SimulationBallState
            {
                position = new FixedVector3(
                    FixedInt.FromInt(ballFromX),
                    FixedInt.Zero,
                    FixedInt.Zero),
                state = (int)ballState,
                holderPlayerIndex = holderPlayerIndex
            };
            var ballTo = new SimulationBallState
            {
                position = new FixedVector3(
                    FixedInt.FromInt(ballToX),
                    FixedInt.Zero,
                    FixedInt.Zero),
                state = (int)ballState,
                holderPlayerIndex = holderPlayerIndex
            };
            var player0 = new ViewPlayerState(
                0,
                player0From,
                player0To,
                predictedFrame - 1,
                predictedFrame,
                ViewSampleSource.Predicted);
            var player1 = new ViewPlayerState(
                1,
                player1From,
                player1To,
                confirmedFrame - 1,
                confirmedFrame,
                ViewSampleSource.Confirmed);
            var ball = new ViewBallState(
                ballFrom,
                ballTo,
                confirmedFrame - 1,
                confirmedFrame,
                ballSource);
            return new ViewWorldState(
                predictedFrame,
                confirmedFrame,
                confirmedFrame,
                player0,
                player1,
                ball);
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

        private static PresentationFrameInterpolator
            CreateDistinctHistoryInterpolator()
        {
            PlayerEntity[] players = CreatePlayers();
            BallEntity ball = CreateBall(FixedInt.Zero);
            var interpolator = new PresentationFrameInterpolator(2);
            interpolator.Reset(10, players, ball);
            players[0].position.x = FixedInt.FromInt(-2);
            players[1].position.x = FixedInt.FromInt(4);
            ball.position.x = FixedInt.FromInt(2);
            interpolator.PushLogicFrame(11, players, ball);
            players[0].position.x = FixedInt.FromInt(-1);
            players[1].position.x = FixedInt.FromInt(5);
            ball.position.x = FixedInt.FromInt(4);
            interpolator.PushLogicFrame(12, players, ball);
            players[0].position.x = FixedInt.Zero;
            players[1].position.x = FixedInt.FromInt(6);
            ball.position.x = FixedInt.FromInt(6);
            interpolator.PushLogicFrame(13, players, ball);
            return interpolator;
        }

        private static void AssertDistinctHistoryUnchanged(
            PresentationFrameInterpolator interpolator)
        {
            var output = new Vector3[2];
            interpolator.Evaluate(
                0,
                0.5f,
                output,
                out PresentationBallSample sample);

            Assert.AreEqual(new Vector3(-0.5f, 0.5f, 0f), output[0]);
            Assert.AreEqual(new Vector3(3.5f, 0.5f, 0f), output[1]);
            Assert.AreEqual(new Vector3(1f, 0f, 0f), sample.BasePosition);
            Assert.AreEqual(10, interpolator.DeepestFrameID);
            Assert.AreEqual(11, interpolator.OldestFrameID);
            Assert.AreEqual(12, interpolator.PreviousFrameID);
            Assert.AreEqual(13, interpolator.CurrentFrameID);
        }

        private static BallEntity CreateBall(FixedInt x)
        {
            var ball = new BallEntity();
            ball.Reset(new FixedVector3(x, FixedInt.Zero, FixedInt.Zero));
            ball.state = BallEntity.EState.Free;
            return ball;
        }

        private static void SetHeldBall(
            PlayerEntity[] players,
            BallEntity ball,
            int holderIndex,
            Vector3 heldOffset)
        {
            Vector3 holderBase =
                PresentationTargetResolver.ResolvePlayerTarget(
                    players[holderIndex]);
            Vector3 position = holderBase + heldOffset;
            ball.position = new FixedVector3(
                FixedInt.FromFloat(position.x),
                FixedInt.FromFloat(position.y),
                FixedInt.FromFloat(position.z));
            ball.velocity = FixedVector3.Zero;
            ball.state = BallEntity.EState.Held;
            ball.holderPlayerIndex = holderIndex;
        }

        private static FrameSnapshot CreateSnapshot(
            int frameID,
            int player0X,
            int player1X,
            int ballX)
        {
            return new FrameSnapshot
            {
                frameID = frameID,
                player1X = FixedInt.FromInt(player0X),
                player1Y = FixedInt.Zero,
                player1Z = FixedInt.Zero,
                player2X = FixedInt.FromInt(player1X),
                player2Y = FixedInt.Zero,
                player2Z = FixedInt.Zero,
                player1State = (int)PlayerEntity.EState.Idle,
                player2State = (int)PlayerEntity.EState.Idle,
                ballPosX = FixedInt.FromInt(ballX),
                ballPosY = FixedInt.Zero,
                ballPosZ = FixedInt.Zero,
                ballState = (int)BallEntity.EState.Free,
                ballHolder = -1
            };
        }
    }
}

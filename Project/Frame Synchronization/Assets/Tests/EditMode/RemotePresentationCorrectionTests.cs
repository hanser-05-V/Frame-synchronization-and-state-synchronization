using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace FrameSyncDemo.Tests
{
    public class RemotePresentationCorrectionTests
    {
        private const float LogicStep = 0.165f;
        private const float ReverseThreshold = LogicStep * 0.1f;
        private const float FinalErrorThreshold = 0.0001f;
        private const float HeldOffsetThreshold = 0.0001f;
        private const float RenderDeltaTime = 1f / 60f;
        private const float MaximumCorrectionSeconds = 0.2f;

        [Test]
        public void Fixed100Ms_Start_DoesNotMoveOppositeAuthoritativeDirection()
        {
            CorrectionTrace trace = RunRollbackTrajectory(
                predictedRemoteX: new[]
                {
                    0f,
                    0f,
                    -LogicStep,
                    -LogicStep * 2f
                },
                correctedRemoteX: new[]
                {
                    0f,
                    0f,
                    LogicStep,
                    LogicStep * 2f,
                    LogicStep * 3f,
                    LogicStep * 4f
                });

            Assert.That(
                trace.UnexpectedReverseDistance,
                Is.LessThanOrEqualTo(
                    ReverseThreshold + FinalErrorThreshold));
            AssertCorrectionCompleted(trace);
        }

        [Test]
        public void Fixed100Ms_Stop_UnexpectedReverseDistance_DoesNotExceedThreshold()
        {
            CorrectionTrace trace = RunRollbackTrajectory(
                predictedRemoteX: new[]
                {
                    0f,
                    LogicStep,
                    LogicStep * 2f,
                    LogicStep * 3f
                },
                correctedRemoteX: new[]
                {
                    0f,
                    LogicStep,
                    LogicStep,
                    LogicStep,
                    LogicStep,
                    LogicStep
                });

            Assert.That(
                trace.UnexpectedReverseDistance,
                Is.LessThanOrEqualTo(
                    ReverseThreshold + FinalErrorThreshold));
            AssertCorrectionCompleted(trace);
        }

        [Test]
        public void Fixed100Ms_SameDirection_UnexpectedReverseDistance_DoesNotExceedThreshold()
        {
            CorrectionTrace trace = RunRollbackTrajectory(
                predictedRemoteX: new[]
                {
                    0f,
                    LogicStep,
                    LogicStep * 3f,
                    LogicStep * 4f
                },
                correctedRemoteX: new[]
                {
                    0f,
                    LogicStep,
                    LogicStep * 2f,
                    LogicStep * 3f,
                    LogicStep * 4f,
                    LogicStep * 5f
                });

            Assert.That(
                trace.UnexpectedReverseDistance,
                Is.LessThanOrEqualTo(
                    ReverseThreshold + FinalErrorThreshold));
            AssertCorrectionCompleted(trace);
        }

        [Test]
        public void Fixed100Ms_FastReverse_HasOneDirectionChangeAndNoSecondaryRebound()
        {
            CorrectionTrace trace = RunRollbackTrajectory(
                predictedRemoteX: new[]
                {
                    0f,
                    LogicStep,
                    LogicStep * 2f,
                    LogicStep * 3f
                },
                correctedRemoteX: new[]
                {
                    0f,
                    LogicStep,
                    0f,
                    -LogicStep,
                    -LogicStep * 2f,
                    -LogicStep * 3f
                });

            Assert.AreEqual(1, trace.DirectionChanges);
            Assert.That(
                trace.MaximumTargetDeviation,
                Is.LessThanOrEqualTo(
                    ReverseThreshold + FinalErrorThreshold));
            AssertCorrectionCompleted(trace);
        }

        [Test]
        public void Fixed100Ms_Correction_ConvergesWithinMaximumDuration()
        {
            CorrectionTrace trace = RunRollbackTrajectory(
                predictedRemoteX: new[]
                {
                    0f,
                    LogicStep + 0.01f,
                    LogicStep * 2f,
                    LogicStep * 3f
                },
                correctedRemoteX: new[]
                {
                    0f,
                    LogicStep,
                    LogicStep,
                    LogicStep,
                    LogicStep,
                    LogicStep
                });

            AssertCorrectionCompleted(trace);
        }

        [Test]
        public void Fixed100Ms_RemoteHeld_BallMaintainsDeepTimelineOffset()
        {
            Vector3 heldOffset = new Vector3(0.4f, 0.8f, 0.6f);
            FrameSnapshot deepest = CreateSnapshot(
                10,
                0f,
                LogicStep,
                LogicStep + heldOffset.x,
                0.5f + heldOffset.y,
                heldOffset.z,
                BallEntity.EState.Held,
                1);
            FrameSnapshot oldest = CreateSnapshot(
                11,
                0f,
                LogicStep * 2f,
                LogicStep * 2f + heldOffset.x,
                0.5f + heldOffset.y,
                heldOffset.z,
                BallEntity.EState.Held,
                1);
            var interpolator = new PresentationFrameInterpolator(2);
            interpolator.ReplaceHistoryAfterRollback(
                CreateSnapshot(13, 0f, 4f * LogicStep),
                CreateSnapshot(12, 0f, 3f * LogicStep),
                oldest,
                deepest);
            var players = new Vector3[2];

            interpolator.Evaluate(
                0,
                0.5f,
                players,
                out PresentationBallSample sample);

            Assert.AreEqual(1, sample.AttachedPlayerIndex);
            Assert.That(
                Vector3.Distance(
                    sample.BasePosition,
                    players[1] + heldOffset),
                Is.LessThanOrEqualTo(HeldOffsetThreshold));
        }

        [Test]
        public void ZeroMs_LocalTimeline_RemainsPreviousToCurrent()
        {
            var interpolator = new PresentationFrameInterpolator(2);
            interpolator.ReplaceHistoryAfterRollback(
                CreateSnapshot(13, 2f, 60f),
                CreateSnapshot(12, 1f, 50f),
                CreateSnapshot(11, 200f, 40f),
                CreateSnapshot(10, 100f, 30f));
            var players = new Vector3[2];

            interpolator.Evaluate(0, 0.5f, players, out _);

            Assert.AreEqual(new Vector3(1.5f, 0.5f, 0f), players[0]);
            Assert.AreEqual(new Vector3(35f, 0.5f, 0f), players[1]);
        }

        [Test]
        public void OverflowRebase_CorrectsFromCapturedScreenAndReplacesHolderAtomically()
        {
            var interpolator = new PresentationFrameInterpolator(2);
            interpolator.Reset(CreateHeldView(
                predictedFrame: 5,
                confirmedFrame: 3,
                presentationFrame: 3,
                holderPlayerIndex: 0,
                ballSource: ViewSampleSource.Predicted,
                localFromX: 0f,
                localToX: 1f,
                remoteFromX: 1f,
                remoteToX: 2f));
            var players = new Vector3[2];
            interpolator.Evaluate(
                0,
                0.75f,
                0.5f,
                players,
                out PresentationBallSample oldBall);
            Vector3 capturedRemote = players[1];
            Vector3 capturedBall = oldBall.BasePosition;

            interpolator.ReplaceViewWorld(CreateHeldView(
                predictedFrame: 10,
                confirmedFrame: 8,
                presentationFrame: 8,
                holderPlayerIndex: 1,
                ballSource: ViewSampleSource.Confirmed,
                localFromX: 1f,
                localToX: 2f,
                remoteFromX: 2f,
                remoteToX: 3f));
            interpolator.Evaluate(
                0,
                0.75f,
                0.25f,
                players,
                out PresentationBallSample rebasedBall);
            Vector3 remoteTarget = players[1];
            Vector3 ballTarget = rebasedBall.BasePosition;

            Assert.AreEqual(1, rebasedBall.AttachedPlayerIndex);

            PresentationCorrectionSmoother remoteSmoother = CreateSmoother();
            PresentationCorrectionSmoother ballSmoother = CreateSmoother();
            remoteSmoother.BeginCorrection(capturedRemote, remoteTarget);
            ballSmoother.BeginCorrection(capturedBall, ballTarget);

            Vector3 displayedRemote = capturedRemote;
            Vector3 displayedBall = capturedBall;
            int samples = 0;
            int maximumSamples = Mathf.CeilToInt(
                MaximumCorrectionSeconds / RenderDeltaTime) + 1;
            while ((remoteSmoother.IsCorrecting || ballSmoother.IsCorrecting) &&
                   samples < maximumSamples)
            {
                displayedRemote = remoteSmoother.Evaluate(
                    remoteTarget,
                    RenderDeltaTime);
                displayedBall = ballSmoother.Evaluate(
                    ballTarget,
                    RenderDeltaTime);
                samples++;
            }

            Assert.IsFalse(remoteSmoother.IsCorrecting);
            Assert.IsFalse(ballSmoother.IsCorrecting);
            Assert.That(
                samples * RenderDeltaTime,
                Is.LessThanOrEqualTo(
                    MaximumCorrectionSeconds + RenderDeltaTime));
            Assert.That(
                Vector3.Distance(displayedRemote, remoteTarget),
                Is.LessThanOrEqualTo(FinalErrorThreshold));
            Assert.That(
                Vector3.Distance(displayedBall, ballTarget),
                Is.LessThanOrEqualTo(FinalErrorThreshold));
        }

        [Test]
        public void OverflowRebase_AtExistingSnapThreshold_SnapsImmediately()
        {
            PresentationCorrectionSmoother smoother = CreateSmoother();
            Vector3 target = Vector3.right * 2f;

            smoother.BeginCorrection(Vector3.zero, target);

            Assert.IsFalse(smoother.IsCorrecting);
            Assert.AreEqual(
                target,
                smoother.Evaluate(target, RenderDeltaTime));
        }

        private static PresentationCorrectionSmoother CreateSmoother()
        {
            return new PresentationCorrectionSmoother(
                0.1f,
                MaximumCorrectionSeconds,
                10f,
                2f);
        }

        private static ViewWorldState CreateHeldView(
            int predictedFrame,
            int confirmedFrame,
            int presentationFrame,
            int holderPlayerIndex,
            ViewSampleSource ballSource,
            float localFromX,
            float localToX,
            float remoteFromX,
            float remoteToX)
        {
            SimulationPlayerState localFrom = CreatePlayerState(localFromX);
            SimulationPlayerState localTo = CreatePlayerState(localToX);
            SimulationPlayerState remoteFrom = CreatePlayerState(remoteFromX);
            SimulationPlayerState remoteTo = CreatePlayerState(remoteToX);
            SimulationPlayerState holderFrom = holderPlayerIndex == 0
                ? localFrom
                : remoteFrom;
            SimulationPlayerState holderTo = holderPlayerIndex == 0
                ? localTo
                : remoteTo;
            SimulationBallState ballFrom = CreateHeldBallState(
                holderFrom.position,
                holderPlayerIndex);
            SimulationBallState ballTo = CreateHeldBallState(
                holderTo.position,
                holderPlayerIndex);
            var local = new ViewPlayerState(
                0,
                localFrom,
                localTo,
                predictedFrame - 1,
                predictedFrame,
                ViewSampleSource.Predicted);
            var remote = new ViewPlayerState(
                1,
                remoteFrom,
                remoteTo,
                presentationFrame - 1,
                presentationFrame,
                ViewSampleSource.Confirmed);
            var ball = new ViewBallState(
                ballFrom,
                ballTo,
                ballSource == ViewSampleSource.Predicted
                    ? predictedFrame - 1
                    : presentationFrame - 1,
                ballSource == ViewSampleSource.Predicted
                    ? predictedFrame
                    : presentationFrame,
                ballSource);
            return new ViewWorldState(
                predictedFrame,
                confirmedFrame,
                presentationFrame,
                local,
                remote,
                ball);
        }

        private static SimulationPlayerState CreatePlayerState(float x)
        {
            return new SimulationPlayerState
            {
                position = new FixedVector3(
                    FixedInt.FromFloat(x),
                    FixedInt.Zero,
                    FixedInt.Zero)
            };
        }

        private static SimulationBallState CreateHeldBallState(
            FixedVector3 holderPosition,
            int holderPlayerIndex)
        {
            return new SimulationBallState
            {
                position = holderPosition + new FixedVector3(
                    FixedInt.FromFloat(0.4f),
                    FixedInt.FromFloat(1.3f),
                    FixedInt.FromFloat(0.6f)),
                state = (int)BallEntity.EState.Held,
                holderPlayerIndex = holderPlayerIndex
            };
        }

        private static CorrectionTrace RunRollbackTrajectory(
            float[] predictedRemoteX,
            float[] correctedRemoteX)
        {
            Assert.AreEqual(4, predictedRemoteX.Length);
            Assert.AreEqual(6, correctedRemoteX.Length);
            var interpolator = new PresentationFrameInterpolator(2);
            ReplaceWindow(interpolator, 13, predictedRemoteX);
            var smoother = CreateSmoother();
            Vector3 firstDisplay = smoother.Evaluate(
                EvaluateRemote(interpolator, 0f),
                RenderDeltaTime);
            Vector3 display = smoother.Evaluate(
                EvaluateRemote(interpolator, 0.5f),
                RenderDeltaTime);
            int previousDirection = Direction(display.x - firstDisplay.x);

            ReplaceWindow(interpolator, 13, correctedRemoteX);
            var targets = new List<Vector3>
            {
                EvaluateRemote(interpolator, 0.5f),
                EvaluateRemote(interpolator, 1f)
            };
            PlayerEntity[] players = CreatePlayers(correctedRemoteX[3]);
            BallEntity ball = CreateFreeBall();
            for (int currentFrame = 14; currentFrame <= 15; currentFrame++)
            {
                players[1].position.x = FixedInt.FromFloat(
                    correctedRemoteX[currentFrame - 10]);
                interpolator.PushLogicFrame(currentFrame, players, ball);
                targets.Add(EvaluateRemote(interpolator, 0.5f));
                targets.Add(EvaluateRemote(interpolator, 1f));
            }

            smoother.BeginCorrection(display, targets[0]);
            int correctionSamples = 0;
            int maximumCorrectionSamples = Mathf.CeilToInt(
                (MaximumCorrectionSeconds + RenderDeltaTime) /
                RenderDeltaTime);
            float reverseDistance = 0f;
            float maximumDeviation = 0f;
            int directionChanges = 0;
            Vector3 finalTarget = targets[0];

            void EvaluateTarget(Vector3 target)
            {
                bool wasCorrecting = smoother.IsCorrecting;
                Vector3 next = smoother.Evaluate(
                    target,
                    RenderDeltaTime);
                if (wasCorrecting)
                    correctionSamples++;
                if (next.x < display.x)
                    reverseDistance += display.x - next.x;

                int direction = Direction(next.x - display.x);
                if (direction != 0)
                {
                    if (previousDirection != 0 &&
                        direction != previousDirection)
                    {
                        directionChanges++;
                    }
                    previousDirection = direction;
                }

                maximumDeviation = Mathf.Max(
                    maximumDeviation,
                    Mathf.Abs(next.x - target.x));
                display = next;
                finalTarget = target;
            }

            foreach (Vector3 target in targets)
                EvaluateTarget(target);

            while (smoother.IsCorrecting &&
                   correctionSamples < maximumCorrectionSamples)
            {
                EvaluateTarget(finalTarget);
            }

            return new CorrectionTrace(
                reverseDistance,
                directionChanges,
                maximumDeviation,
                correctionSamples * RenderDeltaTime,
                Vector3.Distance(display, finalTarget),
                smoother.IsCorrecting);
        }

        private static void AssertCorrectionCompleted(CorrectionTrace trace)
        {
            Assert.IsFalse(trace.IsCorrecting);
            Assert.That(
                trace.CorrectionElapsedSeconds,
                Is.LessThanOrEqualTo(
                    MaximumCorrectionSeconds + RenderDeltaTime));
            Assert.That(
                trace.FinalError,
                Is.LessThanOrEqualTo(FinalErrorThreshold));
        }

        private static void ReplaceWindow(
            PresentationFrameInterpolator interpolator,
            int currentFrame,
            float[] remoteX)
        {
            int currentIndex = currentFrame - 10;
            interpolator.ReplaceHistoryAfterRollback(
                CreateSnapshot(
                    currentFrame,
                    0f,
                    remoteX[currentIndex]),
                CreateSnapshot(
                    currentFrame - 1,
                    0f,
                    remoteX[currentIndex - 1]),
                CreateSnapshot(
                    currentFrame - 2,
                    0f,
                    remoteX[currentIndex - 2]),
                CreateSnapshot(
                    currentFrame - 3,
                    0f,
                    remoteX[currentIndex - 3]));
        }

        private static int Direction(float delta)
        {
            if (delta > FinalErrorThreshold)
                return 1;
            if (delta < -FinalErrorThreshold)
                return -1;
            return 0;
        }

        private static PlayerEntity[] CreatePlayers(float remoteX)
        {
            var local = new PlayerEntity();
            local.Reset(FixedVector3.Zero, 0);
            var remote = new PlayerEntity();
            remote.Reset(
                new FixedVector3(
                    FixedInt.FromFloat(remoteX),
                    FixedInt.Zero,
                    FixedInt.Zero),
                1);
            return new[] { local, remote };
        }

        private static BallEntity CreateFreeBall()
        {
            var ball = new BallEntity();
            ball.Reset(FixedVector3.Zero);
            return ball;
        }

        private static Vector3 EvaluateRemote(
            PresentationFrameInterpolator interpolator,
            float alpha)
        {
            var players = new Vector3[2];
            interpolator.Evaluate(0, alpha, players, out _);
            return players[1];
        }

        private static FrameSnapshot CreateSnapshot(
            int frameID,
            float localX,
            float remoteX,
            float ballX = 0f,
            float ballY = 0f,
            float ballZ = 0f,
            BallEntity.EState ballState = BallEntity.EState.Free,
            int ballHolder = -1)
        {
            return new FrameSnapshot
            {
                frameID = frameID,
                player1X = FixedInt.FromFloat(localX),
                player1Y = FixedInt.Zero,
                player1Z = FixedInt.Zero,
                player2X = FixedInt.FromFloat(remoteX),
                player2Y = FixedInt.Zero,
                player2Z = FixedInt.Zero,
                player1State = (int)PlayerEntity.EState.Idle,
                player2State = (int)PlayerEntity.EState.Idle,
                ballPosX = FixedInt.FromFloat(ballX),
                ballPosY = FixedInt.FromFloat(ballY),
                ballPosZ = FixedInt.FromFloat(ballZ),
                ballState = (int)ballState,
                ballHolder = ballHolder
            };
        }

        private readonly struct CorrectionTrace
        {
            public CorrectionTrace(
                float unexpectedReverseDistance,
                int directionChanges,
                float maximumTargetDeviation,
                float correctionElapsedSeconds,
                float finalError,
                bool isCorrecting)
            {
                UnexpectedReverseDistance = unexpectedReverseDistance;
                DirectionChanges = directionChanges;
                MaximumTargetDeviation = maximumTargetDeviation;
                CorrectionElapsedSeconds = correctionElapsedSeconds;
                FinalError = finalError;
                IsCorrecting = isCorrecting;
            }

            public float UnexpectedReverseDistance { get; }
            public int DirectionChanges { get; }
            public float MaximumTargetDeviation { get; }
            public float CorrectionElapsedSeconds { get; }
            public float FinalError { get; }
            public bool IsCorrecting { get; }
        }
    }
}

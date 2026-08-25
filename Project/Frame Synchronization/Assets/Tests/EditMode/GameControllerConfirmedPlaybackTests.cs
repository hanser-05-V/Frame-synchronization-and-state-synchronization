using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FrameSyncDemo.Tests
{
    public class GameControllerConfirmedPlaybackTests
    {
        private const BindingFlags PrivateInstance =
            BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void AdvanceRealtimePresentation_HeadZeroBuildsFrameZeroSameCall()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator();
            PresentationFrameInterpolator interpolator =
                CreateInitializedInterpolator(coordinator);
            AdvanceDualActual(coordinator, 0, new FrameInput(), new FrameInput());
            ConfirmedPresentationCursor cursor = CreateCursor();
            GameObject owner = new GameObject("ConfirmedPlaybackTest");
            try
            {
                GameController controller = owner.AddComponent<GameController>();
                Configure(controller, coordinator, cursor, interpolator);

                bool submitted = InvokeAdvance(controller, 0f, 0.033f);

                Assert.IsTrue(submitted);
                Assert.AreEqual(0, cursor.ActiveToFrame);
                Assert.AreEqual(0, interpolator.DeepestFrameID);
                ConfirmedPlaybackAdvance sample = GetField<ConfirmedPlaybackAdvance>(
                    controller,
                    "_lastConfirmedPlaybackAdvance");
                Assert.IsTrue(sample.HasPresentationFrame);
                Assert.AreEqual(0, sample.ActiveToFrame);
            }
            finally
            {
                Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void AdvanceRealtimePresentation_LongFramePublishesOnlyFinalCursorInterval()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator();
            PresentationFrameInterpolator interpolator =
                CreateInitializedInterpolator(coordinator);
            for (int frame = 0; frame <= 4; frame++)
            {
                AdvanceDualActual(
                    coordinator,
                    frame,
                    new FrameInput(),
                    new FrameInput());
            }
            ConfirmedPresentationCursor cursor = CreateCursor(
                catchUpGainPerFrame: 0f);
            GameObject owner = new GameObject("ConfirmedPlaybackTest");
            try
            {
                GameController controller = owner.AddComponent<GameController>();
                Configure(controller, coordinator, cursor, interpolator);

                bool submitted = InvokeAdvance(controller, 0.080f, 0.033f);

                ConfirmedPlaybackAdvance sample = GetField<ConfirmedPlaybackAdvance>(
                    controller,
                    "_lastConfirmedPlaybackAdvance");
                Assert.IsTrue(submitted);
                Assert.AreEqual(2, sample.CrossedIntervalCount);
                Assert.AreEqual(sample.ActiveToFrame, interpolator.DeepestFrameID);
                Assert.AreEqual(
                    coordinator.PredictedFrame,
                    interpolator.CurrentFrameID);
            }
            finally
            {
                Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void AdvanceRealtimePresentation_MissingHistoryFaultDoesNotPauseLogic()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator(
                snapshotCapacity: 1);
            PresentationFrameInterpolator interpolator =
                CreateInitializedInterpolator(coordinator);
            AdvanceDualActual(coordinator, 0, new FrameInput(), new FrameInput());
            AdvanceDualActual(coordinator, 1, new FrameInput(), new FrameInput());
            ConfirmedPresentationCursor cursor = CreateCursor();
            GameObject owner = new GameObject("ConfirmedPlaybackTest");
            try
            {
                GameController controller = owner.AddComponent<GameController>();
                Configure(controller, coordinator, cursor, interpolator);
                LogAssert.Expect(
                    LogType.Error,
                    "[RouteC][PresentationFault] confirmed range=[-1,0] " +
                    "is unavailable; confirmed presentation is held.");

                bool submitted = InvokeAdvance(controller, 0f, 0.033f);

                Assert.IsFalse(submitted);
                Assert.IsTrue(cursor.IsFaulted);
                Assert.IsFalse(GetField<bool>(controller, "_paused"));
                Assert.IsTrue(GetField<ConfirmedPlaybackAdvance>(
                    controller,
                    "_lastConfirmedPlaybackAdvance").IsFaulted);
            }
            finally
            {
                Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void AdvanceRealtimePresentation_LocalPredictedShotPreservesConfirmedCursor()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator(
                initialBallFree: false);
            PresentationFrameInterpolator interpolator =
                CreateInitializedInterpolator(coordinator);
            AdvanceDualActual(coordinator, 0, new FrameInput(), new FrameInput());
            ConfirmedPresentationCursor cursor = CreateCursor();
            GameObject owner = new GameObject("ConfirmedPlaybackTest");
            try
            {
                GameController controller = owner.AddComponent<GameController>();
                Configure(controller, coordinator, cursor, interpolator);
                Assert.IsTrue(InvokeAdvance(controller, 0f, 0.033f));

                AdvanceSingleActual(
                    coordinator,
                    1,
                    0,
                    new FrameInput(0, 0x01));
                Assert.IsTrue(InvokeAdvance(controller, 0f, 0.033f));
                var positions = new Vector3[2];
                interpolator.Evaluate(
                    0,
                    0.75f,
                    cursor.InterpolationAlpha,
                    positions,
                    out PresentationBallSample ball);

                Assert.AreEqual(0, cursor.ActiveToFrame);
                Assert.AreEqual(-1, ball.AttachedPlayerIndex);
                Assert.AreNotEqual(BallEntity.EState.Held, coordinator.PredictedWorld.ball.state);
            }
            finally
            {
                Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void AdvanceRealtimePresentation_RemoteHeldOverflowRebasesHolderWithFinalInterval()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator();
            PresentationFrameInterpolator interpolator =
                CreateInitializedInterpolator(coordinator);
            AdvanceDualActual(
                coordinator,
                0,
                new FrameInput(),
                new FrameInput(0, 0x20));
            ConfirmedPresentationCursor cursor = CreateCursor();
            GameObject owner = new GameObject("ConfirmedPlaybackTest");
            try
            {
                GameController controller = owner.AddComponent<GameController>();
                Configure(controller, coordinator, cursor, interpolator);
                Assert.IsTrue(InvokeAdvance(controller, 0f, 0.033f));

                for (int frame = 1; frame <= 9; frame++)
                {
                    AdvanceDualActual(
                        coordinator,
                        frame,
                        new FrameInput(),
                        new FrameInput(1, 0));
                }
                Assert.IsTrue(InvokeAdvance(controller, 0f, 0.033f));
                ConfirmedPlaybackAdvance sample = GetField<ConfirmedPlaybackAdvance>(
                    controller,
                    "_lastConfirmedPlaybackAdvance");
                var positions = new Vector3[2];
                interpolator.Evaluate(
                    0,
                    0.75f,
                    sample.Alpha,
                    positions,
                    out PresentationBallSample ball);

                Assert.IsTrue(sample.IsOverflowRebase);
                Assert.AreEqual(9, sample.ActiveToFrame);
                Assert.AreEqual(sample.ActiveToFrame, interpolator.DeepestFrameID);
                Assert.AreEqual(1, ball.AttachedPlayerIndex);
                Assert.That(
                    Vector3.Distance(ball.BasePosition, positions[1]),
                    Is.GreaterThan(0f));
            }
            finally
            {
                Object.DestroyImmediate(owner);
            }
        }

        private static void Configure(
            GameController controller,
            FrameSyncCoordinator coordinator,
            ConfirmedPresentationCursor cursor,
            PresentationFrameInterpolator interpolator)
        {
            SetField(controller, "_frameSyncCoordinator", coordinator);
            SetField(controller, "_confirmedPresentationCursor", cursor);
            SetField(controller, "_presentationInterpolator", interpolator);
            SetField(
                controller,
                "_runtimeNetworkDiagnostics",
                new RuntimeNetworkDiagnostics(false, _ => { }));
            NetworkConfig.LocalPlayerID = 0;
        }

        private static bool InvokeAdvance(
            GameController controller,
            float deltaTimeSeconds,
            float frameDurationSeconds)
        {
            MethodInfo method = typeof(GameController).GetMethod(
                "AdvanceRealtimePresentation",
                PrivateInstance);
            Assert.IsNotNull(method);
            return (bool)method.Invoke(
                controller,
                new object[] { deltaTimeSeconds, frameDurationSeconds });
        }

        private static void SetField<T>(
            GameController controller,
            string fieldName,
            T value)
        {
            FieldInfo field = typeof(GameController).GetField(
                fieldName,
                PrivateInstance);
            Assert.IsNotNull(field);
            field.SetValue(controller, value);
        }

        private static T GetField<T>(
            GameController controller,
            string fieldName)
        {
            FieldInfo field = typeof(GameController).GetField(
                fieldName,
                PrivateInstance);
            Assert.IsNotNull(field);
            return (T)field.GetValue(controller);
        }

        private static ConfirmedPresentationCursor CreateCursor(
            float catchUpGainPerFrame =
                ConfirmedPlaybackSettings.DefaultCatchUpGainPerFrame)
        {
            return new ConfirmedPresentationCursor(
                new ConfirmedPlaybackSettings(
                    targetReadyBacklog: 0,
                    maxReadyBacklog: 8,
                    catchUpGainPerFrame,
                    maximumPlaybackSpeed: 1.5f,
                    speedSmoothingSeconds: 0.1f,
                    catchUpEnterExcessFrames: 0.25f,
                    catchUpExitExcessFrames: 0.1f,
                    overflowCorrectionMaximumSeconds: 0.2f));
        }

        private static PresentationFrameInterpolator
            CreateInitializedInterpolator(FrameSyncCoordinator coordinator)
        {
            Assert.IsTrue(ViewWorldBuilder.TryBuild(
                coordinator,
                0,
                -1,
                out ViewWorldState initialView));
            var interpolator = new PresentationFrameInterpolator(2);
            interpolator.Reset(initialView);
            return interpolator;
        }

        private static FrameSyncCoordinator CreateCoordinator(
            int snapshotCapacity = 16,
            bool initialBallFree = true)
        {
            var player0 = new PlayerEntity();
            player0.Reset(
                new FixedVector3(
                    FixedInt.FromInt(-1),
                    FixedInt.Zero,
                    FixedInt.Zero),
                0);
            var player1 = new PlayerEntity();
            player1.Reset(
                new FixedVector3(
                    FixedInt.FromInt(1),
                    FixedInt.Zero,
                    FixedInt.Zero),
                1);
            var ball = new BallEntity();
            ball.Reset(FixedVector3.Zero);
            if (initialBallFree)
            {
                ball.state = BallEntity.EState.Free;
                ball.holderPlayerIndex = -1;
            }
            else
            {
                player0.hasBall = true;
                ball.state = BallEntity.EState.Held;
                ball.holderPlayerIndex = 0;
                Assert.IsTrue(BallPossessionSystem.TryUpdateHeldBall(
                    player0,
                    ball));
            }
            var predictedWorld = new DeterministicWorld(
                new[] { player0, player1 },
                new[]
                {
                    new PlayerStateMachine(player0),
                    new PlayerStateMachine(player1)
                },
                ball,
                FixedInt.One,
                CourtConstant.LogicDeltaTime);
            SimulationWorldState initial = predictedWorld.Capture(-1);
            return new FrameSyncCoordinator(
                predictedWorld,
                initial,
                FixedInt.One,
                CourtConstant.LogicDeltaTime,
                snapshotCapacity);
        }

        private static void AdvanceDualActual(
            FrameSyncCoordinator coordinator,
            int frame,
            FrameInput player0,
            FrameInput player1)
        {
            coordinator.RecordActual(frame, 0, player0);
            coordinator.RecordActual(frame, 1, player1);
            FrameInputLedger.ResolvedFrame inputs =
                coordinator.ResolveForPrediction(frame);
            Assert.IsTrue(coordinator.Advance(frame, inputs).Succeeded);
        }

        private static void AdvanceSingleActual(
            FrameSyncCoordinator coordinator,
            int frame,
            int playerIndex,
            FrameInput input)
        {
            coordinator.RecordActual(frame, playerIndex, input);
            FrameInputLedger.ResolvedFrame inputs =
                coordinator.ResolveForPrediction(frame);
            Assert.IsTrue(coordinator.Advance(frame, inputs).Succeeded);
        }
    }
}

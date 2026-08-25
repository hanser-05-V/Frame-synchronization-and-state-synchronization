using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace FrameSyncDemo.Tests
{
    public class ConfirmedPlaybackRuntimeAdapterTests
    {
        [Test]
        public void PresentationControllerDiagnosticsAndCorrection_DoNotMutateLogicWorldsOrHashes()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator();
            for (int frame = 0; frame <= 2; frame++)
            {
                AdvanceDualActual(
                    coordinator,
                    frame,
                    new FrameInput(1, 0),
                    new FrameInput(3, 0));
            }
            SimulationWorldState confirmedBefore = coordinator.ConfirmedWorld;
            SimulationWorldState predictedBefore = coordinator.PredictedWorld;
            ulong confirmedHashBefore = WorldHash.Compute(
                confirmedBefore,
                coordinator.ConfirmedFrame);
            ulong predictedHashBefore = WorldHash.Compute(
                predictedBefore,
                coordinator.PredictedFrame);

            ConfirmedPresentationCursor cursor = CreateCursor();
            ConfirmedPlaybackAdvance normal = cursor.Advance(
                coordinator.ConfirmedFrame,
                0.5f * CourtConstant.LogicDeltaTime.ToFloat(),
                CourtConstant.LogicDeltaTime.ToFloat());
            ConfirmedPlaybackAdvance overflow = cursor.Advance(
                coordinator.ConfirmedFrame + 9,
                0f,
                CourtConstant.LogicDeltaTime.ToFloat());
            cursor.RecalculateAfterRollback(coordinator.ConfirmedFrame + 9);

            var lines = new List<string>();
            var diagnostics = new RuntimeNetworkDiagnostics(
                true,
                lines.Add,
                1000,
                targetReadyBacklog: 0,
                maxReadyBacklog: 8);
            diagnostics.RecordActualArrival(2, 100);
            diagnostics.RecordConfirmedPlayback(150, normal);
            diagnostics.RecordConfirmedPlayback(160, overflow);
            diagnostics.RecordBallSourceSwitch(
                170,
                overflow.ActiveToFrame,
                ViewSampleSource.Confirmed,
                1);

            var smoother = new PresentationCorrectionSmoother(
                0.1f,
                0.2f,
                10f,
                2f);
            smoother.BeginCorrection(
                new Vector3(0.4f, 0f, 0f),
                Vector3.zero,
                0.2f);
            smoother.Evaluate(Vector3.zero, 0.016f);

            SimulationWorldState confirmedAfter = coordinator.ConfirmedWorld;
            SimulationWorldState predictedAfter = coordinator.PredictedWorld;
            Assert.AreEqual(confirmedBefore, confirmedAfter);
            Assert.AreEqual(predictedBefore, predictedAfter);
            Assert.AreEqual(
                confirmedHashBefore,
                WorldHash.Compute(
                    confirmedAfter,
                    coordinator.ConfirmedFrame));
            Assert.AreEqual(
                predictedHashBefore,
                WorldHash.Compute(
                    predictedAfter,
                    coordinator.PredictedFrame));
            Assert.IsNotEmpty(lines);
        }

        private static ConfirmedPresentationCursor CreateCursor()
        {
            return new ConfirmedPresentationCursor(
                new ConfirmedPlaybackSettings(
                    targetReadyBacklog: 0,
                    maxReadyBacklog: 8,
                    catchUpGainPerFrame: 0.15f,
                    maximumPlaybackSpeed: 1.5f,
                    speedSmoothingSeconds: 0.1f,
                    catchUpEnterExcessFrames: 0.25f,
                    catchUpExitExcessFrames: 0.1f,
                    overflowCorrectionMaximumSeconds: 0.2f));
        }

        private static FrameSyncCoordinator CreateCoordinator()
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
            ball.state = BallEntity.EState.Free;
            ball.holderPlayerIndex = -1;
            var world = new DeterministicWorld(
                new[] { player0, player1 },
                new[]
                {
                    new PlayerStateMachine(player0),
                    new PlayerStateMachine(player1)
                },
                ball,
                FixedInt.One,
                CourtConstant.LogicDeltaTime);
            SimulationWorldState initial = world.Capture(-1);
            return new FrameSyncCoordinator(
                world,
                initial,
                FixedInt.One,
                CourtConstant.LogicDeltaTime);
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
    }
}

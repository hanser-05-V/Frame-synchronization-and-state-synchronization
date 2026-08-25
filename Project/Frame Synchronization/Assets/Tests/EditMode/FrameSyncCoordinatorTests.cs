using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class FrameSyncCoordinatorTests
    {
        private static readonly FixedInt MoveDistance = FixedInt.FromInt(1);

        [Test]
        public void RuntimeAssembly_FrameSyncCoordinator_IsAvailable()
        {
            TypeInfo type = typeof(PredictionSystem).Assembly
                .DefinedTypes
                .FirstOrDefault(candidate =>
                    candidate.FullName == "FrameSyncDemo.FrameSyncCoordinator");

            Assert.IsNotNull(type);
        }

        [Test]
        public void FrameSyncCoordinator_P2CNormalAdvanceSurface_IsAvailable()
        {
            Type type = typeof(FrameSyncCoordinator);
            ConstructorInfo constructor = type.GetConstructor(new[]
            {
                typeof(DeterministicWorld),
                typeof(SimulationWorldState).MakeByRefType(),
                typeof(FixedInt),
                typeof(FixedInt),
                typeof(int)
            });

            Assert.IsNotNull(constructor, "Coordinator constructor is missing.");
            Assert.IsNotNull(type.GetProperty("ConfirmedFrame"));
            Assert.IsNotNull(type.GetProperty("PredictedFrame"));
            Assert.IsNotNull(type.GetProperty("ConfirmedWorld"));
            Assert.IsNotNull(type.GetProperty("PredictedWorld"));
            Assert.IsNotNull(type.GetMethod("RecordActual"));
            Assert.IsNotNull(type.GetMethod("ResolveForPrediction"));
            Assert.IsNotNull(type.GetMethod("Advance"));
            Assert.IsNotNull(type.GetMethod("CatchUpConfirmed"));
            Assert.IsNotNull(type.GetMethod("TryGetSnapshot"));
        }

        [Test]
        public void RuntimeAssembly_P2CReconcileSurface_IsAvailable()
        {
            TypeInfo resultType = typeof(PredictionSystem).Assembly
                .DefinedTypes
                .FirstOrDefault(candidate =>
                    candidate.FullName == "FrameSyncDemo.ReconcileResult");

            Assert.IsNotNull(resultType, "ReconcileResult type is missing.");
            Assert.IsNotNull(
                typeof(FrameSyncCoordinator).GetMethod("Reconcile"),
                "Coordinator Reconcile API is missing.");
        }

        [Test]
        public void FrameSyncCoordinator_UnityAdapterReadSurface_IsAvailable()
        {
            Type type = typeof(FrameSyncCoordinator);

            Assert.IsNotNull(type.GetProperty("StartFrame"));
            Assert.IsNotNull(type.GetProperty("ConfirmedThroughFrame"));
            Assert.IsNotNull(type.GetProperty("FirstRetainedFrame"));
            Assert.IsNotNull(type.GetProperty("HasIntegrityFault"));
            Assert.IsNotNull(type.GetMethod("TryGetEarliestMismatch"));
            Assert.IsNotNull(type.GetMethod("TryGetActualFrame"));
            Assert.IsNotNull(type.GetMethod("TryGetInFlightReplayPlan"));
            Assert.IsNotNull(type.GetMethod("TryPruneBefore"));
            Assert.IsNotNull(type.GetMethod("TryGetFrameSnapshot"));
            Assert.IsNotNull(type.GetMethod("SetMoveDistance"));
            Assert.IsNotNull(type.GetMethod("TryRestorePredictedFromTrack"));
            Assert.IsNotNull(typeof(FrameAdvanceResult).GetProperty("SimulationResult"));
        }

        [Test]
        public void EarliestRecoverableCanonicalFrame_EmptySnapshots_UsesInitialWorld()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator(2);

            Assert.AreEqual(0, coordinator.EarliestRecoverableCanonicalFrame);
        }

        [Test]
        public void EarliestRecoverableCanonicalFrame_SnapshotRingRequiresPrecedingFrame()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator(2);

            AdvanceSequential(coordinator, 0);
            Assert.AreEqual(1, coordinator.EarliestRecoverableCanonicalFrame);

            AdvanceSequential(coordinator, 1);
            Assert.AreEqual(1, coordinator.EarliestRecoverableCanonicalFrame);

            AdvanceSequential(coordinator, 2);
            Assert.AreEqual(2, coordinator.EarliestRecoverableCanonicalFrame);
        }

        [Test]
        public void EarliestRecoverableCanonicalFrame_LedgerPruneCanAdvanceFloorFurther()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator(16);
            for (int frame = 0; frame <= 3; frame++)
            {
                RecordBoth(coordinator, frame, default, default);
                AdvanceSequential(coordinator, frame);
            }

            Assert.AreEqual(
                FrameInputLedger.PruneResult.Success,
                coordinator.TryPruneBefore(3));

            Assert.AreEqual(3, coordinator.FirstRetainedFrame);
            Assert.AreEqual(3, coordinator.EarliestRecoverableCanonicalFrame);
        }

        [Test]
        public void Advance_OneActualAndOnePrediction_AdvancesOnlyPredictedWorld()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator();
            coordinator.RecordActual(0, 0, new FrameInput(1, 0));
            FrameInputLedger.ResolvedFrame inputs =
                coordinator.ResolveForPrediction(0);

            FrameAdvanceResult result = coordinator.Advance(0, inputs);

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(-1, coordinator.ConfirmedFrame);
            Assert.AreEqual(0, coordinator.PredictedFrame);
            Assert.AreEqual(-3000, coordinator.ConfirmedWorld.player0.position.x._raw);
            Assert.AreEqual(1000, coordinator.PredictedWorld.player0.position.z._raw);
        }

        [Test]
        public void Advance_ConfirmationHoleThenFill_CatchesUpEveryActualFrameInOrder()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator();
            RecordBoth(coordinator, 0, default, default);
            RecordBoth(coordinator, 2, new FrameInput(3, 0), default);
            AdvanceSequential(coordinator, 0);
            AdvanceSequential(coordinator, 1);
            AdvanceSequential(coordinator, 2);

            Assert.AreEqual(0, coordinator.ConfirmedFrame);
            Assert.IsFalse(coordinator.TryGetSnapshot(WorldTrack.Confirmed, 1, out _));

            RecordBoth(coordinator, 1, new FrameInput(1, 0), default);
            FrameAdvanceResult catchup = coordinator.CatchUpConfirmed();

            Assert.IsTrue(catchup.Succeeded);
            Assert.AreEqual(2, coordinator.ConfirmedFrame);
            Assert.IsTrue(coordinator.TryGetSnapshot(
                WorldTrack.Confirmed,
                1,
                out SimulationWorldState frameOne));
            Assert.IsTrue(coordinator.TryGetSnapshot(
                WorldTrack.Confirmed,
                2,
                out SimulationWorldState frameTwo));
            Assert.AreEqual(1000, frameOne.player0.position.z._raw);
            Assert.AreEqual(1000, frameTwo.player0.position.z._raw);
            Assert.AreEqual(-2000, frameTwo.player0.position.x._raw);
        }

        [Test]
        public void Advance_PredictedMutation_DoesNotAliasConfirmedWorld()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator();
            coordinator.RecordActual(0, 0, new FrameInput(1, 0));

            AdvanceSequential(coordinator, 0);

            Assert.AreEqual(-3000, coordinator.ConfirmedWorld.player0.position.x._raw);
            Assert.AreEqual(0, coordinator.ConfirmedWorld.player0.position.z._raw);
            Assert.AreEqual(-3000, coordinator.PredictedWorld.player0.position.x._raw);
            Assert.AreEqual(1000, coordinator.PredictedWorld.player0.position.z._raw);
        }

        [Test]
        public void Reconcile_LateMismatch_RebuildsPredictedFromLatestConfirmedBaseline()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator();
            coordinator.RecordActual(0, 0, default);
            AdvanceSequential(coordinator, 0);
            AdvanceSequential(coordinator, 1);
            Assert.AreEqual(0, coordinator.PredictedWorld.player1.position.z._raw);
            coordinator.RecordActual(0, 1, new FrameInput(1, 0));
            coordinator.RecordActual(1, 0, default);
            coordinator.RecordActual(1, 1, new FrameInput(1, 0));

            ReconcileResult result = coordinator.Reconcile();

            Assert.IsTrue(result.Succeeded);
            Assert.IsTrue(result.Replayed);
            Assert.AreEqual(0, result.MismatchFrame);
            Assert.AreEqual(1, result.RestoredFrame);
            Assert.AreEqual(0, result.ReplayedFrameCount);
            Assert.AreEqual(2, coordinator.PredictedWorld.player1.position.z.ToInt());
            Assert.AreEqual(
                WorldHash.Compute(coordinator.ConfirmedWorld, 1),
                WorldHash.Compute(coordinator.PredictedWorld, 1));
        }

        [Test]
        public void Reconcile_TwoOutOfOrderMismatches_UsesOneEarliestSuffixAndClearsBoth()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator();
            coordinator.RecordActual(0, 0, default);
            coordinator.RecordActual(1, 0, default);
            AdvanceSequential(coordinator, 0);
            AdvanceSequential(coordinator, 1);
            coordinator.RecordActual(1, 1, new FrameInput(3, 0));
            coordinator.RecordActual(0, 1, new FrameInput(1, 0));

            ReconcileResult first = coordinator.Reconcile();
            ReconcileResult second = coordinator.Reconcile();

            Assert.IsTrue(first.Succeeded);
            Assert.IsTrue(first.Replayed);
            Assert.AreEqual(0, first.MismatchFrame);
            Assert.AreEqual(0, first.ReplayedFrameCount);
            Assert.IsTrue(second.Succeeded);
            Assert.IsFalse(second.Replayed);
            Assert.AreEqual(4, coordinator.ConfirmedWorld.player1.position.x.ToInt());
            Assert.AreEqual(1, coordinator.ConfirmedWorld.player1.position.z.ToInt());
            Assert.AreEqual(
                WorldHash.Compute(coordinator.ConfirmedWorld, 1),
                WorldHash.Compute(coordinator.PredictedWorld, 1));
        }

        [Test]
        public void Reconcile_PredictionMatched_DoesNotReplay()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator();
            coordinator.RecordActual(0, 0, default);
            AdvanceSequential(coordinator, 0);
            coordinator.RecordActual(0, 1, default);

            ReconcileResult result = coordinator.Reconcile();

            Assert.IsTrue(result.Succeeded);
            Assert.IsFalse(result.Replayed);
            Assert.AreEqual(0, coordinator.ConfirmedFrame);
        }

        [Test]
        public void Reconcile_EarlyMismatchSnapshotOverwritten_UsesLatestConfirmedBaseline()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator(2);
            RecordBoth(coordinator, 0, default, default);
            AdvanceSequential(coordinator, 0);
            for (int frame = 1; frame <= 3; frame++)
            {
                coordinator.RecordActual(frame, 0, default);
                AdvanceSequential(coordinator, frame);
            }

            coordinator.RecordActual(1, 1, new FrameInput(1, 0));
            coordinator.RecordActual(2, 1, default);
            coordinator.RecordActual(3, 1, default);

            ReconcileResult result = coordinator.Reconcile();

            Assert.IsTrue(result.Succeeded);
            Assert.IsTrue(result.Replayed);
            Assert.AreEqual(1, result.MismatchFrame);
            Assert.AreEqual(3, result.RestoredFrame);
            Assert.AreEqual(0, result.ReplayedFrameCount);
            Assert.AreEqual(3, coordinator.ConfirmedFrame);
            Assert.AreEqual(3, coordinator.PredictedFrame);
            Assert.AreEqual(
                WorldHash.Compute(coordinator.ConfirmedWorld, 3),
                WorldHash.Compute(coordinator.PredictedWorld, 3));
            Assert.IsTrue(coordinator.TryGetSnapshot(
                WorldTrack.Predicted,
                3,
                out SimulationWorldState predictedSnapshot));
            Assert.AreEqual(
                WorldHash.Compute(coordinator.ConfirmedWorld, 3),
                WorldHash.Compute(predictedSnapshot, 3));
            Assert.IsFalse(coordinator.TryGetEarliestMismatch(out _));
        }

        [Test]
        public void Reconcile_IntegrityFault_ReturnsFailureAndDoesNotPublishSuccess()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator();
            RecordBoth(coordinator, 0, default, default);
            AdvanceSequential(coordinator, 0);
            FrameInputLedger.ActualArrival conflict = coordinator.RecordActual(
                0,
                0,
                new FrameInput(1, 0));

            ReconcileResult result = coordinator.Reconcile();

            Assert.AreEqual(
                FrameInputLedger.ActualDisposition.ConflictingDuplicate,
                conflict.Disposition);
            Assert.IsTrue(coordinator.HasIntegrityFault);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(
                FrameInputLedger.ReplayPlanResult.IntegrityFault,
                result.PlanResult);
        }

        [Test]
        public void Advance_IntegrityFault_DoesNotAdvanceEitherWorld()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator();
            FrameInputLedger.ResolvedFrame inputs =
                coordinator.ResolveForPrediction(0);
            coordinator.RecordActual(0, 0, default);
            coordinator.RecordActual(0, 0, new FrameInput(1, 0));

            FrameAdvanceResult result = coordinator.Advance(0, inputs);

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(coordinator.HasIntegrityFault);
            Assert.AreEqual(-1, coordinator.ConfirmedFrame);
            Assert.AreEqual(-1, coordinator.PredictedFrame);
            Assert.IsFalse(coordinator.TryGetSnapshot(
                WorldTrack.Predicted,
                0,
                out _));
        }

        [Test]
        public void ConfirmedWorld_SameActualHistoryDifferentArrivalOrder_ConvergesByFieldsAndHash()
        {
            FrameSyncCoordinator first = CreateCoordinator();
            FrameSyncCoordinator second = CreateCoordinator();
            first.RecordActual(0, 0, new FrameInput(1, 0));
            first.RecordActual(0, 1, new FrameInput(3, 0));
            second.RecordActual(0, 1, new FrameInput(3, 0));
            second.RecordActual(0, 0, new FrameInput(1, 0));

            AdvanceSequential(first, 0);
            AdvanceSequential(second, 0);

            SimulationWorldState firstWorld = first.ConfirmedWorld;
            SimulationWorldState secondWorld = second.ConfirmedWorld;
            Assert.AreEqual(firstWorld.player0.position, secondWorld.player0.position);
            Assert.AreEqual(firstWorld.player1.position, secondWorld.player1.position);
            Assert.AreEqual(firstWorld.ball.position, secondWorld.ball.position);
            Assert.AreEqual(
                WorldHash.Compute(firstWorld, 0),
                WorldHash.Compute(secondWorld, 0));
        }

        [Test]
        public void PredictedWorlds_MayDivergeBeforeActualWhileConfirmedHeadsStayEqual()
        {
            FrameSyncCoordinator first = CreateCoordinator();
            FrameSyncCoordinator second = CreateCoordinator();
            first.RecordActual(0, 0, new FrameInput(1, 0));
            second.RecordActual(0, 1, new FrameInput(3, 0));

            AdvanceSequential(first, 0);
            AdvanceSequential(second, 0);

            Assert.AreEqual(-1, first.ConfirmedFrame);
            Assert.AreEqual(-1, second.ConfirmedFrame);
            Assert.AreNotEqual(
                WorldHash.Compute(first.PredictedWorld, 0),
                WorldHash.Compute(second.PredictedWorld, 0));
            Assert.AreEqual(
                WorldHash.Compute(first.ConfirmedWorld, -1),
                WorldHash.Compute(second.ConfirmedWorld, -1));
        }


        private static void AdvanceSequential(
            FrameSyncCoordinator coordinator,
            int frame)
        {
            FrameInputLedger.ResolvedFrame inputs =
                coordinator.ResolveForPrediction(frame);
            Assert.IsTrue(coordinator.Advance(frame, inputs).Succeeded);
        }

        private static void RecordBoth(
            FrameSyncCoordinator coordinator,
            int frame,
            FrameInput playerZero,
            FrameInput playerOne)
        {
            coordinator.RecordActual(frame, 0, playerZero);
            coordinator.RecordActual(frame, 1, playerOne);
        }

        private static FrameSyncCoordinator CreateCoordinator(
            int snapshotCapacity = 16)
        {
            RuntimeWorld runtime = CreateRuntimeWorld();
            var predicted = new DeterministicWorld(
                runtime.players,
                runtime.stateMachines,
                runtime.ball,
                MoveDistance,
                CourtConstant.LogicDeltaTime);
            SimulationWorldState initial = predicted.Capture(-1);
            return new FrameSyncCoordinator(
                predicted,
                initial,
                MoveDistance,
                CourtConstant.LogicDeltaTime,
                snapshotCapacity);
        }

        private static RuntimeWorld CreateRuntimeWorld()
        {
            var playerZero = new PlayerEntity();
            playerZero.Reset(
                new FixedVector3(FixedInt.FromInt(-3), FixedInt.Zero, FixedInt.Zero),
                0);
            var playerOne = new PlayerEntity();
            playerOne.Reset(
                new FixedVector3(FixedInt.FromInt(3), FixedInt.Zero, FixedInt.Zero),
                1);
            var players = new[] { playerZero, playerOne };
            var ball = new BallEntity();
            ball.Reset(FixedVector3.Zero);
            playerZero.hasBall = true;
            ball.state = BallEntity.EState.Held;
            ball.holderPlayerIndex = 0;
            Assert.IsTrue(BallPossessionSystem.TryUpdateHeldBall(playerZero, ball));
            return new RuntimeWorld(
                players,
                new[]
                {
                    new PlayerStateMachine(playerZero),
                    new PlayerStateMachine(playerOne)
                },
                ball);
        }

        private sealed class RuntimeWorld
        {
            public readonly PlayerEntity[] players;
            public readonly PlayerStateMachine[] stateMachines;
            public readonly BallEntity ball;

            public RuntimeWorld(
                PlayerEntity[] players,
                PlayerStateMachine[] stateMachines,
                BallEntity ball)
            {
                this.players = players;
                this.stateMachines = stateMachines;
                this.ball = ball;
            }
        }
    }
}

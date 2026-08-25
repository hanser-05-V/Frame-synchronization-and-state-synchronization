using System;
using System.Reflection;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class ViewWorldBuilderTests
    {
        [Test]
        public void Build_PredictedAhead_LocalUsesPredictedAndRemoteUsesConfirmed()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator();
            AdvanceDualActual(coordinator, 0, Move(3), Move(7));
            AdvanceSingleActual(coordinator, 1, 0, Move(3));

            Assert.IsTrue(ViewWorldBuilder.TryBuild(
                coordinator,
                0,
                out ViewWorldState view));

            Assert.AreEqual(ViewSampleSource.Predicted, view.Player0.Source);
            Assert.AreEqual(
                ViewSampleSource.ConfirmedSingleEndpoint,
                view.Player1.Source);
            Assert.AreEqual(1, view.Player0.ToCanonicalFrame);
            Assert.AreEqual(0, view.Player1.ToCanonicalFrame);
            Assert.AreEqual(-1, view.Player0.ToPosition.x.ToInt());
            Assert.AreEqual(2, view.Player1.ToPosition.x.ToInt());
        }

        [Test]
        public void Build_PlayerOneLocal_MirrorsSourceOwnershipWithoutChangingFrameDomain()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator();
            AdvanceDualActual(coordinator, 0, Move(3), Move(7));
            AdvanceSingleActual(coordinator, 1, 1, Move(7));

            Assert.IsTrue(ViewWorldBuilder.TryBuild(
                coordinator,
                1,
                out ViewWorldState view));

            Assert.AreEqual(
                ViewSampleSource.ConfirmedSingleEndpoint,
                view.Player0.Source);
            Assert.AreEqual(ViewSampleSource.Predicted, view.Player1.Source);
            Assert.AreEqual(0, view.Player0.ToCanonicalFrame);
            Assert.AreEqual(1, view.Player1.ToCanonicalFrame);
            Assert.AreEqual(-2, view.Player0.ToPosition.x.ToInt());
            Assert.AreEqual(1, view.Player1.ToPosition.x.ToInt());
        }

        [Test]
        public void Build_InitialConfirmedHistory_DuplicatesInitialEndpointExplicitly()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator();
            AdvanceSingleActual(coordinator, 0, 0, new FrameInput());

            Assert.IsTrue(ViewWorldBuilder.TryBuild(
                coordinator,
                0,
                out ViewWorldState view));

            Assert.AreEqual(-1, view.ConfirmedFrame);
            Assert.AreEqual(-1, view.PresentationFrame);
            Assert.AreEqual(
                ViewSampleSource.InitialConfirmed,
                view.Player1.Source);
            Assert.AreEqual(-1, view.Player1.FromCanonicalFrame);
            Assert.AreEqual(-1, view.Player1.ToCanonicalFrame);
            Assert.AreEqual(
                view.Player1.FromPosition,
                view.Player1.ToPosition);
        }

        [Test]
        public void Build_ConfirmedFrameZero_DuplicatesSingleEndpointWithoutPredictedSplice()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator();
            AdvanceDualActual(coordinator, 0, Move(3), Move(7));
            AdvanceSingleActual(coordinator, 1, 0, Move(3));

            Assert.IsTrue(ViewWorldBuilder.TryBuild(
                coordinator,
                0,
                out ViewWorldState view));

            Assert.AreEqual(
                ViewSampleSource.ConfirmedSingleEndpoint,
                view.Player1.Source);
            Assert.AreEqual(0, view.Player1.FromCanonicalFrame);
            Assert.AreEqual(0, view.Player1.ToCanonicalFrame);
            Assert.AreEqual(
                view.Player1.FromPosition,
                view.Player1.ToPosition);
        }

        [Test]
        public void Build_RemotePickupBeforeDualActual_DoesNotAttachEarly()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator(initialBallFree: true);
            coordinator.RecordActual(0, 1, Pickup());
            AdvanceResolved(coordinator, 0);

            Assert.IsTrue(ViewWorldBuilder.TryBuild(
                coordinator,
                0,
                out ViewWorldState view));

            Assert.AreEqual(-1, view.Ball.ToHolderPlayerIndex);
            Assert.AreNotEqual(BallEntity.EState.Held, view.Ball.ToState);
            Assert.AreEqual(
                ViewSampleSource.InitialConfirmed,
                view.Ball.Source);
        }

        [Test]
        public void Build_ConfirmedPreviousExpired_DuplicatesNewestConfirmedEndpoint()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator(
                initialBallFree: true,
                snapshotCapacity: 1);
            AdvanceDualActual(coordinator, 0, Move(1), Move(2));
            AdvanceDualActual(coordinator, 1, Move(1), Move(2));

            Assert.IsTrue(ViewWorldBuilder.TryBuild(
                coordinator,
                0,
                out ViewWorldState view));

            Assert.AreEqual(
                ViewSampleSource.ConfirmedSingleEndpoint,
                view.Player1.Source);
            Assert.AreEqual(1, view.Player1.FromCanonicalFrame);
            Assert.AreEqual(1, view.Player1.ToCanonicalFrame);
            Assert.AreEqual(
                view.Player1.FromPosition,
                view.Player1.ToPosition);
            Assert.AreEqual(
                ViewSampleSource.ConfirmedSingleEndpoint,
                view.Ball.Source);
        }

        [Test]
        public void ConfirmedPresentationCursor_ConfirmedHeadJumpsTwo_ReturnsConsecutiveFrames()
        {
            var cursor = new ConfirmedPresentationCursor(1);

            Assert.IsFalse(cursor.TryGetNext(0, out _));
            Assert.IsTrue(cursor.TryGetNext(2, out int firstFrame));
            Assert.AreEqual(0, firstFrame);
            cursor.MarkPresented(firstFrame);

            Assert.IsFalse(cursor.TryGetNext(2, out _));
            cursor.Advance(0.033f, 0.033f);

            Assert.IsTrue(cursor.TryGetNext(2, out int secondFrame));
            Assert.AreEqual(1, secondFrame);
            cursor.MarkPresented(secondFrame);

            Assert.AreEqual(1, cursor.LastPresentedFrame);
        }

        [Test]
        public void ConfirmedPresentationCursor_RenderCatchup_DoesNotSkipActiveInterval()
        {
            var cursor = new ConfirmedPresentationCursor(1);

            Assert.IsTrue(cursor.TryGetNext(3, out int firstFrame));
            cursor.MarkPresented(firstFrame);
            cursor.Advance(0.016f, 0.033f);

            Assert.AreEqual(0, cursor.LastPresentedFrame);
            Assert.AreEqual(0.016f / 0.033f, cursor.InterpolationAlpha, 0.0001f);
            Assert.IsFalse(cursor.TryGetNext(3, out _));

            cursor.Advance(0.017f, 0.033f);
            Assert.IsTrue(cursor.TryGetNext(3, out int secondFrame));
            Assert.AreEqual(1, secondFrame);
        }

        [Test]
        public void Build_ExplicitPresentationFrameBehindConfirmedHead_UsesRequestedInterval()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator(
                initialBallFree: true);
            AdvanceDualActual(coordinator, 0, Move(1), Move(2));
            AdvanceDualActual(coordinator, 1, Move(1), Move(2));
            AdvanceDualActual(coordinator, 2, Move(1), Move(2));

            Assert.IsTrue(ViewWorldBuilder.TryBuild(
                coordinator,
                0,
                1,
                out ViewWorldState view));

            Assert.AreEqual(2, view.ConfirmedFrame);
            Assert.AreEqual(1, view.PresentationFrame);
            Assert.AreEqual(0, view.Player1.FromCanonicalFrame);
            Assert.AreEqual(1, view.Player1.ToCanonicalFrame);
            Assert.AreEqual(ViewSampleSource.Confirmed, view.Player1.Source);
        }

        [Test]
        public void Build_RequestedConfirmedInterval_UsesSameFramesForRemoteAndConfirmedBall()
        {
            FrameSyncCoordinator coordinator =
                CreateRemoteHeldCoordinatorThrough(4);

            Assert.IsTrue(ViewWorldBuilder.TryBuild(
                coordinator,
                0,
                3,
                out ViewWorldState view));

            Assert.AreEqual(2, view.Player1.FromCanonicalFrame);
            Assert.AreEqual(3, view.Player1.ToCanonicalFrame);
            Assert.AreEqual(2, view.Ball.FromCanonicalFrame);
            Assert.AreEqual(3, view.Ball.ToCanonicalFrame);
            Assert.AreEqual(ViewSampleSource.Confirmed, view.Player1.Source);
            Assert.AreEqual(ViewSampleSource.Confirmed, view.Ball.Source);
            Assert.AreEqual(1, view.Ball.ToHolderPlayerIndex);
        }

        [Test]
        public void Build_ExplicitPresentationFrameWithExpiredPrevious_ReturnsFalse()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator(
                initialBallFree: true,
                snapshotCapacity: 1);
            AdvanceDualActual(coordinator, 0, Move(1), Move(2));
            AdvanceDualActual(coordinator, 1, Move(1), Move(2));

            Assert.IsFalse(ViewWorldBuilder.TryBuild(
                coordinator,
                0,
                1,
                out _));
        }

        [Test]
        public void Build_PresentationFrameZero_InterpolatesFromInitialConfirmedWorld()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator(
                initialBallFree: true);
            AdvanceDualActual(coordinator, 0, Move(1), Move(2));

            Assert.IsTrue(ViewWorldBuilder.TryBuild(
                coordinator,
                0,
                0,
                out ViewWorldState view));

            Assert.AreEqual(-1, view.Player1.FromCanonicalFrame);
            Assert.AreEqual(0, view.Player1.ToCanonicalFrame);
            Assert.AreEqual(ViewSampleSource.Confirmed, view.Player1.Source);
        }

        [Test]
        public void Build_RemotePickupAfterDualActual_AttachesFromConfirmed()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator(initialBallFree: true);
            AdvanceDualActual(coordinator, 0, new FrameInput(), Pickup());

            Assert.IsTrue(ViewWorldBuilder.TryBuild(
                coordinator,
                0,
                out ViewWorldState view));

            Assert.AreEqual(BallEntity.EState.Held, view.Ball.ToState);
            Assert.AreEqual(1, view.Ball.ToHolderPlayerIndex);
            Assert.AreEqual(
                ViewSampleSource.ConfirmedSingleEndpoint,
                view.Ball.Source);
        }

        [Test]
        public void Build_LocalPickupBeforeConfirmation_AttachesFromPredicted()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator(initialBallFree: true);
            AdvanceSingleActual(coordinator, 0, 0, Pickup());

            Assert.IsTrue(ViewWorldBuilder.TryBuild(
                coordinator,
                0,
                out ViewWorldState view));

            Assert.AreEqual(BallEntity.EState.Held, view.Ball.ToState);
            Assert.AreEqual(0, view.Ball.ToHolderPlayerIndex);
            Assert.AreEqual(
                ViewSampleSource.PredictedSingleEndpoint,
                view.Ball.Source);
        }

        [Test]
        public void Build_LocalShotBeforeConfirmation_DetachesImmediatelyFromPredicted()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator();
            AdvanceSingleActual(coordinator, 0, 0, Shoot());

            Assert.IsTrue(ViewWorldBuilder.TryBuild(
                coordinator,
                0,
                out ViewWorldState view));

            Assert.AreNotEqual(BallEntity.EState.Held, view.Ball.ToState);
            Assert.AreEqual(-1, view.Ball.ToHolderPlayerIndex);
            Assert.AreEqual(
                ViewSampleSource.PredictedSingleEndpoint,
                view.Ball.Source);
        }

        [Test]
        public void Build_LocalShotAfterUnconfirmedPickup_DetachesFromPredictedHistory()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator(initialBallFree: true);
            AdvanceSingleActual(coordinator, 0, 0, Pickup());
            AdvanceSingleActual(coordinator, 1, 0, Shoot());

            Assert.IsTrue(ViewWorldBuilder.TryBuild(
                coordinator,
                0,
                out ViewWorldState view));

            Assert.AreEqual(BallEntity.EState.Held, view.Ball.FromState);
            Assert.AreNotEqual(BallEntity.EState.Held, view.Ball.ToState);
            Assert.AreEqual(-1, view.Ball.ToHolderPlayerIndex);
            Assert.AreEqual(ViewSampleSource.Predicted, view.Ball.Source);
        }

        [Test]
        public void Build_RemoteShot_WaitsForConfirmationBeforeDetaching()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator();
            AdvanceSingleActual(coordinator, 0, 0, Shoot());

            Assert.IsTrue(ViewWorldBuilder.TryBuild(
                coordinator,
                1,
                out ViewWorldState beforeConfirmation));
            Assert.AreEqual(
                BallEntity.EState.Held,
                beforeConfirmation.Ball.ToState);
            Assert.AreEqual(0, beforeConfirmation.Ball.ToHolderPlayerIndex);
            Assert.AreEqual(
                ViewSampleSource.InitialConfirmed,
                beforeConfirmation.Ball.Source);

            coordinator.RecordActual(0, 1, new FrameInput());
            Assert.IsTrue(coordinator.Reconcile().Succeeded);
            Assert.IsTrue(ViewWorldBuilder.TryBuild(
                coordinator,
                1,
                out ViewWorldState afterConfirmation));
            Assert.AreEqual(
                BallEntity.EState.Airborne,
                afterConfirmation.Ball.ToState);
            Assert.AreEqual(-1, afterConfirmation.Ball.ToHolderPlayerIndex);
            Assert.AreEqual(
                ViewSampleSource.ConfirmedSingleEndpoint,
                afterConfirmation.Ball.Source);
        }

        [Test]
        public void ViewBallState_InvalidHeldOwner_IsNotPublishedAsAttachment()
        {
            SimulationBallState invalid = default;
            invalid.state = (int)BallEntity.EState.Held;
            invalid.holderPlayerIndex = 5;
            var viewBall = new ViewBallState(
                invalid,
                invalid,
                0,
                0,
                ViewSampleSource.ConfirmedSingleEndpoint);

            Assert.IsFalse(viewBall.IsAttached);
        }

        [Test]
        public void Build_DoesNotMutateEitherLogicWorldOrHash()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator();
            AdvanceDualActual(coordinator, 0, Move(3), Move(7));
            AdvanceSingleActual(coordinator, 1, 0, Move(3));
            SimulationWorldState confirmedBefore = coordinator.ConfirmedWorld;
            SimulationWorldState predictedBefore = coordinator.PredictedWorld;
            ulong confirmedHash = WorldHash.Compute(confirmedBefore, 0);
            ulong predictedHash = WorldHash.Compute(predictedBefore, 1);

            Assert.IsTrue(ViewWorldBuilder.TryBuild(
                coordinator,
                0,
                out _));

            Assert.AreEqual(
                confirmedHash,
                WorldHash.Compute(coordinator.ConfirmedWorld, 0));
            Assert.AreEqual(
                predictedHash,
                WorldHash.Compute(coordinator.PredictedWorld, 1));
            Assert.AreEqual(
                confirmedBefore.player1.position,
                coordinator.ConfirmedWorld.player1.position);
            Assert.AreEqual(
                predictedBefore.player0.position,
                coordinator.PredictedWorld.player0.position);
        }

        [Test]
        public void Build_AfterPredictedRollback_RemoteStillReadsConfirmedTimeline()
        {
            FrameSyncCoordinator coordinator = CreateCoordinator();
            AdvanceDualActual(coordinator, 0, Move(3), Move(7));
            AdvanceSingleActual(coordinator, 1, 0, Move(3));
            SimulationWorldState predictedBefore = coordinator.PredictedWorld;
            coordinator.RecordActual(1, 1, Move(3));

            ReconcileResult reconcile = coordinator.Reconcile();

            Assert.IsTrue(reconcile.Succeeded);
            Assert.IsTrue(reconcile.Replayed);
            Assert.AreNotEqual(
                predictedBefore.player1.position.x,
                coordinator.PredictedWorld.player1.position.x);
            Assert.IsTrue(ViewWorldBuilder.TryBuild(
                coordinator,
                0,
                out ViewWorldState view));
            Assert.AreEqual(ViewSampleSource.Confirmed, view.Player1.Source);
            Assert.AreEqual(0, view.Player1.FromCanonicalFrame);
            Assert.AreEqual(1, view.Player1.ToCanonicalFrame);
            Assert.AreEqual(
                coordinator.ConfirmedWorld.player1.position,
                view.Player1.ToPosition);
        }

        [Test]
        public void ViewValueTypes_ContainNoMutableRuntimeOrCollectionReferences()
        {
            Type[] viewTypes =
            {
                typeof(ViewPlayerState),
                typeof(ViewBallState),
                typeof(ViewWorldState)
            };

            foreach (Type type in viewTypes)
            {
                Assert.IsTrue(type.IsValueType, type.FullName);
                foreach (FieldInfo field in type.GetFields(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic))
                {
                    Type fieldType = field.FieldType;
                    Assert.IsFalse(fieldType.IsArray, field.Name);
                    Assert.IsFalse(
                        typeof(System.Collections.IEnumerable)
                            .IsAssignableFrom(fieldType),
                        field.Name);
                    Assert.AreNotEqual(typeof(PlayerEntity), fieldType);
                    Assert.AreNotEqual(typeof(BallEntity), fieldType);
                    Assert.AreNotEqual(typeof(PlayerStateMachine), fieldType);
                    Assert.AreNotEqual(typeof(FrameSyncCoordinator), fieldType);
                    Assert.AreNotEqual(typeof(FrameInputLedger), fieldType);
                    Assert.AreNotEqual(typeof(WorldSnapshotStore), fieldType);
                }
            }
        }

        private static FrameSyncCoordinator CreateCoordinator(
            bool initialBallFree = false,
            int snapshotCapacity = 16)
        {
            var player0 = new PlayerEntity();
            player0.Reset(new FixedVector3(
                FixedInt.FromInt(initialBallFree ? -1 : -3),
                FixedInt.Zero,
                FixedInt.Zero), 0);
            var player1 = new PlayerEntity();
            player1.Reset(new FixedVector3(
                FixedInt.FromInt(initialBallFree ? 1 : 3),
                FixedInt.Zero,
                FixedInt.Zero), 1);
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

        private static FrameSyncCoordinator CreateRemoteHeldCoordinatorThrough(
            int lastFrame)
        {
            FrameSyncCoordinator coordinator = CreateCoordinator(
                initialBallFree: true);
            AdvanceDualActual(
                coordinator,
                0,
                new FrameInput(),
                Pickup());
            for (int frame = 1; frame <= lastFrame; frame++)
            {
                AdvanceDualActual(
                    coordinator,
                    frame,
                    new FrameInput(),
                    Move(1));
            }

            return coordinator;
        }

        private static void AdvanceDualActual(
            FrameSyncCoordinator coordinator,
            int frame,
            FrameInput player0,
            FrameInput player1)
        {
            coordinator.RecordActual(frame, 0, player0);
            coordinator.RecordActual(frame, 1, player1);
            AdvanceResolved(coordinator, frame);
        }

        private static void AdvanceSingleActual(
            FrameSyncCoordinator coordinator,
            int frame,
            int playerIndex,
            FrameInput input)
        {
            coordinator.RecordActual(frame, playerIndex, input);
            AdvanceResolved(coordinator, frame);
        }

        private static void AdvanceResolved(
            FrameSyncCoordinator coordinator,
            int frame)
        {
            FrameInputLedger.ResolvedFrame inputs =
                coordinator.ResolveForPrediction(frame);
            Assert.IsTrue(coordinator.Advance(frame, inputs).Succeeded);
        }

        private static FrameInput Move(byte direction)
        {
            return new FrameInput(direction, 0);
        }

        private static FrameInput Pickup()
        {
            return new FrameInput(0, 0x20);
        }

        private static FrameInput Shoot()
        {
            return new FrameInput(0, 0x01);
        }
    }
}

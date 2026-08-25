using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class FrameInputLedgerReplayTests
    {
        [Test]
        public void TryBuildReplayPlan_RecomputesPredictedSuffixFromPrecedingActualWithoutMutatingLiveSlots()
        {
            var ledger = new FrameInputLedger();
            ledger.RecordActual(0, 0, new FrameInput(0x61F0u));
            ledger.ResolveForSimulation(1);
            ledger.ResolveForSimulation(2);
            ledger.RecordActual(1, 0, new FrameInput(0x3500u));
            ledger.TryGetRecord(2, 0, out FrameInputLedger.InputRecord liveBefore);

            Assert.AreEqual(FrameInputLedger.ReplayPlanResult.Success,
                ledger.TryBuildReplayPlan(1, 2, out FrameInputLedger.ReplayInputPlan plan));
            Assert.AreEqual(FrameInputLedger.InputState.Actual, plan.GetFrame(0).GetPlayer(0).State);
            Assert.AreEqual(0x3500u, plan.GetFrame(0).GetPlayer(0).Value._raw);
            Assert.AreEqual(FrameInputLedger.InputState.Predicted, plan.GetFrame(1).GetPlayer(0).State);
            Assert.AreEqual(0x3500u, plan.GetFrame(1).GetPlayer(0).Value._raw);
            Assert.IsTrue(ledger.TryGetRecord(2, 0, out FrameInputLedger.InputRecord liveAfter));
            Assert.AreEqual(liveBefore.Value._raw, liveAfter.Value._raw);
            Assert.AreEqual(liveBefore.State, liveAfter.State);
        }

        [Test]
        public void CommitReplay_ReplacesOnlyPlanPredictionsAndClearsOnlyCapturedGenerationMismatches()
        {
            var ledger = new FrameInputLedger();
            ledger.ResolveForSimulation(1);
            ledger.ResolveForSimulation(2);
            ledger.RecordActual(1, 0, new FrameInput(0x100u));
            Assert.IsTrue(ledger.TryGetEarliestMismatch(out FrameInputLedger.InputMismatch originalMismatch));

            Assert.AreEqual(FrameInputLedger.ReplayPlanResult.Success,
                ledger.TryBuildReplayPlan(1, 2, out FrameInputLedger.ReplayInputPlan plan));
            ledger.ResolveForSimulation(0);
            ledger.RecordActual(0, 1, new FrameInput(0x200u));
            Assert.IsTrue(ledger.TryGetEarliestMismatch(out FrameInputLedger.InputMismatch newerMismatch));
            Assert.AreEqual(0, newerMismatch.Frame);

            ledger.CommitReplay(in plan);

            Assert.IsTrue(ledger.TryGetEarliestMismatch(out FrameInputLedger.InputMismatch retainedMismatch));
            Assert.AreEqual(0, retainedMismatch.Frame);
            Assert.AreEqual(originalMismatch.Generation, plan.Generation);
        }

        [Test]
        public void CommitReplay_StalePlanDoesNotChangeSlotsOrMismatches()
        {
            var ledger = new FrameInputLedger();
            ledger.ResolveForSimulation(1);
            ledger.RecordActual(1, 0, new FrameInput(0x100u));
            ledger.TryBuildReplayPlan(1, 1, out FrameInputLedger.ReplayInputPlan stalePlan);
            ledger.TryBuildReplayPlan(1, 1, out _);
            ledger.TryGetRecord(1, 0, out FrameInputLedger.InputRecord before);

            ledger.CommitReplay(in stalePlan);

            Assert.IsTrue(ledger.TryGetRecord(1, 0, out FrameInputLedger.InputRecord after));
            Assert.AreEqual(before.Value._raw, after.Value._raw);
            Assert.AreEqual(before.State, after.State);
            Assert.IsTrue(ledger.TryGetEarliestMismatch(out _));
        }

        [Test]
        public void CommitReplay_PlanCapturesOnlyMismatchesInsideItsFrameRange()
        {
            var ledger = new FrameInputLedger();
            ledger.ResolveForSimulation(1);
            ledger.ResolveForSimulation(5);
            ledger.RecordActual(1, 0, new FrameInput(0x101u));
            ledger.RecordActual(5, 0, new FrameInput(0x505u));
            Assert.IsTrue(ledger.TryGetEarliestMismatch(out FrameInputLedger.InputMismatch before));
            Assert.AreEqual(1, before.Frame);

            Assert.AreEqual(FrameInputLedger.ReplayPlanResult.Success,
                ledger.TryBuildReplayPlan(5, 5, out FrameInputLedger.ReplayInputPlan plan));
            ledger.CommitReplay(in plan);

            Assert.IsTrue(ledger.TryGetEarliestMismatch(out FrameInputLedger.InputMismatch after));
            Assert.AreEqual(1, after.Frame);

            Assert.AreEqual(FrameInputLedger.ReplayPlanResult.Success,
                ledger.TryBuildReplayPlan(1, 1, out FrameInputLedger.ReplayInputPlan remainingPlan));
            ledger.CommitReplay(in remainingPlan);
            Assert.IsFalse(ledger.TryGetEarliestMismatch(out _));
        }

        [Test]
        public void EarliestMismatch_FailedReplayAttemptLeavesItPendingUntilCommitExposesNext()
        {
            var ledger = new FrameInputLedger();
            ledger.ResolveForSimulation(1);
            ledger.ResolveForSimulation(2);
            ledger.RecordActual(1, 0, new FrameInput(0x101u));
            ledger.RecordActual(2, 0, new FrameInput(0x202u));

            Assert.IsTrue(ledger.TryGetEarliestMismatch(
                out FrameInputLedger.InputMismatch firstAttempt));
            Assert.AreEqual(1, firstAttempt.Frame);
            Assert.IsTrue(ledger.TryGetEarliestMismatch(
                out FrameInputLedger.InputMismatch retryAttempt));
            Assert.AreEqual(1, retryAttempt.Frame);

            Assert.AreEqual(FrameInputLedger.ReplayPlanResult.Success,
                ledger.TryBuildReplayPlan(1, 1, out FrameInputLedger.ReplayInputPlan plan));
            ledger.CommitReplay(in plan);

            Assert.IsTrue(ledger.TryGetEarliestMismatch(
                out FrameInputLedger.InputMismatch next));
            Assert.AreEqual(2, next.Frame);
        }

        [Test]
        public void CommitReplay_PreflightFailureIsAtomicAndReleasesCurrentPlan()
        {
            var ledger = new FrameInputLedger();
            RecordBoth(ledger, 0);
            ledger.ResolveForSimulation(1);
            Assert.AreEqual(FrameInputLedger.ReplayPlanResult.Success,
                ledger.TryBuildReplayPlan(1, 1, out FrameInputLedger.ReplayInputPlan plan));
            FrameInputLedger.ReplayPlanFrame planned = plan.GetFrame(0);
            ledger.RecordActual(1, 0, planned.GetPlayer(0).Value);
            ledger.RecordActual(1, 1, planned.GetPlayer(1).Value);
            ledger.TryGetRecord(1, 0, out FrameInputLedger.InputRecord before);

            ledger.CommitReplay(in plan);

            Assert.IsTrue(ledger.TryGetRecord(1, 0, out FrameInputLedger.InputRecord after));
            Assert.AreEqual(before.Value._raw, after.Value._raw);
            Assert.AreEqual(FrameInputLedger.InputState.Actual, after.State);
            Assert.IsFalse(ledger.TryGetEarliestMismatch(out _));
            Assert.AreEqual(FrameInputLedger.PruneResult.Success, ledger.TryPruneBefore(1));
        }

        [Test]
        public void TryBuildReplayPlan_SingleFrameAtIntMaxValue_DoesNotWrapFrameIteration()
        {
            var ledger = CreateLedgerAtFirstRetainedFrame(int.MaxValue);
            ledger.RecordActual(int.MaxValue, 0, new FrameInput(0x100u));
            ledger.RecordActual(int.MaxValue, 1, new FrameInput(0x200u));

            Assert.AreEqual(FrameInputLedger.ReplayPlanResult.Success,
                ledger.TryBuildReplayPlan(int.MaxValue, int.MaxValue, out FrameInputLedger.ReplayInputPlan plan));
            Assert.AreEqual(1, plan.FrameCount);
            Assert.AreEqual(int.MaxValue, plan.GetFrame(0).CanonicalFrame);
        }

        [Test]
        public void TryBuildReplayPlan_HistoryAndCapacityFailuresDoNotMutateLedger()
        {
            var ledger = new FrameInputLedger();
            RecordBoth(ledger, 0);
            Assert.AreEqual(FrameInputLedger.PruneResult.Success, ledger.TryPruneBefore(1));

            Assert.AreEqual(FrameInputLedger.ReplayPlanResult.HistoryUnavailable,
                ledger.TryBuildReplayPlan(0, 0, out _));
            Assert.AreEqual(1, ledger.FirstRetainedFrame);
            Assert.AreEqual(FrameInputLedger.ReplayPlanResult.CapacityExceeded,
                ledger.TryBuildReplayPlan(1, 257, out _));
        }

        [Test]
        public void TryBuildReplayPlan_MissingPlayerOrFrameLeavesLiveSlotsAndMismatchesUntouched()
        {
            var ledger = new FrameInputLedger();
            ledger.ResolveForSimulation(4);
            ledger.TryGetRecord(4, 0, out FrameInputLedger.InputRecord before);

            Assert.AreEqual(FrameInputLedger.ReplayPlanResult.MissingInput,
                ledger.TryBuildReplayPlan(4, 5, out _));
            Assert.IsTrue(ledger.TryGetRecord(4, 0, out FrameInputLedger.InputRecord after));
            Assert.AreEqual(before.Value._raw, after.Value._raw);
            Assert.AreEqual(before.State, after.State);
            Assert.IsFalse(ledger.TryGetEarliestMismatch(out _));
        }

        [Test]
        public void TryBuildReplayPlan_MissingOnePlayerInExistingFrameLeavesLiveSlotsUntouched()
        {
            var ledger = new FrameInputLedger();
            ledger.RecordActual(6, 0, new FrameInput(0x100u));
            ledger.TryGetRecord(6, 0, out FrameInputLedger.InputRecord before);

            Assert.AreEqual(FrameInputLedger.ReplayPlanResult.MissingInput,
                ledger.TryBuildReplayPlan(6, 6, out _));
            Assert.IsTrue(ledger.TryGetRecord(6, 0, out FrameInputLedger.InputRecord after));
            Assert.AreEqual(before.Value._raw, after.Value._raw);
            Assert.AreEqual(before.State, after.State);
        }

        [Test]
        public void ReplayInputPlan_IsExternallyImmutableAndUncommittedPlanDoesNotAlterLiveSlots()
        {
            var ledger = new FrameInputLedger();
            ledger.ResolveForSimulation(0);
            ledger.ResolveForSimulation(1);
            ledger.TryGetRecord(1, 0, out FrameInputLedger.InputRecord liveBefore);

            Assert.AreEqual(FrameInputLedger.ReplayPlanResult.Success,
                ledger.TryBuildReplayPlan(0, 1, out FrameInputLedger.ReplayInputPlan plan));
            FrameInputLedger.ReplayPlanFrame observed = plan.GetFrame(1);
            Assert.AreEqual(FrameInputLedger.InputState.Predicted, observed.GetPlayer(0).State);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => plan.GetFrame(2));
            Assert.IsTrue(ledger.TryGetRecord(1, 0, out FrameInputLedger.InputRecord liveAfter));
            Assert.AreEqual(liveBefore.Value._raw, liveAfter.Value._raw);
            Assert.AreEqual(liveBefore.State, liveAfter.State);
        }

        [Test]
        public void TryPruneBefore_PreservesSeedsAndRejectsUnsafeFloors()
        {
            var ledger = new FrameInputLedger();
            ledger.RecordActual(0, 0, new FrameInput(0x61F0u));
            ledger.RecordActual(0, 1, new FrameInput(0x0200u));
            ledger.RecordActual(1, 0, new FrameInput(0x0100u));
            ledger.RecordActual(1, 1, new FrameInput(0x0200u));

            Assert.AreEqual(FrameInputLedger.PruneResult.Success, ledger.TryPruneBefore(1));
            Assert.AreEqual(1, ledger.FirstRetainedFrame);
            FrameInput predicted = ledger.ResolveForSimulation(2).GetPlayer(0).Value;
            Assert.AreEqual(0x0100u, predicted._raw);

            ledger.ResolveForSimulation(3);
            ledger.RecordActual(3, 0, new FrameInput(0x0101u));
            Assert.AreEqual(FrameInputLedger.PruneResult.PendingMismatch,
                ledger.TryPruneBefore(2));
        }

        [Test]
        public void TryPruneBefore_RejectsInFlightIntegrityAndUnconfirmedFrames()
        {
            var inFlightLedger = new FrameInputLedger();
            RecordBoth(inFlightLedger, 0);
            inFlightLedger.ResolveForSimulation(1);
            Assert.AreEqual(FrameInputLedger.ReplayPlanResult.Success,
                inFlightLedger.TryBuildReplayPlan(1, 1, out _));
            Assert.AreEqual(FrameInputLedger.PruneResult.InFlightPlan, inFlightLedger.TryPruneBefore(1));

            var unconfirmedLedger = new FrameInputLedger();
            unconfirmedLedger.RecordActual(0, 0, new FrameInput(0x100u));
            Assert.AreEqual(FrameInputLedger.PruneResult.UnconfirmedFrame, unconfirmedLedger.TryPruneBefore(1));

            var faultyLedger = new FrameInputLedger();
            RecordBoth(faultyLedger, 0);
            faultyLedger.RecordActual(0, 0, new FrameInput(0x999u));
            Assert.AreEqual(FrameInputLedger.PruneResult.IntegrityFault, faultyLedger.TryPruneBefore(1));
        }

        [Test]
        public void HistoryWindow_DoesNotSilentlyOverwritePendingFrames()
        {
            var ledger = new FrameInputLedger();

            Assert.AreEqual(FrameInputLedger.ActualDisposition.Accepted,
                ledger.RecordActual(0, 0, new FrameInput(0x100u)).Disposition);
            Assert.AreEqual(FrameInputLedger.ActualDisposition.CapacityExceeded,
                ledger.RecordActual(256, 0, new FrameInput(0x100u)).Disposition);
            Assert.IsTrue(ledger.TryGetRecord(0, 0, out _));
            Assert.IsFalse(ledger.TryGetRecord(256, 0, out _));
        }

        [Test]
        public void HistoryWindow_AfterSafePruneAllowsFirstFrameBeyondCapacityAndMakesOldFramesUnavailable()
        {
            var ledger = new FrameInputLedger();
            for (int frame = 0; frame < 256; frame++)
            {
                RecordBoth(ledger, frame);
            }

            Assert.AreEqual(FrameInputLedger.PruneResult.Success, ledger.TryPruneBefore(1));
            Assert.AreEqual(1, ledger.FirstRetainedFrame);
            Assert.AreEqual(FrameInputLedger.ActualDisposition.Accepted,
                ledger.RecordActual(256, 0, new FrameInput(0x100u)).Disposition);
            Assert.AreEqual(FrameInputLedger.ActualDisposition.HistoryUnavailable,
                ledger.RecordActual(0, 0, new FrameInput(0x100u)).Disposition);
            Assert.Throws<System.InvalidOperationException>(() => ledger.ResolveForSimulation(0));
            Assert.IsFalse(ledger.TryGetRecord(0, 0, out _));
        }

        [Test]
        public void HistoryWindow_RepeatedConfirmedPruneSupportsMoreThanCapacityWithoutOverwriting()
        {
            var ledger = new FrameInputLedger();
            for (int frame = 0; frame <= 512; frame++)
            {
                RecordBoth(ledger, frame);
                if (frame >= 255)
                {
                    int firstFrameToKeep = frame - 254;
                    Assert.That(
                        ledger.TryPruneBefore(firstFrameToKeep),
                        Is.EqualTo(FrameInputLedger.PruneResult.Success)
                            .Or.EqualTo(FrameInputLedger.PruneResult.NoOp));
                }
            }

            Assert.AreEqual(258, ledger.FirstRetainedFrame);
            Assert.AreEqual(512, ledger.ConfirmedThroughFrame);
            Assert.IsFalse(ledger.TryGetRecord(257, 0, out _));
            Assert.IsTrue(ledger.TryGetRecord(512, 0, out FrameInputLedger.InputRecord latest));
            Assert.AreEqual(FrameInputLedger.InputState.Actual, latest.State);
        }

        [Test]
        public void TryPruneBefore_SeedIgnoresBoundaryPredictedValueAndUsesLatestEarlierActual()
        {
            var ledger = new FrameInputLedger();
            ledger.RecordActual(0, 0, new FrameInput(0x61F0u));
            ledger.RecordActual(0, 1, default);
            ledger.RecordActual(1, 0, new FrameInput(0x1200u));
            ledger.RecordActual(1, 1, default);
            Assert.AreEqual(FrameInputLedger.PruneResult.Success, ledger.TryPruneBefore(2));

            Assert.AreEqual(0x1200u, ledger.ResolveForSimulation(2).GetPlayer(0).Value._raw);
        }

        private static void RecordBoth(FrameInputLedger ledger, int frame)
        {
            ledger.RecordActual(frame, 0, new FrameInput(0x100u));
            ledger.RecordActual(frame, 1, new FrameInput(0x200u));
        }

        private static FrameInputLedger CreateLedgerAtFirstRetainedFrame(int frame)
        {
            var ledger = new FrameInputLedger();
            typeof(FrameInputLedger).GetField("_firstRetainedFrame",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(ledger, frame);
            return ledger;
        }
    }
}

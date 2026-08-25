using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class FrameInputLedgerTests
    {
        [Test]
        public void RecordActual_RequiresBothPlayersBeforeConfirmingFrameZero()
        {
            var ledger = new FrameInputLedger();

            Assert.AreEqual(-1, ledger.ConfirmedThroughFrame);
            ledger.RecordActual(0, 0, new FrameInput(0x100u));
            Assert.AreEqual(-1, ledger.ConfirmedThroughFrame);

            ledger.RecordActual(0, 1, new FrameInput(0x200u));

            Assert.AreEqual(0, ledger.ConfirmedThroughFrame);
        }

        [Test]
        public void RecordActual_FillingEarlierGaps_AdvancesConfirmationThroughContiguousPrefix()
        {
            var ledger = new FrameInputLedger();

            RecordBoth(ledger, 2);
            Assert.AreEqual(-1, ledger.ConfirmedThroughFrame);

            RecordBoth(ledger, 0);
            Assert.AreEqual(0, ledger.ConfirmedThroughFrame);

            RecordBoth(ledger, 1);

            Assert.AreEqual(2, ledger.ConfirmedThroughFrame);
        }

        [Test]
        public void ResolveForSimulation_MissingAfterActual_PredictsSustainedInputAndClearsTransientBits()
        {
            var ledger = new FrameInputLedger();
            var actual = FrameInput.FromRaw(0xABCD0000u);
            actual.moveDir = 3;
            actual.buttons = 0x7F;
            ledger.RecordActual(0, 0, actual);

            FrameInput coldStart = new FrameInputLedger().ResolveForSimulation(0).GetPlayer(0).Value;
            FrameInput predicted = ledger.ResolveForSimulation(1).GetPlayer(0).Value;

            Assert.AreEqual(0u, coldStart._raw);
            Assert.AreEqual(FrameInputLedger.InputState.Predicted, ledger.ResolveForSimulation(1).GetPlayer(0).State);
            Assert.AreEqual(actual._raw & ~0x61u, predicted._raw);
        }

        [Test]
        public void RecordActual_AfterPrediction_ReportsMatchOrMismatchAndPreservesEarliestOrdering()
        {
            var ledger = new FrameInputLedger();
            ledger.ResolveForSimulation(2);
            ledger.ResolveForSimulation(1);

            Assert.AreEqual(FrameInputLedger.ActualDisposition.PredictionMismatched,
                ledger.RecordActual(2, 1, new FrameInput(0x300u)).Disposition);
            Assert.AreEqual(FrameInputLedger.ActualDisposition.PredictionMismatched,
                ledger.RecordActual(1, 0, new FrameInput(0x100u)).Disposition);

            Assert.IsTrue(ledger.TryGetEarliestMismatch(out FrameInputLedger.InputMismatch mismatch));
            Assert.AreEqual(1, mismatch.Frame);
            Assert.AreEqual(0, mismatch.PlayerIndex);
            Assert.AreEqual(0u, mismatch.PredictedRaw);
            Assert.AreEqual(0x100u, mismatch.ActualRaw);

            var matchLedger = new FrameInputLedger();
            matchLedger.ResolveForSimulation(0);
            Assert.AreEqual(FrameInputLedger.ActualDisposition.PredictionMatched,
                matchLedger.RecordActual(0, 0, default).Disposition);
        }

        [Test]
        public void ResolveForSimulation_FrameBeyondRetainedCapacity_ThrowsWithoutCreatingSlotOrAdvancingObservation()
        {
            var ledger = new FrameInputLedger();
            const int beyondCapacityFrame = 256;

            Assert.Throws<System.InvalidOperationException>(() => ledger.ResolveForSimulation(beyondCapacityFrame));
            Assert.IsFalse(ledger.TryGetRecord(beyondCapacityFrame, 0, out _));
            Assert.AreEqual(-1, ledger.LastObservedFrame);
        }

        [Test]
        public void TryGetEarliestMismatch_SameFramePlayerOneArrivesFirst_ReturnsPlayerZero()
        {
            var ledger = new FrameInputLedger();
            ledger.ResolveForSimulation(3);

            ledger.RecordActual(3, 1, new FrameInput(0x200u));
            ledger.RecordActual(3, 0, new FrameInput(0x100u));

            Assert.IsTrue(ledger.TryGetEarliestMismatch(out FrameInputLedger.InputMismatch mismatch));
            Assert.AreEqual(3, mismatch.Frame);
            Assert.AreEqual(0, mismatch.PlayerIndex);
        }

        [Test]
        public void RecordActual_ConflictingDuplicate_LatchesFaultAndKeepsFirstValue()
        {
            var ledger = new FrameInputLedger();
            RecordBoth(ledger, 0);
            Assert.AreEqual(0, ledger.ConfirmedThroughFrame);

            Assert.AreEqual(FrameInputLedger.ActualDisposition.IdempotentDuplicate,
                ledger.RecordActual(0, 0, new FrameInput(0x100u)).Disposition);
            Assert.AreEqual(FrameInputLedger.ActualDisposition.ConflictingDuplicate,
                ledger.RecordActual(0, 0, new FrameInput(0x999u)).Disposition);
            Assert.IsTrue(ledger.HasIntegrityFault);
            Assert.IsTrue(ledger.TryGetRecord(0, 0, out FrameInputLedger.InputRecord record));
            Assert.AreEqual(0x100u, record.Value._raw);

            RecordBoth(ledger, 1);
            Assert.AreEqual(0, ledger.ConfirmedThroughFrame);
        }

        [Test]
        public void RecordActual_SameValueDuplicate_IsIdempotentAndDoesNotCreateMismatch()
        {
            var ledger = new FrameInputLedger();
            FrameInput value = new FrameInput(0x123u);

            Assert.AreEqual(FrameInputLedger.ActualDisposition.Accepted,
                ledger.RecordActual(0, 0, value).Disposition);
            Assert.AreEqual(FrameInputLedger.ActualDisposition.IdempotentDuplicate,
                ledger.RecordActual(0, 0, value).Disposition);

            Assert.IsFalse(ledger.HasIntegrityFault);
            Assert.IsFalse(ledger.TryGetEarliestMismatch(out _));
            Assert.IsTrue(ledger.TryGetRecord(0, 0, out FrameInputLedger.InputRecord stored));
            Assert.AreEqual(value._raw, stored.Value._raw);
            Assert.AreEqual(FrameInputLedger.InputState.Actual, stored.State);
        }

        [Test]
        public void ConflictingConfirmedInput_FaultPreventsMismatchConsumptionAndFurtherPublication()
        {
            var ledger = new FrameInputLedger();
            ledger.ResolveForSimulation(1);
            ledger.RecordActual(1, 0, new FrameInput(0x300u));
            Assert.IsTrue(ledger.TryGetEarliestMismatch(out _));

            RecordBoth(ledger, 0);
            Assert.AreEqual(0, ledger.ConfirmedThroughFrame);
            ledger.RecordActual(0, 0, new FrameInput(0x999u));

            Assert.IsTrue(ledger.HasIntegrityFault);
            Assert.IsFalse(ledger.TryGetEarliestMismatch(out _));
            Assert.Throws<System.InvalidOperationException>(() => ledger.ResolveForSimulation(2));
            Assert.IsFalse(ledger.TryGetActualFrame(0, out _));
            Assert.AreEqual(0, ledger.ConfirmedThroughFrame);

            ledger.RecordActual(1, 1, new FrameInput(0x200u));
            Assert.AreEqual(0, ledger.ConfirmedThroughFrame);
        }

        [Test]
        public void TryGetRecord_FrameEntryExists_ReturnsMissingSlotAsQueryableRecord()
        {
            var ledger = new FrameInputLedger();

            Assert.IsFalse(ledger.TryGetRecord(4, 1, out _));
            ledger.RecordActual(4, 0, new FrameInput(0x100u));

            Assert.IsTrue(ledger.TryGetRecord(4, 1, out FrameInputLedger.InputRecord record));
            Assert.AreEqual(FrameInputLedger.InputState.Missing, record.State);
            Assert.AreEqual(0u, record.Value._raw);
        }

        [Test]
        public void InvalidArgumentsAndRepeatedResolve_HaveStableResultsWithoutArrayAlias()
        {
            var ledger = new FrameInputLedger();

            Assert.AreEqual(FrameInputLedger.ActualDisposition.HistoryUnavailable,
                ledger.RecordActual(-1, 0, default).Disposition);
            Assert.AreEqual(FrameInputLedger.ActualDisposition.HistoryUnavailable,
                ledger.RecordActual(0, 2, default).Disposition);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => ledger.ResolveForSimulation(-1));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => ledger.ResolveForSimulation(0).GetPlayer(2));

            FrameInputLedger.ResolvedFrame first = ledger.ResolveForSimulation(0);
            FrameInputLedger.ResolvedFrame second = ledger.ResolveForSimulation(0);
            Assert.AreEqual(first.GetPlayer(0).Value._raw, second.GetPlayer(0).Value._raw);
            Assert.AreEqual(FrameInputLedger.InputState.Predicted, second.GetPlayer(0).State);
        }

        private static void RecordBoth(FrameInputLedger ledger, int frame)
        {
            ledger.RecordActual(frame, 0, new FrameInput(0x100u));
            ledger.RecordActual(frame, 1, new FrameInput(0x200u));
        }
    }
}

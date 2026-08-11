using System.Collections.Generic;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class PredictionSystemResolveRemoteTests
    {
        [Test]
        public void ResolveRemote_CurrentActualTransientInput_PreservesRawInput()
        {
            var prediction = CreatePrediction();
            var actual = new FrameInput(3, 0);
            actual.pickupPressed = true;
            actual.shootReleased = true;
            var actualByFrame = new Dictionary<int, uint> { [0] = actual._raw };

            FrameInput resolved = prediction.ResolveRemote(
                0,
                Resolver(actualByFrame),
                out int? errorFrame,
                out uint correctRaw);

            Assert.AreEqual(actual._raw, resolved._raw);
            Assert.IsTrue(resolved.pickupPressed);
            Assert.IsTrue(resolved.shootReleased);
            Assert.IsNull(errorFrame);
            Assert.AreEqual(0u, correctRaw);
        }

        [Test]
        public void ResolveRemote_MissingFrameAfterTransientActual_ClearsTransientActions()
        {
            var prediction = CreatePrediction();
            var actual = new FrameInput(3, 0);
            actual.pickupPressed = true;
            actual.shootReleased = true;
            var actualByFrame = new Dictionary<int, uint> { [0] = actual._raw };

            prediction.ResolveRemote(0, Resolver(actualByFrame), out _, out _);
            FrameInput predicted = prediction.ResolveRemote(
                1,
                Resolver(actualByFrame),
                out _,
                out _);

            Assert.AreEqual(3, predicted.moveDir);
            Assert.IsFalse(predicted.pickupPressed);
            Assert.IsFalse(predicted.shootReleased);
        }

        [Test]
        public void ResolveRemote_SanitizedPredictionMatchesTruth_DoesNotReportMismatch()
        {
            var prediction = CreatePrediction();
            var transient = new FrameInput(3, 0);
            transient.pickupPressed = true;
            transient.shootReleased = true;
            uint sustainedRaw = new FrameInput(3, 0)._raw;
            var actualByFrame = new Dictionary<int, uint> { [0] = transient._raw };

            prediction.ResolveRemote(0, Resolver(actualByFrame), out _, out _);
            prediction.ResolveRemote(1, Resolver(actualByFrame), out _, out _);
            actualByFrame[1] = sustainedRaw;
            actualByFrame[2] = sustainedRaw;

            prediction.ResolveRemote(
                2,
                Resolver(actualByFrame),
                out int? errorFrame,
                out uint correctRaw);

            Assert.IsNull(errorFrame);
            Assert.AreEqual(0u, correctRaw);
        }

        [Test]
        public void ResolveRemote_TransientHistoricalInputReturnedToPrediction_ReportsHistoricalMismatch()
        {
            var prediction = CreatePrediction();
            var actualByFrame = new Dictionary<int, uint> { [0] = 0 };
            uint moveRaw = new FrameInput(3, 0)._raw;

            prediction.ResolveRemote(0, Resolver(actualByFrame), out _, out _);
            FrameInput predicted = prediction.ResolveRemote(
                1,
                Resolver(actualByFrame),
                out _,
                out _);
            actualByFrame[1] = moveRaw;
            actualByFrame[2] = 0;

            FrameInput current = prediction.ResolveRemote(
                2,
                Resolver(actualByFrame),
                out int? errorFrame,
                out uint correctRaw);

            Assert.AreEqual(0u, predicted._raw);
            Assert.AreEqual(0u, current._raw);
            Assert.AreEqual(1, errorFrame);
            Assert.AreEqual(moveRaw, correctRaw);
        }

        [Test]
        public void ResolveRemote_HistoricalTruthArrivesWhileCurrentFrameMissing_ReportsMismatch()
        {
            var prediction = CreatePrediction();
            var actualByFrame = new Dictionary<int, uint> { [0] = 0 };
            uint moveRaw = new FrameInput(3, 0)._raw;

            prediction.ResolveRemote(0, Resolver(actualByFrame), out _, out _);
            prediction.ResolveRemote(1, Resolver(actualByFrame), out _, out _);
            actualByFrame[1] = moveRaw;

            FrameInput current = prediction.ResolveRemote(
                2,
                Resolver(actualByFrame),
                out int? errorFrame,
                out uint correctRaw);

            Assert.AreEqual(moveRaw, current._raw);
            Assert.AreEqual(1, errorFrame);
            Assert.AreEqual(moveRaw, correctRaw);
        }

        [Test]
        public void ResolveRemote_MultipleHistoricalMismatches_ReportsEarliestOnce()
        {
            var prediction = CreatePrediction();
            var actualByFrame = new Dictionary<int, uint> { [0] = 0 };
            uint firstMoveRaw = new FrameInput(3, 0)._raw;
            uint secondMoveRaw = new FrameInput(7, 0)._raw;

            prediction.ResolveRemote(0, Resolver(actualByFrame), out _, out _);
            prediction.ResolveRemote(1, Resolver(actualByFrame), out _, out _);
            prediction.ResolveRemote(2, Resolver(actualByFrame), out _, out _);
            actualByFrame[1] = firstMoveRaw;
            actualByFrame[2] = secondMoveRaw;
            actualByFrame[3] = 0;

            prediction.ResolveRemote(
                3,
                Resolver(actualByFrame),
                out int? errorFrame,
                out uint correctRaw);
            actualByFrame[4] = 0;
            prediction.ResolveRemote(
                4,
                Resolver(actualByFrame),
                out int? repeatedErrorFrame,
                out uint repeatedCorrectRaw);

            Assert.AreEqual(1, errorFrame);
            Assert.AreEqual(firstMoveRaw, correctRaw);
            Assert.IsNull(repeatedErrorFrame);
            Assert.AreEqual(0u, repeatedCorrectRaw);
        }

        [Test]
        public void ResolveRemote_ColdStartPrediction_WhenExactTruthArrives_ReportsMismatch()
        {
            var prediction = CreatePrediction();
            var actualByFrame = new Dictionary<int, uint>();
            uint moveRaw = new FrameInput(3, 0)._raw;

            prediction.ResolveRemote(0, Resolver(actualByFrame), out _, out _);
            actualByFrame[0] = moveRaw;
            actualByFrame[1] = 0;
            prediction.ResolveRemote(
                1,
                Resolver(actualByFrame),
                out int? errorFrame,
                out uint correctRaw);

            Assert.AreEqual(0, errorFrame);
            Assert.AreEqual(moveRaw, correctRaw);
        }

        [Test]
        public void ResolveRemote_ExactHistoricalPredictionMatches_DoesNotReportMismatch()
        {
            var prediction = CreatePrediction();
            var actualByFrame = new Dictionary<int, uint> { [0] = 0 };

            prediction.ResolveRemote(0, Resolver(actualByFrame), out _, out _);
            prediction.ResolveRemote(1, Resolver(actualByFrame), out _, out _);
            actualByFrame[1] = 0;
            actualByFrame[2] = 0;

            prediction.ResolveRemote(
                2,
                Resolver(actualByFrame),
                out int? errorFrame,
                out uint correctRaw);

            Assert.IsNull(errorFrame);
            Assert.AreEqual(0u, correctRaw);
        }

        private static PredictionSystem CreatePrediction()
        {
            var prediction = new PredictionSystem();
            prediction.Init();
            return prediction;
        }

        private static System.Func<int, uint?> Resolver(
            IReadOnlyDictionary<int, uint> actualByFrame)
        {
            return frame => actualByFrame.TryGetValue(frame, out uint raw)
                ? (uint?)raw
                : null;
        }
    }
}

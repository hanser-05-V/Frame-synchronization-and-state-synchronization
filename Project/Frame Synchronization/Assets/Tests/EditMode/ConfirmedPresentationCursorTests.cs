using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class ConfirmedPresentationCursorTests
    {
        private const float FrameDuration = 0.033f;

        [Test]
        public void WaterlineZero_FirstConfirmedInterval_StartsWithoutWaitingForNextFrame()
        {
            ConfirmedPresentationCursor cursor = CreateCursor(
                targetReadyBacklog: 0);

            ConfirmedPlaybackAdvance sample = cursor.Advance(
                confirmedHead: 0,
                deltaTimeSeconds: 0f,
                frameDurationSeconds: FrameDuration);

            Assert.IsTrue(sample.HasPresentationFrame);
            Assert.AreEqual(-1, sample.ActiveFromFrame);
            Assert.AreEqual(0, sample.ActiveToFrame);
            Assert.AreEqual(0, sample.ReadyBacklog);
        }

        [Test]
        public void WaterlineOne_FirstConfirmedInterval_WaitsUntilOneReadyFrameRemains()
        {
            ConfirmedPresentationCursor cursor = CreateCursor(
                targetReadyBacklog: 1);

            Assert.IsFalse(cursor.Advance(
                0,
                0f,
                FrameDuration).HasPresentationFrame);
            ConfirmedPlaybackAdvance sample = cursor.Advance(
                1,
                0f,
                FrameDuration);

            Assert.IsTrue(sample.HasPresentationFrame);
            Assert.AreEqual(0, sample.ActiveToFrame);
            Assert.AreEqual(1, sample.ReadyBacklog);
        }

        [TestCase(0f, 2f)]
        [TestCase(0.5f, 1.5f)]
        [TestCase(1f, 1f)]
        public void FractionalBacklog_IncludesActiveAlpha(
            float alpha,
            float expected)
        {
            ConfirmedPresentationCursor cursor = StartedCursorAt(
                activeToFrame: 1,
                alpha: alpha,
                targetReadyBacklog: 0);

            ConfirmedPlaybackAdvance sample = cursor.Advance(
                2,
                0f,
                FrameDuration);

            Assert.AreEqual(
                expected,
                sample.FractionalBacklog,
                0.0001f);
        }

        [Test]
        public void OneReadyInterval_TargetSpeedUsesFractionalExcess()
        {
            ConfirmedPresentationCursor cursor = StartedCursorAt(
                activeToFrame: 0,
                alpha: 0f,
                targetReadyBacklog: 0);

            ConfirmedPlaybackAdvance sample = cursor.Advance(
                1,
                0f,
                FrameDuration);

            Assert.AreEqual(1.15f, sample.TargetSpeed, 0.0001f);
            Assert.AreEqual(1f, sample.PlaybackSpeed, 0.0001f);
        }

        [Test]
        public void BacklogEight_TargetSpeedNeverExceedsOnePointFive()
        {
            ConfirmedPresentationCursor cursor = StartedCursorAt(
                activeToFrame: 0,
                alpha: 0f,
                targetReadyBacklog: 0);

            ConfirmedPlaybackAdvance sample = cursor.Advance(
                8,
                0f,
                FrameDuration);

            Assert.AreEqual(1.5f, sample.TargetSpeed, 0.0001f);
        }

        [Test]
        public void PlaybackSpeed_MovesTowardTargetWithinConfiguredTimeScale()
        {
            ConfirmedPresentationCursor cursor = StartedCursorAt(
                activeToFrame: 0,
                alpha: 0f,
                targetReadyBacklog: 0);
            cursor.Advance(8, 0f, FrameDuration);

            ConfirmedPlaybackAdvance sample = cursor.Advance(
                8,
                0.01f,
                FrameDuration);

            Assert.AreEqual(1.5f, sample.TargetSpeed, 0.0001f);
            Assert.AreEqual(1.05f, sample.PlaybackSpeed, 0.0001f);
        }

        [Test]
        public void CatchUpHysteresis_DoesNotToggleInsideDeadBand()
        {
            ConfirmedPresentationCursor cursor = CreateCursor(
                targetReadyBacklog: 0,
                speedSmoothingSeconds: 10f);
            cursor.Advance(0, 0f, FrameDuration);
            cursor.Advance(1, 0f, FrameDuration);

            ConfirmedPlaybackAdvance insideBand = cursor.Advance(
                1,
                0.8f * FrameDuration,
                FrameDuration);

            Assert.Greater(insideBand.TargetSpeed, 1f);
        }

        [TestCase(30)]
        [TestCase(60)]
        [TestCase(144)]
        public void StableThirtySecondTrace_RemainsContinuousAcrossRenderRates(
            int framesPerSecond)
        {
            float renderDeltaSeconds = 1f / framesPerSecond;
            float arrivalAccumulator = 0f;
            int confirmedHead = -1;
            int overflowCount = 0;
            int faultCount = 0;
            int speedTierSwitches = 0;
            bool wasFast = false;
            ConfirmedPresentationCursor cursor = CreateCursor(0);
            ConfirmedPlaybackAdvance sample = default;

            for (float elapsed = 0f;
                 elapsed < 30f;
                 elapsed += renderDeltaSeconds)
            {
                arrivalAccumulator += renderDeltaSeconds;
                while (arrivalAccumulator >= FrameDuration)
                {
                    arrivalAccumulator -= FrameDuration;
                    confirmedHead++;
                }

                sample = cursor.Advance(
                    confirmedHead,
                    renderDeltaSeconds,
                    FrameDuration);
                bool isFast = sample.PlaybackSpeed > 1.001f;
                if (isFast != wasFast)
                    speedTierSwitches++;
                wasFast = isFast;

                Assert.LessOrEqual(sample.ReadyBacklog, 1);
                if (sample.IsOverflowRebase)
                    overflowCount++;
                if (sample.IsFaulted)
                    faultCount++;
            }

            Assert.AreEqual(confirmedHead, sample.ActiveToFrame);
            Assert.AreEqual(0, overflowCount);
            Assert.AreEqual(0, faultCount);
            Assert.LessOrEqual(speedTierSwitches, 2);
            Assert.AreEqual(1f, sample.PlaybackSpeed, 0.001f);
        }

        [Test]
        public void WaterlineOne_FirstVisibleIntervalIsExactlyOneConfirmedHeadLater()
        {
            ConfirmedPresentationCursor waterlineZero = CreateCursor(0);
            ConfirmedPresentationCursor waterlineOne = CreateCursor(1);

            ConfirmedPlaybackAdvance zeroAtHeadZero =
                waterlineZero.Advance(0, 0f, FrameDuration);
            ConfirmedPlaybackAdvance oneAtHeadZero =
                waterlineOne.Advance(0, 0f, FrameDuration);
            ConfirmedPlaybackAdvance oneAtHeadOne =
                waterlineOne.Advance(1, 0f, FrameDuration);

            Assert.IsTrue(zeroAtHeadZero.HasPresentationFrame);
            Assert.IsFalse(oneAtHeadZero.HasPresentationFrame);
            Assert.IsTrue(oneAtHeadOne.HasPresentationFrame);
            Assert.AreEqual(
                zeroAtHeadZero.ActiveToFrame,
                oneAtHeadOne.ActiveToFrame);
        }

        [Test]
        public void LongRenderFrame_CrossesIntervalsSequentiallyAndReturnsFinalPhase()
        {
            ConfirmedPresentationCursor cursor = CreateCursor(
                targetReadyBacklog: 0,
                catchUpGainPerFrame: 0f);
            cursor.Advance(0, 0f, FrameDuration);

            ConfirmedPlaybackAdvance sample = cursor.Advance(
                4,
                0.080f,
                FrameDuration);

            Assert.AreEqual(2, sample.CrossedIntervalCount);
            Assert.AreEqual(2, sample.RenderFrameDropCount);
            Assert.AreEqual(2, sample.ActiveToFrame);
            Assert.That(sample.Alpha, Is.InRange(0f, 1f));
        }

        [Test]
        public void Underflow_HoldsEndpointAndResumesFromNextContinuousInterval()
        {
            ConfirmedPresentationCursor cursor = StartedCursorAt(
                activeToFrame: 0,
                alpha: 1f,
                targetReadyBacklog: 0);

            ConfirmedPlaybackAdvance held = cursor.Advance(
                0,
                0.050f,
                FrameDuration);
            ConfirmedPlaybackAdvance resumed = cursor.Advance(
                1,
                0.010f,
                FrameDuration);

            Assert.IsTrue(held.IsUnderflow);
            Assert.AreEqual(0, held.ActiveToFrame);
            Assert.AreEqual(1, resumed.ActiveToFrame);
            Assert.Greater(resumed.Alpha, 0f);
        }

        [Test]
        public void EighthReadyInterval_DoesNotOverflow()
        {
            ConfirmedPresentationCursor cursor = StartedCursorAt(
                activeToFrame: 3,
                alpha: 0.5f,
                targetReadyBacklog: 0);

            ConfirmedPlaybackAdvance sample = cursor.Advance(
                11,
                0f,
                FrameDuration);

            Assert.AreEqual(8, sample.ReadyBacklog);
            Assert.IsFalse(sample.IsOverflowRebase);
        }

        [TestCase(0, 12)]
        [TestCase(1, 11)]
        public void NinthReadyInterval_RebasesToHeadMinusTarget(
            int targetReadyBacklog,
            int expectedToFrame)
        {
            ConfirmedPresentationCursor cursor = StartedCursorAt(
                activeToFrame: 3,
                alpha: 0.5f,
                targetReadyBacklog: targetReadyBacklog);

            ConfirmedPlaybackAdvance sample = cursor.Advance(
                12,
                0f,
                FrameDuration);

            Assert.IsTrue(sample.IsOverflowRebase);
            Assert.AreEqual(expectedToFrame, sample.ActiveToFrame);
            Assert.AreEqual(4, sample.DroppedFromFrame);
            Assert.AreEqual(expectedToFrame - 1, sample.DroppedToFrame);
            Assert.AreEqual(1f, sample.PlaybackSpeed);
            Assert.AreEqual(1f, sample.TargetSpeed);
        }

        [Test]
        public void PresentationFault_PausesConfirmedTrackWithoutChangingLastGoodFrame()
        {
            ConfirmedPresentationCursor cursor = StartedCursorAt(
                activeToFrame: 5,
                alpha: 0.5f,
                targetReadyBacklog: 0);

            cursor.EnterPresentationFault(4, 6);
            cursor.EnterPresentationFault(7, 8);
            ConfirmedPlaybackAdvance sample = cursor.Advance(
                8,
                1f,
                FrameDuration);

            Assert.IsTrue(sample.IsFaulted);
            Assert.AreEqual(5, sample.ActiveToFrame);
            Assert.AreEqual(0.5f, sample.Alpha, 0.0001f);
            Assert.AreEqual(4, sample.MissingFromFrame);
            Assert.AreEqual(6, sample.MissingToFrame);
        }

        [Test]
        public void Rollback_PreservesCursorAndRecalculatesSpeedFromCurrentBacklog()
        {
            ConfirmedPresentationCursor cursor = StartedCursorAt(
                activeToFrame: 5,
                alpha: 0.5f,
                targetReadyBacklog: 0);
            int frameBefore = cursor.ActiveToFrame;
            float alphaBefore = cursor.InterpolationAlpha;

            cursor.RecalculateAfterRollback(6);

            Assert.AreEqual(frameBefore, cursor.ActiveToFrame);
            Assert.AreEqual(alphaBefore, cursor.InterpolationAlpha);
            Assert.AreEqual(1.075f, cursor.TargetSpeed, 0.0001f);
            Assert.AreEqual(1f, cursor.PlaybackSpeed, 0.0001f);
        }

        private static ConfirmedPresentationCursor CreateCursor(
            int targetReadyBacklog,
            float catchUpGainPerFrame = 0.15f,
            float maximumPlaybackSpeed = 1.5f,
            float speedSmoothingSeconds = 0.10f)
        {
            var settings = new ConfirmedPlaybackSettings(
                targetReadyBacklog: targetReadyBacklog,
                maxReadyBacklog: 8,
                catchUpGainPerFrame: catchUpGainPerFrame,
                maximumPlaybackSpeed: maximumPlaybackSpeed,
                speedSmoothingSeconds: speedSmoothingSeconds,
                catchUpEnterExcessFrames: 0.25f,
                catchUpExitExcessFrames: 0.10f,
                overflowCorrectionMaximumSeconds: 0.20f);
            settings.ValidateOrReset(_ => { });
            return new ConfirmedPresentationCursor(settings);
        }

        private static ConfirmedPresentationCursor StartedCursorAt(
            int activeToFrame,
            float alpha,
            int targetReadyBacklog)
        {
            ConfirmedPresentationCursor cursor = CreateCursor(
                targetReadyBacklog);
            cursor.Advance(
                targetReadyBacklog,
                0f,
                FrameDuration);
            for (int nextToFrame = 1;
                 nextToFrame <= activeToFrame;
                 nextToFrame++)
            {
                cursor.Advance(
                    nextToFrame - 1 + targetReadyBacklog,
                    FrameDuration,
                    FrameDuration);
                cursor.Advance(
                    nextToFrame + targetReadyBacklog,
                    0f,
                    FrameDuration);
            }

            if (alpha > 0f)
            {
                cursor.Advance(
                    activeToFrame + targetReadyBacklog,
                    alpha * FrameDuration,
                    FrameDuration);
            }
            return cursor;
        }
    }
}

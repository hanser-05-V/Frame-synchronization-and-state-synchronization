using System.Collections.Generic;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class ConfirmedPlaybackSettingsTests
    {
        [Test]
        public void Defaults_AreLowLatencyRouteCTuning()
        {
            var settings = new ConfirmedPlaybackSettings();

            Assert.AreEqual(0, settings.TargetReadyBacklog);
            Assert.AreEqual(8, settings.MaxReadyBacklog);
            Assert.AreEqual(0.15f, settings.CatchUpGainPerFrame);
            Assert.AreEqual(1.5f, settings.MaximumPlaybackSpeed);
            Assert.AreEqual(0.10f, settings.SpeedSmoothingSeconds);
            Assert.AreEqual(0.25f, settings.CatchUpEnterExcessFrames);
            Assert.AreEqual(0.10f, settings.CatchUpExitExcessFrames);
            Assert.AreEqual(
                0.20f,
                settings.OverflowCorrectionMaximumSeconds);
        }

        [Test]
        public void ValidateOrReset_InvalidCombination_RestoresWholeDefaultSetOnce()
        {
            var warnings = new List<string>();
            var settings = new ConfirmedPlaybackSettings(
                targetReadyBacklog: 9,
                maxReadyBacklog: 8,
                catchUpGainPerFrame: -1f,
                maximumPlaybackSpeed: 0.5f,
                speedSmoothingSeconds: 0f,
                catchUpEnterExcessFrames: 0.1f,
                catchUpExitExcessFrames: 0.25f,
                overflowCorrectionMaximumSeconds: 0f);

            settings.ValidateOrReset(warnings.Add);

            Assert.AreEqual(1, warnings.Count);
            Assert.AreEqual(0, settings.TargetReadyBacklog);
            Assert.AreEqual(8, settings.MaxReadyBacklog);
            Assert.AreEqual(0.15f, settings.CatchUpGainPerFrame);
            Assert.AreEqual(1.5f, settings.MaximumPlaybackSpeed);
            Assert.AreEqual(0.10f, settings.SpeedSmoothingSeconds);
            Assert.AreEqual(0.25f, settings.CatchUpEnterExcessFrames);
            Assert.AreEqual(0.10f, settings.CatchUpExitExcessFrames);
            Assert.AreEqual(
                0.20f,
                settings.OverflowCorrectionMaximumSeconds);
        }

        [Test]
        public void ValidateOrReset_TargetEqualsCapacity_RestoresDefaultsOnce()
        {
            var warnings = new List<string>();
            var settings = new ConfirmedPlaybackSettings(
                targetReadyBacklog: 8,
                maxReadyBacklog: 8,
                catchUpGainPerFrame: 0.15f,
                maximumPlaybackSpeed: 1.5f,
                speedSmoothingSeconds: 0.10f,
                catchUpEnterExcessFrames: 0.25f,
                catchUpExitExcessFrames: 0.10f,
                overflowCorrectionMaximumSeconds: 0.20f);

            settings.ValidateOrReset(warnings.Add);

            Assert.AreEqual(1, warnings.Count);
            Assert.AreEqual(0, settings.TargetReadyBacklog);
            Assert.AreEqual(8, settings.MaxReadyBacklog);
        }
    }
}

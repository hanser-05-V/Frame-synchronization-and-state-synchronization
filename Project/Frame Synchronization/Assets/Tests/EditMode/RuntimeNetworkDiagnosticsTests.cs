using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class RuntimeNetworkDiagnosticsTests
    {
        [Test]
        public void DisabledRecorder_ProducesNoLines()
        {
            var lines = new List<string>();
            var diagnostics = new RuntimeNetworkDiagnostics(
                false,
                lines.Add,
                1000);

            diagnostics.RecordReceive(
                new NetworkPacketArrival(0x10u, 1, 0, 1));
            diagnostics.RecordDrain(12, 3, 2, 5);
            diagnostics.RecordLogic(13, 2, 5, 7, 5);
            diagnostics.RecordRollback(14, 2, 4, 3, 0x1234UL);
            diagnostics.RecordRemoteRender(15, 1f, 2f, 7, 5, 5);
            diagnostics.RecordActualArrival(7, 16);
            diagnostics.RecordConfirmedPlayback(
                17,
                PlaybackSample(toFrame: 7, alpha: 0.2f));
            diagnostics.RecordBallSourceSwitch(
                18,
                7,
                ViewSampleSource.Confirmed,
                1);

            Assert.IsEmpty(lines);
        }

        [Test]
        public void ActualArrival_FirstVisibleSample_EmitsLatencyOnceUsingCanonicalFrame()
        {
            var lines = new List<string>();
            var diagnostics = new RuntimeNetworkDiagnostics(
                true,
                lines.Add,
                1000);
            diagnostics.RecordActualArrival(7, 100);

            diagnostics.RecordConfirmedPlayback(
                150,
                PlaybackSample(toFrame: 7, alpha: 0.2f));
            diagnostics.RecordConfirmedPlayback(
                160,
                PlaybackSample(toFrame: 7, alpha: 0.5f));

            Assert.AreEqual(
                1,
                lines.Count(line => line.Contains("actualToVisibleMs")));
            StringAssert.Contains(
                "\"actualToVisibleMs\":50.000",
                lines[0]);
        }

        [Test]
        public void PlaybackDiagnostics_EmitBacklogSpeedDropAndOverflowRange()
        {
            RuntimeNetworkDiagnostics diagnostics =
                EnabledDiagnostics(out List<string> lines);

            diagnostics.RecordConfirmedPlayback(
                200,
                OverflowSample(4, 11));

            string line = lines.Single();
            StringAssert.Contains("\"readyBacklog\":", line);
            StringAssert.Contains("\"fractionalBacklog\":", line);
            StringAssert.Contains("\"targetSpeed\":", line);
            StringAssert.Contains("\"playbackSpeed\":", line);
            StringAssert.Contains("\"droppedFromFrame\":4", line);
            StringAssert.Contains("\"droppedToFrame\":11", line);
        }

        [Test]
        public void ActualArrival_CacheIsBoundedAndDuplicateKeepsFirstTimestamp()
        {
            var lines = new List<string>();
            var diagnostics = new RuntimeNetworkDiagnostics(
                true,
                lines.Add,
                1000,
                targetReadyBacklog: 0,
                maxReadyBacklog: 1);
            diagnostics.RecordActualArrival(0, 100);
            diagnostics.RecordActualArrival(1, 110);
            diagnostics.RecordActualArrival(2, 120);
            diagnostics.RecordActualArrival(3, 130);
            diagnostics.RecordActualArrival(3, 999);

            diagnostics.RecordConfirmedPlayback(
                200,
                PlaybackSample(toFrame: 0, alpha: 0.5f));
            diagnostics.RecordConfirmedPlayback(
                230,
                PlaybackSample(toFrame: 3, alpha: 0.5f));

            Assert.AreEqual(
                1,
                lines.Count(line => line.Contains("actualToVisibleMs")));
            StringAssert.Contains(
                "\"actualToVisibleMs\":100.000",
                lines[1]);
        }

        [Test]
        public void BallSourceSwitch_EmitsOnlyWhenSourceOrHolderChanges()
        {
            RuntimeNetworkDiagnostics diagnostics =
                EnabledDiagnostics(out List<string> lines);

            diagnostics.RecordBallSourceSwitch(
                100,
                2,
                ViewSampleSource.Confirmed,
                1);
            diagnostics.RecordBallSourceSwitch(
                110,
                2,
                ViewSampleSource.Confirmed,
                1);
            diagnostics.RecordBallSourceSwitch(
                120,
                3,
                ViewSampleSource.Predicted,
                0);

            Assert.AreEqual(2, lines.Count);
            StringAssert.Contains("\"type\":\"ballSourceSwitch\"", lines[0]);
            StringAssert.Contains("\"source\":\"Confirmed\"", lines[0]);
            StringAssert.Contains("\"holderPlayerIndex\":1", lines[0]);
            StringAssert.Contains("\"source\":\"Predicted\"", lines[1]);
        }

        [Test]
        public void ReceiveAndDrain_EmitIntervalsCountsAndQueueSpan()
        {
            var lines = new List<string>();
            var diagnostics = new RuntimeNetworkDiagnostics(
                true,
                lines.Add,
                1000);

            diagnostics.RecordReceive(
                new NetworkPacketArrival(0x10u, 4, 0, 100));
            diagnostics.RecordReceive(
                new NetworkPacketArrival(0x11u, 5, 1, 133));
            diagnostics.RecordDrain(150, 2, 100, 133);

            StringAssert.Contains("\"type\":\"receive\"", lines[1]);
            StringAssert.Contains("\"intervalMs\":33.000", lines[1]);
            StringAssert.Contains("\"type\":\"drain\"", lines[2]);
            StringAssert.Contains("\"count\":2", lines[2]);
            StringAssert.Contains("\"queueSpanMs\":33.000", lines[2]);
        }

        [Test]
        public void LogicAndRollback_EmitHeadsDeltaReplayAndConfirmedHash()
        {
            var lines = new List<string>();
            var diagnostics = new RuntimeNetworkDiagnostics(
                true,
                lines.Add,
                1000);

            diagnostics.RecordLogic(200, 2, 5, 7, 5);
            diagnostics.RecordRollback(210, 3, 5, 2, 0x1234UL);

            StringAssert.Contains("\"confirmedDelta\":3", lines[0]);
            StringAssert.Contains("\"predictedFrame\":7", lines[0]);
            StringAssert.Contains("\"viewFrame\":5", lines[0]);
            StringAssert.Contains("\"mismatchFrame\":3", lines[1]);
            StringAssert.Contains("\"replayedFrames\":2", lines[1]);
            StringAssert.Contains("\"confirmedHash\":\"0x0000000000001234\"", lines[1]);
        }

        [Test]
        public void RemoteRender_TracksDisplacementReverseAndStationaryDuration()
        {
            var lines = new List<string>();
            var diagnostics = new RuntimeNetworkDiagnostics(
                true,
                lines.Add,
                1000);

            diagnostics.RecordRemoteRender(0, 0f, 0f, 0, 0, 0);
            diagnostics.RecordRemoteRender(10, 1f, 0f, 1, 1, 1);
            diagnostics.RecordRemoteRender(20, 0.5f, 0f, 2, 2, 2);
            diagnostics.RecordRemoteRender(30, 0.5f, 0f, 3, 3, 3);

            StringAssert.Contains("\"displacement\":1.000000", lines[1]);
            StringAssert.Contains("\"reverseDisplacement\":0.500000", lines[2]);
            StringAssert.Contains("\"stationaryMs\":10.000", lines[3]);
        }

        [Test]
        public void Formatting_IsInvariantUnderCommaDecimalCulture()
        {
            CultureInfo original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var lines = new List<string>();
                var diagnostics = new RuntimeNetworkDiagnostics(
                    true,
                    lines.Add,
                    1000);

                diagnostics.RecordRemoteRender(0, 0f, 0f, 0, 0, 0);
                diagnostics.RecordRemoteRender(10, 1.5f, 0f, 1, 1, 1);
                diagnostics.RecordConfirmedPlayback(
                    20,
                    PlaybackSample(toFrame: 1, alpha: 0.25f));

                StringAssert.Contains("1.500000", lines[1]);
                StringAssert.DoesNotContain("1,500000", lines[1]);
                StringAssert.Contains("\"alpha\":0.250000", lines[2]);
                StringAssert.DoesNotContain("0,250000", lines[2]);
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        private static RuntimeNetworkDiagnostics EnabledDiagnostics(
            out List<string> lines)
        {
            lines = new List<string>();
            return new RuntimeNetworkDiagnostics(true, lines.Add, 1000);
        }

        private static ConfirmedPlaybackAdvance PlaybackSample(
            int toFrame,
            float alpha)
        {
            return new ConfirmedPlaybackAdvance(
                activeFromFrame: toFrame - 1,
                activeToFrame: toFrame,
                alpha,
                readyBacklog: 0,
                fractionalBacklog: 1f - alpha,
                excessBacklog: 0f,
                targetSpeed: 1f,
                playbackSpeed: 1f,
                crossedIntervalCount: 0,
                renderFrameDropCount: 0,
                hasPresentationFrame: true,
                isUnderflow: false,
                isOverflowRebase: false,
                droppedFromFrame: -1,
                droppedToFrame: -1,
                isFaulted: false,
                missingFromFrame: -1,
                missingToFrame: -1);
        }

        private static ConfirmedPlaybackAdvance OverflowSample(
            int droppedFromFrame,
            int droppedToFrame)
        {
            return new ConfirmedPlaybackAdvance(
                activeFromFrame: 11,
                activeToFrame: 12,
                alpha: 1f,
                readyBacklog: 0,
                fractionalBacklog: 0f,
                excessBacklog: 0f,
                targetSpeed: 1f,
                playbackSpeed: 1f,
                crossedIntervalCount: 0,
                renderFrameDropCount: 0,
                hasPresentationFrame: true,
                isUnderflow: false,
                isOverflowRebase: true,
                droppedFromFrame,
                droppedToFrame,
                isFaulted: false,
                missingFromFrame: -1,
                missingToFrame: -1);
        }
    }
}

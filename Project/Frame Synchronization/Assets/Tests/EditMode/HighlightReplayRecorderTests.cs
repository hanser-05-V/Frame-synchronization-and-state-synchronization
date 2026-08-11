using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class HighlightReplayRecorderTests
    {
        [Test]
        public void ProcessStableFrame_ScoredShot_CapturesConfiguredWindow()
        {
            var recorder = new HighlightReplayRecorder();

            ProcessRange(recorder, 0, 145, frame =>
                frame < 70
                    ? BallEntity.EState.Held
                    : frame < 100
                        ? BallEntity.EState.Airborne
                        : BallEntity.EState.Scored);

            Assert.AreEqual(1, recorder.Clips.Count);
            HighlightClip clip = recorder.Clips[0];
            Assert.AreEqual(10, clip.StartFrame);
            Assert.AreEqual(70, clip.ShotFrame);
            Assert.AreEqual(100, clip.ScoreFrame);
            Assert.AreEqual(145, clip.EndFrame);
            Assert.AreEqual(1, clip.ShooterPlayerIndex);
            Assert.AreEqual(136, clip.FrameCount);
        }

        [Test]
        public void ProcessStableFrame_EarlyShot_ClampsStartToZero()
        {
            var recorder = new HighlightReplayRecorder();

            ProcessRange(recorder, 0, 75, frame =>
                frame < 20
                    ? BallEntity.EState.Held
                    : frame < 30
                        ? BallEntity.EState.Airborne
                        : BallEntity.EState.Scored);

            Assert.AreEqual(0, recorder.Clips[0].StartFrame);
        }

        [Test]
        public void ProcessStableFrame_MissedShot_DoesNotCreateClip()
        {
            var recorder = new HighlightReplayRecorder();

            ProcessRange(recorder, 0, 100, frame =>
                frame < 10
                    ? BallEntity.EState.Held
                    : frame < 20
                        ? BallEntity.EState.Airborne
                        : BallEntity.EState.Free);

            Assert.AreEqual(0, recorder.Clips.Count);
        }

        [Test]
        public void ProcessStableFrame_FourScoredShots_KeepsLatestThree()
        {
            var recorder = new HighlightReplayRecorder();

            ProcessRange(recorder, 0, 245, GetFourShotState);

            Assert.AreEqual(3, recorder.Clips.Count);
            Assert.AreEqual(70, recorder.Clips[0].ShotFrame);
            Assert.AreEqual(130, recorder.Clips[1].ShotFrame);
            Assert.AreEqual(190, recorder.Clips[2].ShotFrame);
        }

        [Test]
        public void ProcessStableFrame_HistoryEvicted_RejectsIncompleteClip()
        {
            var recorder = new HighlightReplayRecorder();

            ProcessRange(recorder, 0, 545, frame =>
                frame < 10
                    ? BallEntity.EState.Held
                    : frame < 500
                        ? BallEntity.EState.Airborne
                        : BallEntity.EState.Scored);

            Assert.AreEqual(0, recorder.Clips.Count);
            StringAssert.Contains("missing", recorder.LastCaptureError.ToLowerInvariant());
        }

        [Test]
        public void ProcessStableFrame_EndMatchFromEitherPlayer_UsesFirstFrame()
        {
            var recorder = new HighlightReplayRecorder();
            FrameInput endFromPlayer1 = new FrameInput();
            endFromPlayer1.endMatchPressed = true;

            recorder.ProcessStableFrame(
                0,
                CreateSnapshot(0, BallEntity.EState.Held),
                new[] { new FrameInput(), endFromPlayer1 });
            recorder.ProcessStableFrame(
                1,
                CreateSnapshot(1, BallEntity.EState.Held),
                new[] { endFromPlayer1, new FrameInput() });

            Assert.IsTrue(recorder.EndMatchRequested);
            Assert.AreEqual(0, recorder.EndMatchRequestFrame);
            Assert.IsTrue(recorder.CanEnterPostGame);
            Assert.AreEqual(0, recorder.PostGameTerminalFrame);
        }

        [Test]
        public void ProcessStableFrame_EndRequestedDuringPostRoll_WaitsForClip()
        {
            var recorder = new HighlightReplayRecorder();

            for (int frame = 0; frame <= 145; frame++)
            {
                BallEntity.EState state = frame < 70
                    ? BallEntity.EState.Held
                    : frame < 100
                        ? BallEntity.EState.Airborne
                        : BallEntity.EState.Scored;
                FrameInput localInput = new FrameInput();
                if (frame == 110)
                    localInput.endMatchPressed = true;

                recorder.ProcessStableFrame(
                    frame,
                    CreateSnapshot(frame, state),
                    new[] { localInput, new FrameInput() });

                if (frame == 110)
                {
                    Assert.IsFalse(recorder.CanEnterPostGame);
                    Assert.AreEqual(-1, recorder.PostGameTerminalFrame);
                }
            }

            Assert.IsTrue(recorder.CanEnterPostGame);
            Assert.AreEqual(145, recorder.PostGameTerminalFrame);
            Assert.AreEqual(1, recorder.Clips.Count);
        }

        [Test]
        public void ProcessStableFrame_SameStableHistory_PublishesSameTerminalFrame()
        {
            var first = new HighlightReplayRecorder();
            var second = new HighlightReplayRecorder();

            for (int frame = 0; frame <= 145; frame++)
            {
                BallEntity.EState state = frame < 70
                    ? BallEntity.EState.Held
                    : frame < 100
                        ? BallEntity.EState.Airborne
                        : BallEntity.EState.Scored;
                FrameInput endInput = new FrameInput();
                if (frame == 110)
                    endInput.endMatchPressed = true;
                FrameSnapshot snapshot = CreateSnapshot(frame, state);
                FrameInput[] inputs = { endInput, new FrameInput() };

                first.ProcessStableFrame(frame, snapshot, inputs);
                second.ProcessStableFrame(frame, snapshot, inputs);
            }

            Assert.AreEqual(145, first.PostGameTerminalFrame);
            Assert.AreEqual(first.PostGameTerminalFrame, second.PostGameTerminalFrame);
        }

        [Test]
        public void RebaseAt_LostHistory_DiscardsIncompleteShotAndResumes()
        {
            var recorder = new HighlightReplayRecorder();
            ProcessRange(recorder, 0, 20, frame =>
                frame < 10
                    ? BallEntity.EState.Held
                    : BallEntity.EState.Airborne);

            recorder.RebaseAt(120);
            recorder.ProcessStableFrame(
                120,
                CreateSnapshot(120, BallEntity.EState.Free),
                new[] { new FrameInput(), new FrameInput() });

            Assert.AreEqual(0, recorder.Clips.Count);
            Assert.AreEqual(120, recorder.LastProcessedFrame);
        }

        private static void ProcessRange(
            HighlightReplayRecorder recorder,
            int startFrame,
            int endFrame,
            System.Func<int, BallEntity.EState> stateForFrame)
        {
            for (int frame = startFrame; frame <= endFrame; frame++)
            {
                recorder.ProcessStableFrame(
                    frame,
                    CreateSnapshot(frame, stateForFrame(frame)),
                    new[] { new FrameInput(), new FrameInput() });
            }
        }

        private static BallEntity.EState GetFourShotState(int frame)
        {
            if (frame < 10) return BallEntity.EState.Held;
            if (frame < 20) return BallEntity.EState.Airborne;
            if (frame < 66) return BallEntity.EState.Scored;
            if (frame < 70) return BallEntity.EState.Held;
            if (frame < 80) return BallEntity.EState.Airborne;
            if (frame < 126) return BallEntity.EState.Scored;
            if (frame < 130) return BallEntity.EState.Held;
            if (frame < 140) return BallEntity.EState.Airborne;
            if (frame < 186) return BallEntity.EState.Scored;
            if (frame < 190) return BallEntity.EState.Held;
            if (frame < 200) return BallEntity.EState.Airborne;
            return BallEntity.EState.Scored;
        }

        private static FrameSnapshot CreateSnapshot(
            int frameID,
            BallEntity.EState ballState)
        {
            return new FrameSnapshot
            {
                frameID = frameID,
                ballState = (int)ballState,
                ballHolder = ballState == BallEntity.EState.Held ? 1 : -1,
                player1X = FixedInt.FromInt(frameID),
                player2X = FixedInt.FromInt(-frameID)
            };
        }
    }
}

using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class RuntimeControlOverlayTests
    {
        [Test]
        public void BuildText_Playing_ListsOnlySafeLiveOperations()
        {
            string text = RuntimeControlOverlay.BuildText(
                MatchPhase.Playing,
                false,
                false,
                BallEntity.EState.Free,
                -1,
                0,
                0,
                null,
                false);

            StringAssert.Contains("WASD", text);
            StringAssert.Contains("E", text);
            StringAssert.Contains("Space", text);
            StringAssert.Contains("F9", text);
            StringAssert.Contains("Esc", text);
            StringAssert.DoesNotContain("F10", text);
            StringAssert.DoesNotContain("C：", text);
        }

        [Test]
        public void BuildText_PlayingWhileWaiting_ShowsHighlightTailStatus()
        {
            string text = RuntimeControlOverlay.BuildText(
                MatchPhase.Playing,
                true,
                false,
                BallEntity.EState.Free,
                -1,
                0,
                0,
                null,
                false);

            StringAssert.Contains("正在收尾精彩片段", text);
        }

        [Test]
        public void BuildText_ReplayWithClip_ListsPlaybackControlsAndFrames()
        {
            HighlightClip clip = CreateClip(10, 20);

            string text = RuntimeControlOverlay.BuildText(
                MatchPhase.PostGameReplay,
                false,
                false,
                BallEntity.EState.Free,
                -1,
                0,
                3,
                clip,
                true);

            StringAssert.Contains("P", text);
            StringAssert.Contains("[ / ]", text);
            StringAssert.Contains("R", text);
            StringAssert.Contains("1/3", text);
            StringAssert.Contains("10-20", text);
            StringAssert.Contains("播放中", text);
        }

        [Test]
        public void BuildText_ReplayWithoutClip_ShowsEmptyState()
        {
            string text = RuntimeControlOverlay.BuildText(
                MatchPhase.PostGameReplay,
                false,
                false,
                BallEntity.EState.Free,
                -1,
                0,
                0,
                null,
                false);

            StringAssert.Contains("本场暂无精彩片段", text);
        }

        [Test]
        public void BuildText_PlayingWithHeldBallAndPreparation_ShowsStatus()
        {
            string text = RuntimeControlOverlay.BuildText(
                MatchPhase.Playing,
                false,
                true,
                BallEntity.EState.Held,
                1,
                0,
                0,
                null,
                false);

            StringAssert.Contains("\u7bee\u7403\uff1aHeld", text);
            StringAssert.Contains("\u6301\u7403\u8005\uff1aP2", text);
            StringAssert.Contains("\u51c6\u5907\u6295\u7bee\uff0c\u677e\u5f00\u51fa\u624b", text);
        }

        [TestCase(MatchPhase.PostGameReplay, false, true, true, false)]
        [TestCase(MatchPhase.Playing, true, true, true, false)]
        [TestCase(MatchPhase.Playing, false, false, true, false)]
        [TestCase(MatchPhase.Playing, false, true, false, false)]
        [TestCase(MatchPhase.Playing, false, true, true, true)]
        public void ShouldShowShotPreparation_UsesOnlyLocalPresentationState(
            MatchPhase phase,
            bool paused,
            bool spaceHeld,
            bool localHasBall,
            bool expected)
        {
            Assert.AreEqual(
                expected,
                RuntimeControlOverlay.ShouldShowShotPreparation(
                    phase,
                    paused,
                    spaceHeld,
                    localHasBall));
        }

        private static HighlightClip CreateClip(int startFrame, int endFrame)
        {
            var frames = new FrameSnapshot[endFrame - startFrame + 1];
            for (int i = 0; i < frames.Length; i++)
                frames[i].frameID = startFrame + i;

            return new HighlightClip(
                startFrame,
                startFrame,
                endFrame,
                endFrame,
                0,
                frames);
        }
    }
}

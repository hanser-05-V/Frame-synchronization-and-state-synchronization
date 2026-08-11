using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class PostGameHighlightReplayControllerTests
    {
        [Test]
        public void Constructor_WithClip_StartsFirstFramePlaying()
        {
            var controller = new PostGameHighlightReplayController(
                new[] { CreateClip(10, 3) });

            Assert.IsTrue(controller.IsPlaying);
            Assert.AreEqual(0, controller.CurrentClipIndex);
            Assert.AreEqual(0, controller.CurrentFrameIndex);
        }

        [Test]
        public void Update_OneFrameDuration_AdvancesOneFrame()
        {
            var controller = new PostGameHighlightReplayController(
                new[] { CreateClip(10, 3) });

            controller.Update(0.033f);

            Assert.AreEqual(1, controller.CurrentFrameIndex);
        }

        [Test]
        public void TogglePlaying_Paused_DoesNotAdvance()
        {
            var controller = new PostGameHighlightReplayController(
                new[] { CreateClip(10, 3) });

            controller.TogglePlaying();
            controller.Update(1f);

            Assert.IsFalse(controller.IsPlaying);
            Assert.AreEqual(0, controller.CurrentFrameIndex);
        }

        [Test]
        public void Update_EndOfFirstClip_StartsNextClip()
        {
            var controller = new PostGameHighlightReplayController(
                new[] { CreateClip(10, 2), CreateClip(30, 2) });

            controller.Update(0.066f);

            Assert.AreEqual(1, controller.CurrentClipIndex);
            Assert.AreEqual(0, controller.CurrentFrameIndex);
            Assert.IsTrue(controller.IsPlaying);
        }

        [Test]
        public void Update_EndOfLastClip_StopsOnLastFrame()
        {
            var controller = new PostGameHighlightReplayController(
                new[] { CreateClip(10, 2) });

            controller.Update(0.066f);

            Assert.IsFalse(controller.IsPlaying);
            Assert.AreEqual(1, controller.CurrentFrameIndex);
        }

        [Test]
        public void RestartPreviousNext_UseClipBoundaries()
        {
            var controller = new PostGameHighlightReplayController(
                new[] { CreateClip(10, 3), CreateClip(30, 3) });

            controller.Next();
            Assert.AreEqual(1, controller.CurrentClipIndex);
            controller.Update(0.033f);
            controller.Restart();
            Assert.AreEqual(0, controller.CurrentFrameIndex);
            controller.Previous();
            Assert.AreEqual(0, controller.CurrentClipIndex);
            Assert.IsTrue(controller.IsPlaying);
        }

        [Test]
        public void TryGetSample_HalfFrame_ReturnsAdjacentSnapshotsAndAlpha()
        {
            var controller = new PostGameHighlightReplayController(
                new[] { CreateClip(10, 3) });
            controller.Update(0.0165f);

            bool available = controller.TryGetSample(
                out FrameSnapshot from,
                out FrameSnapshot to,
                out float alpha);

            Assert.IsTrue(available);
            Assert.AreEqual(10, from.frameID);
            Assert.AreEqual(11, to.frameID);
            Assert.AreEqual(0.5f, alpha, 0.001f);
        }

        [Test]
        public void Constructor_WithoutClips_ProvidesNoSample()
        {
            var controller = new PostGameHighlightReplayController(
                new HighlightClip[0]);

            Assert.IsFalse(controller.IsPlaying);
            Assert.IsFalse(controller.TryGetSample(
                out _,
                out _,
                out _));
        }

        private static HighlightClip CreateClip(int startFrame, int frameCount)
        {
            var frames = new FrameSnapshot[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                frames[i] = new FrameSnapshot
                {
                    frameID = startFrame + i,
                    player1X = FixedInt.FromInt(startFrame + i)
                };
            }

            return new HighlightClip(
                startFrame,
                startFrame,
                startFrame,
                startFrame + frameCount - 1,
                0,
                frames);
        }
    }
}

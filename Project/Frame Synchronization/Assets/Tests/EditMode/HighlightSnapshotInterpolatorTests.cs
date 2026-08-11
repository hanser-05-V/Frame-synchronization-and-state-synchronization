using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class HighlightSnapshotInterpolatorTests
    {
        [Test]
        public void Interpolate_Halfway_ReturnsExactPresentationWithoutMutatingSnapshots()
        {
            var from = new FrameSnapshot
            {
                frameID = 10,
                player1X = FixedInt.Zero,
                player1Z = FixedInt.FromInt(2),
                player2X = FixedInt.FromInt(-2),
                ballPosX = FixedInt.FromInt(1),
                ballPosY = FixedInt.FromInt(2)
            };
            var to = new FrameSnapshot
            {
                frameID = 11,
                player1X = FixedInt.FromInt(10),
                player1Z = FixedInt.FromInt(4),
                player2X = FixedInt.FromInt(2),
                ballPosX = FixedInt.FromInt(5),
                ballPosY = FixedInt.FromInt(4)
            };

            HighlightPresentationSample sample =
                HighlightSnapshotInterpolator.Interpolate(from, to, 0.5f);

            Assert.AreEqual(5f, sample.player0Position.x, 0.001f);
            Assert.AreEqual(3f, sample.player0Position.z, 0.001f);
            Assert.AreEqual(0.5f, sample.player0Position.y, 0.001f);
            Assert.AreEqual(0f, sample.player1Position.x, 0.001f);
            Assert.AreEqual(3f, sample.ballPosition.x, 0.001f);
            Assert.AreEqual(3f, sample.ballPosition.y, 0.001f);
            Assert.AreEqual(0, from.player1X._raw);
            Assert.AreEqual(FixedInt.FromInt(10), to.player1X);
        }
    }
}

using System;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class CanonicalFrameTests
    {
        [Test]
        public void TryFromLocal_Player0_UsesLocalFrame()
        {
            bool mapped = CanonicalFrame.TryFromLocal(0, 1925, 317, out int frame);

            Assert.IsTrue(mapped);
            Assert.AreEqual(1925, frame);
        }

        [Test]
        public void TryFromLocal_Player1_MapsToPlayer0Timeline()
        {
            bool mapped = CanonicalFrame.TryFromLocal(1, 1620, -305, out int frame);

            Assert.IsTrue(mapped);
            Assert.AreEqual(1925, frame);
        }

        [Test]
        public void TryFromLocal_Player1BeforeAnchor_ReturnsFalse()
        {
            bool mapped = CanonicalFrame.TryFromLocal(
                1,
                10,
                int.MinValue,
                out int frame);

            Assert.IsFalse(mapped);
            Assert.AreEqual(-1, frame);
        }

        [Test]
        public void TryFromLocal_InvalidPlayerIndex_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CanonicalFrame.TryFromLocal(2, 10, 0, out _));
        }
    }
}

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

        [Test]
        public void TryToLocal_PlayerOne_RoundTripsNonZeroOffset()
        {
            Assert.IsTrue(CanonicalFrame.TryToLocal(1, 1925, -305, out int local));
            Assert.AreEqual(1620, local);
            Assert.IsTrue(CanonicalFrame.TryFromLocal(1, local, -305, out int canonical));
            Assert.AreEqual(1925, canonical);
        }

        [Test]
        public void TryToLocal_PlayerOne_RejectsOverflowAndNegativeLocalResult()
        {
            Assert.IsFalse(CanonicalFrame.TryToLocal(1, int.MaxValue, 1, out int overflow));
            Assert.AreEqual(-1, overflow);
            Assert.IsFalse(CanonicalFrame.TryToLocal(1, 0, -1, out int negative));
            Assert.AreEqual(-1, negative);
        }

        [Test]
        public void TryToLocal_InvalidPlayerIndex_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CanonicalFrame.TryToLocal(2, 10, 0, out _));
        }
    }
}

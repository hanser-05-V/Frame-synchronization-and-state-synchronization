using System;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class ResumeReadinessTests
    {
        [Test]
        public void Constructor_ValidValues_PreservesAllFields()
        {
            var readiness = new ResumeReadiness(12, 3, 9);

            Assert.AreEqual(12, readiness.LastContiguousRemoteFrameID);
            Assert.AreEqual(3, readiness.EarliestRecoverableCanonicalFrame);
            Assert.AreEqual(9, readiness.LatestLocalFrameID);
        }

        [Test]
        public void Constructor_ExactLowerBounds_AreValid()
        {
            var readiness = new ResumeReadiness(-1, 0, -1);

            Assert.AreEqual(-1, readiness.LastContiguousRemoteFrameID);
            Assert.AreEqual(0, readiness.EarliestRecoverableCanonicalFrame);
            Assert.AreEqual(-1, readiness.LatestLocalFrameID);
        }

        [TestCase(-2, 0, -1)]
        [TestCase(-1, -1, -1)]
        [TestCase(-1, 0, -2)]
        public void Constructor_ValueBelowFieldFloor_Throws(
            int lastContiguousRemoteFrameID,
            int earliestRecoverableCanonicalFrame,
            int latestLocalFrameID)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ResumeReadiness(
                    lastContiguousRemoteFrameID,
                    earliestRecoverableCanonicalFrame,
                    latestLocalFrameID));
        }
    }
}

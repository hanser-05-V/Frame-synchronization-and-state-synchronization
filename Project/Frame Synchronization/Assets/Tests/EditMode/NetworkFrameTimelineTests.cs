using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class NetworkFrameTimelineTests
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(1925)]
        public void RemoteFrameForLocal_SharedOrigin_ReturnsSameFrame(int localFrame)
        {
            Assert.AreEqual(
                localFrame,
                NetworkFrameTimeline.RemoteFrameForLocal(localFrame));
        }

        [Test]
        public void RemoteFrameOffset_SharedOrigin_IsZero()
        {
            Assert.AreEqual(0, NetworkFrameTimeline.RemoteFrameOffset);
        }

        [TestCase(false, false)]
        [TestCase(true, true)]
        public void CanStartSession_RequiresCompletedConnection(
            bool isConnected,
            bool expected)
        {
            Assert.AreEqual(expected, NetworkFrameTimeline.CanStartSession(isConnected));
        }

        [Test]
        public void CanPauseLocally_OnlyAllowsOfflineSessions()
        {
            Assert.IsTrue(NetworkFrameTimeline.CanPauseLocally(false));
            Assert.IsFalse(NetworkFrameTimeline.CanPauseLocally(true));
        }

    }
}

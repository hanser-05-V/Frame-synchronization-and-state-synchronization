using System.Net.Sockets;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class NetworkTransportSettingsTests
    {
        [Test]
        public void ConfigureLowLatency_TcpClient_EnablesNoDelay()
        {
            using (var client = new TcpClient())
            {
                client.NoDelay = false;
                NetworkTransportSettings.ConfigureLowLatency(client);

                Assert.IsTrue(client.NoDelay);
            }
        }

        [Test]
        public void CalculateRemoteArrivalGap_KnownLatestFrame_ReturnsFrameDifference()
        {
            Assert.AreEqual(
                4,
                NetworkFrameTimeline.CalculateRemoteArrivalGap(120, 116));
        }
    }
}

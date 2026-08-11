using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class RollbackRequestBufferTests
    {
        [Test]
        public void Request_MultipleErrorsBeforeTake_ReturnsEarliestErrorAndTruth()
        {
            var requests = new RollbackRequestBuffer();
            uint lateRaw = new FrameInput(3, 0)._raw;
            uint earliestRaw = new FrameInput(7, 0)._raw;

            requests.Request(12, lateRaw);
            requests.Request(8, earliestRaw);
            requests.Request(10, new FrameInput(1, 0)._raw);

            bool hasRequest = requests.TryTake(out int errorFrame, out uint correctRaw);

            Assert.IsTrue(hasRequest);
            Assert.AreEqual(8, errorFrame);
            Assert.AreEqual(earliestRaw, correctRaw);
            Assert.IsFalse(requests.TryTake(out _, out _));
        }

        [Test]
        public void Request_LaterErrorAfterEarlier_DoesNotOverwriteEarlierTruth()
        {
            var requests = new RollbackRequestBuffer();
            uint earliestRaw = new FrameInput(3, 0)._raw;

            requests.Request(4, earliestRaw);
            requests.Request(9, new FrameInput(7, 0)._raw);

            Assert.IsTrue(requests.TryTake(out int errorFrame, out uint correctRaw));
            Assert.AreEqual(4, errorFrame);
            Assert.AreEqual(earliestRaw, correctRaw);
        }

        [Test]
        public void HasRequest_TracksPendingStateUntilTake()
        {
            var requests = new RollbackRequestBuffer();

            Assert.IsFalse(requests.HasRequest);
            requests.Request(4, 123u);
            Assert.IsTrue(requests.HasRequest);
            requests.TryTake(out _, out _);
            Assert.IsFalse(requests.HasRequest);
        }
    }
}

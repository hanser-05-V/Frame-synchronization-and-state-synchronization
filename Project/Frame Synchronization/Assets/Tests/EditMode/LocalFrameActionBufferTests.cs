using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class LocalFrameActionBufferTests
    {
        [Test]
        public void Capture_NoLogicFrame_PersistsUntilNextConsume()
        {
            var buffer = new LocalFrameActionBuffer();
            buffer.Capture(10, true, false);
            buffer.Capture(11, false, false);

            FrameInput consumed = buffer.Consume(new FrameInput(3, 0));
            FrameInput next = buffer.Consume(new FrameInput(7, 0));

            Assert.IsTrue(consumed.pickupPressed);
            Assert.AreEqual(3, consumed.moveDir);
            Assert.IsFalse(next.pickupPressed);
            Assert.AreEqual(7, next.moveDir);
        }

        [Test]
        public void Capture_SameRenderFrameAfterConsume_DoesNotRequeueAction()
        {
            var buffer = new LocalFrameActionBuffer();
            buffer.Capture(20, true, false);

            FrameInput first = buffer.Consume(new FrameInput());
            buffer.Capture(20, true, false);
            FrameInput catchup = buffer.Consume(new FrameInput());

            Assert.IsTrue(first.pickupPressed);
            Assert.IsFalse(catchup.pickupPressed);
        }

        [Test]
        public void Capture_TwoActionsInOneRenderFrame_MergesActions()
        {
            var buffer = new LocalFrameActionBuffer();

            buffer.Capture(30, true, true);
            FrameInput consumed = buffer.Consume(new FrameInput(5, 0x10));

            Assert.IsTrue(consumed.pickupPressed);
            Assert.IsTrue(consumed.shootReleased);
            Assert.IsTrue(consumed.sprint);
            Assert.AreEqual(5, consumed.moveDir);
        }

        [Test]
        public void Clear_PendingActions_DropsAndSuppressesCurrentRenderFrame()
        {
            var buffer = new LocalFrameActionBuffer();
            buffer.Capture(40, true, true);

            buffer.Clear();
            buffer.Capture(40, true, true);
            FrameInput cleared = buffer.Consume(new FrameInput());
            buffer.Capture(41, true, false);
            FrameInput nextRenderFrame = buffer.Consume(new FrameInput());

            Assert.IsFalse(cleared.pickupPressed);
            Assert.IsFalse(cleared.shootReleased);
            Assert.IsTrue(nextRenderFrame.pickupPressed);
        }

        [Test]
        public void Capture_EndMatchPressed_PersistsUntilOneConsume()
        {
            var buffer = new LocalFrameActionBuffer();
            buffer.Capture(50, false, false, true);
            FrameInput consumed = buffer.Consume(new FrameInput());
            FrameInput next = buffer.Consume(new FrameInput());

            Assert.IsTrue(consumed.endMatchPressed);
            Assert.IsFalse(next.endMatchPressed);
        }
    }
}

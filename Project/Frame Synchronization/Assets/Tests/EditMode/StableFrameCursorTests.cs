using System;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class StableFrameCursorTests
    {
        [Test]
        public void CalculateStableThrough_Connected_UsesOlderAvailableFrame()
        {
            var cursor = new StableFrameCursor();

            int result = cursor.CalculateStableThrough(120, 116, true);

            Assert.AreEqual(116, result);
        }

        [Test]
        public void CalculateStableThrough_Offline_UsesLastExecutedFrame()
        {
            var cursor = new StableFrameCursor();

            int result = cursor.CalculateStableThrough(120, -1, false);

            Assert.AreEqual(120, result);
        }

        [Test]
        public void TryGetNext_MarkedFrames_AdvancesConsecutively()
        {
            var cursor = new StableFrameCursor();

            Assert.IsTrue(cursor.TryGetNext(2, out int firstFrame));
            Assert.AreEqual(0, firstFrame);
            cursor.MarkProcessed(0);

            Assert.IsTrue(cursor.TryGetNext(2, out int secondFrame));
            Assert.AreEqual(1, secondFrame);
        }

        [Test]
        public void MarkProcessed_SkipsFrame_Throws()
        {
            var cursor = new StableFrameCursor();

            Assert.Throws<InvalidOperationException>(
                () => cursor.MarkProcessed(1));
        }

        [Test]
        public void RebaseAt_LostHistory_ResumesAtFirstRetainedFrame()
        {
            var cursor = new StableFrameCursor();

            cursor.RebaseAt(120);

            Assert.IsTrue(cursor.TryGetNext(130, out int frameID));
            Assert.AreEqual(120, frameID);
        }
    }
}

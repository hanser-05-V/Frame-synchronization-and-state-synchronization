using System;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class OutboundActualHistoryTests
    {
        [Test]
        public void Record_SameFrameDifferentRaw_ReturnsConflictWithoutOverwrite()
        {
            var history = new OutboundActualHistory(4);

            Assert.AreEqual(
                OutboundHistoryDisposition.Accepted,
                history.Record(7, 0x100u));
            Assert.AreEqual(
                OutboundHistoryDisposition.ConflictingDuplicate,
                history.Record(7, 0x200u));
            Assert.IsTrue(history.TryGet(7, out uint raw));
            Assert.AreEqual(0x100u, raw);
        }

        [Test]
        public void Record_SameFrameSameRaw_ReturnsIdempotentDuplicate()
        {
            var history = new OutboundActualHistory(4);
            Assert.AreEqual(
                OutboundHistoryDisposition.Accepted,
                history.Record(3, 0xABCDEF01u));

            Assert.AreEqual(
                OutboundHistoryDisposition.IdempotentDuplicate,
                history.Record(3, 0xABCDEF01u));
            Assert.IsTrue(history.TryGet(3, out uint raw));
            Assert.AreEqual(0xABCDEF01u, raw);
        }

        [Test]
        public void Constructor_NonPositiveCapacity_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new OutboundActualHistory(0));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new OutboundActualHistory(-1));
        }

        [Test]
        public void Record_NegativeFrame_Throws()
        {
            var history = new OutboundActualHistory(4);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                history.Record(-1, 0x100u));
        }

        [Test]
        public void Record_EvictedFrame_DoesNotOverwriteNewestRingSlot()
        {
            var history = new OutboundActualHistory(256);
            for (int frameID = 0; frameID <= 256; frameID++)
            {
                Assert.AreEqual(
                    OutboundHistoryDisposition.Accepted,
                    history.Record(frameID, unchecked((uint)frameID)));
            }

            Assert.IsFalse(history.TryGet(0, out _));
            Assert.IsTrue(history.TryGet(1, out uint firstRetained));
            Assert.AreEqual(1u, firstRetained);
            Assert.IsTrue(history.TryGet(256, out uint latest));
            Assert.AreEqual(256u, latest);

            Assert.AreEqual(
                OutboundHistoryDisposition.HistoryUnavailable,
                history.Record(0, 999u));
            Assert.IsTrue(history.TryGet(256, out latest));
            Assert.AreEqual(256u, latest);
        }

        [Test]
        public void TryCopyRange_ContiguousFrames_ReturnsImmutableEightByteCopies()
        {
            var history = new OutboundActualHistory(4);
            history.Record(5, 0x11223344u);
            history.Record(6, 0xAABBCCDDu);

            Assert.IsTrue(history.TryCopyRange(5, 6, out byte[][] payloads));
            Assert.AreEqual(2, payloads.Length);
            CollectionAssert.AreEqual(
                new byte[] { 0x44, 0x33, 0x22, 0x11, 0x05, 0x00, 0x00, 0x00 },
                payloads[0]);
            CollectionAssert.AreEqual(
                new byte[] { 0xDD, 0xCC, 0xBB, 0xAA, 0x06, 0x00, 0x00, 0x00 },
                payloads[1]);

            payloads[0][0] = 0xFF;

            Assert.IsTrue(history.TryCopyRange(5, 5, out byte[][] reread));
            CollectionAssert.AreEqual(
                new byte[] { 0x44, 0x33, 0x22, 0x11, 0x05, 0x00, 0x00, 0x00 },
                reread[0]);
        }

        [Test]
        public void TryCopyRange_AnyMissingFrame_RejectsWholeRange()
        {
            var history = new OutboundActualHistory(4);
            history.Record(10, 0x10u);
            history.Record(12, 0x12u);

            Assert.IsFalse(history.TryCopyRange(10, 12, out byte[][] payloads));
            Assert.IsEmpty(payloads);
        }

        [Test]
        public void Record_FutureJump_EvictsFramesOutsideLogicalWindow()
        {
            var history = new OutboundActualHistory(4);
            history.Record(0, 0x10u);

            history.Record(10, 0x20u);

            Assert.IsFalse(history.TryGet(0, out _));
            Assert.IsFalse(history.TryCopyRange(0, 0, out byte[][] payloads));
            Assert.IsEmpty(payloads);
            Assert.IsTrue(history.TryGet(10, out uint latest));
            Assert.AreEqual(0x20u, latest);
        }
    }
}

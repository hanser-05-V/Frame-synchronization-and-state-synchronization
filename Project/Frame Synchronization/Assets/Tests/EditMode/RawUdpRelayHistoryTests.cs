using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public sealed class RawUdpRelayHistoryTests
    {
        [Test]
        public void RelayHistory_FirstValueIsImmutableAndConflictPreservesFirstRaw()
        {
            var history = new RawUdpRelayHistory(6);

            Assert.AreEqual(
                RawUdpInputDisposition.Accepted,
                history.Remember(new RawUdpInputEntry(10u, 0)));
            Assert.AreEqual(
                RawUdpInputDisposition.Conflict,
                history.Remember(new RawUdpInputEntry(99u, 0)));

            RawUdpInputEntry[] rebuilt = history.RebuildWindow(
                Window(0u, 0, Entry(99u, 0)));
            Assert.AreEqual(10u, rebuilt[0].Raw);
        }

        [Test]
        public void RelayHistory_CleanSteadyStateCreatesOneDownlinkPerNewUpstreamSequence()
        {
            var history = new RawUdpRelayHistory(3);
            int datagrams = 0;

            for (int frameID = 0; frameID < 8; frameID++)
            {
                RawUdpInputWindow window = ConsecutiveWindow(3, (uint)frameID, frameID);
                RememberNewEntries(history, window);
                RawUdpInputEntry[] rebuilt = history.RebuildWindow(window);
                int copies = history.GetRelayCopyCount(
                    NewestOnly(rebuilt),
                    frameID - 1);
                datagrams += copies;
                RecordCopies(history, rebuilt, copies);
            }

            Assert.AreEqual(8, datagrams);
        }

        [Test]
        public void RelayHistory_NormalAdvanceGivesEachFrameNWindowsBeforeItLeavesTheSlidingWindow()
        {
            var history = new RawUdpRelayHistory(6);

            for (int latest = 0; latest <= 10; latest++)
            {
                RawUdpInputWindow window = ConsecutiveWindow(6, (uint)latest, latest);
                RememberNewEntries(history, window);
                RawUdpInputEntry[] rebuilt = history.RebuildWindow(window);
                RecordCopies(history, rebuilt, 1);
            }

            Assert.AreEqual(6, history.GetDownlinkOpportunityCount(5));
        }

        [Test]
        public void RelayHistory_LateFWindowAfterFPlus1ThroughFPlus6GivesFExactlyNBoundedOpportunities()
        {
            var history = new RawUdpRelayHistory(6);
            for (int frameID = 1; frameID <= 6; frameID++)
                history.Remember(Entry((uint)(100 + frameID), frameID));

            RawUdpInputWindow lateWindow = ConsecutiveWindow(6, 50u, 5);
            RawUdpInputEntry late = lateWindow.Entries[0];
            Assert.AreEqual(RawUdpInputDisposition.Accepted, history.Remember(late));
            RawUdpInputEntry[] rebuilt = history.RebuildWindow(lateWindow);
            int copies = history.GetRelayCopyCount(new[] { late }, 6);
            RecordCopies(history, rebuilt, copies);

            Assert.AreEqual(6, copies);
            Assert.AreEqual(6, history.GetDownlinkOpportunityCount(0));
        }

        [Test]
        public void RelayHistory_OneUpstreamWindowNeverCreatesMoreThanNDownlinkDatagrams()
        {
            var history = new RawUdpRelayHistory(6);
            RawUdpInputWindow window = ConsecutiveWindow(6, 1u, 5);
            RememberNewEntries(history, window);

            Assert.LessOrEqual(
                history.GetRelayCopyCount(window.Entries, 20),
                6);
        }

        [Test]
        public void RelayHistory_NewSequenceSameWindowRelaysTailButDuplicateSequenceDoesNot()
        {
            var history = new RawUdpRelayHistory(2);
            var sequences = new RawUdpPacketSequenceWindow();
            RawUdpInputWindow window = ConsecutiveWindow(2, 7u, 1);
            byte[] payload = RawUdpProtocolCodec.EncodeInput(
                window.PlayerIndex,
                2,
                window.PacketSequence,
                window.LatestFrameID,
                window.Entries);
            RememberNewEntries(history, window);

            Assert.AreEqual(
                RawUdpSequenceDisposition.Accepted,
                sequences.Observe(7u, payload));
            Assert.AreEqual(1, history.GetRelayCopyCount(new RawUdpInputEntry[0], 1));
            Assert.AreEqual(
                RawUdpSequenceDisposition.Duplicate,
                sequences.Observe(7u, payload));

            byte[] tailPayload = RawUdpProtocolCodec.EncodeInput(
                0,
                2,
                8u,
                1,
                window.Entries);
            Assert.AreEqual(
                RawUdpSequenceDisposition.Accepted,
                sequences.Observe(8u, tailPayload));
            Assert.AreEqual(1, history.GetRelayCopyCount(new RawUdpInputEntry[0], 1));
        }

        [Test]
        public void RelayHistory_DownlinkSequencesAreIndependentPerReceiverAndWrapSafely()
        {
            var history = new RawUdpRelayHistory(1, uint.MaxValue, 40u);

            Assert.AreEqual(uint.MaxValue, history.TakeNextDownlinkSequence(0));
            Assert.AreEqual(0u, history.TakeNextDownlinkSequence(0));
            Assert.AreEqual(40u, history.TakeNextDownlinkSequence(1));
            Assert.AreEqual(41u, history.TakeNextDownlinkSequence(1));
        }

        [Test]
        public void RelayHistory_256CapacityReturnsTooOldOrCapacityExceededWithoutOverwrite()
        {
            var history = new RawUdpRelayHistory(1);
            Assert.AreEqual(
                RawUdpInputDisposition.CapacityExceeded,
                history.Remember(Entry(300u, 300)));

            for (int frameID = 0; frameID <= 256; frameID++)
            {
                Assert.AreEqual(
                    RawUdpInputDisposition.Accepted,
                    history.Remember(Entry((uint)frameID, frameID)));
            }

            Assert.AreEqual(
                RawUdpInputDisposition.TooOld,
                history.Remember(Entry(999u, 0)));
            Assert.IsFalse(history.TryGetRaw(0, out _));
            Assert.IsTrue(history.TryGetRaw(256, out uint newest));
            Assert.AreEqual(256u, newest);
        }

        [Test]
        public void RelayHistory_BoundaryWindowUsesSourceForAlreadyPurgedPrefix()
        {
            var history = new RawUdpRelayHistory(6);
            for (int frameID = 40; frameID <= 300; frameID++)
                history.Remember(Entry((uint)(1000 + frameID), frameID));

            RawUdpInputWindow canonical = ConsecutiveWindow(6, 400u, 45);
            RawUdpInputEntry[] conflictingEntries = canonical.Entries;
            conflictingEntries[conflictingEntries.Length - 1] =
                Entry(999999u, 45);
            var source = new RawUdpInputWindow(
                0,
                400u,
                45,
                conflictingEntries);
            RawUdpInputEntry[] rebuilt = history.RebuildWindow(source);

            Assert.AreEqual(6, rebuilt.Length);
            for (int index = 0; index < rebuilt.Length - 1; index++)
            {
                Assert.AreEqual(source.Entries[index].FrameID, rebuilt[index].FrameID);
                Assert.AreEqual(source.Entries[index].Raw, rebuilt[index].Raw);
            }
            Assert.AreEqual(45, rebuilt[rebuilt.Length - 1].FrameID);
            Assert.AreEqual(1045u, rebuilt[rebuilt.Length - 1].Raw);
        }

        private static RawUdpInputEntry Entry(uint raw, int frameID)
        {
            return new RawUdpInputEntry(raw, frameID);
        }

        private static RawUdpInputEntry[] NewestOnly(RawUdpInputEntry[] entries)
        {
            return new[] { entries[entries.Length - 1] };
        }

        private static RawUdpInputWindow Window(
            uint sequence,
            int latestFrameID,
            params RawUdpInputEntry[] entries)
        {
            return new RawUdpInputWindow(0, sequence, latestFrameID, entries);
        }

        private static RawUdpInputWindow ConsecutiveWindow(
            int windowSize,
            uint sequence,
            int latestFrameID)
        {
            int count = System.Math.Min(windowSize, latestFrameID + 1);
            var entries = new RawUdpInputEntry[count];
            int first = latestFrameID - count + 1;
            for (int index = 0; index < count; index++)
            {
                int frameID = first + index;
                entries[index] = Entry((uint)(1000 + frameID), frameID);
            }
            return Window(sequence, latestFrameID, entries);
        }

        private static void RememberNewEntries(
            RawUdpRelayHistory history,
            RawUdpInputWindow window)
        {
            RawUdpInputEntry[] entries = window.Entries;
            for (int index = 0; index < entries.Length; index++)
            {
                RawUdpInputDisposition disposition = history.Remember(entries[index]);
                Assert.IsTrue(
                    disposition == RawUdpInputDisposition.Accepted ||
                    disposition == RawUdpInputDisposition.Idempotent);
            }
        }

        private static void RecordCopies(
            RawUdpRelayHistory history,
            RawUdpInputEntry[] entries,
            int copies)
        {
            for (int copy = 0; copy < copies; copy++)
                history.RecordDownlinkOpportunityForEveryEntryInWindow(entries);
        }
    }
}

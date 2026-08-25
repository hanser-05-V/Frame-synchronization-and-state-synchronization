using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class StableRemoteFrameGateTests
    {
        [Test]
        public void StageResolvedThrough_BeforeCorrectionCommit_DoesNotAdvanceStableFrame()
        {
            var gate = new StableRemoteFrameGate();

            gate.StageResolvedThrough(12);

            Assert.AreEqual(-1, gate.ValidatedThroughFrame);
        }

        [Test]
        public void CommitCorrection_AfterRollback_AdvancesToResolvedFrame()
        {
            var gate = new StableRemoteFrameGate();
            gate.StageResolvedThrough(12);

            gate.CommitCorrection();

            Assert.AreEqual(12, gate.ValidatedThroughFrame);
        }

        [Test]
        public void FailedCorrection_DoesNotPublishStagedFrame()
        {
            var gate = new StableRemoteFrameGate();
            gate.StageResolvedThrough(12);

            gate.RejectCorrection();

            Assert.AreEqual(-1, gate.ValidatedThroughFrame);
        }

        [Test]
        public void OlderResolve_AfterCommit_DoesNotMoveBackward()
        {
            var gate = new StableRemoteFrameGate();
            gate.StageResolvedThrough(12);
            gate.CommitCorrection();

            gate.StageResolvedThrough(8);
            gate.CommitCorrection();

            Assert.AreEqual(12, gate.ValidatedThroughFrame);
        }

        [Test]
        public void ConfirmedCanonicalFrame_MappedToPlayerOneLocalFrame_PublishesAfterCorrection()
        {
            const int canonicalFrame = 42;
            const int offset = 17;
            Assert.IsTrue(CanonicalFrame.TryToLocal(
                1,
                canonicalFrame,
                offset,
                out int localFrame));

            var gate = new StableRemoteFrameGate();
            gate.StageResolvedThrough(localFrame);

            Assert.AreEqual(-1, gate.ValidatedThroughFrame);
            gate.CommitCorrection();
            Assert.AreEqual(59, gate.ValidatedThroughFrame);
        }
    }
}

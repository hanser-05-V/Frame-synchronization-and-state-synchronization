using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class DeterministicNetworkFaultModelTests
    {
        [Test]
        public void Profile_InvalidRanges_Throw()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CreateProfile(
                baseDelayMs: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => CreateProfile(
                jitterMs: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => CreateProfile(
                applicationReorderPercent: 101));
            Assert.Throws<ArgumentOutOfRangeException>(() => CreateProfile(
                duplicatePercent: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => CreateProfile(
                recoveredLossPercent: 101));
            Assert.Throws<ArgumentOutOfRangeException>(() => CreateProfile(
                reorderExtraDelayMs: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => CreateProfile(
                lossRecoveryDelayMs: -1));
        }

        [Test]
        public void Decide_Fixed100_AssignsExactlyOneHundredMilliseconds()
        {
            NetworkLabProfile profile = CreateProfile(baseDelayMs: 100);

            DeterministicNetworkFaultModel.Decision decision =
                DeterministicNetworkFaultModel.Decide(
                    profile,
                    0,
                    3,
                    42,
                    0x1234u);

            Assert.AreEqual(100, decision.EffectiveDelayMs);
            Assert.IsFalse(decision.ApplicationReordered);
            Assert.IsFalse(decision.Duplicated);
            Assert.IsFalse(decision.LossRecovered);
        }

        [Test]
        public void Decide_InvalidSenderIndex_Throws()
        {
            NetworkLabProfile profile = CreateProfile();

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                DeterministicNetworkFaultModel.Decide(
                    profile,
                    -1,
                    0,
                    0,
                    0u));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                DeterministicNetworkFaultModel.Decide(
                    profile,
                    2,
                    0,
                    0,
                    0u));
        }

        [Test]
        public void Decide_SameSeedAndScript_ProducesIdenticalTrace()
        {
            NetworkLabProfile profile = CreateFaultProfile(0xA55A1234u);

            List<string> first = BuildTrace(profile, 256);
            List<string> second = BuildTrace(profile, 256);

            CollectionAssert.AreEqual(first, second);
        }

        [Test]
        public void Decide_DifferentSeeds_ChangeAtLeastOneDecision()
        {
            List<string> first = BuildTrace(
                CreateFaultProfile(0x11111111u),
                512);
            List<string> second = BuildTrace(
                CreateFaultProfile(0x22222222u),
                512);

            CollectionAssert.AreNotEqual(first, second);
        }

        [Test]
        public void Decide_AllFaultPercentagesOneHundred_ComposesDelaysAndFlags()
        {
            NetworkLabProfile profile = new NetworkLabProfile(
                100,
                0,
                100,
                40,
                100,
                100,
                200,
                7u);

            DeterministicNetworkFaultModel.Decision decision =
                DeterministicNetworkFaultModel.Decide(
                    profile,
                    1,
                    12,
                    9,
                    0xABCDu);

            Assert.AreEqual(340, decision.EffectiveDelayMs);
            Assert.IsTrue(decision.ApplicationReordered);
            Assert.IsTrue(decision.Duplicated);
            Assert.IsTrue(decision.LossRecovered);
        }

        [Test]
        public void Decide_JitterNeverMakesEffectiveDelayNegative()
        {
            NetworkLabProfile profile = new NetworkLabProfile(
                10,
                50,
                0,
                0,
                0,
                0,
                0,
                123u);

            for (int frame = 0; frame < 512; frame++)
            {
                DeterministicNetworkFaultModel.Decision decision =
                    DeterministicNetworkFaultModel.Decide(
                        profile,
                        frame & 1,
                        frame,
                        frame,
                        (uint)frame);

                Assert.GreaterOrEqual(decision.EffectiveDelayMs, 0);
                Assert.LessOrEqual(decision.EffectiveDelayMs, 60);
            }
        }

        [Test]
        public void CanonicalTraceLine_ContainsNoWallClockFields()
        {
            NetworkLabProfile profile = CreateFaultProfile(19u);
            DeterministicNetworkFaultModel.Decision decision =
                DeterministicNetworkFaultModel.Decide(
                    profile,
                    0,
                    4,
                    7,
                    0x12345678u);

            string line = decision.ToCanonicalTraceLine(
                0,
                4,
                7,
                0x12345678u);

            StringAssert.Contains("sender=0", line);
            StringAssert.Contains("sequence=4", line);
            StringAssert.Contains("frame=7", line);
            StringAssert.DoesNotContain("timestamp", line.ToLowerInvariant());
            StringAssert.DoesNotContain("utc", line.ToLowerInvariant());
            StringAssert.DoesNotContain("enqueue", line.ToLowerInvariant());
            StringAssert.DoesNotContain("due", line.ToLowerInvariant());
            StringAssert.DoesNotContain("sentat", line.ToLowerInvariant());
            StringAssert.DoesNotContain("sendtime", line.ToLowerInvariant());
        }

        private static NetworkLabProfile CreateProfile(
            int baseDelayMs = 0,
            int jitterMs = 0,
            int applicationReorderPercent = 0,
            int reorderExtraDelayMs = 0,
            int duplicatePercent = 0,
            int recoveredLossPercent = 0,
            int lossRecoveryDelayMs = 0,
            uint seed = 1u)
        {
            return new NetworkLabProfile(
                baseDelayMs,
                jitterMs,
                applicationReorderPercent,
                reorderExtraDelayMs,
                duplicatePercent,
                recoveredLossPercent,
                lossRecoveryDelayMs,
                seed);
        }

        private static NetworkLabProfile CreateFaultProfile(uint seed)
        {
            return new NetworkLabProfile(
                100,
                30,
                35,
                70,
                25,
                20,
                180,
                seed);
        }

        private static List<string> BuildTrace(
            NetworkLabProfile profile,
            int frameCount)
        {
            var trace = new List<string>(frameCount);
            for (int frame = 0; frame < frameCount; frame++)
            {
                int sender = frame & 1;
                uint raw = (uint)(0x1000 + frame * 17);
                DeterministicNetworkFaultModel.Decision decision =
                    DeterministicNetworkFaultModel.Decide(
                        profile,
                        sender,
                        frame,
                        frame,
                        raw);
                trace.Add(decision.ToCanonicalTraceLine(
                    sender,
                    frame,
                    frame,
                    raw));
            }

            return trace;
        }
    }
}

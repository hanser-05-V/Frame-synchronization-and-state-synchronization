using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public sealed class RawUdpDatagramFaultModelTests
    {
        [Test]
        public void SameSeedScriptIdentity_ProducesByteIdenticalDecisionTrace()
        {
            List<string> first = BuildTrace(20260820u);
            List<string> second = BuildTrace(20260820u);

            CollectionAssert.AreEqual(first, second);
        }

        [Test]
        public void SelectedDifferentSeeds_DivergeAcrossSufficientSamples()
        {
            CollectionAssert.AreNotEqual(
                BuildTrace(20260820u),
                BuildTrace(20260821u));
        }

        [Test]
        public void ZeroPercentAndBoundaryValues_ProduceLockedResults()
        {
            var clean = new RawUdpDatagramFaultProfile(
                0, 0, 0, 0, 0, 0, 20260820u);
            RawUdpDatagramFaultDecision cleanDecision = Decide(clean, 3u);
            Assert.IsFalse(cleanDecision.Dropped);
            Assert.AreEqual(0, cleanDecision.EffectiveDueTimeOffsetMs);
            Assert.AreEqual(0, cleanDecision.ReorderExtraDelayMs);
            Assert.AreEqual(1, cleanDecision.CopyCount);

            var boundary = new RawUdpDatagramFaultProfile(
                5000, 5000, 100, 100, 5000, 100, 20260820u);
            RawUdpDatagramFaultDecision boundaryDecision = Decide(boundary, 3u);
            Assert.IsTrue(boundaryDecision.Dropped);
            Assert.AreEqual(0, boundaryDecision.CopyCount);
            Assert.GreaterOrEqual(boundaryDecision.EffectiveDueTimeOffsetMs, 0);
            Assert.LessOrEqual(boundaryDecision.EffectiveDueTimeOffsetMs, 15000);
        }

        [Test]
        public void DropDeletesDatagramWithoutSyntheticRecovery()
        {
            var profile = new RawUdpDatagramFaultProfile(
                20, 5, 100, 0, 0, 100, 1u);
            RawUdpDatagramFaultDecision decision = Decide(profile, 11u);

            Assert.IsTrue(decision.Dropped);
            Assert.AreEqual(0, decision.CopyCount);
            StringAssert.DoesNotContain(
                "recover",
                decision.ToCanonicalTraceLine(
                    0,
                    0x1122334455667788UL,
                    RouteCMessageType.RawInput,
                    0,
                    11u).ToLowerInvariant());
        }

        [Test]
        public void DuplicateProducesAtMostOneAdditionalCopy()
        {
            var profile = new RawUdpDatagramFaultProfile(
                0, 0, 0, 0, 0, 100, 1u);
            for (uint sequence = 0; sequence < 1000; sequence++)
                Assert.AreEqual(2, Decide(profile, sequence).CopyCount);
        }

        [Test]
        public void ControlOrdinalAndRawPacketSequenceUseSeparateStableIdentities()
        {
            var profile = FaultyProfile(20260820u);
            RawUdpDatagramFaultDecision input = RawUdpDatagramFaultModel.Decide(
                in profile,
                0,
                0x1122334455667788UL,
                RouteCMessageType.RawInput,
                0,
                7u);
            RawUdpDatagramFaultDecision control = RawUdpDatagramFaultModel.Decide(
                in profile,
                0,
                0x1122334455667788UL,
                RouteCMessageType.RawReady,
                0,
                7u);

            Assert.AreNotEqual(
                input.ToCanonicalTraceLine(0, 0x1122334455667788UL,
                    RouteCMessageType.RawInput, 0, 7u),
                control.ToCanonicalTraceLine(0, 0x1122334455667788UL,
                    RouteCMessageType.RawReady, 0, 7u));
        }

        [Test]
        public void ProfileRejectsPercentOutside0To100AndTimeOutside0To5000ms()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new RawUdpDatagramFaultProfile(-1, 0, 0, 0, 0, 0, 1u));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new RawUdpDatagramFaultProfile(5001, 0, 0, 0, 0, 0, 1u));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new RawUdpDatagramFaultProfile(0, 5001, 0, 0, 0, 0, 1u));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new RawUdpDatagramFaultProfile(0, 0, -1, 0, 0, 0, 1u));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new RawUdpDatagramFaultProfile(0, 0, 0, 101, 0, 0, 1u));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new RawUdpDatagramFaultProfile(0, 0, 0, 0, 5001, 0, 1u));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new RawUdpDatagramFaultProfile(0, 0, 0, 0, 0, 101, 1u));
        }

        [Test]
        public void TraceLabelsUseRawUdpDatagramPrefixAndExcludeTcpKcpRecoveryLabels()
        {
            RawUdpDatagramFaultProfile profile = FaultyProfile(20260820u);
            string trace = Decide(profile, 9u).ToCanonicalTraceLine(
                1,
                0x0102030405060708UL,
                RouteCMessageType.RawInput,
                1,
                9u);

            StringAssert.Contains("raw-udp-datagram-delay", trace);
            StringAssert.Contains("raw-udp-datagram-jitter", trace);
            StringAssert.Contains("raw-udp-datagram-drop", trace);
            StringAssert.Contains("raw-udp-datagram-reorder", trace);
            StringAssert.Contains("raw-udp-datagram-duplicate", trace);
            StringAssert.DoesNotContain("recovered-loss", trace);
            StringAssert.DoesNotContain("hol", trace.ToLowerInvariant());
            StringAssert.DoesNotContain("kcp", trace.ToLowerInvariant());
        }

        private static RawUdpDatagramFaultDecision Decide(
            RawUdpDatagramFaultProfile profile,
            uint identity)
        {
            return RawUdpDatagramFaultModel.Decide(
                in profile,
                0,
                0x1122334455667788UL,
                RouteCMessageType.RawInput,
                0,
                identity);
        }

        private static RawUdpDatagramFaultProfile FaultyProfile(uint seed)
        {
            return new RawUdpDatagramFaultProfile(
                70, 25, 30, 35, 60, 25, seed);
        }

        private static List<string> BuildTrace(uint seed)
        {
            RawUdpDatagramFaultProfile profile = FaultyProfile(seed);
            var lines = new List<string>();
            for (uint sequence = 0; sequence < 512; sequence++)
            {
                byte direction = (byte)(sequence & 1u);
                RouteCMessageType type = sequence % 7u == 0u
                    ? RouteCMessageType.RawReady
                    : RouteCMessageType.RawInput;
                RawUdpDatagramFaultDecision decision =
                    RawUdpDatagramFaultModel.Decide(
                        in profile,
                        direction,
                        0x1122334455667788UL,
                        type,
                        (int)(sequence & 1u),
                        sequence);
                lines.Add(decision.ToCanonicalTraceLine(
                    direction,
                    0x1122334455667788UL,
                    type,
                    (int)(sequence & 1u),
                    sequence));
            }
            return lines;
        }
    }
}

using System;
using System.Reflection;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public sealed class KcpUdpVirtualScenarioMatrixTests
    {
        [Test]
        public void ProtocolDriver_ExposesBoundedScenarioEntryPoint()
        {
            Type driverType = typeof(KcpUdpVirtualScenarioDriver);
            Type kindType = driverType.GetNestedType(
                "ProtocolScenarioKind",
                BindingFlags.Public);
            Type resultType = driverType.GetNestedType(
                "ProtocolResult",
                BindingFlags.Public);

            Assert.IsNotNull(kindType, "The formal protocol scenario enum is missing.");
            Assert.IsNotNull(resultType, "The auditable protocol result is missing.");
            MethodInfo run = driverType.GetMethod(
                "RunProtocolScenario",
                BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(run, "The bounded protocol scenario entry point is missing.");
            Assert.AreEqual(resultType, run.ReturnType);
        }

        [Test]
        public void ProtocolResult_ExposesOutcomeBoundsAndDecisionEvidence()
        {
            Type driverType = typeof(KcpUdpVirtualScenarioDriver);
            Assert.IsNotNull(driverType.GetNestedType(
                "ProtocolOutcome",
                BindingFlags.Public));
            Type resultType = typeof(KcpUdpVirtualScenarioDriver.ProtocolResult);
            string[] propertyNames =
            {
                "Outcome",
                "FinalState",
                "TerminatedWithinBound",
                "TerminationTimeMs",
                "StepCount",
                "HelloSentCount",
                "WelcomeSentCount",
                "ReadySentCount",
                "StartSentCount",
                "ReconnectWelcomeSentCount",
                "ResumeAcceptedSentCount",
                "ResumeCompleteSentCount",
                "DroppedDatagramCount",
                "ReorderedDatagramCount",
                "DuplicateDatagramCount",
                "NonZeroJitterCount",
                "TraceLines",
                "ClientObservedResumeRejected"
            };

            for (int index = 0; index < propertyNames.Length; index++)
            {
                Assert.IsNotNull(
                    resultType.GetProperty(propertyNames[index]),
                    propertyNames[index] + " is missing.");
            }
        }

        [TestCase("clean")]
        [TestCase("fixed-delay")]
        [TestCase("jitter")]
        [TestCase("reorder")]
        [TestCase("duplicate")]
        [TestCase("asymmetric")]
        [TestCase("hello-loss")]
        [TestCase("welcome-loss")]
        [TestCase("ready-loss")]
        [TestCase("start-loss")]
        public void InitialHandshakeMatrix_TerminatesRunningWithinBothBounds(
            string scenario)
        {
            CreateInitialProfiles(
                scenario,
                out UdpDatagramFaultProfile uplink,
                out UdpDatagramFaultProfile downlink);

            KcpUdpVirtualScenarioDriver.ProtocolResult result =
                KcpUdpVirtualScenarioDriver.RunProtocolScenario(
                    KcpUdpVirtualScenarioDriver.ProtocolScenarioKind.InitialHandshake,
                    uplink,
                    downlink,
                    2000,
                    4000);

            WriteProtocolResult(scenario, result);

            Assert.IsTrue(result.TerminatedWithinBound, scenario);
            Assert.AreEqual(
                KcpUdpVirtualScenarioDriver.ProtocolOutcome.Running,
                result.Outcome,
                scenario);
            Assert.AreEqual(NetworkSessionState.Running, result.FinalState, scenario);
            Assert.That(result.TerminationTimeMs, Is.InRange(0, 2000), scenario);
            Assert.That(result.StepCount, Is.InRange(1, 4000), scenario);
            Assert.GreaterOrEqual(result.HelloSentCount, 1, scenario);
            Assert.GreaterOrEqual(result.WelcomeSentCount, 1, scenario);
            Assert.GreaterOrEqual(result.ReadySentCount, 1, scenario);
            Assert.GreaterOrEqual(result.StartSentCount, 1, scenario);

            if (scenario.EndsWith("loss", StringComparison.Ordinal))
                Assert.Greater(result.DroppedDatagramCount, 0, scenario);
            if (scenario == "jitter")
                Assert.Greater(result.NonZeroJitterCount, 0, scenario);
            if (scenario == "reorder")
                Assert.Greater(result.ReorderedDatagramCount, 0, scenario);
            if (scenario == "duplicate")
                Assert.Greater(result.DuplicateDatagramCount, 0, scenario);
            if (scenario == "hello-loss")
                Assert.GreaterOrEqual(result.HelloSentCount, 2, scenario);
            if (scenario == "welcome-loss")
                Assert.GreaterOrEqual(result.WelcomeSentCount, 2, scenario);
            if (scenario == "ready-loss")
                Assert.GreaterOrEqual(result.ReadySentCount, 2, scenario);
            if (scenario == "start-loss")
                Assert.GreaterOrEqual(result.StartSentCount, 2, scenario);
        }

        [Test]
        public void ReconnectWelcomeLoss_RetriesThenTerminatesRunning()
        {
            UdpDatagramFaultProfile uplink = CleanProfile();
            UdpDatagramFaultProfile downlink = FindReconnectWelcomeLossProfile();

            KcpUdpVirtualScenarioDriver.ProtocolResult result =
                KcpUdpVirtualScenarioDriver.RunProtocolScenario(
                    KcpUdpVirtualScenarioDriver.ProtocolScenarioKind.ReconnectWelcomeLoss,
                    uplink,
                    downlink,
                    6000,
                    12000);

            WriteProtocolResult("reconnect-welcome-loss", result);

            Assert.IsTrue(result.TerminatedWithinBound);
            Assert.AreEqual(
                KcpUdpVirtualScenarioDriver.ProtocolOutcome.Running,
                result.Outcome);
            Assert.AreEqual(NetworkSessionState.Running, result.FinalState);
            Assert.GreaterOrEqual(result.ReconnectWelcomeSentCount, 2);
            Assert.Greater(result.DroppedDatagramCount, 0);
            Assert.That(result.TerminationTimeMs, Is.InRange(3000, 6000));
            Assert.That(result.StepCount, Is.InRange(1, 12000));
        }

        [Test]
        public void ResumeControlLoss_RetriesThenTerminatesRunning()
        {
            UdpDatagramFaultProfile uplink = CleanProfile();
            UdpDatagramFaultProfile downlink = FindResumeAcceptedLossProfile();

            KcpUdpVirtualScenarioDriver.ProtocolResult result =
                KcpUdpVirtualScenarioDriver.RunProtocolScenario(
                    KcpUdpVirtualScenarioDriver.ProtocolScenarioKind.ResumeControlLoss,
                    uplink,
                    downlink,
                    6000,
                    12000);

            WriteProtocolResult("resume-control-loss", result);

            Assert.IsTrue(result.TerminatedWithinBound);
            Assert.AreEqual(
                KcpUdpVirtualScenarioDriver.ProtocolOutcome.Running,
                result.Outcome);
            Assert.AreEqual(NetworkSessionState.Running, result.FinalState);
            Assert.GreaterOrEqual(result.ResumeAcceptedSentCount, 2);
            Assert.GreaterOrEqual(result.ResumeCompleteSentCount, 1);
            Assert.Greater(result.DroppedDatagramCount, 0);
        }

        [Test]
        public void UnsafeResumeControl_TerminatesAsExplicitResumeRejected()
        {
            UdpDatagramFaultProfile clean = CleanProfile();

            KcpUdpVirtualScenarioDriver.ProtocolResult result =
                KcpUdpVirtualScenarioDriver.RunProtocolScenario(
                    KcpUdpVirtualScenarioDriver.ProtocolScenarioKind.ResumeControlRejected,
                    clean,
                    clean,
                    6000,
                    12000);

            WriteProtocolResult("resume-control-rejected", result);

            Assert.IsTrue(result.TerminatedWithinBound);
            Assert.AreEqual(
                KcpUdpVirtualScenarioDriver.ProtocolOutcome.ResumeRejected,
                result.Outcome);
            Assert.AreEqual(NetworkSessionState.Terminated, result.FinalState);
            Assert.IsTrue(result.ClientObservedResumeRejected);
            Assert.That(result.TerminationTimeMs, Is.InRange(3000, 6000));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void KcpDropMatrix_DataAndAckLossRecoverWithoutSyntheticEvents(
            bool dropData)
        {
            UdpDatagramFaultProfile clean = CleanProfile();
            UdpDatagramFaultProfile faulty = FindFirstDropThenPassProfile(
                dropData
                    ? UdpDatagramDirection.ClientToServer
                    : UdpDatagramDirection.ServerToClient,
                dropData ? (byte)0x51 : (byte)0x52);
            KcpUdpVirtualScenarioDriver.Result result =
                KcpUdpVirtualScenarioDriver.RunSingleMessage(
                    dropData ? faulty : clean,
                    dropData ? clean : faulty,
                    0xCAFEBABEu,
                    17,
                    1500);

            TestContext.Out.WriteLine(
                "semantic=udp-datagram-drop scenario=" +
                (dropData ? "kcp-data-drop" : "kcp-ack-drop") +
                " terminated=" + result.TerminatedWithinBound +
                " timeMs=" + result.TerminationTimeMs +
                " steps=" + result.StepCount +
                " dataDrops=" + result.DroppedDataCount +
                " ackDrops=" + result.DroppedAckCount +
                " outputs=" + result.SenderOutputCount +
                " pendingSend=" + result.SenderPendingSendCount);

            Assert.IsTrue(result.TerminatedWithinBound);
            Assert.AreEqual(1, result.DeliveryCount);
            Assert.AreEqual(dropData ? 1 : 0, result.DroppedDataCount);
            Assert.AreEqual(dropData ? 0 : 1, result.DroppedAckCount);
            Assert.GreaterOrEqual(result.SenderOutputCount, 2);
        }

        private static void CreateInitialProfiles(
            string scenario,
            out UdpDatagramFaultProfile uplink,
            out UdpDatagramFaultProfile downlink)
        {
            uplink = CleanProfile();
            downlink = CleanProfile();
            switch (scenario)
            {
                case "clean":
                    return;
                case "fixed-delay":
                    uplink = Profile(delayMs: 20, seed: 2u);
                    downlink = Profile(delayMs: 20, seed: 3u);
                    return;
                case "jitter":
                    uplink = Profile(delayMs: 20, jitterMs: 10, seed: 4u);
                    downlink = Profile(delayMs: 20, jitterMs: 10, seed: 5u);
                    return;
                case "reorder":
                    uplink = Profile(reorderPercent: 100, reorderExtraDelayMs: 30, seed: 6u);
                    downlink = Profile(reorderPercent: 100, reorderExtraDelayMs: 30, seed: 7u);
                    return;
                case "duplicate":
                    uplink = Profile(duplicatePercent: 100, seed: 8u);
                    downlink = Profile(duplicatePercent: 100, seed: 9u);
                    return;
                case "asymmetric":
                    uplink = Profile(delayMs: 5, seed: 10u);
                    downlink = Profile(delayMs: 45, jitterMs: 5, seed: 11u);
                    return;
                case "hello-loss":
                    uplink = FindInitialControlLossProfile("hello-loss");
                    return;
                case "welcome-loss":
                    downlink = FindInitialControlLossProfile("welcome-loss");
                    return;
                case "ready-loss":
                    uplink = FindInitialControlLossProfile("ready-loss");
                    return;
                case "start-loss":
                    downlink = FindInitialControlLossProfile("start-loss");
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }

        private static void WriteProtocolResult(
            string scenario,
            KcpUdpVirtualScenarioDriver.ProtocolResult result)
        {
            TestContext.Out.WriteLine(
                "semantic=udp-datagram-drop scenario=" + scenario +
                " outcome=" + result.Outcome +
                " finalState=" + result.FinalState +
                " terminated=" + result.TerminatedWithinBound +
                " timeMs=" + result.TerminationTimeMs +
                " steps=" + result.StepCount +
                " hello=" + result.HelloSentCount +
                " welcome=" + result.WelcomeSentCount +
                " ready=" + result.ReadySentCount +
                " start=" + result.StartSentCount +
                " reconnectWelcome=" + result.ReconnectWelcomeSentCount +
                " resumeAccepted=" + result.ResumeAcceptedSentCount +
                " resumeComplete=" + result.ResumeCompleteSentCount +
                " dropped=" + result.DroppedDatagramCount +
                " reordered=" + result.ReorderedDatagramCount +
                " duplicated=" + result.DuplicateDatagramCount +
                " jittered=" + result.NonZeroJitterCount +
                " observedResumeRejected=" +
                result.ClientObservedResumeRejected);
        }

        private static UdpDatagramFaultProfile FindInitialControlLossProfile(
            string scenario)
        {
            RouteCSessionId session = ScenarioSessionId();
            for (uint seed = 1u; seed <= 10000u; seed++)
            {
                UdpDatagramFaultProfile profile = Profile(dropPercent: 50, seed: seed);
                bool matches;
                switch (scenario)
                {
                    case "hello-loss":
                        matches = IsDropped(profile, UdpDatagramDirection.ClientToServer,
                            RouteCSessionId.Zero, 0UL, RouteCMessageType.Hello) &&
                            !IsDropped(profile, UdpDatagramDirection.ClientToServer,
                                RouteCSessionId.Zero, 1UL, RouteCMessageType.Hello);
                        break;
                    case "welcome-loss":
                        matches = IsDropped(profile, UdpDatagramDirection.ServerToClient,
                            session, 0UL, RouteCMessageType.Welcome) &&
                            !IsDropped(profile, UdpDatagramDirection.ServerToClient,
                                session, 1UL, RouteCMessageType.Welcome);
                        break;
                    case "ready-loss":
                        matches = !IsDropped(profile, UdpDatagramDirection.ClientToServer,
                                RouteCSessionId.Zero, 0UL, RouteCMessageType.Hello) &&
                            IsDropped(profile, UdpDatagramDirection.ClientToServer,
                                session, 1UL, RouteCMessageType.Ready) &&
                            !IsDropped(profile, UdpDatagramDirection.ClientToServer,
                                session, 2UL, RouteCMessageType.Ready);
                        break;
                    case "start-loss":
                        matches = !IsDropped(profile, UdpDatagramDirection.ServerToClient,
                                session, 0UL, RouteCMessageType.Welcome) &&
                            IsDropped(profile, UdpDatagramDirection.ServerToClient,
                                session, 1UL, RouteCMessageType.Start) &&
                            !IsDropped(profile, UdpDatagramDirection.ServerToClient,
                                session, 2UL, RouteCMessageType.Start);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(scenario));
                }
                if (matches)
                    return profile;
            }
            throw new AssertionException("No bounded initial-control seed for " + scenario);
        }

        private static UdpDatagramFaultProfile FindReconnectWelcomeLossProfile()
        {
            RouteCSessionId session = ScenarioSessionId();
            for (uint seed = 1u; seed <= 10000u; seed++)
            {
                UdpDatagramFaultProfile profile = Profile(dropPercent: 50, seed: seed);
                if (!IsDropped(profile, UdpDatagramDirection.ServerToClient,
                        session, 0UL, RouteCMessageType.Welcome) &&
                    !IsDropped(profile, UdpDatagramDirection.ServerToClient,
                        session, 1UL, RouteCMessageType.Start) &&
                    IsDropped(profile, UdpDatagramDirection.ServerToClient,
                        session, 2UL, RouteCMessageType.Welcome) &&
                    !IsDropped(profile, UdpDatagramDirection.ServerToClient,
                        session, 3UL, RouteCMessageType.Welcome) &&
                    !IsDropped(profile, UdpDatagramDirection.ServerToClient,
                        session, 4UL, RouteCMessageType.ResumeAccepted) &&
                    !IsDropped(profile, UdpDatagramDirection.ServerToClient,
                        session, 5UL, RouteCMessageType.ResumeComplete))
                {
                    return profile;
                }
            }
            throw new AssertionException("No bounded reconnect-Welcome loss seed.");
        }

        private static UdpDatagramFaultProfile FindResumeAcceptedLossProfile()
        {
            RouteCSessionId session = ScenarioSessionId();
            for (uint seed = 1u; seed <= 10000u; seed++)
            {
                UdpDatagramFaultProfile profile = Profile(dropPercent: 50, seed: seed);
                if (!IsDropped(profile, UdpDatagramDirection.ServerToClient,
                        session, 0UL, RouteCMessageType.Welcome) &&
                    !IsDropped(profile, UdpDatagramDirection.ServerToClient,
                        session, 1UL, RouteCMessageType.Start) &&
                    !IsDropped(profile, UdpDatagramDirection.ServerToClient,
                        session, 2UL, RouteCMessageType.Welcome) &&
                    IsDropped(profile, UdpDatagramDirection.ServerToClient,
                        session, 3UL, RouteCMessageType.ResumeAccepted) &&
                    !IsDropped(profile, UdpDatagramDirection.ServerToClient,
                        session, 4UL, RouteCMessageType.ResumeAccepted) &&
                    !IsDropped(profile, UdpDatagramDirection.ServerToClient,
                        session, 5UL, RouteCMessageType.ResumeComplete))
                {
                    return profile;
                }
            }
            throw new AssertionException("No bounded ResumeAccepted loss seed.");
        }

        private static UdpDatagramFaultProfile FindFirstDropThenPassProfile(
            UdpDatagramDirection direction,
            byte command)
        {
            RouteCSessionId session = ScenarioSessionId();
            byte[] envelope = KcpEnvelope(session, command, 0u);
            for (uint seed = 1u; seed <= 10000u; seed++)
            {
                UdpDatagramFaultProfile profile = Profile(dropPercent: 50, seed: seed);
                UdpDatagramFaultDecision first = UdpDatagramFaultModel.Decide(
                    in profile, direction, session, 0UL, envelope, out _);
                UdpDatagramFaultDecision second = UdpDatagramFaultModel.Decide(
                    in profile, direction, session, 1UL, envelope, out _);
                if (first.Dropped && !second.Dropped)
                    return profile;
            }
            throw new AssertionException("No bounded KCP drop seed.");
        }

        private static bool IsDropped(
            UdpDatagramFaultProfile profile,
            UdpDatagramDirection direction,
            RouteCSessionId session,
            ulong sequence,
            RouteCMessageType messageType)
        {
            byte[] envelope = RouteCProtocolCodec.Encode(
                messageType,
                session,
                session.IsZero ? 0u : 1u,
                new byte[0]);
            return UdpDatagramFaultModel.Decide(
                in profile,
                direction,
                session,
                sequence,
                envelope,
                out _).Dropped;
        }

        private static byte[] KcpEnvelope(
            RouteCSessionId session,
            byte command,
            uint sequence)
        {
            var payload = new byte[RouteCProtocolConstants.KcpHeaderSize];
            payload[0] = 0x44;
            payload[1] = 0x33;
            payload[2] = 0x22;
            payload[3] = 0x11;
            payload[4] = command;
            payload[12] = (byte)sequence;
            payload[13] = (byte)(sequence >> 8);
            payload[14] = (byte)(sequence >> 16);
            payload[15] = (byte)(sequence >> 24);
            return RouteCProtocolCodec.Encode(
                RouteCMessageType.KcpData,
                session,
                1u,
                payload);
        }

        private static UdpDatagramFaultProfile CleanProfile()
        {
            return Profile();
        }

        private static UdpDatagramFaultProfile Profile(
            int delayMs = 0,
            int jitterMs = 0,
            int dropPercent = 0,
            int reorderPercent = 0,
            int reorderExtraDelayMs = 0,
            int duplicatePercent = 0,
            uint seed = 1u)
        {
            return new UdpDatagramFaultProfile(
                delayMs,
                jitterMs,
                dropPercent,
                reorderPercent,
                reorderExtraDelayMs,
                duplicatePercent,
                seed);
        }

        private static RouteCSessionId ScenarioSessionId()
        {
            return new RouteCSessionId(
                0x1021324354657687UL,
                0x98A9BACBDCEDFE0FUL);
        }
    }
}

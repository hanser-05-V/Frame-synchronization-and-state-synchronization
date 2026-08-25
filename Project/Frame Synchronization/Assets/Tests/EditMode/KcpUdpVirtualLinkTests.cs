using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public sealed class KcpUdpVirtualLinkTests
    {
        [Test]
        public void Profile_TypeExistsInRuntimeAssembly()
        {
            Type profileType = typeof(RouteCProtocolConstants).Assembly.GetType(
                "FrameSyncDemo.UdpDatagramFaultProfile");

            Assert.IsNotNull(
                profileType,
                "Task 10 requires a reusable runtime UDP datagram fault profile.");
        }

        [Test]
        public void RuntimeSurface_DefinesTask10DatagramTypes()
        {
            Assembly runtimeAssembly = typeof(RouteCProtocolConstants).Assembly;
            string[] requiredTypeNames =
            {
                "FrameSyncDemo.UdpDatagramDirection",
                "FrameSyncDemo.UdpDatagramFaultDecision",
                "FrameSyncDemo.UdpDatagramDecisionTraceEntry",
                "FrameSyncDemo.UdpDatagramFaultModel",
                "FrameSyncDemo.UdpVirtualDatagramLink"
            };

            for (int index = 0; index < requiredTypeNames.Length; index++)
            {
                Assert.IsNotNull(
                    runtimeAssembly.GetType(requiredTypeNames[index]),
                    requiredTypeNames[index] + " is missing.");
            }
        }

        [Test]
        public void Profile_ConstructorMatchesLockedTask10Contract()
        {
            Type profileType = typeof(RouteCProtocolConstants).Assembly.GetType(
                "FrameSyncDemo.UdpDatagramFaultProfile");
            Assert.IsNotNull(profileType);

            ConstructorInfo constructor = profileType.GetConstructor(new[]
            {
                typeof(int), typeof(int), typeof(int), typeof(int),
                typeof(int), typeof(int), typeof(uint)
            });

            Assert.IsNotNull(
                constructor,
                "The seven-parameter Task 10 profile constructor is missing.");
        }

        [Test]
        public void KcpHeader_MetadataParserExists()
        {
            MethodInfo parser = typeof(RouteCKcpHeader).GetMethod(
                "TryReadCommandAndSequence",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[]
                {
                    typeof(byte[]),
                    typeof(byte).MakeByRefType(),
                    typeof(uint).MakeByRefType()
                },
                null);

            Assert.IsNotNull(
                parser,
                "Task 10 requires bounded KCP command/sequence parsing.");
        }

        [Test]
        public void Profile_ExposesLockedImmutableValues()
        {
            Type profileType = typeof(UdpDatagramFaultProfile);
            string[] propertyNames =
            {
                "DelayMs",
                "JitterMs",
                "DropPercent",
                "ReorderPercent",
                "ReorderExtraDelayMs",
                "DuplicatePercent",
                "Seed"
            };

            for (int index = 0; index < propertyNames.Length; index++)
            {
                PropertyInfo property = profileType.GetProperty(propertyNames[index]);
                Assert.IsNotNull(property, propertyNames[index] + " is missing.");
                Assert.IsFalse(property.CanWrite, propertyNames[index] + " must be immutable.");
            }
        }

        [Test]
        public void Direction_DefinesStableWireIndependentOrderingValues()
        {
            CollectionAssert.AreEqual(
                new[] { "ClientToServer", "ServerToClient" },
                Enum.GetNames(typeof(UdpDatagramDirection)));
        }

        [Test]
        public void Decision_ExposesFaultOutcomes()
        {
            Type decisionType = typeof(UdpDatagramFaultDecision);
            string[] propertyNames =
            {
                "Dropped",
                "EffectiveDelayMs",
                "JitterOffsetMs",
                "ReorderExtraDelayMs",
                "CopyCount"
            };

            for (int index = 0; index < propertyNames.Length; index++)
            {
                Assert.IsNotNull(
                    decisionType.GetProperty(propertyNames[index]),
                    propertyNames[index] + " is missing.");
            }
        }

        [Test]
        public void FaultModel_ExposesStableDecisionAndTraceEntryPoint()
        {
            MethodInfo decide = typeof(UdpDatagramFaultModel).GetMethod(
                "Decide",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[]
                {
                    typeof(UdpDatagramFaultProfile).MakeByRefType(),
                    typeof(UdpDatagramDirection),
                    typeof(RouteCSessionId),
                    typeof(ulong),
                    typeof(byte[]),
                    typeof(UdpDatagramDecisionTraceEntry).MakeByRefType()
                },
                null);
            MethodInfo trace = typeof(UdpDatagramDecisionTraceEntry).GetMethod(
                "ToCanonicalJsonLine",
                BindingFlags.Public | BindingFlags.Instance);

            Assert.IsNotNull(decide, "The stable datagram decision entry point is missing.");
            Assert.IsNotNull(trace, "The canonical trace serializer is missing.");
        }

        [Test]
        public void TraceEntry_ExposesLockedUdpDatagramSemanticLabel()
        {
            FieldInfo label = typeof(UdpDatagramDecisionTraceEntry).GetField(
                "SemanticLabel",
                BindingFlags.Public | BindingFlags.Static);

            Assert.IsNotNull(label);
            Assert.IsTrue(label.IsLiteral);
            Assert.AreEqual("udp-datagram-drop", label.GetRawConstantValue());
            Assert.AreNotEqual(
                "tcp-recovered-loss-hol",
                label.GetRawConstantValue());
        }

        [Test]
        public void Profile_ValidBoundaryValues_AreRetained()
        {
            var profile = new UdpDatagramFaultProfile(
                5000,
                5000,
                100,
                100,
                5000,
                100,
                0xA1B2C3D4u);

            Assert.AreEqual(5000, profile.DelayMs);
            Assert.AreEqual(5000, profile.JitterMs);
            Assert.AreEqual(100, profile.DropPercent);
            Assert.AreEqual(100, profile.ReorderPercent);
            Assert.AreEqual(5000, profile.ReorderExtraDelayMs);
            Assert.AreEqual(100, profile.DuplicatePercent);
            Assert.AreEqual(0xA1B2C3D4u, profile.Seed);
        }

        [Test]
        public void Profile_InvalidRanges_Throw()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CreateProfile(delayMs: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => CreateProfile(delayMs: 5001));
            Assert.Throws<ArgumentOutOfRangeException>(() => CreateProfile(jitterMs: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => CreateProfile(jitterMs: 5001));
            Assert.Throws<ArgumentOutOfRangeException>(() => CreateProfile(dropPercent: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => CreateProfile(dropPercent: 101));
            Assert.Throws<ArgumentOutOfRangeException>(() => CreateProfile(reorderPercent: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => CreateProfile(reorderPercent: 101));
            Assert.Throws<ArgumentOutOfRangeException>(() => CreateProfile(reorderExtraDelayMs: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => CreateProfile(reorderExtraDelayMs: 5001));
            Assert.Throws<ArgumentOutOfRangeException>(() => CreateProfile(duplicatePercent: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => CreateProfile(duplicatePercent: 101));
        }

        [Test]
        public void KcpHeader_ValidHeaderParsesLittleEndianAndShortPayloadIsSafe()
        {
            var payload = new byte[RouteCProtocolConstants.KcpHeaderSize];
            payload[4] = 0x52;
            payload[12] = 0x78;
            payload[13] = 0x56;
            payload[14] = 0x34;
            payload[15] = 0x12;

            Assert.IsTrue(RouteCKcpHeader.TryReadCommandAndSequence(
                payload,
                out byte command,
                out uint sequence));
            Assert.AreEqual(0x52, command);
            Assert.AreEqual(0x12345678u, sequence);
            Assert.IsFalse(RouteCKcpHeader.TryReadCommandAndSequence(
                new byte[RouteCProtocolConstants.KcpHeaderSize - 1],
                out _,
                out _));
            Assert.IsFalse(RouteCKcpHeader.TryReadCommandAndSequence(
                null,
                out _,
                out _));

            var unknownCommand = new byte[RouteCProtocolConstants.KcpHeaderSize];
            unknownCommand[4] = 0x7F;
            Assert.IsFalse(RouteCKcpHeader.TryReadCommandAndSequence(
                unknownCommand,
                out _,
                out _));

            var truncatedSegment = new byte[RouteCProtocolConstants.KcpHeaderSize];
            truncatedSegment[4] = 0x51;
            truncatedSegment[20] = 1;
            Assert.IsFalse(RouteCKcpHeader.TryReadCommandAndSequence(
                truncatedSegment,
                out _,
                out _));
        }

        [Test]
        public void Decide_CleanProfile_ReturnsOneImmediateCopyAndAuditableTrace()
        {
            var sessionId = new RouteCSessionId(0x0102030405060708UL, 0x1112131415161718UL);
            var profile = CreateProfile(seed: 77u);

            UdpDatagramFaultDecision decision = UdpDatagramFaultModel.Decide(
                in profile,
                UdpDatagramDirection.ClientToServer,
                sessionId,
                9UL,
                BuildKcpEnvelope(sessionId, 0x51, 123u),
                out UdpDatagramDecisionTraceEntry trace);
            string line = trace.ToCanonicalJsonLine();

            Assert.IsFalse(decision.Dropped);
            Assert.AreEqual(0, decision.EffectiveDelayMs);
            Assert.AreEqual(0, decision.JitterOffsetMs);
            Assert.AreEqual(0, decision.ReorderExtraDelayMs);
            Assert.AreEqual(1, decision.CopyCount);
            StringAssert.Contains("\"semantic\":\"udp-datagram-drop\"", line);
            StringAssert.Contains("\"direction\":\"client-to-server\"", line);
            StringAssert.Contains("\"sequence\":9", line);
            StringAssert.Contains("\"messageType\":\"KcpData\"", line);
            StringAssert.Contains("\"kcpCommand\":81", line);
            StringAssert.Contains("\"kcpSequence\":123", line);
        }

        [Test]
        public void Decide_SameSeedAndScript_ProducesNonEmptyByteIdenticalTrace()
        {
            UdpDatagramFaultProfile profile = FaultyProfile(0xA55A1234u);

            List<string> first = BuildTrace(profile, 128);
            List<string> second = BuildTrace(profile, 128);

            Assert.IsNotEmpty(first[0]);
            CollectionAssert.AreEqual(first, second);
        }

        [Test]
        public void Decide_CanonicalTraceEvidenceFile_IsWrittenFromDeterministicScript()
        {
            List<string> trace = BuildTrace(FaultyProfile(0xA55A1234u), 512);
            string path = Path.Combine(
                Path.GetDirectoryName(UnityEngine.Application.dataPath),
                "p2f-task10-decision-trace.jsonl");

            File.WriteAllLines(path, trace);

            Assert.AreEqual(512, trace.Count);
            Assert.IsTrue(File.Exists(path));
            Assert.Greater(new FileInfo(path).Length, 0L);
            TestContext.Out.WriteLine("decisionTracePath=" + path);
        }

        [Test]
        public void Decide_SelectedDifferentSeeds_DivergeWithinFiveHundredTwelveDatagrams()
        {
            List<string> first = BuildTrace(FaultyProfile(0x11111111u), 512);
            List<string> second = BuildTrace(FaultyProfile(0x22222222u), 512);
            int firstDivergence = -1;
            for (int index = 0; index < first.Count; index++)
            {
                if (!string.Equals(first[index], second[index], StringComparison.Ordinal))
                {
                    firstDivergence = index;
                    break;
                }
            }

            Assert.That(firstDivergence, Is.InRange(0, 511));
            TestContext.Out.WriteLine(
                "firstDifferentSeedDivergenceDatagramSequence=" +
                firstDivergence);
        }

        [Test]
        public void Decide_DuplicateChannel_DoesNotPerturbDelayDropOrReorderChannels()
        {
            var withoutDuplicate = new UdpDatagramFaultProfile(
                20, 7, 35, 100, 40, 0, 0xCAFE1234u);
            var withDuplicate = new UdpDatagramFaultProfile(
                20, 7, 35, 100, 40, 100, 0xCAFE1234u);
            RouteCSessionId sessionId = new RouteCSessionId(3UL, 4UL);
            byte[] datagram = BuildKcpEnvelope(sessionId, 0x51, 8u);

            UdpDatagramFaultDecision first = UdpDatagramFaultModel.Decide(
                in withoutDuplicate,
                UdpDatagramDirection.ServerToClient,
                sessionId,
                12UL,
                datagram,
                out _);
            UdpDatagramFaultDecision second = UdpDatagramFaultModel.Decide(
                in withDuplicate,
                UdpDatagramDirection.ServerToClient,
                sessionId,
                12UL,
                datagram,
                out _);

            Assert.AreEqual(first.Dropped, second.Dropped);
            Assert.AreEqual(first.EffectiveDelayMs, second.EffectiveDelayMs);
            Assert.AreEqual(first.JitterOffsetMs, second.JitterOffsetMs);
            Assert.AreEqual(first.ReorderExtraDelayMs, second.ReorderExtraDelayMs);
            Assert.AreEqual(first.Dropped ? 0 : 1, first.CopyCount);
            Assert.AreEqual(first.Dropped ? 0 : 2, second.CopyCount);
        }

        [Test]
        public void Trace_ReconnectSecretsAndLiveTimestamps_AreExcluded()
        {
            var nonce = Sequence(0x10, RouteCProtocolConstants.NonceSize);
            var token = Sequence(0x80, RouteCProtocolConstants.ReconnectTokenSize);
            var sessionId = new RouteCSessionId(0xDEADBEEFDEADBEEFUL, 0x0123456789ABCDEFUL);
            byte[] envelope = RouteCProtocolCodec.Encode(
                RouteCMessageType.Hello,
                sessionId,
                2u,
                RouteCProtocolCodec.EncodeReconnectHello(nonce, token));
            UdpDatagramFaultProfile profile = CreateProfile(seed: 13u);

            UdpDatagramFaultModel.Decide(
                in profile,
                UdpDatagramDirection.ClientToServer,
                sessionId,
                1UL,
                envelope,
                out UdpDatagramDecisionTraceEntry trace);
            string line = trace.ToCanonicalJsonLine();

            StringAssert.Contains("\"sessionFingerprint\":", line);
            StringAssert.DoesNotContain("DEADBEEF", line.ToUpperInvariant());
            StringAssert.DoesNotContain("0123456789ABCDEF", line.ToUpperInvariant());
            StringAssert.DoesNotContain("nonce", line.ToLowerInvariant());
            StringAssert.DoesNotContain("token", line.ToLowerInvariant());
            StringAssert.DoesNotContain("attempt", line.ToLowerInvariant());
            StringAssert.DoesNotContain("timestamp", line.ToLowerInvariant());
            StringAssert.DoesNotContain("utc", line.ToLowerInvariant());
        }

        [Test]
        public void Trace_MalformedAndNonKcpDatagrams_DoNotReadPastBounds()
        {
            UdpDatagramFaultProfile profile = CreateProfile(seed: 99u);

            Assert.DoesNotThrow(() => UdpDatagramFaultModel.Decide(
                in profile,
                UdpDatagramDirection.ServerToClient,
                RouteCSessionId.Zero,
                3UL,
                new byte[3],
                out _));
            UdpDatagramFaultModel.Decide(
                in profile,
                UdpDatagramDirection.ServerToClient,
                RouteCSessionId.Zero,
                3UL,
                new byte[3],
                out UdpDatagramDecisionTraceEntry trace);

            StringAssert.Contains("\"messageType\":null", trace.ToCanonicalJsonLine());
            StringAssert.Contains("\"kcpCommand\":null", trace.ToCanonicalJsonLine());
            StringAssert.Contains("\"kcpSequence\":null", trace.ToCanonicalJsonLine());
        }

        [Test]
        public void VirtualLink_ExposesVirtualTimeEnvelopeApi()
        {
            Type linkType = typeof(UdpVirtualDatagramLink);
            ConstructorInfo constructor = linkType.GetConstructor(new[]
            {
                typeof(UdpDatagramFaultProfile),
                typeof(UdpDatagramFaultProfile)
            });
            MethodInfo enqueue = linkType.GetMethod(
                "Enqueue",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[]
                {
                    typeof(long),
                    typeof(UdpDatagramDirection),
                    typeof(int),
                    typeof(byte[])
                },
                null);
            MethodInfo dequeue = linkType.GetMethod(
                "TryDequeueDue",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[]
                {
                    typeof(long),
                    typeof(UdpDatagramDirection).MakeByRefType(),
                    typeof(int).MakeByRefType(),
                    typeof(byte[]).MakeByRefType()
                },
                null);

            Assert.IsNotNull(constructor, "The asymmetric-profile constructor is missing.");
            Assert.IsNotNull(enqueue, "The full-envelope enqueue method is missing.");
            Assert.AreEqual(typeof(UdpDatagramDecisionTraceEntry), enqueue.ReturnType);
            Assert.IsNotNull(dequeue, "The virtual-time release method is missing.");
            Assert.IsNotNull(linkType.GetProperty("PendingCount"));
        }

        [Test]
        public void VirtualLink_FixedDelay_ReleasesOnlyAtVirtualDueTime()
        {
            var profile = CreateProfile(delayMs: 10);
            var link = new UdpVirtualDatagramLink(profile, profile);
            byte[] envelope = BuildKcpEnvelope(new RouteCSessionId(1UL, 2UL), 0x51, 1u);

            UdpDatagramDecisionTraceEntry trace = link.Enqueue(
                100L,
                UdpDatagramDirection.ClientToServer,
                7,
                envelope);

            Assert.AreEqual(10, trace.Decision.EffectiveDelayMs);
            Assert.AreEqual(1, link.PendingCount);
            Assert.IsFalse(link.TryDequeueDue(109L, out _, out _, out _));
            Assert.IsTrue(link.TryDequeueDue(
                110L,
                out UdpDatagramDirection direction,
                out int route,
                out byte[] delivered));
            Assert.AreEqual(UdpDatagramDirection.ClientToServer, direction);
            Assert.AreEqual(7, route);
            CollectionAssert.AreEqual(envelope, delivered);
            Assert.AreEqual(0, link.PendingCount);
        }

        [Test]
        public void VirtualLink_EnqueueCopiesWholeEnvelopeBeforeCallerMutation()
        {
            UdpDatagramFaultProfile profile = CreateProfile();
            var link = new UdpVirtualDatagramLink(profile, profile);
            byte[] envelope = BuildKcpEnvelope(new RouteCSessionId(3UL, 4UL), 0x51, 2u);
            byte[] expected = (byte[])envelope.Clone();

            link.Enqueue(0L, UdpDatagramDirection.ClientToServer, 0, envelope);
            for (int index = 0; index < envelope.Length; index++)
                envelope[index] ^= 0xFF;

            Assert.IsTrue(link.TryDequeueDue(0L, out _, out _, out byte[] delivered));
            CollectionAssert.AreEqual(expected, delivered);
        }

        [Test]
        public void VirtualLink_DuplicateCopiesAreIndependentArrays()
        {
            UdpDatagramFaultProfile profile = CreateProfile(duplicatePercent: 100);
            var link = new UdpVirtualDatagramLink(profile, profile);
            byte[] envelope = BuildKcpEnvelope(new RouteCSessionId(5UL, 6UL), 0x51, 3u);
            byte firstByte = envelope[0];

            link.Enqueue(0L, UdpDatagramDirection.ClientToServer, 0, envelope);

            Assert.AreEqual(2, link.PendingCount);
            Assert.IsTrue(link.TryDequeueDue(0L, out _, out _, out byte[] first));
            first[0] ^= 0xFF;
            Assert.IsTrue(link.TryDequeueDue(0L, out _, out _, out byte[] second));
            Assert.AreEqual(firstByte, second[0]);
            Assert.AreNotSame(first, second);
        }

        [Test]
        public void VirtualLink_SameDueTime_ReleasesByDirectionSequenceAndCopyIndex()
        {
            UdpDatagramFaultProfile profile = CreateProfile(
                delayMs: 10,
                duplicatePercent: 100);
            var link = new UdpVirtualDatagramLink(profile, profile);
            RouteCSessionId session = new RouteCSessionId(7UL, 8UL);

            UdpDatagramDecisionTraceEntry firstTrace = link.Enqueue(
                0L,
                UdpDatagramDirection.ClientToServer,
                10,
                BuildKcpEnvelope(session, 0x51, 0u));
            UdpDatagramDecisionTraceEntry secondTrace = link.Enqueue(
                0L,
                UdpDatagramDirection.ClientToServer,
                11,
                BuildKcpEnvelope(session, 0x51, 1u));
            UdpDatagramDecisionTraceEntry thirdTrace = link.Enqueue(
                0L,
                UdpDatagramDirection.ServerToClient,
                20,
                BuildKcpEnvelope(session, 0x52, 0u));

            Assert.AreEqual(0UL, firstTrace.Sequence);
            Assert.AreEqual(1UL, secondTrace.Sequence);
            Assert.AreEqual(0UL, thirdTrace.Sequence);
            var routes = new List<int>();
            while (link.TryDequeueDue(10L, out _, out int route, out _))
                routes.Add(route);
            CollectionAssert.AreEqual(new[] { 10, 10, 11, 11, 20, 20 }, routes);
        }

        [Test]
        public void VirtualLink_DropPermanentlySchedulesNoRecoveryEvent()
        {
            UdpDatagramFaultProfile profile = CreateProfile(dropPercent: 100);
            var link = new UdpVirtualDatagramLink(profile, profile);

            UdpDatagramDecisionTraceEntry trace = link.Enqueue(
                5L,
                UdpDatagramDirection.ClientToServer,
                1,
                BuildKcpEnvelope(new RouteCSessionId(9UL, 10UL), 0x51, 4u));

            Assert.IsTrue(trace.Decision.Dropped);
            Assert.AreEqual(0, trace.Decision.CopyCount);
            Assert.AreEqual(0, link.PendingCount);
            Assert.IsFalse(link.TryDequeueDue(long.MaxValue, out _, out _, out _));
        }

        [Test]
        public void VirtualLink_JitterAndReorderBecomeDeterministicDueOffset()
        {
            UdpDatagramFaultProfile profile = CreateProfile(
                delayMs: 10,
                jitterMs: 5,
                reorderPercent: 100,
                reorderExtraDelayMs: 20,
                seed: 0x12345678u);
            var link = new UdpVirtualDatagramLink(profile, profile);

            UdpDatagramDecisionTraceEntry trace = link.Enqueue(
                50L,
                UdpDatagramDirection.ServerToClient,
                2,
                BuildKcpEnvelope(new RouteCSessionId(11UL, 12UL), 0x52, 5u));
            int offset = trace.Decision.EffectiveDelayMs;

            Assert.That(offset, Is.InRange(25, 35));
            Assert.AreEqual(20, trace.Decision.ReorderExtraDelayMs);
            Assert.IsFalse(link.TryDequeueDue(50L + offset - 1L, out _, out _, out _));
            Assert.IsTrue(link.TryDequeueDue(50L + offset, out _, out _, out _));
        }

        [Test]
        public void VirtualLink_AsymmetricProfiles_AffectOnlyTheirDirection()
        {
            var uplink = CreateProfile(delayMs: 3);
            var downlink = CreateProfile(delayMs: 9);
            var link = new UdpVirtualDatagramLink(uplink, downlink);
            RouteCSessionId session = new RouteCSessionId(13UL, 14UL);

            link.Enqueue(
                0L,
                UdpDatagramDirection.ServerToClient,
                20,
                BuildKcpEnvelope(session, 0x52, 0u));
            link.Enqueue(
                0L,
                UdpDatagramDirection.ClientToServer,
                10,
                BuildKcpEnvelope(session, 0x51, 0u));

            Assert.IsTrue(link.TryDequeueDue(3L, out _, out int firstRoute, out _));
            Assert.AreEqual(10, firstRoute);
            Assert.IsFalse(link.TryDequeueDue(8L, out _, out _, out _));
            Assert.IsTrue(link.TryDequeueDue(9L, out _, out int secondRoute, out _));
            Assert.AreEqual(20, secondRoute);
        }

        [Test]
        public void RealKcpScenarioDriver_ExposesBoundedRecoveryEntryPoint()
        {
            Type driverType = typeof(KcpUdpVirtualLinkTests).Assembly.GetType(
                "FrameSyncDemo.Tests.KcpUdpVirtualScenarioDriver");
            Assert.IsNotNull(
                driverType,
                "Task 10 requires a test-only driver over real RouteCKcpSession instances.");

            MethodInfo run = driverType.GetMethod(
                "RunSingleMessage",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[]
                {
                    typeof(UdpDatagramFaultProfile),
                    typeof(UdpDatagramFaultProfile),
                    typeof(uint),
                    typeof(int),
                    typeof(int)
                },
                null);
            Assert.IsNotNull(run, "The bounded real-KCP recovery entry point is missing.");
            Assert.AreNotEqual(typeof(void), run.ReturnType);
        }

        [Test]
        public void RealKcpScenarioResult_ExposesTerminationDeliveryAndFaultEvidence()
        {
            Type resultType = typeof(KcpUdpVirtualScenarioDriver.Result);
            string[] propertyNames =
            {
                "TerminatedWithinBound",
                "TerminationTimeMs",
                "StepCount",
                "ReceivedRaw",
                "ReceivedFrameID",
                "DeliveryCount",
                "SenderOutputCount",
                "ReceiverOutputCount",
                "DroppedDataCount",
                "DroppedAckCount",
                "ReorderedDatagramCount",
                "DuplicateDatagramCount",
                "TraceLines",
                "ReceivedRaws",
                "ReceivedFrameIDs",
                "NetworkOutOfOrderReleaseCount",
                "SenderPendingSendCount"
            };

            for (int index = 0; index < propertyNames.Length; index++)
            {
                Assert.IsNotNull(
                    resultType.GetProperty(propertyNames[index]),
                    propertyNames[index] + " is missing.");
            }
        }

        [Test]
        public void KcpRecovery_PermanentAckLoss_DoesNotClaimCompletion()
        {
            UdpDatagramFaultProfile clean = CreateProfile();
            UdpDatagramFaultProfile permanentAckLoss =
                CreateProfile(dropPercent: 100, seed: 0xACCu);

            KcpUdpVirtualScenarioDriver.Result result =
                KcpUdpVirtualScenarioDriver.RunSingleMessage(
                    clean,
                    permanentAckLoss,
                    0x10203040u,
                    9,
                    1500);

            Assert.IsFalse(result.TerminatedWithinBound);
            Assert.Greater(result.SenderPendingSendCount, 0);
            Assert.AreEqual(1, result.DeliveryCount);
            Assert.Greater(result.DroppedAckCount, 0);
        }

        [Test]
        public void RealKcpScenarioDriver_ExposesBoundedSequenceEntryPoint()
        {
            MethodInfo run = typeof(KcpUdpVirtualScenarioDriver).GetMethod(
                "RunSequence",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[]
                {
                    typeof(UdpDatagramFaultProfile),
                    typeof(UdpDatagramFaultProfile),
                    typeof(int),
                    typeof(int),
                    typeof(int)
                },
                null);

            Assert.IsNotNull(run, "The bounded multi-message KCP entry point is missing.");
            Assert.AreEqual(typeof(KcpUdpVirtualScenarioDriver.Result), run.ReturnType);
        }

        [Test]
        public void RealKcp_DataDrop_RecoversOnlyThroughKcpRetransmission()
        {
            UdpDatagramFaultProfile uplink = FindFirstDropThenPassProfile(
                UdpDatagramDirection.ClientToServer,
                0x51);
            UdpDatagramFaultProfile downlink = CreateProfile();

            KcpUdpVirtualScenarioDriver.Result result =
                KcpUdpVirtualScenarioDriver.RunSingleMessage(
                    uplink,
                    downlink,
                    0x10203040u,
                    55,
                    1000);

            Assert.IsTrue(result.TerminatedWithinBound);
            Assert.AreEqual(0x10203040u, result.ReceivedRaw);
            Assert.AreEqual(55, result.ReceivedFrameID);
            Assert.AreEqual(1, result.DeliveryCount);
            Assert.AreEqual(1, result.DroppedDataCount);
            Assert.AreEqual(0, result.DroppedAckCount);
            Assert.GreaterOrEqual(result.SenderOutputCount, 2);
            Assert.That(result.TerminationTimeMs, Is.InRange(1, 1000));
            Assert.That(result.StepCount, Is.InRange(1, 1001));
        }

        [Test]
        public void RealKcp_AckDrop_RecoversThroughDuplicateDataAndRealAck()
        {
            UdpDatagramFaultProfile uplink = CreateProfile();
            UdpDatagramFaultProfile downlink = FindFirstDropThenPassProfile(
                UdpDatagramDirection.ServerToClient,
                0x52);

            KcpUdpVirtualScenarioDriver.Result result =
                KcpUdpVirtualScenarioDriver.RunSingleMessage(
                    uplink,
                    downlink,
                    0x55667788u,
                    91,
                    1200);

            Assert.IsTrue(result.TerminatedWithinBound);
            Assert.AreEqual(0x55667788u, result.ReceivedRaw);
            Assert.AreEqual(91, result.ReceivedFrameID);
            Assert.AreEqual(1, result.DeliveryCount);
            Assert.AreEqual(0, result.DroppedDataCount);
            Assert.AreEqual(1, result.DroppedAckCount);
            Assert.GreaterOrEqual(result.SenderOutputCount, 2);
            Assert.GreaterOrEqual(result.ReceiverOutputCount, 2);
            Assert.That(result.TerminationTimeMs, Is.InRange(1, 1200));
        }

        [Test]
        public void RealKcp_DuplicateAndReorder_DeliverBusinessExactlyOnceInOrder()
        {
            UdpDatagramFaultProfile profile = FindFirstReorderedSecondImmediateProfile();

            KcpUdpVirtualScenarioDriver.Result result =
                KcpUdpVirtualScenarioDriver.RunSequence(
                    profile,
                    profile,
                    12,
                    10,
                    2000);

            Assert.IsTrue(result.TerminatedWithinBound);
            Assert.AreEqual(12, result.DeliveryCount);
            Assert.AreEqual(12, result.ReceivedFrameIDs.Length);
            Assert.AreEqual(12, result.ReceivedRaws.Length);
            for (int index = 0; index < 12; index++)
            {
                Assert.AreEqual(index, result.ReceivedFrameIDs[index]);
                Assert.AreEqual(
                    unchecked(0xABC00000u + (uint)index),
                    result.ReceivedRaws[index]);
            }
            Assert.Greater(result.ReorderedDatagramCount, 0);
            Assert.Greater(result.DuplicateDatagramCount, 0);
            Assert.Greater(
                result.NetworkOutOfOrderReleaseCount,
                0,
                "outputs=" + result.SenderOutputCount +
                " reordered=" + result.ReorderedDatagramCount +
                " duplicates=" + result.DuplicateDatagramCount +
                " traces=" + string.Join(" | ", result.TraceLines));
            Assert.That(result.TerminationTimeMs, Is.InRange(1, 2000));
        }

        private static UdpDatagramFaultProfile CreateProfile(
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

        private static UdpDatagramFaultProfile FaultyProfile(uint seed)
        {
            return new UdpDatagramFaultProfile(25, 9, 31, 37, 50, 29, seed);
        }

        private static UdpDatagramFaultProfile FindFirstDropThenPassProfile(
            UdpDatagramDirection direction,
            byte command)
        {
            RouteCSessionId sessionId = ScenarioSessionId();
            byte[] firstEnvelope = BuildKcpEnvelope(sessionId, command, 0u);
            for (uint seed = 1u; seed <= 10000u; seed++)
            {
                var profile = new UdpDatagramFaultProfile(
                    0, 0, 50, 0, 0, 0, seed);
                UdpDatagramFaultDecision first = UdpDatagramFaultModel.Decide(
                    in profile,
                    direction,
                    sessionId,
                    0UL,
                    firstEnvelope,
                    out _);
                UdpDatagramFaultDecision second = UdpDatagramFaultModel.Decide(
                    in profile,
                    direction,
                    sessionId,
                    1UL,
                    firstEnvelope,
                    out _);
                if (first.Dropped && !second.Dropped)
                    return profile;
            }
            throw new AssertionException("No bounded first-drop/second-pass seed was found.");
        }

        private static UdpDatagramFaultProfile FindFirstReorderedSecondImmediateProfile()
        {
            for (uint seed = 1u; seed <= 512u; seed++)
            {
                var profile = new UdpDatagramFaultProfile(
                    0, 0, 0, 50, 100, 100, seed);
                KcpUdpVirtualScenarioDriver.Result probe =
                    KcpUdpVirtualScenarioDriver.RunSequence(
                        profile,
                        profile,
                        12,
                        10,
                        2000);
                if (probe.TerminatedWithinBound &&
                    probe.ReorderedDatagramCount > 0 &&
                    probe.DuplicateDatagramCount > 0 &&
                    probe.NetworkOutOfOrderReleaseCount > 0)
                {
                    return profile;
                }
            }
            throw new AssertionException(
                "No seed produced an actual reordered KCP release within 512 candidates.");
        }

        private static RouteCSessionId ScenarioSessionId()
        {
            return new RouteCSessionId(
                0x1021324354657687UL,
                0x98A9BACBDCEDFE0FUL);
        }

        private static List<string> BuildTrace(
            UdpDatagramFaultProfile profile,
            int datagramCount)
        {
            var result = new List<string>(datagramCount);
            var sessionId = new RouteCSessionId(0x0102030405060708UL, 0x1112131415161718UL);
            for (int index = 0; index < datagramCount; index++)
            {
                byte command = (index & 1) == 0 ? (byte)0x51 : (byte)0x52;
                UdpDatagramDirection direction = (index & 2) == 0
                    ? UdpDatagramDirection.ClientToServer
                    : UdpDatagramDirection.ServerToClient;
                byte[] envelope = BuildKcpEnvelope(sessionId, command, (uint)index);
                UdpDatagramFaultModel.Decide(
                    in profile,
                    direction,
                    sessionId,
                    (ulong)index,
                    envelope,
                    out UdpDatagramDecisionTraceEntry trace);
                result.Add(trace.ToCanonicalJsonLine());
            }
            return result;
        }

        private static byte[] BuildKcpEnvelope(
            RouteCSessionId sessionId,
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
                sessionId,
                1u,
                payload);
        }

        private static byte[] Sequence(byte first, int count)
        {
            var result = new byte[count];
            for (int index = 0; index < count; index++)
                result[index] = unchecked((byte)(first + index));
            return result;
        }
    }
}

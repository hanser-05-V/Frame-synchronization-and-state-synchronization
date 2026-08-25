using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public sealed class KcpUdpVirtualConvergenceTests
    {
        [Test]
        public void ConvergenceDriver_RequiredSurface_IsAvailable()
        {
            Type driver = typeof(KcpUdpVirtualScenarioDriver);
            MethodInfo method = driver.GetMethod(
                "RunConvergenceScenario",
                BindingFlags.Public | BindingFlags.Static);

            Assert.IsNotNull(method);
            Assert.AreEqual(
                new[]
                {
                    typeof(UdpDatagramFaultProfile),
                    typeof(UdpDatagramFaultProfile),
                    typeof(int),
                    typeof(int)
                },
                method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());

            Type result = driver.GetNestedType(
                "ConvergenceResult",
                BindingFlags.Public);
            Assert.IsNotNull(result);
            foreach (string property in new[]
            {
                "TerminatedWithinBound",
                "BusinessPayloadSize",
                "ActualFramesAscending",
                "IntermediatePredictionDiverged",
                "PeerZeroConfirmedHash",
                "PeerOneConfirmedHash",
                "PeerZeroPredictedHash",
                "PeerOnePredictedHash",
                "PeerZeroConfirmedFrame",
                "PeerOneConfirmedFrame",
                "PeerZeroPredictedFrame",
                "PeerOnePredictedFrame",
                "PeerZeroReconciled",
                "PeerOneReconciled"
            })
            {
                Assert.IsNotNull(result.GetProperty(property), property);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CompleteBusinessScript_AfterRealKcpFaults_ConvergesBothWorldTracks(
            bool faulted)
        {
            UdpDatagramFaultProfile zeroToOne = faulted
                ? Profile(7, 7, 0, 100, 35, 100, 0x510u)
                : Profile(0, 0, 0, 0, 0, 0, 0x510u);
            UdpDatagramFaultProfile oneToZero = faulted
                ? Profile(31, 5, 0, 100, 19, 100, 0xA20u)
                : Profile(0, 0, 0, 0, 0, 0, 0xA20u);

            KcpUdpVirtualScenarioDriver.ConvergenceResult result =
                KcpUdpVirtualScenarioDriver.RunConvergenceScenario(
                    zeroToOne,
                    oneToZero,
                    12,
                    5000);

            TestContext.Out.WriteLine(
                "semantic=udp-datagram-drop scenario=world-convergence" +
                " faulted=" + faulted +
                " terminated=" + result.TerminatedWithinBound +
                " ascendingActuals=" + result.ActualFramesAscending +
                " intermediatePredictionDiverged=" +
                result.IntermediatePredictionDiverged +
                " confirmedFrame0=" + result.PeerZeroConfirmedFrame +
                " confirmedFrame1=" + result.PeerOneConfirmedFrame +
                " confirmedHash0=" + result.PeerZeroConfirmedHash +
                " confirmedHash1=" + result.PeerOneConfirmedHash +
                " predictedHash0=" + result.PeerZeroPredictedHash +
                " predictedHash1=" + result.PeerOnePredictedHash);

            Assert.IsTrue(result.TerminatedWithinBound);
            Assert.AreEqual(8, result.BusinessPayloadSize);
            Assert.IsTrue(result.ActualFramesAscending);
            Assert.IsTrue(result.IntermediatePredictionDiverged);
            Assert.IsTrue(result.PeerZeroReconciled);
            Assert.IsTrue(result.PeerOneReconciled);
            Assert.AreEqual(11, result.PeerZeroConfirmedFrame);
            Assert.AreEqual(11, result.PeerOneConfirmedFrame);
            Assert.AreEqual(11, result.PeerZeroPredictedFrame);
            Assert.AreEqual(11, result.PeerOnePredictedFrame);
            Assert.AreEqual(
                result.PeerZeroConfirmedHash,
                result.PeerOneConfirmedHash);
            Assert.AreEqual(
                result.PeerZeroPredictedHash,
                result.PeerOnePredictedHash);
            Assert.AreEqual(
                result.PeerZeroConfirmedHash,
                result.PeerZeroPredictedHash);
            Assert.AreEqual(
                result.PeerOneConfirmedHash,
                result.PeerOnePredictedHash);
        }

        private static UdpDatagramFaultProfile Profile(
            int delayMs,
            int jitterMs,
            int dropPercent,
            int reorderPercent,
            int reorderExtraDelayMs,
            int duplicatePercent,
            uint seed)
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
    }
}

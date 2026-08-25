using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FrameSyncDemo.Tests
{
    public class P2FClientTransportOwnershipTests
    {
        private const BindingFlags InstanceFields =
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic;

        [Test]
        public void NetworkClient_IsTransportFacadeWithoutProtocolOrSocketOwnership()
        {
            FieldInfo[] fields = typeof(NetworkClient).GetFields(InstanceFields);

            Assert.AreEqual(
                1,
                fields.Count(field =>
                    field.FieldType == typeof(IFrameTransportClient)),
                "NetworkClient must own exactly one transport interface field.");
            Assert.IsFalse(
                fields.Any(field =>
                    field.FieldType == typeof(TcpClient) ||
                    field.FieldType == typeof(NetworkStream) ||
                    field.FieldType == typeof(Socket) ||
                    field.FieldType.FullName == "kcp2k.Kcp"),
                "NetworkClient must not own TCP, UDP, or KCP internals.");
            Assert.AreEqual(
                0,
                fields.Count(field =>
                    field.FieldType ==
                    typeof(ConcurrentQueue<NetworkPacketArrival>)),
                "The facade must not create a second remote-input truth source.");
        }

        [TestCase("FrameSyncDemo.KcpUdpClientTransport")]
        [TestCase("FrameSyncDemo.TcpClientTransport")]
        [TestCase("FrameSyncDemo.RawUdpInputTransport")]
        public void ConcreteTransport_OwnsExactlyOneRemoteArrivalQueue(
            string transportTypeName)
        {
            Type transportType = typeof(NetworkClient).Assembly.GetType(
                transportTypeName,
                false);

            Assert.IsNotNull(
                transportType,
                "Missing concrete transport: " + transportTypeName);
            FieldInfo[] fields = transportType.GetFields(InstanceFields);
            Assert.AreEqual(
                1,
                fields.Count(field =>
                    field.FieldType ==
                    typeof(ConcurrentQueue<NetworkPacketArrival>)));
        }

        [Test]
        public void TcpTransport_ExclusivelyOwnsTcpClientAndStream()
        {
            FieldInfo[] fields = typeof(TcpClientTransport).GetFields(
                InstanceFields);

            Assert.AreEqual(
                1,
                fields.Count(field => field.FieldType == typeof(TcpClient)));
            Assert.AreEqual(
                1,
                fields.Count(field => field.FieldType == typeof(NetworkStream)));
        }

        [Test]
        public void KcpWorker_UsesDeadlineSelectWithoutUnityOrOneMillisecondPolling()
        {
            FieldInfo[] fields = typeof(KcpUdpClientTransport).GetFields(
                InstanceFields);
            Assert.AreEqual(
                1,
                fields.Count(field => field.FieldType == typeof(Socket)));

            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Network",
                "KcpUdpClientTransport.cs"));
            StringAssert.Contains("Socket.Select", source);
            StringAssert.Contains(
                "RouteCProtocolConstants.MaximumDatagramsPerRound",
                source);
            StringAssert.Contains(
                "RouteCProtocolConstants.MaximumMessagesPerSessionPerRound",
                source);
            StringAssert.DoesNotContain("Thread.Sleep(1)", source);
            StringAssert.DoesNotContain("Time.deltaTime", source);
        }

        [Test]
        public void GameController_DoesNotOwnRemoteArrivalQueue()
        {
            FieldInfo[] fields = typeof(GameController).GetFields(InstanceFields);

            Assert.AreEqual(
                0,
                fields.Count(field =>
                    field.FieldType ==
                    typeof(ConcurrentQueue<NetworkPacketArrival>)));
        }

        [Test]
        public void GameController_StartUsesAllmanBraces()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "GameController.cs")).Replace("\r\n", "\n");

            StringAssert.Contains(
                "        private void Start()\n" +
                "        {\n" +
                "            Initialize();\n" +
                "        }",
                source);
        }
    }
}

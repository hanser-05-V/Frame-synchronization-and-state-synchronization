using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FrameSyncDemo.Tests
{
    public sealed class RawUdpFacadeWiringTests
    {
        private const BindingFlags PrivateStatic =
            BindingFlags.Static | BindingFlags.NonPublic;
        private const BindingFlags PrivateInstance =
            BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void TransportEnum_PreservesKcp0Tcp1AndAppendsRawUdp2()
        {
            Assert.AreEqual(0, (int)NetworkTransportKind.KcpUdp);
            Assert.AreEqual(1, (int)NetworkTransportKind.Tcp);
            Assert.AreEqual(2, (int)NetworkTransportKind.RawUdp);
        }

        [Test]
        public void ConnectConfigured_CommandLineOverridesInspectorAndDefaultsPerField()
        {
            NetworkConnectionOptions options = NetworkConnectionOptions.Resolve(
                new[]
                {
                    "game.exe",
                    "--transport", "raw-udp",
                    "--server-port", "9001"
                },
                NetworkTransportKind.Tcp,
                "inspector.example",
                7000);

            Assert.AreEqual(NetworkTransportKind.RawUdp, options.Transport);
            Assert.AreEqual("inspector.example", options.Host);
            Assert.AreEqual(9001, options.Port);
        }

        [Test]
        public void ConnectConfigured_InspectorOverridesNetworkConfigDefaults()
        {
            NetworkConnectionOptions inspector = NetworkConnectionOptions.Resolve(
                new string[0],
                NetworkTransportKind.Tcp,
                "localhost",
                7777);
            Assert.AreEqual(NetworkTransportKind.Tcp, inspector.Transport);
            Assert.AreEqual("localhost", inspector.Host);
            Assert.AreEqual(7777, inspector.Port);

            NetworkConnectionOptions defaults = NetworkConnectionOptions.Resolve(
                new string[0],
                NetworkTransportKind.KcpUdp,
                "  ",
                0);
            Assert.AreEqual(NetworkConfig.DEFAULT_IP, defaults.Host);
            Assert.AreEqual(NetworkConfig.DEFAULT_PORT, defaults.Port);
        }

        [Test]
        public void ConnectConfigured_InvalidTransportHostOrPortFailsBeforeCreatingTransport()
        {
            Assert.Throws<ArgumentException>(() => NetworkConnectionOptions.Resolve(
                new[] { "--transport", "quic" },
                NetworkTransportKind.Tcp,
                "localhost",
                8888));
            Assert.Throws<ArgumentException>(() => NetworkConnectionOptions.Resolve(
                new[] { "--server-ip", "bad host name" },
                NetworkTransportKind.Tcp,
                "localhost",
                8888));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                NetworkConnectionOptions.Resolve(
                    new[] { "--server-port", "65536" },
                    NetworkTransportKind.Tcp,
                    "localhost",
                    8888));

            var gameObject = new GameObject("InvalidConfiguredClient");
            try
            {
                var client = gameObject.AddComponent<NetworkClient>();
                SetField(client, "_serverIP", "bad host name");
                Assert.Throws<ArgumentException>(() => client.ConnectConfigured());
                Assert.IsNull(GetField(client, "_transport"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void Factory_RawUdpCreatesOnlyRawTransportAndLeavesInterfaceUnchanged()
        {
            MethodInfo factory = typeof(NetworkClient).GetMethod(
                "CreateTransport",
                PrivateStatic);
            Assert.IsNotNull(factory);
            var transport = (IFrameTransportClient)factory.Invoke(
                null,
                new object[] { NetworkTransportKind.RawUdp });
            try
            {
                Assert.IsInstanceOf<RawUdpInputTransport>(transport);
                Assert.AreEqual(5, typeof(IFrameTransportClient).GetProperties().Length);
                CollectionAssert.AreEquivalent(
                    new[]
                    {
                        "Start",
                        "TryEnqueueLocalInput",
                        "TryDequeueRemoteInput",
                        "TryDequeueEvent",
                        "SubmitResumeReadiness",
                        "RequestStop",
                        "WaitForStop"
                    },
                    typeof(IFrameTransportClient).GetMethods()
                        .Where(method => !method.IsSpecialName)
                        .Select(method => method.Name));
            }
            finally
            {
                transport.Dispose();
            }
        }

        [Test]
        public void RawTransport_SubmitResumeReadinessIsNoOpAndNeverPublishesResumeRequired()
        {
            using (var transport = new RawUdpInputTransport(
                new StopwatchMonotonicClock(),
                () => new byte[RouteCProtocolConstants.NonceSize]))
            {
                var readiness = new ResumeReadiness(10, 5, 11);
                transport.SubmitResumeReadiness(in readiness);

                Assert.IsFalse(transport.TryDequeueEvent(out _));
                Assert.AreEqual(NetworkSessionState.Disconnected, transport.State);
            }
        }

        [Test]
        public void GameController_UsesConfiguredFacadeOnceWithoutRawGameplayBranch()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "GameController.cs"));

            Assert.AreEqual(1, Count(source, "_networkClient.ConnectConfigured();"));
            StringAssert.DoesNotContain("NetworkTransportKind.RawUdp", source);
            StringAssert.Contains(
                "transportEvent.State == NetworkSessionState.Terminated",
                source);
            StringAssert.Contains(
                "_networkClient.State != NetworkSessionState.Running",
                source);
            StringAssert.Contains("_frameEngine.StartEngine();", source);
        }

        private static int Count(string value, string token)
        {
            int count = 0;
            int offset = 0;
            while ((offset = value.IndexOf(
                       token,
                       offset,
                       StringComparison.Ordinal)) >= 0)
            {
                count++;
                offset += token.Length;
            }
            return count;
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, PrivateInstance);
            Assert.IsNotNull(field, "Missing field: " + name);
            field.SetValue(target, value);
        }

        private static object GetField(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, PrivateInstance);
            Assert.IsNotNull(field, "Missing field: " + name);
            return field.GetValue(target);
        }
    }
}

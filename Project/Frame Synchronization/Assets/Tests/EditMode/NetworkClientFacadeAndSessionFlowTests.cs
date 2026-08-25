using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FrameSyncDemo.Tests
{
    public class NetworkClientFacadeAndSessionFlowTests
    {
        private const BindingFlags PrivateInstance =
            BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void NetworkClient_MapsFacadeStateWrappersAndBoundedDestroy()
        {
            var gameObject = new GameObject("NetworkClientFacadeTest");
            var client = gameObject.AddComponent<NetworkClient>();
            var transport = new FakeTransport
            {
                State = NetworkSessionState.Resuming,
                HasStartedSession = true,
                IsRunning = false,
                LocalPlayerIndex = 1,
                LatestRemoteFrameID = 12
            };
            SetField(client, "_transport", transport);
            transport.RemoteInputs.Enqueue(new NetworkPacketArrival(
                0x1234u,
                12,
                0,
                1));

            Assert.IsFalse(client.IsConnected);
            Assert.IsTrue(client.HasStartedSession);
            Assert.AreEqual(1, client.LocalPlayerIndex);
            Assert.AreEqual(0, client.RemotePlayerIndex);
            Assert.AreEqual(12, client.LatestRemoteFrameID);
            client.SendInput(0x44u, 13);
            Assert.AreEqual(0x44u, transport.LastSentRaw);
            Assert.AreEqual(13, transport.LastSentFrameID);
            Assert.IsTrue(client.TryGetRemoteInput(
                out uint raw,
                out int remoteFrameID));
            Assert.AreEqual(0x1234u, raw);
            Assert.AreEqual(12, remoteFrameID);

            MethodInfo onDestroy = typeof(NetworkClient).GetMethod(
                "OnDestroy",
                PrivateInstance);
            Assert.IsNotNull(onDestroy);
            onDestroy.Invoke(client, null);

            Assert.IsTrue(transport.StopRequested);
            Assert.IsTrue(transport.WaitCalled);
            Assert.IsTrue(transport.Disposed);
            Assert.AreEqual(1, transport.StopRequestCount);
            Assert.AreEqual(1, transport.WaitCallCount);
            Assert.AreEqual(1, transport.DisposeCallCount);
            UnityEngine.Object.DestroyImmediate(gameObject);
        }

        [Test]
        public void SendQueueRejection_IsVisibleAndGameControllerPausesWithoutAdvancingReadiness()
        {
            var gameObject = new GameObject("NetworkSendQueueRejectionTest");
            var engine = gameObject.AddComponent<FrameEngine>();
            var client = gameObject.AddComponent<NetworkClient>();
            var controller = gameObject.AddComponent<GameController>();
            var transport = new FakeTransport
            {
                State = NetworkSessionState.Running,
                HasStartedSession = true,
                IsRunning = true,
                LocalPlayerIndex = 0,
                AcceptLocalInput = false
            };

            try
            {
                SetField(client, "_transport", transport);
                SetField(controller, "_frameEngine", engine);
                SetField(controller, "_networkClient", client);
                engine.Initialize(2, 33);

                MethodInfo sendInput = typeof(NetworkClient).GetMethod(
                    "SendInput",
                    new[] { typeof(uint), typeof(int) });
                Assert.IsNotNull(sendInput);
                Assert.AreEqual(
                    false,
                    sendInput.Invoke(client, new object[] { 0x55u, 7 }));

                MethodInfo trySubmitLocalInput = typeof(GameController).GetMethod(
                    "TrySubmitLocalInput",
                    PrivateInstance);
                Assert.IsNotNull(
                    trySubmitLocalInput,
                    "GameController must centralize send success and readiness advancement.");
                LogAssert.Expect(
                    LogType.Error,
                    "[RouteC][MatchTerminalFault] state=Terminated reason=LocalInputQueueFull");

                Assert.AreEqual(
                    false,
                    trySubmitLocalInput.Invoke(
                        controller,
                        new object[] { 0x66u, 8 }));
                Assert.AreEqual(
                    -1,
                    GetField(controller, "_latestLocallySubmittedFrameID"));
                Assert.IsTrue((bool)GetField(controller, "_paused"));
                StringAssert.Contains(
                    "LocalInputQueueFull",
                    (string)GetField(controller, "_networkTerminalFault"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void Connect_WhenTransportAlreadyExists_RejectsDuplicateWithoutReplacingOwner()
        {
            var gameObject = new GameObject("NetworkDuplicateConnectTest");
            var client = gameObject.AddComponent<NetworkClient>();
            var transport = new FakeTransport
            {
                State = NetworkSessionState.Running,
                HasStartedSession = true,
                IsRunning = true
            };

            try
            {
                SetField(client, "_transport", transport);

                Assert.Throws<InvalidOperationException>(
                    () => client.Connect("127.0.0.1", 9));
                Assert.AreSame(transport, GetField(client, "_transport"));
                Assert.IsFalse(transport.StopRequested);
                Assert.IsFalse(transport.Disposed);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void GameController_StartsOnceAndKeepsNetworkPathUntilTerminalEvent()
        {
            var gameObject = new GameObject("NetworkSessionFlowTest");
            var engine = gameObject.AddComponent<FrameEngine>();
            var client = gameObject.AddComponent<NetworkClient>();
            var controller = gameObject.AddComponent<GameController>();
            var transport = new FakeTransport
            {
                State = NetworkSessionState.Handshaking
            };
            SetField(client, "_transport", transport);
            SetField(controller, "_frameEngine", engine);
            SetField(controller, "_networkClient", client);
            engine.Initialize(2, 33);

            MethodInfo processEvents = typeof(GameController).GetMethod(
                "ProcessNetworkTransportEvents",
                PrivateInstance);
            MethodInfo shouldUseNetwork = typeof(GameController).GetMethod(
                "ShouldUseNetworkInputPath",
                PrivateInstance);
            Assert.IsNotNull(processEvents);
            Assert.IsNotNull(shouldUseNetwork);

            processEvents.Invoke(controller, null);
            Assert.IsFalse(engine.IsRunning);

            transport.State = NetworkSessionState.Running;
            transport.IsRunning = true;
            transport.HasStartedSession = true;
            transport.Events.Enqueue(new NetworkTransportEvent(
                NetworkSessionState.Running,
                NetworkTransportEventReason.SessionStarted,
                "session",
                1u,
                10u));
            processEvents.Invoke(controller, null);
            Assert.IsTrue(engine.IsRunning);

            engine.Pause();
            transport.Events.Enqueue(new NetworkTransportEvent(
                NetworkSessionState.Running,
                NetworkTransportEventReason.SessionStarted,
                "session",
                1u,
                11u));
            processEvents.Invoke(controller, null);
            Assert.IsFalse(engine.IsRunning);

            transport.State = NetworkSessionState.Reconnecting;
            transport.IsRunning = false;
            Assert.IsTrue((bool)shouldUseNetwork.Invoke(controller, null));

            transport.State = NetworkSessionState.Terminated;
            transport.HasStartedSession = false;
            transport.Events.Enqueue(new NetworkTransportEvent(
                NetworkSessionState.Terminated,
                NetworkTransportEventReason.ResumeGraceExpired,
                "session",
                1u,
                20u));
            LogAssert.Expect(
                LogType.Error,
                "[RouteC][MatchTerminalFault] state=Terminated reason=ResumeGraceExpired");
            processEvents.Invoke(controller, null);

            Assert.IsFalse(engine.IsRunning);
            Assert.IsTrue((bool)GetField(controller, "_paused"));
            Assert.IsNotEmpty((string)GetField(
                controller,
                "_networkTerminalFault"));

            UnityEngine.Object.DestroyImmediate(gameObject);
        }

        [Test]
        public void GameController_SessionStartedBeforeStatus_WaitsForRunningSnapshot()
        {
            var gameObject = new GameObject("NetworkStartPublicationRaceTest");
            var engine = gameObject.AddComponent<FrameEngine>();
            var client = gameObject.AddComponent<NetworkClient>();
            var controller = gameObject.AddComponent<GameController>();
            var transport = new FakeTransport
            {
                State = NetworkSessionState.AwaitingReady,
                HasStartedSession = false,
                IsRunning = false,
                LocalPlayerIndex = -1
            };

            try
            {
                SetField(client, "_transport", transport);
                SetField(controller, "_frameEngine", engine);
                SetField(controller, "_networkClient", client);
                engine.Initialize(2, 33);
                MethodInfo processEvents = typeof(GameController).GetMethod(
                    "ProcessNetworkTransportEvents",
                    PrivateInstance);
                Assert.IsNotNull(processEvents);

                transport.Events.Enqueue(new NetworkTransportEvent(
                    NetworkSessionState.Running,
                    NetworkTransportEventReason.SessionStarted,
                    "session",
                    1u,
                    10u));
                processEvents.Invoke(controller, null);
                Assert.IsFalse(
                    engine.IsRunning,
                    "An event without its Running snapshot must remain pending.");

                transport.State = NetworkSessionState.Running;
                transport.HasStartedSession = true;
                transport.IsRunning = true;
                transport.LocalPlayerIndex = 1;
                processEvents.Invoke(controller, null);
                Assert.IsTrue(engine.IsRunning);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
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

        private sealed class FakeTransport : IFrameTransportClient
        {
            public readonly Queue<NetworkPacketArrival> RemoteInputs =
                new Queue<NetworkPacketArrival>();
            public readonly Queue<NetworkTransportEvent> Events =
                new Queue<NetworkTransportEvent>();

            public NetworkSessionState State { get; set; }
            public bool IsRunning { get; set; }
            public bool HasStartedSession { get; set; }
            public int LocalPlayerIndex { get; set; }
            public int LatestRemoteFrameID { get; set; }
            public uint LastSentRaw { get; private set; }
            public int LastSentFrameID { get; private set; }
            public bool StopRequested => StopRequestCount > 0;
            public bool WaitCalled => WaitCallCount > 0;
            public bool Disposed => DisposeCallCount > 0;
            public int StopRequestCount { get; private set; }
            public int WaitCallCount { get; private set; }
            public int DisposeCallCount { get; private set; }
            public bool AcceptLocalInput { get; set; } = true;

            public void Start(string host, int port)
            {
            }

            public bool TryEnqueueLocalInput(uint raw, int localFrameID)
            {
                LastSentRaw = raw;
                LastSentFrameID = localFrameID;
                return AcceptLocalInput;
            }

            public bool TryDequeueRemoteInput(out NetworkPacketArrival arrival)
            {
                if (RemoteInputs.Count > 0)
                {
                    arrival = RemoteInputs.Dequeue();
                    return true;
                }

                arrival = default;
                return false;
            }

            public bool TryDequeueEvent(out NetworkTransportEvent transportEvent)
            {
                if (Events.Count > 0)
                {
                    transportEvent = Events.Dequeue();
                    return true;
                }

                transportEvent = default;
                return false;
            }

            public void SubmitResumeReadiness(in ResumeReadiness readiness)
            {
            }

            public void RequestStop()
            {
                StopRequestCount++;
            }

            public bool WaitForStop(int millisecondsTimeout)
            {
                WaitCallCount++;
                return true;
            }

            public void Dispose()
            {
                DisposeCallCount++;
                RequestStop();
                WaitForStop(
                    NetworkConfig.InitialKcpIntervalMs +
                    NetworkConfig.WorkerStopMarginMs);
            }
        }
    }
}

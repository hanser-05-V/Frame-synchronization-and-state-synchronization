using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class NetworkLedgerWiringTests
    {
        [Test]
        public void NetworkClient_HasNoSecondRemoteActualDictionaryOrIndexApis()
        {
            Assert.IsNull(typeof(NetworkClient).GetField(
                "_remoteInputDict",
                BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.IsNull(typeof(NetworkClient).GetMethod("DrainQueueToDict"));
            Assert.IsNull(typeof(NetworkClient).GetMethod("TryGetRemoteInputAt"));
            Assert.IsNull(typeof(NetworkClient).GetMethod("CleanupRemoteInputs"));
            Assert.IsNull(typeof(NetworkClient).GetProperty("LatestDrainedRemoteFrameID"));
        }

        [Test]
        public void ConcreteTransportsOwnArrivalFifoAndFacadeKeepsDequeueSurfaces()
        {
            FieldInfo[] queues = typeof(NetworkClient)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(field => field.FieldType ==
                    typeof(ConcurrentQueue<NetworkPacketArrival>))
                .ToArray();

            Assert.AreEqual(0, queues.Length);
            Assert.AreEqual(1, CountArrivalQueues(typeof(KcpUdpClientTransport)));
            Assert.AreEqual(1, CountArrivalQueues(typeof(TcpClientTransport)));
            Assert.IsNotNull(typeof(NetworkClient).GetMethod(
                "TryGetRemoteInput",
                new[] { typeof(NetworkPacketArrival).MakeByRefType() }));
            Assert.IsNotNull(typeof(NetworkClient).GetMethod(
                "TryGetRemoteInput",
                new[]
                {
                    typeof(uint).MakeByRefType(),
                    typeof(int).MakeByRefType()
                }));
        }

        private static int CountArrivalQueues(System.Type transportType)
        {
            return transportType
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Count(field => field.FieldType ==
                    typeof(ConcurrentQueue<NetworkPacketArrival>));
        }

        [Test]
        public void NetworkPacketArrival_PreservesProtocolAndReceiveMetadata()
        {
            var arrival = new NetworkPacketArrival(
                0x12345678u,
                42,
                7,
                9001);

            Assert.AreEqual(0x12345678u, arrival.Raw);
            Assert.AreEqual(42, arrival.RemoteFrameID);
            Assert.AreEqual(7, arrival.ReceiveSequence);
            Assert.AreEqual(9001, arrival.ReceivedTimestamp);
        }

        [Test]
        public void Ledger_CanonicalRemoteArrivalsOutOfOrder_ConfirmsOnlyWhenGapFills()
        {
            var ledger = new FrameInputLedger();
            RecordBoth(ledger, 0);
            RecordBoth(ledger, 2);
            Assert.AreEqual(0, ledger.ConfirmedThroughFrame);

            RecordBoth(ledger, 1);

            Assert.AreEqual(2, ledger.ConfirmedThroughFrame);
        }

        [Test]
        public void Ledger_PlayerOneLocalAndRemoteFrames_MapToSameCanonicalFrameBeforeAccess()
        {
            const int offset = 17;
            const int canonicalFrame = 42;
            const int playerOneLocalFrame = canonicalFrame + offset;

            Assert.IsTrue(CanonicalFrame.TryFromLocal(
                1,
                playerOneLocalFrame,
                offset,
                out int localCanonicalFrame));
            Assert.IsTrue(CanonicalFrame.TryFromLocal(
                0,
                canonicalFrame,
                offset,
                out int remoteCanonicalFrame));
            Assert.AreEqual(canonicalFrame, localCanonicalFrame);
            Assert.AreEqual(canonicalFrame, remoteCanonicalFrame);

            var ledger = new FrameInputLedger();
            ledger.RecordActual(localCanonicalFrame, 1, new FrameInput(0x200u));
            ledger.RecordActual(remoteCanonicalFrame, 0, new FrameInput(0x100u));

            Assert.IsTrue(ledger.TryGetActualFrame(
                canonicalFrame,
                out FrameInputLedger.ResolvedFrame actual));
            Assert.AreEqual(0x100u, actual.GetPlayer(0).Value._raw);
            Assert.AreEqual(0x200u, actual.GetPlayer(1).Value._raw);
        }

        private static void RecordBoth(FrameInputLedger ledger, int frame)
        {
            ledger.RecordActual(frame, 0, new FrameInput((uint)(0x100 + frame)));
            ledger.RecordActual(frame, 1, new FrameInput((uint)(0x200 + frame)));
        }
    }
}

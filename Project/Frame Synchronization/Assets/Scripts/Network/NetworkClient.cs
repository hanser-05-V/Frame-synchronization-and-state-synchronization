using System;
using System.Security.Cryptography;
using UnityEngine;

namespace FrameSyncDemo
{
    public class NetworkClient : MonoBehaviour
    {
        [Header("连接配置")]
        [SerializeField] private NetworkTransportKind _transportKind =
            NetworkConfig.Transport;
        [SerializeField] private string _serverIP = NetworkConfig.DEFAULT_IP;
        [SerializeField] private int _serverPort = NetworkConfig.DEFAULT_PORT;

        private IFrameTransportClient _transport;

        public bool IsConnected => _transport != null && _transport.IsRunning;

        public bool HasStartedSession =>
            _transport != null && _transport.HasStartedSession;

        public NetworkSessionState State => _transport == null
            ? NetworkSessionState.Disconnected
            : _transport.State;

        public int LocalPlayerIndex => _transport == null
            ? -1
            : _transport.LocalPlayerIndex;

        public int RemotePlayerIndex => LocalPlayerIndex == 0 ? 1 : 0;

        public int LatestRemoteFrameID => _transport == null
            ? -1
            : _transport.LatestRemoteFrameID;

        public void Connect(string ip, int port)
        {
            Connect(new NetworkConnectionOptions(
                NetworkConfig.Transport,
                ip,
                port));
        }

        public void ConnectConfigured()
        {
            NetworkConnectionOptions options = NetworkConnectionOptions.Resolve(
                Environment.GetCommandLineArgs(),
                _transportKind,
                _serverIP,
                _serverPort);
            Connect(options);
        }

        private void Connect(NetworkConnectionOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (_transport != null)
            {
                throw new InvalidOperationException(
                    "Network client already owns a transport.");
            }

            _transportKind = options.Transport;
            _serverIP = options.Host;
            _serverPort = options.Port;
            _transport = CreateTransport(options.Transport);
            _transport.Start(_serverIP, _serverPort);
        }

        public bool SendInput(uint raw)
        {
            return SendInput(raw, -1);
        }

        public bool SendInput(uint raw, int localFrameID)
        {
            return _transport != null &&
                   _transport.TryEnqueueLocalInput(raw, localFrameID);
        }

        public bool TryGetRemoteInput(out uint raw, out int remoteFrameID)
        {
            if (TryGetRemoteInput(out NetworkPacketArrival arrival))
            {
                raw = arrival.Raw;
                remoteFrameID = arrival.RemoteFrameID;
                return true;
            }

            raw = 0u;
            remoteFrameID = -1;
            return false;
        }

        public bool TryGetRemoteInput(out NetworkPacketArrival arrival)
        {
            if (_transport != null)
                return _transport.TryDequeueRemoteInput(out arrival);

            arrival = default;
            return false;
        }

        public bool TryGetTransportEvent(
            out NetworkTransportEvent transportEvent)
        {
            if (_transport != null)
                return _transport.TryDequeueEvent(out transportEvent);

            transportEvent = default;
            return false;
        }

        public void SubmitResumeReadiness(in ResumeReadiness readiness)
        {
            _transport?.SubmitResumeReadiness(in readiness);
        }

        private static IFrameTransportClient CreateTransport(
            NetworkTransportKind transportKind)
        {
            switch (transportKind)
            {
                case NetworkTransportKind.KcpUdp:
                    return new KcpUdpClientTransport(
                        new StopwatchMonotonicClock(),
                        CreateNonce);
                case NetworkTransportKind.Tcp:
                    return new TcpClientTransport();
                case NetworkTransportKind.RawUdp:
                    return new RawUdpInputTransport(
                        new StopwatchMonotonicClock(),
                        CreateNonce);
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(transportKind));
            }
        }

        private static byte[] CreateNonce()
        {
            var nonce = new byte[RouteCProtocolConstants.NonceSize];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
                random.GetBytes(nonce);
            return nonce;
        }

        private void OnDestroy()
        {
            if (_transport == null)
                return;

            _transport.Dispose();
            _transport = null;
        }

        private void OnApplicationQuit()
        {
            _transport?.RequestStop();
        }
    }
}

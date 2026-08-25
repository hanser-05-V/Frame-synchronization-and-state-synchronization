using System;
using System.Net;
using FrameSyncDemo;

namespace FrameSyncServer
{
    public sealed class RawUdpServerPlayerSlot
    {
        private readonly byte[] _nonce;
        private readonly byte[] _welcomeDatagram;

        public RawUdpServerPlayerSlot(
            byte playerIndex,
            IPEndPoint endpoint,
            byte[] nonce,
            RouteCSessionId sessionId,
            int windowSize,
            byte[] welcomeDatagram)
        {
            PlayerIndex = playerIndex;
            Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            if (nonce == null)
                throw new ArgumentNullException(nameof(nonce));
            if (welcomeDatagram == null)
                throw new ArgumentNullException(nameof(welcomeDatagram));
            _nonce = (byte[])nonce.Clone();
            _welcomeDatagram = (byte[])welcomeDatagram.Clone();
            SessionId = sessionId;
            InputReceiver = new RawUdpInputReceiver(windowSize, false);
            RelayHistory = new RawUdpRelayHistory(windowSize);
        }

        public byte PlayerIndex { get; }
        public IPEndPoint Endpoint { get; }
        public RouteCSessionId SessionId { get; }
        public bool IsReady { get; internal set; }
        public bool HasFrameZero { get; internal set; }
        public RawUdpInputReceiver InputReceiver { get; }
        public RawUdpRelayHistory RelayHistory { get; }
        public byte[] Nonce => (byte[])_nonce.Clone();
        public byte[] WelcomeDatagram => (byte[])_welcomeDatagram.Clone();

        public bool EndpointEquals(IPEndPoint endpoint)
        {
            return endpoint != null &&
                   Endpoint.Port == endpoint.Port &&
                   Endpoint.Address.Equals(endpoint.Address);
        }

        public bool NonceEquals(byte[] nonce)
        {
            if (nonce == null || nonce.Length != _nonce.Length)
                return false;
            for (int index = 0; index < _nonce.Length; index++)
            {
                if (_nonce[index] != nonce[index])
                    return false;
            }
            return true;
        }
    }
}

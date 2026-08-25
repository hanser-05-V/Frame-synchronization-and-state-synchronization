using System;
using System.Net;
using FrameSyncDemo;

namespace FrameSyncServer
{
    public sealed class KcpServerSession
    {
        private byte[] _clientNonce;
        private byte[] _reconnectToken;
        private byte[] _welcomeDatagram;
        private byte[] _previousReconnectToken;
        private byte[] _reconnectNonce;
        private uint _reconnectRequestGeneration;
        private bool _ready;
        private byte[] _startDatagram;

        public KcpServerSession(
            byte playerIndex,
            RouteCSessionId sessionId,
            uint generation,
            uint conversation,
            IPEndPoint endpoint)
        {
            if (playerIndex > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(playerIndex));
            }
            if (sessionId.IsZero)
            {
                throw new ArgumentException(
                    "KCP server SessionID must be nonzero.",
                    nameof(sessionId));
            }
            if (generation == 0u)
            {
                throw new ArgumentOutOfRangeException(nameof(generation));
            }
            if (conversation == 0u)
            {
                throw new ArgumentOutOfRangeException(nameof(conversation));
            }
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            PlayerIndex = playerIndex;
            SessionId = sessionId;
            Generation = generation;
            Conversation = conversation;
            Endpoint = new IPEndPoint(endpoint.Address, endpoint.Port);
            State = NetworkSessionState.AwaitingReady;
            History = new ServerInputHistory(
                RouteCProtocolConstants.InputHistoryCapacity);
        }

        internal KcpServerSession(
            byte playerIndex,
            RouteCSessionId sessionId,
            uint generation,
            uint conversation,
            IPEndPoint endpoint,
            byte[] clientNonce,
            byte[] reconnectToken,
            byte[] welcomeDatagram)
            : this(
                playerIndex,
                sessionId,
                generation,
                conversation,
                endpoint)
        {
            if (clientNonce == null ||
                clientNonce.Length != RouteCProtocolConstants.NonceSize)
            {
                throw new ArgumentException(
                    "Client nonce has the wrong length.",
                    nameof(clientNonce));
            }
            if (reconnectToken == null ||
                reconnectToken.Length !=
                RouteCProtocolConstants.ReconnectTokenSize)
            {
                throw new ArgumentException(
                    "Reconnect token has the wrong length.",
                    nameof(reconnectToken));
            }
            if (welcomeDatagram == null)
            {
                throw new ArgumentNullException(nameof(welcomeDatagram));
            }

            _clientNonce = (byte[])clientNonce.Clone();
            _reconnectToken = (byte[])reconnectToken.Clone();
            _welcomeDatagram = (byte[])welcomeDatagram.Clone();
        }

        public byte PlayerIndex { get; }
        public RouteCSessionId SessionId { get; }
        public uint Generation { get; private set; }
        public uint Conversation { get; private set; }
        public IPEndPoint Endpoint { get; private set; }
        public NetworkSessionState State { get; private set; }
        public uint LastValidActivityAt { get; private set; }
        internal bool IsReady => _ready;
        internal ResumeReadiness? ResumeReadiness { get; private set; }
        public ServerInputHistory History { get; }
        internal RouteCKcpSession KcpSession { get; private set; }

        internal bool MatchesInitialHandshake(
            IPEndPoint endpoint,
            byte[] nonce)
        {
            return Generation == 1u &&
                   Endpoint.Equals(endpoint) &&
                   BytesEqual(_clientNonce, nonce);
        }

        internal byte[] CopyWelcomeDatagram()
        {
            return _welcomeDatagram == null
                ? null
                : (byte[])_welcomeDatagram.Clone();
        }

        internal bool MatchesReconnectToken(byte[] token)
        {
            return BytesEqual(_reconnectToken, token);
        }

        internal bool MatchesReconnectRetry(
            IPEndPoint endpoint,
            uint requestGeneration,
            byte[] oldToken,
            byte[] newNonce)
        {
            return endpoint != null &&
                   Endpoint.Equals(endpoint) &&
                   requestGeneration == _reconnectRequestGeneration &&
                   BytesEqual(_previousReconnectToken, oldToken) &&
                   BytesEqual(_reconnectNonce, newNonce);
        }

        internal void SwitchGeneration(
            uint generation,
            uint conversation,
            IPEndPoint endpoint,
            byte[] newNonce,
            byte[] newReconnectToken,
            byte[] welcomeDatagram)
        {
            if (generation != Generation + 1u)
            {
                throw new ArgumentOutOfRangeException(nameof(generation));
            }
            if (conversation == 0u)
            {
                throw new ArgumentOutOfRangeException(nameof(conversation));
            }
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }
            if (newNonce == null ||
                newNonce.Length != RouteCProtocolConstants.NonceSize)
            {
                throw new ArgumentException(
                    "Reconnect nonce has the wrong length.",
                    nameof(newNonce));
            }
            if (newReconnectToken == null ||
                newReconnectToken.Length !=
                RouteCProtocolConstants.ReconnectTokenSize)
            {
                throw new ArgumentException(
                    "Reconnect token has the wrong length.",
                    nameof(newReconnectToken));
            }
            if (welcomeDatagram == null)
            {
                throw new ArgumentNullException(nameof(welcomeDatagram));
            }

            _reconnectRequestGeneration = Generation;
            _previousReconnectToken = _reconnectToken == null
                ? null
                : (byte[])_reconnectToken.Clone();
            _reconnectNonce = (byte[])newNonce.Clone();
            _reconnectToken = (byte[])newReconnectToken.Clone();
            _welcomeDatagram = (byte[])welcomeDatagram.Clone();
            Generation = generation;
            Conversation = conversation;
            Endpoint = new IPEndPoint(endpoint.Address, endpoint.Port);
            _ready = false;
            ResumeReadiness = null;
            _startDatagram = null;
            State = NetworkSessionState.Resuming;
            KcpSession = null;
        }

        internal void AttachKcp(
            RouteCKcpSettings settings,
            Action<byte[], int> output)
        {
            KcpSession = new RouteCKcpSession(
                Conversation,
                settings,
                output);
        }

        internal void MarkReady(uint nowMs)
        {
            _ready = true;
            MarkValidActivity(nowMs);
        }

        internal void MarkResumeReady(
            ResumeReadiness readiness,
            uint nowMs)
        {
            ResumeReadiness = readiness;
            MarkValidActivity(nowMs);
        }

        internal void BeginPeerResume()
        {
            if (State != NetworkSessionState.Running)
            {
                throw new InvalidOperationException(
                    "Only a running online peer can enter resume coordination.");
            }

            ResumeReadiness = null;
            State = NetworkSessionState.Resuming;
        }

        internal void CompleteResume()
        {
            if (State != NetworkSessionState.Resuming)
            {
                throw new InvalidOperationException(
                    "Only a resuming session can complete resume.");
            }

            ResumeReadiness = null;
            State = NetworkSessionState.Running;
        }

        internal void MarkValidActivity(uint nowMs)
        {
            LastValidActivityAt = nowMs;
        }

        internal void BeginRunning(byte[] startDatagram)
        {
            if (startDatagram == null)
            {
                throw new ArgumentNullException(nameof(startDatagram));
            }

            _startDatagram = (byte[])startDatagram.Clone();
            State = NetworkSessionState.Running;
        }

        internal byte[] CopyStartDatagram()
        {
            return _startDatagram == null
                ? null
                : (byte[])_startDatagram.Clone();
        }

        internal void MarkTimedOut()
        {
            if (State == NetworkSessionState.Running)
            {
                State = NetworkSessionState.Reconnecting;
            }
        }

        internal void Terminate()
        {
            State = NetworkSessionState.Terminated;
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            int difference = 0;
            for (int index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }
            return difference == 0;
        }
    }
}

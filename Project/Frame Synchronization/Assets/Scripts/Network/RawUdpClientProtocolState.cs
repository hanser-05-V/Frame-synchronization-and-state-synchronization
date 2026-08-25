using System;
using System.Collections.Generic;

namespace FrameSyncDemo
{
    public sealed class RawUdpClientProtocolState
    {
        private readonly IMonotonicClock _clock;
        private readonly byte[] _clientNonce;
        private readonly Action<RouteCProtocolMessage> _send;
        private readonly Action<NetworkTransportEvent> _publishEvent;
        private readonly Action<RawUdpInputEntry> _publishArrival;
        private readonly SortedDictionary<int, RawUdpInputEntry> _preStartInputs =
            new SortedDictionary<int, RawUdpInputEntry>();

        private uint _handshakeStartedAt;
        private uint _lastControlSendAt;
        private uint _lastValidTrafficAt;
        private RouteCProtocolMessage _acceptedWelcome;
        private RawUdpInputReceiver _inputReceiver;

        public RawUdpClientProtocolState(
            IMonotonicClock clock,
            byte[] clientNonce,
            Action<RouteCProtocolMessage> send,
            Action<NetworkTransportEvent> publishEvent,
            Action<RawUdpInputEntry> publishArrival)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            if (clientNonce == null)
                throw new ArgumentNullException(nameof(clientNonce));
            if (clientNonce.Length != RouteCProtocolConstants.NonceSize)
                throw new ArgumentException("Client nonce must be exactly 16 bytes.", nameof(clientNonce));
            _clientNonce = (byte[])clientNonce.Clone();
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _publishEvent = publishEvent ?? throw new ArgumentNullException(nameof(publishEvent));
            _publishArrival = publishArrival ?? throw new ArgumentNullException(nameof(publishArrival));

            State = NetworkSessionState.Disconnected;
            LocalPlayerIndex = -1;
            LatestRemoteFrameID = -1;
        }

        public NetworkSessionState State { get; private set; }
        public bool HasStartedSession { get; private set; }
        public int LocalPlayerIndex { get; private set; }
        public int NegotiatedWindowSize { get; private set; }
        public int LatestRemoteFrameID { get; private set; }
        public RouteCSessionId SessionId { get; private set; }

        public void Start()
        {
            if (State != NetworkSessionState.Disconnected)
                throw new InvalidOperationException("Raw UDP session has already started.");

            _handshakeStartedAt = _clock.Milliseconds;
            _lastValidTrafficAt = _handshakeStartedAt;
            Transition(
                NetworkSessionState.Handshaking,
                NetworkTransportEventReason.StartRequested);
            SendHello();
        }

        public void Tick()
        {
            if (State == NetworkSessionState.Handshaking ||
                State == NetworkSessionState.AwaitingReady)
            {
                if (ElapsedSince(_handshakeStartedAt) >=
                    RouteCProtocolConstants.RawHandshakeTimeoutMs)
                {
                    Terminate(
                        NetworkTransportEventReason.HandshakeTimeout,
                        default,
                        false);
                    return;
                }

                if (ElapsedSince(_lastControlSendAt) >=
                    RouteCProtocolConstants.RawHandshakeRetryMs)
                {
                    if (State == NetworkSessionState.Handshaking)
                        SendHello();
                    else
                        SendReady();
                }
                return;
            }

            if (State == NetworkSessionState.Running &&
                ElapsedSince(_lastValidTrafficAt) >=
                RouteCProtocolConstants.RawValidTrafficTimeoutMs)
            {
                var fault = new RawUdpFault(
                    RawUdpFaultReason.ConnectionTimedOut,
                    CurrentFaultPlayerIndex(),
                    -1,
                    LatestRemoteFrameID);
                Terminate(
                    NetworkTransportEventReason.ConnectionTimedOut,
                    fault,
                    true);
            }
        }

        public void HandleIncoming(RouteCProtocolMessage message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));
            if (State == NetworkSessionState.Terminated ||
                State == NetworkSessionState.Disconnected)
            {
                return;
            }

            if (HasUnknownSessionOrStaleGeneration(message))
                return;

            if (!RawUdpProtocolCodec.TryValidateOuterMessage(
                message,
                false,
                out _))
            {
                TerminateProtocolViolation(-1);
                return;
            }

            switch (message.MessageType)
            {
                case RouteCMessageType.RawWelcome:
                    HandleWelcome(message);
                    break;
                case RouteCMessageType.RawStart:
                    HandleStart(message);
                    break;
                case RouteCMessageType.RawInput:
                    HandleInput(message);
                    break;
                case RouteCMessageType.RawFault:
                    HandleFault(message);
                    break;
                default:
                    TerminateProtocolViolation(-1);
                    break;
            }
        }

        public void RequestStop()
        {
            if (State == NetworkSessionState.Terminated ||
                State == NetworkSessionState.Disconnected)
            {
                return;
            }

            Transition(
                NetworkSessionState.Terminated,
                NetworkTransportEventReason.StopRequested);
        }

        public void TerminateLocalFault(
            in RawUdpFault fault,
            NetworkTransportEventReason eventReason)
        {
            Terminate(eventReason, fault, true);
        }

        private void HandleWelcome(RouteCProtocolMessage message)
        {
            if (State != NetworkSessionState.Handshaking)
            {
                if (_acceptedWelcome != null && MessagesEqual(_acceptedWelcome, message))
                {
                    _lastValidTrafficAt = _clock.Milliseconds;
                    if (State == NetworkSessionState.AwaitingReady)
                        SendReady();
                    return;
                }

                TerminateProtocolViolation(-1);
                return;
            }

            if (!RawUdpProtocolCodec.TryDecodeWelcome(
                message.Payload,
                out byte[] echoNonce,
                out byte playerIndex,
                out byte windowSize) ||
                !BytesEqual(_clientNonce, echoNonce))
            {
                TerminateProtocolViolation(-1);
                return;
            }

            SessionId = message.SessionId;
            LocalPlayerIndex = playerIndex;
            NegotiatedWindowSize = windowSize;
            _acceptedWelcome = new RouteCProtocolMessage(
                message.MessageType,
                message.SessionId,
                message.Generation,
                message.Payload);
            _inputReceiver = new RawUdpInputReceiver(windowSize, true);
            _lastValidTrafficAt = _clock.Milliseconds;
            Transition(
                NetworkSessionState.AwaitingReady,
                NetworkTransportEventReason.WelcomeAccepted);
            SendReady();
        }

        private void HandleStart(RouteCProtocolMessage message)
        {
            if (!HasExpectedSession(message) ||
                !RawUdpProtocolCodec.TryDecodeStart(
                    message.Payload,
                    out int canonicalStartFrame) ||
                canonicalStartFrame != 0)
            {
                TerminateProtocolViolation(-1);
                return;
            }

            if (State == NetworkSessionState.Running)
            {
                _lastValidTrafficAt = _clock.Milliseconds;
                return;
            }
            if (State != NetworkSessionState.AwaitingReady)
            {
                TerminateProtocolViolation(-1);
                return;
            }

            HasStartedSession = true;
            _lastValidTrafficAt = _clock.Milliseconds;
            Transition(
                NetworkSessionState.Running,
                NetworkTransportEventReason.SessionStarted);
            foreach (RawUdpInputEntry entry in _preStartInputs.Values)
                _publishArrival(entry);
            _preStartInputs.Clear();
        }

        private void HandleInput(RouteCProtocolMessage message)
        {
            if ((State != NetworkSessionState.AwaitingReady &&
                 State != NetworkSessionState.Running) ||
                !HasExpectedSession(message) ||
                !RawUdpProtocolCodec.TryDecodeInput(
                    message.Payload,
                    (byte)NegotiatedWindowSize,
                    out RawUdpInputWindow window,
                    out _))
            {
                TerminateProtocolViolation(-1);
                return;
            }

            int expectedRemotePlayer = LocalPlayerIndex == 0 ? 1 : 0;
            if (window.PlayerIndex != expectedRemotePlayer)
            {
                TerminateProtocolViolation(window.LatestFrameID);
                return;
            }

            RawUdpInputDisposition disposition = _inputReceiver.Accept(
                in window,
                out RawUdpInputEntry[] acceptedEntries,
                out RawUdpFault terminalFault);
            LatestRemoteFrameID = _inputReceiver.HighestObservedLatestFrameID;
            if (disposition == RawUdpInputDisposition.Conflict ||
                disposition == RawUdpInputDisposition.CapacityExceeded ||
                disposition == RawUdpInputDisposition.UnrecoverableGap)
            {
                Terminate(
                    MapFaultReason(terminalFault.Reason),
                    terminalFault,
                    true);
                return;
            }

            _lastValidTrafficAt = _clock.Milliseconds;
            if (acceptedEntries.Length == 0)
                return;

            if (State == NetworkSessionState.AwaitingReady)
            {
                if (_preStartInputs.Count + acceptedEntries.Length >
                    RouteCProtocolConstants.RawQueueCapacity)
                {
                    var fault = new RawUdpFault(
                        RawUdpFaultReason.CapacityExceeded,
                        (byte)expectedRemotePlayer,
                        acceptedEntries[0].FrameID,
                        LatestRemoteFrameID);
                    Terminate(
                        NetworkTransportEventReason.CapacityExceeded,
                        fault,
                        true);
                    return;
                }

                for (int index = 0; index < acceptedEntries.Length; index++)
                {
                    RawUdpInputEntry entry = acceptedEntries[index];
                    _preStartInputs.Add(entry.FrameID, entry);
                }
                return;
            }

            for (int index = 0; index < acceptedEntries.Length; index++)
                _publishArrival(acceptedEntries[index]);
        }

        private void HandleFault(RouteCProtocolMessage message)
        {
            if (!RawUdpProtocolCodec.TryDecodeFault(
                message.Payload,
                out RawUdpFault fault))
            {
                TerminateProtocolViolation(-1);
                return;
            }

            if (fault.Reason == RawUdpFaultReason.MatchFull)
            {
                if (State != NetworkSessionState.Handshaking)
                {
                    TerminateProtocolViolation(-1);
                    return;
                }
            }
            else if (!HasExpectedSession(message))
            {
                TerminateProtocolViolation(fault.FrameID);
                return;
            }

            Terminate(MapFaultReason(fault.Reason), fault, false);
        }

        private void SendHello()
        {
            _send(new RouteCProtocolMessage(
                RouteCMessageType.RawHello,
                RouteCSessionId.Zero,
                0u,
                RawUdpProtocolCodec.EncodeHello(_clientNonce)));
            _lastControlSendAt = _clock.Milliseconds;
        }

        private void SendReady()
        {
            _send(new RouteCProtocolMessage(
                RouteCMessageType.RawReady,
                SessionId,
                RouteCProtocolConstants.RawGeneration,
                RawUdpProtocolCodec.EncodeReady()));
            _lastControlSendAt = _clock.Milliseconds;
        }

        private void TerminateProtocolViolation(int frameID)
        {
            var fault = new RawUdpFault(
                RawUdpFaultReason.ProtocolViolation,
                CurrentFaultPlayerIndex(),
                frameID,
                LatestRemoteFrameID);
            Terminate(
                NetworkTransportEventReason.ProtocolViolation,
                fault,
                true);
        }

        private void Terminate(
            NetworkTransportEventReason eventReason,
            RawUdpFault fault,
            bool sendToPeer)
        {
            if (State == NetworkSessionState.Terminated)
                return;

            if (sendToPeer && !SessionId.IsZero)
            {
                _send(new RouteCProtocolMessage(
                    RouteCMessageType.RawFault,
                    SessionId,
                    RouteCProtocolConstants.RawGeneration,
                    RawUdpProtocolCodec.EncodeFault(in fault)));
            }
            Transition(NetworkSessionState.Terminated, eventReason);
        }

        private void Transition(
            NetworkSessionState state,
            NetworkTransportEventReason reason)
        {
            State = state;
            _publishEvent(new NetworkTransportEvent(
                state,
                reason,
                RawUdpSessionFingerprint.Format(SessionId),
                SessionId.IsZero ? 0u : RouteCProtocolConstants.RawGeneration,
                _clock.Milliseconds));
        }

        private bool HasExpectedSession(RouteCProtocolMessage message)
        {
            return !SessionId.IsZero &&
                   message.SessionId == SessionId &&
                   message.Generation == RouteCProtocolConstants.RawGeneration;
        }

        private bool HasUnknownSessionOrStaleGeneration(
            RouteCProtocolMessage message)
        {
            if (!SessionId.IsZero)
            {
                return message.SessionId != SessionId ||
                       message.Generation !=
                       RouteCProtocolConstants.RawGeneration;
            }

            if (State != NetworkSessionState.Handshaking)
                return true;
            if (message.MessageType == RouteCMessageType.RawWelcome)
            {
                return message.SessionId.IsZero ||
                       message.Generation !=
                       RouteCProtocolConstants.RawGeneration;
            }
            return message.MessageType != RouteCMessageType.RawFault ||
                   !message.SessionId.IsZero ||
                   message.Generation != 0u;
        }

        private byte CurrentFaultPlayerIndex()
        {
            return LocalPlayerIndex < 0 ? (byte)255 : (byte)LocalPlayerIndex;
        }

        private uint ElapsedSince(uint timestamp)
        {
            return unchecked(_clock.Milliseconds - timestamp);
        }

        private static bool MessagesEqual(
            RouteCProtocolMessage left,
            RouteCProtocolMessage right)
        {
            return left.MessageType == right.MessageType &&
                   left.SessionId == right.SessionId &&
                   left.Generation == right.Generation &&
                   BytesEqual(left.Payload, right.Payload);
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;
            for (int index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                    return false;
            }
            return true;
        }

        private static NetworkTransportEventReason MapFaultReason(
            RawUdpFaultReason reason)
        {
            switch (reason)
            {
                case RawUdpFaultReason.HandshakeTimeout:
                    return NetworkTransportEventReason.HandshakeTimeout;
                case RawUdpFaultReason.ConnectionTimedOut:
                    return NetworkTransportEventReason.ConnectionTimedOut;
                case RawUdpFaultReason.ProtocolViolation:
                    return NetworkTransportEventReason.ProtocolViolation;
                case RawUdpFaultReason.ConflictingInput:
                    return NetworkTransportEventReason.ConflictingInput;
                case RawUdpFaultReason.UnrecoverableInputGap:
                    return NetworkTransportEventReason.UnrecoverableInputGap;
                case RawUdpFaultReason.CapacityExceeded:
                    return NetworkTransportEventReason.CapacityExceeded;
                case RawUdpFaultReason.MatchFull:
                    return NetworkTransportEventReason.MatchFull;
                default:
                    return NetworkTransportEventReason.PeerFault;
            }
        }
    }
}

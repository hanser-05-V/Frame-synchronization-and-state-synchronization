using System;
using System.Globalization;

namespace FrameSyncDemo
{
    public sealed class KcpUdpClientStateMachine
    {
        private readonly IMonotonicClock _clock;
        private readonly Func<byte[]> _nonceFactory;
        private readonly Action<RouteCProtocolMessage> _send;
        private readonly Action<NetworkTransportEvent> _publishEvent;

        private byte[] _activeNonce;
        private byte[] _reconnectToken;
        private uint _lastHandshakeSendAt;
        private uint _lastOutboundActivityAt;
        private uint _lastValidActivityAt;
        private RouteCSessionId _sessionId;
        private uint _generation;
        private uint _conversation;
        private ResumeReadiness _resumeReadiness = new ResumeReadiness(-1, 0, -1);
        private bool _resumeReadySent;
        private byte[] _resumeAttemptID;
        private bool _isResumePeer;
        private bool _resumeStateSent;
        private KcpClientResumePlan _acceptedResumePlan;
        private KcpClientResumePlan _pendingResumePlan;
        private bool _resumeCompleteSent;
        private int _uploadedLocalThrough = -1;
        private int _receivedRemoteThrough = -1;

        public KcpUdpClientStateMachine(
            IMonotonicClock clock,
            Func<byte[]> nonceFactory,
            Action<RouteCProtocolMessage> send,
            Action<NetworkTransportEvent> publishEvent)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _nonceFactory = nonceFactory ??
                throw new ArgumentNullException(nameof(nonceFactory));
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _publishEvent = publishEvent ??
                throw new ArgumentNullException(nameof(publishEvent));

            State = NetworkSessionState.Disconnected;
            LocalPlayerIndex = -1;
            LatestRemoteFrameID = -1;
        }

        public NetworkSessionState State { get; private set; }

        public bool IsRunning => State == NetworkSessionState.Running;

        public bool HasStartedSession { get; private set; }

        public int LocalPlayerIndex { get; private set; }

        public int LatestRemoteFrameID { get; private set; }

        public int UnexpectedControlCount { get; private set; }

        public int RejectedEnvelopeCount { get; private set; }

        public int ResumeWrongSessionCount { get; private set; }

        public int ResumeWrongGenerationCount { get; private set; }

        public int ResumeWrongAttemptCount { get; private set; }

        public int ResumeWrongDirectionCount { get; private set; }

        internal RouteCSessionId SessionId => _sessionId;

        internal uint Generation => _generation;

        internal uint Conversation => _conversation;

        public string DiagnosticSummary => string.Format(
            CultureInfo.InvariantCulture,
            "State={0} Session={1} Generation={2} UnexpectedControl={3} RejectedEnvelope={4}",
            State,
            SessionFingerprint(_sessionId),
            _generation,
            UnexpectedControlCount,
            RejectedEnvelopeCount);

        public void Start()
        {
            if (State != NetworkSessionState.Disconnected)
                throw new InvalidOperationException("Client session has already started.");

            _activeNonce = CreateNonce();
            Transition(
                NetworkSessionState.Handshaking,
                NetworkTransportEventReason.StartRequested);
            SendInitialHello();
        }

        public void Tick()
        {
            switch (State)
            {
                case NetworkSessionState.Handshaking:
                    if (ElapsedSince(_lastHandshakeSendAt) >=
                        RouteCProtocolConstants.HandshakeRetryMs)
                    {
                        SendInitialHello();
                    }
                    break;
                case NetworkSessionState.AwaitingReady:
                    if (ElapsedSince(_lastHandshakeSendAt) >=
                        RouteCProtocolConstants.HandshakeRetryMs)
                    {
                        SendReady();
                    }
                    break;
                case NetworkSessionState.Running:
                    if (ElapsedSince(_lastValidActivityAt) >=
                        RouteCProtocolConstants.DisconnectTimeoutMs)
                    {
                        _activeNonce = CreateNonce();
                        Transition(
                            NetworkSessionState.Reconnecting,
                            NetworkTransportEventReason.ConnectionTimedOut);
                        SendReconnectHello();
                    }
                    else if (ElapsedSince(_lastOutboundActivityAt) >=
                             RouteCProtocolConstants.HeartbeatSilenceMs)
                    {
                        SendHeartbeat();
                    }
                    break;
                case NetworkSessionState.Reconnecting:
                    if (ElapsedSince(_lastValidActivityAt) >=
                        RouteCProtocolConstants.ResumeGraceMs)
                    {
                        Transition(
                            NetworkSessionState.Terminated,
                            NetworkTransportEventReason.ResumeGraceExpired);
                    }
                    else if (ElapsedSince(_lastHandshakeSendAt) >=
                        RouteCProtocolConstants.HandshakeRetryMs)
                    {
                        SendReconnectHello();
                    }
                    break;
                case NetworkSessionState.Resuming:
                    if (ElapsedSince(_lastValidActivityAt) >=
                        RouteCProtocolConstants.ResumeGraceMs)
                    {
                        Transition(
                            NetworkSessionState.Terminated,
                            NetworkTransportEventReason.ResumeGraceExpired);
                    }
                    else if (_resumeCompleteSent &&
                             ElapsedSince(_lastHandshakeSendAt) >=
                             RouteCProtocolConstants.HandshakeRetryMs)
                    {
                        SendResumeComplete();
                    }
                    else if (_acceptedResumePlan == null &&
                             HasSentResumeControl &&
                             ElapsedSince(_lastHandshakeSendAt) >=
                             RouteCProtocolConstants.HandshakeRetryMs)
                    {
                        if (_isResumePeer)
                            SendResumeState();
                        else
                            SendReady();
                    }
                    break;
            }
        }

        internal uint NextActionAt(uint nowMs)
        {
            switch (State)
            {
                case NetworkSessionState.Handshaking:
                case NetworkSessionState.AwaitingReady:
                    return unchecked(
                        _lastHandshakeSendAt +
                        RouteCProtocolConstants.HandshakeRetryMs);
                case NetworkSessionState.Running:
                    return EarlierDeadline(
                        nowMs,
                        unchecked(
                            _lastValidActivityAt +
                            RouteCProtocolConstants.DisconnectTimeoutMs),
                        unchecked(
                            _lastOutboundActivityAt +
                            RouteCProtocolConstants.HeartbeatSilenceMs));
                case NetworkSessionState.Reconnecting:
                    return EarlierDeadline(
                        nowMs,
                        unchecked(
                            _lastValidActivityAt +
                            RouteCProtocolConstants.ResumeGraceMs),
                        unchecked(
                            _lastHandshakeSendAt +
                            RouteCProtocolConstants.HandshakeRetryMs));
                case NetworkSessionState.Resuming:
                    uint graceDeadline = unchecked(
                        _lastValidActivityAt +
                        RouteCProtocolConstants.ResumeGraceMs);
                    if (!HasSentResumeControl)
                        return graceDeadline;
                    return EarlierDeadline(
                        nowMs,
                        graceDeadline,
                        unchecked(
                            _lastHandshakeSendAt +
                            RouteCProtocolConstants.HandshakeRetryMs));
                default:
                    return nowMs;
            }
        }

        public void SubmitResumeReadiness(in ResumeReadiness readiness)
        {
            _resumeReadiness = readiness;
            if (State != NetworkSessionState.Resuming)
                return;

            if (_isResumePeer && !_resumeStateSent)
            {
                _resumeStateSent = true;
                SendResumeState();
            }
            else if (!_isResumePeer && !_resumeReadySent)
            {
                _resumeReadySent = true;
                SendReady();
            }
        }

        public void NotifyBusinessSent()
        {
            if (!HasStartedSession || State == NetworkSessionState.Terminated)
                return;

            _lastOutboundActivityAt = _clock.Milliseconds;
        }

        public bool TryTakeResumePlan(out KcpClientResumePlan plan)
        {
            plan = _pendingResumePlan;
            _pendingResumePlan = null;
            return plan != null;
        }

        public void SubmitResumeProgress(
            int uploadedLocalThrough,
            int receivedRemoteThrough)
        {
            if (State != NetworkSessionState.Resuming ||
                _acceptedResumePlan == null)
            {
                return;
            }

            if (uploadedLocalThrough > _uploadedLocalThrough)
                _uploadedLocalThrough = uploadedLocalThrough;
            if (receivedRemoteThrough > _receivedRemoteThrough)
                _receivedRemoteThrough = receivedRemoteThrough;
            if (!_resumeCompleteSent &&
                _uploadedLocalThrough >=
                    _acceptedResumePlan.RequiredUploadedThrough &&
                _receivedRemoteThrough >=
                    _acceptedResumePlan.ExpectedRemoteThrough)
            {
                _resumeCompleteSent = true;
                SendResumeComplete();
            }
        }

        internal void RejectResumeLocally(ResumeRejectedReason reason)
        {
            if (State != NetworkSessionState.Resuming ||
                _resumeAttemptID == null)
            {
                return;
            }

            _send(new RouteCProtocolMessage(
                RouteCMessageType.ResumeRejected,
                _sessionId,
                _generation,
                RouteCProtocolCodec.EncodeResumeRejected(
                    _resumeAttemptID,
                    (ushort)reason)));
            MarkHandshakeSent();
            Terminate(NetworkTransportEventReason.OutboundHistoryFault);
        }

        public void RequestStop()
        {
            Terminate(NetworkTransportEventReason.StopRequested);
        }

        internal void Terminate(NetworkTransportEventReason reason)
        {
            if (State == NetworkSessionState.Terminated)
                return;

            Transition(
                NetworkSessionState.Terminated,
                reason);
        }

        public void HandleIncoming(RouteCProtocolMessage message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            switch (State)
            {
                case NetworkSessionState.Handshaking:
                    HandleHandshakingMessage(message);
                    break;
                case NetworkSessionState.AwaitingReady:
                    HandleAwaitingReadyMessage(message);
                    break;
                case NetworkSessionState.Running:
                    HandleRunningMessage(message);
                    break;
                case NetworkSessionState.Reconnecting:
                    HandleReconnectingMessage(message);
                    break;
                case NetworkSessionState.Resuming:
                    HandleResumingMessage(message);
                    break;
                default:
                    UnexpectedControlCount++;
                    break;
            }
        }

        public bool AcceptKcpData(RouteCProtocolMessage message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            if ((State != NetworkSessionState.Running &&
                 State != NetworkSessionState.Resuming) ||
                message.MessageType != RouteCMessageType.KcpData ||
                message.SessionId != _sessionId ||
                message.Generation != _generation ||
                !RouteCKcpHeader.TryReadConversation(
                    message.Payload,
                    out uint conversation) ||
                conversation != _conversation)
            {
                RejectedEnvelopeCount++;
                return false;
            }

            MarkValidActivity();
            return true;
        }

        private void HandleHandshakingMessage(RouteCProtocolMessage message)
        {
            if (message.MessageType != RouteCMessageType.Welcome)
            {
                UnexpectedControlCount++;
                return;
            }

            if (!TryDecodeValidWelcome(
                    message,
                    out byte playerIndex,
                    out uint conversation,
                    out byte[] newReconnectToken,
                    out bool resumeRequired) ||
                resumeRequired ||
                message.SessionId.IsZero ||
                message.Generation != 1u)
            {
                RejectedEnvelopeCount++;
                return;
            }

            _sessionId = message.SessionId;
            _generation = message.Generation;
            _conversation = conversation;
            _reconnectToken = (byte[])newReconnectToken.Clone();
            LocalPlayerIndex = playerIndex;
            MarkValidActivity();
            Transition(
                NetworkSessionState.AwaitingReady,
                NetworkTransportEventReason.WelcomeAccepted);
            SendReady();
        }

        private void HandleAwaitingReadyMessage(RouteCProtocolMessage message)
        {
            if (message.MessageType == RouteCMessageType.Welcome)
            {
                if (IsDuplicateWelcome(message, false))
                {
                    MarkValidActivity();
                    SendReady();
                }
                else
                    RejectedEnvelopeCount++;
                return;
            }

            if (message.MessageType == RouteCMessageType.Start)
            {
                HandleStart(message);
                return;
            }

            UnexpectedControlCount++;
        }

        private void HandleStart(RouteCProtocolMessage message)
        {
            if (message.SessionId != _sessionId ||
                message.Generation != _generation ||
                !RouteCProtocolCodec.TryDecodeStart(
                    message.Payload,
                    out int canonicalStartFrame) ||
                canonicalStartFrame != 0)
            {
                RejectedEnvelopeCount++;
                return;
            }

            MarkValidActivity();
            HasStartedSession = true;
            Transition(
                NetworkSessionState.Running,
                NetworkTransportEventReason.SessionStarted);
        }

        private void HandleRunningMessage(RouteCProtocolMessage message)
        {
            if (TryHandleResumeRejected(message))
                return;

            if (TryHandleEstablishedAdvisory(message))
                return;

            if (message.MessageType == RouteCMessageType.ResumeProbe)
            {
                HandleInitialResumeProbe(message);
                return;
            }

            if (message.MessageType == RouteCMessageType.ResumeAccepted)
            {
                HandleResumeAccepted(message);
                return;
            }

            if (message.MessageType == RouteCMessageType.ResumeComplete)
            {
                HandleDuplicateServerResumeComplete(message);
                return;
            }

            if (message.MessageType == RouteCMessageType.ResumeState)
            {
                ResumeWrongDirectionCount++;
                return;
            }

            if (message.MessageType == RouteCMessageType.Start &&
                message.SessionId == _sessionId &&
                message.Generation == _generation &&
                RouteCProtocolCodec.TryDecodeStart(
                    message.Payload,
                    out int canonicalStartFrame) &&
                canonicalStartFrame == 0)
            {
                MarkValidActivity();
                return;
            }

            UnexpectedControlCount++;
        }

        private void HandleReconnectingMessage(RouteCProtocolMessage message)
        {
            if (TryHandleResumeRejected(message))
                return;

            if (TryHandleEstablishedAdvisory(message))
                return;

            if (message.MessageType != RouteCMessageType.Welcome)
            {
                UnexpectedControlCount++;
                return;
            }

            if (_generation == uint.MaxValue ||
                message.SessionId != _sessionId ||
                message.Generation != _generation + 1u ||
                !TryDecodeValidWelcome(
                    message,
                    out byte playerIndex,
                    out uint conversation,
                    out byte[] newReconnectToken,
                    out bool resumeRequired) ||
                playerIndex != LocalPlayerIndex)
            {
                RejectedEnvelopeCount++;
                return;
            }

            _generation = message.Generation;
            _conversation = conversation;
            _reconnectToken = (byte[])newReconnectToken.Clone();
            MarkValidActivity();

            if (resumeRequired)
            {
                _resumeReadySent = false;
                _resumeStateSent = false;
                _isResumePeer = false;
                _resumeAttemptID = null;
                ResetAcceptedResume();
                Transition(
                    NetworkSessionState.Resuming,
                    NetworkTransportEventReason.ResumeRequired);
            }
            else
            {
                Transition(
                    NetworkSessionState.Running,
                    NetworkTransportEventReason.ReconnectAccepted);
            }
        }

        private void HandleResumingMessage(RouteCProtocolMessage message)
        {
            if (TryHandleResumeRejected(message))
                return;

            if (TryHandleEstablishedAdvisory(message))
                return;

            if (message.MessageType == RouteCMessageType.ResumeProbe)
            {
                HandleDuplicateResumeProbe(message);
                return;
            }

            if (message.MessageType == RouteCMessageType.ResumeAccepted)
            {
                HandleResumeAccepted(message);
                return;
            }

            if (message.MessageType == RouteCMessageType.ResumeComplete)
            {
                HandleServerResumeComplete(message);
                return;
            }

            if (message.MessageType == RouteCMessageType.ResumeState)
            {
                ResumeWrongDirectionCount++;
                return;
            }

            if (message.MessageType == RouteCMessageType.Welcome)
            {
                if (IsDuplicateWelcome(message, true))
                {
                    MarkValidActivity();
                    if (_resumeReadySent)
                        SendReady();
                }
                else
                    RejectedEnvelopeCount++;
                return;
            }

            UnexpectedControlCount++;
        }

        private void HandleInitialResumeProbe(RouteCProtocolMessage message)
        {
            if (!TryValidateResumeProbe(message, out byte[] attemptID))
                return;

            if (_acceptedResumePlan != null &&
                BytesEqual(_resumeAttemptID, attemptID))
            {
                MarkValidActivity();
                if (_resumeCompleteSent)
                    SendResumeComplete();
                return;
            }

            _resumeAttemptID = attemptID;
            _isResumePeer = true;
            _resumeReadySent = false;
            _resumeStateSent = false;
            ResetAcceptedResume();
            MarkValidActivity();
            Transition(
                NetworkSessionState.Resuming,
                NetworkTransportEventReason.ResumeRequired);
        }

        private void HandleDuplicateResumeProbe(RouteCProtocolMessage message)
        {
            if (!_isResumePeer)
            {
                ResumeWrongDirectionCount++;
                return;
            }
            if (!TryValidateResumeProbe(message, out byte[] attemptID))
                return;
            if (!BytesEqual(_resumeAttemptID, attemptID))
            {
                ResumeWrongAttemptCount++;
                return;
            }

            MarkValidActivity();
            if (_resumeStateSent)
                SendResumeState();
        }

        private bool TryValidateResumeProbe(
            RouteCProtocolMessage message,
            out byte[] attemptID)
        {
            attemptID = null;
            if (message.SessionId != _sessionId)
            {
                ResumeWrongSessionCount++;
                return false;
            }
            if (message.Generation != _generation)
            {
                ResumeWrongGenerationCount++;
                return false;
            }
            if (!RouteCProtocolCodec.TryDecodeResumeProbe(
                    message.Payload,
                    out attemptID))
            {
                RejectedEnvelopeCount++;
                return false;
            }
            return true;
        }

        private void HandleResumeAccepted(RouteCProtocolMessage message)
        {
            if (message.SessionId != _sessionId)
            {
                ResumeWrongSessionCount++;
                return;
            }
            if (message.Generation != _generation)
            {
                ResumeWrongGenerationCount++;
                return;
            }
            if (!RouteCProtocolCodec.TryDecodeResumeAccepted(
                    message.Payload,
                    out byte[] attemptID,
                    out int uploadFrom,
                    out int uploadThrough,
                    out int replayFrom,
                    out int replayThrough,
                    out int peerFrom,
                    out int peerThrough))
            {
                RejectedEnvelopeCount++;
                return;
            }
            if (_resumeAttemptID != null &&
                !BytesEqual(_resumeAttemptID, attemptID))
            {
                ResumeWrongAttemptCount++;
                return;
            }

            KcpClientResumePlan plan;
            try
            {
                plan = new KcpClientResumePlan(
                    attemptID,
                    _isResumePeer,
                    uploadFrom,
                    uploadThrough,
                    replayFrom,
                    replayThrough,
                    peerFrom,
                    peerThrough);
            }
            catch (ArgumentException)
            {
                RejectedEnvelopeCount++;
                return;
            }

            if (_acceptedResumePlan != null)
            {
                if (!ResumePlansEqual(_acceptedResumePlan, plan))
                {
                    RejectedEnvelopeCount++;
                    return;
                }

                MarkValidActivity();
                if (_resumeCompleteSent)
                    SendResumeComplete();
                return;
            }

            _resumeAttemptID = (byte[])attemptID.Clone();
            _acceptedResumePlan = plan;
            _pendingResumePlan = plan;
            _uploadedLocalThrough = -1;
            _receivedRemoteThrough = -1;
            MarkValidActivity();
        }

        private void HandleServerResumeComplete(
            RouteCProtocolMessage message)
        {
            if (!TryValidateServerResumeComplete(message))
                return;
            if (!_resumeCompleteSent)
            {
                ResumeWrongDirectionCount++;
                return;
            }

            MarkValidActivity();
            Transition(
                NetworkSessionState.Running,
                NetworkTransportEventReason.ReconnectAccepted);
        }

        private void HandleDuplicateServerResumeComplete(
            RouteCProtocolMessage message)
        {
            if (_acceptedResumePlan == null ||
                !TryValidateServerResumeComplete(message))
            {
                ResumeWrongDirectionCount++;
                return;
            }

            MarkValidActivity();
        }

        private bool TryValidateServerResumeComplete(
            RouteCProtocolMessage message)
        {
            if (message.SessionId != _sessionId)
            {
                ResumeWrongSessionCount++;
                return false;
            }
            if (message.Generation != _generation)
            {
                ResumeWrongGenerationCount++;
                return false;
            }
            if (!RouteCProtocolCodec.TryDecodeResumeComplete(
                    message.Payload,
                    out byte[] attemptID,
                    out int uploadedLocalThrough,
                    out int receivedRemoteThrough))
            {
                RejectedEnvelopeCount++;
                return false;
            }
            if (!BytesEqual(_resumeAttemptID, attemptID))
            {
                ResumeWrongAttemptCount++;
                return false;
            }
            if (_acceptedResumePlan == null ||
                uploadedLocalThrough <
                    _acceptedResumePlan.RequiredUploadedThrough ||
                receivedRemoteThrough <
                    _acceptedResumePlan.ExpectedRemoteThrough)
            {
                RejectedEnvelopeCount++;
                return false;
            }
            return true;
        }

        private bool TryHandleResumeRejected(RouteCProtocolMessage message)
        {
            if (message.MessageType != RouteCMessageType.ResumeRejected)
                return false;

            if (message.SessionId != _sessionId ||
                message.Generation != _generation ||
                !RouteCProtocolCodec.TryDecodeResumeRejected(
                    message.Payload,
                    out byte[] attemptID,
                    out ushort reason))
            {
                RejectedEnvelopeCount++;
                return true;
            }
            bool validReason =
                reason == (ushort)ResumeRejectedReason.ResumeGraceExpired ||
                reason == (ushort)ResumeRejectedReason.UnsafeResume;
            if (!validReason)
            {
                RejectedEnvelopeCount++;
                return true;
            }
            bool hasActiveAttempt =
                State == NetworkSessionState.Resuming &&
                _resumeAttemptID != null;
            bool zeroTerminalWithoutActiveAttempt =
                IsZeroAttemptID(attemptID) && !hasActiveAttempt;
            if (!zeroTerminalWithoutActiveAttempt &&
                _resumeAttemptID != null &&
                !BytesEqual(_resumeAttemptID, attemptID))
            {
                ResumeWrongAttemptCount++;
                return true;
            }

            MarkValidActivity();
            Transition(
                NetworkSessionState.Terminated,
                NetworkTransportEventReason.ResumeRejected);
            return true;
        }

        private static bool IsZeroAttemptID(byte[] attemptID)
        {
            if (attemptID == null ||
                attemptID.Length != RouteCProtocolConstants.NonceSize)
            {
                return false;
            }

            int combined = 0;
            for (int index = 0; index < attemptID.Length; index++)
                combined |= attemptID[index];
            return combined == 0;
        }

        private bool TryHandleEstablishedAdvisory(
            RouteCProtocolMessage message)
        {
            bool heartbeat = message.MessageType == RouteCMessageType.Heartbeat;
            bool disconnect = message.MessageType == RouteCMessageType.Disconnect;
            if (!heartbeat && !disconnect)
                return false;

            int expectedPayloadLength = heartbeat ? 0 : 2;
            if (message.SessionId != _sessionId ||
                message.Generation != _generation ||
                message.PayloadLength != expectedPayloadLength)
            {
                RejectedEnvelopeCount++;
                return true;
            }

            MarkValidActivity();
            if (disconnect)
                PublishEvent(NetworkTransportEventReason.DisconnectAdvisory);
            return true;
        }

        private bool TryDecodeValidWelcome(
            RouteCProtocolMessage message,
            out byte playerIndex,
            out uint conversation,
            out byte[] newReconnectToken,
            out bool resumeRequired)
        {
            if (!RouteCProtocolCodec.TryDecodeWelcome(
                    message.Payload,
                    out byte[] echoNonce,
                    out playerIndex,
                    out conversation,
                    out newReconnectToken,
                    out int heartbeatMs,
                    out int timeoutMs,
                    out resumeRequired) ||
                !BytesEqual(_activeNonce, echoNonce) ||
                playerIndex > 1 ||
                conversation == 0u ||
                heartbeatMs != RouteCProtocolConstants.HeartbeatSilenceMs ||
                timeoutMs != RouteCProtocolConstants.DisconnectTimeoutMs)
            {
                playerIndex = 0;
                conversation = 0u;
                newReconnectToken = null;
                resumeRequired = false;
                return false;
            }

            return true;
        }

        private bool IsDuplicateWelcome(
            RouteCProtocolMessage message,
            bool expectedResumeRequired)
        {
            return message.SessionId == _sessionId &&
                   message.Generation == _generation &&
                   TryDecodeValidWelcome(
                       message,
                       out byte playerIndex,
                       out uint conversation,
                       out byte[] reconnectToken,
                       out bool resumeRequired) &&
                   playerIndex == LocalPlayerIndex &&
                   conversation == _conversation &&
                   BytesEqual(reconnectToken, _reconnectToken) &&
                   resumeRequired == expectedResumeRequired;
        }

        private byte[] CreateNonce()
        {
            byte[] nonce = _nonceFactory();
            if (nonce == null)
                throw new InvalidOperationException("Nonce factory returned null.");
            if (nonce.Length != RouteCProtocolConstants.NonceSize)
            {
                throw new InvalidOperationException(
                    "Nonce factory returned an invalid nonce length.");
            }

            return (byte[])nonce.Clone();
        }

        private void SendInitialHello()
        {
            _send(new RouteCProtocolMessage(
                RouteCMessageType.Hello,
                RouteCSessionId.Zero,
                0u,
                RouteCProtocolCodec.EncodeInitialHello(_activeNonce)));
            MarkHandshakeSent();
        }

        private void SendReady()
        {
            _send(new RouteCProtocolMessage(
                RouteCMessageType.Ready,
                _sessionId,
                _generation,
                RouteCProtocolCodec.EncodeReady(
                    _resumeReadiness.LastContiguousRemoteFrameID,
                    _resumeReadiness.EarliestRecoverableCanonicalFrame,
                    _resumeReadiness.LatestLocalFrameID)));
            MarkHandshakeSent();
        }

        private void SendResumeState()
        {
            _send(new RouteCProtocolMessage(
                RouteCMessageType.ResumeState,
                _sessionId,
                _generation,
                RouteCProtocolCodec.EncodeResumeState(
                    _resumeAttemptID,
                    _resumeReadiness.LastContiguousRemoteFrameID,
                    _resumeReadiness.EarliestRecoverableCanonicalFrame,
                    _resumeReadiness.LatestLocalFrameID)));
            MarkHandshakeSent();
        }

        private void SendResumeComplete()
        {
            _send(new RouteCProtocolMessage(
                RouteCMessageType.ResumeComplete,
                _sessionId,
                _generation,
                RouteCProtocolCodec.EncodeResumeComplete(
                    _resumeAttemptID,
                    _uploadedLocalThrough,
                    _receivedRemoteThrough)));
            MarkHandshakeSent();
        }

        private void SendReconnectHello()
        {
            _send(new RouteCProtocolMessage(
                RouteCMessageType.Hello,
                _sessionId,
                _generation,
                RouteCProtocolCodec.EncodeReconnectHello(
                    _activeNonce,
                    _reconnectToken)));
            MarkHandshakeSent();
        }

        private void SendHeartbeat()
        {
            _send(new RouteCProtocolMessage(
                RouteCMessageType.Heartbeat,
                _sessionId,
                _generation,
                Array.Empty<byte>()));
            _lastOutboundActivityAt = _clock.Milliseconds;
        }

        private void MarkHandshakeSent()
        {
            uint now = _clock.Milliseconds;
            _lastHandshakeSendAt = now;
            _lastOutboundActivityAt = now;
        }

        private bool HasSentResumeControl =>
            _isResumePeer ? _resumeStateSent : _resumeReadySent;

        private void ResetAcceptedResume()
        {
            _acceptedResumePlan = null;
            _pendingResumePlan = null;
            _resumeCompleteSent = false;
            _uploadedLocalThrough = -1;
            _receivedRemoteThrough = -1;
        }

        private static bool ResumePlansEqual(
            KcpClientResumePlan left,
            KcpClientResumePlan right)
        {
            return BytesEqual(left.CopyAttemptID(), right.CopyAttemptID()) &&
                   left.IsPeer == right.IsPeer &&
                   left.UploadFrom == right.UploadFrom &&
                   left.UploadThrough == right.UploadThrough &&
                   left.ReplayFrom == right.ReplayFrom &&
                   left.ReplayThrough == right.ReplayThrough &&
                   left.PeerFrom == right.PeerFrom &&
                   left.PeerThrough == right.PeerThrough;
        }

        private uint ElapsedSince(uint timestamp)
        {
            return unchecked(_clock.Milliseconds - timestamp);
        }

        private static uint EarlierDeadline(
            uint nowMs,
            uint left,
            uint right)
        {
            uint leftDelay = unchecked(left - nowMs);
            uint rightDelay = unchecked(right - nowMs);
            return leftDelay <= rightDelay ? left : right;
        }

        private void MarkValidActivity()
        {
            _lastValidActivityAt = _clock.Milliseconds;
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;

            int difference = 0;
            for (int index = 0; index < left.Length; index++)
                difference |= left[index] ^ right[index];
            return difference == 0;
        }

        private void Transition(
            NetworkSessionState next,
            NetworkTransportEventReason reason)
        {
            bool allowed;
            switch (State)
            {
                case NetworkSessionState.Disconnected:
                    allowed = next == NetworkSessionState.Handshaking ||
                              next == NetworkSessionState.Terminated;
                    break;
                case NetworkSessionState.Handshaking:
                    allowed = next == NetworkSessionState.AwaitingReady ||
                              next == NetworkSessionState.Terminated;
                    break;
                case NetworkSessionState.AwaitingReady:
                    allowed = next == NetworkSessionState.Running ||
                              next == NetworkSessionState.Terminated;
                    break;
                case NetworkSessionState.Running:
                    allowed = next == NetworkSessionState.Reconnecting ||
                              next == NetworkSessionState.Resuming ||
                              next == NetworkSessionState.Terminated;
                    break;
                case NetworkSessionState.Reconnecting:
                    allowed = next == NetworkSessionState.Running ||
                              next == NetworkSessionState.Resuming ||
                              next == NetworkSessionState.Terminated;
                    break;
                case NetworkSessionState.Resuming:
                    allowed = next == NetworkSessionState.Running ||
                              next == NetworkSessionState.Terminated;
                    break;
                default:
                    allowed = false;
                    break;
            }

            if (!allowed)
            {
                throw new InvalidOperationException(
                    string.Format("Invalid client transition {0} -> {1}.", State, next));
            }

            State = next;
            if (State == NetworkSessionState.Terminated)
            {
                HasStartedSession = false;
                LocalPlayerIndex = -1;
            }
            PublishEvent(reason);
        }

        private void PublishEvent(NetworkTransportEventReason reason)
        {
            _publishEvent(new NetworkTransportEvent(
                State,
                reason,
                SessionFingerprint(_sessionId),
                _generation,
                _clock.Milliseconds));
        }

        private static string SessionFingerprint(RouteCSessionId sessionId)
        {
            if (sessionId.IsZero)
                return "none";

            unchecked
            {
                uint hash = 2166136261u;
                ulong high = sessionId.High;
                ulong low = sessionId.Low;
                for (int index = 0; index < 8; index++)
                {
                    hash = (hash ^ (byte)(high >> (index * 8))) * 16777619u;
                    hash = (hash ^ (byte)(low >> (index * 8))) * 16777619u;
                }
                return hash.ToString("X8");
            }
        }
    }
}

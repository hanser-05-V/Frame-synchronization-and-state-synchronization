using System;
using System.Net;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using FrameSyncDemo;

namespace FrameSyncServer
{
    public sealed class ServerSessionRouter
    {
        private readonly KcpServerSession[] _sessions;
        private readonly KcpServerDiagnostics _diagnostics;
        private readonly Func<RouteCSessionId> _sessionIdFactory;
        private readonly Func<byte[]> _tokenFactory;
        private readonly Func<uint> _conversationFactory;
        private readonly Func<byte[]> _resumeAttemptFactory;
        private readonly int _kcpIntervalMs;
        private readonly List<KcpServerDatagram> _actions =
            new List<KcpServerDatagram>();
        private bool _terminated;
        private ServerResumeAttempt _resumeAttempt;

        public ServerSessionRouter(
            KcpServerSession first,
            KcpServerSession second,
            KcpServerDiagnostics diagnostics)
        {
            _sessions = new[] { first, second };
            _diagnostics = diagnostics ??
                throw new ArgumentNullException(nameof(diagnostics));
            var random = new CryptoRandomSource();
            _resumeAttemptFactory = random.CreateNonce;
        }

        public ServerSessionRouter(
            KcpServerDiagnostics diagnostics,
            Func<RouteCSessionId> sessionIdFactory,
            Func<byte[]> tokenFactory,
            Func<uint> conversationFactory)
        {
            _sessions = new KcpServerSession[2];
            _diagnostics = diagnostics ??
                throw new ArgumentNullException(nameof(diagnostics));
            _sessionIdFactory = sessionIdFactory ??
                throw new ArgumentNullException(nameof(sessionIdFactory));
            _tokenFactory = tokenFactory ??
                throw new ArgumentNullException(nameof(tokenFactory));
            _conversationFactory = conversationFactory ??
                throw new ArgumentNullException(nameof(conversationFactory));
            var random = new CryptoRandomSource();
            _resumeAttemptFactory = random.CreateNonce;
        }

        public ServerSessionRouter(
            KcpServerDiagnostics diagnostics,
            Func<RouteCSessionId> sessionIdFactory,
            Func<byte[]> tokenFactory,
            Func<uint> conversationFactory,
            int kcpIntervalMs)
            : this(
                diagnostics,
                sessionIdFactory,
                tokenFactory,
                conversationFactory)
        {
            _kcpIntervalMs = new RouteCKcpSettings(kcpIntervalMs).IntervalMs;
        }

        public ServerSessionRouter(
            KcpServerDiagnostics diagnostics,
            Func<RouteCSessionId> sessionIdFactory,
            Func<byte[]> tokenFactory,
            Func<uint> conversationFactory,
            Func<byte[]> resumeAttemptFactory,
            int kcpIntervalMs)
        {
            _sessions = new KcpServerSession[2];
            _diagnostics = diagnostics ??
                throw new ArgumentNullException(nameof(diagnostics));
            _sessionIdFactory = sessionIdFactory ??
                throw new ArgumentNullException(nameof(sessionIdFactory));
            _tokenFactory = tokenFactory ??
                throw new ArgumentNullException(nameof(tokenFactory));
            _conversationFactory = conversationFactory ??
                throw new ArgumentNullException(nameof(conversationFactory));
            _resumeAttemptFactory = resumeAttemptFactory ??
                throw new ArgumentNullException(nameof(resumeAttemptFactory));
            _kcpIntervalMs = new RouteCKcpSettings(kcpIntervalMs).IntervalMs;
        }

        public int ActiveSessionCount
        {
            get
            {
                int count = 0;
                for (int index = 0; index < _sessions.Length; index++)
                {
                    if (_sessions[index] != null)
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        public ServerHandshakeDisposition HandleInitialHello(
            IPEndPoint endpoint,
            byte[] clientNonce,
            out byte[] welcomeDatagram,
            out KcpServerSession session)
        {
            return HandleInitialHelloAt(
                endpoint,
                clientNonce,
                0u,
                out welcomeDatagram,
                out session);
        }

        public ServerHandshakeDisposition HandleInitialHelloAt(
            IPEndPoint endpoint,
            byte[] clientNonce,
            uint nowMs,
            out byte[] welcomeDatagram,
            out KcpServerSession session)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }
            if (clientNonce == null ||
                clientNonce.Length != RouteCProtocolConstants.NonceSize)
            {
                welcomeDatagram = null;
                session = null;
                return ServerHandshakeDisposition.Rejected;
            }

            for (int index = 0; index < _sessions.Length; index++)
            {
                KcpServerSession candidate = _sessions[index];
                if (candidate != null &&
                    candidate.MatchesInitialHandshake(endpoint, clientNonce))
                {
                    welcomeDatagram = candidate.CopyWelcomeDatagram();
                    session = candidate;
                    return ServerHandshakeDisposition.IdempotentRetry;
                }
            }

            int playerIndex = FindFreePlayerIndex();
            if (playerIndex < 0)
            {
                welcomeDatagram = null;
                session = null;
                return ServerHandshakeDisposition.MatchFull;
            }

            RouteCSessionId sessionId = AllocateSessionId();
            uint conversation = AllocateConversation();
            byte[] reconnectToken = _tokenFactory();
            if (reconnectToken == null ||
                reconnectToken.Length !=
                RouteCProtocolConstants.ReconnectTokenSize)
            {
                throw new InvalidOperationException(
                    "Reconnect token factory returned an invalid value.");
            }

            byte[] welcomePayload = RouteCProtocolCodec.EncodeWelcome(
                clientNonce,
                (byte)playerIndex,
                conversation,
                reconnectToken,
                RouteCProtocolConstants.HeartbeatSilenceMs,
                RouteCProtocolConstants.DisconnectTimeoutMs,
                false);
            welcomeDatagram = RouteCProtocolCodec.Encode(
                RouteCMessageType.Welcome,
                sessionId,
                1u,
                welcomePayload);
            session = new KcpServerSession(
                (byte)playerIndex,
                sessionId,
                1u,
                conversation,
                endpoint,
                clientNonce,
                reconnectToken,
                welcomeDatagram);
            _sessions[playerIndex] = session;
            session.MarkValidActivity(nowMs);
            AttachKcp(session);
            return ServerHandshakeDisposition.Allocated;
        }

        public bool HandleReady(
            RouteCProtocolMessage message,
            IPEndPoint endpoint,
            uint nowMs)
        {
            if (_terminated)
                return false;

            if (!TryRouteEstablishedControl(
                    message,
                    endpoint,
                    out KcpServerSession session,
                    out _))
            {
                return false;
            }
            if (message.MessageType != RouteCMessageType.Ready ||
                !RouteCProtocolCodec.TryDecodeReady(
                    message.Payload,
                    out int lastContiguousRemoteFrameID,
                    out int earliestRecoverableCanonicalFrame,
                    out int latestLocalFrameID))
            {
                return false;
            }

            ResumeReadiness readiness;
            try
            {
                readiness = new ResumeReadiness(
                    lastContiguousRemoteFrameID,
                    earliestRecoverableCanonicalFrame,
                    latestLocalFrameID);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }

            if (_resumeAttempt != null && _resumeAttempt.IsCompleted)
            {
                if (!_resumeAttempt.MatchesReadiness(
                        session.PlayerIndex,
                        readiness))
                {
                    _diagnostics.RecordResumeWrongDirection();
                    return false;
                }

                session.MarkValidActivity(nowMs);
                EnqueueResumeComplete();
                return true;
            }

            if (session.State == NetworkSessionState.Resuming)
            {
                return HandleResumeReady(session, readiness, nowMs);
            }

            session.MarkReady(nowMs);
            if (BothSessionsReady())
            {
                if (_sessions[0].State != NetworkSessionState.Running ||
                    _sessions[1].State != NetworkSessionState.Running)
                {
                    BeginRunning();
                    EnqueueStart(_sessions[0]);
                    EnqueueStart(_sessions[1]);
                }
                else
                {
                    EnqueueStart(session);
                }
            }
            return true;
        }

        public bool HandleResumeState(
            RouteCProtocolMessage message,
            IPEndPoint endpoint,
            uint nowMs)
        {
            if (message == null ||
                message.MessageType != RouteCMessageType.ResumeState)
            {
                _diagnostics.RecordResumeWrongDirection();
                return false;
            }
            if (endpoint == null)
            {
                _diagnostics.RecordResumeWrongEndpoint();
                return false;
            }

            KcpServerSession session = FindSession(message.SessionId);
            if (session == null)
            {
                _diagnostics.RecordResumeWrongSession();
                return false;
            }
            if (message.Generation != session.Generation)
            {
                _diagnostics.RecordResumeWrongGeneration();
                return false;
            }
            if (!session.Endpoint.Equals(endpoint))
            {
                _diagnostics.RecordResumeWrongEndpoint();
                return false;
            }
            if (_resumeAttempt == null ||
                session.PlayerIndex != _resumeAttempt.PeerPlayerIndex)
            {
                _diagnostics.RecordResumeWrongDirection();
                return false;
            }
            if (!RouteCProtocolCodec.TryDecodeResumeState(
                    message.Payload,
                    out byte[] attemptID,
                    out int lastContiguousRemoteFrameID,
                    out int earliestRecoverableCanonicalFrame,
                    out int latestLocalFrameID))
            {
                _diagnostics.RecordResumeInvalidPayload();
                return false;
            }
            if (!_resumeAttempt.MatchesAttemptID(attemptID))
            {
                _diagnostics.RecordResumeWrongAttempt();
                return false;
            }

            ResumeReadiness readiness;
            try
            {
                readiness = new ResumeReadiness(
                    lastContiguousRemoteFrameID,
                    earliestRecoverableCanonicalFrame,
                    latestLocalFrameID);
            }
            catch (ArgumentOutOfRangeException)
            {
                _diagnostics.RecordResumeInvalidPayload();
                return false;
            }
            if (!_resumeAttempt.TrySetPeerReadiness(readiness))
            {
                _diagnostics.RecordResumeInvalidPayload();
                return false;
            }
            if (_resumeAttempt.IsCompleted)
            {
                session.MarkValidActivity(nowMs);
                EnqueueResumeComplete();
                return true;
            }

            session.MarkResumeReady(readiness, nowMs);
            if (_resumeAttempt.IsFrozen)
            {
                EnqueueResumeAccepted();
                return true;
            }
            if (!TryFreezeResumeAttempt())
                return false;
            EnqueueResumeAccepted();
            return true;
        }

        public bool HandleServerOnlyResumeControl(
            RouteCProtocolMessage message,
            IPEndPoint endpoint)
        {
            if (message == null ||
                (message.MessageType != RouteCMessageType.ResumeProbe &&
                 message.MessageType != RouteCMessageType.ResumeAccepted))
            {
                _diagnostics.RecordResumeWrongDirection();
                return false;
            }
            if (endpoint == null)
            {
                _diagnostics.RecordResumeWrongEndpoint();
                return false;
            }

            KcpServerSession session = FindSession(message.SessionId);
            if (session == null)
            {
                _diagnostics.RecordResumeWrongSession();
                return false;
            }
            if (message.Generation != session.Generation)
            {
                _diagnostics.RecordResumeWrongGeneration();
                return false;
            }
            if (!session.Endpoint.Equals(endpoint))
            {
                _diagnostics.RecordResumeWrongEndpoint();
                return false;
            }

            _diagnostics.RecordResumeWrongDirection();
            return false;
        }

        public bool HandleResumeComplete(
            RouteCProtocolMessage message,
            IPEndPoint endpoint,
            uint nowMs)
        {
            if (!TryValidateResumeParticipant(
                    message,
                    endpoint,
                    RouteCMessageType.ResumeComplete,
                    out KcpServerSession session))
            {
                return false;
            }
            if (!RouteCProtocolCodec.TryDecodeResumeComplete(
                    message.Payload,
                    out byte[] attemptID,
                    out int uploadedLocalThrough,
                    out int receivedRemoteThrough))
            {
                _diagnostics.RecordResumeInvalidPayload();
                return false;
            }
            if (!_resumeAttempt.MatchesAttemptID(attemptID))
            {
                _diagnostics.RecordResumeWrongAttempt();
                return false;
            }
            if (_resumeAttempt.IsCompleted)
            {
                EnqueueResumeComplete();
                return true;
            }

            if (session.PlayerIndex ==
                    _resumeAttempt.ReconnectingPlayerIndex &&
                _resumeAttempt.UploadFrom <= _resumeAttempt.UploadThrough &&
                !session.History.TryCopyRange(
                    _resumeAttempt.UploadFrom,
                    _resumeAttempt.UploadThrough,
                    out _))
            {
                return false;
            }
            if (!_resumeAttempt.TryCompletePlayer(
                    session.PlayerIndex,
                    uploadedLocalThrough,
                    receivedRemoteThrough))
            {
                _diagnostics.RecordResumeInvalidPayload();
                return false;
            }

            session.MarkValidActivity(nowMs);
            if (!_resumeAttempt.BothPlayersCompleted)
                return true;
            if (!_resumeAttempt.IsTailReleaseStarted &&
                !TryBeginFrozenTailRelease())
                return false;
            TryCompleteResumeAfterTailQueue();
            return true;
        }

        public bool HandleResumeRejected(
            RouteCProtocolMessage message,
            IPEndPoint endpoint,
            uint nowMs)
        {
            if (!TryValidateResumeParticipant(
                    message,
                    endpoint,
                    RouteCMessageType.ResumeRejected,
                    out KcpServerSession session))
            {
                return false;
            }
            if (!RouteCProtocolCodec.TryDecodeResumeRejected(
                    message.Payload,
                    out byte[] attemptID,
                    out ushort reason) ||
                reason != (ushort)ResumeRejectedReason.UnsafeResume)
            {
                _diagnostics.RecordResumeInvalidPayload();
                return false;
            }
            if (!_resumeAttempt.MatchesAttemptID(attemptID))
            {
                _diagnostics.RecordResumeWrongAttempt();
                return false;
            }

            session.MarkValidActivity(nowMs);
            RejectResume(
                ServerResumeFailureKind.ClientHistoryUnavailable,
                ResumeRejectedReason.UnsafeResume);
            return true;
        }

        public KcpServerDatagram[] DrainActions()
        {
            KcpServerDatagram[] actions = _actions.ToArray();
            _actions.Clear();
            return actions;
        }

        public ServerHandshakeDisposition HandleReconnectHello(
            IPEndPoint endpoint,
            RouteCSessionId sessionId,
            uint generation,
            byte[] oldReconnectToken,
            byte[] newClientNonce,
            out byte[] welcomeDatagram,
            out KcpServerSession session)
        {
            return HandleReconnectHelloAt(
                endpoint,
                sessionId,
                generation,
                oldReconnectToken,
                newClientNonce,
                0u,
                out welcomeDatagram,
                out session);
        }

        public ServerHandshakeDisposition HandleReconnectHelloAt(
            IPEndPoint endpoint,
            RouteCSessionId sessionId,
            uint generation,
            byte[] oldReconnectToken,
            byte[] newClientNonce,
            uint nowMs,
            out byte[] welcomeDatagram,
            out KcpServerSession session)
        {
            welcomeDatagram = null;
            session = null;
            if (_terminated ||
                endpoint == null ||
                oldReconnectToken == null ||
                oldReconnectToken.Length !=
                RouteCProtocolConstants.ReconnectTokenSize ||
                newClientNonce == null ||
                newClientNonce.Length != RouteCProtocolConstants.NonceSize)
            {
                return ServerHandshakeDisposition.Rejected;
            }

            session = FindSession(sessionId);
            if (session == null)
            {
                return ServerHandshakeDisposition.Rejected;
            }
            if (session.MatchesReconnectRetry(
                    endpoint,
                    generation,
                    oldReconnectToken,
                    newClientNonce))
            {
                welcomeDatagram = session.CopyWelcomeDatagram();
                session.MarkValidActivity(nowMs);
                return ServerHandshakeDisposition.IdempotentRetry;
            }
            if (generation != session.Generation ||
                generation == uint.MaxValue ||
                !session.MatchesReconnectToken(oldReconnectToken))
            {
                session = null;
                return ServerHandshakeDisposition.Rejected;
            }
            if (session.State == NetworkSessionState.Resuming)
            {
                RejectResume(
                    ServerResumeFailureKind.GenerationChanged,
                    ResumeRejectedReason.UnsafeResume);
                return ServerHandshakeDisposition.Rejected;
            }
            if (_resumeAttempt != null && _resumeAttempt.IsCompleted)
                _resumeAttempt = null;

            uint newGeneration = generation + 1u;
            uint newConversation = AllocateConversation();
            byte[] newReconnectToken = _tokenFactory();
            if (newReconnectToken == null ||
                newReconnectToken.Length !=
                RouteCProtocolConstants.ReconnectTokenSize)
            {
                throw new InvalidOperationException(
                    "Reconnect token factory returned an invalid value.");
            }

            byte[] welcomePayload = RouteCProtocolCodec.EncodeWelcome(
                newClientNonce,
                session.PlayerIndex,
                newConversation,
                newReconnectToken,
                RouteCProtocolConstants.HeartbeatSilenceMs,
                RouteCProtocolConstants.DisconnectTimeoutMs,
                true);
            welcomeDatagram = RouteCProtocolCodec.Encode(
                RouteCMessageType.Welcome,
                session.SessionId,
                newGeneration,
                welcomePayload);
            session.SwitchGeneration(
                newGeneration,
                newConversation,
                endpoint,
                newClientNonce,
                newReconnectToken,
                welcomeDatagram);
            session.MarkValidActivity(nowMs);
            AttachKcp(session);
            return ServerHandshakeDisposition.Reconnected;
        }

        public bool HandleKcpData(
            RouteCProtocolMessage message,
            IPEndPoint endpoint,
            uint nowMs)
        {
            if (!TryRoute(
                    message,
                    endpoint,
                    out KcpServerSession session,
                    out _))
            {
                return false;
            }
            if (message.MessageType != RouteCMessageType.KcpData ||
                session.KcpSession == null ||
                (session.State != NetworkSessionState.Running &&
                 session.State != NetworkSessionState.Resuming))
            {
                return false;
            }

            byte[] payload = message.Payload;
            if (session.KcpSession.InputDatagram(
                    payload,
                    0,
                    payload.Length) != 0)
            {
                return false;
            }

            session.MarkValidActivity(nowMs);
            return true;
        }

        public bool HandleHeartbeat(
            RouteCProtocolMessage message,
            IPEndPoint endpoint,
            uint nowMs)
        {
            if (!TryRouteEstablishedControl(
                    message,
                    endpoint,
                    out KcpServerSession session,
                    out _) ||
                message.MessageType != RouteCMessageType.Heartbeat ||
                message.PayloadLength != 0 ||
                (session.State != NetworkSessionState.Running &&
                 session.State != NetworkSessionState.Resuming))
            {
                return false;
            }

            session.MarkValidActivity(nowMs);
            return true;
        }

        public bool HandleDisconnect(
            RouteCProtocolMessage message,
            IPEndPoint endpoint,
            uint nowMs)
        {
            if (!TryRouteEstablishedControl(
                    message,
                    endpoint,
                    out KcpServerSession session,
                    out _) ||
                message.MessageType != RouteCMessageType.Disconnect ||
                message.PayloadLength != 2 ||
                (session.State != NetworkSessionState.Running &&
                 session.State != NetworkSessionState.Resuming))
            {
                return false;
            }

            session.MarkValidActivity(nowMs);
            return true;
        }

        public void Tick(uint nowMs, long workerTimestamp)
        {
            var budget = new KcpServerRoundBudget(
                _diagnostics,
                Stopwatch.GetTimestamp());
            TickWithBudget(nowMs, workerTimestamp, budget);
        }

        internal uint NextActionAt(uint nowMs)
        {
            uint deadline = unchecked(nowMs + 10u);
            if (_resumeAttempt != null &&
                !_resumeAttempt.HasPeerReadiness)
            {
                uint resumeDeadline = unchecked(
                    _resumeAttempt.LastProbeSentAt +
                    RouteCProtocolConstants.HandshakeRetryMs);
                if (unchecked(resumeDeadline - nowMs) <
                    unchecked(deadline - nowMs))
                {
                    deadline = resumeDeadline;
                }
            }
            for (int index = 0; index < _sessions.Length; index++)
            {
                KcpServerSession session = _sessions[index];
                if (session == null || session.KcpSession == null)
                {
                    continue;
                }

                uint candidate = session.KcpSession.NextUpdateAt(nowMs);
                if (unchecked(candidate - nowMs) <
                    unchecked(deadline - nowMs))
                {
                    deadline = candidate;
                }
            }
            return deadline;
        }

        internal void TickWithBudget(
            uint nowMs,
            long workerTimestamp,
            KcpServerRoundBudget budget)
        {
            if (budget == null)
            {
                throw new ArgumentNullException(nameof(budget));
            }

            _diagnostics.BeginTick();
            if (_terminated)
                return;

            PumpFrozenReplay(budget);
            if (_terminated)
                return;
            PumpFrozenTails(budget);
            if (_terminated)
                return;

            for (int index = 0; index < _sessions.Length; index++)
            {
                KcpServerSession session = _sessions[index];
                if (session == null || session.KcpSession == null)
                {
                    continue;
                }

                uint nextUpdateAt = session.KcpSession.NextUpdateAt(nowMs);
                if (unchecked((int)(nowMs - nextUpdateAt)) >= 0)
                {
                    _diagnostics.RecordPlayerProcessed((byte)index);
                    session.KcpSession.Update(nowMs, workerTimestamp);
                    _diagnostics.RecordKcpTimingSample(
                        (byte)index,
                        session.KcpSession.SnapshotDiagnostics());
                }
                DrainBusinessInputs(session, budget);
            }

            ApplyTimeouts(nowMs);
            if (!_terminated)
                RetryResumeProbe(nowMs);
        }

        public bool TryRoute(
            RouteCProtocolMessage message,
            IPEndPoint endpoint,
            out KcpServerSession session,
            out RouteCProtocolDropReason reason)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            session = FindSession(message.SessionId);
            if (session == null)
            {
                reason = RouteCProtocolDropReason.SessionNotFound;
                return Reject(reason, out session);
            }
            if (message.Generation != session.Generation)
            {
                reason = RouteCProtocolDropReason.StaleGeneration;
                return Reject(reason, out session);
            }
            if (!session.Endpoint.Equals(endpoint))
            {
                reason = RouteCProtocolDropReason.WrongEndpoint;
                return Reject(reason, out session);
            }

            byte[] payload = message.Payload;
            if (!RouteCKcpHeader.TryReadConversation(
                    payload,
                    out uint conversation))
            {
                reason = RouteCProtocolDropReason.KcpPayloadTooShort;
                return Reject(reason, out session);
            }
            if (conversation != session.Conversation)
            {
                reason = RouteCProtocolDropReason.WrongConversation;
                return Reject(reason, out session);
            }

            reason = RouteCProtocolDropReason.None;
            return true;
        }

        private KcpServerSession FindSession(RouteCSessionId sessionId)
        {
            for (int index = 0; index < _sessions.Length; index++)
            {
                KcpServerSession candidate = _sessions[index];
                if (candidate != null && candidate.SessionId == sessionId)
                {
                    return candidate;
                }
            }

            return null;
        }

        private int FindFreePlayerIndex()
        {
            for (int index = 0; index < _sessions.Length; index++)
            {
                if (_sessions[index] == null)
                {
                    return index;
                }
            }
            return -1;
        }

        private bool TryRouteEstablishedControl(
            RouteCProtocolMessage message,
            IPEndPoint endpoint,
            out KcpServerSession session,
            out RouteCProtocolDropReason reason)
        {
            session = null;
            if (message == null || endpoint == null)
            {
                reason = RouteCProtocolDropReason.SessionNotFound;
                return false;
            }

            session = FindSession(message.SessionId);
            if (session == null)
            {
                reason = RouteCProtocolDropReason.SessionNotFound;
                return false;
            }
            if (message.Generation != session.Generation)
            {
                reason = RouteCProtocolDropReason.StaleGeneration;
                return false;
            }
            if (!session.Endpoint.Equals(endpoint))
            {
                reason = RouteCProtocolDropReason.WrongEndpoint;
                return false;
            }

            reason = RouteCProtocolDropReason.None;
            return true;
        }

        private bool TryValidateResumeParticipant(
            RouteCProtocolMessage message,
            IPEndPoint endpoint,
            RouteCMessageType expectedType,
            out KcpServerSession session)
        {
            session = null;
            if (message == null || message.MessageType != expectedType)
            {
                _diagnostics.RecordResumeWrongDirection();
                return false;
            }
            if (endpoint == null)
            {
                _diagnostics.RecordResumeWrongEndpoint();
                return false;
            }

            session = FindSession(message.SessionId);
            if (session == null)
            {
                _diagnostics.RecordResumeWrongSession();
                return false;
            }
            if (message.Generation != session.Generation)
            {
                _diagnostics.RecordResumeWrongGeneration();
                return false;
            }
            if (!session.Endpoint.Equals(endpoint))
            {
                _diagnostics.RecordResumeWrongEndpoint();
                return false;
            }
            if (_resumeAttempt == null || !_resumeAttempt.IsFrozen ||
                (session.PlayerIndex !=
                    _resumeAttempt.ReconnectingPlayerIndex &&
                 session.PlayerIndex != _resumeAttempt.PeerPlayerIndex) ||
                (session.State != NetworkSessionState.Resuming &&
                 !_resumeAttempt.IsCompleted))
            {
                _diagnostics.RecordResumeWrongDirection();
                return false;
            }
            return true;
        }

        private bool BothSessionsReady()
        {
            return _sessions[0] != null &&
                   _sessions[1] != null &&
                   _sessions[0].IsReady &&
                   _sessions[1].IsReady;
        }

        private bool HandleResumeReady(
            KcpServerSession reconnecting,
            ResumeReadiness readiness,
            uint nowMs)
        {
            if (_resumeAttempt != null)
            {
                if (!_resumeAttempt.MatchesReconnectReadiness(
                        reconnecting.PlayerIndex,
                        readiness))
                {
                    _diagnostics.RecordResumeWrongDirection();
                    return false;
                }

                reconnecting.MarkResumeReady(readiness, nowMs);
                EnqueueResumeProbe(nowMs);
                return true;
            }

            KcpServerSession peer = _sessions[1 - reconnecting.PlayerIndex];
            if (peer == null || peer.State != NetworkSessionState.Running)
            {
                _diagnostics.RecordResumeWrongDirection();
                return false;
            }

            byte[] attemptID = _resumeAttemptFactory();
            if (attemptID == null ||
                attemptID.Length != RouteCProtocolConstants.NonceSize)
            {
                throw new InvalidOperationException(
                    "Resume AttemptID factory returned an invalid value.");
            }

            byte[] probeDatagram = RouteCProtocolCodec.Encode(
                RouteCMessageType.ResumeProbe,
                peer.SessionId,
                peer.Generation,
                RouteCProtocolCodec.EncodeResumeProbe(attemptID));
            var action = new KcpServerDatagram(
                peer.Endpoint,
                probeDatagram);
            _resumeAttempt = new ServerResumeAttempt(
                attemptID,
                reconnecting.PlayerIndex,
                readiness,
                action,
                nowMs);
            reconnecting.MarkResumeReady(readiness, nowMs);
            peer.BeginPeerResume();
            _actions.Add(action);
            return true;
        }

        private void RetryResumeProbe(uint nowMs)
        {
            if (_resumeAttempt != null &&
                _resumeAttempt.IsProbeRetryDue(nowMs))
            {
                EnqueueResumeProbe(nowMs);
            }
        }

        private void EnqueueResumeProbe(uint nowMs)
        {
            _actions.Add(_resumeAttempt.CopyProbeDatagram());
            _resumeAttempt.MarkProbeSent(nowMs);
        }

        private bool TryFreezeResumeAttempt()
        {
            KcpServerSession reconnecting =
                _sessions[_resumeAttempt.ReconnectingPlayerIndex];
            KcpServerSession peer = _sessions[_resumeAttempt.PeerPlayerIndex];
            ResumeReadiness reconnectingReadiness =
                _resumeAttempt.ReconnectingReadiness;
            ResumeReadiness peerReadiness = _resumeAttempt.PeerReadiness;

            if (reconnectingReadiness.LastContiguousRemoteFrameID >
                    peerReadiness.LatestLocalFrameID ||
                peerReadiness.LastContiguousRemoteFrameID >
                    reconnectingReadiness.LatestLocalFrameID)
            {
                RejectResume(
                    ServerResumeFailureKind.InvalidResumeState,
                    ResumeRejectedReason.UnsafeResume);
                return false;
            }

            if (!TryGetRangeStart(
                    peerReadiness.LastContiguousRemoteFrameID,
                    out int uploadFrom) ||
                !TryGetRangeStart(
                    reconnectingReadiness.LastContiguousRemoteFrameID,
                    out int replayFrom))
            {
                RejectResume(
                    ServerResumeFailureKind.InvalidResumeState,
                    ResumeRejectedReason.UnsafeResume);
                return false;
            }

            int uploadThrough = reconnectingReadiness.LatestLocalFrameID;
            int replayThrough = peerReadiness.LatestLocalFrameID;
            int peerFrom = uploadFrom;
            int peerThrough = uploadThrough;

            if ((IsNonEmptyRange(replayFrom, replayThrough) &&
                 replayFrom < reconnectingReadiness.
                     EarliestRecoverableCanonicalFrame) ||
                (IsNonEmptyRange(peerFrom, peerThrough) &&
                 peerFrom < peerReadiness.
                     EarliestRecoverableCanonicalFrame))
            {
                RejectResume(
                    ServerResumeFailureKind.CorrectionFloorUnavailable,
                    ResumeRejectedReason.UnsafeResume);
                return false;
            }
            if (GetRangeLength(uploadFrom, uploadThrough) >
                    RouteCProtocolConstants.InputHistoryCapacity ||
                GetRangeLength(replayFrom, replayThrough) >
                    RouteCProtocolConstants.InputHistoryCapacity ||
                GetRangeLength(peerFrom, peerThrough) >
                    RouteCProtocolConstants.InputHistoryCapacity)
            {
                RejectResume(
                    ServerResumeFailureKind.RangeCapacityExceeded,
                    ResumeRejectedReason.UnsafeResume);
                return false;
            }

            byte[][] replayPayloads = Array.Empty<byte[]>();
            if (replayFrom <= replayThrough &&
                !peer.History.TryCopyRange(
                    replayFrom,
                    replayThrough,
                    out replayPayloads))
            {
                RejectResume(
                    ServerResumeFailureKind.ServerHistoryUnavailable,
                    ResumeRejectedReason.UnsafeResume);
                return false;
            }
            if (!reconnecting.History.TryBeginFreeze(uploadThrough) ||
                !peer.History.TryBeginFreeze(replayThrough))
            {
                RejectResume(
                    ServerResumeFailureKind.InvalidResumeState,
                    ResumeRejectedReason.UnsafeResume);
                return false;
            }

            byte[] acceptedPayload = RouteCProtocolCodec.EncodeResumeAccepted(
                _resumeAttempt.CopyAttemptID(),
                uploadFrom,
                uploadThrough,
                replayFrom,
                replayThrough,
                peerFrom,
                peerThrough);
            var reconnectingDatagram = new KcpServerDatagram(
                reconnecting.Endpoint,
                RouteCProtocolCodec.Encode(
                    RouteCMessageType.ResumeAccepted,
                    reconnecting.SessionId,
                    reconnecting.Generation,
                    acceptedPayload));
            var peerDatagram = new KcpServerDatagram(
                peer.Endpoint,
                RouteCProtocolCodec.Encode(
                    RouteCMessageType.ResumeAccepted,
                    peer.SessionId,
                    peer.Generation,
                    acceptedPayload));
            _resumeAttempt.Freeze(
                acceptedPayload,
                reconnectingDatagram,
                peerDatagram,
                uploadFrom,
                uploadThrough,
                replayFrom,
                replayThrough,
                peerFrom,
                peerThrough,
                replayPayloads);
            return true;
        }

        private void PumpFrozenReplay(KcpServerRoundBudget budget)
        {
            if (_resumeAttempt == null ||
                !_resumeAttempt.IsFrozen ||
                _resumeAttempt.PendingReplayCount == 0)
                return;

            KcpServerSession reconnecting =
                _sessions[_resumeAttempt.ReconnectingPlayerIndex];
            if (reconnecting.KcpSession == null)
            {
                RejectResume(
                    ServerResumeFailureKind.ProcessingBudgetExceeded,
                    ResumeRejectedReason.UnsafeResume);
                return;
            }

            while (_resumeAttempt.PendingReplayCount > 0 &&
                   budget.HasLiveSliceBudget(
                       System.Diagnostics.Stopwatch.GetTimestamp()) &&
                   budget.TryTakeMessage(reconnecting.PlayerIndex) &&
                   _resumeAttempt.TryTakeNextReplayPayload(
                       out byte[] replayPayload))
            {
                if (!RouteCProtocolCodec.TryDecodeBusinessInput(
                        replayPayload,
                        out uint raw,
                        out int frameID) ||
                    reconnecting.KcpSession.SendBusinessInput(raw, frameID) != 0)
                {
                    RejectResume(
                        ServerResumeFailureKind.ProcessingBudgetExceeded,
                        ResumeRejectedReason.UnsafeResume);
                    return;
                }
            }
        }

        private static bool TryGetRangeStart(
            int lastContiguousFrameID,
            out int rangeStart)
        {
            if (lastContiguousFrameID == int.MaxValue)
            {
                rangeStart = 0;
                return false;
            }

            rangeStart = lastContiguousFrameID + 1;
            return true;
        }

        private static bool IsNonEmptyRange(
            int fromFrameID,
            int throughFrameID)
        {
            return fromFrameID <= throughFrameID;
        }

        private static long GetRangeLength(
            int fromFrameID,
            int throughFrameID)
        {
            return IsNonEmptyRange(fromFrameID, throughFrameID)
                ? (long)throughFrameID - fromFrameID + 1L
                : 0L;
        }

        private void EnqueueResumeAccepted()
        {
            KcpServerDatagram[] datagrams =
                _resumeAttempt.CopyAcceptedDatagrams();
            for (int index = 0; index < datagrams.Length; index++)
                _actions.Add(datagrams[index]);
        }

        private bool TryBeginFrozenTailRelease()
        {
            var tails = new byte[2][][];
            for (int senderIndex = 0; senderIndex < _sessions.Length; senderIndex++)
            {
                if (!_sessions[senderIndex].History.TryReleaseLiveTail(
                        out tails[senderIndex]))
                {
                    RejectResume(
                        ServerResumeFailureKind.InvalidResumeState,
                        ResumeRejectedReason.UnsafeResume);
                    return false;
                }
            }

            var payloadsByReceiver = new byte[2][][];
            for (int senderIndex = 0; senderIndex < tails.Length; senderIndex++)
                payloadsByReceiver[1 - senderIndex] = tails[senderIndex];
            _resumeAttempt.BeginTailRelease(payloadsByReceiver);
            return true;
        }

        private void PumpFrozenTails(KcpServerRoundBudget budget)
        {
            if (_resumeAttempt == null ||
                !_resumeAttempt.IsTailReleaseStarted ||
                _resumeAttempt.IsCompleted)
            {
                return;
            }

            for (byte receiverIndex = 0; receiverIndex < 2; receiverIndex++)
            {
                KcpServerSession receiver = _sessions[receiverIndex];
                if (receiver == null || receiver.KcpSession == null)
                {
                    RejectResume(
                        ServerResumeFailureKind.ProcessingBudgetExceeded,
                        ResumeRejectedReason.UnsafeResume);
                    return;
                }

                while (_resumeAttempt.PendingTailCount > 0 &&
                       budget.HasLiveSliceBudget(
                           System.Diagnostics.Stopwatch.GetTimestamp()) &&
                       budget.TryTakeMessage(receiverIndex) &&
                       _resumeAttempt.TryTakeNextTailPayload(
                           receiverIndex,
                           out byte[] payload))
                {
                    if (!RouteCProtocolCodec.TryDecodeBusinessInput(
                            payload,
                            out uint raw,
                            out int frameID) ||
                        receiver.KcpSession.SendBusinessInput(raw, frameID) != 0)
                    {
                        RejectResume(
                            ServerResumeFailureKind.ProcessingBudgetExceeded,
                            ResumeRejectedReason.UnsafeResume);
                        return;
                    }
                }
            }

            TryCompleteResumeAfterTailQueue();
        }

        private void TryCompleteResumeAfterTailQueue()
        {
            if (_resumeAttempt == null ||
                _resumeAttempt.IsCompleted ||
                !_resumeAttempt.BothPlayersCompleted ||
                !_resumeAttempt.IsTailReleaseStarted ||
                _resumeAttempt.PendingTailCount != 0)
            {
                return;
            }

            CompleteResumeAttempt();
            EnqueueResumeComplete();
        }

        private void CompleteResumeAttempt()
        {
            KcpServerSession reconnecting =
                _sessions[_resumeAttempt.ReconnectingPlayerIndex];
            KcpServerSession peer = _sessions[_resumeAttempt.PeerPlayerIndex];
            byte[] attemptID = _resumeAttempt.CopyAttemptID();
            byte[] reconnectingPayload =
                RouteCProtocolCodec.EncodeResumeComplete(
                    attemptID,
                    _resumeAttempt.UploadThrough,
                    _resumeAttempt.ReplayThrough);
            byte[] peerPayload = RouteCProtocolCodec.EncodeResumeComplete(
                attemptID,
                _resumeAttempt.ReplayThrough,
                _resumeAttempt.PeerThrough);
            var reconnectingDatagram = new KcpServerDatagram(
                reconnecting.Endpoint,
                RouteCProtocolCodec.Encode(
                    RouteCMessageType.ResumeComplete,
                    reconnecting.SessionId,
                    reconnecting.Generation,
                    reconnectingPayload));
            var peerDatagram = new KcpServerDatagram(
                peer.Endpoint,
                RouteCProtocolCodec.Encode(
                    RouteCMessageType.ResumeComplete,
                    peer.SessionId,
                    peer.Generation,
                    peerPayload));

            reconnecting.CompleteResume();
            peer.CompleteResume();
            _resumeAttempt.Complete(
                reconnectingDatagram,
                peerDatagram);
        }

        private void EnqueueResumeComplete()
        {
            KcpServerDatagram[] datagrams =
                _resumeAttempt.CopyCompleteDatagrams();
            for (int index = 0; index < datagrams.Length; index++)
                _actions.Add(datagrams[index]);
        }

        private void BeginRunning()
        {
            for (int index = 0; index < _sessions.Length; index++)
            {
                KcpServerSession session = _sessions[index];
                byte[] startDatagram = RouteCProtocolCodec.Encode(
                    RouteCMessageType.Start,
                    session.SessionId,
                    session.Generation,
                    RouteCProtocolCodec.EncodeStart(0));
                session.BeginRunning(startDatagram);
            }
        }

        private void EnqueueStart(KcpServerSession session)
        {
            byte[] startDatagram = session.CopyStartDatagram();
            if (startDatagram != null)
            {
                _actions.Add(new KcpServerDatagram(
                    session.Endpoint,
                    startDatagram));
            }
        }

        private RouteCSessionId AllocateSessionId()
        {
            for (int attempt = 0; attempt < 1024; attempt++)
            {
                RouteCSessionId candidate = _sessionIdFactory();
                if (!candidate.IsZero && FindSession(candidate) == null)
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException(
                "Unable to allocate a unique nonzero SessionID.");
        }

        private uint AllocateConversation()
        {
            for (int attempt = 0; attempt < 1024; attempt++)
            {
                uint candidate = _conversationFactory();
                if (candidate != 0u && !ConversationIsActive(candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException(
                "Unable to allocate a unique nonzero KCP Conv.");
        }

        private bool ConversationIsActive(uint conversation)
        {
            for (int index = 0; index < _sessions.Length; index++)
            {
                KcpServerSession session = _sessions[index];
                if (session != null &&
                    session.Conversation == conversation)
                {
                    return true;
                }
            }
            return false;
        }

        private void AttachKcp(KcpServerSession session)
        {
            if (_kcpIntervalMs == 0)
            {
                return;
            }

            session.AttachKcp(
                new RouteCKcpSettings(_kcpIntervalMs),
                (bytes, count) =>
                {
                    var payload = new byte[count];
                    Buffer.BlockCopy(bytes, 0, payload, 0, count);
                    byte[] datagram = RouteCProtocolCodec.Encode(
                        RouteCMessageType.KcpData,
                        session.SessionId,
                        session.Generation,
                        payload);
                    _actions.Add(new KcpServerDatagram(
                        session.Endpoint,
                        datagram));
                });
            _diagnostics.RecordKcpCoreInterval(
                session.PlayerIndex,
                ReadCoreIntervalMs(session.KcpSession));
        }

        private static int ReadCoreIntervalMs(RouteCKcpSession session)
        {
            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.NonPublic;
            FieldInfo coreField = typeof(RouteCKcpSession).GetField(
                "_kcp",
                flags);
            if (coreField == null)
            {
                throw new InvalidOperationException(
                    "RouteCKcpSession core field is unavailable.");
            }
            object core = coreField.GetValue(session);
            FieldInfo intervalField = core.GetType().GetField(
                "interval",
                flags);
            if (intervalField == null)
            {
                throw new InvalidOperationException(
                    "kcp2k core interval field is unavailable.");
            }
            return Convert.ToInt32(
                intervalField.GetValue(core),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        private void DrainBusinessInputs(
            KcpServerSession sender,
            KcpServerRoundBudget budget)
        {
            while (sender.KcpSession.HasPendingBusinessInput)
            {
                if (!budget.HasLiveSliceBudget(Stopwatch.GetTimestamp()) ||
                    !budget.TryTakeMessage(sender.PlayerIndex))
                {
                    break;
                }
                if (!sender.KcpSession.TryReceiveBusinessInput(
                        out uint raw,
                        out int frameID))
                {
                    break;
                }
                if (frameID < 0)
                {
                    continue;
                }

                HandleDecodedBusinessInput(sender, raw, frameID);
            }
        }

        internal bool HandleDecodedBusinessInput(
            KcpServerSession sender,
            uint raw,
            int frameID)
        {
            if (sender == null || frameID < 0 || _terminated)
                return false;

            OutboundHistoryDisposition disposition =
                sender.History.Record(frameID, raw);
            switch (disposition)
            {
                case OutboundHistoryDisposition.Accepted:
                    RelayAcceptedBusinessInput(sender, raw, frameID);
                    return true;
                case OutboundHistoryDisposition.IdempotentDuplicate:
                    RelayFrozenUploadIfRequired(sender, raw, frameID);
                    return true;
                case OutboundHistoryDisposition.ConflictingDuplicate:
                    RejectResume(
                        ServerResumeFailureKind.ConflictingInput,
                        ResumeRejectedReason.UnsafeResume);
                    return false;
                case OutboundHistoryDisposition.CapacityExceeded:
                    RejectResume(
                        ServerResumeFailureKind.LiveTailCapacityExceeded,
                        ResumeRejectedReason.UnsafeResume);
                    return false;
                case OutboundHistoryDisposition.HistoryUnavailable:
                    if (_resumeAttempt != null)
                    {
                        RejectResume(
                            ServerResumeFailureKind.ServerHistoryUnavailable,
                            ResumeRejectedReason.UnsafeResume);
                    }
                    return false;
                default:
                    return false;
            }
        }

        private void RelayAcceptedBusinessInput(
            KcpServerSession sender,
            uint raw,
            int frameID)
        {
            KcpServerSession receiver = _sessions[1 - sender.PlayerIndex];
            if (receiver == null || receiver.KcpSession == null)
                return;

            bool normalRelay =
                sender.State == NetworkSessionState.Running &&
                receiver.State == NetworkSessionState.Running;
            if (normalRelay || IsRequiredFrozenUpload(sender, frameID))
                receiver.KcpSession.SendBusinessInput(raw, frameID);
        }

        private void RelayFrozenUploadIfRequired(
            KcpServerSession sender,
            uint raw,
            int frameID)
        {
            if (!IsRequiredFrozenUpload(sender, frameID))
                return;

            KcpServerSession receiver = _sessions[1 - sender.PlayerIndex];
            if (receiver != null && receiver.KcpSession != null)
                receiver.KcpSession.SendBusinessInput(raw, frameID);
        }

        private bool IsRequiredFrozenUpload(
            KcpServerSession sender,
            int frameID)
        {
            return _resumeAttempt != null &&
                _resumeAttempt.IsFrozen &&
                sender.PlayerIndex == _resumeAttempt.ReconnectingPlayerIndex &&
                frameID >= _resumeAttempt.UploadFrom &&
                frameID <= _resumeAttempt.UploadThrough;
        }

        private void ApplyTimeouts(uint nowMs)
        {
            if (_terminated)
            {
                return;
            }

            bool graceExpired = false;
            for (int index = 0; index < _sessions.Length; index++)
            {
                KcpServerSession session = _sessions[index];
                if (session == null)
                {
                    continue;
                }

                uint idleMs = unchecked(nowMs - session.LastValidActivityAt);
                if (session.State == NetworkSessionState.Running &&
                    idleMs >= RouteCProtocolConstants.DisconnectTimeoutMs)
                {
                    session.MarkTimedOut();
                }
                if ((session.State == NetworkSessionState.Reconnecting ||
                     session.State == NetworkSessionState.Resuming) &&
                    idleMs >= RouteCProtocolConstants.ResumeGraceMs)
                {
                    graceExpired = true;
                }
            }

            if (graceExpired)
            {
                TerminateMatch();
            }
        }

        private void TerminateMatch()
        {
            RejectResume(
                ServerResumeFailureKind.GraceExpired,
                ResumeRejectedReason.ResumeGraceExpired);
        }

        private void RejectResume(
            ServerResumeFailureKind failureKind,
            ResumeRejectedReason reason)
        {
            if (_terminated)
            {
                return;
            }

            _terminated = true;
            _diagnostics.RecordResumeFailure(failureKind);
            _actions.Clear();
            byte[] attemptId = _resumeAttempt == null ||
                _resumeAttempt.IsCompleted
                ? new byte[RouteCProtocolConstants.NonceSize]
                : _resumeAttempt.CopyAttemptID();
            byte[] payload = RouteCProtocolCodec.EncodeResumeRejected(
                attemptId,
                (ushort)reason);
            for (int index = 0; index < _sessions.Length; index++)
            {
                KcpServerSession session = _sessions[index];
                if (session == null)
                {
                    continue;
                }

                session.Terminate();
                byte[] datagram = RouteCProtocolCodec.Encode(
                    RouteCMessageType.ResumeRejected,
                    session.SessionId,
                    session.Generation,
                    payload);
                _actions.Add(new KcpServerDatagram(
                    session.Endpoint,
                    datagram));
            }
        }

        private bool Reject(
            RouteCProtocolDropReason reason,
            out KcpServerSession session)
        {
            _diagnostics.RecordDrop(reason);
            session = null;
            return false;
        }
    }
}

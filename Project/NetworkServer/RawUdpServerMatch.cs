using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using FrameSyncDemo;

namespace FrameSyncServer
{
    public sealed class RawUdpServerMatch
    {
        private readonly int _windowSize;
        private readonly Func<RouteCSessionId> _sessionFactory;
        private readonly List<RawUdpServerPlayerSlot> _slots =
            new List<RawUdpServerPlayerSlot>(2);
        private bool _isTerminal;
        private RawUdpFaultReason _terminalReason;
        private byte _terminalPlayerIndex = 255;
        private int _terminalFrameID = -1;
        private int _terminalObservedLatestFrameID = -1;
        private OutboundAction[] _terminalActions = new OutboundAction[0];

        public RawUdpServerMatch(int windowSize)
            : this(windowSize, CreateRandomSessionId)
        {
        }

        public RawUdpServerMatch(
            int windowSize,
            Func<RouteCSessionId> sessionFactory)
        {
            if (windowSize < RouteCProtocolConstants.RawMinimumWindowSize ||
                windowSize > RouteCProtocolConstants.RawMaximumWindowSize)
            {
                throw new ArgumentOutOfRangeException(nameof(windowSize));
            }
            _windowSize = windowSize;
            _sessionFactory = sessionFactory ??
                throw new ArgumentNullException(nameof(sessionFactory));
        }

        public bool RelayEnabled =>
            _slots.Count == 2 && _slots[0].IsReady && _slots[1].IsReady;
        public int PlayerCount => _slots.Count;
        public bool IsTerminal => _isTerminal;
        public RawUdpFaultReason TerminalReason => _terminalReason;
        public byte TerminalPlayerIndex => _terminalPlayerIndex;
        public int TerminalFrameID => _terminalFrameID;
        public int TerminalObservedLatestFrameID =>
            _terminalObservedLatestFrameID;
        public long LateRecoveryRelayCopies { get; private set; }
        public int WindowSize => _windowSize;

        public bool TryGetPlayerIndex(
            IPEndPoint endpoint,
            out byte playerIndex)
        {
            RawUdpServerPlayerSlot slot = FindByEndpoint(endpoint);
            if (slot == null)
            {
                playerIndex = 0;
                return false;
            }
            playerIndex = slot.PlayerIndex;
            return true;
        }

        public Result TerminateCapacityExceeded(byte playerIndex)
        {
            return Terminate(
                RawUdpFaultReason.CapacityExceeded,
                playerIndex,
                -1,
                -1);
        }

        public Result TerminateTimeout(
            RawUdpFaultReason reason,
            byte playerIndex)
        {
            if (reason != RawUdpFaultReason.HandshakeTimeout &&
                reason != RawUdpFaultReason.ConnectionTimedOut)
            {
                throw new ArgumentOutOfRangeException(nameof(reason));
            }
            return Terminate(reason, playerIndex, -1, -1);
        }

        public byte GetHandshakeTimeoutPlayerIndex()
        {
            if (_slots.Count != 2)
                return 255;
            if (_slots[0].IsReady == _slots[1].IsReady)
                return 255;
            return _slots[0].IsReady ? (byte)1 : (byte)0;
        }

        public OutboundAction[] CreateStartRetries()
        {
            if (!RelayEnabled || _isTerminal)
                return new OutboundAction[0];

            var actions = new List<OutboundAction>(2);
            for (int index = 0; index < _slots.Count; index++)
            {
                RawUdpServerPlayerSlot slot = _slots[index];
                if (!slot.HasFrameZero)
                    actions.Add(CreateStart(slot));
            }
            return actions.ToArray();
        }

        public OutboundAction[] CreateTerminalFaultRetries()
        {
            return _isTerminal
                ? (OutboundAction[])_terminalActions.Clone()
                : new OutboundAction[0];
        }

        public Result Process(IPEndPoint endpoint, byte[] datagram)
        {
            return Process(endpoint, datagram, RelayEnabled);
        }

        public Result Process(
            IPEndPoint endpoint,
            byte[] datagram,
            bool relayEnabledAtReceive)
        {
            if (endpoint == null)
                throw new ArgumentNullException(nameof(endpoint));
            if (datagram == null)
                throw new ArgumentNullException(nameof(datagram));
            if (_isTerminal)
                return Result.Terminal(_terminalReason, new OutboundAction[0]);

            RawUdpServerPlayerSlot endpointSlot = FindByEndpoint(endpoint);
            if (datagram.Length > RouteCProtocolConstants.MaximumDatagramSize)
            {
                return endpointSlot == null
                    ? Result.Drop(RouteCProtocolDropReason.RawDatagramTooLarge)
                    : Terminate(
                        RawUdpFaultReason.ProtocolViolation,
                        endpointSlot.PlayerIndex,
                        -1,
                        -1);
            }

            if (!RouteCProtocolCodec.TryDecode(
                datagram,
                datagram.Length,
                out RouteCProtocolMessage message,
                out RouteCProtocolDropReason envelopeReason))
            {
                return Result.Drop(envelopeReason);
            }

            if (message.MessageType == RouteCMessageType.RawInput &&
                datagram.Length >
                RouteCProtocolConstants.RawMaximumInputDatagramSize)
            {
                return endpointSlot == null
                    ? Result.Drop(RouteCProtocolDropReason.RawDatagramTooLarge)
                    : Terminate(
                        RawUdpFaultReason.ProtocolViolation,
                        endpointSlot.PlayerIndex,
                        -1,
                        -1);
            }

            if (message.MessageType == RouteCMessageType.RawHello)
                return HandleHello(endpoint, endpointSlot, message);

            RawUdpServerPlayerSlot sessionSlot = FindBySession(message.SessionId);
            if (sessionSlot == null)
                return Result.Drop(RouteCProtocolDropReason.SessionNotFound);
            if (!sessionSlot.EndpointEquals(endpoint))
                return Result.Drop(RouteCProtocolDropReason.WrongEndpoint);
            if (message.Generation != RouteCProtocolConstants.RawGeneration)
                return Result.Drop(RouteCProtocolDropReason.StaleGeneration);

            switch (message.MessageType)
            {
                case RouteCMessageType.RawReady:
                    return HandleReady(sessionSlot, message);
                case RouteCMessageType.RawInput:
                    return HandleInput(
                        sessionSlot,
                        message,
                        relayEnabledAtReceive);
                case RouteCMessageType.RawFault:
                    return HandleFault(sessionSlot, message);
                default:
                    return Terminate(
                        RawUdpFaultReason.ProtocolViolation,
                        sessionSlot.PlayerIndex,
                        -1,
                        -1);
            }
        }

        private Result HandleReady(
            RawUdpServerPlayerSlot slot,
            RouteCProtocolMessage message)
        {
            if (!RawUdpProtocolCodec.TryValidateOuterMessage(
                    message,
                    true,
                    out _) ||
                !RawUdpProtocolCodec.TryDecodeReady(message.Payload))
            {
                return Terminate(
                    RawUdpFaultReason.ProtocolViolation,
                    slot.PlayerIndex,
                    -1,
                    -1);
            }

            slot.IsReady = true;
            OutboundAction[] actions = RelayEnabled
                ? CreateStartRetries()
                : new OutboundAction[0];
            return Result.Accept(actions, 0);
        }

        private Result HandleInput(
            RawUdpServerPlayerSlot slot,
            RouteCProtocolMessage message,
            bool relayEnabledAtReceive)
        {
            if (!relayEnabledAtReceive || !RelayEnabled)
            {
                return Terminate(
                    RawUdpFaultReason.ProtocolViolation,
                    slot.PlayerIndex,
                    -1,
                    -1);
            }
            if (!RawUdpProtocolCodec.TryValidateOuterMessage(
                    message,
                    true,
                    out _) ||
                !RawUdpProtocolCodec.TryDecodeInput(
                    message.Payload,
                    (byte)_windowSize,
                    out RawUdpInputWindow window,
                    out _))
            {
                return Terminate(
                    RawUdpFaultReason.ProtocolViolation,
                    slot.PlayerIndex,
                    -1,
                    -1);
            }
            if (window.PlayerIndex != slot.PlayerIndex)
            {
                return Terminate(
                    RawUdpFaultReason.ProtocolViolation,
                    slot.PlayerIndex,
                    window.LatestFrameID,
                    slot.InputReceiver.HighestObservedLatestFrameID);
            }

            int highestObservedBeforeWindow =
                slot.InputReceiver.HighestObservedLatestFrameID;
            RawUdpInputDisposition disposition = slot.InputReceiver.Accept(
                in window,
                out RawUdpInputEntry[] acceptedEntries,
                out RawUdpFault terminalFault);
            if (disposition == RawUdpInputDisposition.Conflict ||
                disposition == RawUdpInputDisposition.CapacityExceeded ||
                disposition == RawUdpInputDisposition.UnrecoverableGap)
            {
                return Terminate(
                    terminalFault.Reason,
                    terminalFault.PlayerIndex,
                    terminalFault.FrameID,
                    terminalFault.ObservedLatestFrameID);
            }
            if (disposition == RawUdpInputDisposition.TooOld)
            {
                return Result.Drop(
                    slot.InputReceiver.LastSequenceDisposition ==
                        RawUdpSequenceDisposition.TooOld
                        ? RouteCProtocolDropReason.PacketTooOld
                        : RouteCProtocolDropReason.InputTooOld,
                    true);
            }

            for (int index = 0; index < acceptedEntries.Length; index++)
            {
                RawUdpInputEntry acceptedEntry = acceptedEntries[index];
                RawUdpInputDisposition historyDisposition =
                    slot.RelayHistory.Remember(in acceptedEntry);
                if (historyDisposition == RawUdpInputDisposition.Conflict)
                {
                    return Terminate(
                        RawUdpFaultReason.ConflictingInput,
                        slot.PlayerIndex,
                        acceptedEntry.FrameID,
                        slot.InputReceiver.HighestObservedLatestFrameID);
                }
                if (historyDisposition == RawUdpInputDisposition.CapacityExceeded)
                {
                    return Terminate(
                        RawUdpFaultReason.CapacityExceeded,
                        slot.PlayerIndex,
                        acceptedEntry.FrameID,
                        slot.InputReceiver.HighestObservedLatestFrameID);
                }
                if (acceptedEntry.FrameID == 0)
                    slot.HasFrameZero = true;
            }

            if (slot.InputReceiver.LastSequenceDisposition !=
                RawUdpSequenceDisposition.Accepted)
            {
                return Result.Accept(new OutboundAction[0], 0);
            }

            RawUdpInputEntry[] rebuiltEntries =
                slot.RelayHistory.RebuildWindow(window);
            int relayCopies = slot.RelayHistory.GetRelayCopyCount(
                acceptedEntries,
                highestObservedBeforeWindow);
            RawUdpServerPlayerSlot peer = _slots[1 - slot.PlayerIndex];
            var actions = new OutboundAction[relayCopies];
            for (int copy = 0; copy < relayCopies; copy++)
            {
                uint downlinkSequence =
                    slot.RelayHistory.TakeNextDownlinkSequence(
                        peer.PlayerIndex);
                byte[] payload = RawUdpProtocolCodec.EncodeInput(
                    slot.PlayerIndex,
                    (byte)_windowSize,
                    downlinkSequence,
                    window.LatestFrameID,
                    rebuiltEntries);
                byte[] datagram = RouteCProtocolCodec.Encode(
                    RouteCMessageType.RawInput,
                    peer.SessionId,
                    RouteCProtocolConstants.RawGeneration,
                    payload);
                actions[copy] = new OutboundAction(peer.Endpoint, datagram);
                slot.RelayHistory.
                    RecordDownlinkOpportunityForEveryEntryInWindow(
                        rebuiltEntries);
            }
            LateRecoveryRelayCopies += relayCopies - 1;
            return Result.Accept(
                actions,
                acceptedEntries.Length + actions.Length);
        }

        private Result HandleFault(
            RawUdpServerPlayerSlot slot,
            RouteCProtocolMessage message)
        {
            if (!RawUdpProtocolCodec.TryValidateOuterMessage(
                    message,
                    true,
                    out _) ||
                !RawUdpProtocolCodec.TryDecodeFault(
                    message.Payload,
                    out RawUdpFault fault) ||
                fault.Reason == RawUdpFaultReason.MatchFull)
            {
                return Terminate(
                    RawUdpFaultReason.ProtocolViolation,
                    slot.PlayerIndex,
                    -1,
                    -1);
            }

            return Terminate(
                fault.Reason,
                fault.PlayerIndex,
                fault.FrameID,
                fault.ObservedLatestFrameID);
        }

        private static OutboundAction CreateStart(
            RawUdpServerPlayerSlot slot)
        {
            byte[] datagram = RouteCProtocolCodec.Encode(
                RouteCMessageType.RawStart,
                slot.SessionId,
                RouteCProtocolConstants.RawGeneration,
                RawUdpProtocolCodec.EncodeStart(0));
            return new OutboundAction(slot.Endpoint, datagram);
        }

        private Result HandleHello(
            IPEndPoint endpoint,
            RawUdpServerPlayerSlot endpointSlot,
            RouteCProtocolMessage message)
        {
            if (!RawUdpProtocolCodec.TryDecodeHello(
                message.Payload,
                out byte[] nonce))
            {
                return endpointSlot == null
                    ? Result.Drop(RouteCProtocolDropReason.InvalidRawControlPayload)
                    : Terminate(
                        RawUdpFaultReason.ProtocolViolation,
                        endpointSlot.PlayerIndex,
                        -1,
                        -1);
            }

            if (endpointSlot != null)
            {
                if (!endpointSlot.NonceEquals(nonce))
                {
                    return Terminate(
                        RawUdpFaultReason.ProtocolViolation,
                        endpointSlot.PlayerIndex,
                        -1,
                        -1);
                }
                if (!message.SessionId.IsZero || message.Generation != 0u)
                    return Result.Drop(RouteCProtocolDropReason.StaleGeneration);
                return Result.Send(new OutboundAction(
                    endpoint,
                    endpointSlot.WelcomeDatagram));
            }

            if (!message.SessionId.IsZero || message.Generation != 0u)
                return Result.Drop(RouteCProtocolDropReason.StaleGeneration);
            if (FindByNonce(nonce) != null)
                return Result.Drop(RouteCProtocolDropReason.WrongEndpoint);
            if (_slots.Count >= 2)
                return Result.Send(CreateMatchFull(endpoint));

            byte playerIndex = (byte)_slots.Count;
            RouteCSessionId sessionId = AllocateSessionId();
            byte[] welcomePayload = RawUdpProtocolCodec.EncodeWelcome(
                nonce,
                playerIndex,
                (byte)_windowSize);
            byte[] welcomeDatagram = RouteCProtocolCodec.Encode(
                RouteCMessageType.RawWelcome,
                sessionId,
                RouteCProtocolConstants.RawGeneration,
                welcomePayload);
            var slot = new RawUdpServerPlayerSlot(
                playerIndex,
                endpoint,
                nonce,
                sessionId,
                _windowSize,
                welcomeDatagram);
            _slots.Add(slot);
            return Result.Send(new OutboundAction(endpoint, welcomeDatagram));
        }

        private OutboundAction CreateMatchFull(IPEndPoint endpoint)
        {
            var fault = new RawUdpFault(
                RawUdpFaultReason.MatchFull,
                255,
                -1,
                -1);
            byte[] datagram = RouteCProtocolCodec.Encode(
                RouteCMessageType.RawFault,
                RouteCSessionId.Zero,
                0u,
                RawUdpProtocolCodec.EncodeFault(in fault));
            return new OutboundAction(endpoint, datagram);
        }

        private Result Terminate(
            RawUdpFaultReason reason,
            byte playerIndex,
            int frameID,
            int observedLatestFrameID)
        {
            _isTerminal = true;
            _terminalReason = reason;
            _terminalPlayerIndex = playerIndex;
            _terminalFrameID = frameID;
            _terminalObservedLatestFrameID = observedLatestFrameID;
            var actions = new OutboundAction[_slots.Count];
            var fault = new RawUdpFault(
                reason,
                playerIndex,
                frameID,
                observedLatestFrameID);
            byte[] payload = RawUdpProtocolCodec.EncodeFault(in fault);
            for (int index = 0; index < _slots.Count; index++)
            {
                RawUdpServerPlayerSlot slot = _slots[index];
                byte[] datagram = RouteCProtocolCodec.Encode(
                    RouteCMessageType.RawFault,
                    slot.SessionId,
                    RouteCProtocolConstants.RawGeneration,
                    payload);
                actions[index] = new OutboundAction(slot.Endpoint, datagram);
            }
            _terminalActions = (OutboundAction[])actions.Clone();
            return Result.Terminal(reason, actions);
        }

        private RouteCSessionId AllocateSessionId()
        {
            for (int attempt = 0; attempt < 1024; attempt++)
            {
                RouteCSessionId candidate = _sessionFactory();
                if (!candidate.IsZero && FindBySession(candidate) == null)
                    return candidate;
            }
            throw new InvalidOperationException("Could not allocate a unique Raw UDP SessionID.");
        }

        private RawUdpServerPlayerSlot FindByEndpoint(IPEndPoint endpoint)
        {
            for (int index = 0; index < _slots.Count; index++)
            {
                if (_slots[index].EndpointEquals(endpoint))
                    return _slots[index];
            }
            return null;
        }

        private RawUdpServerPlayerSlot FindByNonce(byte[] nonce)
        {
            for (int index = 0; index < _slots.Count; index++)
            {
                if (_slots[index].NonceEquals(nonce))
                    return _slots[index];
            }
            return null;
        }

        private RawUdpServerPlayerSlot FindBySession(RouteCSessionId sessionId)
        {
            for (int index = 0; index < _slots.Count; index++)
            {
                if (_slots[index].SessionId == sessionId)
                    return _slots[index];
            }
            return null;
        }

        private static RouteCSessionId CreateRandomSessionId()
        {
            var bytes = new byte[16];
            using (RandomNumberGenerator generator = RandomNumberGenerator.Create())
                generator.GetBytes(bytes);
            return new RouteCSessionId(
                ReadUInt64BigEndian(bytes, 0),
                ReadUInt64BigEndian(bytes, 8));
        }

        private static ulong ReadUInt64BigEndian(byte[] value, int offset)
        {
            ulong result = 0UL;
            for (int index = 0; index < 8; index++)
                result = (result << 8) | value[offset + index];
            return result;
        }

        public sealed class OutboundAction
        {
            private readonly byte[] _datagram;

            public OutboundAction(IPEndPoint endpoint, byte[] datagram)
            {
                Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
                if (datagram == null)
                    throw new ArgumentNullException(nameof(datagram));
                _datagram = (byte[])datagram.Clone();
            }

            public IPEndPoint Endpoint { get; }
            public byte[] Datagram => (byte[])_datagram.Clone();
        }

        public sealed class Result
        {
            private Result(
                bool isTerminal,
                RawUdpFaultReason faultReason,
                RouteCProtocolDropReason dropReason,
                OutboundAction[] actions,
                int acceptedWorkItems)
            {
                IsTerminal = isTerminal;
                FaultReason = faultReason;
                DropReason = dropReason;
                Actions = actions ?? new OutboundAction[0];
                AcceptedWorkItems = acceptedWorkItems;
                IsValidTraffic = false;
            }

            public bool IsTerminal { get; }
            public RawUdpFaultReason FaultReason { get; }
            public RouteCProtocolDropReason DropReason { get; }
            public OutboundAction[] Actions { get; }
            public int AcceptedWorkItems { get; }
            public bool IsValidTraffic { get; private set; }

            public static Result Send(OutboundAction action)
            {
                Result result = new Result(
                    false,
                    default,
                    RouteCProtocolDropReason.None,
                    new[] { action },
                    0);
                result.IsValidTraffic = true;
                return result;
            }

            public static Result Accept(
                OutboundAction[] actions,
                int acceptedWorkItems)
            {
                Result result = new Result(
                    false,
                    default,
                    RouteCProtocolDropReason.None,
                    actions,
                    acceptedWorkItems);
                result.IsValidTraffic = true;
                return result;
            }

            public static Result Drop(
                RouteCProtocolDropReason reason,
                bool isValidTraffic = false)
            {
                Result result = new Result(
                    false,
                    default,
                    reason,
                    new OutboundAction[0],
                    0);
                result.IsValidTraffic = isValidTraffic;
                return result;
            }

            public static Result Terminal(
                RawUdpFaultReason reason,
                OutboundAction[] actions)
            {
                return new Result(
                    true,
                    reason,
                    RouteCProtocolDropReason.None,
                    actions,
                    0);
            }
        }
    }
}

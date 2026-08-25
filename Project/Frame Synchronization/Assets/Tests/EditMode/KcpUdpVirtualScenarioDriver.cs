using System;
using System.Collections.Generic;

namespace FrameSyncDemo.Tests
{
    public sealed class KcpUdpVirtualScenarioDriver
    {
        public enum ProtocolScenarioKind
        {
            InitialHandshake,
            ReconnectWelcomeLoss,
            ResumeControlLoss,
            ResumeControlRejected
        }

        public enum ProtocolOutcome
        {
            BoundExceeded,
            Running,
            ResumeRejected
        }

        public sealed class ProtocolResult
        {
            public ProtocolOutcome Outcome { get; internal set; }
            public NetworkSessionState FinalState { get; internal set; }
            public bool TerminatedWithinBound { get; internal set; }
            public int TerminationTimeMs { get; internal set; }
            public int StepCount { get; internal set; }
            public int HelloSentCount { get; internal set; }
            public int WelcomeSentCount { get; internal set; }
            public int ReadySentCount { get; internal set; }
            public int StartSentCount { get; internal set; }
            public int ReconnectWelcomeSentCount { get; internal set; }
            public int ResumeAcceptedSentCount { get; internal set; }
            public int ResumeCompleteSentCount { get; internal set; }
            public int DroppedDatagramCount { get; internal set; }
            public int ReorderedDatagramCount { get; internal set; }
            public int DuplicateDatagramCount { get; internal set; }
            public int NonZeroJitterCount { get; internal set; }
            public string[] TraceLines { get; internal set; }
            public bool ClientObservedResumeRejected { get; internal set; }
        }

        public static Result RunSingleMessage(
            UdpDatagramFaultProfile clientToServerProfile,
            UdpDatagramFaultProfile serverToClientProfile,
            uint raw,
            int frameID,
            int maximumTimeMs)
        {
            if (frameID < 0)
                throw new ArgumentOutOfRangeException(nameof(frameID));
            if (maximumTimeMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumTimeMs));

            var run = new KcpRun(
                clientToServerProfile,
                serverToClientProfile,
                new[] { raw },
                new[] { frameID },
                1,
                maximumTimeMs);
            return run.Execute();
        }

        public static Result RunSequence(
            UdpDatagramFaultProfile clientToServerProfile,
            UdpDatagramFaultProfile serverToClientProfile,
            int messageCount,
            int sendIntervalMs,
            int maximumTimeMs)
        {
            if (messageCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(messageCount));
            if (sendIntervalMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(sendIntervalMs));
            if (maximumTimeMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumTimeMs));

            var raws = new uint[messageCount];
            var frameIDs = new int[messageCount];
            for (int index = 0; index < messageCount; index++)
            {
                raws[index] = unchecked(0xABC00000u + (uint)index);
                frameIDs[index] = index;
            }
            var run = new KcpRun(
                clientToServerProfile,
                serverToClientProfile,
                raws,
                frameIDs,
                sendIntervalMs,
                maximumTimeMs);
            return run.Execute();
        }

        public static ConvergenceResult RunConvergenceScenario(
            UdpDatagramFaultProfile peerZeroToOneProfile,
            UdpDatagramFaultProfile peerOneToZeroProfile,
            int frameCount,
            int maximumTimeMs)
        {
            if (frameCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(frameCount));
            if (maximumTimeMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumTimeMs));

            var frames = new int[frameCount];
            var peerZeroInputs = new uint[frameCount];
            var peerOneInputs = new uint[frameCount];
            for (int frame = 0; frame < frameCount; frame++)
            {
                frames[frame] = frame;
                peerZeroInputs[frame] = new FrameInput(3, 0)._raw;
                peerOneInputs[frame] = default;
            }

            Result zeroToOne = new KcpRun(
                peerZeroToOneProfile,
                peerOneToZeroProfile,
                peerZeroInputs,
                frames,
                1,
                maximumTimeMs).Execute();
            Result oneToZero = new KcpRun(
                peerOneToZeroProfile,
                peerZeroToOneProfile,
                peerOneInputs,
                frames,
                1,
                maximumTimeMs).Execute();

            FrameSyncCoordinator peerZero = CreateCoordinator();
            FrameSyncCoordinator peerOne = CreateCoordinator();
            bool intermediatePredictionDiverged = false;
            bool advancesSucceeded = true;
            for (int frame = 0; frame < frameCount; frame++)
            {
                peerZero.RecordActual(
                    frame,
                    0,
                    new FrameInput(peerZeroInputs[frame]));
                peerOne.RecordActual(
                    frame,
                    1,
                    new FrameInput(peerOneInputs[frame]));

                advancesSucceeded &= peerZero.Advance(
                    frame,
                    peerZero.ResolveForPrediction(frame)).Succeeded;
                advancesSucceeded &= peerOne.Advance(
                    frame,
                    peerOne.ResolveForPrediction(frame)).Succeeded;
                intermediatePredictionDiverged |=
                    WorldHash.Compute(peerZero.PredictedWorld, frame) !=
                    WorldHash.Compute(peerOne.PredictedWorld, frame);
            }

            RecordRemoteActuals(peerZero, 1, oneToZero);
            RecordRemoteActuals(peerOne, 0, zeroToOne);
            ReconcileResult peerZeroReconcile = peerZero.Reconcile();
            ReconcileResult peerOneReconcile = peerOne.Reconcile();
            int finalFrame = frameCount - 1;

            return new ConvergenceResult
            {
                TerminatedWithinBound =
                    zeroToOne.TerminatedWithinBound &&
                    oneToZero.TerminatedWithinBound,
                BusinessPayloadSize = RouteCProtocolConstants.BusinessInputSize,
                ActualFramesAscending =
                    HasCompleteAscendingFrames(zeroToOne, frameCount) &&
                    HasCompleteAscendingFrames(oneToZero, frameCount),
                IntermediatePredictionDiverged =
                    intermediatePredictionDiverged,
                PeerZeroConfirmedHash =
                    WorldHash.Compute(peerZero.ConfirmedWorld, finalFrame),
                PeerOneConfirmedHash =
                    WorldHash.Compute(peerOne.ConfirmedWorld, finalFrame),
                PeerZeroPredictedHash =
                    WorldHash.Compute(peerZero.PredictedWorld, finalFrame),
                PeerOnePredictedHash =
                    WorldHash.Compute(peerOne.PredictedWorld, finalFrame),
                PeerZeroConfirmedFrame = peerZero.ConfirmedFrame,
                PeerOneConfirmedFrame = peerOne.ConfirmedFrame,
                PeerZeroPredictedFrame = peerZero.PredictedFrame,
                PeerOnePredictedFrame = peerOne.PredictedFrame,
                PeerZeroReconciled =
                    advancesSucceeded && peerZeroReconcile.Succeeded,
                PeerOneReconciled =
                    advancesSucceeded && peerOneReconcile.Succeeded
            };
        }

        public static ProtocolResult RunProtocolScenario(
            ProtocolScenarioKind scenarioKind,
            UdpDatagramFaultProfile clientToServerProfile,
            UdpDatagramFaultProfile serverToClientProfile,
            int maximumTimeMs,
            int maximumSteps)
        {
            if (!Enum.IsDefined(typeof(ProtocolScenarioKind), scenarioKind))
                throw new ArgumentOutOfRangeException(nameof(scenarioKind));
            if (maximumTimeMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumTimeMs));
            if (maximumSteps <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumSteps));

            var run = new ProtocolRun(
                scenarioKind,
                clientToServerProfile,
                serverToClientProfile,
                maximumTimeMs,
                maximumSteps);
            return run.Execute();
        }

        public sealed class Result
        {
            public bool TerminatedWithinBound { get; internal set; }
            public int TerminationTimeMs { get; internal set; }
            public int StepCount { get; internal set; }
            public uint ReceivedRaw { get; internal set; }
            public int ReceivedFrameID { get; internal set; }
            public int DeliveryCount { get; internal set; }
            public int SenderOutputCount { get; internal set; }
            public int ReceiverOutputCount { get; internal set; }
            public int DroppedDataCount { get; internal set; }
            public int DroppedAckCount { get; internal set; }
            public int ReorderedDatagramCount { get; internal set; }
            public int DuplicateDatagramCount { get; internal set; }
            public string[] TraceLines { get; internal set; }
            public uint[] ReceivedRaws { get; internal set; }
            public int[] ReceivedFrameIDs { get; internal set; }
            public int NetworkOutOfOrderReleaseCount { get; internal set; }
            public int SenderPendingSendCount { get; internal set; }
        }

        public sealed class ConvergenceResult
        {
            public bool TerminatedWithinBound { get; internal set; }
            public int BusinessPayloadSize { get; internal set; }
            public bool ActualFramesAscending { get; internal set; }
            public bool IntermediatePredictionDiverged { get; internal set; }
            public ulong PeerZeroConfirmedHash { get; internal set; }
            public ulong PeerOneConfirmedHash { get; internal set; }
            public ulong PeerZeroPredictedHash { get; internal set; }
            public ulong PeerOnePredictedHash { get; internal set; }
            public int PeerZeroConfirmedFrame { get; internal set; }
            public int PeerOneConfirmedFrame { get; internal set; }
            public int PeerZeroPredictedFrame { get; internal set; }
            public int PeerOnePredictedFrame { get; internal set; }
            public bool PeerZeroReconciled { get; internal set; }
            public bool PeerOneReconciled { get; internal set; }
        }

        private static void RecordRemoteActuals(
            FrameSyncCoordinator coordinator,
            int playerIndex,
            Result networkResult)
        {
            int count = Math.Min(
                networkResult.ReceivedFrameIDs.Length,
                networkResult.ReceivedRaws.Length);
            for (int index = 0; index < count; index++)
            {
                coordinator.RecordActual(
                    networkResult.ReceivedFrameIDs[index],
                    playerIndex,
                    new FrameInput(networkResult.ReceivedRaws[index]));
            }
        }

        private static bool HasCompleteAscendingFrames(
            Result result,
            int frameCount)
        {
            if (result.ReceivedFrameIDs.Length != frameCount ||
                result.ReceivedRaws.Length != frameCount)
            {
                return false;
            }

            for (int frame = 0; frame < frameCount; frame++)
            {
                if (result.ReceivedFrameIDs[frame] != frame)
                    return false;
            }
            return true;
        }

        private static FrameSyncCoordinator CreateCoordinator()
        {
            FixedInt moveDistance = FixedInt.FromInt(1);
            var playerZero = new PlayerEntity();
            playerZero.Reset(
                new FixedVector3(
                    FixedInt.FromInt(-3),
                    FixedInt.Zero,
                    FixedInt.Zero),
                0);
            var playerOne = new PlayerEntity();
            playerOne.Reset(
                new FixedVector3(
                    FixedInt.FromInt(3),
                    FixedInt.Zero,
                    FixedInt.Zero),
                1);
            var players = new[] { playerZero, playerOne };
            var stateMachines = new[]
            {
                new PlayerStateMachine(playerZero),
                new PlayerStateMachine(playerOne)
            };
            var ball = new BallEntity();
            ball.Reset(FixedVector3.Zero);
            playerZero.hasBall = true;
            ball.state = BallEntity.EState.Held;
            ball.holderPlayerIndex = 0;
            if (!BallPossessionSystem.TryUpdateHeldBall(playerZero, ball))
            {
                throw new InvalidOperationException(
                    "Unable to create the deterministic convergence world.");
            }

            var predicted = new DeterministicWorld(
                players,
                stateMachines,
                ball,
                moveDistance,
                CourtConstant.LogicDeltaTime);
            SimulationWorldState initial = predicted.Capture(-1);
            return new FrameSyncCoordinator(
                predicted,
                initial,
                moveDistance,
                CourtConstant.LogicDeltaTime,
                64);
        }

        private sealed class KcpRun
        {
            private static readonly RouteCSessionId SessionId =
                new RouteCSessionId(
                    0x1021324354657687UL,
                    0x98A9BACBDCEDFE0FUL);

            private readonly UdpVirtualDatagramLink _link;
            private readonly RouteCKcpSession _sender;
            private readonly RouteCKcpSession _receiver;
            private readonly uint[] _raws;
            private readonly int[] _frameIDs;
            private readonly int _sendIntervalMs;
            private readonly int _maximumTimeMs;
            private readonly List<string> _traceLines = new List<string>();
            private readonly List<uint> _receivedRaws = new List<uint>();
            private readonly List<int> _receivedFrameIDs = new List<int>();
            private int _nowMs;
            private int _senderOutputCount;
            private int _receiverOutputCount;
            private int _droppedDataCount;
            private int _droppedAckCount;
            private int _reorderedDatagramCount;
            private int _duplicateDatagramCount;
            private int _networkOutOfOrderReleaseCount;
            private int _deliveryCount;
            private uint _receivedRaw;
            private int _receivedFrameID;
            private bool _hasClientToServerSequence;
            private bool _hasServerToClientSequence;
            private uint _clientToServerSequenceHighWater;
            private uint _serverToClientSequenceHighWater;

            public KcpRun(
                UdpDatagramFaultProfile clientToServerProfile,
                UdpDatagramFaultProfile serverToClientProfile,
                uint[] raws,
                int[] frameIDs,
                int sendIntervalMs,
                int maximumTimeMs)
            {
                _link = new UdpVirtualDatagramLink(
                    clientToServerProfile,
                    serverToClientProfile);
                _raws = raws ?? throw new ArgumentNullException(nameof(raws));
                _frameIDs = frameIDs ?? throw new ArgumentNullException(nameof(frameIDs));
                if (_raws.Length == 0 || _raws.Length != _frameIDs.Length)
                    throw new ArgumentException("KCP input scripts must be nonempty and aligned.");
                _sendIntervalMs = sendIntervalMs;
                _maximumTimeMs = maximumTimeMs;
                var settings = new RouteCKcpSettings(
                    RouteCProtocolConstants.InitialKcpIntervalMs);
                _sender = new RouteCKcpSession(
                    0x11223344u,
                    settings,
                    HandleSenderOutput);
                _receiver = new RouteCKcpSession(
                    0x11223344u,
                    settings,
                    HandleReceiverOutput);
            }

            public Result Execute()
            {
                uint senderNextUpdateAt = 0u;
                uint receiverNextUpdateAt = 0u;
                int nextInputIndex = 0;

                for (_nowMs = 0; _nowMs <= _maximumTimeMs; _nowMs++)
                {
                    uint now = (uint)_nowMs;
                    if (nextInputIndex < _raws.Length &&
                        _nowMs == nextInputIndex * _sendIntervalMs)
                    {
                        _sender.SendBusinessInput(
                            _raws[nextInputIndex],
                            _frameIDs[nextInputIndex]);
                        nextInputIndex++;
                    }
                    if (now >= senderNextUpdateAt)
                    {
                        _sender.Update(now, _nowMs);
                        senderNextUpdateAt = _sender.NextUpdateAt(now);
                    }
                    if (now >= receiverNextUpdateAt)
                    {
                        _receiver.Update(now, _nowMs);
                        receiverNextUpdateAt = _receiver.NextUpdateAt(now);
                    }

                    DrainDueDatagrams();
                    DrainBusinessInputs();
                    if (nextInputIndex == _raws.Length &&
                        _deliveryCount == _raws.Length &&
                        _sender.PendingSendCount == 0 &&
                        _link.PendingCount == 0 &&
                        _receiver.PendingSendCount == 0)
                    {
                        return BuildResult(true, _nowMs);
                    }
                }

                return BuildResult(false, _maximumTimeMs);
            }

            private void HandleSenderOutput(byte[] payload, int count)
            {
                _senderOutputCount++;
                EnqueueOutput(
                    UdpDatagramDirection.ClientToServer,
                    payload,
                    count);
            }

            private void HandleReceiverOutput(byte[] payload, int count)
            {
                _receiverOutputCount++;
                EnqueueOutput(
                    UdpDatagramDirection.ServerToClient,
                    payload,
                    count);
            }

            private void EnqueueOutput(
                UdpDatagramDirection direction,
                byte[] payload,
                int count)
            {
                var copy = new byte[count];
                Buffer.BlockCopy(payload, 0, copy, 0, count);
                byte[] envelope = RouteCProtocolCodec.Encode(
                    RouteCMessageType.KcpData,
                    SessionId,
                    1u,
                    copy);
                UdpDatagramDecisionTraceEntry trace = _link.Enqueue(
                    _nowMs,
                    direction,
                    0,
                    envelope);
                _traceLines.Add(trace.ToCanonicalJsonLine());
                if (trace.Decision.Dropped && trace.KcpCommand == 0x51)
                    _droppedDataCount++;
                if (trace.Decision.Dropped && trace.KcpCommand == 0x52)
                    _droppedAckCount++;
                if (trace.Decision.ReorderExtraDelayMs > 0)
                    _reorderedDatagramCount++;
                if (trace.Decision.CopyCount == 2)
                    _duplicateDatagramCount++;
            }

            private void DrainDueDatagrams()
            {
                while (_link.TryDequeueDue(
                    _nowMs,
                    out UdpDatagramDirection direction,
                    out _,
                    out byte[] envelope))
                {
                    if (!RouteCProtocolCodec.TryDecode(
                            envelope,
                            envelope.Length,
                            out RouteCProtocolMessage message,
                            out _) ||
                        message.MessageType != RouteCMessageType.KcpData)
                    {
                        throw new InvalidOperationException(
                            "The virtual link released an invalid KCP envelope.");
                    }

                    byte[] payload = message.Payload;
                    RecordNetworkReleaseOrder(direction, payload);
                    int result = direction == UdpDatagramDirection.ClientToServer
                        ? _receiver.InputDatagram(payload, 0, payload.Length)
                        : _sender.InputDatagram(payload, 0, payload.Length);
                    if (result != 0)
                    {
                        throw new InvalidOperationException(
                            "Real KCP rejected a virtual-link datagram.");
                    }
                }
            }

            private void DrainBusinessInputs()
            {
                while (_receiver.TryReceiveBusinessInput(
                    out uint raw,
                    out int frameID))
                {
                    _deliveryCount++;
                    _receivedRaw = raw;
                    _receivedFrameID = frameID;
                    _receivedRaws.Add(raw);
                    _receivedFrameIDs.Add(frameID);
                }
            }

            private void RecordNetworkReleaseOrder(
                UdpDatagramDirection direction,
                byte[] payload)
            {
                if (!RouteCKcpHeader.TryReadCommandAndSequence(
                        payload,
                        out _,
                        out uint sequence))
                {
                    return;
                }

                if (direction == UdpDatagramDirection.ClientToServer)
                {
                    if (_hasClientToServerSequence &&
                        sequence < _clientToServerSequenceHighWater)
                    {
                        _networkOutOfOrderReleaseCount++;
                    }
                    if (!_hasClientToServerSequence ||
                        sequence > _clientToServerSequenceHighWater)
                    {
                        _clientToServerSequenceHighWater = sequence;
                    }
                    _hasClientToServerSequence = true;
                    return;
                }

                if (_hasServerToClientSequence &&
                    sequence < _serverToClientSequenceHighWater)
                {
                    _networkOutOfOrderReleaseCount++;
                }
                if (!_hasServerToClientSequence ||
                    sequence > _serverToClientSequenceHighWater)
                {
                    _serverToClientSequenceHighWater = sequence;
                }
                _hasServerToClientSequence = true;
            }

            private Result BuildResult(
                bool terminatedWithinBound,
                int terminationTimeMs)
            {
                return new Result
                {
                    TerminatedWithinBound = terminatedWithinBound,
                    TerminationTimeMs = terminationTimeMs,
                    StepCount = terminationTimeMs + 1,
                    ReceivedRaw = _receivedRaw,
                    ReceivedFrameID = _receivedFrameID,
                    DeliveryCount = _deliveryCount,
                    SenderOutputCount = _senderOutputCount,
                    ReceiverOutputCount = _receiverOutputCount,
                    DroppedDataCount = _droppedDataCount,
                    DroppedAckCount = _droppedAckCount,
                    ReorderedDatagramCount = _reorderedDatagramCount,
                    DuplicateDatagramCount = _duplicateDatagramCount,
                    TraceLines = _traceLines.ToArray(),
                    ReceivedRaws = _receivedRaws.ToArray(),
                    ReceivedFrameIDs = _receivedFrameIDs.ToArray(),
                    NetworkOutOfOrderReleaseCount =
                        _networkOutOfOrderReleaseCount,
                    SenderPendingSendCount = _sender.PendingSendCount
                };
            }
        }

        private sealed class ProtocolRun
        {
            private static readonly RouteCSessionId SessionId =
                new RouteCSessionId(
                    0x1021324354657687UL,
                    0x98A9BACBDCEDFE0FUL);

            private readonly ProtocolScenarioKind _scenarioKind;
            private readonly int _maximumTimeMs;
            private readonly int _maximumSteps;
            private readonly ProtocolClock _clock = new ProtocolClock();
            private readonly UdpVirtualDatagramLink _link;
            private readonly KcpUdpClientStateMachine _machine;
            private readonly List<string> _traceLines = new List<string>();
            private readonly byte[] _initialToken = Sequence(
                0x30,
                RouteCProtocolConstants.ReconnectTokenSize);
            private readonly byte[] _reconnectToken = Sequence(
                0x70,
                RouteCProtocolConstants.ReconnectTokenSize);
            private readonly byte[] _resumeAttemptID = Sequence(
                0xA0,
                RouteCProtocolConstants.NonceSize);
            private int _nonceCount;
            private int _helloSentCount;
            private int _welcomeSentCount;
            private int _readySentCount;
            private int _startSentCount;
            private int _reconnectWelcomeSentCount;
            private int _resumeAcceptedSentCount;
            private int _resumeCompleteSentCount;
            private int _droppedDatagramCount;
            private int _reorderedDatagramCount;
            private int _duplicateDatagramCount;
            private int _nonZeroJitterCount;
            private bool _initialRunningSeen;
            private bool _resumeCycleSeen;
            private bool _resumeReadinessSubmitted;
            private bool _clientObservedResumeRejected;

            public ProtocolRun(
                ProtocolScenarioKind scenarioKind,
                UdpDatagramFaultProfile clientToServerProfile,
                UdpDatagramFaultProfile serverToClientProfile,
                int maximumTimeMs,
                int maximumSteps)
            {
                _scenarioKind = scenarioKind;
                _maximumTimeMs = maximumTimeMs;
                _maximumSteps = maximumSteps;
                _link = new UdpVirtualDatagramLink(
                    clientToServerProfile,
                    serverToClientProfile);
                _machine = new KcpUdpClientStateMachine(
                    _clock,
                    CreateNonce,
                    HandleClientOutput,
                    transportEvent =>
                    {
                        if (transportEvent.Reason ==
                            NetworkTransportEventReason.ResumeRejected)
                        {
                            _clientObservedResumeRejected = true;
                        }
                    });
            }

            public ProtocolResult Execute()
            {
                _machine.Start();
                for (int step = 1;
                     step <= _maximumSteps &&
                     _clock.Milliseconds <= _maximumTimeMs;
                     step++)
                {
                    _machine.Tick();
                    DrainDueDatagrams();
                    DriveResumeProgress();

                    if (_machine.State == NetworkSessionState.Running)
                    {
                        if (_scenarioKind == ProtocolScenarioKind.InitialHandshake)
                        {
                            return BuildResult(
                                ProtocolOutcome.Running,
                                true,
                                step);
                        }
                        if (!_initialRunningSeen)
                        {
                            _initialRunningSeen = true;
                        }
                        else if (_resumeCycleSeen)
                        {
                            return BuildResult(
                                ProtocolOutcome.Running,
                                true,
                                step);
                        }
                    }

                    if (_machine.State == NetworkSessionState.Reconnecting ||
                        _machine.State == NetworkSessionState.Resuming)
                    {
                        _resumeCycleSeen = true;
                    }
                    if (_machine.State == NetworkSessionState.Resuming &&
                        !_resumeReadinessSubmitted)
                    {
                        var readiness = new ResumeReadiness(-1, 0, -1);
                        _machine.SubmitResumeReadiness(in readiness);
                        _resumeReadinessSubmitted = true;
                        DrainDueDatagrams();
                        DriveResumeProgress();
                    }

                    if (_machine.State == NetworkSessionState.Terminated)
                    {
                        return BuildResult(
                            _clientObservedResumeRejected
                                ? ProtocolOutcome.ResumeRejected
                                : ProtocolOutcome.BoundExceeded,
                            _clientObservedResumeRejected,
                            step);
                    }

                    _clock.AdvanceOneMillisecond();
                }

                return BuildResult(
                    ProtocolOutcome.BoundExceeded,
                    false,
                    _maximumSteps);
            }

            private byte[] CreateNonce()
            {
                byte first = _nonceCount++ == 0 ? (byte)0x10 : (byte)0x50;
                return Sequence(first, RouteCProtocolConstants.NonceSize);
            }

            private void HandleClientOutput(RouteCProtocolMessage message)
            {
                if (message.MessageType == RouteCMessageType.Hello)
                    _helloSentCount++;
                else if (message.MessageType == RouteCMessageType.Ready)
                    _readySentCount++;
                else if (message.MessageType == RouteCMessageType.ResumeComplete)
                    _resumeCompleteSentCount++;

                byte[] envelope = RouteCProtocolCodec.Encode(
                    message.MessageType,
                    message.SessionId,
                    message.Generation,
                    message.Payload);
                RecordTrace(_link.Enqueue(
                    _clock.Milliseconds,
                    UdpDatagramDirection.ClientToServer,
                    0,
                    envelope));
            }

            private void HandleServerOutput(
                RouteCMessageType messageType,
                uint generation,
                byte[] payload)
            {
                if (messageType == RouteCMessageType.Welcome)
                {
                    _welcomeSentCount++;
                    if (generation == 2u)
                        _reconnectWelcomeSentCount++;
                }
                else if (messageType == RouteCMessageType.Start)
                {
                    _startSentCount++;
                }
                else if (messageType == RouteCMessageType.ResumeAccepted)
                {
                    _resumeAcceptedSentCount++;
                }
                else if (messageType == RouteCMessageType.ResumeComplete)
                {
                    _resumeCompleteSentCount++;
                }

                byte[] envelope = RouteCProtocolCodec.Encode(
                    messageType,
                    SessionId,
                    generation,
                    payload);
                RecordTrace(_link.Enqueue(
                    _clock.Milliseconds,
                    UdpDatagramDirection.ServerToClient,
                    0,
                    envelope));
            }

            private void RecordTrace(UdpDatagramDecisionTraceEntry trace)
            {
                _traceLines.Add(trace.ToCanonicalJsonLine());
                if (trace.Decision.Dropped)
                    _droppedDatagramCount++;
                if (trace.Decision.ReorderExtraDelayMs > 0)
                    _reorderedDatagramCount++;
                if (trace.Decision.CopyCount == 2)
                    _duplicateDatagramCount++;
                if (trace.Decision.JitterOffsetMs != 0)
                    _nonZeroJitterCount++;
            }

            private void DrainDueDatagrams()
            {
                while (_link.TryDequeueDue(
                    _clock.Milliseconds,
                    out UdpDatagramDirection direction,
                    out _,
                    out byte[] envelope))
                {
                    if (!RouteCProtocolCodec.TryDecode(
                            envelope,
                            envelope.Length,
                            out RouteCProtocolMessage message,
                            out _))
                    {
                        throw new InvalidOperationException(
                            "Protocol scenario received an invalid envelope.");
                    }

                    if (direction == UdpDatagramDirection.ClientToServer)
                        HandleAtScriptedServer(message);
                    else
                        _machine.HandleIncoming(message);
                }
            }

            private void HandleAtScriptedServer(RouteCProtocolMessage message)
            {
                if (message.MessageType == RouteCMessageType.Hello)
                {
                    if (RouteCProtocolCodec.TryDecodeInitialHello(
                            message.Payload,
                            out byte[] initialNonce))
                    {
                        SendWelcome(initialNonce, false, 1u, _initialToken, 77u);
                    }
                    else if (RouteCProtocolCodec.TryDecodeReconnectHello(
                        message.Payload,
                        out byte[] reconnectNonce,
                        out _))
                    {
                        SendWelcome(reconnectNonce, true, 2u, _reconnectToken, 78u);
                    }
                    return;
                }

                if (message.MessageType == RouteCMessageType.Ready)
                {
                    if (message.Generation == 1u)
                    {
                        HandleServerOutput(
                            RouteCMessageType.Start,
                            1u,
                            RouteCProtocolCodec.EncodeStart(0));
                    }
                    else if (_scenarioKind ==
                             ProtocolScenarioKind.ResumeControlRejected)
                    {
                        HandleServerOutput(
                            RouteCMessageType.ResumeRejected,
                            2u,
                            RouteCProtocolCodec.EncodeResumeRejected(
                                new byte[RouteCProtocolConstants.NonceSize],
                                (ushort)ResumeRejectedReason.UnsafeResume));
                    }
                    else
                    {
                        HandleServerOutput(
                            RouteCMessageType.ResumeAccepted,
                            2u,
                            RouteCProtocolCodec.EncodeResumeAccepted(
                                _resumeAttemptID,
                                0,
                                -1,
                                0,
                                -1,
                                0,
                                -1));
                    }
                    return;
                }

                if (message.MessageType == RouteCMessageType.ResumeComplete)
                {
                    HandleServerOutput(
                        RouteCMessageType.ResumeComplete,
                        2u,
                        message.Payload);
                }
            }

            private void SendWelcome(
                byte[] echoNonce,
                bool resumeRequired,
                uint generation,
                byte[] reconnectToken,
                uint conversation)
            {
                HandleServerOutput(
                    RouteCMessageType.Welcome,
                    generation,
                    RouteCProtocolCodec.EncodeWelcome(
                        echoNonce,
                        0,
                        conversation,
                        reconnectToken,
                        RouteCProtocolConstants.HeartbeatSilenceMs,
                        RouteCProtocolConstants.DisconnectTimeoutMs,
                        resumeRequired));
            }

            private void DriveResumeProgress()
            {
                if (!_machine.TryTakeResumePlan(out KcpClientResumePlan plan))
                    return;

                _machine.SubmitResumeProgress(
                    plan.RequiredUploadedThrough,
                    plan.ExpectedRemoteThrough);
                DrainDueDatagrams();
            }

            private ProtocolResult BuildResult(
                ProtocolOutcome outcome,
                bool terminatedWithinBound,
                int stepCount)
            {
                return new ProtocolResult
                {
                    Outcome = outcome,
                    FinalState = _machine.State,
                    TerminatedWithinBound = terminatedWithinBound,
                    TerminationTimeMs = (int)_clock.Milliseconds,
                    StepCount = stepCount,
                    HelloSentCount = _helloSentCount,
                    WelcomeSentCount = _welcomeSentCount,
                    ReadySentCount = _readySentCount,
                    StartSentCount = _startSentCount,
                    ReconnectWelcomeSentCount = _reconnectWelcomeSentCount,
                    ResumeAcceptedSentCount = _resumeAcceptedSentCount,
                    ResumeCompleteSentCount = _resumeCompleteSentCount,
                    DroppedDatagramCount = _droppedDatagramCount,
                    ReorderedDatagramCount = _reorderedDatagramCount,
                    DuplicateDatagramCount = _duplicateDatagramCount,
                    NonZeroJitterCount = _nonZeroJitterCount,
                    TraceLines = _traceLines.ToArray(),
                    ClientObservedResumeRejected =
                        _clientObservedResumeRejected
                };
            }
        }

        private sealed class ProtocolClock : IMonotonicClock
        {
            public uint Milliseconds { get; private set; }

            public long Timestamp => Milliseconds;

            public void AdvanceOneMillisecond()
            {
                Milliseconds = unchecked(Milliseconds + 1u);
            }
        }

        private static byte[] Sequence(byte first, int count)
        {
            var result = new byte[count];
            for (int index = 0; index < result.Length; index++)
                result[index] = unchecked((byte)(first + index));
            return result;
        }
    }
}

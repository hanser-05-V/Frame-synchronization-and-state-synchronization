using System;
using System.Collections.Generic;

namespace FrameSyncDemo
{
    public sealed class FrameInputLedger
    {
        public enum InputState
        {
            Missing,
            Predicted,
            Actual
        }

        public enum ActualDisposition
        {
            Accepted,
            PredictionMatched,
            PredictionMismatched,
            IdempotentDuplicate,
            ConflictingDuplicate,
            HistoryUnavailable,
            CapacityExceeded
        }

        public readonly struct ActualArrival
        {
            public ActualArrival(ActualDisposition disposition, int canonicalFrame, int playerIndex)
            {
                Disposition = disposition;
                CanonicalFrame = canonicalFrame;
                PlayerIndex = playerIndex;
            }

            public ActualDisposition Disposition { get; }
            public int CanonicalFrame { get; }
            public int PlayerIndex { get; }
        }

        public readonly struct InputRecord
        {
            public InputRecord(FrameInput value, InputState state)
            {
                Value = value;
                State = state;
            }

            public FrameInput Value { get; }
            public InputState State { get; }
        }

        public readonly struct ResolvedFrame
        {
            private readonly InputRecord _playerZero;
            private readonly InputRecord _playerOne;

            public ResolvedFrame(InputRecord playerZero, InputRecord playerOne)
            {
                _playerZero = playerZero;
                _playerOne = playerOne;
            }

            public InputRecord GetPlayer(int playerIndex)
            {
                if (playerIndex == 0)
                {
                    return _playerZero;
                }

                if (playerIndex == 1)
                {
                    return _playerOne;
                }

                throw new ArgumentOutOfRangeException(nameof(playerIndex));
            }
        }

        public readonly struct InputMismatch
        {
            public InputMismatch(int frame, int playerIndex, uint predictedRaw, uint actualRaw, long generation)
            {
                Frame = frame;
                PlayerIndex = playerIndex;
                PredictedRaw = predictedRaw;
                ActualRaw = actualRaw;
                Generation = generation;
            }

            public int Frame { get; }
            public int PlayerIndex { get; }
            public uint PredictedRaw { get; }
            public uint ActualRaw { get; }
            public long Generation { get; }
        }

        public enum ReplayPlanResult
        {
            Success,
            HistoryUnavailable,
            CapacityExceeded,
            MissingInput,
            IntegrityFault
        }

        public enum PruneResult
        {
            Success,
            NoOp,
            HistoryUnavailable,
            CapacityExceeded,
            PendingMismatch,
            InFlightPlan,
            IntegrityFault,
            UnconfirmedFrame
        }

        public readonly struct ReplayPlanFrame
        {
            public ReplayPlanFrame(int canonicalFrame, ResolvedFrame inputs)
            {
                CanonicalFrame = canonicalFrame;
                Inputs = inputs;
            }

            public int CanonicalFrame { get; }
            public ResolvedFrame Inputs { get; }

            public InputRecord GetPlayer(int playerIndex)
            {
                return Inputs.GetPlayer(playerIndex);
            }
        }

        public sealed class ReplayInputPlan
        {
            private readonly ReplayPlanFrame[] _frames;
            private readonly InputMismatch[] _capturedMismatches;

            internal ReplayInputPlan(
                long generation,
                int fromFrame,
                int throughFrame,
                ReplayPlanFrame[] frames,
                InputMismatch[] capturedMismatches)
            {
                Generation = generation;
                FromFrame = fromFrame;
                ThroughFrame = throughFrame;
                _frames = frames;
                _capturedMismatches = capturedMismatches;
            }

            public long Generation { get; }
            public int FromFrame { get; }
            public int ThroughFrame { get; }
            public int FrameCount => _frames == null ? 0 : _frames.Length;
            internal int CapturedMismatchCount => _capturedMismatches == null ? 0 : _capturedMismatches.Length;

            public ReplayPlanFrame GetFrame(int index)
            {
                if (_frames == null || index < 0 || index >= _frames.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return _frames[index];
            }

            internal InputMismatch GetCapturedMismatch(int index)
            {
                if (_capturedMismatches == null || index < 0 || index >= _capturedMismatches.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return _capturedMismatches[index];
            }
        }

        private const int PlayerCount = 2;
        private const int HistoryCapacity = 256;

        private readonly SortedDictionary<int, FrameSlots> _frames = new SortedDictionary<int, FrameSlots>();
        private readonly SortedDictionary<MismatchKey, InputMismatch> _mismatches =
            new SortedDictionary<MismatchKey, InputMismatch>();
        private readonly PredictionSeed[] _prunedSeeds = new PredictionSeed[PlayerCount];
        private int _confirmedThroughFrame = -1;
        private int _firstRetainedFrame;
        private int _lastObservedFrame = -1;
        private long _nextGeneration;
        private long _inFlightGeneration = -1;
        private ReplayInputPlan _inFlightPlan;
        private bool _hasIntegrityFault;

        public int StartFrame => 0;
        public int ConfirmedThroughFrame => _confirmedThroughFrame;
        public int FirstRetainedFrame => _firstRetainedFrame;
        public int LastObservedFrame => _lastObservedFrame;
        public bool HasIntegrityFault => _hasIntegrityFault;

        public ActualArrival RecordActual(int canonicalFrame, int playerIndex, FrameInput input)
        {
            if (!IsValidFrame(canonicalFrame) || !IsValidPlayer(playerIndex) || canonicalFrame < _firstRetainedFrame)
            {
                return new ActualArrival(ActualDisposition.HistoryUnavailable, canonicalFrame, playerIndex);
            }

            if (!CanAddressFrame(canonicalFrame))
            {
                return new ActualArrival(ActualDisposition.CapacityExceeded, canonicalFrame, playerIndex);
            }

            _lastObservedFrame = Math.Max(_lastObservedFrame, canonicalFrame);
            FrameSlots slots = GetOrCreateFrame(canonicalFrame);
            InputRecord current = slots.Get(playerIndex);
            ActualDisposition disposition;

            if (current.State == InputState.Missing)
            {
                slots.Set(playerIndex, new InputRecord(input, InputState.Actual));
                disposition = ActualDisposition.Accepted;
            }
            else if (current.State == InputState.Predicted)
            {
                slots.Set(playerIndex, new InputRecord(input, InputState.Actual));
                if (current.Value._raw == input._raw)
                {
                    disposition = ActualDisposition.PredictionMatched;
                }
                else
                {
                    disposition = ActualDisposition.PredictionMismatched;
                    _mismatches[new MismatchKey(canonicalFrame, playerIndex)] =
                        new InputMismatch(canonicalFrame, playerIndex, current.Value._raw, input._raw, _nextGeneration);
                }
            }
            else if (current.Value._raw == input._raw)
            {
                disposition = ActualDisposition.IdempotentDuplicate;
            }
            else
            {
                _hasIntegrityFault = true;
                disposition = ActualDisposition.ConflictingDuplicate;
            }

            AdvanceConfirmationHead();
            return new ActualArrival(disposition, canonicalFrame, playerIndex);
        }

        public ReplayPlanResult TryBuildReplayPlan(int fromFrame, int throughFrame, out ReplayInputPlan plan)
        {
            return TryBuildReplayPlan(
                fromFrame,
                throughFrame,
                fromFrame,
                out plan);
        }

        public ReplayPlanResult TryBuildReplayPlan(
            int fromFrame,
            int throughFrame,
            int mismatchFromFrame,
            out ReplayInputPlan plan)
        {
            plan = null;
            if (_hasIntegrityFault)
            {
                return ReplayPlanResult.IntegrityFault;
            }

            bool isEmptySuffix = (long)throughFrame + 1 == fromFrame;
            if (mismatchFromFrame < _firstRetainedFrame ||
                throughFrame < _firstRetainedFrame ||
                (!isEmptySuffix &&
                    (fromFrame < _firstRetainedFrame || throughFrame < fromFrame)))
            {
                return ReplayPlanResult.HistoryUnavailable;
            }

            long count = isEmptySuffix
                ? 0
                : (long)throughFrame - fromFrame + 1;
            if (count > HistoryCapacity || !CanAddressFrame(throughFrame))
            {
                return ReplayPlanResult.CapacityExceeded;
            }

            var frames = new ReplayPlanFrame[(int)count];
            FrameInput[] seeds =
            {
                FindPrecedingActual(fromFrame, 0),
                FindPrecedingActual(fromFrame, 1)
            };

            for (int index = 0; index < count; index++)
            {
                int frame = checked((int)((long)fromFrame + index));
                if (!_frames.TryGetValue(frame, out FrameSlots slots))
                {
                    return ReplayPlanResult.MissingInput;
                }

                InputRecord[] records = new InputRecord[PlayerCount];
                for (int playerIndex = 0; playerIndex < PlayerCount; playerIndex++)
                {
                    InputRecord record = slots.Get(playerIndex);
                    if (record.State == InputState.Missing)
                    {
                        return ReplayPlanResult.MissingInput;
                    }

                    if (record.State == InputState.Actual)
                    {
                        records[playerIndex] = record;
                        seeds[playerIndex] = record.Value;
                    }
                    else
                    {
                        records[playerIndex] = new InputRecord(
                            seeds[playerIndex].ToPredictionInput(),
                            InputState.Predicted);
                    }
                }

                frames[index] = new ReplayPlanFrame(
                    frame,
                    new ResolvedFrame(records[0], records[1]));
            }

            var capturedMismatches = new List<InputMismatch>();
            foreach (InputMismatch mismatch in _mismatches.Values)
            {
                if (mismatch.Frame >= mismatchFromFrame &&
                    mismatch.Frame <= throughFrame)
                {
                    capturedMismatches.Add(mismatch);
                }
            }
            long generation = _nextGeneration++;
            plan = new ReplayInputPlan(generation, fromFrame, throughFrame, frames, capturedMismatches.ToArray());
            _inFlightGeneration = generation;
            _inFlightPlan = plan;
            return ReplayPlanResult.Success;
        }

        public void CommitReplay(in ReplayInputPlan plan)
        {
            if (plan == null || !ReferenceEquals(plan, _inFlightPlan) || plan.Generation != _inFlightGeneration)
            {
                return;
            }

            for (int index = 0; index < plan.FrameCount; index++)
            {
                ReplayPlanFrame frame = plan.GetFrame(index);
                if (!_frames.TryGetValue(frame.CanonicalFrame, out FrameSlots slots))
                {
                    ReleaseInFlightPlan(plan);
                    return;
                }

                for (int playerIndex = 0; playerIndex < PlayerCount; playerIndex++)
                {
                    InputRecord replacement = frame.GetPlayer(playerIndex);
                    if (replacement.State == InputState.Predicted)
                    {
                        InputRecord current = slots.Get(playerIndex);
                        if (current.State != InputState.Predicted)
                        {
                            ReleaseInFlightPlan(plan);
                            return;
                        }
                    }
                }
            }

            for (int index = 0; index < plan.FrameCount; index++)
            {
                ReplayPlanFrame frame = plan.GetFrame(index);
                FrameSlots slots = _frames[frame.CanonicalFrame];
                for (int playerIndex = 0; playerIndex < PlayerCount; playerIndex++)
                {
                    InputRecord replacement = frame.GetPlayer(playerIndex);
                    if (replacement.State == InputState.Predicted)
                    {
                        slots.Set(playerIndex, replacement);
                    }
                }
            }

            for (int index = 0; index < plan.CapturedMismatchCount; index++)
            {
                InputMismatch captured = plan.GetCapturedMismatch(index);
                var key = new MismatchKey(captured.Frame, captured.PlayerIndex);
                if (_mismatches.TryGetValue(key, out InputMismatch current) &&
                    current.Generation == captured.Generation &&
                    current.PredictedRaw == captured.PredictedRaw &&
                    current.ActualRaw == captured.ActualRaw)
                {
                    _mismatches.Remove(key);
                }
            }

            ReleaseInFlightPlan(plan);
        }

        public bool IsCurrentReplayPlan(ReplayInputPlan plan)
        {
            return plan != null && ReferenceEquals(plan, _inFlightPlan) &&
                plan.Generation == _inFlightGeneration && !_hasIntegrityFault;
        }

        public bool TryGetInFlightReplayPlan(out ReplayInputPlan plan)
        {
            plan = _inFlightPlan;
            return plan != null;
        }

        public PruneResult TryPruneBefore(int firstFrameToKeep)
        {
            if (_hasIntegrityFault)
            {
                return PruneResult.IntegrityFault;
            }

            if (_inFlightPlan != null)
            {
                return PruneResult.InFlightPlan;
            }

            if (_mismatches.Count != 0)
            {
                return PruneResult.PendingMismatch;
            }

            if (firstFrameToKeep < _firstRetainedFrame)
            {
                return PruneResult.HistoryUnavailable;
            }

            if (firstFrameToKeep == _firstRetainedFrame)
            {
                return PruneResult.NoOp;
            }

            if (firstFrameToKeep - 1 > _confirmedThroughFrame)
            {
                return PruneResult.UnconfirmedFrame;
            }

            for (int playerIndex = 0; playerIndex < PlayerCount; playerIndex++)
            {
                _prunedSeeds[playerIndex] = new PredictionSeed(FindPrecedingActual(firstFrameToKeep, playerIndex));
            }

            var discardedFrames = new List<int>();
            foreach (int frame in _frames.Keys)
            {
                if (frame < firstFrameToKeep)
                {
                    discardedFrames.Add(frame);
                }
                else
                {
                    break;
                }
            }

            foreach (int frame in discardedFrames)
            {
                _frames.Remove(frame);
            }

            _firstRetainedFrame = firstFrameToKeep;
            return PruneResult.Success;
        }

        public ResolvedFrame ResolveForSimulation(int canonicalFrame)
        {
            if (!IsValidFrame(canonicalFrame))
            {
                throw new ArgumentOutOfRangeException(nameof(canonicalFrame));
            }

            if (canonicalFrame < _firstRetainedFrame)
            {
                throw new InvalidOperationException("Requested frame is no longer retained by the ledger.");
            }

            if (!CanAddressFrame(canonicalFrame))
            {
                throw new InvalidOperationException("Requested frame exceeds the ledger history capacity.");
            }

            if (_hasIntegrityFault)
            {
                throw new InvalidOperationException("Ledger integrity fault prevents simulation input resolution.");
            }

            _lastObservedFrame = Math.Max(_lastObservedFrame, canonicalFrame);
            FrameSlots slots = GetOrCreateFrame(canonicalFrame);
            for (int playerIndex = 0; playerIndex < PlayerCount; playerIndex++)
            {
                if (slots.Get(playerIndex).State == InputState.Missing)
                {
                    slots.Set(playerIndex, new InputRecord(
                        FindPrecedingActual(canonicalFrame, playerIndex).ToPredictionInput(),
                        InputState.Predicted));
                }
            }

            return slots.ToResolvedFrame();
        }

        /// <summary>
        /// Returns true when the frame entry exists and its requested slot can be queried.
        /// A true result may still contain <see cref="InputState.Missing"/>; inspect
        /// <see cref="InputRecord.State"/> to distinguish slot state.
        /// </summary>
        public bool TryGetRecord(int canonicalFrame, int playerIndex, out InputRecord record)
        {
            if (!IsValidFrame(canonicalFrame) || canonicalFrame < _firstRetainedFrame || !IsValidPlayer(playerIndex) ||
                !_frames.TryGetValue(canonicalFrame, out FrameSlots slots))
            {
                record = default;
                return false;
            }

            record = slots.Get(playerIndex);
            return true;
        }

        public bool TryGetActualFrame(int canonicalFrame, out ResolvedFrame inputs)
        {
            if (_hasIntegrityFault || !_frames.TryGetValue(canonicalFrame, out FrameSlots slots) ||
                slots.Get(0).State != InputState.Actual || slots.Get(1).State != InputState.Actual)
            {
                inputs = default;
                return false;
            }

            inputs = slots.ToResolvedFrame();
            return true;
        }

        public bool TryGetEarliestMismatch(out InputMismatch mismatch)
        {
            if (_hasIntegrityFault)
            {
                mismatch = default;
                return false;
            }

            foreach (KeyValuePair<MismatchKey, InputMismatch> entry in _mismatches)
            {
                mismatch = entry.Value;
                return true;
            }

            mismatch = default;
            return false;
        }

        private static bool IsValidFrame(int canonicalFrame)
        {
            return canonicalFrame >= 0;
        }

        private static bool IsValidPlayer(int playerIndex)
        {
            return playerIndex >= 0 && playerIndex < PlayerCount;
        }

        private FrameSlots GetOrCreateFrame(int canonicalFrame)
        {
            if (!_frames.TryGetValue(canonicalFrame, out FrameSlots slots))
            {
                slots = new FrameSlots();
                _frames.Add(canonicalFrame, slots);
            }

            return slots;
        }

        private FrameInput FindPrecedingActual(int canonicalFrame, int playerIndex)
        {
            FrameInput latest = _prunedSeeds[playerIndex].Value;
            foreach (KeyValuePair<int, FrameSlots> entry in _frames)
            {
                if (entry.Key >= canonicalFrame)
                {
                    break;
                }

                InputRecord candidate = entry.Value.Get(playerIndex);
                if (candidate.State == InputState.Actual)
                {
                    latest = candidate.Value;
                }
            }

            return latest;
        }

        private bool CanAddressFrame(int canonicalFrame)
        {
            return canonicalFrame >= _firstRetainedFrame &&
                (long)canonicalFrame - _firstRetainedFrame < HistoryCapacity;
        }

        private void AdvanceConfirmationHead()
        {
            if (_hasIntegrityFault)
            {
                return;
            }

            if (_confirmedThroughFrame == int.MaxValue)
            {
                return;
            }

            int candidateFrame = _confirmedThroughFrame + 1;
            while (_frames.TryGetValue(candidateFrame, out FrameSlots slots) &&
                slots.Get(0).State == InputState.Actual && slots.Get(1).State == InputState.Actual)
            {
                _confirmedThroughFrame = candidateFrame;
                if (candidateFrame == int.MaxValue)
                {
                    return;
                }

                candidateFrame++;
            }
        }

        private void ReleaseInFlightPlan(ReplayInputPlan plan)
        {
            if (ReferenceEquals(plan, _inFlightPlan) && plan.Generation == _inFlightGeneration)
            {
                _inFlightGeneration = -1;
                _inFlightPlan = null;
            }
        }

        private sealed class FrameSlots
        {
            private InputRecord _playerZero = new InputRecord(default, InputState.Missing);
            private InputRecord _playerOne = new InputRecord(default, InputState.Missing);

            public InputRecord Get(int playerIndex)
            {
                return playerIndex == 0 ? _playerZero : _playerOne;
            }

            public void Set(int playerIndex, InputRecord record)
            {
                if (playerIndex == 0)
                {
                    _playerZero = record;
                }
                else
                {
                    _playerOne = record;
                }
            }

            public ResolvedFrame ToResolvedFrame()
            {
                return new ResolvedFrame(_playerZero, _playerOne);
            }
        }

        private readonly struct PredictionSeed
        {
            public PredictionSeed(FrameInput value)
            {
                Value = value;
            }

            public FrameInput Value { get; }
        }

        private readonly struct MismatchKey : IComparable<MismatchKey>
        {
            public MismatchKey(int frame, int playerIndex)
            {
                Frame = frame;
                PlayerIndex = playerIndex;
            }

            public int Frame { get; }
            public int PlayerIndex { get; }

            public int CompareTo(MismatchKey other)
            {
                int frameComparison = Frame.CompareTo(other.Frame);
                return frameComparison != 0
                    ? frameComparison
                    : PlayerIndex.CompareTo(other.PlayerIndex);
            }
        }
    }
}

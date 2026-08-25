using System;

namespace FrameSyncDemo
{
    public sealed class FrameSyncCoordinator
    {
        private readonly FrameInputLedger _ledger;
        private readonly DeterministicWorld _confirmedWorld;
        private readonly DeterministicWorld _predictedWorld;
        private readonly WorldSnapshotStore _confirmedSnapshots;
        private readonly WorldSnapshotStore _predictedSnapshots;
        private int _confirmedFrame = -1;
        private int _predictedFrame = -1;

        public FrameSyncCoordinator(
            DeterministicWorld predictedWorld,
            in SimulationWorldState initialWorld,
            FixedInt moveDistance,
            FixedInt deltaTime,
            int snapshotCapacity = 512)
        {
            if (predictedWorld == null)
            {
                throw new ArgumentNullException(nameof(predictedWorld));
            }

            _ledger = new FrameInputLedger();
            _predictedWorld = predictedWorld;
            _predictedWorld.Restore(initialWorld);
            _confirmedWorld = DeterministicWorld.CreateIsolated(
                initialWorld,
                moveDistance,
                deltaTime);
            _confirmedSnapshots = new WorldSnapshotStore(
                initialWorld,
                snapshotCapacity);
            _predictedSnapshots = new WorldSnapshotStore(
                initialWorld,
                snapshotCapacity);
        }

        public int ConfirmedFrame => _confirmedFrame;
        public int PredictedFrame => _predictedFrame;
        public int StartFrame => _ledger.StartFrame;
        public int ConfirmedThroughFrame => _ledger.ConfirmedThroughFrame;
        public int FirstRetainedFrame => _ledger.FirstRetainedFrame;
        public int EarliestRecoverableCanonicalFrame
        {
            get
            {
                int ledgerFloor = FirstRetainedFrame;
                int snapshotFloor =
                    _predictedSnapshots.EarliestReadableFrame < 0
                        ? StartFrame
                        : _predictedSnapshots.EarliestReadableFrame + 1;
                return Math.Max(ledgerFloor, snapshotFloor);
            }
        }
        public bool HasIntegrityFault => _ledger.HasIntegrityFault;
        public SimulationWorldState InitialWorld =>
            _confirmedSnapshots.InitialWorld;
        public SimulationWorldState ConfirmedWorld =>
            _confirmedWorld.Capture(_confirmedFrame);
        public SimulationWorldState PredictedWorld =>
            _predictedWorld.Capture(_predictedFrame);

        public FrameInputLedger.ActualArrival RecordActual(
            int canonicalFrame,
            int playerIndex,
            FrameInput input)
        {
            return _ledger.RecordActual(canonicalFrame, playerIndex, input);
        }

        public FrameInputLedger.ResolvedFrame ResolveForPrediction(
            int canonicalFrame)
        {
            return _ledger.ResolveForSimulation(canonicalFrame);
        }

        public FrameAdvanceResult Advance(
            int canonicalFrame,
            FrameInputLedger.ResolvedFrame inputs)
        {
            if (_ledger.HasIntegrityFault ||
                canonicalFrame != _predictedFrame + 1)
            {
                return new FrameAdvanceResult(false);
            }

            FrameSimulationResult simulationResult =
                _predictedWorld.Step(ToInputs(inputs));
            _predictedFrame = canonicalFrame;
            _predictedSnapshots.Store(_predictedWorld.Capture(canonicalFrame));
            FrameAdvanceResult confirmed = CatchUpConfirmed();
            return new FrameAdvanceResult(
                confirmed.Succeeded,
                simulationResult);
        }

        public FrameAdvanceResult CatchUpConfirmed()
        {
            int targetFrame = Math.Min(
                _ledger.ConfirmedThroughFrame,
                _predictedFrame);
            while (_confirmedFrame < targetFrame)
            {
                int nextFrame = _confirmedFrame + 1;
                if (!_ledger.TryGetActualFrame(
                        nextFrame,
                        out FrameInputLedger.ResolvedFrame actualInputs))
                {
                    return new FrameAdvanceResult(false);
                }

                _confirmedWorld.Step(ToInputs(actualInputs));
                _confirmedFrame = nextFrame;
                _confirmedSnapshots.Store(
                    _confirmedWorld.Capture(nextFrame));
            }

            return new FrameAdvanceResult(true);
        }

        public ReconcileResult Reconcile()
        {
            if (_ledger.HasIntegrityFault)
            {
                return new ReconcileResult(
                    false,
                    false,
                    -1,
                    _confirmedFrame,
                    0,
                    FrameInputLedger.ReplayPlanResult.IntegrityFault,
                    FrameReplayResult.Failure.None);
            }

            if (!_ledger.TryGetEarliestMismatch(
                    out FrameInputLedger.InputMismatch mismatch))
            {
                FrameAdvanceResult catchupWithoutMismatch = CatchUpConfirmed();
                return new ReconcileResult(
                    catchupWithoutMismatch.Succeeded,
                    false,
                    -1,
                    _confirmedFrame,
                    0,
                    catchupWithoutMismatch.Succeeded
                        ? FrameInputLedger.ReplayPlanResult.Success
                        : FrameInputLedger.ReplayPlanResult.MissingInput,
                    FrameReplayResult.Failure.None);
            }

            int restoredFrame = Math.Min(
                _ledger.ConfirmedThroughFrame,
                _predictedFrame);
            for (int frame = _confirmedFrame + 1;
                frame <= restoredFrame;
                frame++)
            {
                if (!_ledger.TryGetActualFrame(frame, out _))
                {
                    return new ReconcileResult(
                        false,
                        false,
                        mismatch.Frame,
                        _confirmedFrame,
                        0,
                        FrameInputLedger.ReplayPlanResult.MissingInput,
                        FrameReplayResult.Failure.None);
                }
            }

            int replayFromFrame = restoredFrame + 1;
            FrameInputLedger.ReplayPlanResult planResult =
                _ledger.TryBuildReplayPlan(
                    replayFromFrame,
                    _predictedFrame,
                    mismatch.Frame,
                    out FrameInputLedger.ReplayInputPlan plan);
            if (planResult != FrameInputLedger.ReplayPlanResult.Success)
            {
                return new ReconcileResult(
                    false,
                    false,
                    mismatch.Frame,
                    restoredFrame,
                    0,
                    planResult,
                    FrameReplayResult.Failure.None);
            }

            FrameAdvanceResult catchup = CatchUpConfirmed();
            if (!catchup.Succeeded || _confirmedFrame != restoredFrame)
            {
                return new ReconcileResult(
                    false,
                    false,
                    mismatch.Frame,
                    _confirmedFrame,
                    0,
                    FrameInputLedger.ReplayPlanResult.MissingInput,
                    FrameReplayResult.Failure.None);
            }

            FrameReplayResult replay = FrameReplaySystem.Replay(
                plan,
                _ledger,
                _confirmedSnapshots,
                _predictedSnapshots,
                _predictedWorld,
                restoredFrame);
            if (replay.succeeded)
            {
                _predictedSnapshots.Store(
                    _predictedWorld.Capture(_predictedFrame));
            }
            return new ReconcileResult(
                replay.succeeded,
                replay.succeeded,
                mismatch.Frame,
                replay.restoredFrame,
                replay.replayedFrameCount,
                planResult,
                replay.failure);
        }

        public bool TryGetSnapshot(
            WorldTrack track,
            int canonicalFrame,
            out SimulationWorldState world)
        {
            WorldSnapshotStore snapshots = track == WorldTrack.Confirmed
                ? _confirmedSnapshots
                : _predictedSnapshots;
            if (!snapshots.TryGet(canonicalFrame, out FrameSnapshot snapshot))
            {
                world = default;
                return false;
            }

            world = snapshot.ToWorld();
            return true;
        }

        public bool TryGetFrameSnapshot(
            WorldTrack track,
            int canonicalFrame,
            out FrameSnapshot snapshot)
        {
            WorldSnapshotStore snapshots = track == WorldTrack.Confirmed
                ? _confirmedSnapshots
                : _predictedSnapshots;
            return snapshots.TryGet(canonicalFrame, out snapshot);
        }

        public bool TryGetEarliestMismatch(
            out FrameInputLedger.InputMismatch mismatch)
        {
            return _ledger.TryGetEarliestMismatch(out mismatch);
        }

        public bool TryGetActualFrame(
            int canonicalFrame,
            out FrameInputLedger.ResolvedFrame inputs)
        {
            return _ledger.TryGetActualFrame(canonicalFrame, out inputs);
        }

        public bool TryGetInFlightReplayPlan(
            out FrameInputLedger.ReplayInputPlan plan)
        {
            return _ledger.TryGetInFlightReplayPlan(out plan);
        }

        public FrameInputLedger.PruneResult TryPruneBefore(
            int firstFrameToKeep)
        {
            return _ledger.TryPruneBefore(firstFrameToKeep);
        }

        public void SetMoveDistance(FixedInt moveDistance)
        {
            _confirmedWorld.SetMoveDistance(moveDistance);
            _predictedWorld.SetMoveDistance(moveDistance);
        }

        public bool TryRestorePredictedFromTrack(
            WorldTrack sourceTrack,
            int canonicalFrame)
        {
            WorldSnapshotStore source = sourceTrack == WorldTrack.Confirmed
                ? _confirmedSnapshots
                : _predictedSnapshots;
            if (!source.TryRestore(canonicalFrame, _predictedWorld))
            {
                return false;
            }

            _predictedFrame = canonicalFrame;
            _predictedSnapshots.TruncateAfter(canonicalFrame);
            _predictedSnapshots.Store(
                _predictedWorld.Capture(canonicalFrame));
            return true;
        }

        private static FrameInput[] ToInputs(
            FrameInputLedger.ResolvedFrame inputs)
        {
            return new[]
            {
                inputs.GetPlayer(0).Value,
                inputs.GetPlayer(1).Value
            };
        }
    }
}

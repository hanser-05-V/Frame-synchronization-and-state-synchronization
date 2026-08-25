using System;

namespace FrameSyncDemo
{
    /// <summary>
    /// Restores an exact prior snapshot and replays only immutable ledger plans.
    /// </summary>
    public static class FrameReplaySystem
    {
        public static FrameReplayResult Replay(
            FrameInputLedger.ReplayInputPlan plan,
            FrameInputLedger ledger,
            PredictionSystem predictionSystem,
            DeterministicWorld world,
            Action resetWorld)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            if (predictionSystem == null)
            {
                throw new ArgumentNullException(nameof(predictionSystem));
            }

            if (ledger == null)
            {
                throw new ArgumentNullException(nameof(ledger));
            }

            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (!ledger.IsCurrentReplayPlan(plan))
            {
                return new FrameReplayResult(false, -1, 0, -1, FrameReplayResult.Failure.StalePlan);
            }

            int restoredFrame = plan.FromFrame == ledger.StartFrame
                ? -1
                : plan.FromFrame - 1;
            bool needsReset = plan.FromFrame == ledger.StartFrame;
            bool hasExactPriorSnapshot = !needsReset &&
                predictionSystem.TryGetWorldSnapshot(restoredFrame, out _);
            if ((!needsReset && !hasExactPriorSnapshot) || (needsReset && resetWorld == null))
            {
                return new FrameReplayResult(false, -1, 0, -1, FrameReplayResult.Failure.MissingSnapshot);
            }

            if (!needsReset && !predictionSystem.TryGetWorldSnapshot(restoredFrame, out _))
            {
                return new FrameReplayResult(
                    false,
                    restoredFrame,
                    0,
                    -1,
                    FrameReplayResult.Failure.MissingSnapshot);
            }

            if (needsReset)
            {
                resetWorld();
            }
            else if (!predictionSystem.RestoreWorldSnapshot(restoredFrame, world))
            {
                return new FrameReplayResult(
                    false,
                    restoredFrame,
                    0,
                    -1,
                    FrameReplayResult.Failure.MissingSnapshot);
            }

            for (int index = 0; index < plan.FrameCount; index++)
            {
                FrameInputLedger.ReplayPlanFrame frame = plan.GetFrame(index);
                world.Step(new[]
                {
                    frame.GetPlayer(0).Value,
                    frame.GetPlayer(1).Value
                });
                predictionSystem.TakeWorldSnapshot(frame.CanonicalFrame, world);
            }

            ledger.CommitReplay(in plan);
            return new FrameReplayResult(true, restoredFrame, plan.FrameCount, -1);
        }

        public static FrameReplayResult Replay(
            FrameInputLedger.ReplayInputPlan plan,
            FrameInputLedger ledger,
            WorldSnapshotStore restoreSource,
            WorldSnapshotStore replayDestination,
            DeterministicWorld world,
            int restoredFrame)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }
            if (ledger == null)
            {
                throw new ArgumentNullException(nameof(ledger));
            }
            if (restoreSource == null)
            {
                throw new ArgumentNullException(nameof(restoreSource));
            }
            if (replayDestination == null)
            {
                throw new ArgumentNullException(nameof(replayDestination));
            }
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }
            if (!ledger.IsCurrentReplayPlan(plan))
            {
                return new FrameReplayResult(
                    false,
                    restoredFrame,
                    0,
                    -1,
                    FrameReplayResult.Failure.StalePlan);
            }
            if (plan.FromFrame != restoredFrame + 1)
            {
                return new FrameReplayResult(
                    false,
                    restoredFrame,
                    0,
                    -1,
                    FrameReplayResult.Failure.MissingSnapshot);
            }

            if (restoredFrame < 0)
            {
                restoreSource.RestoreInitial(world);
            }
            else if (!restoreSource.TryRestore(restoredFrame, world))
            {
                return new FrameReplayResult(
                    false,
                    restoredFrame,
                    0,
                    -1,
                    FrameReplayResult.Failure.MissingSnapshot);
            }

            for (int index = 0; index < plan.FrameCount; index++)
            {
                FrameInputLedger.ReplayPlanFrame frame = plan.GetFrame(index);
                world.Step(new[]
                {
                    frame.GetPlayer(0).Value,
                    frame.GetPlayer(1).Value
                });
                replayDestination.Store(
                    world.Capture(frame.CanonicalFrame));
            }

            ledger.CommitReplay(in plan);
            return new FrameReplayResult(
                true,
                restoredFrame,
                plan.FrameCount,
                -1);
        }
    }
}

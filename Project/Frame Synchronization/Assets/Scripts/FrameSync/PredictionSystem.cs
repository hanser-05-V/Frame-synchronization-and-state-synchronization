using System;

namespace FrameSyncDemo
{
    /// <summary>
    /// Stores and restores deterministic world snapshots.
    /// </summary>
    public class PredictionSystem
    {
        private SnapshotBuffer _snapshotBuffer;

        public void Init(int capacity = 512)
        {
            _snapshotBuffer = new SnapshotBuffer(capacity);
        }

        public FrameSnapshot TakeWorldSnapshot(int frameID, PlayerEntity[] players, BallEntity ball)
        {
            SimulationWorldState world = WorldStateCodec.Capture(frameID, players, ball);
            return StoreWorldSnapshot(world);
        }

        public FrameSnapshot TakeWorldSnapshot(int frameID, DeterministicWorld deterministicWorld)
        {
            if (deterministicWorld == null)
            {
                throw new ArgumentNullException(nameof(deterministicWorld));
            }

            SimulationWorldState world = deterministicWorld.Capture(frameID);
            return StoreWorldSnapshot(world);
        }

        public bool TryGetWorldSnapshot(int frameID, out FrameSnapshot snapshot)
        {
            if (!_snapshotBuffer.HasSnapshot(frameID))
            {
                snapshot = new FrameSnapshot { frameID = -1 };
                return false;
            }

            snapshot = _snapshotBuffer.GetSnapshot(frameID);
            return true;
        }

        public bool RestoreWorldSnapshot(int frameID, PlayerEntity[] players, BallEntity ball)
        {
            WorldStateCodec.ValidateRuntimeWorld(players, ball);
            if (!_snapshotBuffer.HasSnapshot(frameID))
            {
                return false;
            }

            WorldStateCodec.Restore(_snapshotBuffer.GetSnapshot(frameID).ToWorld(), players, ball);
            return true;
        }

        public bool RestoreWorldSnapshot(int frameID, DeterministicWorld deterministicWorld)
        {
            if (deterministicWorld == null)
            {
                throw new ArgumentNullException(nameof(deterministicWorld));
            }

            if (!_snapshotBuffer.HasSnapshot(frameID))
            {
                return false;
            }

            deterministicWorld.Restore(_snapshotBuffer.GetSnapshot(frameID).ToWorld());
            return true;
        }

        private FrameSnapshot StoreWorldSnapshot(in SimulationWorldState world)
        {
            FrameSnapshot snapshot = FrameSnapshot.FromWorld(world);
            _snapshotBuffer.AddSnapshot(snapshot);
            return snapshot;
        }
    }
}

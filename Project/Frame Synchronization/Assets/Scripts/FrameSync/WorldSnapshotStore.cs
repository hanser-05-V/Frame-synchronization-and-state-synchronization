using System;

namespace FrameSyncDemo
{
    public sealed class WorldSnapshotStore
    {
        private readonly SimulationWorldState _initialWorld;
        private readonly SnapshotBuffer _snapshots;
        private readonly int _capacity;
        private int _latestReadableFrame = -1;

        public WorldSnapshotStore(
            in SimulationWorldState initialWorld,
            int capacity = 512)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _initialWorld = initialWorld;
            _capacity = capacity;
            _snapshots = new SnapshotBuffer(capacity);
        }

        public SimulationWorldState InitialWorld => _initialWorld;

        public int EarliestReadableFrame
        {
            get
            {
                if (_latestReadableFrame < 0)
                    return -1;

                int firstCandidate = Math.Max(
                    0,
                    _latestReadableFrame - _capacity + 1);
                for (int frame = firstCandidate;
                    frame <= _latestReadableFrame;
                    frame++)
                {
                    if (_snapshots.HasSnapshot(frame))
                        return frame;
                }

                return -1;
            }
        }

        public int LatestReadableFrame
        {
            get
            {
                if (_latestReadableFrame < 0)
                    return -1;

                int firstCandidate = Math.Max(
                    0,
                    _latestReadableFrame - _capacity + 1);
                for (int frame = _latestReadableFrame;
                    frame >= firstCandidate;
                    frame--)
                {
                    if (_snapshots.HasSnapshot(frame))
                        return frame;
                }

                return -1;
            }
        }

        public FrameSnapshot Store(in SimulationWorldState world)
        {
            FrameSnapshot snapshot = FrameSnapshot.FromWorld(world);
            _snapshots.AddSnapshot(snapshot);
            if (snapshot.frameID > _latestReadableFrame)
            {
                _latestReadableFrame = snapshot.frameID;
            }
            return snapshot;
        }

        public void TruncateAfter(int canonicalFrame)
        {
            if (canonicalFrame < -1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(canonicalFrame));
            }

            if (_latestReadableFrame > canonicalFrame)
            {
                _latestReadableFrame = canonicalFrame;
            }
        }

        public bool TryGet(int canonicalFrame, out FrameSnapshot snapshot)
        {
            if (canonicalFrame > _latestReadableFrame ||
                !_snapshots.HasSnapshot(canonicalFrame))
            {
                snapshot = new FrameSnapshot { frameID = -1 };
                return false;
            }

            snapshot = _snapshots.GetSnapshot(canonicalFrame);
            return true;
        }

        public bool TryRestore(int canonicalFrame, DeterministicWorld world)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (!TryGet(canonicalFrame, out FrameSnapshot snapshot))
            {
                return false;
            }

            world.Restore(snapshot.ToWorld());
            return true;
        }

        public void RestoreInitial(DeterministicWorld world)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            world.Restore(_initialWorld);
        }
    }
}

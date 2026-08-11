using System;

namespace FrameSyncDemo
{
    public sealed class HighlightClip
    {
        private readonly FrameSnapshot[] _frames;

        public int StartFrame { get; }
        public int ShotFrame { get; }
        public int ScoreFrame { get; }
        public int EndFrame { get; }
        public int ShooterPlayerIndex { get; }
        public int FrameCount => _frames.Length;

        public HighlightClip(
            int startFrame,
            int shotFrame,
            int scoreFrame,
            int endFrame,
            int shooterPlayerIndex,
            FrameSnapshot[] frames)
        {
            if (frames == null)
                throw new ArgumentNullException(nameof(frames));
            if (frames.Length != endFrame - startFrame + 1)
                throw new ArgumentException("Frame range does not match clip length.", nameof(frames));

            StartFrame = startFrame;
            ShotFrame = shotFrame;
            ScoreFrame = scoreFrame;
            EndFrame = endFrame;
            ShooterPlayerIndex = shooterPlayerIndex;
            _frames = (FrameSnapshot[])frames.Clone();
        }

        public FrameSnapshot GetFrame(int index)
        {
            if (index < 0 || index >= _frames.Length)
                throw new ArgumentOutOfRangeException(nameof(index));

            return _frames[index];
        }
    }
}

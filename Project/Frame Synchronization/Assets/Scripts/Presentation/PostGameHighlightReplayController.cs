using System;
using System.Collections.Generic;

namespace FrameSyncDemo
{
    public sealed class PostGameHighlightReplayController
    {
        public const float DefaultFrameDurationSeconds = 0.033f;

        private readonly List<HighlightClip> _clips;
        private readonly float _frameDurationSeconds;
        private float _accumulatorSeconds;

        public bool IsPlaying { get; private set; }
        public int CurrentClipIndex { get; private set; }
        public int CurrentFrameIndex { get; private set; }
        public int ClipCount => _clips.Count;
        public HighlightClip CurrentClip =>
            _clips.Count == 0 ? null : _clips[CurrentClipIndex];

        public PostGameHighlightReplayController(
            IReadOnlyList<HighlightClip> clips,
            float frameDurationSeconds = DefaultFrameDurationSeconds)
        {
            if (clips == null)
                throw new ArgumentNullException(nameof(clips));
            if (frameDurationSeconds <= 0f ||
                float.IsNaN(frameDurationSeconds) ||
                float.IsInfinity(frameDurationSeconds))
            {
                throw new ArgumentOutOfRangeException(nameof(frameDurationSeconds));
            }

            _clips = new List<HighlightClip>(clips.Count);
            for (int i = 0; i < clips.Count; i++)
            {
                if (clips[i] == null)
                    throw new ArgumentException("Clips cannot contain null.", nameof(clips));
                _clips.Add(clips[i]);
            }

            _frameDurationSeconds = frameDurationSeconds;
            CurrentClipIndex = 0;
            CurrentFrameIndex = 0;
            IsPlaying = _clips.Count > 0;
        }

        public void Update(float deltaTimeSeconds)
        {
            if (!IsPlaying || deltaTimeSeconds <= 0f)
                return;

            _accumulatorSeconds += deltaTimeSeconds;
            while (IsPlaying &&
                   _accumulatorSeconds + 0.000001f >= _frameDurationSeconds)
            {
                _accumulatorSeconds -= _frameDurationSeconds;
                AdvanceOneFrame();
            }
        }

        public void TogglePlaying()
        {
            if (_clips.Count == 0)
                return;

            IsPlaying = !IsPlaying;
        }

        public void Restart()
        {
            if (_clips.Count == 0)
                return;

            CurrentFrameIndex = 0;
            _accumulatorSeconds = 0f;
            IsPlaying = true;
        }

        public void Previous()
        {
            if (_clips.Count == 0)
                return;

            CurrentClipIndex = Math.Max(0, CurrentClipIndex - 1);
            Restart();
        }

        public void Next()
        {
            if (_clips.Count == 0)
                return;

            CurrentClipIndex = Math.Min(
                _clips.Count - 1,
                CurrentClipIndex + 1);
            Restart();
        }

        public bool TryGetSample(
            out FrameSnapshot from,
            out FrameSnapshot to,
            out float alpha)
        {
            from = default;
            to = default;
            alpha = 0f;
            if (_clips.Count == 0)
                return false;

            HighlightClip clip = _clips[CurrentClipIndex];
            from = clip.GetFrame(CurrentFrameIndex);
            int nextFrameIndex = Math.Min(
                CurrentFrameIndex + 1,
                clip.FrameCount - 1);
            to = clip.GetFrame(nextFrameIndex);
            if (nextFrameIndex != CurrentFrameIndex)
            {
                alpha = Math.Max(
                    0f,
                    Math.Min(1f, _accumulatorSeconds / _frameDurationSeconds));
            }

            return true;
        }

        private void AdvanceOneFrame()
        {
            HighlightClip clip = _clips[CurrentClipIndex];
            if (CurrentFrameIndex + 1 < clip.FrameCount)
            {
                CurrentFrameIndex++;
                return;
            }

            if (CurrentClipIndex + 1 < _clips.Count)
            {
                CurrentClipIndex++;
                CurrentFrameIndex = 0;
                return;
            }

            CurrentFrameIndex = clip.FrameCount - 1;
            _accumulatorSeconds = 0f;
            IsPlaying = false;
        }
    }
}

using System;
using UnityEngine;

namespace FrameSyncDemo
{
    [Serializable]
    public sealed class ConfirmedPlaybackSettings
    {
        public const int DefaultTargetReadyBacklog = 0;
        public const int DefaultMaxReadyBacklog = 8;
        public const float DefaultCatchUpGainPerFrame = 0.15f;
        public const float DefaultMaximumPlaybackSpeed = 1.5f;
        public const float DefaultSpeedSmoothingSeconds = 0.10f;
        public const float DefaultCatchUpEnterExcessFrames = 0.25f;
        public const float DefaultCatchUpExitExcessFrames = 0.10f;
        public const float DefaultOverflowCorrectionMaximumSeconds = 0.20f;

        [SerializeField]
        private int _targetReadyBacklog = DefaultTargetReadyBacklog;

        [SerializeField]
        private int _maxReadyBacklog = DefaultMaxReadyBacklog;

        [SerializeField]
        private float _catchUpGainPerFrame = DefaultCatchUpGainPerFrame;

        [SerializeField]
        private float _maximumPlaybackSpeed = DefaultMaximumPlaybackSpeed;

        [SerializeField]
        private float _speedSmoothingSeconds = DefaultSpeedSmoothingSeconds;

        [SerializeField]
        private float _catchUpEnterExcessFrames =
            DefaultCatchUpEnterExcessFrames;

        [SerializeField]
        private float _catchUpExitExcessFrames =
            DefaultCatchUpExitExcessFrames;

        [SerializeField]
        private float _overflowCorrectionMaximumSeconds =
            DefaultOverflowCorrectionMaximumSeconds;

        public int TargetReadyBacklog => _targetReadyBacklog;
        public int MaxReadyBacklog => _maxReadyBacklog;
        public float CatchUpGainPerFrame => _catchUpGainPerFrame;
        public float MaximumPlaybackSpeed => _maximumPlaybackSpeed;
        public float SpeedSmoothingSeconds => _speedSmoothingSeconds;
        public float CatchUpEnterExcessFrames =>
            _catchUpEnterExcessFrames;
        public float CatchUpExitExcessFrames =>
            _catchUpExitExcessFrames;
        public float OverflowCorrectionMaximumSeconds =>
            _overflowCorrectionMaximumSeconds;

        public ConfirmedPlaybackSettings()
        {
        }

        public ConfirmedPlaybackSettings(
            int targetReadyBacklog,
            int maxReadyBacklog,
            float catchUpGainPerFrame,
            float maximumPlaybackSpeed,
            float speedSmoothingSeconds,
            float catchUpEnterExcessFrames,
            float catchUpExitExcessFrames,
            float overflowCorrectionMaximumSeconds)
        {
            _targetReadyBacklog = targetReadyBacklog;
            _maxReadyBacklog = maxReadyBacklog;
            _catchUpGainPerFrame = catchUpGainPerFrame;
            _maximumPlaybackSpeed = maximumPlaybackSpeed;
            _speedSmoothingSeconds = speedSmoothingSeconds;
            _catchUpEnterExcessFrames = catchUpEnterExcessFrames;
            _catchUpExitExcessFrames = catchUpExitExcessFrames;
            _overflowCorrectionMaximumSeconds =
                overflowCorrectionMaximumSeconds;
        }

        public void ValidateOrReset(Action<string> warningSink)
        {
            if (warningSink == null)
                throw new ArgumentNullException(nameof(warningSink));

            bool invalid =
                _targetReadyBacklog < 0 ||
                _maxReadyBacklog < 1 ||
                _targetReadyBacklog >= _maxReadyBacklog ||
                !IsFinite(_catchUpGainPerFrame) ||
                _catchUpGainPerFrame < 0f ||
                !IsFinite(_maximumPlaybackSpeed) ||
                _maximumPlaybackSpeed < 1f ||
                !IsFinite(_speedSmoothingSeconds) ||
                _speedSmoothingSeconds <= 0f ||
                !IsFinite(_catchUpEnterExcessFrames) ||
                !IsFinite(_catchUpExitExcessFrames) ||
                _catchUpExitExcessFrames < 0f ||
                _catchUpEnterExcessFrames <=
                    _catchUpExitExcessFrames ||
                !IsFinite(_overflowCorrectionMaximumSeconds) ||
                _overflowCorrectionMaximumSeconds <= 0f;
            if (!invalid)
                return;

            ResetToDefaults();
            warningSink(
                "[RouteC][ConfirmedPlayback] Invalid settings; " +
                "restored Route C defaults.");
        }

        private void ResetToDefaults()
        {
            _targetReadyBacklog = DefaultTargetReadyBacklog;
            _maxReadyBacklog = DefaultMaxReadyBacklog;
            _catchUpGainPerFrame = DefaultCatchUpGainPerFrame;
            _maximumPlaybackSpeed = DefaultMaximumPlaybackSpeed;
            _speedSmoothingSeconds = DefaultSpeedSmoothingSeconds;
            _catchUpEnterExcessFrames =
                DefaultCatchUpEnterExcessFrames;
            _catchUpExitExcessFrames =
                DefaultCatchUpExitExcessFrames;
            _overflowCorrectionMaximumSeconds =
                DefaultOverflowCorrectionMaximumSeconds;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}

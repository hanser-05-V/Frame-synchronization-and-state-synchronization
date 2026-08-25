using System;

namespace FrameSyncDemo
{
    public readonly struct ConfirmedPlaybackAdvance
    {
        public ConfirmedPlaybackAdvance(
            int activeFromFrame,
            int activeToFrame,
            float alpha,
            int readyBacklog,
            float fractionalBacklog,
            float excessBacklog,
            float targetSpeed,
            float playbackSpeed,
            int crossedIntervalCount,
            long renderFrameDropCount,
            bool hasPresentationFrame,
            bool isUnderflow,
            bool isOverflowRebase,
            int droppedFromFrame,
            int droppedToFrame,
            bool isFaulted,
            int missingFromFrame,
            int missingToFrame)
        {
            ActiveFromFrame = activeFromFrame;
            ActiveToFrame = activeToFrame;
            Alpha = alpha;
            ReadyBacklog = readyBacklog;
            FractionalBacklog = fractionalBacklog;
            ExcessBacklog = excessBacklog;
            TargetSpeed = targetSpeed;
            PlaybackSpeed = playbackSpeed;
            CrossedIntervalCount = crossedIntervalCount;
            RenderFrameDropCount = renderFrameDropCount;
            HasPresentationFrame = hasPresentationFrame;
            IsUnderflow = isUnderflow;
            IsOverflowRebase = isOverflowRebase;
            DroppedFromFrame = droppedFromFrame;
            DroppedToFrame = droppedToFrame;
            IsFaulted = isFaulted;
            MissingFromFrame = missingFromFrame;
            MissingToFrame = missingToFrame;
        }

        public int ActiveFromFrame { get; }
        public int ActiveToFrame { get; }
        public float Alpha { get; }
        public int ReadyBacklog { get; }
        public float FractionalBacklog { get; }
        public float ExcessBacklog { get; }
        public float TargetSpeed { get; }
        public float PlaybackSpeed { get; }
        public int CrossedIntervalCount { get; }
        public long RenderFrameDropCount { get; }
        public bool HasPresentationFrame { get; }
        public bool IsUnderflow { get; }
        public bool IsOverflowRebase { get; }
        public int DroppedFromFrame { get; }
        public int DroppedToFrame { get; }
        public bool IsFaulted { get; }
        public int MissingFromFrame { get; }
        public int MissingToFrame { get; }
    }

    public sealed class ConfirmedPresentationCursor
    {
        private readonly int _startupBufferFrames;
        private readonly bool _usesLegacyApi;
        private readonly int _targetReadyBacklog;
        private readonly int _maxReadyBacklog;
        private readonly float _catchUpGainPerFrame;
        private readonly float _maximumPlaybackSpeed;
        private readonly float _speedSmoothingSeconds;
        private readonly float _catchUpEnterExcessFrames;
        private readonly float _catchUpExitExcessFrames;
        private int _lastPresentedFrame = -1;
        private bool _started;
        private bool _hasActiveInterval;
        private float _interpolationAlpha = 1f;
        private int _activeFromFrame = -1;
        private int _activeToFrame = -1;
        private int _lastConfirmedHead = -1;
        private bool _hasPresentationFrame;
        private bool _catchingUp;
        private float _playbackAlpha = 1f;
        private float _targetSpeed = 1f;
        private float _playbackSpeed = 1f;
        private float _catchUpQualificationSeconds;
        private long _renderFrameDropCount;
        private bool _isFaulted;
        private int _missingFromFrame = -1;
        private int _missingToFrame = -1;

        public ConfirmedPresentationCursor(int startupBufferFrames)
        {
            if (startupBufferFrames < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(startupBufferFrames));
            }

            _startupBufferFrames = startupBufferFrames;
            _usesLegacyApi = true;
            _targetReadyBacklog = startupBufferFrames;
            _maxReadyBacklog =
                ConfirmedPlaybackSettings.DefaultMaxReadyBacklog;
            _catchUpGainPerFrame =
                ConfirmedPlaybackSettings.DefaultCatchUpGainPerFrame;
            _maximumPlaybackSpeed =
                ConfirmedPlaybackSettings.DefaultMaximumPlaybackSpeed;
            _speedSmoothingSeconds =
                ConfirmedPlaybackSettings.DefaultSpeedSmoothingSeconds;
            _catchUpEnterExcessFrames =
                ConfirmedPlaybackSettings.DefaultCatchUpEnterExcessFrames;
            _catchUpExitExcessFrames =
                ConfirmedPlaybackSettings.DefaultCatchUpExitExcessFrames;
        }

        public ConfirmedPresentationCursor(
            ConfirmedPlaybackSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            _startupBufferFrames = settings.TargetReadyBacklog;
            _usesLegacyApi = false;
            _targetReadyBacklog = settings.TargetReadyBacklog;
            _maxReadyBacklog = settings.MaxReadyBacklog;
            _catchUpGainPerFrame = settings.CatchUpGainPerFrame;
            _maximumPlaybackSpeed = settings.MaximumPlaybackSpeed;
            _speedSmoothingSeconds = settings.SpeedSmoothingSeconds;
            _catchUpEnterExcessFrames =
                settings.CatchUpEnterExcessFrames;
            _catchUpExitExcessFrames =
                settings.CatchUpExitExcessFrames;
        }

        public int LastPresentedFrame => _lastPresentedFrame;
        public int ActiveFromFrame => _activeFromFrame;
        public int ActiveToFrame => _activeToFrame;
        public float InterpolationAlpha => _usesLegacyApi
            ? _interpolationAlpha
            : _playbackAlpha;
        public float TargetSpeed => _targetSpeed;
        public float PlaybackSpeed => _playbackSpeed;
        public bool IsFaulted => _isFaulted;

        public ConfirmedPlaybackAdvance Advance(
            int confirmedHead,
            float deltaTimeSeconds,
            float frameDurationSeconds)
        {
            ValidateAdvanceArguments(
                confirmedHead,
                deltaTimeSeconds,
                frameDurationSeconds);
            _lastConfirmedHead = confirmedHead;

            if (_isFaulted)
            {
                return CreateAdvance(
                    confirmedHead,
                    crossedIntervalCount: 0,
                    isUnderflow: false);
            }

            if (_hasPresentationFrame &&
                confirmedHead - _activeToFrame > _maxReadyBacklog)
            {
                return RebaseForOverflow(confirmedHead);
            }

            if (!_hasPresentationFrame || _playbackAlpha >= 1f)
                TryActivateNextInterval(confirmedHead);

            if (_hasPresentationFrame &&
                confirmedHead - _activeToFrame > _maxReadyBacklog)
            {
                return RebaseForOverflow(confirmedHead);
            }

            if (!_hasPresentationFrame)
            {
                return CreateAdvance(
                    confirmedHead,
                    crossedIntervalCount: 0,
                    isUnderflow: false);
            }

            CalculateBacklog(
                confirmedHead,
                out int startingReadyBacklog,
                out _,
                out float startingExcess);
            UpdateTargetSpeed(startingExcess);
            float playbackSpeedTarget = GetPlaybackSpeedTarget(
                startingReadyBacklog,
                deltaTimeSeconds);
            float maximumSpeedDelta =
                (_maximumPlaybackSpeed - 1f) *
                deltaTimeSeconds /
                _speedSmoothingSeconds;
            _playbackSpeed = MoveTowards(
                _playbackSpeed,
                playbackSpeedTarget,
                maximumSpeedDelta);

            float remainingFrames =
                deltaTimeSeconds *
                _playbackSpeed /
                frameDurationSeconds;
            int crossedIntervalCount = 0;
            bool isUnderflow = false;
            int iterationGuard = Math.Max(
                1,
                confirmedHead - _activeToFrame + 2);
            while (remainingFrames > 0f && iterationGuard-- > 0)
            {
                if (_playbackAlpha >= 1f &&
                    !TryActivateNextInterval(confirmedHead))
                {
                    isUnderflow = true;
                    break;
                }

                float consumed = Math.Min(
                    1f - _playbackAlpha,
                    remainingFrames);
                _playbackAlpha += consumed;
                remainingFrames -= consumed;
                if (_playbackAlpha >= 1f)
                {
                    _playbackAlpha = 1f;
                    crossedIntervalCount++;
                }
            }

            if (_playbackAlpha >= 1f &&
                !TryActivateNextInterval(confirmedHead))
            {
                isUnderflow = true;
            }

            if (crossedIntervalCount > 1)
                _renderFrameDropCount += crossedIntervalCount;

            CalculateBacklog(
                confirmedHead,
                out _,
                out _,
                out float finalExcess);
            UpdateTargetSpeed(finalExcess);
            return CreateAdvance(
                confirmedHead,
                crossedIntervalCount,
                isUnderflow);
        }

        public void EnterPresentationFault(
            int missingFromFrame,
            int missingToFrame)
        {
            if (missingFromFrame < -1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(missingFromFrame));
            }
            if (missingToFrame < missingFromFrame)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(missingToFrame));
            }
            if (_isFaulted)
                return;

            _isFaulted = true;
            _missingFromFrame = missingFromFrame;
            _missingToFrame = missingToFrame;
            _catchingUp = false;
            _catchUpQualificationSeconds = 0f;
            _targetSpeed = 1f;
        }

        public void RecalculateAfterRollback(int confirmedHead)
        {
            if (confirmedHead < -1)
                throw new ArgumentOutOfRangeException(nameof(confirmedHead));
            if (confirmedHead < _lastConfirmedHead)
            {
                throw new InvalidOperationException(
                    "Confirmed presentation head cannot move backwards.");
            }

            _lastConfirmedHead = confirmedHead;
            _catchingUp = false;
            _catchUpQualificationSeconds = 0f;
            _targetSpeed = 1f;
            _playbackSpeed = 1f;
            if (!_hasPresentationFrame || _isFaulted)
                return;

            CalculateBacklog(
                confirmedHead,
                out _,
                out _,
                out float excessBacklog);
            UpdateTargetSpeed(excessBacklog);
        }

        private void ValidateAdvanceArguments(
            int confirmedHead,
            float deltaTimeSeconds,
            float frameDurationSeconds)
        {
            if (confirmedHead < -1)
                throw new ArgumentOutOfRangeException(nameof(confirmedHead));
            if (confirmedHead < _lastConfirmedHead)
            {
                throw new InvalidOperationException(
                    "Confirmed presentation head cannot move backwards.");
            }
            if (!IsFinite(deltaTimeSeconds) || deltaTimeSeconds < 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(deltaTimeSeconds));
            }
            if (!IsFinite(frameDurationSeconds) ||
                frameDurationSeconds <= 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frameDurationSeconds));
            }
        }

        private bool TryActivateNextInterval(int confirmedHead)
        {
            if (_hasPresentationFrame && _playbackAlpha < 1f)
                return true;

            int nextToFrame = _hasPresentationFrame
                ? _activeToFrame + 1
                : 0;
            if (confirmedHead - nextToFrame < _targetReadyBacklog)
                return false;

            _activeFromFrame = nextToFrame - 1;
            _activeToFrame = nextToFrame;
            _playbackAlpha = 0f;
            _hasPresentationFrame = true;
            return true;
        }

        private ConfirmedPlaybackAdvance RebaseForOverflow(
            int confirmedHead)
        {
            int oldActiveToFrame = _activeToFrame;
            int newActiveToFrame =
                confirmedHead - _targetReadyBacklog;
            int droppedFromFrame = oldActiveToFrame + 1;
            int droppedToFrame = newActiveToFrame - 1;
            if (droppedFromFrame > droppedToFrame)
            {
                droppedFromFrame = -1;
                droppedToFrame = -1;
            }

            _activeFromFrame = newActiveToFrame - 1;
            _activeToFrame = newActiveToFrame;
            _playbackAlpha = 1f;
            _hasPresentationFrame = true;
            _catchingUp = false;
            _catchUpQualificationSeconds = 0f;
            _targetSpeed = 1f;
            _playbackSpeed = 1f;
            return CreateAdvance(
                confirmedHead,
                crossedIntervalCount: 0,
                isUnderflow: false,
                isOverflowRebase: true,
                droppedFromFrame,
                droppedToFrame);
        }

        private void CalculateBacklog(
            int confirmedHead,
            out int readyBacklog,
            out float fractionalBacklog,
            out float excessBacklog)
        {
            readyBacklog = confirmedHead - _activeToFrame;
            fractionalBacklog = confirmedHead -
                (_activeFromFrame + _playbackAlpha);
            excessBacklog = Math.Max(
                0f,
                fractionalBacklog - (_targetReadyBacklog + 1f));
        }

        private void UpdateTargetSpeed(float excessBacklog)
        {
            if (!_catchingUp &&
                excessBacklog > _catchUpEnterExcessFrames)
            {
                _catchingUp = true;
            }
            else if (_catchingUp &&
                excessBacklog < _catchUpExitExcessFrames)
            {
                _catchingUp = false;
                _catchUpQualificationSeconds = 0f;
            }

            _targetSpeed = _catchingUp
                ? Math.Min(
                    _maximumPlaybackSpeed,
                    1f + _catchUpGainPerFrame * excessBacklog)
                : 1f;
        }

        private float GetPlaybackSpeedTarget(
            int readyBacklog,
            float deltaTimeSeconds)
        {
            if (!_catchingUp)
            {
                _catchUpQualificationSeconds = 0f;
                return 1f;
            }

            if (readyBacklog >= 2)
            {
                _catchUpQualificationSeconds =
                    _speedSmoothingSeconds;
                return _targetSpeed;
            }

            _catchUpQualificationSeconds = Math.Min(
                _speedSmoothingSeconds,
                _catchUpQualificationSeconds + deltaTimeSeconds);
            return _catchUpQualificationSeconds >=
                _speedSmoothingSeconds
                ? _targetSpeed
                : 1f;
        }

        private ConfirmedPlaybackAdvance CreateAdvance(
            int confirmedHead,
            int crossedIntervalCount,
            bool isUnderflow,
            bool isOverflowRebase = false,
            int droppedFromFrame = -1,
            int droppedToFrame = -1)
        {
            CalculateBacklog(
                confirmedHead,
                out int readyBacklog,
                out float fractionalBacklog,
                out float excessBacklog);
            return new ConfirmedPlaybackAdvance(
                _activeFromFrame,
                _activeToFrame,
                _playbackAlpha,
                readyBacklog,
                fractionalBacklog,
                excessBacklog,
                _targetSpeed,
                _playbackSpeed,
                crossedIntervalCount,
                _renderFrameDropCount,
                _hasPresentationFrame,
                isUnderflow,
                isOverflowRebase,
                droppedFromFrame,
                droppedToFrame,
                _isFaulted,
                _missingFromFrame,
                _missingToFrame);
        }

        private static float MoveTowards(
            float current,
            float target,
            float maximumDelta)
        {
            if (Math.Abs(target - current) <= maximumDelta)
                return target;

            return current + Math.Sign(target - current) * maximumDelta;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public bool TryGetNext(
            int confirmedThroughFrame,
            out int frameID)
        {
            if (confirmedThroughFrame < -1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(confirmedThroughFrame));
            }
            if (confirmedThroughFrame < _lastPresentedFrame)
            {
                throw new InvalidOperationException(
                    "Confirmed presentation head cannot move backwards.");
            }

            frameID = _lastPresentedFrame + 1;
            if (_hasActiveInterval && _interpolationAlpha < 1f)
                return false;

            if (!_started)
            {
                int bufferedFrames =
                    confirmedThroughFrame - _lastPresentedFrame;
                if (bufferedFrames <= _startupBufferFrames)
                    return false;

                _started = true;
            }

            return frameID <= confirmedThroughFrame;
        }

        public void MarkPresented(int frameID)
        {
            int expectedFrame = _lastPresentedFrame + 1;
            if (frameID != expectedFrame)
            {
                throw new InvalidOperationException(
                    "Confirmed presentation frames must be consecutive. " +
                    $"Expected {expectedFrame}, got {frameID}.");
            }

            _lastPresentedFrame = frameID;
            _hasActiveInterval = true;
            _interpolationAlpha = 0f;
        }

        public void Advance(
            float deltaTimeSeconds,
            float frameDurationSeconds)
        {
            if (float.IsNaN(deltaTimeSeconds) ||
                float.IsInfinity(deltaTimeSeconds) ||
                deltaTimeSeconds < 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(deltaTimeSeconds));
            }
            if (float.IsNaN(frameDurationSeconds) ||
                float.IsInfinity(frameDurationSeconds) ||
                frameDurationSeconds <= 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frameDurationSeconds));
            }
            if (!_hasActiveInterval || _interpolationAlpha >= 1f)
                return;

            _interpolationAlpha = Math.Min(
                1f,
                _interpolationAlpha +
                deltaTimeSeconds / frameDurationSeconds);
        }
    }
}

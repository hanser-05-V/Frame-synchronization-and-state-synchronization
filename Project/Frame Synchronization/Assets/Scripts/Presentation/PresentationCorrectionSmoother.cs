using System;
using UnityEngine;

namespace FrameSyncDemo
{
    /// <summary>
    /// 仅用于表现层：以连续显示速度和有限纠正速度消除回滚显示误差。
    /// </summary>
    public sealed class PresentationCorrectionSmoother
    {
        private const float CompletionDistance = 0.0001f;
        private const float MaximumAllowedDurationSeconds = 0.2f;

        private readonly float _minimumDurationSeconds;
        private readonly float _maximumDurationSeconds;
        private readonly float _maximumCorrectionSpeed;
        private readonly float _snapDistance;
        private Vector3 _currentDisplayPosition;
        private Vector3 _previousTargetPosition;
        private Vector3 _displayVelocity;
        private Vector3 _startOffset;
        private Vector3 _initialRelativeVelocity;
        private float _activeDurationSeconds;
        private float _elapsedSeconds;
        private bool _hasDisplayState;
        private bool _initializeCorrectionVelocity;

        public PresentationCorrectionSmoother(float durationSeconds)
            : this(
                durationSeconds,
                Mathf.Min(
                    durationSeconds * 2f,
                    MaximumAllowedDurationSeconds),
                10f,
                2f)
        {
        }

        public PresentationCorrectionSmoother(
            float minimumDurationSeconds,
            float maximumDurationSeconds,
            float maximumCorrectionSpeed,
            float snapDistance)
        {
            if (!IsFinite(minimumDurationSeconds) ||
                minimumDurationSeconds <= 0f ||
                minimumDurationSeconds > MaximumAllowedDurationSeconds)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(minimumDurationSeconds),
                    "表现纠正最短时间必须在 (0, 0.2] 秒范围内。");
            }
            if (!IsFinite(maximumDurationSeconds) ||
                maximumDurationSeconds < minimumDurationSeconds ||
                maximumDurationSeconds > MaximumAllowedDurationSeconds)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumDurationSeconds),
                    "表现纠正最长时间必须不小于最短时间且不超过 0.2 秒。");
            }
            if (!IsFinite(maximumCorrectionSpeed) ||
                maximumCorrectionSpeed <= 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumCorrectionSpeed),
                    "表现纠正最大速度必须是有限正数。");
            }
            if (!IsFinite(snapDistance) || snapDistance <= 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(snapDistance),
                    "表现纠正直接跳转距离必须是有限正数。");
            }
            if ((double)maximumCorrectionSpeed * maximumDurationSeconds <
                snapDistance)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumCorrectionSpeed),
                    "最大纠正速度与最长时间不足以覆盖直接跳转距离。");
            }

            _minimumDurationSeconds = minimumDurationSeconds;
            _maximumDurationSeconds = maximumDurationSeconds;
            _maximumCorrectionSpeed = maximumCorrectionSpeed;
            _snapDistance = snapDistance;
        }

        public bool IsCorrecting { get; private set; }

        public void BeginCorrection(
            Vector3 currentDisplayPosition,
            Vector3 correctedTargetPosition)
        {
            if (!_hasDisplayState)
                _displayVelocity = Vector3.zero;

            _currentDisplayPosition = currentDisplayPosition;
            _previousTargetPosition = correctedTargetPosition;
            _startOffset = currentDisplayPosition - correctedTargetPosition;
            _elapsedSeconds = 0f;
            _hasDisplayState = true;

            float distance = _startOffset.magnitude;
            if (distance <= CompletionDistance)
            {
                IsCorrecting = false;
                _currentDisplayPosition = correctedTargetPosition;
                _previousTargetPosition = correctedTargetPosition;
                return;
            }

            if (distance >= _snapDistance)
            {
                IsCorrecting = false;
                _hasDisplayState = false;
                _displayVelocity = Vector3.zero;
                return;
            }

            float speedLimitedDuration =
                1.5f * distance / _maximumCorrectionSpeed;
            _activeDurationSeconds = Mathf.Clamp(
                speedLimitedDuration,
                _minimumDurationSeconds,
                _maximumDurationSeconds);
            _initialRelativeVelocity = Vector3.zero;
            _initializeCorrectionVelocity = true;
            IsCorrecting = true;
        }

        public Vector3 Evaluate(Vector3 currentTargetPosition, float deltaTime)
        {
            float safeDeltaTime = IsFinite(deltaTime) && deltaTime > 0f
                ? deltaTime
                : 0f;
            if (!IsCorrecting)
            {
                TrackUncorrectedTarget(currentTargetPosition, safeDeltaTime);
                return currentTargetPosition;
            }

            if (safeDeltaTime <= 0f)
                return _currentDisplayPosition;

            Vector3 targetVelocity =
                (currentTargetPosition - _previousTargetPosition) /
                safeDeltaTime;
            if (_initializeCorrectionVelocity)
            {
                _initialRelativeVelocity = Vector3.ClampMagnitude(
                    _displayVelocity - targetVelocity,
                    _maximumCorrectionSpeed);
                float offsetVelocityDot = Vector3.Dot(
                    _startOffset,
                    _initialRelativeVelocity);
                if (offsetVelocityDot > 0f)
                {
                    _activeDurationSeconds = _maximumDurationSeconds;
                }
                else if (offsetVelocityDot < 0f)
                {
                    Vector3 offsetDirection = _startOffset.normalized;
                    float parallelSpeed = Vector3.Dot(
                        _initialRelativeVelocity,
                        offsetDirection);
                    float maximumTowardSpeed =
                        3f * _startOffset.magnitude /
                        _activeDurationSeconds;
                    if (parallelSpeed < -maximumTowardSpeed)
                    {
                        _initialRelativeVelocity += offsetDirection *
                            (-maximumTowardSpeed - parallelSpeed);
                    }
                }
                _initializeCorrectionVelocity = false;
            }

            _elapsedSeconds += safeDeltaTime;
            float progress = Mathf.Clamp01(
                _elapsedSeconds / _activeDurationSeconds);
            float progressSquared = progress * progress;
            float progressCubed = progressSquared * progress;
            float positionBasis =
                2f * progressCubed - 3f * progressSquared + 1f;
            float velocityBasis =
                progressCubed - 2f * progressSquared + progress;
            Vector3 desiredError =
                _startOffset * positionBasis +
                _initialRelativeVelocity *
                (_activeDurationSeconds * velocityBasis);

            Vector3 previousError =
                _currentDisplayPosition - _previousTargetPosition;
            Vector3 errorChange = Vector3.ClampMagnitude(
                desiredError - previousError,
                _maximumCorrectionSpeed * safeDeltaTime);
            Vector3 nextError = previousError + errorChange;
            Vector3 nextDisplayPosition = currentTargetPosition + nextError;
            _displayVelocity =
                (nextDisplayPosition - _currentDisplayPosition) /
                safeDeltaTime;
            _currentDisplayPosition = nextDisplayPosition;
            _previousTargetPosition = currentTargetPosition;

            if (progress >= 1f &&
                nextError.sqrMagnitude <=
                CompletionDistance * CompletionDistance)
            {
                _currentDisplayPosition = currentTargetPosition;
                _displayVelocity = targetVelocity;
                IsCorrecting = false;
                return currentTargetPosition;
            }

            return nextDisplayPosition;
        }

        public void Snap()
        {
            _currentDisplayPosition = Vector3.zero;
            _previousTargetPosition = Vector3.zero;
            _displayVelocity = Vector3.zero;
            _startOffset = Vector3.zero;
            _initialRelativeVelocity = Vector3.zero;
            _activeDurationSeconds = 0f;
            _elapsedSeconds = 0f;
            _hasDisplayState = false;
            _initializeCorrectionVelocity = false;
            IsCorrecting = false;
        }

        private void TrackUncorrectedTarget(
            Vector3 currentTargetPosition,
            float safeDeltaTime)
        {
            if (_hasDisplayState && safeDeltaTime > 0f)
            {
                _displayVelocity =
                    (currentTargetPosition - _currentDisplayPosition) /
                    safeDeltaTime;
            }
            else
            {
                _displayVelocity = Vector3.zero;
            }

            _currentDisplayPosition = currentTargetPosition;
            _previousTargetPosition = currentTargetPosition;
            _hasDisplayState = true;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}

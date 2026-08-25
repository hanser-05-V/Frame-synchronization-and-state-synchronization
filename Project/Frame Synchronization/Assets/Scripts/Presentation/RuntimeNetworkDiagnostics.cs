using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace FrameSyncDemo
{
    public sealed class RuntimeNetworkDiagnostics
    {
        private const float StationaryEpsilon = 0.000001f;

        private readonly bool _enabled;
        private readonly Action<string> _sink;
        private readonly long _timestampFrequency;
        private readonly int _targetReadyBacklog;
        private readonly int _maxReadyBacklog;
        private readonly int _actualArrivalCapacity;
        private readonly SortedDictionary<int, long> _actualArrivalTimestamps;
        private bool _hasReceive;
        private long _lastReceiveTimestamp;
        private bool _hasRemotePosition;
        private float _lastRemoteX;
        private float _lastRemoteZ;
        private float _lastMotionX;
        private float _lastMotionZ;
        private long _lastMotionTimestamp;
        private bool _hasPlaybackSpeedTier;
        private bool _wasFastPlayback;
        private int _speedTierSwitchCount;
        private bool _wasCatchUp;
        private long _catchUpStartTimestamp;
        private bool _wasUnderflow;
        private long _underflowStartTimestamp;
        private bool _hasBallSource;
        private ViewSampleSource _lastBallSource;
        private int _lastBallHolderPlayerIndex;

        public RuntimeNetworkDiagnostics(
            bool enabled,
            Action<string> sink,
            long timestampFrequency = 0,
            int targetReadyBacklog =
                ConfirmedPlaybackSettings.DefaultTargetReadyBacklog,
            int maxReadyBacklog =
                ConfirmedPlaybackSettings.DefaultMaxReadyBacklog)
        {
            if (sink == null)
                throw new ArgumentNullException(nameof(sink));
            if (timestampFrequency < 0)
                throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
            if (targetReadyBacklog < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(targetReadyBacklog));
            }
            if (maxReadyBacklog < 1 ||
                targetReadyBacklog >= maxReadyBacklog)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxReadyBacklog));
            }

            _enabled = enabled;
            _sink = sink;
            _timestampFrequency = timestampFrequency > 0
                ? timestampFrequency
                : Stopwatch.Frequency;
            _targetReadyBacklog = targetReadyBacklog;
            _maxReadyBacklog = maxReadyBacklog;
            _actualArrivalCapacity = maxReadyBacklog <= int.MaxValue - 2
                ? maxReadyBacklog + 2
                : int.MaxValue;
            _actualArrivalTimestamps = enabled
                ? new SortedDictionary<int, long>()
                : null;
        }

        public bool Enabled => _enabled;

        public void RecordActualArrival(
            int canonicalFrame,
            long receivedTimestamp)
        {
            if (!_enabled)
                return;
            if (canonicalFrame < 0)
                throw new ArgumentOutOfRangeException(nameof(canonicalFrame));
            if (_actualArrivalTimestamps.ContainsKey(canonicalFrame))
                return;

            _actualArrivalTimestamps.Add(
                canonicalFrame,
                receivedTimestamp);
            while (_actualArrivalTimestamps.Count > _actualArrivalCapacity)
                RemoveOldestActualArrival();
        }

        public void RecordConfirmedPlayback(
            long observedTimestamp,
            in ConfirmedPlaybackAdvance sample)
        {
            if (!_enabled)
                return;

            bool fastPlayback = sample.PlaybackSpeed > 1.001f;
            if (_hasPlaybackSpeedTier &&
                fastPlayback != _wasFastPlayback)
            {
                _speedTierSwitchCount++;
            }
            _hasPlaybackSpeedTier = true;
            _wasFastPlayback = fastPlayback;

            bool isCatchUp = sample.TargetSpeed > 1.001f || fastPlayback;
            double catchUpDurationMs = UpdatePhaseDuration(
                observedTimestamp,
                isCatchUp,
                ref _wasCatchUp,
                ref _catchUpStartTimestamp);
            double underflowDurationMs = UpdatePhaseDuration(
                observedTimestamp,
                sample.IsUnderflow,
                ref _wasUnderflow,
                ref _underflowStartTimestamp);

            long actualArrivalTimestamp = 0;
            bool hasActualToVisible = sample.HasPresentationFrame &&
                sample.Alpha > 0f &&
                _actualArrivalTimestamps.TryGetValue(
                    sample.ActiveToFrame,
                    out actualArrivalTimestamp);
            double actualToVisibleMs = hasActualToVisible
                ? ToMilliseconds(observedTimestamp - actualArrivalTimestamp)
                : 0.0;
            if (hasActualToVisible)
            {
                _actualArrivalTimestamps.Remove(sample.ActiveToFrame);
            }
            if (sample.HasPresentationFrame)
                PruneActualArrivalsBefore(sample.ActiveToFrame);

            string actualToVisibleField = hasActualToVisible
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    ",\"actualToVisibleMs\":{0}",
                    actualToVisibleMs.ToString(
                        "F3",
                        CultureInfo.InvariantCulture))
                : string.Empty;
            Emit(string.Format(
                CultureInfo.InvariantCulture,
                "{{\"type\":\"confirmedPlayback\"," +
                "\"observedTimestamp\":{0}," +
                "\"targetReadyBacklog\":{1}," +
                "\"maxReadyBacklog\":{2}," +
                "\"activeFromFrame\":{3},\"activeToFrame\":{4}," +
                "\"alpha\":{5:F6},\"readyBacklog\":{6}," +
                "\"fractionalBacklog\":{7:F6}," +
                "\"excessBacklog\":{8:F6}," +
                "\"targetSpeed\":{9:F6},\"playbackSpeed\":{10:F6}," +
                "\"speedTierSwitchCount\":{11}," +
                "\"catchUpDurationMs\":{12:F3}," +
                "\"crossedIntervalCount\":{13}," +
                "\"renderFrameDropCount\":{14}," +
                "\"isUnderflow\":{15},\"underflowDurationMs\":{16:F3}," +
                "\"isOverflowRebase\":{17}," +
                "\"droppedFromFrame\":{18},\"droppedToFrame\":{19}," +
                "\"isFaulted\":{20},\"missingFromFrame\":{21}," +
                "\"missingToFrame\":{22}{23}}}",
                observedTimestamp,
                _targetReadyBacklog,
                _maxReadyBacklog,
                sample.ActiveFromFrame,
                sample.ActiveToFrame,
                sample.Alpha,
                sample.ReadyBacklog,
                sample.FractionalBacklog,
                sample.ExcessBacklog,
                sample.TargetSpeed,
                sample.PlaybackSpeed,
                _speedTierSwitchCount,
                catchUpDurationMs,
                sample.CrossedIntervalCount,
                sample.RenderFrameDropCount,
                ToJsonBoolean(sample.IsUnderflow),
                underflowDurationMs,
                ToJsonBoolean(sample.IsOverflowRebase),
                sample.DroppedFromFrame,
                sample.DroppedToFrame,
                ToJsonBoolean(sample.IsFaulted),
                sample.MissingFromFrame,
                sample.MissingToFrame,
                actualToVisibleField));
        }

        public void RecordBallSourceSwitch(
            long observedTimestamp,
            int presentationFrame,
            ViewSampleSource source,
            int holderPlayerIndex)
        {
            if (!_enabled)
                return;
            if (_hasBallSource &&
                source == _lastBallSource &&
                holderPlayerIndex == _lastBallHolderPlayerIndex)
            {
                return;
            }

            _hasBallSource = true;
            _lastBallSource = source;
            _lastBallHolderPlayerIndex = holderPlayerIndex;
            Emit(string.Format(
                CultureInfo.InvariantCulture,
                "{{\"type\":\"ballSourceSwitch\"," +
                "\"observedTimestamp\":{0},\"presentationFrame\":{1}," +
                "\"source\":\"{2}\",\"holderPlayerIndex\":{3}}}",
                observedTimestamp,
                presentationFrame,
                source,
                holderPlayerIndex));
        }

        public void RecordReceive(NetworkPacketArrival arrival)
        {
            if (!_enabled)
                return;

            double intervalMs = _hasReceive
                ? ToMilliseconds(
                    arrival.ReceivedTimestamp - _lastReceiveTimestamp)
                : 0.0;
            _hasReceive = true;
            _lastReceiveTimestamp = arrival.ReceivedTimestamp;
            Emit(string.Format(
                CultureInfo.InvariantCulture,
                "{{\"type\":\"receive\",\"sequence\":{0},\"frame\":{1}," +
                "\"raw\":\"0x{2:X8}\",\"receivedTimestamp\":{3}," +
                "\"intervalMs\":{4}}}",
                arrival.ReceiveSequence,
                arrival.RemoteFrameID,
                arrival.Raw,
                arrival.ReceivedTimestamp,
                intervalMs.ToString("F3", CultureInfo.InvariantCulture)));
        }

        public void RecordDrain(
            long observedTimestamp,
            int count,
            long firstReceivedTimestamp,
            long lastReceivedTimestamp)
        {
            if (!_enabled)
                return;
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            double queueSpanMs = count > 1
                ? ToMilliseconds(lastReceivedTimestamp - firstReceivedTimestamp)
                : 0.0;
            double newestAgeMs = count > 0
                ? ToMilliseconds(observedTimestamp - lastReceivedTimestamp)
                : 0.0;
            Emit(string.Format(
                CultureInfo.InvariantCulture,
                "{{\"type\":\"drain\",\"observedTimestamp\":{0}," +
                "\"count\":{1},\"firstReceivedTimestamp\":{2}," +
                "\"lastReceivedTimestamp\":{3},\"queueSpanMs\":{4}," +
                "\"newestAgeMs\":{5}}}",
                observedTimestamp,
                count,
                firstReceivedTimestamp,
                lastReceivedTimestamp,
                queueSpanMs.ToString("F3", CultureInfo.InvariantCulture),
                newestAgeMs.ToString("F3", CultureInfo.InvariantCulture)));
        }

        public void RecordLogic(
            long observedTimestamp,
            int confirmedBefore,
            int confirmedAfter,
            int predictedFrame,
            int viewFrame)
        {
            if (!_enabled)
                return;

            Emit(string.Format(
                CultureInfo.InvariantCulture,
                "{{\"type\":\"logic\",\"observedTimestamp\":{0}," +
                "\"confirmedBefore\":{1},\"confirmedAfter\":{2}," +
                "\"confirmedDelta\":{3},\"predictedFrame\":{4}," +
                "\"viewFrame\":{5}}}",
                observedTimestamp,
                confirmedBefore,
                confirmedAfter,
                confirmedAfter - confirmedBefore,
                predictedFrame,
                viewFrame));
        }

        public void RecordRollback(
            long observedTimestamp,
            int mismatchFrame,
            int restoredFrame,
            int replayedFrames,
            ulong confirmedHash)
        {
            if (!_enabled)
                return;

            Emit(string.Format(
                CultureInfo.InvariantCulture,
                "{{\"type\":\"rollback\",\"observedTimestamp\":{0}," +
                "\"mismatchFrame\":{1},\"restoredFrame\":{2}," +
                "\"replayedFrames\":{3}," +
                "\"confirmedHash\":\"0x{4:X16}\"}}",
                observedTimestamp,
                mismatchFrame,
                restoredFrame,
                replayedFrames,
                confirmedHash));
        }

        public void RecordRemoteRender(
            long observedTimestamp,
            float x,
            float z,
            int predictedFrame,
            int confirmedFrame,
            int viewFrame)
        {
            if (!_enabled)
                return;

            float displacement = 0f;
            float reverseDisplacement = 0f;
            double stationaryMs = 0.0;
            if (!_hasRemotePosition)
            {
                _hasRemotePosition = true;
                _lastMotionTimestamp = observedTimestamp;
            }
            else
            {
                float deltaX = x - _lastRemoteX;
                float deltaZ = z - _lastRemoteZ;
                displacement = (float)Math.Sqrt(
                    deltaX * deltaX + deltaZ * deltaZ);
                if (displacement > StationaryEpsilon)
                {
                    float dot = deltaX * _lastMotionX + deltaZ * _lastMotionZ;
                    if (dot < 0f)
                        reverseDisplacement = displacement;

                    _lastMotionX = deltaX;
                    _lastMotionZ = deltaZ;
                    _lastMotionTimestamp = observedTimestamp;
                }
                else
                {
                    stationaryMs = ToMilliseconds(
                        observedTimestamp - _lastMotionTimestamp);
                }
            }

            _lastRemoteX = x;
            _lastRemoteZ = z;
            Emit(string.Format(
                CultureInfo.InvariantCulture,
                "{{\"type\":\"remoteRender\",\"observedTimestamp\":{0}," +
                "\"x\":{1:F6},\"z\":{2:F6},\"displacement\":{3:F6}," +
                "\"reverseDisplacement\":{4:F6},\"stationaryMs\":{5:F3}," +
                "\"predictedFrame\":{6},\"confirmedFrame\":{7}," +
                "\"viewFrame\":{8}}}",
                observedTimestamp,
                x,
                z,
                displacement,
                reverseDisplacement,
                stationaryMs,
                predictedFrame,
                confirmedFrame,
                viewFrame));
        }

        private double ToMilliseconds(long timestampDelta)
        {
            return timestampDelta * 1000.0 / _timestampFrequency;
        }

        private double UpdatePhaseDuration(
            long observedTimestamp,
            bool isActive,
            ref bool wasActive,
            ref long startTimestamp)
        {
            if (!isActive)
            {
                wasActive = false;
                startTimestamp = 0;
                return 0.0;
            }

            if (!wasActive)
                startTimestamp = observedTimestamp;
            wasActive = true;
            return ToMilliseconds(observedTimestamp - startTimestamp);
        }

        private void PruneActualArrivalsBefore(int firstPossibleFrame)
        {
            while (_actualArrivalTimestamps.Count > 0)
            {
                int oldestFrame = GetOldestActualArrivalFrame();
                if (oldestFrame >= firstPossibleFrame)
                    return;

                _actualArrivalTimestamps.Remove(oldestFrame);
            }
        }

        private void RemoveOldestActualArrival()
        {
            _actualArrivalTimestamps.Remove(
                GetOldestActualArrivalFrame());
        }

        private int GetOldestActualArrivalFrame()
        {
            using (SortedDictionary<int, long>.Enumerator enumerator =
                _actualArrivalTimestamps.GetEnumerator())
            {
                if (!enumerator.MoveNext())
                {
                    throw new InvalidOperationException(
                        "Actual-arrival diagnostics cache is empty.");
                }

                return enumerator.Current.Key;
            }
        }

        private static string ToJsonBoolean(bool value)
        {
            return value ? "true" : "false";
        }

        private void Emit(string line)
        {
            _sink("[RouteC][P2E] " + line);
        }
    }
}

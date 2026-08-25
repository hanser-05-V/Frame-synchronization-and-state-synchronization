using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using FrameSyncDemo;

namespace FrameSyncServer
{
    public sealed class KcpServerDiagnostics
    {
        private const int MaximumTimingSamplesPerPlayer = 65536;
        private readonly long[] _dropCounts = new long[byte.MaxValue + 1];
        private int _workerFault;
        private string _workerFaultCategory = string.Empty;
        private long _receiveBudgetExhaustionCount;
        private readonly long[] _messageBudgetExhaustionCounts = new long[2];
        private long _liveSliceBudgetExhaustionCount;
        private int _lastTickFirstProcessedPlayerIndex = -1;
        private int _lastTickSecondProcessedPlayerIndex = -1;
        private long _resumeWrongSessionCount;
        private long _resumeWrongGenerationCount;
        private long _resumeWrongEndpointCount;
        private long _resumeWrongAttemptCount;
        private long _resumeWrongDirectionCount;
        private long _resumeInvalidPayloadCount;
        private readonly long[] _resumeFailureCounts =
            new long[byte.MaxValue + 1];
        private readonly object _timingGate = new object();
        private readonly List<long>[] _actualUpdateGapTicks =
        {
            new List<long>(),
            new List<long>()
        };
        private readonly List<long>[] _wakeupErrorTicks =
        {
            new List<long>(),
            new List<long>()
        };
        private readonly List<int>[] _effectiveIntervalMsSamples =
        {
            new List<int>(),
            new List<int>()
        };
        private readonly RouteCKcpDiagnosticsSnapshot[] _latestKcpDiagnostics =
            new RouteCKcpDiagnosticsSnapshot[2];
        private readonly int[] _coreIntervalMs = { -1, -1 };
        private long _totalDropCount;
        private long _workerCpuTimeTicks;
        private int _workerCpuMeasurementAvailable;

        public long TotalDropCount => Interlocked.Read(ref _totalDropCount);
        public bool WorkerFault => Volatile.Read(ref _workerFault) != 0;
        public string WorkerFaultCategory => _workerFaultCategory;
        public long ReceiveBudgetExhaustionCount =>
            Interlocked.Read(ref _receiveBudgetExhaustionCount);
        public long LiveSliceBudgetExhaustionCount =>
            Interlocked.Read(ref _liveSliceBudgetExhaustionCount);
        public int LastTickFirstProcessedPlayerIndex =>
            Volatile.Read(ref _lastTickFirstProcessedPlayerIndex);
        public int LastTickSecondProcessedPlayerIndex =>
            Volatile.Read(ref _lastTickSecondProcessedPlayerIndex);
        public long ResumeWrongSessionCount =>
            Interlocked.Read(ref _resumeWrongSessionCount);
        public long ResumeWrongGenerationCount =>
            Interlocked.Read(ref _resumeWrongGenerationCount);
        public long ResumeWrongEndpointCount =>
            Interlocked.Read(ref _resumeWrongEndpointCount);
        public long ResumeWrongAttemptCount =>
            Interlocked.Read(ref _resumeWrongAttemptCount);
        public long ResumeWrongDirectionCount =>
            Interlocked.Read(ref _resumeWrongDirectionCount);
        public long ResumeInvalidPayloadCount =>
            Interlocked.Read(ref _resumeInvalidPayloadCount);
        public long WorkerCpuTimeTicks =>
            Interlocked.Read(ref _workerCpuTimeTicks);
        public bool WorkerCpuMeasurementAvailable =>
            Volatile.Read(ref _workerCpuMeasurementAvailable) != 0;

        public long[] GetActualUpdateGapTicks(byte playerIndex)
        {
            ValidatePlayerIndex(playerIndex);
            lock (_timingGate)
            {
                return _actualUpdateGapTicks[playerIndex].ToArray();
            }
        }

        public long[] GetWakeupErrorTicks(byte playerIndex)
        {
            ValidatePlayerIndex(playerIndex);
            lock (_timingGate)
            {
                return _wakeupErrorTicks[playerIndex].ToArray();
            }
        }

        public int[] GetEffectiveIntervalMsSamples(byte playerIndex)
        {
            ValidatePlayerIndex(playerIndex);
            lock (_timingGate)
            {
                return _effectiveIntervalMsSamples[playerIndex].ToArray();
            }
        }

        public RouteCKcpDiagnosticsSnapshot GetLatestKcpDiagnostics(
            byte playerIndex)
        {
            ValidatePlayerIndex(playerIndex);
            lock (_timingGate)
            {
                return _latestKcpDiagnostics[playerIndex];
            }
        }

        public int GetCoreIntervalMs(byte playerIndex)
        {
            ValidatePlayerIndex(playerIndex);
            lock (_timingGate)
            {
                return _coreIntervalMs[playerIndex];
            }
        }

        public string ToMeasurementJson()
        {
            var builder = new StringBuilder(4096);
            builder.Append(
                "{\"schema\":\"route-c-kcp-server-diagnostics-v1\"," +
                "\"workerCpuMeasurementAvailable\":");
            builder.Append(
                WorkerCpuMeasurementAvailable ? "true" : "false");
            builder.Append(",\"workerFault\":");
            builder.Append(WorkerFault ? "true" : "false");
            builder.Append(",\"workerFaultCategory\":\"");
            builder.Append(WorkerFaultCategory);
            builder.Append('\"');
            builder.Append(",\"workerCpuTimeTicks\":");
            builder.Append(
                WorkerCpuTimeTicks.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"players\":[");
            lock (_timingGate)
            {
                for (int playerIndex = 0; playerIndex < 2; playerIndex++)
                {
                    if (playerIndex > 0)
                    {
                        builder.Append(',');
                    }
                    RouteCKcpDiagnosticsSnapshot latest =
                        _latestKcpDiagnostics[playerIndex];
                    builder.Append("{\"playerIndex\":");
                    builder.Append(playerIndex);
                    builder.Append(",\"coreEffectiveIntervalMs\":");
                    builder.Append(_coreIntervalMs[playerIndex]);
                    builder.Append(",\"requestedIntervalMs\":");
                    builder.Append(latest == null
                        ? -1
                        : latest.RequestedIntervalMs);
                    builder.Append(",\"effectiveCheckDeltaMs\":");
                    builder.Append(latest == null
                        ? -1
                        : latest.EffectiveCheckDeltaMs);
                    builder.Append(",\"kcpUpdateCalls\":");
                    builder.Append(latest == null ? 0L : latest.UpdateCount);
                    builder.Append(",\"kcpOutputDatagrams\":");
                    builder.Append(latest == null
                        ? 0L
                        : latest.OutputDatagramCount);
                    builder.Append(",\"kcpOutputPayloadBytes\":");
                    builder.Append(latest == null
                        ? 0L
                        : latest.OutputByteCount);
                    builder.Append(",\"waitSndHighWater\":");
                    builder.Append(latest == null
                        ? 0
                        : latest.WaitSndHighWater);
                    builder.Append(",\"actualUpdateGapTicks\":");
                    AppendLongArray(
                        builder,
                        _actualUpdateGapTicks[playerIndex]);
                    builder.Append(",\"wakeupErrorTicks\":");
                    AppendLongArray(
                        builder,
                        _wakeupErrorTicks[playerIndex]);
                    builder.Append(",\"effectiveCheckDeltaMsSamples\":");
                    AppendIntArray(
                        builder,
                        _effectiveIntervalMsSamples[playerIndex]);
                    builder.Append('}');
                }
            }
            builder.Append("]}");
            return builder.ToString();
        }

        public long GetMessageBudgetExhaustionCount(byte playerIndex)
        {
            if (playerIndex > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(playerIndex));
            }
            return Interlocked.Read(
                ref _messageBudgetExhaustionCounts[playerIndex]);
        }

        public long GetResumeFailureCount(ServerResumeFailureKind kind)
        {
            return Interlocked.Read(ref _resumeFailureCounts[(byte)kind]);
        }

        public long GetDropCount(RouteCProtocolDropReason reason)
        {
            return Interlocked.Read(ref _dropCounts[(byte)reason]);
        }

        internal void RecordDrop(RouteCProtocolDropReason reason)
        {
            if (reason == RouteCProtocolDropReason.None)
            {
                throw new ArgumentOutOfRangeException(nameof(reason));
            }

            Interlocked.Increment(ref _dropCounts[(byte)reason]);
            Interlocked.Increment(ref _totalDropCount);
        }

        internal void RecordWorkerFault(string category)
        {
            _workerFaultCategory =
                string.IsNullOrEmpty(category) ? "Unknown" : category;
            Interlocked.Exchange(ref _workerFault, 1);
        }

        internal void RecordReceiveBudgetExhaustion()
        {
            Interlocked.Increment(ref _receiveBudgetExhaustionCount);
        }

        internal void RecordMessageBudgetExhaustion(byte playerIndex)
        {
            Interlocked.Increment(
                ref _messageBudgetExhaustionCounts[playerIndex]);
        }

        internal void RecordLiveSliceBudgetExhaustion()
        {
            Interlocked.Increment(ref _liveSliceBudgetExhaustionCount);
        }

        internal void BeginTick()
        {
            Volatile.Write(ref _lastTickFirstProcessedPlayerIndex, -1);
            Volatile.Write(ref _lastTickSecondProcessedPlayerIndex, -1);
        }

        internal void RecordPlayerProcessed(byte playerIndex)
        {
            if (Volatile.Read(ref _lastTickFirstProcessedPlayerIndex) < 0)
            {
                Volatile.Write(
                    ref _lastTickFirstProcessedPlayerIndex,
                    playerIndex);
                return;
            }
            if (Volatile.Read(ref _lastTickSecondProcessedPlayerIndex) < 0)
            {
                Volatile.Write(
                    ref _lastTickSecondProcessedPlayerIndex,
                    playerIndex);
            }
        }

        internal void RecordResumeWrongSession()
        {
            Interlocked.Increment(ref _resumeWrongSessionCount);
        }

        internal void RecordResumeWrongGeneration()
        {
            Interlocked.Increment(ref _resumeWrongGenerationCount);
        }

        internal void RecordResumeWrongEndpoint()
        {
            Interlocked.Increment(ref _resumeWrongEndpointCount);
        }

        internal void RecordResumeWrongAttempt()
        {
            Interlocked.Increment(ref _resumeWrongAttemptCount);
        }

        internal void RecordResumeWrongDirection()
        {
            Interlocked.Increment(ref _resumeWrongDirectionCount);
        }

        internal void RecordResumeInvalidPayload()
        {
            Interlocked.Increment(ref _resumeInvalidPayloadCount);
        }

        internal void RecordResumeFailure(ServerResumeFailureKind kind)
        {
            Interlocked.Increment(ref _resumeFailureCounts[(byte)kind]);
        }

        internal void RecordKcpTimingSample(
            byte playerIndex,
            RouteCKcpDiagnosticsSnapshot snapshot)
        {
            ValidatePlayerIndex(playerIndex);
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            lock (_timingGate)
            {
                _latestKcpDiagnostics[playerIndex] = snapshot;
                if (_effectiveIntervalMsSamples[playerIndex].Count <
                    MaximumTimingSamplesPerPlayer)
                {
                    _effectiveIntervalMsSamples[playerIndex].Add(
                        snapshot.EffectiveCheckDeltaMs);
                }
                if (snapshot.UpdateCount > 1)
                {
                    if (_actualUpdateGapTicks[playerIndex].Count <
                        MaximumTimingSamplesPerPlayer)
                    {
                        _actualUpdateGapTicks[playerIndex].Add(
                            snapshot.LastActualUpdateGapTicks);
                        _wakeupErrorTicks[playerIndex].Add(
                            snapshot.LastWakeupErrorTicks);
                    }
                }
            }
        }

        internal void RecordKcpCoreInterval(
            byte playerIndex,
            int coreIntervalMs)
        {
            ValidatePlayerIndex(playerIndex);
            if (coreIntervalMs <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(coreIntervalMs));
            }
            lock (_timingGate)
            {
                _coreIntervalMs[playerIndex] = coreIntervalMs;
            }
        }

        internal void RecordWorkerCpuTime(
            long cpuTimeTicks,
            bool measurementAvailable)
        {
            if (cpuTimeTicks < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cpuTimeTicks));
            }

            Interlocked.Exchange(ref _workerCpuTimeTicks, cpuTimeTicks);
            Volatile.Write(
                ref _workerCpuMeasurementAvailable,
                measurementAvailable ? 1 : 0);
        }

        private static void ValidatePlayerIndex(byte playerIndex)
        {
            if (playerIndex > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(playerIndex));
            }
        }

        private static void AppendLongArray(
            StringBuilder builder,
            List<long> values)
        {
            builder.Append('[');
            for (int index = 0; index < values.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append(values[index].ToString(
                    CultureInfo.InvariantCulture));
            }
            builder.Append(']');
        }

        private static void AppendIntArray(
            StringBuilder builder,
            List<int> values)
        {
            builder.Append('[');
            for (int index = 0; index < values.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append(values[index].ToString(
                    CultureInfo.InvariantCulture));
            }
            builder.Append(']');
        }
    }
}

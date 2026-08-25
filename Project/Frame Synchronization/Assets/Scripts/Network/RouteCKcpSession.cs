using System;
using System.Diagnostics;
using System.Threading;
using kcp2k;

namespace FrameSyncDemo
{
    public sealed class RouteCKcpSession
    {
        private readonly Kcp _kcp;
        private readonly int _ownerThreadId;
        private readonly Action<byte[], int> _output;
        private readonly int _requestedIntervalMs;
        private int _effectiveCheckDeltaMs;
        private bool _hasPreviousUpdateTimestamp;
        private long _previousUpdateTimestamp;
        private long _expectedUpdateGapTicks;
        private long _timingSampleCount;
        private long _actualUpdateGapSumTicks;
        private long _lastActualUpdateGapTicks;
        private long _maxActualUpdateGapTicks;
        private long _wakeupErrorSumTicks;
        private long _lastWakeupErrorTicks;
        private long _maxWakeupErrorTicks;
        private long _updateCount;
        private long _outputDatagramCount;
        private long _outputByteCount;
        private long _inputDatagramCount;
        private long _inputByteCount;
        private long _receiveCount;
        private int _waitSndHighWater;
        private long _inputErrorCount;

        public RouteCKcpSession(
            uint conv,
            RouteCKcpSettings settings,
            Action<byte[], int> output)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (output == null)
                throw new ArgumentNullException(nameof(output));

            Conversation = conv;
            _requestedIntervalMs = settings.IntervalMs;
            _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
            _output = output;
            _kcp = new Kcp(conv, HandleOutput);
            settings.Configure(_kcp);
        }

        public uint Conversation { get; }

        public int RequestedIntervalMs => _requestedIntervalMs;

        public int PendingSendCount
        {
            get
            {
                AssertOwnerThread();
                return _kcp.WaitSnd;
            }
        }

        public bool HasPendingBusinessInput
        {
            get
            {
                AssertOwnerThread();
                return _kcp.PeekSize() >= 0;
            }
        }

        public uint NextUpdateAt(uint nowMs)
        {
            AssertOwnerThread();
            return _kcp.Check(nowMs);
        }

        public int SendBusinessInput(uint raw, int frameID)
        {
            AssertOwnerThread();
            byte[] payload = RouteCProtocolCodec.EncodeBusinessInput(raw, frameID);
            int result = _kcp.Send(payload, 0, payload.Length);
            RecordWaitSndHighWater();
            return result;
        }

        public int InputDatagram(byte[] payload, int offset, int count)
        {
            AssertOwnerThread();
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));
            if (offset < 0 || count < 0 || offset > payload.Length - count)
                throw new ArgumentOutOfRangeException(nameof(offset));

            _inputDatagramCount++;
            _inputByteCount += count;
            int result = _kcp.Input(payload, offset, count);
            if (result != 0)
                _inputErrorCount++;
            RecordWaitSndHighWater();
            return result;
        }

        public bool TryReceiveBusinessInput(out uint raw, out int frameID)
        {
            AssertOwnerThread();
            int size = _kcp.PeekSize();
            if (size < 0)
            {
                raw = 0u;
                frameID = 0;
                return false;
            }

            var payload = new byte[size];
            int received = _kcp.Receive(payload, payload.Length);
            if (received >= 0)
                _receiveCount++;
            if (received != payload.Length ||
                !RouteCProtocolCodec.TryDecodeBusinessInput(
                    payload,
                    out raw,
                    out frameID))
            {
                raw = 0u;
                frameID = 0;
                return false;
            }

            return true;
        }

        public void Update(uint nowMs, long workerTimestamp)
        {
            AssertOwnerThread();
            RecordTiming(workerTimestamp);
            _updateCount++;
            _kcp.Update(nowMs);
            uint nextUpdateAt = _kcp.Check(nowMs);
            _effectiveCheckDeltaMs = unchecked((int)(nextUpdateAt - nowMs));
            _expectedUpdateGapTicks = MillisecondsToTicks(_effectiveCheckDeltaMs);
            RecordWaitSndHighWater();
        }

        public RouteCKcpDiagnosticsSnapshot SnapshotDiagnostics()
        {
            AssertOwnerThread();
            return new RouteCKcpDiagnosticsSnapshot(
                _requestedIntervalMs,
                _effectiveCheckDeltaMs,
                _lastActualUpdateGapTicks,
                Mean(_actualUpdateGapSumTicks, _timingSampleCount),
                _maxActualUpdateGapTicks,
                _lastWakeupErrorTicks,
                Mean(_wakeupErrorSumTicks, _timingSampleCount),
                _maxWakeupErrorTicks,
                _updateCount,
                _outputDatagramCount,
                _outputByteCount,
                _inputDatagramCount,
                _inputByteCount,
                _receiveCount,
                _waitSndHighWater,
                _inputErrorCount);
        }

        private void HandleOutput(byte[] bytes, int count)
        {
            var copy = new byte[count];
            Buffer.BlockCopy(bytes, 0, copy, 0, count);
            _outputDatagramCount++;
            _outputByteCount += count;
            _output(copy, count);
        }

        private void RecordTiming(long workerTimestamp)
        {
            if (_hasPreviousUpdateTimestamp)
            {
                long actualGapTicks = workerTimestamp - _previousUpdateTimestamp;
                if (actualGapTicks >= 0)
                {
                    long wakeupErrorTicks = actualGapTicks - _expectedUpdateGapTicks;
                    _timingSampleCount++;
                    _actualUpdateGapSumTicks += actualGapTicks;
                    _lastActualUpdateGapTicks = actualGapTicks;
                    if (actualGapTicks > _maxActualUpdateGapTicks)
                        _maxActualUpdateGapTicks = actualGapTicks;
                    _wakeupErrorSumTicks += wakeupErrorTicks;
                    _lastWakeupErrorTicks = wakeupErrorTicks;
                    if (_timingSampleCount == 1 ||
                        wakeupErrorTicks > _maxWakeupErrorTicks)
                    {
                        _maxWakeupErrorTicks = wakeupErrorTicks;
                    }
                }
            }

            _hasPreviousUpdateTimestamp = true;
            _previousUpdateTimestamp = workerTimestamp;
        }

        private void RecordWaitSndHighWater()
        {
            if (_kcp.WaitSnd > _waitSndHighWater)
                _waitSndHighWater = _kcp.WaitSnd;
        }

        private static long MillisecondsToTicks(int milliseconds)
        {
            return milliseconds * Stopwatch.Frequency / 1000L;
        }

        private static long Mean(long sum, long count)
        {
            return count == 0 ? 0L : sum / count;
        }

        private void AssertOwnerThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
            {
                throw new InvalidOperationException(
                    "Route C KCP sessions may only be used by their creating worker thread.");
            }
        }
    }
}

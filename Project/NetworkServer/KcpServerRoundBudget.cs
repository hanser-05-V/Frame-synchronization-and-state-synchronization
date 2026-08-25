using System;
using System.Diagnostics;
using FrameSyncDemo;

namespace FrameSyncServer
{
    public sealed class KcpServerRoundBudget
    {
        private readonly KcpServerDiagnostics _diagnostics;
        private readonly long _sliceStartTimestamp;
        private readonly int[] _messageCounts = new int[2];
        private readonly bool[] _messageExhaustionRecorded = new bool[2];
        private int _datagramCount;
        private bool _receiveExhaustionRecorded;
        private bool _sliceExhaustionRecorded;

        public KcpServerRoundBudget(
            KcpServerDiagnostics diagnostics,
            long sliceStartTimestamp)
        {
            _diagnostics = diagnostics ??
                throw new ArgumentNullException(nameof(diagnostics));
            _sliceStartTimestamp = sliceStartTimestamp;
        }

        public bool TryTakeDatagram()
        {
            if (_datagramCount <
                RouteCProtocolConstants.MaximumDatagramsPerRound)
            {
                _datagramCount++;
                return true;
            }

            if (!_receiveExhaustionRecorded)
            {
                _receiveExhaustionRecorded = true;
                _diagnostics.RecordReceiveBudgetExhaustion();
            }
            return false;
        }

        public bool TryTakeMessage(byte playerIndex)
        {
            if (playerIndex > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(playerIndex));
            }
            if (_messageCounts[playerIndex] <
                RouteCProtocolConstants.MaximumMessagesPerSessionPerRound)
            {
                _messageCounts[playerIndex]++;
                return true;
            }

            if (!_messageExhaustionRecorded[playerIndex])
            {
                _messageExhaustionRecorded[playerIndex] = true;
                _diagnostics.RecordMessageBudgetExhaustion(playerIndex);
            }
            return false;
        }

        public bool HasLiveSliceBudget(long nowTimestamp)
        {
            long budgetTicks =
                RouteCProtocolConstants.MaximumWorkerSliceMs *
                Stopwatch.Frequency /
                1000L;
            if (nowTimestamp - _sliceStartTimestamp < budgetTicks)
            {
                return true;
            }

            if (!_sliceExhaustionRecorded)
            {
                _sliceExhaustionRecorded = true;
                _diagnostics.RecordLiveSliceBudgetExhaustion();
            }
            return false;
        }
    }
}

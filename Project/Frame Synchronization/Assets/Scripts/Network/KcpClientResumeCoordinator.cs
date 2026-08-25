using System;

namespace FrameSyncDemo
{
    public sealed class KcpClientResumeCoordinator
    {
        private const int RecentRemoteFrameCapacity =
            RouteCProtocolConstants.InputHistoryCapacity;

        private readonly OutboundActualHistory _history;
        private readonly int[] _recentRemoteFrames =
            new int[RecentRemoteFrameCapacity];
        private KcpClientResumePlan _plan;
        private byte[][] _pendingUploadPayloads = Array.Empty<byte[]>();
        private int _nextUploadPayloadIndex;
        private bool _uploadQueued;
        private int _nextExpectedRemoteFrame;
        private int _recentRemoteFrameStart;
        private int _recentRemoteFrameCount;

        public KcpClientResumeCoordinator(OutboundActualHistory history)
        {
            _history = history ?? throw new ArgumentNullException(nameof(history));
            UploadedLocalThrough = -1;
            ReceivedRemoteThrough = -1;
        }

        public bool IsActive => _plan != null;

        public int UploadedLocalThrough { get; private set; }

        public int ReceivedRemoteThrough { get; private set; }

        public int PendingUploadCount =>
            _pendingUploadPayloads.Length - _nextUploadPayloadIndex;

        public bool IsCompleteReady =>
            IsActive &&
            _uploadQueued &&
            ReceivedRemoteThrough >= _plan.ExpectedRemoteThrough;

        public bool TryBegin(
            KcpClientResumePlan plan,
            out byte[][] uploadPayloads)
        {
            if (plan == null)
                throw new ArgumentNullException(nameof(plan));
            uploadPayloads = Array.Empty<byte[]>();
            if (IsActive)
            {
                return false;
            }

            if (!plan.IsPeer &&
                plan.UploadFrom <= plan.UploadThrough &&
                !_history.TryCopyRange(
                    plan.UploadFrom,
                    plan.UploadThrough,
                    out uploadPayloads))
            {
                return false;
            }
            if (plan.IsPeer || plan.UploadFrom > plan.UploadThrough)
                uploadPayloads = Array.Empty<byte[]>();
            if (!_history.TryBeginFreeze(plan.LocalFrozenThrough))
            {
                uploadPayloads = Array.Empty<byte[]>();
                return false;
            }

            _plan = plan;
            _pendingUploadPayloads = uploadPayloads;
            _nextUploadPayloadIndex = 0;
            _uploadQueued = plan.IsPeer ||
                plan.UploadFrom > plan.UploadThrough;
            UploadedLocalThrough = _uploadQueued
                ? plan.RequiredUploadedThrough
                : plan.UploadFrom - 1;
            _nextExpectedRemoteFrame = plan.ExpectedRemoteFrom;
            ReceivedRemoteThrough = plan.ExpectedRemoteFrom <=
                plan.ExpectedRemoteThrough
                ? plan.ExpectedRemoteFrom - 1
                : plan.ExpectedRemoteThrough;
            ApplyRecentRemoteFrames();
            return true;
        }

        public void MarkUploadQueued()
        {
            if (!IsActive)
                throw new InvalidOperationException("Resume is not active.");
            if (PendingUploadCount != 0)
            {
                throw new InvalidOperationException(
                    "Resume upload still has pending payloads.");
            }

            _uploadQueued = true;
            UploadedLocalThrough = _plan.RequiredUploadedThrough;
        }

        public bool TryTakeNextUploadPayload(out byte[] payload)
        {
            if (!IsActive || PendingUploadCount == 0)
            {
                payload = null;
                return false;
            }

            byte[] source = _pendingUploadPayloads[_nextUploadPayloadIndex++];
            payload = new byte[source.Length];
            Buffer.BlockCopy(source, 0, payload, 0, source.Length);
            return true;
        }

        public void ObserveRemoteFrame(int frameID)
        {
            if (!IsActive)
            {
                RememberRemoteFrame(frameID);
                return;
            }

            AdvanceRemoteFrame(frameID);
        }

        private void AdvanceRemoteFrame(int frameID)
        {
            if (_nextExpectedRemoteFrame > _plan.ExpectedRemoteThrough ||
                frameID != _nextExpectedRemoteFrame)
                return;

            ReceivedRemoteThrough = frameID;
            _nextExpectedRemoteFrame++;
        }

        private void RememberRemoteFrame(int frameID)
        {
            int writeIndex = (_recentRemoteFrameStart +
                _recentRemoteFrameCount) % RecentRemoteFrameCapacity;
            if (_recentRemoteFrameCount == RecentRemoteFrameCapacity)
            {
                _recentRemoteFrameStart =
                    (_recentRemoteFrameStart + 1) % RecentRemoteFrameCapacity;
                writeIndex = (_recentRemoteFrameStart +
                    _recentRemoteFrameCount - 1) % RecentRemoteFrameCapacity;
            }
            else
            {
                _recentRemoteFrameCount++;
            }
            _recentRemoteFrames[writeIndex] = frameID;
        }

        private void ApplyRecentRemoteFrames()
        {
            for (int index = 0; index < _recentRemoteFrameCount; index++)
            {
                int readIndex = (_recentRemoteFrameStart + index) %
                    RecentRemoteFrameCapacity;
                AdvanceRemoteFrame(_recentRemoteFrames[readIndex]);
            }
            _recentRemoteFrameStart = 0;
            _recentRemoteFrameCount = 0;
        }

        public bool TryReleaseLocalTail(out byte[][] payloads)
        {
            payloads = Array.Empty<byte[]>();
            if (!IsActive || !_history.TryReleaseLiveTail(out payloads))
                return false;

            _plan = null;
            _pendingUploadPayloads = Array.Empty<byte[]>();
            _nextUploadPayloadIndex = 0;
            _uploadQueued = false;
            _nextExpectedRemoteFrame = 0;
            return true;
        }
    }
}

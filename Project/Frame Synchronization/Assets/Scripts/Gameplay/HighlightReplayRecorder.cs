using System;
using System.Collections.Generic;

namespace FrameSyncDemo
{
    public sealed class HighlightReplayRecorder
    {
        public const int PreRollFrames = 60;
        public const int PostRollFrames = 45;
        public const int MaximumClipCount = 3;
        public const int HistoryCapacity = 512;

        private readonly Dictionary<int, FrameSnapshot> _history =
            new Dictionary<int, FrameSnapshot>(HistoryCapacity);
        private readonly Queue<PendingClip> _pendingClips =
            new Queue<PendingClip>();
        private readonly List<HighlightClip> _clips =
            new List<HighlightClip>(MaximumClipCount);

        private int _lastProcessedFrame = -1;
        private bool _hasPreviousSnapshot;
        private FrameSnapshot _previousSnapshot;
        private int _activeShotFrame = -1;
        private int _activeShooterPlayerIndex = -1;

        public IReadOnlyList<HighlightClip> Clips => _clips;
        public int LastProcessedFrame => _lastProcessedFrame;
        public bool EndMatchRequested { get; private set; }
        public int EndMatchRequestFrame { get; private set; } = -1;
        public int PostGameTerminalFrame { get; private set; } = -1;
        public bool CanEnterPostGame => PostGameTerminalFrame >= 0;
        public string LastCaptureError { get; private set; } = string.Empty;

        public void ProcessStableFrame(
            int frameID,
            FrameSnapshot snapshot,
            FrameInput[] actualInputs)
        {
            if (frameID != _lastProcessedFrame + 1)
            {
                throw new InvalidOperationException(
                    $"Stable highlight frames must be consecutive. " +
                    $"Expected {_lastProcessedFrame + 1}, got {frameID}.");
            }
            if (snapshot.frameID != frameID)
                throw new ArgumentException("Snapshot frame does not match frameID.", nameof(snapshot));
            if (actualInputs == null || actualInputs.Length != 2)
                throw new ArgumentException("Exactly two actual inputs are required.", nameof(actualInputs));

            _lastProcessedFrame = frameID;
            _history[frameID] = snapshot;
            int evictedFrame = frameID - HistoryCapacity;
            if (evictedFrame >= 0)
                _history.Remove(evictedFrame);

            DetectEndMatch(frameID, actualInputs);
            DetectHighlightTransition(frameID, snapshot);
            FinalizeReadyClips(frameID);
            PublishPostGameTerminalFrame(frameID);

            _previousSnapshot = snapshot;
            _hasPreviousSnapshot = true;
        }

        public void RebaseAt(int nextFrameID)
        {
            if (nextFrameID < 0)
                throw new ArgumentOutOfRangeException(nameof(nextFrameID));
            if (nextFrameID <= _lastProcessedFrame + 1)
            {
                throw new InvalidOperationException(
                    "Highlight recorder rebase must move forward.");
            }

            _history.Clear();
            _pendingClips.Clear();
            _lastProcessedFrame = nextFrameID - 1;
            _hasPreviousSnapshot = false;
            _previousSnapshot = default;
            _activeShotFrame = -1;
            _activeShooterPlayerIndex = -1;
            PostGameTerminalFrame = -1;
            LastCaptureError =
                $"Highlight stable history expired; resumed at frame {nextFrameID}.";
        }

        private void DetectEndMatch(int frameID, FrameInput[] actualInputs)
        {
            if (EndMatchRequested)
                return;

            if (!actualInputs[0].endMatchPressed &&
                !actualInputs[1].endMatchPressed)
            {
                return;
            }

            EndMatchRequested = true;
            EndMatchRequestFrame = frameID;
        }

        private void DetectHighlightTransition(
            int frameID,
            FrameSnapshot currentSnapshot)
        {
            if (!_hasPreviousSnapshot)
                return;

            BallEntity.EState previousState =
                (BallEntity.EState)_previousSnapshot.ballState;
            BallEntity.EState currentState =
                (BallEntity.EState)currentSnapshot.ballState;

            if (previousState == BallEntity.EState.Held &&
                currentState == BallEntity.EState.Airborne)
            {
                _activeShotFrame = frameID;
                _activeShooterPlayerIndex = _previousSnapshot.ballHolder;
                return;
            }

            if (previousState == BallEntity.EState.Airborne &&
                currentState == BallEntity.EState.Scored)
            {
                if (_activeShotFrame >= 0)
                {
                    _pendingClips.Enqueue(new PendingClip
                    {
                        startFrame = Math.Max(0, _activeShotFrame - PreRollFrames),
                        shotFrame = _activeShotFrame,
                        scoreFrame = frameID,
                        endFrame = frameID + PostRollFrames,
                        shooterPlayerIndex = _activeShooterPlayerIndex
                    });
                }

                _activeShotFrame = -1;
                _activeShooterPlayerIndex = -1;
                return;
            }

            if (previousState == BallEntity.EState.Airborne &&
                currentState == BallEntity.EState.Free)
            {
                _activeShotFrame = -1;
                _activeShooterPlayerIndex = -1;
            }
        }

        private void FinalizeReadyClips(int stableFrame)
        {
            while (_pendingClips.Count > 0 &&
                   _pendingClips.Peek().endFrame <= stableFrame)
            {
                PendingClip pending = _pendingClips.Dequeue();
                int frameCount = pending.endFrame - pending.startFrame + 1;
                var frames = new FrameSnapshot[frameCount];
                bool complete = true;
                for (int i = 0; i < frameCount; i++)
                {
                    int frameID = pending.startFrame + i;
                    if (!_history.TryGetValue(frameID, out frames[i]))
                    {
                        LastCaptureError =
                            $"Highlight snapshot missing at frame {frameID} " +
                            $"for range [{pending.startFrame}, {pending.endFrame}].";
                        complete = false;
                        break;
                    }
                }

                if (!complete)
                    continue;

                if (_clips.Count == MaximumClipCount)
                    _clips.RemoveAt(0);

                _clips.Add(new HighlightClip(
                    pending.startFrame,
                    pending.shotFrame,
                    pending.scoreFrame,
                    pending.endFrame,
                    pending.shooterPlayerIndex,
                    frames));
            }
        }

        private void PublishPostGameTerminalFrame(int stableFrame)
        {
            if (PostGameTerminalFrame >= 0 ||
                !EndMatchRequested ||
                _pendingClips.Count > 0)
            {
                return;
            }

            PostGameTerminalFrame = stableFrame;
        }

        private struct PendingClip
        {
            public int startFrame;
            public int shotFrame;
            public int scoreFrame;
            public int endFrame;
            public int shooterPlayerIndex;
        }
    }
}

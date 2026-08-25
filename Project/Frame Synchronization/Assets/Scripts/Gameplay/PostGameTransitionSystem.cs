namespace FrameSyncDemo
{
    public static class PostGameTransitionSystem
    {
        public static bool TryRestoreTerminalWorld(
            HighlightReplayRecorder recorder,
            FrameSyncCoordinator coordinator,
            int canonicalTerminalFrame,
            out int terminalFrame)
        {
            terminalFrame = recorder?.PostGameTerminalFrame ?? -1;
            if (terminalFrame < 0 ||
                canonicalTerminalFrame < 0 ||
                coordinator == null)
            {
                return false;
            }

            int restoreFrame = System.Math.Max(
                canonicalTerminalFrame,
                coordinator.ConfirmedFrame);
            if (restoreFrame > coordinator.PredictedFrame)
            {
                return false;
            }

            if (!coordinator.TryRestorePredictedFromTrack(
                WorldTrack.Confirmed,
                restoreFrame))
            {
                return false;
            }

            terminalFrame = restoreFrame;
            return true;
        }

        public static bool TryRestoreTerminalWorld(
            HighlightReplayRecorder recorder,
            PredictionSystem predictionSystem,
            PlayerEntity[] players,
            BallEntity ball,
            out int terminalFrame)
        {
            terminalFrame = recorder?.PostGameTerminalFrame ?? -1;
            if (terminalFrame < 0 || predictionSystem == null)
                return false;

            return predictionSystem.RestoreWorldSnapshot(
                terminalFrame,
                players,
                ball);
        }
    }
}

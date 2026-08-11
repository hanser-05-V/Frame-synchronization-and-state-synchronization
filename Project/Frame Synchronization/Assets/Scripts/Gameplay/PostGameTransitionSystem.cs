namespace FrameSyncDemo
{
    public static class PostGameTransitionSystem
    {
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

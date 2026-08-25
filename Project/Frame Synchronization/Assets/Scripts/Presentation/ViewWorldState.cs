namespace FrameSyncDemo
{
    public readonly struct ViewWorldState
    {
        public ViewWorldState(
            int predictedFrame,
            int confirmedFrame,
            int presentationFrame,
            in ViewPlayerState player0,
            in ViewPlayerState player1,
            in ViewBallState ball)
        {
            PredictedFrame = predictedFrame;
            ConfirmedFrame = confirmedFrame;
            PresentationFrame = presentationFrame;
            Player0 = player0;
            Player1 = player1;
            Ball = ball;
        }

        public int PredictedFrame { get; }
        public int ConfirmedFrame { get; }
        public int PresentationFrame { get; }
        public ViewPlayerState Player0 { get; }
        public ViewPlayerState Player1 { get; }
        public ViewBallState Ball { get; }

        public ViewPlayerState GetPlayer(int playerIndex)
        {
            if (playerIndex == 0)
                return Player0;
            if (playerIndex == 1)
                return Player1;

            throw new System.ArgumentOutOfRangeException(
                nameof(playerIndex));
        }
    }
}

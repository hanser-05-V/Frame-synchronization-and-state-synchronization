namespace FrameSyncDemo
{
    public readonly struct ViewBallState
    {
        public ViewBallState(
            in SimulationBallState from,
            in SimulationBallState to,
            int fromCanonicalFrame,
            int toCanonicalFrame,
            ViewSampleSource source)
        {
            FromPosition = from.position;
            ToPosition = to.position;
            FromVelocity = from.velocity;
            ToVelocity = to.velocity;
            FromState = (BallEntity.EState)from.state;
            ToState = (BallEntity.EState)to.state;
            FromHolderPlayerIndex = from.holderPlayerIndex;
            ToHolderPlayerIndex = to.holderPlayerIndex;
            FromCanonicalFrame = fromCanonicalFrame;
            ToCanonicalFrame = toCanonicalFrame;
            Source = source;
        }

        public FixedVector3 FromPosition { get; }
        public FixedVector3 ToPosition { get; }
        public FixedVector3 FromVelocity { get; }
        public FixedVector3 ToVelocity { get; }
        public BallEntity.EState FromState { get; }
        public BallEntity.EState ToState { get; }
        public int FromHolderPlayerIndex { get; }
        public int ToHolderPlayerIndex { get; }
        public int FromCanonicalFrame { get; }
        public int ToCanonicalFrame { get; }
        public ViewSampleSource Source { get; }
        public bool IsAttached =>
            ToState == BallEntity.EState.Held &&
            ToHolderPlayerIndex >= 0 &&
            ToHolderPlayerIndex <= 1;
    }
}

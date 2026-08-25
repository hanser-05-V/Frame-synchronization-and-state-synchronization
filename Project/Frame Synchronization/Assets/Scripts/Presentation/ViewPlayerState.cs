namespace FrameSyncDemo
{
    public readonly struct ViewPlayerState
    {
        public ViewPlayerState(
            int playerIndex,
            in SimulationPlayerState from,
            in SimulationPlayerState to,
            int fromCanonicalFrame,
            int toCanonicalFrame,
            ViewSampleSource source)
        {
            PlayerIndex = playerIndex;
            FromPosition = from.position;
            ToPosition = to.position;
            FromFacing = from.facing;
            ToFacing = to.facing;
            FromState = (PlayerEntity.EState)from.state;
            ToState = (PlayerEntity.EState)to.state;
            FromHasBall = from.hasBall;
            ToHasBall = to.hasBall;
            FromCanonicalFrame = fromCanonicalFrame;
            ToCanonicalFrame = toCanonicalFrame;
            Source = source;
        }

        public int PlayerIndex { get; }
        public FixedVector3 FromPosition { get; }
        public FixedVector3 ToPosition { get; }
        public FixedVector3 FromFacing { get; }
        public FixedVector3 ToFacing { get; }
        public PlayerEntity.EState FromState { get; }
        public PlayerEntity.EState ToState { get; }
        public bool FromHasBall { get; }
        public bool ToHasBall { get; }
        public int FromCanonicalFrame { get; }
        public int ToCanonicalFrame { get; }
        public ViewSampleSource Source { get; }
    }
}

namespace FrameSyncDemo
{
    /// <summary>
    /// 单帧确定性模拟的结果摘要，供外层适配器输出日志与执行展示。
    /// </summary>
    public readonly struct FrameSimulationResult
    {
        public readonly BallEntity.EState previousBallState;
        public readonly FixedInt previousBallY;
        public readonly int shooterPlayerIndex;
        public readonly bool heldInvariantValid;

        public FrameSimulationResult(
            BallEntity.EState previousBallState,
            FixedInt previousBallY,
            int shooterPlayerIndex,
            bool heldInvariantValid)
        {
            this.previousBallState = previousBallState;
            this.previousBallY = previousBallY;
            this.shooterPlayerIndex = shooterPlayerIndex;
            this.heldInvariantValid = heldInvariantValid;
        }
    }
}

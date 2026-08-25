namespace FrameSyncDemo
{
    public readonly struct FrameAdvanceResult
    {
        public FrameAdvanceResult(
            bool succeeded,
            FrameSimulationResult simulationResult = default)
        {
            Succeeded = succeeded;
            SimulationResult = simulationResult;
        }

        public bool Succeeded { get; }
        public FrameSimulationResult SimulationResult { get; }
    }
}

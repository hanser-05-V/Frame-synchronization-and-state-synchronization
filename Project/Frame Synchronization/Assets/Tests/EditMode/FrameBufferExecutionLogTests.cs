using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class FrameBufferExecutionLogTests
    {
        [Test]
        public void AddFrame_ResolvedLedgerValues_ArePreservedAsExecutionLog()
        {
            var ledger = new FrameInputLedger();
            ledger.RecordActual(0, 0, new FrameInput(0x100u));
            FrameInputLedger.ResolvedFrame resolved = ledger.ResolveForSimulation(0);
            var executedInputs = new[]
            {
                resolved.GetPlayer(0).Value,
                resolved.GetPlayer(1).Value
            };
            var buffer = new FrameBuffer();

            Assert.IsTrue(buffer.AddFrame(0, executedInputs));
            Assert.IsTrue(buffer.PeekFrame(0, out FrameBuffer.Frame logged));
            Assert.AreEqual(executedInputs[0]._raw, logged.inputs[0]._raw);
            Assert.AreEqual(executedInputs[1]._raw, logged.inputs[1]._raw);
            Assert.AreEqual(FrameInputLedger.InputState.Actual,
                resolved.GetPlayer(0).State);
            Assert.AreEqual(FrameInputLedger.InputState.Predicted,
                resolved.GetPlayer(1).State);
        }
    }
}

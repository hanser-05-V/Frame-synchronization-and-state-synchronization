using System;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class FrameReplaySystemTests
    {
        private static readonly FixedInt MoveDistance = FixedInt.FromFloat(0.165f);

        [Test]
        public void Replay_PlanOnlyOverload_UsesLedgerPlanAndCommitsItsMismatch()
        {
            World expected = CreateWorld(1);
            World replayed = CreateWorld(1);
            var replayWorld = CreateDeterministicWorld(replayed);
            var prediction = new PredictionSystem();
            prediction.Init();
            var ledger = new FrameInputLedger();

            FrameInput[] frameZero = CreateInputs();
            RecordBoth(ledger, 0, frameZero);
            Step(expected, frameZero);
            replayWorld.Step(frameZero);
            prediction.TakeWorldSnapshot(0, replayWorld);

            FrameInput[] authoritative = CreateInputs(player0Direction: 3, player1Direction: 1);
            ledger.ResolveForSimulation(1);
            RecordBoth(ledger, 1, authoritative);
            Assert.AreEqual(FrameInputLedger.ReplayPlanResult.Success,
                ledger.TryBuildReplayPlan(1, 1, out FrameInputLedger.ReplayInputPlan plan));
            Step(expected, authoritative);

            FrameReplayResult result = FrameReplaySystem.Replay(
                plan,
                ledger,
                prediction,
                replayWorld,
                () => ResetWorld(replayed, 1));

            Assert.IsTrue(result.succeeded);
            Assert.AreEqual(0, result.restoredFrame);
            Assert.AreEqual(1, result.replayedFrameCount);
            AssertWorldState(expected, replayed);
            Assert.IsFalse(ledger.TryGetEarliestMismatch(out _));
        }

        [Test]
        public void Replay_PlanStartingAtLedgerStart_ResetsThenStepsPlan()
        {
            World expected = CreateWorld(1);
            World replayed = CreateWorld(0);
            var expectedWorld = CreateDeterministicWorld(expected);
            var replayWorld = CreateDeterministicWorld(replayed);
            var prediction = new PredictionSystem();
            prediction.Init();
            var ledger = new FrameInputLedger();
            FrameInput[] authoritative = CreateInputs(player0Direction: 3, player1Direction: 1);
            RecordBoth(ledger, 0, authoritative);
            Assert.AreEqual(FrameInputLedger.ReplayPlanResult.Success,
                ledger.TryBuildReplayPlan(0, 0, out FrameInputLedger.ReplayInputPlan plan));
            expectedWorld.Step(authoritative);

            int resetCount = 0;
            FrameReplayResult result = FrameReplaySystem.Replay(
                plan,
                ledger,
                prediction,
                replayWorld,
                () =>
                {
                    resetCount++;
                    ResetWorld(replayed, 1);
                });

            Assert.IsTrue(result.succeeded);
            Assert.AreEqual(-1, result.restoredFrame);
            Assert.AreEqual(1, resetCount);
            AssertWorldState(expected, replayed);
        }

        [Test]
        public void Replay_PlanOnlyWithoutExactPriorSnapshot_FailsBeforeWorldMutation()
        {
            World replayed = CreateWorld(1);
            var replayWorld = CreateDeterministicWorld(replayed);
            var prediction = new PredictionSystem();
            prediction.Init();
            var ledger = new FrameInputLedger();
            ledger.ResolveForSimulation(1);
            Assert.AreEqual(FrameInputLedger.ReplayPlanResult.Success,
                ledger.TryBuildReplayPlan(1, 1, out FrameInputLedger.ReplayInputPlan plan));
            SimulationWorldState before = replayWorld.Capture(99);

            FrameReplayResult result = FrameReplaySystem.Replay(plan, ledger, prediction, replayWorld, null);

            Assert.IsFalse(result.succeeded);
            Assert.AreEqual(FrameReplayResult.Failure.MissingSnapshot, result.failure);
            Assert.AreEqual(before, replayWorld.Capture(99));
            Assert.IsTrue(ledger.IsCurrentReplayPlan(plan));
        }

        [Test]
        public void Replay_PlanOnlyStalePlan_FailsBeforeWorldMutation()
        {
            World replayed = CreateWorld(1);
            var replayWorld = CreateDeterministicWorld(replayed);
            var prediction = new PredictionSystem();
            prediction.Init();
            var ledger = new FrameInputLedger();
            ledger.ResolveForSimulation(0);
            Assert.AreEqual(FrameInputLedger.ReplayPlanResult.Success,
                ledger.TryBuildReplayPlan(0, 0, out FrameInputLedger.ReplayInputPlan stalePlan));
            Assert.AreEqual(FrameInputLedger.ReplayPlanResult.Success,
                ledger.TryBuildReplayPlan(0, 0, out _));
            SimulationWorldState before = replayWorld.Capture(99);

            FrameReplayResult result = FrameReplaySystem.Replay(
                stalePlan,
                ledger,
                prediction,
                replayWorld,
                () => ResetWorld(replayed, 1));

            Assert.IsFalse(result.succeeded);
            Assert.AreEqual(FrameReplayResult.Failure.StalePlan, result.failure);
            Assert.AreEqual(before, replayWorld.Capture(99));
        }

        [Test]
        public void Replay_OlderSnapshotThanPlanStart_FailsWithoutMutationOrPlanCommit()
        {
            World replayed = CreateWorld(1);
            var replayWorld = CreateDeterministicWorld(replayed);
            var prediction = new PredictionSystem();
            prediction.Init();
            var ledger = new FrameInputLedger();
            replayWorld.Step(CreateInputs(player0Direction: 3));
            prediction.TakeWorldSnapshot(0, replayWorld);
            ledger.ResolveForSimulation(2);
            ledger.RecordActual(2, 1, new FrameInput(0x200u));
            Assert.IsTrue(ledger.TryGetEarliestMismatch(out FrameInputLedger.InputMismatch mismatch));
            Assert.AreEqual(2, mismatch.Frame);
            Assert.AreEqual(FrameInputLedger.ReplayPlanResult.Success,
                ledger.TryBuildReplayPlan(2, 2, out FrameInputLedger.ReplayInputPlan plan));
            SimulationWorldState before = replayWorld.Capture(99);

            FrameReplayResult result = FrameReplaySystem.Replay(plan, ledger, prediction, replayWorld, null);

            Assert.IsFalse(result.succeeded);
            Assert.AreEqual(FrameReplayResult.Failure.MissingSnapshot, result.failure);
            Assert.AreEqual(before, replayWorld.Capture(99));
            Assert.IsTrue(ledger.IsCurrentReplayPlan(plan));
        }

        [Test]
        public void Replay_CommitRebuildsPredictedSuffix_LaterActualIsClaimableMismatch()
        {
            World replayed = CreateWorld(1);
            var replayWorld = CreateDeterministicWorld(replayed);
            var prediction = new PredictionSystem();
            prediction.Init();
            var ledger = new FrameInputLedger();

            FrameInput[] frameZero = CreateInputs();
            RecordBoth(ledger, 0, frameZero);
            replayWorld.Step(frameZero);
            prediction.TakeWorldSnapshot(0, replayWorld);
            ledger.ResolveForSimulation(1);
            ledger.ResolveForSimulation(2);

            FrameInput changedRemote = CreateInput(direction: 3);
            ledger.RecordActual(1, 1, changedRemote);
            Assert.AreEqual(FrameInputLedger.ReplayPlanResult.Success,
                ledger.TryBuildReplayPlan(1, 2, out FrameInputLedger.ReplayInputPlan plan));
            Assert.IsTrue(FrameReplaySystem.Replay(plan, ledger, prediction, replayWorld, null).succeeded);
            Assert.IsFalse(ledger.TryGetEarliestMismatch(out _));

            FrameInputLedger.ActualArrival arrival = ledger.RecordActual(2, 1, CreateInput());

            Assert.AreEqual(FrameInputLedger.ActualDisposition.PredictionMismatched, arrival.Disposition);
            Assert.IsTrue(ledger.TryGetEarliestMismatch(out FrameInputLedger.InputMismatch mismatch));
            Assert.AreEqual(2, mismatch.Frame);
            Assert.AreEqual(1, mismatch.PlayerIndex);
            Assert.AreEqual(CreateInput()._raw, mismatch.ActualRaw);
            Assert.AreEqual(changedRemote._raw, mismatch.PredictedRaw);
        }

        private static void Step(World world, FrameInput[] inputs)
        {
            FrameSimulationSystem.Step(
                world.players,
                world.stateMachines,
                world.ball,
                inputs,
                MoveDistance,
                CourtConstant.LogicDeltaTime);
        }

        private static World CreateWorld(int holderPlayerIndex)
        {
            var player0 = new PlayerEntity();
            var player1 = new PlayerEntity();
            var players = new[] { player0, player1 };
            var world = new World(
                players,
                new[]
                {
                    new PlayerStateMachine(player0),
                    new PlayerStateMachine(player1)
                },
                new BallEntity());
            ResetWorld(world, holderPlayerIndex);
            return world;
        }

        private static DeterministicWorld CreateDeterministicWorld(World world)
        {
            return new DeterministicWorld(
                world.players,
                world.stateMachines,
                world.ball,
                MoveDistance,
                CourtConstant.LogicDeltaTime);
        }

        private static void ResetWorld(World world, int holderPlayerIndex)
        {
            world.players[0].Reset(
                new FixedVector3(FixedInt.FromInt(-3), FixedInt.Zero, FixedInt.Zero),
                0);
            world.players[1].Reset(
                new FixedVector3(FixedInt.FromInt(3), FixedInt.Zero, FixedInt.Zero),
                1);
            world.ball.Reset(FixedVector3.Zero);
            world.players[holderPlayerIndex].hasBall = true;
            world.ball.state = BallEntity.EState.Held;
            world.ball.holderPlayerIndex = holderPlayerIndex;
            Assert.IsTrue(BallPossessionSystem.TryUpdateHeldBall(
                world.players[holderPlayerIndex],
                world.ball));
        }

        private static FrameInput[] CreateInputs(
            byte player0Direction = 0,
            byte player1Direction = 0)
        {
            return new[]
            {
                CreateInput(player0Direction),
                CreateInput(player1Direction)
            };
        }

        private static FrameInput CreateInput(byte direction = 0)
        {
            return new FrameInput(direction, 0);
        }

        private static void RecordBoth(FrameInputLedger ledger, int frame, FrameInput[] inputs)
        {
            ledger.RecordActual(frame, 0, inputs[0]);
            ledger.RecordActual(frame, 1, inputs[1]);
        }

        private static void AssertWorldState(World expected, World actual)
        {
            for (int i = 0; i < expected.players.Length; i++)
            {
                AssertVectorRaw(expected.players[i].position, actual.players[i].position);
                AssertVectorRaw(expected.players[i].facing, actual.players[i].facing);
                Assert.AreEqual(expected.players[i].state, actual.players[i].state);
                Assert.AreEqual(expected.players[i].hasBall, actual.players[i].hasBall);
            }

            AssertVectorRaw(expected.ball.position, actual.ball.position);
            AssertVectorRaw(expected.ball.velocity, actual.ball.velocity);
            Assert.AreEqual(expected.ball.state, actual.ball.state);
            Assert.AreEqual(expected.ball.holderPlayerIndex, actual.ball.holderPlayerIndex);
        }

        private static void AssertVectorRaw(FixedVector3 expected, FixedVector3 actual)
        {
            Assert.AreEqual(expected.x._raw, actual.x._raw);
            Assert.AreEqual(expected.y._raw, actual.y._raw);
            Assert.AreEqual(expected.z._raw, actual.z._raw);
        }

        private sealed class World
        {
            public readonly PlayerEntity[] players;
            public readonly PlayerStateMachine[] stateMachines;
            public readonly BallEntity ball;

            public World(
                PlayerEntity[] players,
                PlayerStateMachine[] stateMachines,
                BallEntity ball)
            {
                this.players = players;
                this.stateMachines = stateMachines;
                this.ball = ball;
            }
        }
    }
}

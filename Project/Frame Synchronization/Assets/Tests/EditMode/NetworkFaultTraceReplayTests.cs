using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class NetworkFaultTraceReplayTests
    {
        private const int TerminalFrame = 31;
        private static readonly FixedInt MoveDistance = FixedInt.FromInt(1);

        [Test]
        public void SameSeedAndScript_ReplaysIdenticalDecisionsAndRollbackTuples()
        {
            var profile = new NetworkLabProfile(
                70,
                25,
                35,
                60,
                25,
                30,
                90,
                771u);

            ReplayResult first = Run(profile);
            ReplayResult second = Run(profile);

            CollectionAssert.AreEqual(
                first.DecisionTraceLines,
                second.DecisionTraceLines);
            CollectionAssert.AreEqual(
                first.RollbackTuples,
                second.RollbackTuples);
            AssertConverged(first);
            AssertConverged(second);
        }

        [Test]
        public void ScenarioMatrix_EventuallyFillsEveryHoleAndConverges()
        {
            var scenarios = new[]
            {
                new Scenario("normal", new NetworkLabProfile(
                    0, 0, 0, 0, 0, 0, 0, 1u)),
                new Scenario("fixed100", new NetworkLabProfile(
                    100, 0, 0, 0, 0, 0, 0, 2u)),
                new Scenario("fixed200", new NetworkLabProfile(
                    200, 0, 0, 0, 0, 0, 0, 3u)),
                new Scenario("jitter", new NetworkLabProfile(
                    80, 50, 0, 0, 0, 0, 0, 4u)),
                new Scenario("applicationReorder", new NetworkLabProfile(
                    30, 0, 50, 100, 0, 0, 0, 5u)),
                new Scenario("duplicate", new NetworkLabProfile(
                    30, 0, 0, 0, 100, 0, 0, 6u)),
                new Scenario("recoveredLoss", new NetworkLabProfile(
                    30, 0, 0, 0, 0, 50, 120, 7u))
            };

            foreach (Scenario scenario in scenarios)
            {
                ReplayResult result = Run(scenario.Profile);

                AssertConverged(result, scenario.Name);
                if (scenario.Name == "applicationReorder")
                {
                    Assert.IsTrue(
                        result.SawApplicationReorder,
                        scenario.Name + " selected no reordered events.");
                    Assert.IsTrue(
                        result.SawArrivalInversion,
                        scenario.Name + " produced no observable inversion.");
                    Assert.IsTrue(
                        result.SawConfirmationHole,
                        scenario.Name + " did not exercise a confirmation hole.");
                }
                if (scenario.Name == "duplicate")
                {
                    Assert.Greater(
                        result.IdempotentDuplicateCount,
                        0,
                        scenario.Name + " did not reach Ledger idempotence.");
                }
                if (scenario.Name == "recoveredLoss")
                {
                    Assert.IsTrue(
                        result.SawRecoveredLoss,
                        scenario.Name + " selected no recovered-loss events.");
                }
            }
        }

        private static ReplayResult Run(NetworkLabProfile profile)
        {
            FrameSyncCoordinator coordinator = CreateCoordinator();
            List<DeliveryEvent> deliveries = BuildDeliveries(profile);
            var decisionLines = new List<string>();
            foreach (DeliveryEvent delivery in deliveries.Where(
                candidate => candidate.CopyIndex == 0).OrderBy(
                candidate => candidate.SenderSequence))
            {
                decisionLines.Add(delivery.Decision.ToCanonicalTraceLine(
                    1,
                    delivery.SenderSequence,
                    delivery.Frame,
                    delivery.Raw));
            }

            var rollbackTuples = new List<string>();
            int deliveryIndex = 0;
            int idempotentDuplicateCount = 0;
            int highestDeliveredFrame = -1;
            bool sawArrivalInversion = false;
            bool sawConfirmationHole = false;

            for (int frame = 0; frame <= TerminalFrame; frame++)
            {
                long nowMs = frame * 33L;
                DeliverThrough(
                    deliveries,
                    ref deliveryIndex,
                    nowMs,
                    coordinator,
                    ref idempotentDuplicateCount,
                    ref highestDeliveredFrame,
                    ref sawArrivalInversion,
                    ref sawConfirmationHole);

                coordinator.RecordActual(frame, 0, LocalInput(frame));
                FrameInputLedger.ResolvedFrame inputs =
                    coordinator.ResolveForPrediction(frame);
                Assert.IsTrue(coordinator.Advance(frame, inputs).Succeeded);
                ReconcileIfNeeded(coordinator, rollbackTuples);
            }

            while (deliveryIndex < deliveries.Count)
            {
                long dueAtMs = deliveries[deliveryIndex].DueAtMs;
                DeliverThrough(
                    deliveries,
                    ref deliveryIndex,
                    dueAtMs,
                    coordinator,
                    ref idempotentDuplicateCount,
                    ref highestDeliveredFrame,
                    ref sawArrivalInversion,
                    ref sawConfirmationHole);
                ReconcileIfNeeded(coordinator, rollbackTuples);
            }

            ReconcileIfNeeded(coordinator, rollbackTuples);
            return new ReplayResult(
                decisionLines,
                rollbackTuples,
                coordinator,
                idempotentDuplicateCount,
                deliveries.Any(item => item.Decision.ApplicationReordered),
                deliveries.Any(item => item.Decision.LossRecovered),
                sawArrivalInversion,
                sawConfirmationHole);
        }

        private static List<DeliveryEvent> BuildDeliveries(
            NetworkLabProfile profile)
        {
            var deliveries = new List<DeliveryEvent>();
            long reliableBarrierDueMs = long.MinValue;
            for (int frame = 0; frame <= TerminalFrame; frame++)
            {
                uint raw = RemoteInput(frame)._raw;
                var decision = DeterministicNetworkFaultModel.Decide(
                    profile,
                    1,
                    frame,
                    frame,
                    raw);
                long dueAtMs = frame * 33L + decision.EffectiveDelayMs;
                if (!decision.ApplicationReordered)
                {
                    dueAtMs = Math.Max(dueAtMs, reliableBarrierDueMs);
                    reliableBarrierDueMs = Math.Max(
                        reliableBarrierDueMs,
                        dueAtMs);
                }

                deliveries.Add(new DeliveryEvent(
                    dueAtMs,
                    frame,
                    frame,
                    0,
                    raw,
                    decision));
                if (decision.Duplicated)
                {
                    deliveries.Add(new DeliveryEvent(
                        dueAtMs,
                        frame,
                        frame,
                        1,
                        raw,
                        decision));
                }
            }

            return deliveries
                .OrderBy(item => item.DueAtMs)
                .ThenBy(item => item.SenderSequence)
                .ThenBy(item => item.CopyIndex)
                .ToList();
        }

        private static void DeliverThrough(
            List<DeliveryEvent> deliveries,
            ref int deliveryIndex,
            long nowMs,
            FrameSyncCoordinator coordinator,
            ref int idempotentDuplicateCount,
            ref int highestDeliveredFrame,
            ref bool sawArrivalInversion,
            ref bool sawConfirmationHole)
        {
            while (deliveryIndex < deliveries.Count &&
                deliveries[deliveryIndex].DueAtMs <= nowMs)
            {
                DeliveryEvent delivery = deliveries[deliveryIndex++];
                if (delivery.Frame < highestDeliveredFrame)
                    sawArrivalInversion = true;
                highestDeliveredFrame = Math.Max(
                    highestDeliveredFrame,
                    delivery.Frame);

                FrameInputLedger.ActualArrival arrival =
                    coordinator.RecordActual(
                        delivery.Frame,
                        1,
                        FrameInput.FromRaw(delivery.Raw));
                if (arrival.Disposition ==
                    FrameInputLedger.ActualDisposition.IdempotentDuplicate)
                {
                    idempotentDuplicateCount++;
                }

                if (highestDeliveredFrame >
                    coordinator.ConfirmedThroughFrame + 1)
                {
                    sawConfirmationHole = true;
                }
            }
        }

        private static void ReconcileIfNeeded(
            FrameSyncCoordinator coordinator,
            List<string> rollbackTuples)
        {
            bool hadMismatch = coordinator.TryGetEarliestMismatch(
                out FrameInputLedger.InputMismatch mismatch);
            ReconcileResult result = coordinator.Reconcile();
            Assert.IsTrue(result.Succeeded);
            if (hadMismatch && result.Replayed)
            {
                rollbackTuples.Add(string.Format(
                    "{0}:{1}:{2}",
                    mismatch.Frame,
                    result.RestoredFrame,
                    result.ReplayedFrameCount));
            }
        }

        private static void AssertConverged(
            ReplayResult result,
            string scenarioName = "sameSeed")
        {
            Assert.AreEqual(
                TerminalFrame,
                result.Coordinator.ConfirmedThroughFrame,
                scenarioName + " did not fill every Actual input hole.");
            Assert.AreEqual(
                TerminalFrame,
                result.Coordinator.ConfirmedFrame,
                scenarioName + " Confirmed world did not reach terminal frame.");
            Assert.AreEqual(
                TerminalFrame,
                result.Coordinator.PredictedFrame,
                scenarioName + " Predicted world did not remain at terminal frame.");
            Assert.IsTrue(result.Coordinator.TryGetActualFrame(
                TerminalFrame,
                out _));
            Assert.AreEqual(
                WorldHash.Compute(
                    result.Coordinator.ConfirmedWorld,
                    TerminalFrame),
                WorldHash.Compute(
                    result.Coordinator.PredictedWorld,
                    TerminalFrame),
                scenarioName + " terminal Confirmed/Predicted Hash mismatch.");
        }

        private static FrameInput LocalInput(int frame)
        {
            byte direction = (byte)(frame % 4 < 2 ? 1 : 3);
            return new FrameInput(direction, 0);
        }

        private static FrameInput RemoteInput(int frame)
        {
            byte direction = (byte)(frame % 6 < 3 ? 7 : 5);
            return new FrameInput(direction, 0);
        }

        private static FrameSyncCoordinator CreateCoordinator()
        {
            var playerZero = new PlayerEntity();
            playerZero.Reset(
                new FixedVector3(
                    FixedInt.FromInt(-3),
                    FixedInt.Zero,
                    FixedInt.Zero),
                0);
            var playerOne = new PlayerEntity();
            playerOne.Reset(
                new FixedVector3(
                    FixedInt.FromInt(3),
                    FixedInt.Zero,
                    FixedInt.Zero),
                1);
            var players = new[] { playerZero, playerOne };
            var ball = new BallEntity();
            ball.Reset(FixedVector3.Zero);
            playerZero.hasBall = true;
            ball.state = BallEntity.EState.Held;
            ball.holderPlayerIndex = 0;
            Assert.IsTrue(BallPossessionSystem.TryUpdateHeldBall(
                playerZero,
                ball));
            var predicted = new DeterministicWorld(
                players,
                new[]
                {
                    new PlayerStateMachine(playerZero),
                    new PlayerStateMachine(playerOne)
                },
                ball,
                MoveDistance,
                CourtConstant.LogicDeltaTime);
            SimulationWorldState initial = predicted.Capture(-1);
            return new FrameSyncCoordinator(
                predicted,
                initial,
                MoveDistance,
                CourtConstant.LogicDeltaTime,
                128);
        }

        private readonly struct Scenario
        {
            public Scenario(string name, NetworkLabProfile profile)
            {
                Name = name;
                Profile = profile;
            }

            public string Name { get; }
            public NetworkLabProfile Profile { get; }
        }

        private readonly struct DeliveryEvent
        {
            public DeliveryEvent(
                long dueAtMs,
                long senderSequence,
                int frame,
                int copyIndex,
                uint raw,
                DeterministicNetworkFaultModel.Decision decision)
            {
                DueAtMs = dueAtMs;
                SenderSequence = senderSequence;
                Frame = frame;
                CopyIndex = copyIndex;
                Raw = raw;
                Decision = decision;
            }

            public long DueAtMs { get; }
            public long SenderSequence { get; }
            public int Frame { get; }
            public int CopyIndex { get; }
            public uint Raw { get; }
            public DeterministicNetworkFaultModel.Decision Decision { get; }
        }

        private sealed class ReplayResult
        {
            public ReplayResult(
                List<string> decisionTraceLines,
                List<string> rollbackTuples,
                FrameSyncCoordinator coordinator,
                int idempotentDuplicateCount,
                bool sawApplicationReorder,
                bool sawRecoveredLoss,
                bool sawArrivalInversion,
                bool sawConfirmationHole)
            {
                DecisionTraceLines = decisionTraceLines;
                RollbackTuples = rollbackTuples;
                Coordinator = coordinator;
                IdempotentDuplicateCount = idempotentDuplicateCount;
                SawApplicationReorder = sawApplicationReorder;
                SawRecoveredLoss = sawRecoveredLoss;
                SawArrivalInversion = sawArrivalInversion;
                SawConfirmationHole = sawConfirmationHole;
            }

            public List<string> DecisionTraceLines { get; }
            public List<string> RollbackTuples { get; }
            public FrameSyncCoordinator Coordinator { get; }
            public int IdempotentDuplicateCount { get; }
            public bool SawApplicationReorder { get; }
            public bool SawRecoveredLoss { get; }
            public bool SawArrivalInversion { get; }
            public bool SawConfirmationHole { get; }
        }
    }
}

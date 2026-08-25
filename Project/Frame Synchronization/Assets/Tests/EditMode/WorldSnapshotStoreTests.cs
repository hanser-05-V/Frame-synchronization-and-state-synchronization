using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class WorldSnapshotStoreTests
    {
        [Test]
        public void Constructor_NonPositiveCapacity_Throws()
        {
            SimulationWorldState initial =
                CreateWorldState(-1, -3000, 3000);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new WorldSnapshotStore(initial, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new WorldSnapshotStore(initial, -1));
        }

        [Test]
        public void ReadableBounds_EmptyThenRingWrap_ReportsActualSnapshots()
        {
            SimulationWorldState initial =
                CreateWorldState(-1, -3000, 3000);
            var store = new WorldSnapshotStore(initial, 2);

            Assert.AreEqual(-1, store.EarliestReadableFrame);
            Assert.AreEqual(-1, store.LatestReadableFrame);

            store.Store(CreateWorldState(0, -2000, 3000));
            store.Store(CreateWorldState(1, -1000, 3000));
            Assert.AreEqual(0, store.EarliestReadableFrame);
            Assert.AreEqual(1, store.LatestReadableFrame);

            store.Store(CreateWorldState(2, 0, 3000));
            Assert.AreEqual(1, store.EarliestReadableFrame);
            Assert.AreEqual(2, store.LatestReadableFrame);
        }

        [Test]
        public void ReadableBounds_TruncatePastOverwrittenHistoryThenRewrite_TracksActualWindow()
        {
            SimulationWorldState initial =
                CreateWorldState(-1, -3000, 3000);
            var store = new WorldSnapshotStore(initial, 2);
            store.Store(CreateWorldState(0, -2000, 3000));
            store.Store(CreateWorldState(1, -1000, 3000));
            store.Store(CreateWorldState(2, 0, 3000));

            store.TruncateAfter(0);

            Assert.AreEqual(-1, store.EarliestReadableFrame);
            Assert.AreEqual(-1, store.LatestReadableFrame);

            store.Store(CreateWorldState(0, 1000, 3000));
            store.Store(CreateWorldState(1, 2000, 3000));

            Assert.AreEqual(0, store.EarliestReadableFrame);
            Assert.AreEqual(1, store.LatestReadableFrame);
        }

        [Test]
        public void TruncateAfter_HidesFutureSnapshotsUntilRewritten()
        {
            SimulationWorldState initial =
                CreateWorldState(-1, -3000, 3000);
            var store = new WorldSnapshotStore(initial, 4);
            store.Store(CreateWorldState(0, -2000, 3000));
            store.Store(CreateWorldState(1, -1000, 3000));

            store.TruncateAfter(0);

            Assert.IsTrue(store.TryGet(0, out _));
            Assert.IsFalse(store.TryGet(1, out _));

            store.Store(CreateWorldState(1, 0, 3000));

            Assert.IsTrue(store.TryGet(1, out FrameSnapshot rewritten));
            Assert.AreEqual(0, rewritten.world.player0.position.x._raw);
        }

        [Test]
        public void RuntimeAssembly_P2CWorldSnapshotStore_IsAvailable()
        {
            TypeInfo type = typeof(PredictionSystem).Assembly
                .DefinedTypes
                .FirstOrDefault(candidate =>
                    candidate.FullName == "FrameSyncDemo.WorldSnapshotStore");

            Assert.IsNotNull(type,
                "P2-C requires a dedicated value-state snapshot store.");
        }

        [Test]
        public void DeterministicWorld_P2CCloneFactory_IsAvailable()
        {
            MethodInfo method = typeof(DeterministicWorld).GetMethod(
                "CreateIsolated",
                BindingFlags.Public | BindingFlags.Static);

            Assert.IsNotNull(method,
                "P2-C requires an isolated runtime-world clone factory.");
        }

        [Test]
        public void Stores_SameFrameDifferentValues_RemainIndependentValueCopies()
        {
            SimulationWorldState initial = CreateWorldState(-1, -3000, 3000);
            var confirmed = new WorldSnapshotStore(initial, 2);
            var predicted = new WorldSnapshotStore(initial, 2);
            SimulationWorldState confirmedFrame = CreateWorldState(0, -2000, 3000);
            SimulationWorldState predictedFrame = CreateWorldState(0, -1000, 3000);

            confirmed.Store(confirmedFrame);
            predicted.Store(predictedFrame);
            confirmedFrame.player0.position.x = FixedInt.FromInt(99);
            predictedFrame.player0.position.x = FixedInt.FromInt(88);

            Assert.IsTrue(confirmed.TryGet(0, out FrameSnapshot confirmedSnapshot));
            Assert.IsTrue(predicted.TryGet(0, out FrameSnapshot predictedSnapshot));
            Assert.AreEqual(-2000, confirmedSnapshot.world.player0.position.x._raw);
            Assert.AreEqual(-1000, predictedSnapshot.world.player0.position.x._raw);
            Assert.IsFalse(confirmed.TryGet(1, out FrameSnapshot missing));
            Assert.IsFalse(missing.IsValid);
            Assert.AreEqual(-3000, confirmed.InitialWorld.player0.position.x._raw);
        }

        [Test]
        public void CreateIsolated_StepClone_DoesNotMutateSourceRuntimeWorld()
        {
            RuntimeWorld source = CreateRuntimeWorld();
            var sourceWorld = new DeterministicWorld(
                source.players,
                source.stateMachines,
                source.ball,
                FixedInt.FromInt(1),
                CourtConstant.LogicDeltaTime);
            SimulationWorldState initial = sourceWorld.Capture(-1);
            ulong sourceBefore = WorldHash.Compute(initial, -1);

            DeterministicWorld clone = DeterministicWorld.CreateIsolated(
                initial,
                FixedInt.FromInt(1),
                CourtConstant.LogicDeltaTime);
            clone.Step(new[] { new FrameInput(1, 0), default(FrameInput) });

            Assert.AreEqual(sourceBefore, WorldHash.Compute(sourceWorld.Capture(-1), -1));
            Assert.AreNotEqual(
                sourceBefore,
                WorldHash.Compute(clone.Capture(-1), -1));
        }

        [Test]
        public void Restore_ExactSnapshotAndInitial_AreExplicitAndMissingIsNonMutating()
        {
            SimulationWorldState initial = CreateWorldState(-1, -3000, 3000);
            SimulationWorldState frameZero = CreateWorldState(0, -2000, 3000);
            var store = new WorldSnapshotStore(initial, 2);
            store.Store(frameZero);
            DeterministicWorld world = DeterministicWorld.CreateIsolated(
                CreateWorldState(-1, 9000, 3000),
                FixedInt.FromInt(1),
                CourtConstant.LogicDeltaTime);

            Assert.IsFalse(store.TryRestore(1, world));
            Assert.AreEqual(9000, world.Capture(-1).player0.position.x._raw);
            Assert.IsTrue(store.TryRestore(0, world));
            Assert.AreEqual(-2000, world.Capture(0).player0.position.x._raw);

            store.RestoreInitial(world);

            Assert.AreEqual(-3000, world.Capture(-1).player0.position.x._raw);
        }

        private static SimulationWorldState CreateWorldState(
            int frame,
            int playerZeroXRaw,
            int playerOneXRaw)
        {
            RuntimeWorld runtime = CreateRuntimeWorld();
            runtime.players[0].position.x = new FixedInt(playerZeroXRaw);
            runtime.players[1].position.x = new FixedInt(playerOneXRaw);
            return WorldStateCodec.Capture(frame, runtime.players, runtime.ball);
        }

        private static RuntimeWorld CreateRuntimeWorld()
        {
            var playerZero = new PlayerEntity();
            playerZero.Reset(
                new FixedVector3(FixedInt.FromInt(-3), FixedInt.Zero, FixedInt.Zero),
                0);
            var playerOne = new PlayerEntity();
            playerOne.Reset(
                new FixedVector3(FixedInt.FromInt(3), FixedInt.Zero, FixedInt.Zero),
                1);
            var players = new[] { playerZero, playerOne };
            var ball = new BallEntity();
            ball.Reset(FixedVector3.Zero);
            playerZero.hasBall = true;
            ball.state = BallEntity.EState.Held;
            ball.holderPlayerIndex = 0;
            Assert.IsTrue(BallPossessionSystem.TryUpdateHeldBall(playerZero, ball));
            return new RuntimeWorld(
                players,
                new[]
                {
                    new PlayerStateMachine(playerZero),
                    new PlayerStateMachine(playerOne)
                },
                ball);
        }

        private sealed class RuntimeWorld
        {
            public readonly PlayerEntity[] players;
            public readonly PlayerStateMachine[] stateMachines;
            public readonly BallEntity ball;

            public RuntimeWorld(
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

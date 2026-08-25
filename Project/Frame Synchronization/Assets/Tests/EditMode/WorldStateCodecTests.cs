using System;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class WorldStateCodecTests
    {
        [Test]
        public void SimulationWorldState_CopyThenMutate_DoesNotAliasOriginal()
        {
            SimulationWorldState original = CreateState(12);
            SimulationWorldState copy = original;

            copy.player0.position.x = FixedInt.FromInt(99);
            copy.ball.holderPlayerIndex = 1;

            Assert.AreNotEqual(copy.player0.position.x, original.player0.position.x);
            Assert.AreNotEqual(copy.ball.holderPlayerIndex, original.ball.holderPlayerIndex);
        }

        [Test]
        public void Capture_DistinctRuntimeWorld_CopiesEverySynchronizedField()
        {
            CreateRuntimeWorld(out PlayerEntity[] players, out BallEntity ball);

            SimulationWorldState state = WorldStateCodec.Capture(37, players, ball);

            AssertState(CreateState(37), state);
        }

        [Test]
        public void Restore_MutatedRuntimeWorld_RestoresEverySynchronizedField()
        {
            CreateRuntimeWorld(out PlayerEntity[] players, out BallEntity ball);
            SimulationWorldState expected = CreateState(41);
            players[0].Reset(FixedVector3.Zero, 0);
            players[1].Reset(FixedVector3.Zero, 1);
            ball.Reset(FixedVector3.Zero);

            WorldStateCodec.Restore(expected, players, ball);

            AssertState(expected, WorldStateCodec.Capture(41, players, ball));
        }

        [Test]
        public void Capture_PlayerCountIsNotTwo_ThrowsArgumentException()
        {
            CreateRuntimeWorld(out PlayerEntity[] players, out BallEntity ball);

            Assert.Throws<ArgumentException>(
                () => WorldStateCodec.Capture(1, new[] { players[0] }, ball));
            Assert.Throws<ArgumentException>(
                () => WorldStateCodec.Capture(1, new PlayerEntity[] { players[0], null }, ball));
            Assert.Throws<ArgumentNullException>(
                () => WorldStateCodec.Capture(1, players, null));
        }

        [Test]
        public void Capture_EnumStates_UseExistingIntegerValues()
        {
            CreateRuntimeWorld(out PlayerEntity[] players, out BallEntity ball);

            SimulationWorldState state = WorldStateCodec.Capture(1, players, ball);

            Assert.AreEqual((int)players[0].state, state.player0.state);
            Assert.AreEqual((int)players[1].state, state.player1.state);
            Assert.AreEqual((int)ball.state, state.ball.state);
        }

        private static SimulationWorldState CreateState(int frameID)
        {
            return new SimulationWorldState
            {
                frameID = frameID,
                player0 = new SimulationPlayerState
                {
                    position = new FixedVector3(
                        FixedInt.FromInt(-3),
                        FixedInt.FromInt(1),
                        FixedInt.FromInt(2)),
                    facing = FixedVector3.Forward,
                    state = (int)PlayerEntity.EState.Idle,
                    hasBall = true
                },
                player1 = new SimulationPlayerState
                {
                    position = new FixedVector3(
                        FixedInt.FromInt(3),
                        FixedInt.FromInt(4),
                        FixedInt.FromInt(5)),
                    facing = FixedVector3.Back,
                    state = (int)PlayerEntity.EState.Run,
                    hasBall = false
                },
                ball = new SimulationBallState
                {
                    position = new FixedVector3(
                        FixedInt.FromInt(6),
                        FixedInt.FromInt(7),
                        FixedInt.FromInt(8)),
                    velocity = new FixedVector3(
                        FixedInt.FromInt(9),
                        FixedInt.FromInt(10),
                        FixedInt.FromInt(11)),
                    state = (int)BallEntity.EState.Held,
                    holderPlayerIndex = 0
                }
            };
        }

        private static void CreateRuntimeWorld(
            out PlayerEntity[] players,
            out BallEntity ball)
        {
            players = new[] { new PlayerEntity(), new PlayerEntity() };
            SimulationWorldState state = CreateState(0);
            players[0].position = state.player0.position;
            players[0].facing = state.player0.facing;
            players[0].state = (PlayerEntity.EState)state.player0.state;
            players[0].hasBall = state.player0.hasBall;
            players[1].position = state.player1.position;
            players[1].facing = state.player1.facing;
            players[1].state = (PlayerEntity.EState)state.player1.state;
            players[1].hasBall = state.player1.hasBall;
            ball = new BallEntity
            {
                position = state.ball.position,
                velocity = state.ball.velocity,
                state = (BallEntity.EState)state.ball.state,
                holderPlayerIndex = state.ball.holderPlayerIndex
            };
        }

        private static void AssertState(
            SimulationWorldState expected,
            SimulationWorldState actual)
        {
            Assert.AreEqual(expected.frameID, actual.frameID);
            Assert.AreEqual(expected.player0.position, actual.player0.position);
            Assert.AreEqual(expected.player0.facing, actual.player0.facing);
            Assert.AreEqual(expected.player0.state, actual.player0.state);
            Assert.AreEqual(expected.player0.hasBall, actual.player0.hasBall);
            Assert.AreEqual(expected.player1.position, actual.player1.position);
            Assert.AreEqual(expected.player1.facing, actual.player1.facing);
            Assert.AreEqual(expected.player1.state, actual.player1.state);
            Assert.AreEqual(expected.player1.hasBall, actual.player1.hasBall);
            Assert.AreEqual(expected.ball.position, actual.ball.position);
            Assert.AreEqual(expected.ball.velocity, actual.ball.velocity);
            Assert.AreEqual(expected.ball.state, actual.ball.state);
            Assert.AreEqual(expected.ball.holderPlayerIndex, actual.ball.holderPlayerIndex);
        }
    }
}

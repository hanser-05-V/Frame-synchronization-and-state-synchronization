using System;
using System.Collections;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class WorldHashTests
    {
        private enum SnapshotField
        {
            FrameID,
            Player1X,
            Player1Y,
            Player1Z,
            Player2X,
            Player2Y,
            Player2Z,
            Player1FacingX,
            Player1FacingY,
            Player1FacingZ,
            Player2FacingX,
            Player2FacingY,
            Player2FacingZ,
            Player1State,
            Player2State,
            Player1HasBall,
            Player2HasBall,
            BallPosX,
            BallPosY,
            BallPosZ,
            BallVelX,
            BallVelY,
            BallVelZ,
            BallState,
            BallHolder
        }

        [Test]
        public void Compute_KnownSnapshot_ReturnsStableReferenceHash()
        {
            FrameSnapshot snapshot = CreateSnapshot();

            ulong hash = WorldHash.Compute(snapshot);

            Assert.AreEqual(2, WorldHash.SchemaVersion);
            Assert.AreEqual("FNV1A64", WorldHash.AlgorithmName);
            Assert.AreEqual(0x51BD0E2C3C87AFF5UL, hash);
        }

        [Test]
        public void Compute_SameSnapshotRepeated_ReturnsSameHash()
        {
            FrameSnapshot snapshot = CreateSnapshot();

            ulong first = WorldHash.Compute(snapshot);
            ulong second = WorldHash.Compute(snapshot);

            Assert.AreEqual(first, second);
        }

        [Test]
        public void Compute_WorldAndEquivalentSnapshot_ReturnSameHash()
        {
            FrameSnapshot snapshot = CreateSnapshot();

            Assert.AreEqual(
                WorldHash.Compute(snapshot, 37),
                WorldHash.Compute(snapshot.ToWorld(), 37));
        }

        [Test]
        public void Compute_DifferentLocalFramesAtSameCanonicalFrame_ReturnsSameHash()
        {
            FrameSnapshot player0Snapshot = CreateSnapshot();
            FrameSnapshot player1Snapshot = player0Snapshot;
            player1Snapshot.frameID = 12;

            ulong player0Hash = WorldHash.Compute(player0Snapshot, 37);
            ulong player1Hash = WorldHash.Compute(player1Snapshot, 37);

            Assert.AreEqual(player0Hash, player1Hash);
        }

        [Test]
        public void Compute_SameStateAtDifferentCanonicalFrames_ReturnsDifferentHash()
        {
            FrameSnapshot snapshot = CreateSnapshot();

            Assert.AreNotEqual(
                WorldHash.Compute(snapshot, 37),
                WorldHash.Compute(snapshot, 38));
        }

        [TestCaseSource(nameof(AllSnapshotFields))]
        public void Compute_SingleSynchronizedFieldChanges_ReturnsDifferentHash(
            int fieldValue)
        {
            var field = (SnapshotField)fieldValue;
            FrameSnapshot original = CreateSnapshot();
            FrameSnapshot changed = original;
            Mutate(ref changed, field);

            Assert.AreNotEqual(
                WorldHash.Compute(original),
                WorldHash.Compute(changed),
                $"字段 {field} 未参与完整世界 Hash");
        }

        [Test]
        public void SynchronizedFieldList_FrameSnapshotHasOneCanonicalStorageGraph()
        {
            CollectionAssert.AreEqual(
                new[] { "world" },
                Array.ConvertAll(
                    typeof(FrameSnapshot).GetFields(),
                    field => field.Name));
            CollectionAssert.AreEquivalent(
                new[] { "frameID", "player0", "player1", "ball" },
                Array.ConvertAll(
                    typeof(SimulationWorldState).GetFields(),
                    field => field.Name));
            CollectionAssert.AreEquivalent(
                new[] { "position", "facing", "state", "hasBall" },
                Array.ConvertAll(
                    typeof(SimulationPlayerState).GetFields(),
                    field => field.Name));
            CollectionAssert.AreEquivalent(
                new[] { "position", "velocity", "state", "holderPlayerIndex" },
                Array.ConvertAll(
                    typeof(SimulationBallState).GetFields(),
                    field => field.Name));
        }

        [Test]
        public void Compute_PlayerSlotsSwapped_ReturnsDifferentHash()
        {
            FrameSnapshot original = CreateSnapshot();
            FrameSnapshot swapped = SwapPlayers(original);

            Assert.AreNotEqual(WorldHash.Compute(original), WorldHash.Compute(swapped));
        }

        private static IEnumerable AllSnapshotFields()
        {
            foreach (SnapshotField field in Enum.GetValues(typeof(SnapshotField)))
            {
                yield return new TestCaseData((int)field)
                    .SetName($"Compute_{field}Changes_ReturnsDifferentHash");
            }
        }

        private static FrameSnapshot CreateSnapshot()
        {
            return new FrameSnapshot
            {
                frameID = 37,
                player1X = Raw(-3000),
                player1Y = Raw(1000),
                player1Z = Raw(2000),
                player2X = Raw(4000),
                player2Y = Raw(2000),
                player2Z = Raw(-5000),
                player1FacingX = Raw(1000),
                player1FacingY = Raw(0),
                player1FacingZ = Raw(0),
                player2FacingX = Raw(0),
                player2FacingY = Raw(0),
                player2FacingZ = Raw(-1000),
                player1State = (int)PlayerEntity.EState.Shooting,
                player2State = (int)PlayerEntity.EState.Run,
                player1HasBall = false,
                player2HasBall = true,
                ballPosX = Raw(2000),
                ballPosY = Raw(3000),
                ballPosZ = Raw(7000),
                ballVelX = Raw(-1000),
                ballVelY = Raw(6000),
                ballVelZ = Raw(2000),
                ballState = (int)BallEntity.EState.Airborne,
                ballHolder = -1
            };
        }

        private static FixedInt Raw(int value)
        {
            return new FixedInt(value);
        }

        private static void Mutate(ref FrameSnapshot snapshot, SnapshotField field)
        {
            switch (field)
            {
                case SnapshotField.FrameID:
                    snapshot.world.frameID++;
                    break;
                case SnapshotField.Player1X:
                    snapshot.world.player0.position.x._raw++;
                    break;
                case SnapshotField.Player1Y:
                    snapshot.world.player0.position.y._raw++;
                    break;
                case SnapshotField.Player1Z:
                    snapshot.world.player0.position.z._raw++;
                    break;
                case SnapshotField.Player2X:
                    snapshot.world.player1.position.x._raw++;
                    break;
                case SnapshotField.Player2Y:
                    snapshot.world.player1.position.y._raw++;
                    break;
                case SnapshotField.Player2Z:
                    snapshot.world.player1.position.z._raw++;
                    break;
                case SnapshotField.Player1FacingX:
                    snapshot.world.player0.facing.x._raw++;
                    break;
                case SnapshotField.Player1FacingY:
                    snapshot.world.player0.facing.y._raw++;
                    break;
                case SnapshotField.Player1FacingZ:
                    snapshot.world.player0.facing.z._raw++;
                    break;
                case SnapshotField.Player2FacingX:
                    snapshot.world.player1.facing.x._raw++;
                    break;
                case SnapshotField.Player2FacingY:
                    snapshot.world.player1.facing.y._raw++;
                    break;
                case SnapshotField.Player2FacingZ:
                    snapshot.world.player1.facing.z._raw++;
                    break;
                case SnapshotField.Player1State:
                    snapshot.world.player0.state++;
                    break;
                case SnapshotField.Player2State:
                    snapshot.world.player1.state++;
                    break;
                case SnapshotField.Player1HasBall:
                    snapshot.world.player0.hasBall = !snapshot.world.player0.hasBall;
                    break;
                case SnapshotField.Player2HasBall:
                    snapshot.world.player1.hasBall = !snapshot.world.player1.hasBall;
                    break;
                case SnapshotField.BallPosX:
                    snapshot.world.ball.position.x._raw++;
                    break;
                case SnapshotField.BallPosY:
                    snapshot.world.ball.position.y._raw++;
                    break;
                case SnapshotField.BallPosZ:
                    snapshot.world.ball.position.z._raw++;
                    break;
                case SnapshotField.BallVelX:
                    snapshot.world.ball.velocity.x._raw++;
                    break;
                case SnapshotField.BallVelY:
                    snapshot.world.ball.velocity.y._raw++;
                    break;
                case SnapshotField.BallVelZ:
                    snapshot.world.ball.velocity.z._raw++;
                    break;
                case SnapshotField.BallState:
                    snapshot.world.ball.state++;
                    break;
                case SnapshotField.BallHolder:
                    snapshot.world.ball.holderPlayerIndex++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(field), field, null);
            }
        }

        private static FrameSnapshot SwapPlayers(FrameSnapshot snapshot)
        {
            FrameSnapshot swapped = snapshot;
            swapped.player1X = snapshot.player2X;
            swapped.player1Y = snapshot.player2Y;
            swapped.player1Z = snapshot.player2Z;
            swapped.player2X = snapshot.player1X;
            swapped.player2Y = snapshot.player1Y;
            swapped.player2Z = snapshot.player1Z;
            swapped.player1FacingX = snapshot.player2FacingX;
            swapped.player1FacingY = snapshot.player2FacingY;
            swapped.player1FacingZ = snapshot.player2FacingZ;
            swapped.player2FacingX = snapshot.player1FacingX;
            swapped.player2FacingY = snapshot.player1FacingY;
            swapped.player2FacingZ = snapshot.player1FacingZ;
            swapped.player1State = snapshot.player2State;
            swapped.player2State = snapshot.player1State;
            swapped.player1HasBall = snapshot.player2HasBall;
            swapped.player2HasBall = snapshot.player1HasBall;
            return swapped;
        }
    }
}

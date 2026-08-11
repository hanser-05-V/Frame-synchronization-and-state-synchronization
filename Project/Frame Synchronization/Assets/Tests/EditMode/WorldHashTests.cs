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
        public void SynchronizedFieldList_FrameSnapshotPublicFields_MatchesEveryHashCase()
        {
            var expectedNames = new ArrayList();
            foreach (SnapshotField field in Enum.GetValues(typeof(SnapshotField)))
            {
                string name = field.ToString();
                expectedNames.Add(char.ToLowerInvariant(name[0]) + name.Substring(1));
            }

            var actualNames = new ArrayList();
            foreach (var field in typeof(FrameSnapshot).GetFields())
                actualNames.Add(field.Name);

            CollectionAssert.AreEquivalent(expectedNames, actualNames);
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
                    snapshot.frameID++;
                    break;
                case SnapshotField.Player1X:
                    snapshot.player1X._raw++;
                    break;
                case SnapshotField.Player1Y:
                    snapshot.player1Y._raw++;
                    break;
                case SnapshotField.Player1Z:
                    snapshot.player1Z._raw++;
                    break;
                case SnapshotField.Player2X:
                    snapshot.player2X._raw++;
                    break;
                case SnapshotField.Player2Y:
                    snapshot.player2Y._raw++;
                    break;
                case SnapshotField.Player2Z:
                    snapshot.player2Z._raw++;
                    break;
                case SnapshotField.Player1FacingX:
                    snapshot.player1FacingX._raw++;
                    break;
                case SnapshotField.Player1FacingY:
                    snapshot.player1FacingY._raw++;
                    break;
                case SnapshotField.Player1FacingZ:
                    snapshot.player1FacingZ._raw++;
                    break;
                case SnapshotField.Player2FacingX:
                    snapshot.player2FacingX._raw++;
                    break;
                case SnapshotField.Player2FacingY:
                    snapshot.player2FacingY._raw++;
                    break;
                case SnapshotField.Player2FacingZ:
                    snapshot.player2FacingZ._raw++;
                    break;
                case SnapshotField.Player1State:
                    snapshot.player1State++;
                    break;
                case SnapshotField.Player2State:
                    snapshot.player2State++;
                    break;
                case SnapshotField.Player1HasBall:
                    snapshot.player1HasBall = !snapshot.player1HasBall;
                    break;
                case SnapshotField.Player2HasBall:
                    snapshot.player2HasBall = !snapshot.player2HasBall;
                    break;
                case SnapshotField.BallPosX:
                    snapshot.ballPosX._raw++;
                    break;
                case SnapshotField.BallPosY:
                    snapshot.ballPosY._raw++;
                    break;
                case SnapshotField.BallPosZ:
                    snapshot.ballPosZ._raw++;
                    break;
                case SnapshotField.BallVelX:
                    snapshot.ballVelX._raw++;
                    break;
                case SnapshotField.BallVelY:
                    snapshot.ballVelY._raw++;
                    break;
                case SnapshotField.BallVelZ:
                    snapshot.ballVelZ._raw++;
                    break;
                case SnapshotField.BallState:
                    snapshot.ballState++;
                    break;
                case SnapshotField.BallHolder:
                    snapshot.ballHolder++;
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

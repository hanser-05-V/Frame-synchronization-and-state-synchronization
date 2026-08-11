using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class FrameInputTests
    {
        [Test]
        public void PickupPressed_SetAndClear_UsesBitFive()
        {
            var input = new FrameInput();

            input.pickupPressed = true;

            Assert.AreEqual(0x20u, input._raw);
            Assert.IsTrue(FrameInput.FromRaw(0x20u).pickupPressed);

            input.pickupPressed = false;

            Assert.AreEqual(0u, input._raw);
        }

        [Test]
        public void ShootReleased_SetAndClear_UsesBitZeroAndShootAlias()
        {
            var input = new FrameInput();

            input.shootReleased = true;

            Assert.AreEqual(0x01u, input._raw);
            Assert.IsTrue(input.shoot);

            input.shoot = false;

            Assert.IsFalse(input.shootReleased);
            Assert.AreEqual(0u, input._raw);
        }

        [Test]
        public void ToPredictionInput_TransientBitsSet_ClearsOnlyTransientBits()
        {
            var authoritative = FrameInput.FromRaw(0xA5A50000u);
            authoritative.moveDir = 7;
            authoritative.buttons = 0x3F;

            FrameInput predicted = authoritative.ToPredictionInput();

            Assert.IsFalse(predicted.pickupPressed);
            Assert.IsFalse(predicted.shootReleased);
            Assert.AreEqual(authoritative._raw & ~0x21u, predicted._raw);
            Assert.AreEqual(7, predicted.moveDir);
            Assert.IsTrue(predicted.pass);
            Assert.IsTrue(predicted.steal);
            Assert.IsTrue(predicted.block);
            Assert.IsTrue(predicted.sprint);
        }

        [Test]
        public void EndMatchPressed_SetAndPredict_UsesTransientBitSix()
        {
            var authoritative = new FrameInput();
            authoritative.endMatchPressed = true;

            Assert.AreEqual(0x40u, authoritative._raw);
            FrameInput predicted = authoritative.ToPredictionInput();
            Assert.AreEqual(0u, predicted._raw & 0x40u);
        }
    }
}

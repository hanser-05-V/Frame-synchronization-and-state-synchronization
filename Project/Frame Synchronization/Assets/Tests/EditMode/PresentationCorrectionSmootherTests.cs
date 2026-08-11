using System;
using NUnit.Framework;
using UnityEngine;

namespace FrameSyncDemo.Tests
{
    public class PresentationCorrectionSmootherTests
    {
        [Test]
        public void Evaluate_WithoutCorrection_ReturnsCurrentTarget()
        {
            var smoother = new PresentationCorrectionSmoother(0.1f);
            var target = new Vector3(1f, 2f, 3f);

            Vector3 result = smoother.Evaluate(target, 0.016f);

            Assert.AreEqual(target, result);
            Assert.IsFalse(smoother.IsCorrecting);
        }

        [Test]
        public void BeginCorrection_FirstEvaluate_ConsumesTimeAndDoesNotFreeze()
        {
            var smoother = new PresentationCorrectionSmoother(0.1f);
            var previousDisplay = new Vector3(1f, 0f, 0f);

            smoother.BeginCorrection(previousDisplay, Vector3.zero);
            Vector3 result = smoother.Evaluate(Vector3.zero, 0.016f);

            Assert.Less(result.x, previousDisplay.x);
            Assert.Greater(result.x, 0f);
            Assert.IsTrue(smoother.IsCorrecting);
        }

        [Test]
        public void BeginCorrection_MovingDisplay_FirstStepPreservesForwardMotion()
        {
            var smoother = new PresentationCorrectionSmoother(0.1f);
            smoother.Evaluate(Vector3.zero, 0.016f);
            smoother.Evaluate(new Vector3(0.16f, 0f, 0f), 0.016f);

            smoother.BeginCorrection(
                new Vector3(0.16f, 0f, 0f),
                new Vector3(-0.1f, 0f, 0f));
            Vector3 result = smoother.Evaluate(
                new Vector3(-0.1f, 0f, 0f),
                0.016f);

            Assert.Greater(result.x, 0.16f);
        }

        [Test]
        public void BeginCorrection_MovingAway_DeceleratesBeforeReversing()
        {
            var smoother = new PresentationCorrectionSmoother(
                0.1f,
                0.2f,
                10f,
                2f);
            smoother.Evaluate(Vector3.zero, 0.016f);
            smoother.Evaluate(new Vector3(0.16f, 0f, 0f), 0.016f);
            var correctedTarget = new Vector3(-0.1f, 0f, 0f);

            smoother.BeginCorrection(
                new Vector3(0.16f, 0f, 0f),
                correctedTarget);
            Vector3 first = smoother.Evaluate(correctedTarget, 0.016f);
            Vector3 second = smoother.Evaluate(correctedTarget, 0.016f);
            Vector3 third = smoother.Evaluate(correctedTarget, 0.016f);

            Assert.Greater(first.x, 0.16f);
            Assert.GreaterOrEqual(second.x, first.x);
            Assert.GreaterOrEqual(third.x, second.x);
        }

        [Test]
        public void BeginCorrection_MovingTowardNearbyTarget_DoesNotOvershootOrReverse()
        {
            var smoother = new PresentationCorrectionSmoother(
                0.1f,
                0.2f,
                10f,
                2f);
            smoother.Evaluate(Vector3.zero, 0.016f);
            smoother.Evaluate(new Vector3(0.16f, 0f, 0f), 0.016f);
            var correctedTarget = new Vector3(0.21f, 0f, 0f);
            smoother.BeginCorrection(
                new Vector3(0.16f, 0f, 0f),
                correctedTarget);

            float previousX = 0.16f;
            for (int i = 0; i < 8; i++)
            {
                Vector3 result = smoother.Evaluate(
                    correctedTarget,
                    0.016f);
                Assert.GreaterOrEqual(result.x, previousX - 0.0001f);
                Assert.LessOrEqual(result.x, correctedTarget.x + 0.0001f);
                previousX = result.x;
            }
        }

        [Test]
        public void Evaluate_DurationElapsed_ReturnsLatestTargetExactly()
        {
            var smoother = new PresentationCorrectionSmoother(
                0.1f,
                0.2f,
                10f,
                2f);
            smoother.BeginCorrection(new Vector3(1f, 0f, 0f), Vector3.zero);

            Vector3 progressing = smoother.Evaluate(
                new Vector3(1f, 0f, 0f),
                0.05f);
            Vector3 completed = smoother.Evaluate(
                new Vector3(2f, 0f, 0f),
                0.2f);

            Assert.AreNotEqual(new Vector3(1f, 0f, 0f), progressing);
            Assert.AreEqual(new Vector3(2f, 0f, 0f), completed);
            Assert.IsFalse(smoother.IsCorrecting);
        }

        [Test]
        public void Evaluate_MovingTarget_PreservesDecayingOffsetAndFollowsTarget()
        {
            var smoother = new PresentationCorrectionSmoother(
                0.1f,
                0.2f,
                10f,
                2f);
            smoother.BeginCorrection(new Vector3(1f, 0f, 0f), Vector3.zero);

            Vector3 result = smoother.Evaluate(
                new Vector3(0.5f, 0f, 0f),
                0.05f);

            Assert.Greater(result.x, 0.5f);
            Assert.Less(result.x, 1.5f);
        }

        [Test]
        public void BeginCorrection_WhileCorrecting_RestartsFromCurrentDisplay()
        {
            var smoother = new PresentationCorrectionSmoother(
                0.1f,
                0.2f,
                10f,
                2f);
            smoother.BeginCorrection(new Vector3(1f, 0f, 0f), Vector3.zero);
            Vector3 currentDisplay = smoother.Evaluate(Vector3.zero, 0.05f);

            smoother.BeginCorrection(
                currentDisplay,
                new Vector3(-0.25f, 0f, 0f));
            Vector3 restarted = smoother.Evaluate(
                new Vector3(-0.25f, 0f, 0f),
                0.016f);

            Assert.AreNotEqual(currentDisplay, restarted);
            Assert.LessOrEqual(
                Vector3.Distance(restarted, currentDisplay),
                0.1601f);
            Assert.IsTrue(smoother.IsCorrecting);
        }

        [Test]
        public void Evaluate_CorrectionChange_DoesNotExceedConfiguredSpeed()
        {
            var smoother = new PresentationCorrectionSmoother(
                0.1f,
                0.2f,
                1f,
                0.2f);
            var previousDisplay = new Vector3(0.19f, 0f, 0f);
            smoother.BeginCorrection(previousDisplay, Vector3.zero);

            Vector3 result = smoother.Evaluate(Vector3.zero, 0.016f);

            Assert.LessOrEqual(
                Vector3.Distance(previousDisplay, result),
                0.0161f);
        }

        [Test]
        public void BeginCorrection_AtSnapDistance_ReturnsTargetImmediately()
        {
            var smoother = new PresentationCorrectionSmoother(
                0.1f,
                0.2f,
                10f,
                2f);
            smoother.BeginCorrection(new Vector3(2f, 0f, 0f), Vector3.zero);

            Vector3 result = smoother.Evaluate(Vector3.zero, 0.016f);

            Assert.AreEqual(Vector3.zero, result);
            Assert.IsFalse(smoother.IsCorrecting);
        }

        [Test]
        public void BeginCorrection_ZeroOffset_DoesNotEnterCorrection()
        {
            var smoother = new PresentationCorrectionSmoother(0.1f);
            var target = new Vector3(2f, 3f, 4f);

            smoother.BeginCorrection(target, target);

            Assert.IsFalse(smoother.IsCorrecting);
            Assert.AreEqual(target, smoother.Evaluate(target, 0.05f));
        }

        [Test]
        public void Snap_DuringCorrection_ClearsOffsetImmediately()
        {
            var smoother = new PresentationCorrectionSmoother(0.1f);
            smoother.BeginCorrection(new Vector3(1f, 0f, 0f), Vector3.zero);

            smoother.Snap();
            Vector3 result = smoother.Evaluate(
                new Vector3(2f, 0f, 0f),
                0.016f);

            Assert.AreEqual(new Vector3(2f, 0f, 0f), result);
            Assert.IsFalse(smoother.IsCorrecting);
        }

        [TestCase(0f)]
        [TestCase(-0.1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void Constructor_InvalidDuration_Throws(float duration)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new PresentationCorrectionSmoother(duration));
        }

        [Test]
        public void Constructor_MaximumAllowedSingleDuration_DoesNotThrow()
        {
            Assert.DoesNotThrow(
                () => new PresentationCorrectionSmoother(0.2f));
        }

        [Test]
        public void Constructor_InvalidExtendedConfiguration_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new PresentationCorrectionSmoother(0.2f, 0.1f, 10f, 2f));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new PresentationCorrectionSmoother(0.1f, 0.2f, 0f, 2f));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new PresentationCorrectionSmoother(0.1f, 0.2f, 10f, 0f));
        }

        [Test]
        public void Constructor_UnusableParameterCombination_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new PresentationCorrectionSmoother(0.1f, 0.3f, 10f, 2f));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new PresentationCorrectionSmoother(
                    0.1f,
                    0.2f,
                    float.Epsilon,
                    2f));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new PresentationCorrectionSmoother(0.1f, 0.2f, 1f, 2f));
        }

        [TestCase(-0.1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void Evaluate_InvalidDeltaTime_TreatsItAsZero(float deltaTime)
        {
            var smoother = new PresentationCorrectionSmoother(0.1f);
            smoother.BeginCorrection(new Vector3(1f, 0f, 0f), Vector3.zero);

            Vector3 result = smoother.Evaluate(
                new Vector3(1f, 0f, 0f),
                deltaTime);

            Assert.AreEqual(new Vector3(1f, 0f, 0f), result);
            Assert.IsTrue(smoother.IsCorrecting);
        }
    }
}

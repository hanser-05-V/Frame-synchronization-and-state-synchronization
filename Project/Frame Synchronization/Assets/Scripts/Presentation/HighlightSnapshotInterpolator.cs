using UnityEngine;

namespace FrameSyncDemo
{
    public static class HighlightSnapshotInterpolator
    {
        private static readonly Vector3 PlayerVisualOffset =
            Vector3.up * 0.5f;

        public static HighlightPresentationSample Interpolate(
            FrameSnapshot from,
            FrameSnapshot to,
            float alpha)
        {
            float clampedAlpha = Mathf.Clamp01(alpha);
            Vector3 fromPlayer0 = new Vector3(
                from.player1X.ToFloat(),
                from.player1Y.ToFloat(),
                from.player1Z.ToFloat()) + PlayerVisualOffset;
            Vector3 toPlayer0 = new Vector3(
                to.player1X.ToFloat(),
                to.player1Y.ToFloat(),
                to.player1Z.ToFloat()) + PlayerVisualOffset;
            Vector3 fromPlayer1 = new Vector3(
                from.player2X.ToFloat(),
                from.player2Y.ToFloat(),
                from.player2Z.ToFloat()) + PlayerVisualOffset;
            Vector3 toPlayer1 = new Vector3(
                to.player2X.ToFloat(),
                to.player2Y.ToFloat(),
                to.player2Z.ToFloat()) + PlayerVisualOffset;
            Vector3 fromBall = new Vector3(
                from.ballPosX.ToFloat(),
                from.ballPosY.ToFloat(),
                from.ballPosZ.ToFloat());
            Vector3 toBall = new Vector3(
                to.ballPosX.ToFloat(),
                to.ballPosY.ToFloat(),
                to.ballPosZ.ToFloat());

            return new HighlightPresentationSample
            {
                player0Position = Vector3.Lerp(fromPlayer0, toPlayer0, clampedAlpha),
                player1Position = Vector3.Lerp(fromPlayer1, toPlayer1, clampedAlpha),
                ballPosition = Vector3.Lerp(fromBall, toBall, clampedAlpha)
            };
        }
    }
}

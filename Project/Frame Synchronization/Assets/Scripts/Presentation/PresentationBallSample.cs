using UnityEngine;

namespace FrameSyncDemo
{
    /// <summary>
    /// 一次表现求值产生的篮球基准及表现附着键。
    /// </summary>
    public readonly struct PresentationBallSample
    {
        public PresentationBallSample(
            Vector3 basePosition,
            int attachedPlayerIndex)
        {
            BasePosition = basePosition;
            AttachedPlayerIndex = attachedPlayerIndex;
        }

        public Vector3 BasePosition { get; }
        public int AttachedPlayerIndex { get; }
        public bool IsAttached => AttachedPlayerIndex >= 0;
    }
}

using System;

namespace FrameSyncDemo
{
    /// <summary>
    /// 将各客户端本地帧映射到 Player0 的统一世界时间线。
    /// </summary>
    public static class CanonicalFrame
    {
        public static bool TryFromLocal(
            int localPlayerIndex,
            int localFrame,
            int remoteFrameOffset,
            out int canonicalFrame)
        {
            if (localPlayerIndex == 0)
            {
                canonicalFrame = localFrame;
                return localFrame >= 0;
            }

            if (localPlayerIndex != 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(localPlayerIndex),
                    localPlayerIndex,
                    "Only Player0 and Player1 are supported.");
            }

            if (remoteFrameOffset == int.MinValue)
            {
                canonicalFrame = -1;
                return false;
            }

            long mapped = (long)localFrame - remoteFrameOffset;
            if (mapped < 0 || mapped > int.MaxValue)
            {
                canonicalFrame = -1;
                return false;
            }

            canonicalFrame = (int)mapped;
            return true;
        }

        public static bool TryToLocal(
            int localPlayerIndex,
            int canonicalFrame,
            int remoteFrameOffset,
            out int localFrame)
        {
            if (localPlayerIndex == 0)
            {
                localFrame = canonicalFrame;
                return canonicalFrame >= 0;
            }

            if (localPlayerIndex != 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(localPlayerIndex),
                    localPlayerIndex,
                    "Only Player0 and Player1 are supported.");
            }

            if (canonicalFrame < 0 || remoteFrameOffset == int.MinValue)
            {
                localFrame = -1;
                return false;
            }

            long mapped = (long)canonicalFrame + remoteFrameOffset;
            if (mapped < 0 || mapped > int.MaxValue)
            {
                localFrame = -1;
                return false;
            }

            localFrame = (int)mapped;
            return true;
        }
    }
}

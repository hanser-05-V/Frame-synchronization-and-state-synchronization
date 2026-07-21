namespace FrameSyncDemo
{
    /// <summary>
    /// 脱同步检测器 (v1 — XOR轻量校验)
    /// 将所有确定性状态字段压入XOR hash，两端应产出相同值。
    /// 阶段4 将通过网络传输 hash 进行对照比对。
    ///
    /// 校验字段:
    ///   - frameID: 当前帧号
    ///   - player posX/posZ: 方块位置 (阶段1后将扩展为 FixedVector3 + 球状态 + 状态机)
    /// </summary>
    public static class MD5Checker
    {
        /// <summary>每多少帧输出一次校验日志 (0=禁用)</summary>
        public static int LogIntervalFrames = 200;

        private static uint _lastHash = 0;
        private static int _lastCheckFrame = -1;

        /// <summary>
        /// 计算当前帧的 XOR 状态 hash
        /// </summary>
        public static uint ComputeHash(int frameID, FixedInt[] posX, FixedInt[] posZ)
        {
            uint hash = 0;

            // ① 帧号
            hash ^= (uint)frameID;

            // ② 所有玩家位置
            for (int i = 0; i < posX.Length; i++)
            {
                hash ^= (uint)posX[i]._raw;
                hash ^= (uint)posZ[i]._raw;
            }

            return hash;
        }

        /// <summary>
        /// 在 OnFrameUpdate 末尾调用。每 LogIntervalFrames 帧输出一次 hash 日志，
        /// 双端可对比日志确认状态一致。
        /// </summary>
        public static void CheckAndLog(int frameID, FixedInt[] posX, FixedInt[] posZ)
        {
            if (LogIntervalFrames <= 0) return;

            if (frameID % LogIntervalFrames == 0 && frameID > 0)
            {
                uint hash = ComputeHash(frameID, posX, posZ);

                if (_lastCheckFrame < 0)
                {
                    _lastHash = hash;
                    _lastCheckFrame = frameID;
                    UnityEngine.Debug.Log($"[MD5] 帧{frameID} hash=0x{hash:X8} (首次记录)");
                }
                else
                {
                    bool consistent = (hash == _lastHash && frameID == _lastCheckFrame + LogIntervalFrames);
                    if (consistent)
                    {
                        UnityEngine.Debug.Log($"[MD5] 帧{frameID} hash=0x{hash:X8} ✅ 与上轮间隔一致");
                    }
                    else
                    {
                        UnityEngine.Debug.LogWarning($"[MD5] 帧{frameID} hash=0x{hash:X8} ⚠️ 异常! " +
                            $"上轮: frame={_lastCheckFrame} hash=0x{_lastHash:X8}");
                    }
                    _lastHash = hash;
                    _lastCheckFrame = frameID;
                }
            }
        }
    }
}

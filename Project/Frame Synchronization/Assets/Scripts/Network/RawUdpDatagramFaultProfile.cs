using System;

namespace FrameSyncDemo
{
    public readonly struct RawUdpDatagramFaultProfile
    {
        public RawUdpDatagramFaultProfile(
            int delayMs,
            int jitterMs,
            int dropPercent,
            int reorderPercent,
            int reorderExtraDelayMs,
            int duplicatePercent,
            uint seed)
        {
            ValidateTime(delayMs, nameof(delayMs));
            ValidateTime(jitterMs, nameof(jitterMs));
            ValidatePercent(dropPercent, nameof(dropPercent));
            ValidatePercent(reorderPercent, nameof(reorderPercent));
            ValidateTime(reorderExtraDelayMs, nameof(reorderExtraDelayMs));
            ValidatePercent(duplicatePercent, nameof(duplicatePercent));

            DelayMs = delayMs;
            JitterMs = jitterMs;
            DropPercent = dropPercent;
            ReorderPercent = reorderPercent;
            ReorderExtraDelayMs = reorderExtraDelayMs;
            DuplicatePercent = duplicatePercent;
            Seed = seed;
        }

        public int DelayMs { get; }
        public int JitterMs { get; }
        public int DropPercent { get; }
        public int ReorderPercent { get; }
        public int ReorderExtraDelayMs { get; }
        public int DuplicatePercent { get; }
        public uint Seed { get; }

        private static void ValidateTime(int value, string parameterName)
        {
            if (value < 0 || value > 5000)
                throw new ArgumentOutOfRangeException(parameterName);
        }

        private static void ValidatePercent(int value, string parameterName)
        {
            if (value < 0 || value > 100)
                throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

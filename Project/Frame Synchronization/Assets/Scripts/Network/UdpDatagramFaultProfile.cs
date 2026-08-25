using System;

namespace FrameSyncDemo
{
    public readonly struct UdpDatagramFaultProfile
    {
        public const int MaximumDelayMs = 5000;

        public UdpDatagramFaultProfile(
            int delayMs,
            int jitterMs,
            int dropPercent,
            int reorderPercent,
            int reorderExtraDelayMs,
            int duplicatePercent,
            uint seed)
        {
            ValidateDelay(delayMs, nameof(delayMs));
            ValidateDelay(jitterMs, nameof(jitterMs));
            ValidatePercent(dropPercent, nameof(dropPercent));
            ValidatePercent(reorderPercent, nameof(reorderPercent));
            ValidateDelay(reorderExtraDelayMs, nameof(reorderExtraDelayMs));
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

        private static void ValidateDelay(int value, string parameterName)
        {
            if (value < 0 || value > MaximumDelayMs)
                throw new ArgumentOutOfRangeException(parameterName);
        }

        private static void ValidatePercent(int value, string parameterName)
        {
            if (value < 0 || value > 100)
                throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

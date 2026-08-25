using System;

namespace FrameSyncDemo
{
    public readonly struct NetworkLabProfile
    {
        public const int MaximumDelayComponentMs = 600000;

        public NetworkLabProfile(
            int baseDelayMs,
            int jitterMs,
            int applicationReorderPercent,
            int reorderExtraDelayMs,
            int duplicatePercent,
            int recoveredLossPercent,
            int lossRecoveryDelayMs,
            uint seed)
        {
            BaseDelayMs = ValidateDelay(
                baseDelayMs,
                nameof(baseDelayMs));
            JitterMs = ValidateDelay(
                jitterMs,
                nameof(jitterMs));
            ApplicationReorderPercent = ValidatePercent(
                applicationReorderPercent,
                nameof(applicationReorderPercent));
            ReorderExtraDelayMs = ValidateDelay(
                reorderExtraDelayMs,
                nameof(reorderExtraDelayMs));
            DuplicatePercent = ValidatePercent(
                duplicatePercent,
                nameof(duplicatePercent));
            RecoveredLossPercent = ValidatePercent(
                recoveredLossPercent,
                nameof(recoveredLossPercent));
            LossRecoveryDelayMs = ValidateDelay(
                lossRecoveryDelayMs,
                nameof(lossRecoveryDelayMs));
            Seed = seed;
        }

        public int BaseDelayMs { get; }
        public int JitterMs { get; }
        public int ApplicationReorderPercent { get; }
        public int ReorderExtraDelayMs { get; }
        public int DuplicatePercent { get; }
        public int RecoveredLossPercent { get; }
        public int LossRecoveryDelayMs { get; }
        public uint Seed { get; }

        private static int ValidateDelay(int value, string parameterName)
        {
            if (value < 0 || value > MaximumDelayComponentMs)
                throw new ArgumentOutOfRangeException(parameterName);

            return value;
        }

        private static int ValidatePercent(int value, string parameterName)
        {
            if (value < 0 || value > 100)
                throw new ArgumentOutOfRangeException(parameterName);

            return value;
        }
    }
}

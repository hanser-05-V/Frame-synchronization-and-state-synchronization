using System;
using System.Globalization;

namespace FrameSyncServer
{
    public sealed class NetworkLabOptions
    {
        private NetworkLabOptions(
            FrameSyncDemo.NetworkLabProfile profile,
            string decisionTracePath,
            string timingTracePath)
        {
            Profile = profile;
            DecisionTracePath = decisionTracePath ?? string.Empty;
            TimingTracePath = timingTracePath ?? string.Empty;
        }

        public FrameSyncDemo.NetworkLabProfile Profile { get; }
        public string DecisionTracePath { get; }
        public string TimingTracePath { get; }

        public static NetworkLabOptions FromInteractiveChoice(string choice)
        {
            int delayMs = 0;
            if (choice == "1")
                delayMs = 100;
            else if (choice == "2")
                delayMs = 200;

            return new NetworkLabOptions(
                new FrameSyncDemo.NetworkLabProfile(
                    delayMs,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    1u),
                string.Empty,
                string.Empty);
        }

        public static NetworkLabOptions Parse(string[] args)
        {
            if (args == null)
                throw new ArgumentNullException(nameof(args));

            int baseDelayMs = 0;
            int jitterMs = 0;
            int reorderPercent = 0;
            int reorderDelayMs = 0;
            int duplicatePercent = 0;
            int lossPercent = 0;
            int lossRecoveryMs = 0;
            uint seed = 1u;
            string decisionTracePath = string.Empty;
            string timingTracePath = string.Empty;

            for (int index = 0; index < args.Length; index++)
            {
                string option = args[index];
                if (index + 1 >= args.Length)
                    throw new ArgumentException("Missing value for " + option + ".", nameof(args));

                string value = args[++index];
                switch (option)
                {
                    case "--delay-ms":
                        baseDelayMs = ParseInt(value, option);
                        break;
                    case "--jitter-ms":
                        jitterMs = ParseInt(value, option);
                        break;
                    case "--reorder-percent":
                        reorderPercent = ParseInt(value, option);
                        break;
                    case "--reorder-delay-ms":
                        reorderDelayMs = ParseInt(value, option);
                        break;
                    case "--duplicate-percent":
                        duplicatePercent = ParseInt(value, option);
                        break;
                    case "--loss-percent":
                        lossPercent = ParseInt(value, option);
                        break;
                    case "--loss-recovery-ms":
                        lossRecoveryMs = ParseInt(value, option);
                        break;
                    case "--seed":
                        seed = ParseUInt(value, option);
                        break;
                    case "--decision-trace":
                        decisionTracePath = value;
                        break;
                    case "--timing-trace":
                        timingTracePath = value;
                        break;
                    default:
                        throw new ArgumentException("Unknown network lab option: " + option, nameof(args));
                }
            }

            return new NetworkLabOptions(
                new FrameSyncDemo.NetworkLabProfile(
                    baseDelayMs,
                    jitterMs,
                    reorderPercent,
                    reorderDelayMs,
                    duplicatePercent,
                    lossPercent,
                    lossRecoveryMs,
                    seed),
                decisionTracePath,
                timingTracePath);
        }

        private static int ParseInt(string value, string option)
        {
            if (!int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int result))
            {
                throw new ArgumentException("Invalid integer for " + option + ": " + value);
            }

            return result;
        }

        private static uint ParseUInt(string value, string option)
        {
            if (!uint.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out uint result))
            {
                throw new ArgumentException("Invalid unsigned integer for " + option + ": " + value);
            }

            return result;
        }
    }
}

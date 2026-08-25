using System;
using System.Collections.Generic;
using System.Globalization;
using FrameSyncDemo;

namespace FrameSyncServer
{
    public sealed class ServerOptions
    {
        private const int DefaultKcpIntervalMs = 10;
        private const int DefaultRawWindowSize = 6;

        private ServerOptions(
            NetworkTransportKind transport,
            int kcpIntervalMs,
            int rawWindowSize,
            string kcpStopFilePath,
            string kcpDiagnosticsOutputPath,
            NetworkLabOptions labOptions,
            bool useLegacyInteractive)
        {
            Transport = transport;
            KcpIntervalMs = kcpIntervalMs;
            RawWindowSize = rawWindowSize;
            KcpStopFilePath = kcpStopFilePath;
            KcpDiagnosticsOutputPath = kcpDiagnosticsOutputPath;
            LabOptions = labOptions ?? throw new ArgumentNullException(nameof(labOptions));
            UseLegacyInteractive = useLegacyInteractive;
        }

        public NetworkTransportKind Transport { get; }
        public int KcpIntervalMs { get; }
        public int RawWindowSize { get; }
        public string KcpStopFilePath { get; }
        public string KcpDiagnosticsOutputPath { get; }
        public NetworkLabOptions LabOptions { get; }
        public bool UseLegacyInteractive { get; }

        public static ServerOptions Parse(string[] args)
        {
            if (args == null)
            {
                throw new ArgumentNullException(nameof(args));
            }

            if (args.Length == 0)
            {
                return new ServerOptions(
                    NetworkTransportKind.Tcp,
                    DefaultKcpIntervalMs,
                    DefaultRawWindowSize,
                    null,
                    null,
                    NetworkLabOptions.FromInteractiveChoice("0"),
                    true);
            }

            NetworkTransportKind transport = NetworkTransportKind.KcpUdp;
            int kcpIntervalMs = DefaultKcpIntervalMs;
            int rawWindowSize = DefaultRawWindowSize;
            bool rawWindowSpecified = false;
            string kcpStopFilePath = null;
            string kcpDiagnosticsOutputPath = null;
            var labArguments = new List<string>();
            for (int index = 0; index < args.Length; index++)
            {
                string option = args[index];
                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException(
                        "Missing value for " + option + ".",
                        nameof(args));
                }

                string value = args[++index];
                switch (option)
                {
                    case "--transport":
                        transport = ParseTransport(value);
                        break;
                    case "--kcp-interval-ms":
                        kcpIntervalMs = ParseKcpInterval(value);
                        break;
                    case "--raw-window":
                        rawWindowSize = ParseRawWindow(value);
                        rawWindowSpecified = true;
                        break;
                    case "--kcp-stop-file":
                        kcpStopFilePath = ParseNonEmptyPath(option, value);
                        break;
                    case "--kcp-diagnostics-output":
                        kcpDiagnosticsOutputPath = ParseNonEmptyPath(
                            option,
                            value);
                        break;
                    default:
                        labArguments.Add(option);
                        labArguments.Add(value);
                        break;
                }
            }

            if (rawWindowSpecified &&
                transport != NetworkTransportKind.RawUdp)
            {
                throw new ArgumentException(
                    "--raw-window requires --transport raw-udp.",
                    nameof(args));
            }
            bool hasKcpStopFile = kcpStopFilePath != null;
            bool hasKcpDiagnosticsOutput = kcpDiagnosticsOutputPath != null;
            if (hasKcpStopFile != hasKcpDiagnosticsOutput)
            {
                throw new ArgumentException(
                    "--kcp-stop-file and --kcp-diagnostics-output must be " +
                    "specified together.",
                    nameof(args));
            }
            if (hasKcpStopFile && transport != NetworkTransportKind.KcpUdp)
            {
                throw new ArgumentException(
                    "KCP probe control options require --transport kcp.",
                    nameof(args));
            }

            return new ServerOptions(
                transport,
                kcpIntervalMs,
                rawWindowSize,
                kcpStopFilePath,
                kcpDiagnosticsOutputPath,
                NetworkLabOptions.Parse(labArguments.ToArray()),
                false);
        }

        public static ServerOptions FromInteractiveChoice(string choice)
        {
            return new ServerOptions(
                NetworkTransportKind.Tcp,
                DefaultKcpIntervalMs,
                DefaultRawWindowSize,
                null,
                null,
                NetworkLabOptions.FromInteractiveChoice(choice),
                false);
        }

        private static NetworkTransportKind ParseTransport(string value)
        {
            if (string.Equals(value, "tcp", StringComparison.OrdinalIgnoreCase))
            {
                return NetworkTransportKind.Tcp;
            }

            if (string.Equals(value, "kcp", StringComparison.OrdinalIgnoreCase))
            {
                return NetworkTransportKind.KcpUdp;
            }

            if (string.Equals(
                value,
                "raw-udp",
                StringComparison.OrdinalIgnoreCase))
            {
                return NetworkTransportKind.RawUdp;
            }

            throw new ArgumentException("Unknown transport: " + value);
        }

        private static int ParseKcpInterval(string value)
        {
            int intervalMs;
            if (!int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out intervalMs) ||
                (intervalMs != 1 &&
                 intervalMs != 5 &&
                 intervalMs != 10 &&
                 intervalMs != 20))
            {
                throw new ArgumentException(
                    "--kcp-interval-ms must be one of 1, 5, 10, or 20: " +
                    value);
            }

            return intervalMs;
        }

        private static int ParseRawWindow(string value)
        {
            if (!int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int windowSize) ||
                windowSize < RouteCProtocolConstants.RawMinimumWindowSize ||
                windowSize > RouteCProtocolConstants.RawMaximumWindowSize)
            {
                throw new ArgumentException(
                    "--raw-window must be an integer from 1 through 16: " +
                    value);
            }

            return windowSize;
        }

        private static string ParseNonEmptyPath(string option, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    option + " requires a non-empty path.",
                    nameof(value));
            }
            return value;
        }
    }
}

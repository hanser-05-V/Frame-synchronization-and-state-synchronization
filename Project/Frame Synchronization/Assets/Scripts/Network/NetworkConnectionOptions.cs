using System;

namespace FrameSyncDemo
{
    public sealed class NetworkConnectionOptions
    {
        public NetworkConnectionOptions(
            NetworkTransportKind transport,
            string host,
            int port)
        {
            if (!Enum.IsDefined(typeof(NetworkTransportKind), transport))
                throw new ArgumentOutOfRangeException(nameof(transport));

            string normalizedHost = host?.Trim();
            if (string.IsNullOrEmpty(normalizedHost) ||
                Uri.CheckHostName(normalizedHost) == UriHostNameType.Unknown)
            {
                throw new ArgumentException(
                    "Server host must be a valid IP literal or hostname.",
                    nameof(host));
            }
            if (port < 1 || port > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(port));

            Transport = transport;
            Host = normalizedHost;
            Port = port;
        }

        public NetworkTransportKind Transport { get; }
        public string Host { get; }
        public int Port { get; }

        public static NetworkConnectionOptions Resolve(
            string[] arguments,
            NetworkTransportKind inspectorTransport,
            string inspectorHost,
            int inspectorPort)
        {
            if (arguments == null)
                throw new ArgumentNullException(nameof(arguments));
            if (!Enum.IsDefined(
                    typeof(NetworkTransportKind),
                    inspectorTransport))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(inspectorTransport));
            }

            NetworkTransportKind transport = inspectorTransport;
            string host = string.IsNullOrWhiteSpace(inspectorHost)
                ? NetworkConfig.DEFAULT_IP
                : inspectorHost;
            int port = inspectorPort == 0
                ? NetworkConfig.DEFAULT_PORT
                : inspectorPort;

            for (int index = 0; index < arguments.Length; index++)
            {
                string argument = arguments[index] ?? string.Empty;
                if (TryReadOption(
                        arguments,
                        ref index,
                        argument,
                        "--transport",
                        out string transportValue))
                {
                    transport = ParseTransport(transportValue);
                }
                else if (TryReadOption(
                             arguments,
                             ref index,
                             argument,
                             "--server-ip",
                             out string hostValue))
                {
                    host = hostValue;
                }
                else if (TryReadOption(
                             arguments,
                             ref index,
                             argument,
                             "--server-port",
                             out string portValue))
                {
                    if (!int.TryParse(portValue, out port))
                    {
                        throw new ArgumentException(
                            "Server port must be an integer.",
                            nameof(arguments));
                    }
                }
            }

            return new NetworkConnectionOptions(transport, host, port);
        }

        private static NetworkTransportKind ParseTransport(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "kcp":
                    return NetworkTransportKind.KcpUdp;
                case "tcp":
                    return NetworkTransportKind.Tcp;
                case "raw-udp":
                    return NetworkTransportKind.RawUdp;
                default:
                    throw new ArgumentException(
                        "Transport must be kcp, tcp, or raw-udp.",
                        nameof(value));
            }
        }

        private static bool TryReadOption(
            string[] arguments,
            ref int index,
            string argument,
            string option,
            out string value)
        {
            value = null;
            if (string.Equals(
                    argument,
                    option,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= arguments.Length ||
                    string.IsNullOrWhiteSpace(arguments[index + 1]))
                {
                    throw new ArgumentException(
                        "Missing value for " + option + ".",
                        nameof(arguments));
                }
                value = arguments[++index];
                return true;
            }

            string prefix = option + "=";
            if (!argument.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            value = argument.Substring(prefix.Length);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "Missing value for " + option + ".",
                    nameof(arguments));
            }
            return true;
        }
    }
}

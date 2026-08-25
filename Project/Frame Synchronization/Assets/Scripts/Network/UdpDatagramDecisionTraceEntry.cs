using System.Globalization;

namespace FrameSyncDemo
{
    public readonly struct UdpDatagramDecisionTraceEntry
    {
        public const string SemanticLabel = "udp-datagram-drop";

        internal UdpDatagramDecisionTraceEntry(
            UdpDatagramDirection direction,
            string sessionFingerprint,
            ulong sequence,
            RouteCMessageType? messageType,
            byte? kcpCommand,
            uint? kcpSequence,
            in UdpDatagramFaultDecision decision)
        {
            Direction = direction;
            SessionFingerprint = sessionFingerprint;
            Sequence = sequence;
            MessageType = messageType;
            KcpCommand = kcpCommand;
            KcpSequence = kcpSequence;
            Decision = decision;
        }

        public UdpDatagramDirection Direction { get; }
        public string SessionFingerprint { get; }
        public ulong Sequence { get; }
        public RouteCMessageType? MessageType { get; }
        public byte? KcpCommand { get; }
        public uint? KcpSequence { get; }
        public UdpDatagramFaultDecision Decision { get; }

        public string ToCanonicalJsonLine()
        {
            string direction = Direction == UdpDatagramDirection.ClientToServer
                ? "client-to-server"
                : "server-to-client";
            string messageType = MessageType.HasValue
                ? "\"" + MessageType.Value + "\""
                : "null";
            string kcpCommand = KcpCommand.HasValue
                ? KcpCommand.Value.ToString(CultureInfo.InvariantCulture)
                : "null";
            string kcpSequence = KcpSequence.HasValue
                ? KcpSequence.Value.ToString(CultureInfo.InvariantCulture)
                : "null";
            return string.Format(
                CultureInfo.InvariantCulture,
                "{{\"semantic\":\"{0}\"," +
                "\"direction\":\"{1}\"," +
                "\"sessionFingerprint\":\"{2}\"," +
                "\"sequence\":{3}," +
                "\"messageType\":{4}," +
                "\"kcpCommand\":{5}," +
                "\"kcpSequence\":{6}," +
                "\"dropped\":{7}," +
                "\"effectiveDelayMs\":{8}," +
                "\"jitterOffsetMs\":{9}," +
                "\"reorderExtraDelayMs\":{10}," +
                "\"copyCount\":{11}}}",
                SemanticLabel,
                direction,
                SessionFingerprint,
                Sequence,
                messageType,
                kcpCommand,
                kcpSequence,
                Decision.Dropped ? "true" : "false",
                Decision.EffectiveDelayMs,
                Decision.JitterOffsetMs,
                Decision.ReorderExtraDelayMs,
                Decision.CopyCount);
        }
    }
}

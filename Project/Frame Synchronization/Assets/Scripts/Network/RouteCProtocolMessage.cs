using System;

namespace FrameSyncDemo
{
    public sealed class RouteCProtocolMessage
    {
        private readonly byte[] _payload;

        public RouteCProtocolMessage(
            RouteCMessageType messageType,
            RouteCSessionId sessionId,
            uint generation,
            byte[] payload)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));

            MessageType = messageType;
            SessionId = sessionId;
            Generation = generation;
            _payload = (byte[])payload.Clone();
        }

        public RouteCMessageType MessageType { get; }
        public RouteCSessionId SessionId { get; }
        public uint Generation { get; }
        public int PayloadLength => _payload.Length;
        public byte[] Payload => (byte[])_payload.Clone();
    }
}

using System;
using System.Globalization;

namespace FrameSyncDemo
{
    public readonly struct NetworkTransportEvent
    {
        public NetworkTransportEvent(
            NetworkSessionState state,
            NetworkTransportEventReason reason,
            string sessionFingerprint,
            uint generation,
            uint timestamp)
        {
            if (sessionFingerprint == null)
                throw new ArgumentNullException(nameof(sessionFingerprint));

            State = state;
            Reason = reason;
            SessionFingerprint = sessionFingerprint;
            Generation = generation;
            Timestamp = timestamp;
        }

        public NetworkSessionState State { get; }

        public NetworkTransportEventReason Reason { get; }

        public string SessionFingerprint { get; }

        public uint Generation { get; }

        public uint Timestamp { get; }

        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "State={0} Reason={1} Session={2} Generation={3} Timestamp={4}",
                State,
                Reason,
                SessionFingerprint,
                Generation,
                Timestamp);
        }
    }
}

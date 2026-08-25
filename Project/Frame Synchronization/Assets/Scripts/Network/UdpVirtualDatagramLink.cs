using System;
using System.Collections.Generic;

namespace FrameSyncDemo
{
    public sealed class UdpVirtualDatagramLink
    {
        private readonly UdpDatagramFaultProfile _clientToServerProfile;
        private readonly UdpDatagramFaultProfile _serverToClientProfile;
        private readonly List<PendingDatagram> _pending =
            new List<PendingDatagram>();
        private ulong _nextClientToServerSequence;
        private ulong _nextServerToClientSequence;

        public UdpVirtualDatagramLink(
            UdpDatagramFaultProfile clientToServerProfile,
            UdpDatagramFaultProfile serverToClientProfile)
        {
            _clientToServerProfile = clientToServerProfile;
            _serverToClientProfile = serverToClientProfile;
        }

        public int PendingCount => _pending.Count;

        public UdpDatagramDecisionTraceEntry Enqueue(
            long nowMs,
            UdpDatagramDirection direction,
            int route,
            byte[] datagram)
        {
            ValidateNow(nowMs);
            ValidateDirection(direction);
            if (route < 0)
                throw new ArgumentOutOfRangeException(nameof(route));
            if (datagram == null)
                throw new ArgumentNullException(nameof(datagram));

            var envelope = (byte[])datagram.Clone();
            RouteCSessionId sessionId = TryReadSessionId(envelope);
            ulong sequence = TakeNextSequence(direction);
            UdpDatagramFaultProfile profile =
                direction == UdpDatagramDirection.ClientToServer
                    ? _clientToServerProfile
                    : _serverToClientProfile;
            UdpDatagramFaultDecision decision = UdpDatagramFaultModel.Decide(
                in profile,
                direction,
                sessionId,
                sequence,
                envelope,
                out UdpDatagramDecisionTraceEntry traceEntry);
            if (!decision.Dropped)
            {
                long dueTimeMs = checked(nowMs + decision.EffectiveDelayMs);
                for (int copyIndex = 0;
                     copyIndex < decision.CopyCount;
                     copyIndex++)
                {
                    _pending.Add(new PendingDatagram(
                        dueTimeMs,
                        direction,
                        sequence,
                        copyIndex,
                        route,
                        (byte[])envelope.Clone()));
                }
            }

            return traceEntry;
        }

        public bool TryDequeueDue(
            long nowMs,
            out UdpDatagramDirection direction,
            out int route,
            out byte[] datagram)
        {
            ValidateNow(nowMs);
            int selectedIndex = -1;
            for (int index = 0; index < _pending.Count; index++)
            {
                PendingDatagram candidate = _pending[index];
                if (candidate.DueTimeMs > nowMs)
                    continue;
                if (selectedIndex < 0 ||
                    Compare(candidate, _pending[selectedIndex]) < 0)
                {
                    selectedIndex = index;
                }
            }

            if (selectedIndex < 0)
            {
                direction = default;
                route = 0;
                datagram = null;
                return false;
            }

            PendingDatagram selected = _pending[selectedIndex];
            _pending.RemoveAt(selectedIndex);
            direction = selected.Direction;
            route = selected.Route;
            datagram = selected.Datagram;
            return true;
        }

        private ulong TakeNextSequence(UdpDatagramDirection direction)
        {
            if (direction == UdpDatagramDirection.ClientToServer)
            {
                if (_nextClientToServerSequence == ulong.MaxValue)
                    throw new OverflowException("Client-to-server datagram sequence overflowed.");
                return _nextClientToServerSequence++;
            }

            if (_nextServerToClientSequence == ulong.MaxValue)
                throw new OverflowException("Server-to-client datagram sequence overflowed.");
            return _nextServerToClientSequence++;
        }

        private static RouteCSessionId TryReadSessionId(byte[] datagram)
        {
            if (RouteCProtocolCodec.TryDecode(
                    datagram,
                    datagram.Length,
                    out RouteCProtocolMessage message,
                    out _))
            {
                return message.SessionId;
            }
            return RouteCSessionId.Zero;
        }

        private static int Compare(PendingDatagram left, PendingDatagram right)
        {
            int result = left.DueTimeMs.CompareTo(right.DueTimeMs);
            if (result != 0)
                return result;
            result = ((byte)left.Direction).CompareTo((byte)right.Direction);
            if (result != 0)
                return result;
            result = left.Sequence.CompareTo(right.Sequence);
            return result != 0
                ? result
                : left.CopyIndex.CompareTo(right.CopyIndex);
        }

        private static void ValidateNow(long nowMs)
        {
            if (nowMs < 0)
                throw new ArgumentOutOfRangeException(nameof(nowMs));
        }

        private static void ValidateDirection(UdpDatagramDirection direction)
        {
            if (direction != UdpDatagramDirection.ClientToServer &&
                direction != UdpDatagramDirection.ServerToClient)
            {
                throw new ArgumentOutOfRangeException(nameof(direction));
            }
        }

        private sealed class PendingDatagram
        {
            public PendingDatagram(
                long dueTimeMs,
                UdpDatagramDirection direction,
                ulong sequence,
                int copyIndex,
                int route,
                byte[] datagram)
            {
                DueTimeMs = dueTimeMs;
                Direction = direction;
                Sequence = sequence;
                CopyIndex = copyIndex;
                Route = route;
                Datagram = datagram;
            }

            public long DueTimeMs { get; }
            public UdpDatagramDirection Direction { get; }
            public ulong Sequence { get; }
            public int CopyIndex { get; }
            public int Route { get; }
            public byte[] Datagram { get; }
        }
    }
}

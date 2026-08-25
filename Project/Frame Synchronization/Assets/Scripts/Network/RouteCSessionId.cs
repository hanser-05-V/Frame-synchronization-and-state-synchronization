using System;

namespace FrameSyncDemo
{
    public readonly struct RouteCSessionId : IEquatable<RouteCSessionId>
    {
        public static readonly RouteCSessionId Zero = default;

        public RouteCSessionId(ulong high, ulong low)
        {
            High = high;
            Low = low;
        }

        public ulong High { get; }
        public ulong Low { get; }
        public bool IsZero => High == 0UL && Low == 0UL;

        public bool Equals(RouteCSessionId other)
        {
            return High == other.High && Low == other.Low;
        }

        public override bool Equals(object obj)
        {
            return obj is RouteCSessionId other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (High.GetHashCode() * 397) ^ Low.GetHashCode();
            }
        }

        public static bool operator ==(
            RouteCSessionId left,
            RouteCSessionId right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(
            RouteCSessionId left,
            RouteCSessionId right)
        {
            return !left.Equals(right);
        }
    }
}

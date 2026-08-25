namespace FrameSyncDemo
{
    public static class RawUdpSerialNumber
    {
        public static RawUdpSerialOrder Compare(uint left, uint right)
        {
            if (left == right)
                return RawUdpSerialOrder.Equal;

            uint difference = unchecked(left - right);
            if (difference == 0x80000000u)
                return RawUdpSerialOrder.Ambiguous;
            return difference < 0x80000000u
                ? RawUdpSerialOrder.Newer
                : RawUdpSerialOrder.Older;
        }
    }
}

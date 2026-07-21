using System;

namespace FrameSyncDemo
{
    /// <summary>
    /// 定点数数学库 — 帧同步确定性的计算基础
    /// 为 FixedInt (×1000) 提供 Sqrt/Abs/Min/Max/Clamp 等函数
    /// 参考街篮2 FixedMath，Demo 精简为仅需函数
    /// </summary>
    public static class FixedMath
    {
        // ----- 基础函数 -----
        public static FixedInt Abs(FixedInt value) => new FixedInt(value._raw < 0 ? -value._raw : value._raw);

        public static FixedInt Min(FixedInt a, FixedInt b) => new FixedInt(a._raw < b._raw ? a._raw : b._raw);

        public static FixedInt Max(FixedInt a, FixedInt b) => new FixedInt(a._raw > b._raw ? a._raw : b._raw);

        public static FixedInt Clamp(FixedInt value, FixedInt min, FixedInt max)
        {
            if (value._raw < min._raw) return min;
            if (value._raw > max._raw) return max;
            return value;
        }

        public static FixedInt Sign(FixedInt value)
        {
            if (value._raw > 0) return FixedInt.One;
            if (value._raw < 0) return -FixedInt.One;
            return FixedInt.Zero;
        }

        // ----- 平方根 (Newton法) -----
        // 输入: value = raw_v / SCALE, 输出: result = sqrt(raw_v / SCALE)
        // Newton迭代: raw_r = sqrt(raw_v * SCALE)
        public static FixedInt Sqrt(FixedInt value)
        {
            if (value._raw <= 0) return FixedInt.Zero;

            // n = value._raw * SCALE — 注意用 long 防止溢出
            long n = (long)value._raw * FixedInt.SCALE;
            if (n <= 0) return FixedInt.Zero;

            // Newton法求整数平方根: r_{k+1} = (r_k + n/r_k) / 2
            long r = n;
            for (int i = 0; i < 20; i++)
            {
                long next = (r + n / r) >> 1;
                if (next == r || next == r + 1 || next == r - 1)
                    break;
                r = next;
            }
            return new FixedInt((int)r);
        }
    }
}

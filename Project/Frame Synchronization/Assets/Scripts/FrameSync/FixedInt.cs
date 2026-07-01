using UnityEngine;

namespace FrameSyncDemo
{
    /// <summary>
    /// 定点数 — 帧同步确定性的基石
    /// 内部用 int × 1000 模拟小数，精度 0.001
    /// 参考街篮2 FixedNumber (32.16格式)，本Demo简化为 1000倍率
    /// </summary>
    [System.Serializable]
    public struct FixedInt
    {
        public const int SCALE = 1000;
        public int _raw;

        public FixedInt(int raw)
        {
            _raw = raw;
        }

        // ----- 常量 -----
        public static readonly FixedInt Zero = new FixedInt(0);
        public static readonly FixedInt One = new FixedInt(SCALE);
        public static readonly FixedInt Half = new FixedInt(SCALE / 2);

        // ----- 构造 -----
        public static FixedInt FromFloat(float value)
        {
            return new FixedInt(Mathf.RoundToInt(value * SCALE));
        }

        public static FixedInt FromInt(int value)
        {
            return new FixedInt(value * SCALE);
        }

        // ----- 转换 -----
        public float ToFloat()
        {
            return _raw / (float)SCALE;
        }

        public int ToInt()
        {
            return _raw / SCALE;
        }

        // ----- 运算符 -----
        public static FixedInt operator +(FixedInt a, FixedInt b) => new FixedInt(a._raw + b._raw);
        public static FixedInt operator -(FixedInt a, FixedInt b) => new FixedInt(a._raw - b._raw);
        public static FixedInt operator -(FixedInt a) => new FixedInt(-a._raw);
        public static FixedInt operator *(FixedInt a, FixedInt b) => new FixedInt((a._raw * b._raw) / SCALE);
        public static FixedInt operator /(FixedInt a, FixedInt b) => new FixedInt((a._raw * SCALE) / b._raw);

        public static bool operator ==(FixedInt a, FixedInt b) => a._raw == b._raw;
        public static bool operator !=(FixedInt a, FixedInt b) => a._raw != b._raw;
        public static bool operator >(FixedInt a, FixedInt b) => a._raw > b._raw;
        public static bool operator <(FixedInt a, FixedInt b) => a._raw < b._raw;
        public static bool operator >=(FixedInt a, FixedInt b) => a._raw >= b._raw;
        public static bool operator <=(FixedInt a, FixedInt b) => a._raw <= b._raw;

        public override bool Equals(object obj)
        {
            return obj is FixedInt other && _raw == other._raw;
        }

        public override int GetHashCode()
        {
            return _raw.GetHashCode();
        }

        public override string ToString()
        {
            float value = ToFloat();
            return value.ToString("F3");
        }
    }
}

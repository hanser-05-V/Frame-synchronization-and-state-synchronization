using UnityEngine;

namespace FrameSyncDemo
{
    /// <summary>
    /// 定点数三维向量 — 帧同步确定性的几何运算基础
    /// 3 个 FixedInt 组成 (x, y, z)，提供向量运算
    /// 参考街篮2 FixedVector3，Demo 精简为核心方法
    /// </summary>
    public struct FixedVector3
    {
        public FixedInt x;
        public FixedInt y;
        public FixedInt z;

        // ----- 常量 -----
        public static readonly FixedVector3 Zero = new FixedVector3(FixedInt.Zero, FixedInt.Zero, FixedInt.Zero);
        public static readonly FixedVector3 One = new FixedVector3(FixedInt.One, FixedInt.One, FixedInt.One);
        public static readonly FixedVector3 Half = new FixedVector3(FixedInt.Half, FixedInt.Half, FixedInt.Half);

        public static readonly FixedVector3 Forward = new FixedVector3(FixedInt.Zero, FixedInt.Zero, FixedInt.One);
        public static readonly FixedVector3 Back = new FixedVector3(FixedInt.Zero, FixedInt.Zero, -FixedInt.One);
        public static readonly FixedVector3 Up = new FixedVector3(FixedInt.Zero, FixedInt.One, FixedInt.Zero);
        public static readonly FixedVector3 Down = new FixedVector3(FixedInt.Zero, -FixedInt.One, FixedInt.Zero);
        public static readonly FixedVector3 Right = new FixedVector3(FixedInt.One, FixedInt.Zero, FixedInt.Zero);
        public static readonly FixedVector3 Left = new FixedVector3(-FixedInt.One, FixedInt.Zero, FixedInt.Zero);

        // ----- 属性 -----
        public FixedInt sqrMagnitude => x * x + y * y + z * z;
        public FixedInt sqrMagnitudeXZ => x * x + z * z;
        public FixedInt magnitude => FixedMath.Sqrt(sqrMagnitude);   //mo
        public FixedInt magnitudeXZ => FixedMath.Sqrt(sqrMagnitudeXZ);

        public FixedVector3 normalized
        {
            get
            {
                var mag = magnitude;
                if (mag._raw == 0) return Zero;
                return this / mag;
            }
        }

        public FixedVector3 abs => new FixedVector3(
            FixedMath.Abs(x),
            FixedMath.Abs(y),
            FixedMath.Abs(z)
        );

        // ----- 构造 -----
        public FixedVector3(FixedInt x, FixedInt y, FixedInt z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public FixedVector3(FixedInt x, FixedInt z)
        {
            this.x = x;
            this.y = FixedInt.Zero;
            this.z = z;
        }

        // ----- 实例方法 -----
        public void Normalize()
        {
            var mag = magnitude;
            if (mag._raw == 0) return;
            x /= mag;
            y /= mag;
            z /= mag;
        }

        // ----- 静态方法 -----
        public static FixedInt Dot(FixedVector3 a, FixedVector3 b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z;
        }

        public static FixedVector3 Cross(FixedVector3 a, FixedVector3 b)
        {
            return new FixedVector3(
                a.y * b.z - a.z * b.y,
                a.z * b.x - a.x * b.z,
                a.x * b.y - a.y * b.x
            );
        }

        public static FixedInt Distance(FixedVector3 a, FixedVector3 b)
        {
            return (a - b).magnitude;
        }

        public static FixedInt SqrDistance(FixedVector3 a, FixedVector3 b)
        {
            return (a - b).sqrMagnitude;
        }

        public static FixedInt SqrDistanceXZ(FixedVector3 a, FixedVector3 b)
        {
            return (a - b).sqrMagnitudeXZ;
        }

        /// <summary>XZ平面叉乘的y分量 — 判断左右方向</summary>
        public static FixedInt CrossXZ(FixedVector3 a, FixedVector3 b)
        {
            return a.x * b.z - b.x * a.z;
        }

        public static FixedVector3 Lerp(FixedVector3 a, FixedVector3 b, FixedInt t)
        {
            t = FixedMath.Clamp(t, FixedInt.Zero, FixedInt.One);
            return a + (b - a) * t;
        }

        public static FixedVector3 ClampLerp(FixedVector3 a, FixedVector3 b, FixedInt t)
        {
            t = FixedMath.Clamp(t, FixedInt.Zero, FixedInt.One);
            return Lerp(a, b, t);
        }

        // ----- 运算符 -----
        public static FixedVector3 operator +(FixedVector3 a, FixedVector3 b)
        {
            return new FixedVector3(a.x + b.x, a.y + b.y, a.z + b.z);
        }

        public static FixedVector3 operator -(FixedVector3 a, FixedVector3 b)
        {
            return new FixedVector3(a.x - b.x, a.y - b.y, a.z - b.z);
        }

        public static FixedVector3 operator -(FixedVector3 v)
        {
            return new FixedVector3(-v.x, -v.y, -v.z);
        }

        public static FixedVector3 operator *(FixedVector3 v, FixedInt s)
        {
            return new FixedVector3(v.x * s, v.y * s, v.z * s);
        }

        public static FixedVector3 operator *(FixedInt s, FixedVector3 v)
        {
            return v * s;
        }

        public static FixedVector3 operator *(FixedVector3 v, int s)
        {
            return new FixedVector3(v.x * FixedInt.FromInt(s), v.y * FixedInt.FromInt(s), v.z * FixedInt.FromInt(s));
        }

        public static FixedVector3 operator /(FixedVector3 v, FixedInt d)
        {
            if (d._raw == 0) return v;
            return new FixedVector3(v.x / d, v.y / d, v.z / d);
        }

        public static bool operator ==(FixedVector3 a, FixedVector3 b)
        {
            return a.x._raw == b.x._raw && a.y._raw == b.y._raw && a.z._raw == b.z._raw;
        }

        public static bool operator !=(FixedVector3 a, FixedVector3 b)
        {
            return a.x._raw != b.x._raw || a.y._raw != b.y._raw || a.z._raw != b.z._raw;
        }

        public override bool Equals(object obj)
        {
            if (!(obj is FixedVector3)) return false;
            var other = (FixedVector3)obj;
            return this == other;
        }

        public override int GetHashCode()
        {
            return x._raw ^ (y._raw << 10) ^ (z._raw << 20);
        }

        public override string ToString()
        {
            return $"({x.ToFloat():F3}, {y.ToFloat():F3}, {z.ToFloat():F3})";
        }

        // ----- Unity转换（仅编辑器和客户端，不参与逻辑运算）-----
#if !Server
        public Vector3 ToVector3()
        {
            return new Vector3(x.ToFloat(), y.ToFloat(), z.ToFloat());
        }

        public static FixedVector3 FromVector3(Vector3 v)
        {
            return new FixedVector3(
                FixedInt.FromFloat(v.x),
                FixedInt.FromFloat(v.y),
                FixedInt.FromFloat(v.z)
            );
        }
#endif
    }
}

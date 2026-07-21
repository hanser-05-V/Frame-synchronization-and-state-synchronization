namespace FrameSyncDemo
{
    /// <summary>
    /// 球场常量 — 半场 1v1 街篮配置
    /// 所有单位与 FixedInt (×1000) 一致，使用整数米
    /// 参考街篮2 MatchConstant + CourtConstant
    /// </summary>
    public static class CourtConstant
    {
        // ----- 半场尺寸 -----
        public static readonly FixedInt CourtHalfWidth = FixedInt.FromInt(5);   // 半宽 5m (总宽 10m)
        public static readonly FixedInt CourtHalfDepth = FixedInt.FromInt(4);   // 半深 4m (总深 8m)
        public static readonly FixedInt CourtTotalWidth = FixedInt.FromInt(10); // 总宽 10m

        // ----- 篮筐 -----
        public static readonly FixedInt HoopHeight = FixedInt.FromInt(305);     // 篮筐高 3.05m (raw=3050)
        public static readonly FixedInt HoopY = FixedInt.FromInt(305);
        public static readonly FixedInt HoopRadius = FixedInt.FromInt(23);      // 篮筐半径 0.23m (NBA ≈ 0.225m)
        public static readonly FixedInt HoopZ = FixedInt.FromInt(387);          // 篮筐在Z轴位置 (距底线)

        // ----- 三分线 -----
        public static readonly FixedInt ThreePointRadius = FixedInt.FromInt(450); // 三分线半径 4.5m (raw=4500)

        // ----- 罚球线 -----
        public static readonly FixedInt FreeThrowLineZ = FixedInt.FromInt(250);   // 罚球线Z位置 (raw=2500)
        public static readonly FixedInt FreeThrowLineWidth = FixedInt.FromInt(240); // 罚球线半宽

        // ----- 物理常量 -----
        public static readonly FixedInt Gravity = FixedInt.FromInt(-980);      // 重力 -9.8 m/s² (raw=-9800)

        // ----- 球场边界检测 -----
        public static FixedVector3 MakeSureInside(FixedVector3 pos)
        {
            pos.x = FixedMath.Clamp(pos.x, -CourtHalfWidth, CourtHalfWidth);
            pos.z = FixedMath.Clamp(pos.z, -FixedInt.Zero, CourtHalfDepth * FixedInt.FromInt(2));
            return pos;
        }

        /// <summary>检测XZ位置是否在球场内</summary>
        public static bool IsInsideCourt(FixedInt x, FixedInt z)
        {
            if (x._raw < -CourtHalfWidth._raw || x._raw > CourtHalfWidth._raw) return false;
            if (z._raw < -FixedInt.Zero._raw || z._raw > (CourtHalfDepth * FixedInt.FromInt(2))._raw) return false;
            return true;
        }
    }
}

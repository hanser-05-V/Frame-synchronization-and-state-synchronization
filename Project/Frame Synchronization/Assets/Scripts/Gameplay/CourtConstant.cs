namespace FrameSyncDemo
{
    /// <summary>
    /// 球场常量 — 半场 1v1 街篮配置
    /// 所有单位与 FixedInt (×1000) 一致
    /// ⚠️ FromInt(n) = n 米, FromFloat(n) = n 米
    /// 参考街篮2 MatchConstant + CourtConstant
    /// </summary>
    public static class CourtConstant
    {
        // ----- 半场尺寸 -----
        public static readonly FixedInt CourtHalfWidth = FixedInt.FromInt(5);   // 半宽 5m
        public static readonly FixedInt CourtHalfDepth = FixedInt.FromInt(4);   // 半深 4m
        public static readonly FixedInt CourtTotalWidth = FixedInt.FromInt(10); // 总宽 10m

        // ----- 篮筐 -----
        public static readonly FixedInt HoopHeight = FixedInt.FromFloat(3.05f); // 篮筐高 3.05m
        public static readonly FixedInt HoopY = FixedInt.FromFloat(3.05f);
        public static readonly FixedInt HoopRadius = FixedInt.FromFloat(0.23f); // 篮筐半径 0.23m
        public static readonly FixedInt HoopZ = FixedInt.FromFloat(3.87f);      // 篮筐在Z轴位置 (距底线)

        // ----- 三分线 -----
        public static readonly FixedInt ThreePointRadius = FixedInt.FromFloat(4.5f); // 三分线半径 4.5m

        // ----- 罚球线 -----
        public static readonly FixedInt FreeThrowLineZ = FixedInt.FromFloat(2.5f);   // 罚球线Z位置
        public static readonly FixedInt FreeThrowLineWidth = FixedInt.FromFloat(2.4f); // 罚球线半宽

        // ----- 物理常量 -----
        public static readonly FixedInt Gravity = FixedInt.FromFloat(-9.8f);  // 重力 -9.8 m/s²

        // ----- P0 持球挂点 -----
        public static readonly FixedInt HeldBallHeight = FixedInt.FromFloat(1.2f);
        public static readonly FixedInt HeldBallForwardOffset = FixedInt.FromFloat(0.5f);
        public static readonly FixedInt PickupRadius = FixedInt.FromFloat(1.25f);

        // ----- P0 投篮 -----
        public static readonly FixedInt LogicDeltaTime = FixedInt.FromFloat(0.033f);
        public static readonly FixedInt ShotReleaseHeight = FixedInt.FromFloat(1.5f);
        public static readonly FixedInt ShotForwardOffset = FixedInt.FromFloat(0.35f);
        public const int ShotFlightFrames = 30;

        // ----- 球场边界检测 -----
        public static FixedVector3 MakeSureInside(FixedVector3 pos)
        {
            pos.x = FixedMath.Clamp(pos.x, -CourtHalfWidth, CourtHalfWidth);
            pos.z = FixedMath.Clamp(pos.z, -FixedInt.Zero, CourtHalfDepth * FixedInt.FromInt(2));
            return pos;
        }

        public static bool IsInsideCourt(FixedInt x, FixedInt z)
        {
            if (x._raw < -CourtHalfWidth._raw || x._raw > CourtHalfWidth._raw) return false;
            if (z._raw < -FixedInt.Zero._raw || z._raw > (CourtHalfDepth * FixedInt.FromInt(2))._raw) return false;
            return true;
        }
    }
}

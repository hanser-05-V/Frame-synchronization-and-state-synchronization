namespace FrameSyncDemo
{
    /// <summary>
    /// 球实体数据 — 不包含渲染
    /// 6种状态: Free(自由)/Held(被持有)/Airborne(空中)/Scored(已进球)/OutOfBounds(出界)/Resetting(重设)
    /// 参考街篮2 BallEntity
    /// </summary>
    public class BallEntity
    {
        public enum EState
        {
            Free,       // 自由滚动/弹跳
            Held,       // 被球员持有
            Airborne,   // 空中飞行（投篮/传球后）
            Scored,     // 已进球（等待重设）
            OutOfBounds,// 出界
            Resetting,  // 重设位置中
        }

        // ----- 运行时状态 -----
        public FixedVector3 position;
        public FixedVector3 velocity;
        public EState state;
        public int holderPlayerIndex = -1;  // 持有者 -1=无持有者

        // ----- 初始化 -----
        public void Reset(FixedVector3 startPos, bool startAirborne = false)
        {
            position = startPos;
            velocity = FixedVector3.Zero;
            state = startAirborne ? EState.Airborne : EState.Resetting;
            holderPlayerIndex = -1;
        }
    }
}

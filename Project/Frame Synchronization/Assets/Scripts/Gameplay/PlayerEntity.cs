namespace FrameSyncDemo
{
    /// <summary>
    /// 球员实体数据 — 不包含渲染
    /// 16种状态:
    ///   Idle/Walk/Run/Dribble/Sprint(运球)
    ///   ShootReady/Shooting(投篮) / PassReady/Passing(传球)
    ///   Steal(抢断) / Block(盖帽) / Jump(起跳)
    ///   Rebound(抢篮板) / Stagger(硬直) / Fall(倒地) / GetUp(起身)
    /// 参考街篮2 PlayerEntity + PlayerBaseState
    /// </summary>
    public class PlayerEntity
    {
        public enum EState
        {
            Idle,       // 待机
            Walk,       // 走路
            Run,        // 奔跑
            Dribble,    // 运球跑
            Sprint,     // 冲刺
            ShootReady, // 举球待投
            Shooting,   // 投篮出手
            PassReady,  // 传球准备
            Passing,    // 传球中
            Steal,      // 抢断
            Block,      // 盖帽
            Jump,       // 原地跳
            Rebound,    // 抢篮板（起跳）
            Stagger,    // 被撞硬直
            Fall,       // 倒地
            GetUp,      // 起身
        }

        // ----- 运行时状态 -----
        public FixedVector3 position;
        public FixedVector3 facing;    // 面朝方向（归一化向量）
        public EState state;
        public bool hasBall;           // 是否持球
        public int playerIndex;        // 0=P1 1=P2

        // ----- 初始化 -----
        public void Reset(FixedVector3 startPos, int index)
        {
            position = startPos;
            facing = FixedVector3.Forward;
            state = EState.Idle;
            hasBall = false;
            playerIndex = index;
        }
    }
}

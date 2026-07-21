namespace FrameSyncDemo
{
    /// <summary>
    /// 球物理系统 — 抛物线轨迹 + 篮筐碰撞检测
    /// 纯 Euler 积分，无摩擦/弹性简化（街篮2有弹地+摩擦，Demo 纯抛物线）
    /// 参考街篮2 BallPhysicalState.Simulation
    /// </summary>
    public class BallPhysicsSystem
    {
        /// <summary>单帧物理更新</summary>
        public static void Update(BallEntity ball, FixedInt deltaTime)
        {
            if (ball.state != BallEntity.EState.Airborne &&
                ball.state != BallEntity.EState.Free)
                return;

            // Euler 积分: v += g * dt,  p += v * dt
            FixedInt dt = deltaTime;
            var vel = ball.velocity;
            vel.y += CourtConstant.Gravity * dt;            // 重力
            ball.position += vel * dt;                       // 位移
            ball.velocity = vel;

            // 地板碰撞（简化：触地即停，无弹跳）
            if (ball.position.y._raw <= 0)
            {
                ball.position.y = FixedInt.Zero;
                ball.velocity = FixedVector3.Zero;
                ball.state = BallEntity.EState.Free;
            }

            // 篮筐检测：球穿过篮筐平面 (y ≥ 3.05 且 下一帧 y < 3.05) 且在篮筐半径内
            if (DetectScored(ball, dt))
            {
                ball.state = BallEntity.EState.Scored;
            }
        }

        /// <summary>篮筐碰撞检测 — 球穿篮筐即为进球</summary>
        private static bool DetectScored(BallEntity ball, FixedInt dt)
        {
            // 不在空中不检测
            if (ball.state != BallEntity.EState.Airborne)
                return false;

            // 位置在篮筐水平半径内
            var dx = ball.position.x;
            var dz = ball.position.z - CourtConstant.HoopZ;
            var distXZ = dx * dx + dz * dz;

            // 上一帧位置 >= 篮筐高度 且 当前位置 < 篮筐高度
            // (从上方穿过篮筐平面)
            var prevY = ball.position.y + ball.velocity.y * dt;
            bool passedThrough = prevY._raw >= CourtConstant.HoopY._raw &&
                                 ball.position.y._raw < CourtConstant.HoopY._raw;

            if (passedThrough && distXZ._raw <= (CourtConstant.HoopRadius * CourtConstant.HoopRadius)._raw)
            {
                return true;
            }

            return false;
        }
    }
}

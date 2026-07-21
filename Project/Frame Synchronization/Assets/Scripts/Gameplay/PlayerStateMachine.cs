using UnityEngine;

namespace FrameSyncDemo
{
    /// <summary>
    /// 球员状态机 — 手动 switch + 转换白名单
    /// 16 状态，白名单限制合法转换路径
    /// 参考街篮2 PlayerBaseState.CheckChangeList
    /// </summary>
    public class PlayerStateMachine
    {
        private PlayerEntity _entity;

        // 转换白名单: fromState → 允许的 toState 列表
        private static readonly System.Collections.Generic.Dictionary<PlayerEntity.EState, PlayerEntity.EState[]> _whiteList;

        static PlayerStateMachine()
        {
            _whiteList = new System.Collections.Generic.Dictionary<PlayerEntity.EState, PlayerEntity.EState[]>
            {
                { PlayerEntity.EState.Idle,       new[] { PlayerEntity.EState.Walk, PlayerEntity.EState.Run, PlayerEntity.EState.Dribble,
                                                          PlayerEntity.EState.ShootReady, PlayerEntity.EState.PassReady,
                                                          PlayerEntity.EState.Steal, PlayerEntity.EState.Block, PlayerEntity.EState.Jump,
                                                          PlayerEntity.EState.Stagger } },
                { PlayerEntity.EState.Walk,       new[] { PlayerEntity.EState.Idle, PlayerEntity.EState.Run, PlayerEntity.EState.Dribble,
                                                          PlayerEntity.EState.ShootReady, PlayerEntity.EState.PassReady,
                                                          PlayerEntity.EState.Steal, PlayerEntity.EState.Stagger } },
                { PlayerEntity.EState.Run,        new[] { PlayerEntity.EState.Idle, PlayerEntity.EState.Walk, PlayerEntity.EState.Dribble,
                                                          PlayerEntity.EState.Sprint, PlayerEntity.EState.ShootReady,
                                                          PlayerEntity.EState.Steal, PlayerEntity.EState.Stagger } },
                { PlayerEntity.EState.Dribble,    new[] { PlayerEntity.EState.Idle, PlayerEntity.EState.Walk, PlayerEntity.EState.Sprint,
                                                          PlayerEntity.EState.ShootReady, PlayerEntity.EState.PassReady,
                                                          PlayerEntity.EState.Steal, PlayerEntity.EState.Stagger } },
                { PlayerEntity.EState.Sprint,     new[] { PlayerEntity.EState.Dribble, PlayerEntity.EState.Run,
                                                          PlayerEntity.EState.ShootReady, PlayerEntity.EState.Steal,
                                                          PlayerEntity.EState.Stagger } },
                { PlayerEntity.EState.ShootReady, new[] { PlayerEntity.EState.Shooting, PlayerEntity.EState.Idle,
                                                          PlayerEntity.EState.Stagger } },
                { PlayerEntity.EState.Shooting,   new[] { PlayerEntity.EState.Idle, PlayerEntity.EState.Stagger } },
                { PlayerEntity.EState.PassReady,  new[] { PlayerEntity.EState.Passing, PlayerEntity.EState.Idle,
                                                          PlayerEntity.EState.Stagger } },
                { PlayerEntity.EState.Passing,    new[] { PlayerEntity.EState.Idle, PlayerEntity.EState.Stagger } },
                { PlayerEntity.EState.Steal,      new[] { PlayerEntity.EState.Idle, PlayerEntity.EState.Walk, PlayerEntity.EState.Run,
                                                          PlayerEntity.EState.Dribble, PlayerEntity.EState.Stagger } },
                { PlayerEntity.EState.Block,      new[] { PlayerEntity.EState.Idle, PlayerEntity.EState.Jump,
                                                          PlayerEntity.EState.Stagger } },
                { PlayerEntity.EState.Jump,       new[] { PlayerEntity.EState.Idle, PlayerEntity.EState.Rebound,
                                                          PlayerEntity.EState.Block, PlayerEntity.EState.Stagger,
                                                          PlayerEntity.EState.Fall } },
                { PlayerEntity.EState.Rebound,    new[] { PlayerEntity.EState.Idle, PlayerEntity.EState.Fall } },
                { PlayerEntity.EState.Stagger,    new[] { PlayerEntity.EState.Fall, PlayerEntity.EState.Idle,
                                                          PlayerEntity.EState.GetUp } },
                { PlayerEntity.EState.Fall,       new[] { PlayerEntity.EState.GetUp } },
                { PlayerEntity.EState.GetUp,      new[] { PlayerEntity.EState.Idle, PlayerEntity.EState.Walk,
                                                          PlayerEntity.EState.Stagger } },
            };
        }

        public PlayerStateMachine(PlayerEntity entity)
        {
            _entity = entity;
        }

        /// <summary>尝试转换到新状态，白名单检查</summary>
        public bool TryChangeState(PlayerEntity.EState newState)
        {
            if (_entity.state == newState)
                return true;

            if (_whiteList.TryGetValue(_entity.state, out var allowed))
            {
                for (int i = 0; i < allowed.Length; i++)
                {
                    if (allowed[i] == newState)
                    {
                        var oldState = _entity.state;
                        _entity.state = newState;
                        Debug.Log($"[FSM] P{_entity.playerIndex}: {oldState} → {newState}");
                        return true;
                    }
                }
                Debug.LogWarning($"[FSM] ⛔ P{_entity.playerIndex}: 禁止转换 {_entity.state} → {newState}");
                return false;
            }

            _entity.state = newState;
            return true;
        }
    }
}

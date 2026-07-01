using System;
using UnityEngine;

namespace FrameSyncDemo
{
    /// <summary>
    /// 帧循环引擎 — 帧同步的心跳
    /// 参考街篮2 FrameEngine + LocalBattleController.LogicUpdate()
    /// 时序：Update()中先查询输入 → 再执行帧 → 触发回调（正确的帧同步顺序）
    /// </summary>
    public class FrameEngine : MonoBehaviour
    {
        [SerializeField] private int _frameIntervalMs = 33;
        [SerializeField] private int _frameCountPerSecond = 30;

        [SerializeField] private bool _isRunning = false;
        [SerializeField] private int _currentFrame = 0;
        [SerializeField] private int _catchupCount = 0;
        [SerializeField] private float _elapsedTime = 0f;

        private long _lastLogicMs = 0;
        private long _startMs = 0;
        private long _pausedTime = 0;  // 暂停时的时间戳
        private FrameBuffer _frameBuffer;
        private int _playerCount;

        // ----- 回调 -----
        /// <summary>每帧执行前，请求输入。GameController 注册此回调来提供玩家输入。</summary>
        public Func<FrameInput[]> OnRequestInput;
        /// <summary>每帧执行后，通知 GameController 更新位置。frameID=当前帧号, inputs=该帧Input</summary>
        public event Action<int, FrameInput[]> OnFrameUpdate;
        /// <summary>追帧回调</summary>
        public event Action<int> OnCatchup;

        // ----- 属性 -----
        public int CurrentFrame => _currentFrame;
        public float ElapsedTime => _elapsedTime;
        public int CatchupCount => _catchupCount;
        public bool IsRunning => _isRunning;
        public int FrameIntervalMs => _frameIntervalMs;
        public int TargetFPS => _frameCountPerSecond;
        public FrameBuffer Buffer => _frameBuffer;

        public void Initialize(int playerCount, int frameIntervalMs = 33)
        {
            _playerCount = playerCount;
            _frameIntervalMs = frameIntervalMs;
            _frameCountPerSecond = 1000 / frameIntervalMs;
            _frameBuffer = new FrameBuffer();
            _currentFrame = 0;
            _elapsedTime = 0f;
            _catchupCount = 0;
            _lastLogicMs = 0;
            _isRunning = false;
        }

        public void StartEngine()
        {
            _startMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _lastLogicMs = 0;
            _isRunning = true;
            Debug.Log($"[FrameEngine] 启动 — {_frameCountPerSecond}FPS ({_frameIntervalMs}ms/帧)");
        }

        public void Pause()
        {
            if (!_isRunning) return;
            _isRunning = false;
            _pausedTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public void Resume()
        {
            if (_isRunning) return;
            _isRunning = true;
            // 暂停期间流逝的时间跳过（避免追帧）
            _startMs += DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _pausedTime;
        }

        private void Update()
        {
            if (!_isRunning || _frameBuffer == null) return;

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long elapsedMs = nowMs - _startMs;

            int frameRun = 0;
            while (elapsedMs - _lastLogicMs >= _frameIntervalMs)
            {
                _lastLogicMs += _frameIntervalMs;
                ExecuteOneFrame();
                frameRun++;
            }

            if (frameRun > 1)
            {
                _catchupCount++;
                OnCatchup?.Invoke(frameRun - 1);
            }
        }

        /// <summary>执行一帧：收集输入 → 写缓冲 → 触发回调</summary>
        private void ExecuteOneFrame()
        {
            int frameID = _currentFrame++;

            // ① 收集输入（由 GameController 通过 OnRequestInput 提供）
            FrameInput[] inputs;
            if (OnRequestInput != null)
                inputs = OnRequestInput.Invoke();
            else
            {
                // fallback：空输入
                inputs = new FrameInput[_playerCount];
                for (int i = 0; i < _playerCount; i++) inputs[i] = new FrameInput();
            }

            // ② 写入环形缓冲区
            _frameBuffer.AddFrame(frameID, inputs);

            // ③ 更新运行时间
            _elapsedTime = _lastLogicMs / 1000f;

            // ④ 通知逻辑层（GameController 在这里更新方块位置）
            OnFrameUpdate?.Invoke(frameID, inputs);

            // ④.5 标记消费（延迟4帧，模拟网络缓冲的"待消费"效果）
            _frameBuffer.MarkRead(frameID - 4);

            // ⑤ 同步 FrameDebugger
            FrameDebugger.Instance?.UpdateFrame(frameID, inputs, _frameBuffer.GetSnapshot(),
                _catchupCount, _elapsedTime, _isRunning);
        }

        private void OnDestroy()
        {
            _isRunning = false;
            _frameBuffer?.Clear();
        }
    }
}

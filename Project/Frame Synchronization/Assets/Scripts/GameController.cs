using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

namespace FrameSyncDemo
{
    /// <summary>
    /// 游戏控制器 — 方块控制 + Input收集 + 录回放
    /// 正确时序：FrameEngine.OnRequestInput(读键) → 执行帧 → OnFrameUpdate(更新位置)
    /// </summary>
    public class GameController : MonoBehaviour
    {
        [Header("绑定")]
        [SerializeField] private FrameEngine _frameEngine;

        [Header("方块设置")]
        [SerializeField] private float _moveSpeed = 5f;
        [Header("材质")]
        [SerializeField] private Material _blockMaterial;
        [SerializeField] private Material _planeMaterial;

        private GameObject[] _blocks;
        private MeshRenderer[] _blockRenderers;
        private FixedInt[] _blockPosX;
        private FixedInt[] _blockPosZ;
        private int _playerCount = 2;
        private bool _paused = false;
        private NetworkClient _networkClient;
        private PredictionSystem _predictionSystem;

        private static readonly int[] DirX = { 0, 0, 1, 1, 1, 0, -1, -1, -1 };
        private static readonly int[] DirZ = { 0, 1, 1, 0, -1, -1, -1, 0, 1 };

        private void Start()
        {
            Initialize();
        }

        private void Initialize()
        {
            // 绑定引擎
            _frameEngine.Initialize(_playerCount, 33);
            _frameEngine.OnRequestInput = ReadInputs;   // 引擎每帧从这里拉输入
            _frameEngine.OnFrameUpdate += OnFrameUpdate; // 引擎每帧执行完通知这里 逻辑层执行逻辑
            _frameEngine.OnCatchup += OnCatchup; //追帧 回调

            // 创建方块
            _blocks = new GameObject[_playerCount];
            _blockRenderers = new MeshRenderer[_playerCount];
            _blockPosX = new FixedInt[_playerCount];
            _blockPosZ = new FixedInt[_playerCount];

            Color p1Color = new Color(0.9f, 0.2f, 0.2f);
            Color p2Color = new Color(0.2f, 0.4f, 0.9f);

            for (int i = 0; i < _playerCount; i++)
            {
                _blocks[i] = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _blocks[i].transform.SetParent(transform);
                _blocks[i].name = i == 0 ? "P1_RedBlock" : "P2_BlueBlock";
                _blocks[i].transform.position = new Vector3(i == 0 ? -3 : 3, 0.5f, 0);
                _blocks[i].transform.localScale = new Vector3(1, 1, 1);
                _blocks[i].GetComponent<MeshRenderer>().material = _blockMaterial;
                _blockRenderers[i] = _blocks[i].GetComponent<MeshRenderer>();
                if (_blockRenderers[i] != null)
                    _blockRenderers[i].material.color = i == 0 ? p1Color : p2Color;

                _blockPosX[i] = FixedInt.FromFloat(_blocks[i].transform.position.x);
                _blockPosZ[i] = FixedInt.FromFloat(_blocks[i].transform.position.z);
            }

            // 地面
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(transform);
            ground.transform.position = Vector3.zero;
            ground.transform.localScale = new Vector3(2, 1, 2);
            
            MeshRenderer groundRenderer = ground.GetComponent<MeshRenderer>();
            groundRenderer.material = _planeMaterial;
            ground.GetComponent<Renderer>().material.color = new Color(0.15f, 0.15f, 0.18f);

            // FrameDebugger
            if (FrameDebugger.Instance == null)
            {
                var go = new GameObject("FrameDebugger");
                go.AddComponent<FrameDebugger>();
            }
            FrameDebugger.Instance.playerCount = _playerCount;
            FrameDebugger.Instance.targetFPS = _frameEngine.TargetFPS;
            FrameDebugger.Instance.frameIntervalMs = _frameEngine.FrameIntervalMs;

            // ===== 网络初始化 =====
            _networkClient = gameObject.GetComponent<NetworkClient>();
            if (_networkClient == null)
                _networkClient = gameObject.AddComponent<NetworkClient>();

            // 设置引擎网络模式（联网时抑制自动 MarkRead）
            _frameEngine.IsNetworkMode = true;

            // ===== 预测系统初始化 =====
            _predictionSystem = new PredictionSystem();
            _predictionSystem.Init();

            // Editor 作为 P0(操控P1)，exe 作为 P1(操控P2)
            // TODO: 当两个都是exe时，需通过命令行参数/配置文件区分localID
            int localID = Application.isEditor ? 0 : 1;
            NetworkConfig.LocalPlayerID = localID;

            // 连接服务器（如果服务器没开，Connect 会超时约 21 秒）
            // 先开服务器再启动 Unity 可避免等待
            _networkClient.Connect(NetworkConfig.DEFAULT_IP, NetworkConfig.DEFAULT_PORT);

            _frameEngine.StartEngine();
        }

        // ===== 引擎回调 =====

        /// <summary>FrameEngine 每帧从这里拉输入</summary>
        private FrameInput[] ReadInputs()
        {
            if (_paused)
            {
                return new FrameInput[] { new FrameInput(), new FrameInput() };
            }

            // 回放模式：从录制数据中按帧读取 Input，驱动方块实际移动
            if (FrameDebugger.Instance.isPlayingBack)
            {
                var d = FrameDebugger.Instance;
                // playbackFrame 在 FrameDebugger.UpdateFrame() 中被递增
                // 回放帧用完 → 返回空 Input（方块停止，等 OnFrameUpdate 里触发 StopPlayback）
                if (d.playbackFrame >= d.recordedFrameCount)
                {
                    return new FrameInput[] { new FrameInput(), new FrameInput() };
                }
                int baseIdx = d.playbackFrame * d.recordedPlayerCount;
                var inputs = new FrameInput[d.recordedPlayerCount];
                for (int p = 0; p < d.recordedPlayerCount; p++)
                {
                    int idx = baseIdx + p;
                    if (idx < d.recordedInputs.Count)
                        inputs[p] = FrameInput.FromRaw(d.recordedInputs[idx]);
                    else
                        inputs[p] = new FrameInput();
                }
                return inputs;
            }

            // 网络模式：P1本地+P2远程（或反过来）
            if (_networkClient != null && _networkClient.IsConnected)
            {
                // 读本机键盘
                var myInput = _networkClient.LocalPlayerIndex == 0
                    ? ReadP1Input() : ReadP2Input();

                // 从网络队列取真实远程Input
                uint remoteRaw;
                bool hasRemote = _networkClient.TryGetRemoteInput(out remoteRaw);

                // 预测对手输入（含验证+回滚标记）
                int currentFrame = _frameEngine.CurrentFrame - 1;
                FrameInput remoteInput = _predictionSystem.PredictRemote(hasRemote, remoteRaw, currentFrame);

                // 发送本机 Input + 本地帧号（8字节协议）
                _networkClient.SendInput(myInput._raw, _frameEngine.CurrentFrame - 1);

                // 按 playerIndex 排序返回 [P1的Input, P2的Input]
                var result = new FrameInput[2];
                result[_networkClient.LocalPlayerIndex] = myInput;
                result[_networkClient.RemotePlayerIndex] = remoteInput;
                return result;
            }

            return new FrameInput[] { ReadP1Input(), ReadP2Input() };
        }

        /// <summary>FrameEngine 每帧处理完后通知这里更新位置</summary>
        private void OnFrameUpdate(int frameID, FrameInput[] inputs)
        {
            if (_paused) return;

            for (int i = 0; i < inputs.Length; i++)
            {
                byte dir = inputs[i].moveDir;
                if (dir > 0 && dir <= 8)
                {
                    FixedInt speed = FixedInt.FromFloat(_moveSpeed * 0.033f);
                    _blockPosX[i] += speed * FixedInt.FromInt(DirX[dir]);
                    _blockPosZ[i] += speed * FixedInt.FromInt(DirZ[dir]);
                }

                // 更新方块位置
                _blocks[i].transform.position = new Vector3(
                    _blockPosX[i].ToFloat(), 0.5f, _blockPosZ[i].ToFloat());

                // 同步调试数据
                FrameDebugger.Instance?.UpdatePlayerRender(i,
                    _blocks[i].transform.position,
                    i == 0 ? new Color(0.9f, 0.2f, 0.2f) : new Color(0.2f, 0.4f, 0.9f));
            }

            // 检查是否有待处理的回滚（预测错误触发）
            if (_predictionSystem != null && _predictionSystem.HasPendingRollback
                && (FrameDebugger.Instance == null || !FrameDebugger.Instance.isPlayingBack))
            {
                if (_predictionSystem.TryConsumeRollback(out int errorFrame, out uint correctRemoteRaw))
                {
                    Debug.Log($"[Rollback] 帧{errorFrame}预测错误，开始回滚纠正");
                    DoRollback(errorFrame, correctRemoteRaw);
                }
            }

            // 回放结束自动暂停
            if (FrameDebugger.Instance != null && FrameDebugger.Instance.isPlayingBack &&
                FrameDebugger.Instance.playbackFrame >= FrameDebugger.Instance.recordedFrameCount)
            {
                FrameDebugger.Instance.StopPlayback();
                _frameEngine.Pause();
            }

            // 非回放模式下拍照快照（用于回滚恢复）
            if (_predictionSystem != null && (FrameDebugger.Instance == null || !FrameDebugger.Instance.isPlayingBack))
            {
                _predictionSystem.TakeSnapshot(frameID, _blockPosX, _blockPosZ);
            }
        }

        private void HandlePlayback(int frameID, int playerCount)
        {
            // 回放的停止由 FrameDebugger.UpdateFrame() 内部的 playbackFrame 计数控制
            // 不能用全局 frameID 对比 — 那会瞬间判定"已播完"
        }

        private void OnCatchup(int count)
        {
            Debug.Log($"[GameController] ⚡追帧 {count}");
        }

        // ===== Update — 键盘快捷键 =====
        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.R))
            {
                if (!FrameDebugger.Instance.isRecording && !FrameDebugger.Instance.isPlayingBack)
                {
                    // 录制开始：记录方块当前位置
                    FrameDebugger.Instance.recordStartPositions = new Vector3[_playerCount];
                    for (int i = 0; i < _playerCount; i++)
                        FrameDebugger.Instance.recordStartPositions[i] = _blocks[i].transform.position;
                    FrameDebugger.Instance.StartRecording();
                }
                else if (FrameDebugger.Instance.isRecording)
                    FrameDebugger.Instance.StopRecording();
            }
            if (Input.GetKeyDown(KeyCode.P))
            {
                if (FrameDebugger.Instance.isPlayingBack)
                    FrameDebugger.Instance.StopPlayback();
                else
                {
                    // 回放：回到录制开始时的位置
                    var startPos = FrameDebugger.Instance.recordStartPositions;
                    if (startPos != null)
                    {
                        for (int i = 0; i < _playerCount && i < startPos.Length; i++)
                        {
                            _blockPosX[i] = FixedInt.FromFloat(startPos[i].x);
                            _blockPosZ[i] = FixedInt.FromFloat(startPos[i].z);
                            _blocks[i].transform.position = startPos[i];
                        }
                    }
                    FrameDebugger.Instance.StartPlayback();
                }
            }
            if (Input.GetKeyDown(KeyCode.C))
            {
                FrameDebugger.Instance.ClearRecording();
                ResetPositions();
            }
            if (Input.GetKeyDown(KeyCode.Space))
            {
                _paused = !_paused;
                if (_paused) _frameEngine.Pause();
                else _frameEngine.Resume();
            }

            if(Input.GetKeyDown(KeyCode.Escape))
            {
                Application.Quit();
            }

        }

        // ===== 输入读取 =====
        private FrameInput ReadP1Input()
        {
            byte dir = 0, btn = 0;
            float h = 0, v = 0;
            if (Input.GetKey(KeyCode.A)) h = -1;
            if (Input.GetKey(KeyCode.D)) h = 1;
            if (Input.GetKey(KeyCode.W)) v = 1;
            if (Input.GetKey(KeyCode.S)) v = -1;
            dir = DirToByte(h, v);
            if (Input.GetKey(KeyCode.Q)) btn |= 1;
            if (Input.GetKey(KeyCode.E)) btn |= 2;
            return new FrameInput(dir, btn);
        }

        private FrameInput ReadP2Input()
        {
            byte dir = 0, btn = 0;
            float h = 0, v = 0;
            if (Input.GetKey(KeyCode.LeftArrow)) h = -1;
            if (Input.GetKey(KeyCode.RightArrow)) h = 1;
            if (Input.GetKey(KeyCode.UpArrow)) v = 1;
            if (Input.GetKey(KeyCode.DownArrow)) v = -1;
            dir = DirToByte(h, v);
            if (Input.GetKey(KeyCode.Keypad1)) btn |= 1;
            if (Input.GetKey(KeyCode.Keypad2)) btn |= 2;
            return new FrameInput(dir, btn);
        }

        private byte DirToByte(float h, float v)
        {
            if (h == 0 && v == 0) return 0;
            if (h == 0 && v > 0) return 1;
            if (h > 0 && v > 0) return 2;
            if (h > 0 && v == 0) return 3;
            if (h > 0 && v < 0) return 4;
            if (h == 0 && v < 0) return 5;
            if (h < 0 && v < 0) return 6;
            if (h < 0 && v == 0) return 7;
            if (h < 0 && v > 0) return 8;
            return 0;
        }

        private void ResetPositions()
        {
            _blockPosX[0] = FixedInt.FromFloat(-3);
            _blockPosZ[0] = FixedInt.Zero;
            _blockPosX[1] = FixedInt.FromFloat(3);
            _blockPosZ[1] = FixedInt.Zero;
            _blocks[0].transform.position = new Vector3(-3, 0.5f, 0);
            _blocks[1].transform.position = new Vector3(3, 0.5f, 0);
        }

        // ===== 回滚逻辑 =====

        /// <summary>执行回滚：恢复快照 → 重跑 errorFrame..currentFrame → 继续预测</summary>
        private void DoRollback(int errorFrame, uint correctRemoteRaw)
        {
            int safeFrame = errorFrame - 1;
            if (safeFrame < 0) return;

            if (!_predictionSystem.RestoreSnapshot(safeFrame, ref _blockPosX, ref _blockPosZ))
            {
                Debug.LogError("[Rollback] 快照恢复失败");
                return;
            }

            int currentFrame = _frameEngine.CurrentFrame;
            FixedInt speed = FixedInt.FromFloat(_moveSpeed * 0.033f);
            int localIdx = _networkClient != null ? _networkClient.LocalPlayerIndex : 0;
            int remoteIdx = _networkClient != null ? _networkClient.RemotePlayerIndex : 1;

            // 当前已知正确的远程输入（回滚期间持续使用）
            uint knownCorrectRemoteRaw = correctRemoteRaw;

            for (int i = safeFrame + 1; i <= currentFrame; i++)
            {
                FrameInput[] inputs = new FrameInput[2];

                // 本地Input从 FrameBuffer 取
                FrameBuffer.Frame frameData;
                if (_frameEngine.Buffer.GetFrame(i, out frameData))
                    inputs[localIdx] = frameData.inputs[localIdx];
                else
                    inputs[localIdx] = localIdx == 0 ? ReadP1Input() : ReadP2Input();

                // 远程Input：从 errorFrame 开始全部用正确的值
                // （因为 errorFrame 及之后的预测都基于错误假设，全被清除了）
                if (i >= errorFrame)
                {
                    // 尝试从队列取更新的远程数据（可能在回滚期间有新数据到达）
                    uint fresherRaw;
                    if (_networkClient.TryGetRemoteInput(out fresherRaw))
                        knownCorrectRemoteRaw = fresherRaw;
                    inputs[remoteIdx] = FrameInput.FromRaw(knownCorrectRemoteRaw);
                }
                else
                {
                    inputs[remoteIdx] = frameData.inputs[remoteIdx];
                }

                // 执行位置更新
                for (int p = 0; p < inputs.Length; p++)
                {
                    byte dir = inputs[p].moveDir;
                    if (dir > 0 && dir <= 8)
                    {
                        _blockPosX[p] += speed * FixedInt.FromInt(DirX[dir]);
                        _blockPosZ[p] += speed * FixedInt.FromInt(DirZ[dir]);
                    }
                }

                _blocks[0].transform.position = new Vector3(_blockPosX[0].ToFloat(), 0.5f, _blockPosZ[0].ToFloat());
                _blocks[1].transform.position = new Vector3(_blockPosX[1].ToFloat(), 0.5f, _blockPosZ[1].ToFloat());
                _predictionSystem.TakeSnapshot(i, _blockPosX, _blockPosZ);
            }

            Debug.Log($"[Rollback] 回滚完成: safe={safeFrame} → 重放帧{safeFrame+1}..{currentFrame} " +
                $"(纠正远程Input: {correctRemoteRaw:X8})");
        }

        private void OnDestroy()
        {
            if (_frameEngine != null)
            {
                _frameEngine.OnFrameUpdate -= OnFrameUpdate;
                _frameEngine.OnCatchup -= OnCatchup;
            }
        }
    }
}

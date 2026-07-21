using UnityEngine;
using System.Collections.Generic;

namespace FrameSyncDemo
{
    /// <summary>
    /// 游戏控制器 — 球员控制 + Input收集 + 录回放 + 球物理
    /// 阶段1扩展：方块→球员+球，集成PlayerStateMachine+BallPhysicsSystem
    /// </summary>
    public class GameController : MonoBehaviour
    {
        [Header("绑定")]
        [SerializeField] private FrameEngine _frameEngine;

        [Header("渲染设置")]
        [SerializeField] private float _moveSpeed = 5f;
        [SerializeField] private Material _blockMaterial;
        [SerializeField] private Material _planeMaterial;

        // ----- 渲染对象 -----
        private GameObject[] _players;
        private GameObject _ballObject;

        // ----- 逻辑数据（阶段1）-----
        private PlayerEntity[] _playerEntities;
        private PlayerStateMachine[] _playerFSMs;
        private BallEntity _ballEntity;

        // ----- 兼容字段（阶段0回滚用）-----
        private FixedInt[] _blockPosX;
        private FixedInt[] _blockPosZ;

        private int _playerCount = 2;
        private bool _paused = false;
        private NetworkClient _networkClient;
        private PredictionSystem _predictionSystem;

        private int _remoteFrameOffset = int.MinValue;
        private bool _pendingRollback = false;
        private int _pendingRollbackErrorFrame = 0;
        private uint _pendingRollbackCorrectRaw = 0;

        private static readonly int[] DirX = { 0, 0, 1, 1, 1, 0, -1, -1, -1 };
        private static readonly int[] DirZ = { 0, 1, 1, 0, -1, -1, -1, 0, 1 };

        private void Start() { Initialize(); }

        private void Initialize()
        {
            _frameEngine.Initialize(_playerCount, 33);
            _frameEngine.OnRequestInput = ReadInputs;
            _frameEngine.OnFrameUpdate += OnFrameUpdate;
            _frameEngine.OnPostFrameUpdate += OnPostFrameUpdate;
            _frameEngine.OnCatchup += OnCatchup;

            // ===== 阶段1：球场渲染 =====
            var courtObj = new GameObject("BasketballCourt");
            courtObj.transform.SetParent(transform);
            courtObj.AddComponent<BasketballCourt>();

            // ===== 阶段1：球员 + 球 =====
            _playerEntities = new PlayerEntity[_playerCount];
            _playerFSMs = new PlayerStateMachine[_playerCount];
            for (int i = 0; i < _playerCount; i++)
            {
                _playerEntities[i] = new PlayerEntity();
                FixedVector3 startPos = new FixedVector3(
                    FixedInt.FromFloat(i == 0 ? -3f : 3f),
                    FixedInt.Zero,
                    FixedInt.Zero);
                _playerEntities[i].Reset(startPos, i);
                _playerFSMs[i] = new PlayerStateMachine(_playerEntities[i]);
            }

            _ballEntity = new BallEntity();
            _ballEntity.Reset(new FixedVector3(FixedInt.Zero, CourtConstant.HoopY, CourtConstant.HoopZ), startAirborne: true);
            // 给球一个很小的水平速度，让它有抛物线视觉

            // ===== 渲染 =====
            _players = new GameObject[_playerCount];
            _blockPosX = new FixedInt[_playerCount];
            _blockPosZ = new FixedInt[_playerCount];
            Color[] colors = { new Color(0.9f, 0.2f, 0.2f), new Color(0.2f, 0.4f, 0.9f) };

            for (int i = 0; i < _playerCount; i++)
            {
                _players[i] = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                _players[i].transform.SetParent(transform);
                _players[i].name = i == 0 ? "P1_Red" : "P2_Blue";
                _players[i].transform.localScale = new Vector3(0.6f, 0.8f, 0.6f);

                Vector3 pos = _playerEntities[i].position.ToVector3();
                _players[i].transform.position = pos + Vector3.up * 0.5f;

                var mr = _players[i].GetComponent<MeshRenderer>();
                if (mr != null) mr.material.color = colors[i];

                _blockPosX[i] = _playerEntities[i].position.x;
                _blockPosZ[i] = _playerEntities[i].position.z;
            }

            _ballObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _ballObject.name = "Ball";
            _ballObject.transform.SetParent(transform);
            _ballObject.transform.localScale = Vector3.one * 0.25f;
            _ballObject.transform.position = _ballEntity.position.ToVector3();
            var ballMr = _ballObject.GetComponent<MeshRenderer>();
            if (ballMr != null) ballMr.material.color = new Color(0.9f, 0.5f, 0.1f);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(transform);
            ground.transform.position = Vector3.zero;
            ground.transform.localScale = new Vector3(2, 1, 2);
            var gmr = ground.GetComponent<MeshRenderer>();
            gmr.material = _planeMaterial;
            gmr.material.color = new Color(0.15f, 0.15f, 0.18f);

            if (FrameDebugger.Instance == null)
            {
                var go = new GameObject("FrameDebugger");
                go.AddComponent<FrameDebugger>();
            }
            FrameDebugger.Instance.playerCount = _playerCount;
            FrameDebugger.Instance.targetFPS = _frameEngine.TargetFPS;
            FrameDebugger.Instance.frameIntervalMs = _frameEngine.FrameIntervalMs;

            _networkClient = gameObject.GetComponent<NetworkClient>();
            if (_networkClient == null)
                _networkClient = gameObject.AddComponent<NetworkClient>();
            _frameEngine.IsNetworkMode = true;

            _predictionSystem = new PredictionSystem();
            _predictionSystem.Init();

            int localID = Application.isEditor ? 0 : 1;
            NetworkConfig.LocalPlayerID = localID;
            _networkClient.Connect(NetworkConfig.DEFAULT_IP, NetworkConfig.DEFAULT_PORT);
            _frameEngine.StartEngine();
        }

        // ===== ReadInputs — 和阶段0完全一致 =====
        private FrameInput[] ReadInputs()
        {
            if (_paused)
                return new FrameInput[] { new FrameInput(), new FrameInput() };

            if (FrameDebugger.Instance.isPlayingBack)
            {
                var d = FrameDebugger.Instance;
                if (d.playbackFrame >= d.recordedFrameCount)
                    return new FrameInput[] { new FrameInput(), new FrameInput() };
                var inputs = new FrameInput[d.recordedPlayerCount];
                int baseIdx = d.playbackFrame * d.recordedPlayerCount;
                for (int p = 0; p < d.recordedPlayerCount; p++)
                {
                    int idx = baseIdx + p;
                    inputs[p] = idx < d.recordedInputs.Count
                        ? FrameInput.FromRaw(d.recordedInputs[idx])
                        : new FrameInput();
                }
                return inputs;
            }

            if (_networkClient != null && _networkClient.IsConnected)
            {
                var myInput = _networkClient.LocalPlayerIndex == 0
                    ? ReadP1Input() : ReadP2Input();
                int currentFrame = _frameEngine.CurrentFrame - 1;
                _networkClient.DrainQueueToDict();

                if (_remoteFrameOffset == int.MinValue)
                {
                    int minRemote = _networkClient.GetMinRemoteFrameID();
                    if (minRemote >= 0)
                    {
                        _remoteFrameOffset = currentFrame - minRemote;
                        Debug.Log($"[GameController] 首包锚定: localFrame={currentFrame}, minRemote={minRemote}, offset={_remoteFrameOffset}");
                    }
                }

                FrameInput remoteInput;
                if (_remoteFrameOffset != int.MinValue)
                {
                    int remoteTarget = currentFrame - _remoteFrameOffset;
                    uint actualRaw;
                    bool hasActual = _networkClient.TryGetRemoteInputAt(remoteTarget, out actualRaw);
                    int? errorFrame;
                    uint correctRaw;
                    remoteInput = _predictionSystem.ResolveRemote(currentFrame, actualRaw, hasActual, out errorFrame, out correctRaw);
                    if (errorFrame.HasValue)
                    {
                        _pendingRollback = true;
                        _pendingRollbackErrorFrame = errorFrame.Value;
                        _pendingRollbackCorrectRaw = correctRaw;
                    }
                }
                else
                {
                    int? dummyError; uint dummyCorrect;
                    remoteInput = _predictionSystem.ResolveRemote(currentFrame, 0, false, out dummyError, out dummyCorrect);
                }

                _networkClient.SendInput(myInput._raw, currentFrame);
                var result = new FrameInput[2];
                result[_networkClient.LocalPlayerIndex] = myInput;
                result[_networkClient.RemotePlayerIndex] = remoteInput;
                return result;
            }

            return new FrameInput[] { ReadP1Input(), ReadP2Input() };
        }

        // ===== OnFrameUpdate — 球员更新 =====
        private void OnFrameUpdate(int frameID, FrameInput[] inputs)
        {
            if (_paused) return;

            FixedInt speed = FixedInt.FromFloat(_moveSpeed * 0.033f);

            for (int i = 0; i < inputs.Length; i++)
            {
                byte dir = inputs[i].moveDir;
                var player = _playerEntities[i];

                // 状态机：有方向→Run，无方向→Idle
                _playerFSMs[i].TryChangeState(
                    dir > 0 && dir <= 8 ? PlayerEntity.EState.Run : PlayerEntity.EState.Idle);

                if (dir > 0 && dir <= 8)
                {
                    FixedInt mx = speed * FixedInt.FromInt(DirX[dir]);
                    FixedInt mz = speed * FixedInt.FromInt(DirZ[dir]);
                    _blockPosX[i] += mx;
                    _blockPosZ[i] += mz;
                    player.position.x += mx;
                    player.position.z += mz;
                    if (mx._raw != 0 || mz._raw != 0)
                        player.facing = new FixedVector3(mx, FixedInt.Zero, mz).normalized;
                }

                _players[i].transform.position = new Vector3(
                    _blockPosX[i].ToFloat(), 0.5f, _blockPosZ[i].ToFloat());

                FrameDebugger.Instance?.UpdatePlayerRender(i,
                    _players[i].transform.position,
                    i == 0 ? new Color(0.9f, 0.2f, 0.2f) : new Color(0.2f, 0.4f, 0.9f));
            }

            if (FrameDebugger.Instance != null && FrameDebugger.Instance.isPlayingBack &&
                FrameDebugger.Instance.playbackFrame >= FrameDebugger.Instance.recordedFrameCount)
            {
                FrameDebugger.Instance.StopPlayback();
                _frameEngine.Pause();
            }

            if (_predictionSystem != null && (FrameDebugger.Instance == null || !FrameDebugger.Instance.isPlayingBack))
                TakeExpandedSnapshot(frameID);

            MD5Checker.CheckAndLog(frameID, _blockPosX, _blockPosZ);
        }

        // ===== OnPostFrameUpdate — 球物理 =====
        private void OnPostFrameUpdate(int frameID, FrameInput[] inputs)
        {
            if (_paused) return;

            if (_ballEntity.state == BallEntity.EState.Airborne ||
                _ballEntity.state == BallEntity.EState.Free)
            {
                BallPhysicsSystem.Update(_ballEntity, FixedInt.FromFloat(0.033f));
            }

            _ballObject.transform.position = _ballEntity.position.ToVector3();
        }

        private void HandlePlayback(int frameID, int playerCount) { }

        private void OnCatchup(int count)
        {
            Debug.Log($"[GameController] ⚡追帧 {count}");
        }

        // ===== Update — 键盘 =====
        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.R))
            {
                if (!FrameDebugger.Instance.isRecording && !FrameDebugger.Instance.isPlayingBack)
                {
                    FrameDebugger.Instance.recordStartPositions = new Vector3[_playerCount];
                    for (int i = 0; i < _playerCount; i++)
                        FrameDebugger.Instance.recordStartPositions[i] = _players[i].transform.position;
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
                    var sp = FrameDebugger.Instance.recordStartPositions;
                    if (sp != null)
                    {
                        for (int i = 0; i < _playerCount && i < sp.Length; i++)
                        {
                            _blockPosX[i] = FixedInt.FromFloat(sp[i].x);
                            _blockPosZ[i] = FixedInt.FromFloat(sp[i].z);
                            _players[i].transform.position = sp[i];
                        }
                    }
                    FrameDebugger.Instance.StartPlayback();
                }
            }
            if (Input.GetKeyDown(KeyCode.C)) { FrameDebugger.Instance.ClearRecording(); ResetPositions(); }
            if (Input.GetKeyDown(KeyCode.Space)) { _paused = !_paused; if (_paused) _frameEngine.Pause(); else _frameEngine.Resume(); }
            if (Input.GetKeyDown(KeyCode.Escape)) Application.Quit();
        }

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
            if (Input.GetKey(KeyCode.Alpha1)) btn |= 4;
            if (Input.GetKey(KeyCode.Alpha2)) btn |= 8;
            if (Input.GetKey(KeyCode.LeftShift)) btn |= 16;
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
            if (Input.GetKey(KeyCode.Keypad3)) btn |= 4;
            if (Input.GetKey(KeyCode.Keypad4)) btn |= 8;
            if (Input.GetKey(KeyCode.Keypad0)) btn |= 16;
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
            for (int i = 0; i < _playerCount; i++)
            {
                _players[i].transform.position = new Vector3(i == 0 ? -3 : 3, 0.5f, 0);
                _playerEntities[i].position = new FixedVector3(FixedInt.FromFloat(i == 0 ? -3 : 3), FixedInt.Zero, FixedInt.Zero);
            }
        }

        // ===== 回滚 =====
        private void DoRollback(int errorFrame, uint correctRemoteRaw)
        {
            int safeFrame = errorFrame - 1;
            int foundFrame = -1;
            for (int f = safeFrame; f >= 0; f--)
            {
                if (_predictionSystem.RestoreSnapshot(f, ref _blockPosX, ref _blockPosZ))
                { foundFrame = f; break; }
            }
            if (foundFrame < 0) { ResetPositions(); foundFrame = 0; }
            safeFrame = foundFrame;

            int lastExecutedFrame = _frameEngine.CurrentFrame - 1;
            FixedInt speed = FixedInt.FromFloat(_moveSpeed * 0.033f);
            int localIdx = _networkClient != null ? _networkClient.LocalPlayerIndex : 0;
            int remoteIdx = _networkClient != null ? _networkClient.RemotePlayerIndex : 1;

            for (int i = safeFrame + 1; i <= lastExecutedFrame; i++)
            {
                FrameInput[] inputs = new FrameInput[2];
                if (_frameEngine.Buffer.GetFrame(i, out var frameData))
                    inputs[localIdx] = frameData.inputs[localIdx];
                else { Debug.LogError($"[Rollback] 帧{i}本地Input缺失"); return; }

                int remoteTarget = i - _remoteFrameOffset;
                uint actualRaw;
                if (_networkClient.TryGetRemoteInputAt(remoteTarget, out actualRaw))
                    inputs[remoteIdx] = FrameInput.FromRaw(actualRaw);
                else
                    inputs[remoteIdx] = FrameInput.FromRaw(correctRemoteRaw);

                for (int p = 0; p < inputs.Length; p++)
                {
                    byte dir = inputs[p].moveDir;
                    if (dir > 0 && dir <= 8)
                    {
                        _blockPosX[p] += speed * FixedInt.FromInt(DirX[dir]);
                        _blockPosZ[p] += speed * FixedInt.FromInt(DirZ[dir]);
                    }
                }
                _players[0].transform.position = new Vector3(_blockPosX[0].ToFloat(), 0.5f, _blockPosZ[0].ToFloat());
                _players[1].transform.position = new Vector3(_blockPosX[1].ToFloat(), 0.5f, _blockPosZ[1].ToFloat());
                _predictionSystem.TakeSnapshot(i, _blockPosX, _blockPosZ);
                UpdatePlayerEntitiesFromRollback();
            }
        }

        private void LateUpdate()
        {
            if (_networkClient == null || !_networkClient.IsConnected) return;
            _networkClient.DrainQueueToDict();
            if (_remoteFrameOffset != int.MinValue)
            {
                int minKeep = (_frameEngine.CurrentFrame - _remoteFrameOffset) - 120;
                if (minKeep > 0) _networkClient.CleanupRemoteInputs(minKeep);
            }
            if (_pendingRollback && (FrameDebugger.Instance == null || !FrameDebugger.Instance.isPlayingBack))
            {
                _pendingRollback = false;
                DoRollback(_pendingRollbackErrorFrame, _pendingRollbackCorrectRaw);
            }
        }

        private void TakeExpandedSnapshot(int frameID)
        {
            _predictionSystem.TakeSnapshot(frameID, _blockPosX, _blockPosZ);
        }

        private void UpdatePlayerEntitiesFromRollback()
        {
            for (int i = 0; i < _playerCount; i++)
            {
                _playerEntities[i].position.x = _blockPosX[i];
                _playerEntities[i].position.z = _blockPosZ[i];
            }
        }

        private void OnDestroy()
        {
            if (_frameEngine != null)
            {
                _frameEngine.OnFrameUpdate -= OnFrameUpdate;
                _frameEngine.OnPostFrameUpdate -= OnPostFrameUpdate;
                _frameEngine.OnCatchup -= OnCatchup;
            }
        }
    }
}

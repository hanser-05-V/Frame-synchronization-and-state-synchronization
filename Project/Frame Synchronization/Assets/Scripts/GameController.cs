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

        [Header("完整世界 Hash")]
        [SerializeField] private int _worldHashLogIntervalFrames = 200;

        [Header("回滚表现平滑")]
        [SerializeField] private float _rollbackVisualSmoothingSeconds = 0.1f;
        [SerializeField] private float _rollbackVisualMaxSmoothingSeconds = 0.2f;
        [SerializeField] private float _rollbackVisualMaxCorrectionSpeed = 10f;
        [SerializeField] private float _rollbackVisualSnapDistance = 2f;

        // ----- 渲染对象 -----
        private GameObject[] _players;
        private GameObject _ballObject;
        private PresentationFrameInterpolator _presentationInterpolator;
        private PresentationCorrectionSmoother[] _playerCorrectionSmoothers;
        private PresentationCorrectionSmoother _ballSmoother;
        private Vector3[] _interpolatedPlayerPositions;
        private Vector3[] _playerPresentationPositions;
        private Vector3[] _rollbackPlayerDisplayPositions;
        private int _lastPresentedBallAttachmentIndex = -1;
        private bool _hasPresentedBallAttachment;

        // ----- 逻辑数据（阶段1）-----
        private PlayerEntity[] _playerEntities;
        private PlayerStateMachine[] _playerFSMs;
        private BallEntity _ballEntity;
        private bool _reportedHeldInvariantError;

        // ----- 兼容字段（阶段0回滚用）-----
        private FixedInt[] _blockPosX;
        private FixedInt[] _blockPosZ;

        private int _playerCount = 2;
        private bool _paused = false;
        private NetworkClient _networkClient;
        private PredictionSystem _predictionSystem;
        private MatchPhase _matchPhase = MatchPhase.Playing;
        private StableFrameCursor _highlightStableCursor;
        private StableRemoteFrameGate _highlightRemoteFrameGate;
        private HighlightReplayRecorder _highlightRecorder;
        private PostGameHighlightReplayController _highlightReplayController;
        private RuntimeControlOverlay _controlOverlay;
        private string _lastHighlightCaptureError = string.Empty;

        private readonly RollbackRequestBuffer _rollbackRequests =
            new RollbackRequestBuffer();
        private readonly LocalFrameActionBuffer _localFrameActionBuffer =
            new LocalFrameActionBuffer();

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
            _ballEntity.Reset(FixedVector3.Zero);
            _playerEntities[0].hasBall = true;
            _ballEntity.state = BallEntity.EState.Held;
            _ballEntity.holderPlayerIndex = 0;
            if (!BallPossessionSystem.TryUpdateHeldBall(_playerEntities[0], _ballEntity))
                Debug.LogError("[P0] 初始球权不一致，无法设置持球挂点");

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
            _highlightStableCursor = new StableFrameCursor();
            _highlightRemoteFrameGate = new StableRemoteFrameGate();
            _highlightRecorder = new HighlightReplayRecorder();
            _controlOverlay = gameObject.GetComponent<RuntimeControlOverlay>();
            if (_controlOverlay == null)
                _controlOverlay = gameObject.AddComponent<RuntimeControlOverlay>();
            UpdateControlOverlay();
            InitializePresentationSmoothing();

            int localID = Application.isEditor ? 0 : 1;
            NetworkConfig.LocalPlayerID = localID;
            _networkClient.Connect(NetworkConfig.DEFAULT_IP, NetworkConfig.DEFAULT_PORT);
            if (!NetworkFrameTimeline.CanStartSession(_networkClient.IsConnected))
            {
                Debug.LogError("[GameController] 联网会话未共同放行，帧引擎保持停止");
                return;
            }

            Debug.Log(
                "[GameController] 双客户端已共同放行，共享帧起点=0，远端偏移=0");
            _frameEngine.StartEngine();
        }

        // ===== ReadInputs — 和阶段0完全一致 =====
        private FrameInput[] ReadInputs()
        {
            CaptureLocalFrameActions();

            if (_paused)
                return new FrameInput[] { new FrameInput(), new FrameInput() };

            if (_networkClient != null && _networkClient.IsConnected)
            {
                FrameInput myInput = ReadPrimaryInput();
                int currentFrame = _frameEngine.CurrentFrame - 1;
                _networkClient.DrainQueueToDict();
                LogNetworkFrameGap(currentFrame);

                FrameInput remoteInput = _predictionSystem.ResolveRemote(
                    currentFrame,
                    GetRemoteActualRawForLocalFrame,
                    out int? errorFrame,
                    out uint correctRaw);
                _highlightRemoteFrameGate.StageResolvedThrough(
                    System.Math.Min(
                        currentFrame,
                        _networkClient.LatestDrainedRemoteFrameID));
                if (errorFrame.HasValue)
                {
                    _rollbackRequests.Request(errorFrame.Value, correctRaw);
                }

                _networkClient.SendInput(myInput._raw, currentFrame);
                var result = new FrameInput[2];
                result[_networkClient.LocalPlayerIndex] = myInput;
                result[_networkClient.RemotePlayerIndex] = remoteInput;
                return result;
            }

            return new FrameInput[] { ReadPrimaryInput(), ReadP2Input() };
        }

        // ===== OnFrameUpdate — 确定性世界更新 =====
        private void OnFrameUpdate(int frameID, FrameInput[] inputs)
        {
            if (_paused) return;

            SimulateLogicFrame(frameID, inputs, true);
        }

        // ===== OnPostFrameUpdate — 完整帧快照 =====
        private void OnPostFrameUpdate(int frameID, FrameInput[] inputs)
        {
            if (_paused) return;

            if (_predictionSystem != null)
            {
                FrameSnapshot snapshot = _predictionSystem.TakeWorldSnapshot(
                    frameID,
                    _playerEntities,
                    _ballEntity);

                if (!_rollbackRequests.HasRequest)
                    LogNormalWorldHash(snapshot);
            }

            if (_presentationInterpolator != null &&
                _presentationInterpolator.CanPushLogicFrame(frameID))
            {
                _presentationInterpolator.PushLogicFrame(
                    frameID,
                    _playerEntities,
                    _ballEntity);
            }
        }

        private FrameSimulationResult SimulateLogicFrame(
            int frameID,
            FrameInput[] inputs,
            bool emitLogs)
        {
            FixedInt moveDistance = FixedInt.FromFloat(_moveSpeed * 0.033f);
            FrameSimulationResult result = FrameSimulationSystem.Step(
                _playerEntities,
                _playerFSMs,
                _ballEntity,
                inputs,
                moveDistance,
                CourtConstant.LogicDeltaTime);

            SyncLegacyPositionsFromEntities();
            if (emitLogs)
                LogSimulationResult(frameID, result);
            return result;
        }

        private void LogSimulationResult(int frameID, FrameSimulationResult result)
        {
            if (!result.heldInvariantValid)
            {
                int holderIndex = _ballEntity.holderPlayerIndex;
                string message = holderIndex < 0 || holderIndex >= _playerEntities.Length
                    ? $"Held 状态的持有者索引无效: {holderIndex}"
                    : $"P{holderIndex} 与篮球的持球状态不一致";
                ReportHeldInvariantErrorOnce(frameID, message);
            }
            else
            {
                _reportedHeldInvariantError = false;
            }

            if (result.previousBallState == BallEntity.EState.Free &&
                _ballEntity.state == BallEntity.EState.Held)
            {
                Debug.Log(
                    $"[RouteC][BallState] frame={frameID} Free->Held " +
                    $"holder=P{_ballEntity.holderPlayerIndex} " +
                    $"positionRaw={FormatRawVector(_ballEntity.position)}");
            }

            if (result.shooterPlayerIndex >= 0)
            {
                Debug.Log(
                    $"[RouteC][BallState] frame={frameID} Held->Airborne " +
                    $"holder=P{result.shooterPlayerIndex} " +
                    $"positionRaw={FormatRawVector(_ballEntity.position)} " +
                    $"velocityRaw={FormatRawVector(_ballEntity.velocity)}");
            }

            if (result.previousBallState == BallEntity.EState.Airborne &&
                _ballEntity.state == BallEntity.EState.Scored)
            {
                Debug.Log(
                    $"[RouteC][BallState] frame={frameID} Airborne->Scored " +
                    $"positionRaw={FormatRawVector(_ballEntity.position)}");
            }
            else if (result.previousBallState == BallEntity.EState.Airborne &&
                     _ballEntity.state == BallEntity.EState.Free)
            {
                Debug.Log(
                    $"[RouteC][BallState] frame={frameID} Airborne->Free reason=Floor " +
                    $"positionRaw={FormatRawVector(_ballEntity.position)}");
            }
            else if (result.previousBallState == BallEntity.EState.Scored &&
                     result.previousBallY._raw > 0 &&
                     _ballEntity.state == BallEntity.EState.Free &&
                     _ballEntity.position.y._raw == 0)
            {
                Debug.Log(
                    $"[RouteC][BallState] frame={frameID} Scored->Free reason=Floor " +
                    $"positionRaw={FormatRawVector(_ballEntity.position)}");
            }
        }

        private void ReportHeldInvariantErrorOnce(int frameID, string message)
        {
            if (_reportedHeldInvariantError)
                return;

            _reportedHeldInvariantError = true;
            Debug.LogError($"[RouteC][BallInvariant] frame={frameID} {message}");
        }

        private static string FormatRawVector(FixedVector3 value)
        {
            return $"({value.x._raw},{value.y._raw},{value.z._raw})";
        }

        private void SyncLegacyPositionsFromEntities()
        {
            for (int i = 0; i < _playerCount; i++)
            {
                _blockPosX[i] = _playerEntities[i].position.x;
                _blockPosZ[i] = _playerEntities[i].position.z;
            }
        }

        private void InitializePresentationSmoothing()
        {
            float durationSeconds = _rollbackVisualSmoothingSeconds;
            if (float.IsNaN(durationSeconds) ||
                float.IsInfinity(durationSeconds) ||
                durationSeconds <= 0f ||
                durationSeconds > 0.2f)
            {
                durationSeconds = 0.1f;
                Debug.LogWarning(
                    "[GameController] 回滚表现平滑时长无效，已使用默认值 0.1 秒");
            }

            float maximumDurationSeconds =
                _rollbackVisualMaxSmoothingSeconds;
            if (float.IsNaN(maximumDurationSeconds) ||
                float.IsInfinity(maximumDurationSeconds) ||
                maximumDurationSeconds < durationSeconds ||
                maximumDurationSeconds > 0.2f)
            {
                maximumDurationSeconds = 0.2f;
                Debug.LogWarning(
                    "[GameController] 回滚表现最长时长无效，已使用默认值 0.2 秒");
            }

            float maximumCorrectionSpeed =
                _rollbackVisualMaxCorrectionSpeed;
            if (float.IsNaN(maximumCorrectionSpeed) ||
                float.IsInfinity(maximumCorrectionSpeed) ||
                maximumCorrectionSpeed <= 0f)
            {
                maximumCorrectionSpeed = 10f;
                Debug.LogWarning(
                    "[GameController] 回滚表现最大纠正速度无效，已使用默认值 10");
            }

            float snapDistance = _rollbackVisualSnapDistance;
            if (float.IsNaN(snapDistance) ||
                float.IsInfinity(snapDistance) ||
                snapDistance <= 0f)
            {
                snapDistance = 2f;
                Debug.LogWarning(
                    "[GameController] 回滚表现直接跳转距离无效，已使用默认值 2");
            }

            if ((double)maximumCorrectionSpeed * maximumDurationSeconds <
                snapDistance)
            {
                durationSeconds = 0.1f;
                maximumDurationSeconds = 0.2f;
                maximumCorrectionSpeed = 10f;
                snapDistance = 2f;
                Debug.LogWarning(
                    "[GameController] 回滚表现参数组合不可完成，已整组恢复默认值");
            }

            _presentationInterpolator =
                new PresentationFrameInterpolator(_playerCount);
            _playerCorrectionSmoothers =
                new PresentationCorrectionSmoother[_playerCount];
            for (int i = 0; i < _playerCount; i++)
            {
                _playerCorrectionSmoothers[i] =
                    new PresentationCorrectionSmoother(
                        durationSeconds,
                        maximumDurationSeconds,
                        maximumCorrectionSpeed,
                        snapDistance);
            }

            _ballSmoother = new PresentationCorrectionSmoother(
                durationSeconds,
                maximumDurationSeconds,
                maximumCorrectionSpeed,
                snapDistance);
            _interpolatedPlayerPositions = new Vector3[_playerCount];
            _playerPresentationPositions = new Vector3[_playerCount];
            _rollbackPlayerDisplayPositions = new Vector3[_playerCount];
            SnapPresentationToLogic();
        }

        private void SyncPresentationFromLogic(float deltaTime)
        {
            if (_players == null ||
                _playerEntities == null ||
                _ballObject == null ||
                _ballEntity == null ||
                _presentationInterpolator == null ||
                _playerCorrectionSmoothers == null ||
                _interpolatedPlayerPositions == null ||
                _playerPresentationPositions == null)
            {
                return;
            }

            _presentationInterpolator.Evaluate(
                GetPresentationLocalPlayerIndex(),
                _frameEngine.RenderInterpolationAlpha,
                _interpolatedPlayerPositions,
                out PresentationBallSample ballSample);

            for (int i = 0; i < _playerCount; i++)
            {
                Vector3 position = _playerCorrectionSmoothers[i].Evaluate(
                    _interpolatedPlayerPositions[i],
                    deltaTime);
                _players[i].transform.position = position;
                _playerPresentationPositions[i] = position;
                FrameDebugger.Instance?.UpdatePlayerRender(
                    i,
                    position,
                    i == 0
                        ? new Color(0.9f, 0.2f, 0.2f)
                        : new Color(0.2f, 0.4f, 0.9f));
            }

            Vector3 ballTarget =
                PresentationTargetResolver.ResolveBufferedBallTarget(
                    ballSample,
                    _interpolatedPlayerPositions,
                    _playerPresentationPositions);
            bool useIndependentSmoother =
                PresentationTargetResolver.ShouldUseIndependentBallSmoother(
                    ballSample);
            if (_hasPresentedBallAttachment &&
                PresentationTargetResolver.ShouldTransferBallVisualCorrection(
                    _lastPresentedBallAttachmentIndex,
                    ballSample.AttachedPlayerIndex) &&
                useIndependentSmoother)
            {
                _ballSmoother.BeginCorrection(
                    _ballObject.transform.position,
                    ballTarget);
            }

            Vector3 ballPosition;
            if (useIndependentSmoother)
            {
                ballPosition = _ballSmoother.Evaluate(ballTarget, deltaTime);
            }
            else
            {
                _ballSmoother.Snap();
                ballPosition = ballTarget;
            }

            _ballObject.transform.position = ballPosition;
            RememberPresentedBallAttachment(ballSample);
        }

        private void SnapPresentationToLogic()
        {
            SnapPresentationToLogic(_frameEngine.CurrentFrame - 1);
        }

        private void SnapPresentationToLogic(int frameID)
        {
            if (_playerCorrectionSmoothers != null)
            {
                for (int i = 0; i < _playerCorrectionSmoothers.Length; i++)
                    _playerCorrectionSmoothers[i]?.Snap();
            }

            _ballSmoother?.Snap();
            _hasPresentedBallAttachment = false;
            _lastPresentedBallAttachmentIndex = -1;
            _presentationInterpolator?.Reset(
                frameID,
                _playerEntities,
                _ballEntity);
            SyncPresentationFromLogic(0f);
        }

        private void HandlePlayback(int frameID, int playerCount) { }

        private void OnCatchup(int count)
        {
            Debug.Log($"[GameController] ⚡追帧 {count}");
        }

        // ===== Update — 键盘 =====
        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Application.Quit();
                return;
            }

            if (_matchPhase == MatchPhase.PostGameReplay)
            {
                HandlePostGameReplayInput();
                UpdateControlOverlay();
                return;
            }

            CaptureLocalFrameActions();
            if (Input.GetKeyDown(KeyCode.F10))
            {
                bool isConnected = _networkClient != null && _networkClient.IsConnected;
                if (!NetworkFrameTimeline.CanPauseLocally(isConnected))
                {
                    Debug.LogWarning(
                        "[GameController] 联网会话不支持本机单独暂停，已忽略 F10");
                }
                else
                {
                    ClearLocalFrameActionsForCurrentRenderFrame();
                    _paused = !_paused;
                    if (_paused) _frameEngine.Pause();
                    else _frameEngine.Resume();
                }
            }
            UpdateControlOverlay();
        }

        private void HandlePostGameReplayInput()
        {
            if (_highlightReplayController == null)
                return;

            if (Input.GetKeyDown(KeyCode.P))
                _highlightReplayController.TogglePlaying();
            if (Input.GetKeyDown(KeyCode.LeftBracket))
                _highlightReplayController.Previous();
            if (Input.GetKeyDown(KeyCode.RightBracket))
                _highlightReplayController.Next();
            if (Input.GetKeyDown(KeyCode.R))
                _highlightReplayController.Restart();
        }

        private void CaptureLocalFrameActions()
        {
            if (_paused || _matchPhase != MatchPhase.Playing)
                return;

            _localFrameActionBuffer.Capture(
                Time.frameCount,
                Input.GetKeyDown(KeyCode.E),
                Input.GetKeyUp(KeyCode.Space),
                Input.GetKeyDown(KeyCode.F9));
        }

        private void ClearLocalFrameActionsForCurrentRenderFrame()
        {
            _localFrameActionBuffer.Capture(
                Time.frameCount,
                false,
                false,
                false);
            _localFrameActionBuffer.Clear();
        }

        private FrameInput ReadPrimaryInput()
        {
            byte dir = 0, btn = 0;
            float h = 0, v = 0;
            if (Input.GetKey(KeyCode.A)) h = -1;
            if (Input.GetKey(KeyCode.D)) h = 1;
            if (Input.GetKey(KeyCode.W)) v = 1;
            if (Input.GetKey(KeyCode.S)) v = -1;
            dir = DirToByte(h, v);
            if (Input.GetKey(KeyCode.Alpha1)) btn |= 4;
            if (Input.GetKey(KeyCode.Alpha2)) btn |= 8;
            if (Input.GetKey(KeyCode.LeftShift)) btn |= 16;
            return _localFrameActionBuffer.Consume(new FrameInput(dir, btn));
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
            ClearLocalFrameActionsForCurrentRenderFrame();
            ResetWorldLogic();
            SnapPresentationToLogic();
        }

        private void ResetWorldLogic()
        {
            for (int i = 0; i < _playerCount; i++)
            {
                FixedVector3 startPosition = new FixedVector3(
                    FixedInt.FromInt(i == 0 ? -3 : 3),
                    FixedInt.Zero,
                    FixedInt.Zero);
                _playerEntities[i].Reset(startPosition, i);
            }

            _ballEntity.Reset(FixedVector3.Zero);
            _playerEntities[0].hasBall = true;
            _ballEntity.state = BallEntity.EState.Held;
            _ballEntity.holderPlayerIndex = 0;
            BallPossessionSystem.TryUpdateHeldBall(_playerEntities[0], _ballEntity);
            _reportedHeldInvariantError = false;
            SyncLegacyPositionsFromEntities();
        }

        // ===== 回滚 =====
        private bool DoRollback(int errorFrame, uint correctRemoteRaw)
        {
            int lastExecutedFrame = _frameEngine.CurrentFrame - 1;
            int localIdx = _networkClient != null ? _networkClient.LocalPlayerIndex : 0;
            int remoteIdx = _networkClient != null ? _networkClient.RemotePlayerIndex : 1;
            for (int i = 0; i < _playerCount; i++)
                _rollbackPlayerDisplayPositions[i] = _players[i].transform.position;
            Vector3 ballDisplayPosition = _ballObject.transform.position;
            FixedInt moveDistance = FixedInt.FromFloat(_moveSpeed * 0.033f);
            FrameReplayResult result = FrameReplaySystem.Replay(
                errorFrame,
                lastExecutedFrame,
                correctRemoteRaw,
                NetworkFrameTimeline.RemoteFrameOffset,
                localIdx,
                remoteIdx,
                _frameEngine.Buffer,
                remoteFrame =>
                {
                    if (_networkClient != null &&
                        _networkClient.TryGetRemoteInputAt(remoteFrame, out uint actualRaw))
                    {
                        return actualRaw;
                    }

                    return null;
                },
                _predictionSystem,
                _playerEntities,
                _playerFSMs,
                _ballEntity,
                moveDistance,
                CourtConstant.LogicDeltaTime,
                ResetWorldLogic);

            SyncLegacyPositionsFromEntities();
            if (!result.succeeded)
            {
                Debug.LogError($"[Rollback] 帧{result.missingInputFrame}本地Input缺失");
                return false;
            }

            ReplacePresentationHistoryAfterRollback(lastExecutedFrame);
            BeginRollbackPresentationCorrection(
                ballDisplayPosition);

            Debug.Log(
                $"[RouteC][Rollback] errorFrame={errorFrame} " +
                $"restoredFrame={result.restoredFrame} " +
                $"replayed={result.replayedFrameCount} " +
                $"ballState={_ballEntity.state}");

            if (!_predictionSystem.TryGetWorldSnapshot(
                lastExecutedFrame,
                out FrameSnapshot finalSnapshot))
            {
                Debug.LogError(
                    $"[RouteC][WorldHash] frame={lastExecutedFrame} " +
                    "回滚成功但最终快照缺失");
                return false;
            }

            LogRollbackWorldHash(
                finalSnapshot,
                errorFrame,
                result.restoredFrame,
                result.replayedFrameCount);
            return true;
        }

        private void BeginRollbackPresentationCorrection(
            Vector3 ballDisplayPosition)
        {
            if (_presentationInterpolator == null ||
                _playerCorrectionSmoothers == null ||
                _ballSmoother == null ||
                _interpolatedPlayerPositions == null ||
                _rollbackPlayerDisplayPositions == null ||
                _playerPresentationPositions == null)
            {
                return;
            }

            _presentationInterpolator.Evaluate(
                GetPresentationLocalPlayerIndex(),
                _frameEngine.RenderInterpolationAlpha,
                _interpolatedPlayerPositions,
                out PresentationBallSample ballSample);
            for (int i = 0; i < _playerCount; i++)
            {
                _playerCorrectionSmoothers[i].BeginCorrection(
                    _rollbackPlayerDisplayPositions[i],
                    _interpolatedPlayerPositions[i]);
                _playerPresentationPositions[i] =
                    _rollbackPlayerDisplayPositions[i];
            }

            Vector3 ballTarget =
                PresentationTargetResolver.ResolveBufferedBallTarget(
                    ballSample,
                    _interpolatedPlayerPositions,
                    _playerPresentationPositions);
            if (PresentationTargetResolver.ShouldUseIndependentBallSmoother(
                ballSample))
            {
                _ballSmoother.BeginCorrection(
                    ballDisplayPosition,
                    ballTarget);
            }
            else
            {
                _ballSmoother.Snap();
            }
            RememberPresentedBallAttachment(ballSample);
        }

        private void ReplacePresentationHistoryAfterRollback(
            int lastExecutedFrame)
        {
            if (_predictionSystem.TryGetWorldSnapshot(
                lastExecutedFrame,
                out FrameSnapshot newest))
            {
                FrameSnapshot? previous = null;
                FrameSnapshot? oldest = null;
                if (_predictionSystem.TryGetWorldSnapshot(
                    lastExecutedFrame - 1,
                    out FrameSnapshot previousValue))
                {
                    previous = previousValue;
                    if (_predictionSystem.TryGetWorldSnapshot(
                        lastExecutedFrame - 2,
                        out FrameSnapshot oldestValue))
                    {
                        oldest = oldestValue;
                    }
                }

                _presentationInterpolator.ReplaceHistoryAfterRollback(
                    newest,
                    previous,
                    oldest);
                return;
            }

            Debug.LogError(
                $"[RouteC][Presentation] frame={lastExecutedFrame} " +
                "回滚最终快照缺失，表现缓冲退化为纠正后的实时世界");
            _presentationInterpolator.ReplaceAfterRollback(
                lastExecutedFrame,
                _playerEntities,
                _ballEntity);
        }

        private void RememberPresentedBallAttachment(
            PresentationBallSample sample)
        {
            _lastPresentedBallAttachmentIndex =
                sample.AttachedPlayerIndex;
            _hasPresentedBallAttachment = true;
        }

        private void LogNormalWorldHash(FrameSnapshot snapshot)
        {
            if (!TryGetCanonicalFrame(snapshot.frameID, out int canonicalFrame) ||
                _worldHashLogIntervalFrames <= 0 ||
                canonicalFrame % _worldHashLogIntervalFrames != 0)
            {
                return;
            }

            ulong hash = WorldHash.Compute(snapshot, canonicalFrame);
            Debug.Log(
                $"[RouteC][WorldHash] schema={WorldHash.SchemaVersion} " +
                $"algo={WorldHash.AlgorithmName} " +
                $"localPlayer={GetLocalPlayerLogIndex()} " +
                $"frame={canonicalFrame} localFrame={snapshot.frameID} " +
                $"phase=final source=normal " +
                $"hash=0x{hash:X16}");
        }

        private void LogRollbackWorldHash(
            FrameSnapshot snapshot,
            int errorFrame,
            int restoredFrame,
            int replayedFrameCount)
        {
            if (!TryGetCanonicalFrame(snapshot.frameID, out int canonicalFrame))
                return;

            ulong hash = WorldHash.Compute(snapshot, canonicalFrame);
            Debug.Log(
                $"[RouteC][WorldHash] schema={WorldHash.SchemaVersion} " +
                $"algo={WorldHash.AlgorithmName} " +
                $"localPlayer={GetLocalPlayerLogIndex()} " +
                $"frame={canonicalFrame} localFrame={snapshot.frameID} " +
                $"phase=final source=rollback " +
                $"errorFrame={errorFrame} restoredFrame={restoredFrame} " +
                $"replayed={replayedFrameCount} hash=0x{hash:X16}");
        }

        private int GetLocalPlayerLogIndex()
        {
            if (_networkClient != null && _networkClient.LocalPlayerIndex >= 0)
                return _networkClient.LocalPlayerIndex;

            return NetworkConfig.LocalPlayerID;
        }

        private int GetPresentationLocalPlayerIndex()
        {
            int localPlayerIndex = GetLocalPlayerLogIndex();
            return localPlayerIndex >= 0 && localPlayerIndex < _playerCount
                ? localPlayerIndex
                : 0;
        }

        private uint? GetRemoteActualRawForLocalFrame(int localFrame)
        {
            if (_networkClient == null)
                return null;

            int remoteFrame = NetworkFrameTimeline.RemoteFrameForLocal(localFrame);
            return _networkClient.TryGetRemoteInputAt(remoteFrame, out uint actualRaw)
                ? (uint?)actualRaw
                : null;
        }

        private void LogNetworkFrameGap(int localFrame)
        {
            if (_networkClient == null ||
                localFrame < 0 ||
                localFrame % 120 != 0)
            {
                return;
            }

            int latestRemoteFrame = _networkClient.LatestRemoteFrameID;
            if (latestRemoteFrame < 0)
                return;

            int arrivalGap = NetworkFrameTimeline.CalculateRemoteArrivalGap(
                localFrame,
                latestRemoteFrame);
            Debug.Log(
                $"[RouteC][NetworkTiming] localPlayer={GetLocalPlayerLogIndex()} " +
                $"localFrame={localFrame} latestRemoteFrame={latestRemoteFrame} " +
                $"arrivalGap={arrivalGap}");
        }

        private bool TryGetCanonicalFrame(int localFrame, out int canonicalFrame)
        {
            return CanonicalFrame.TryFromLocal(
                GetLocalPlayerLogIndex(),
                localFrame,
                NetworkFrameTimeline.RemoteFrameOffset,
                out canonicalFrame);
        }

        private void LateUpdate()
        {
            if (_matchPhase == MatchPhase.PostGameReplay)
            {
                _highlightReplayController?.Update(Time.unscaledDeltaTime);
                ApplyPostGameReplayPresentation();
                UpdateControlOverlay();
                return;
            }

            if (_networkClient != null && _networkClient.IsConnected)
            {
                _networkClient.DrainQueueToDict();
                bool correctionCompleted = true;
                if (PresentationTargetResolver.ShouldProcessRollback(
                        _paused,
                        false) &&
                    _rollbackRequests.TryTake(
                        out int errorFrame,
                        out uint correctRaw))
                {
                    correctionCompleted = DoRollback(errorFrame, correctRaw);
                    if (!correctionCompleted)
                        _rollbackRequests.Request(errorFrame, correctRaw);
                }

                if (correctionCompleted)
                    _highlightRemoteFrameGate.CommitCorrection();
            }

            ProcessStableHighlightFrames();
            if (_matchPhase == MatchPhase.PostGameReplay)
            {
                ApplyPostGameReplayPresentation();
                UpdateControlOverlay();
                return;
            }

            if (_networkClient != null && _networkClient.IsConnected)
            {
                int minKeep = _frameEngine.CurrentFrame - 120;
                if (minKeep > 0)
                    _networkClient.CleanupRemoteInputs(minKeep);
            }

            SyncPresentationFromLogic(_paused ? 0f : Time.deltaTime);
            UpdateControlOverlay();
        }

        private void ProcessStableHighlightFrames()
        {
            if (_highlightStableCursor == null ||
                _highlightRecorder == null ||
                _predictionSystem == null ||
                _frameEngine == null)
            {
                return;
            }

            bool isConnected = _networkClient != null &&
                _networkClient.IsConnected;
            int latestRemoteFrame = isConnected
                ? _highlightRemoteFrameGate.ValidatedThroughFrame
                : -1;
            int stableThroughFrame = _highlightStableCursor.CalculateStableThrough(
                _frameEngine.CurrentFrame - 1,
                latestRemoteFrame,
                isConnected);

            while (_highlightStableCursor.TryGetNext(
                stableThroughFrame,
                out int frameID))
            {
                if (!_predictionSystem.TryGetWorldSnapshot(
                    frameID,
                    out FrameSnapshot snapshot))
                {
                    if (TryRebaseExpiredHighlightHistory(frameID))
                        continue;

                    Debug.LogError(
                        $"[RouteC][Highlight] stable snapshot missing " +
                        $"frame={frameID}");
                    return;
                }

                if (!TryGetStableActualInputs(frameID, out FrameInput[] inputs))
                {
                    if (TryRebaseExpiredHighlightHistory(frameID))
                        continue;

                    return;
                }

                _highlightRecorder.ProcessStableFrame(
                    frameID,
                    snapshot,
                    inputs);
                _highlightStableCursor.MarkProcessed(frameID);

                if (!string.IsNullOrEmpty(_highlightRecorder.LastCaptureError) &&
                    _highlightRecorder.LastCaptureError != _lastHighlightCaptureError)
                {
                    _lastHighlightCaptureError =
                        _highlightRecorder.LastCaptureError;
                    Debug.LogError(
                        $"[RouteC][Highlight] {_lastHighlightCaptureError}");
                }

                if (_highlightRecorder.CanEnterPostGame)
                {
                    EnterPostGameReplay();
                    return;
                }
            }
        }

        private bool TryRebaseExpiredHighlightHistory(int missingFrameID)
        {
            int firstRetainedFrame = System.Math.Max(
                0,
                _frameEngine.Buffer.MaxWrittenFrame - FrameBuffer.CAPACITY + 1);
            if (_networkClient != null && _networkClient.IsConnected)
            {
                int firstRetainedRemoteFrame =
                    _networkClient.GetMinRemoteFrameID();
                if (firstRetainedRemoteFrame >= 0)
                {
                    firstRetainedFrame = System.Math.Max(
                        firstRetainedFrame,
                        firstRetainedRemoteFrame);
                }
            }

            if (firstRetainedFrame <= missingFrameID)
                return false;

            _highlightStableCursor.RebaseAt(firstRetainedFrame);
            _highlightRecorder.RebaseAt(firstRetainedFrame);
            Debug.LogError(
                $"[RouteC][Highlight] stable history expired at " +
                $"frame={missingFrameID}; resumed at frame={firstRetainedFrame}");
            return true;
        }

        private bool TryGetStableActualInputs(
            int frameID,
            out FrameInput[] inputs)
        {
            inputs = null;
            if (!_frameEngine.Buffer.PeekFrame(
                frameID,
                out FrameBuffer.Frame frameData) ||
                frameData.inputs == null ||
                frameData.inputs.Length != _playerCount)
            {
                return false;
            }

            inputs = (FrameInput[])frameData.inputs.Clone();
            if (_networkClient == null || !_networkClient.IsConnected)
                return true;

            int remoteFrame = NetworkFrameTimeline.RemoteFrameForLocal(frameID);
            if (!_networkClient.TryGetRemoteInputAt(
                remoteFrame,
                out uint actualRemoteRaw))
            {
                inputs = null;
                return false;
            }

            inputs[_networkClient.RemotePlayerIndex] =
                FrameInput.FromRaw(actualRemoteRaw);
            return true;
        }

        private void EnterPostGameReplay()
        {
            if (_matchPhase == MatchPhase.PostGameReplay)
                return;

            if (!PostGameTransitionSystem.TryRestoreTerminalWorld(
                _highlightRecorder,
                _predictionSystem,
                _playerEntities,
                _ballEntity,
                out int terminalFrame))
            {
                Debug.LogError(
                    $"[RouteC][Highlight] terminal snapshot missing " +
                    $"frame={terminalFrame}; postgame transition cancelled");
                return;
            }

            ClearLocalFrameActionsForCurrentRenderFrame();
            SnapPresentationToLogic(terminalFrame);
            _frameEngine.Pause();
            _matchPhase = MatchPhase.PostGameReplay;
            _highlightReplayController =
                new PostGameHighlightReplayController(_highlightRecorder.Clips);
            Debug.Log(
                $"[RouteC][Highlight] postgame replay started " +
                $"requestFrame={_highlightRecorder.EndMatchRequestFrame} " +
                $"terminalFrame={terminalFrame} " +
                $"clipCount={_highlightRecorder.Clips.Count}");
        }

        private void ApplyPostGameReplayPresentation()
        {
            if (_highlightReplayController == null ||
                !_highlightReplayController.TryGetSample(
                    out FrameSnapshot from,
                    out FrameSnapshot to,
                    out float alpha))
            {
                return;
            }

            HighlightPresentationSample sample =
                HighlightSnapshotInterpolator.Interpolate(from, to, alpha);
            _players[0].transform.position = sample.player0Position;
            _players[1].transform.position = sample.player1Position;
            _ballObject.transform.position = sample.ballPosition;
        }

        private void UpdateControlOverlay()
        {
            if (_controlOverlay == null)
                return;

            _controlOverlay.Phase = _matchPhase;
            _controlOverlay.WaitingForHighlightTail =
                _matchPhase == MatchPhase.Playing &&
                _highlightRecorder != null &&
                _highlightRecorder.EndMatchRequested &&
                !_highlightRecorder.CanEnterPostGame;
            int localPlayerIndex = GetPresentationLocalPlayerIndex();
            bool localHasBall = _playerEntities != null &&
                localPlayerIndex >= 0 &&
                localPlayerIndex < _playerEntities.Length &&
                _playerEntities[localPlayerIndex].hasBall;
            _controlOverlay.IsPreparingShot =
                RuntimeControlOverlay.ShouldShowShotPreparation(
                    _matchPhase,
                    _paused,
                    Input.GetKey(KeyCode.Space),
                    localHasBall);
            _controlOverlay.BallState = _ballEntity != null
                ? _ballEntity.state
                : BallEntity.EState.Resetting;
            _controlOverlay.HolderPlayerIndex = _ballEntity != null
                ? _ballEntity.holderPlayerIndex
                : -1;
            _controlOverlay.ClipIndex =
                _highlightReplayController?.CurrentClipIndex ?? 0;
            _controlOverlay.ClipCount =
                _highlightReplayController?.ClipCount ?? 0;
            _controlOverlay.CurrentClip =
                _highlightReplayController?.CurrentClip;
            _controlOverlay.ReplayPlaying =
                _highlightReplayController != null &&
                _highlightReplayController.IsPlaying;
        }

        private void OnDestroy()
        {
            _localFrameActionBuffer.Clear();
            if (_frameEngine != null)
            {
                _frameEngine.OnFrameUpdate -= OnFrameUpdate;
                _frameEngine.OnPostFrameUpdate -= OnPostFrameUpdate;
                _frameEngine.OnCatchup -= OnCatchup;
            }
        }
    }
}

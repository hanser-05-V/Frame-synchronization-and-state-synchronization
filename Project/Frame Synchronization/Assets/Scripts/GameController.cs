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

        [Header("P2-E Network Diagnostics")]
        [SerializeField] private bool _enableP2EDiagnostics;

        [Header("P2-E Confirmed Playback")]
        [SerializeField]
        private ConfirmedPlaybackSettings _confirmedPlaybackSettings =
            new ConfirmedPlaybackSettings();

        [Header("回滚表现平滑")]
        [SerializeField] private float _rollbackVisualSmoothingSeconds = 0.1f;
        [SerializeField] private float _rollbackVisualMaxSmoothingSeconds = 0.2f;
        [SerializeField] private float _rollbackVisualMaxCorrectionSpeed = 10f;
        [SerializeField] private float _rollbackVisualSnapDistance = 2f;

        // ----- 渲染对象 -----
        private GameObject[] _players;
        private GameObject _ballObject;
        private PresentationFrameInterpolator _presentationInterpolator;
        private ConfirmedPresentationCursor _confirmedPresentationCursor;
        private ConfirmedPlaybackAdvance _lastConfirmedPlaybackAdvance;
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
        private FrameSyncCoordinator _frameSyncCoordinator;
        private FrameInputLedger.ResolvedFrame _pendingResolvedInputs;
        private int _pendingCanonicalFrame = -1;
        private int _latestLocallySubmittedFrameID = -1;
        private FrameAdvanceResult _lastFrameAdvance;
        private bool _reportedHeldInvariantError;

        // ----- 兼容字段（阶段0回滚用）-----
        private FixedInt[] _blockPosX;
        private FixedInt[] _blockPosZ;

        private int _playerCount = 2;
        private bool _paused = false;
        private NetworkClient _networkClient;
        private bool _networkStartPending;
        private bool _networkSessionStarted;
        private bool _networkSessionStartEventPending;
        private string _networkTerminalFault = string.Empty;
        private RuntimeNetworkDiagnostics _runtimeNetworkDiagnostics;
        private MatchPhase _matchPhase = MatchPhase.Playing;
        private StableFrameCursor _highlightStableCursor;
        private StableRemoteFrameGate _highlightRemoteFrameGate;
        private HighlightReplayRecorder _highlightRecorder;
        private PostGameHighlightReplayController _highlightReplayController;
        private RuntimeControlOverlay _controlOverlay;
        private string _lastHighlightCaptureError = string.Empty;

        private readonly LocalFrameActionBuffer _localFrameActionBuffer =
            new LocalFrameActionBuffer();

        private void Start()
        {
            Initialize();
        }

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
            var predictedWorld = new DeterministicWorld(
                _playerEntities,
                _playerFSMs,
                _ballEntity,
                FixedInt.FromFloat(_moveSpeed * 0.033f),
                CourtConstant.LogicDeltaTime);

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
            if (_confirmedPlaybackSettings == null)
            {
                _confirmedPlaybackSettings =
                    new ConfirmedPlaybackSettings();
            }
            _confirmedPlaybackSettings.ValidateOrReset(Debug.LogWarning);
            _runtimeNetworkDiagnostics = new RuntimeNetworkDiagnostics(
                IsP2EDiagnosticsEnabled(),
                Debug.Log,
                timestampFrequency: 0,
                targetReadyBacklog:
                    _confirmedPlaybackSettings.TargetReadyBacklog,
                maxReadyBacklog:
                    _confirmedPlaybackSettings.MaxReadyBacklog);
            _frameEngine.IsNetworkMode = true;

            SimulationWorldState initialWorld = predictedWorld.Capture(-1);
            _frameSyncCoordinator = new FrameSyncCoordinator(
                predictedWorld,
                initialWorld,
                FixedInt.FromFloat(_moveSpeed * 0.033f),
                CourtConstant.LogicDeltaTime);
            _latestLocallySubmittedFrameID = -1;
            _confirmedPresentationCursor =
                new ConfirmedPresentationCursor(
                    _confirmedPlaybackSettings);
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
            _networkClient.ConnectConfigured();
            _networkStartPending = true;
            _networkSessionStarted = false;
            _networkSessionStartEventPending = false;
            _networkTerminalFault = string.Empty;
            Debug.Log("[GameController] 网络握手进行中，帧引擎保持停止");
        }

        // ===== ReadInputs — 和阶段0完全一致 =====
        private FrameInput[] ReadInputs()
        {
            CaptureLocalFrameActions();

            if (_paused)
                return new FrameInput[] { new FrameInput(), new FrameInput() };

            int localFrame = _frameEngine.CurrentFrame - 1;
            if (localFrame < 0)
                return new FrameInput[] { new FrameInput(), new FrameInput() };

            int canonicalFrame = localFrame;

            if (ShouldUseNetworkInputPath())
            {
                FrameInput localInput = ReadPrimaryInput();
                if (!CanonicalFrame.TryFromLocal(
                    _networkClient.LocalPlayerIndex,
                    localFrame,
                    NetworkFrameTimeline.RemoteFrameOffset,
                    out canonicalFrame))
                {
                    _paused = true;
                    return new FrameInput[] { new FrameInput(), new FrameInput() };
                }

                if (!RecordLedgerActual(
                        canonicalFrame,
                        _networkClient.LocalPlayerIndex,
                        localInput) ||
                    !DrainRemoteInputsToLedger())
                {
                    return new FrameInput[] { new FrameInput(), new FrameInput() };
                }

                LogNetworkFrameGap(localFrame);
                if (!TrySubmitLocalInput(localInput._raw, localFrame))
                    return new FrameInput[] { new FrameInput(), new FrameInput() };
            }
            else
            {
                if (!RecordLedgerActual(canonicalFrame, 0, ReadPrimaryInput()) ||
                    !RecordLedgerActual(canonicalFrame, 1, ReadP2Input()))
                {
                    return new FrameInput[] { new FrameInput(), new FrameInput() };
                }
            }

            FrameInputLedger.ResolvedFrame resolved =
                _frameSyncCoordinator.ResolveForPrediction(canonicalFrame);
            _pendingCanonicalFrame = canonicalFrame;
            _pendingResolvedInputs = resolved;
            return new[] { resolved.GetPlayer(0).Value, resolved.GetPlayer(1).Value };
        }

        // ===== OnFrameUpdate — 确定性世界更新 =====
        private void OnFrameUpdate(int frameID, FrameInput[] inputs)
        {
            if (_paused) return;

            if (!TryGetCanonicalFrame(frameID, out int canonicalFrame) ||
                canonicalFrame != _pendingCanonicalFrame)
            {
                _paused = true;
                Debug.LogError(
                    $"[RouteC][CoordinatorFault] invalid pending frame " +
                    $"local={frameID} canonical={canonicalFrame} " +
                    $"pending={_pendingCanonicalFrame}");
                return;
            }

            int confirmedBefore = _frameSyncCoordinator.ConfirmedFrame;
            _lastFrameAdvance = _frameSyncCoordinator.Advance(
                canonicalFrame,
                _pendingResolvedInputs);
            if (!_lastFrameAdvance.Succeeded)
            {
                _paused = true;
                Debug.LogError(
                    $"[RouteC][CoordinatorFault] advance failed frame={canonicalFrame}");
                return;
            }

            int viewFrame = -1;
            if (_runtimeNetworkDiagnostics.Enabled &&
                TryBuildRealtimeViewWorld(out ViewWorldState diagnosticView))
            {
                viewFrame = diagnosticView.PresentationFrame;
            }
            _runtimeNetworkDiagnostics.RecordLogic(
                System.Diagnostics.Stopwatch.GetTimestamp(),
                confirmedBefore,
                _frameSyncCoordinator.ConfirmedFrame,
                _frameSyncCoordinator.PredictedFrame,
                viewFrame);

            SyncLegacyPositionsFromEntities();
            LogSimulationResult(frameID, _lastFrameAdvance.SimulationResult);
        }

        // ===== OnPostFrameUpdate — 完整帧快照 =====
        private void OnPostFrameUpdate(int frameID, FrameInput[] inputs)
        {
            if (_paused) return;

            if (_frameSyncCoordinator != null)
            {
                if (!TryGetCanonicalFrame(frameID, out int canonicalFrame))
                {
                    _paused = true;
                    Debug.LogError($"[RouteC][LedgerFault] invalid local snapshot frame={frameID}");
                    return;
                }

                if (!_frameSyncCoordinator.TryGetFrameSnapshot(
                        WorldTrack.Predicted,
                        canonicalFrame,
                        out FrameSnapshot snapshot))
                {
                    _paused = true;
                    Debug.LogError(
                        $"[RouteC][CoordinatorFault] predicted snapshot missing " +
                        $"frame={canonicalFrame}");
                    return;
                }

                if (!_frameSyncCoordinator.TryGetEarliestMismatch(out _) &&
                    _frameSyncCoordinator.TryGetFrameSnapshot(
                        WorldTrack.Confirmed,
                        canonicalFrame,
                        out FrameSnapshot confirmedSnapshot))
                {
                    LogNormalWorldHash(confirmedSnapshot, canonicalFrame);
                }
            }

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
                GetConfirmedPresentationAlpha(),
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
            bool attachmentChanged = _hasPresentedBallAttachment &&
                PresentationTargetResolver.ShouldTransferBallVisualCorrection(
                    _lastPresentedBallAttachmentIndex,
                    ballSample.AttachedPlayerIndex);
            if (attachmentChanged)
            {
                _ballSmoother.BeginCorrection(
                    _ballObject.transform.position,
                    ballTarget);
            }

            Vector3 ballPosition;
            if (PresentationTargetResolver.ShouldUseBallCorrectionSmoother(
                    ballSample,
                    _ballSmoother.IsCorrecting))
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

            if (_runtimeNetworkDiagnostics.Enabled)
            {
                int remotePlayerIndex = GetPresentationLocalPlayerIndex() == 0
                    ? 1
                    : 0;
                int viewFrame =
                    _lastConfirmedPlaybackAdvance.HasPresentationFrame
                        ? _lastConfirmedPlaybackAdvance.ActiveToFrame
                        : -1;
                Vector3 remotePosition =
                    _players[remotePlayerIndex].transform.position;
                _runtimeNetworkDiagnostics.RecordRemoteRender(
                    System.Diagnostics.Stopwatch.GetTimestamp(),
                    remotePosition.x,
                    remotePosition.z,
                    _frameSyncCoordinator.PredictedFrame,
                    _frameSyncCoordinator.ConfirmedFrame,
                    viewFrame);
            }
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
            if (_presentationInterpolator != null &&
                TryBuildRealtimeViewWorld(out ViewWorldState viewWorld))
            {
                _presentationInterpolator.Reset(viewWorld);
            }
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

            ProcessNetworkTransportEvents();

            if (_matchPhase == MatchPhase.PostGameReplay)
            {
                HandlePostGameReplayInput();
                UpdateControlOverlay();
                return;
            }

            CaptureLocalFrameActions();
            if (Input.GetKeyDown(KeyCode.F10))
            {
                bool isConnected = ShouldUseNetworkInputPath();
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

        private bool ShouldUseNetworkInputPath()
        {
            return _networkClient != null &&
                   _networkClient.HasStartedSession;
        }

        private bool TrySubmitLocalInput(uint raw, int localFrameID)
        {
            if (_networkClient != null &&
                _networkClient.SendInput(raw, localFrameID))
            {
                if (localFrameID > _latestLocallySubmittedFrameID)
                    _latestLocallySubmittedFrameID = localFrameID;
                return true;
            }

            EnterMatchTerminalFault(new NetworkTransportEvent(
                NetworkSessionState.Terminated,
                NetworkTransportEventReason.LocalInputQueueFull,
                "none",
                0u,
                0u));
            return false;
        }

        private void ProcessNetworkTransportEvents()
        {
            if (_networkClient == null)
                return;

            while (_networkClient.TryGetTransportEvent(
                out NetworkTransportEvent transportEvent))
            {
                if (transportEvent.Reason ==
                        NetworkTransportEventReason.SessionStarted &&
                    transportEvent.State == NetworkSessionState.Running)
                {
                    _networkSessionStartEventPending = true;
                    continue;
                }

                if (transportEvent.Reason ==
                    NetworkTransportEventReason.ResumeRequired)
                {
                    ResumeReadiness readiness = BuildResumeReadiness();
                    _networkClient.SubmitResumeReadiness(in readiness);
                    continue;
                }

                if (transportEvent.State == NetworkSessionState.Terminated ||
                    transportEvent.Reason ==
                        NetworkTransportEventReason.ResumeRejected ||
                    transportEvent.Reason ==
                        NetworkTransportEventReason.WorkerFault ||
                    transportEvent.Reason ==
                        NetworkTransportEventReason.WorkerStopTimedOut ||
                    transportEvent.Reason ==
                        NetworkTransportEventReason.OutboundHistoryFault)
                {
                    _networkSessionStartEventPending = false;
                    EnterMatchTerminalFault(transportEvent);
                }
            }

            TryStartPendingNetworkSession();
        }

        private void TryStartPendingNetworkSession()
        {
            if (!_networkSessionStartEventPending ||
                _networkSessionStarted ||
                _networkClient.State != NetworkSessionState.Running ||
                !_networkClient.HasStartedSession ||
                (_networkClient.LocalPlayerIndex != 0 &&
                 _networkClient.LocalPlayerIndex != 1))
            {
                return;
            }

            _networkSessionStartEventPending = false;
            _networkSessionStarted = true;
            _networkStartPending = false;
            _frameEngine.StartEngine();
            Debug.Log(
                "[GameController] 双客户端已共同放行，共享帧起点=0，远端偏移=0");
        }

        private void EnterMatchTerminalFault(
            NetworkTransportEvent transportEvent)
        {
            _networkStartPending = false;
            _paused = true;
            _frameEngine.Pause();
            _networkTerminalFault =
                "[RouteC][MatchTerminalFault] state=" +
                transportEvent.State +
                " reason=" +
                transportEvent.Reason;
            Debug.LogError(_networkTerminalFault);
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

        // ===== 回滚 =====
        private bool DoRollback(int errorFrame)
        {
            int lastExecutedLocalFrame = _frameEngine.CurrentFrame - 1;
            if (!TryGetCanonicalFrame(lastExecutedLocalFrame, out int lastExecutedFrame))
            {
                _paused = true;
                Debug.LogError(
                    $"[RouteC][LedgerFault] invalid rollback local frame={lastExecutedLocalFrame}");
                return false;
            }

            for (int i = 0; i < _playerCount; i++)
                _rollbackPlayerDisplayPositions[i] = _players[i].transform.position;
            Vector3 ballDisplayPosition = _ballObject.transform.position;
            ReconcileResult result = _frameSyncCoordinator.Reconcile();

            SyncLegacyPositionsFromEntities();
            if (!result.Succeeded)
            {
                _paused = true;
                Debug.LogError(
                    $"[RouteC][RollbackFault] phase=replay " +
                    $"plan={result.PlanResult} failure={result.ReplayFailure} " +
                    $"errorFrame={errorFrame} restored={result.RestoredFrame}");
                return false;
            }

            _confirmedPresentationCursor?.RecalculateAfterRollback(
                _frameSyncCoordinator.ConfirmedFrame);
            if (ReplacePresentationHistoryAfterRollback(lastExecutedFrame))
            {
                BeginRollbackPresentationCorrection(
                    ballDisplayPosition);
            }

            Debug.Log(
                $"[RouteC][Rollback] errorFrame={errorFrame} " +
                $"restoredFrame={result.RestoredFrame} " +
                $"replayed={result.ReplayedFrameCount} " +
                $"ballState={_ballEntity.state}");

            int confirmedFrame = _frameSyncCoordinator.ConfirmedFrame;
            FrameSnapshot confirmedSnapshot = default;
            bool hasConfirmedSnapshot = confirmedFrame >= 0 &&
                _frameSyncCoordinator.TryGetFrameSnapshot(
                    WorldTrack.Confirmed,
                    confirmedFrame,
                    out confirmedSnapshot);
            if (confirmedFrame >= 0 && !hasConfirmedSnapshot)
            {
                Debug.LogError(
                    $"[RouteC][WorldHash] frame={confirmedFrame} " +
                    "回滚成功但确认快照缺失");
                _paused = true;
                return false;
            }

            if (hasConfirmedSnapshot)
            {
                LogRollbackWorldHash(
                    confirmedSnapshot,
                    errorFrame,
                    result.RestoredFrame,
                    result.ReplayedFrameCount);
                _runtimeNetworkDiagnostics.RecordRollback(
                    System.Diagnostics.Stopwatch.GetTimestamp(),
                    errorFrame,
                    result.RestoredFrame,
                    result.ReplayedFrameCount,
                    WorldHash.Compute(confirmedSnapshot, confirmedFrame));
            }
            return true;
        }

        private void BeginRollbackPresentationCorrection(
            Vector3 ballDisplayPosition,
            float maximumDurationSeconds = 0f)
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
                GetConfirmedPresentationAlpha(),
                _interpolatedPlayerPositions,
                out PresentationBallSample ballSample);
            for (int i = 0; i < _playerCount; i++)
            {
                if (maximumDurationSeconds > 0f)
                {
                    _playerCorrectionSmoothers[i].BeginCorrection(
                        _rollbackPlayerDisplayPositions[i],
                        _interpolatedPlayerPositions[i],
                        maximumDurationSeconds);
                }
                else
                {
                    _playerCorrectionSmoothers[i].BeginCorrection(
                        _rollbackPlayerDisplayPositions[i],
                        _interpolatedPlayerPositions[i]);
                }
                _playerPresentationPositions[i] =
                    _rollbackPlayerDisplayPositions[i];
            }

            Vector3 ballTarget =
                PresentationTargetResolver.ResolveBufferedBallTarget(
                    ballSample,
                    _interpolatedPlayerPositions,
                    _playerPresentationPositions);
            bool attachmentChanged = _hasPresentedBallAttachment &&
                PresentationTargetResolver.ShouldTransferBallVisualCorrection(
                    _lastPresentedBallAttachmentIndex,
                    ballSample.AttachedPlayerIndex);
            if (PresentationTargetResolver.ShouldUseIndependentBallSmoother(
                    ballSample) ||
                attachmentChanged)
            {
                if (maximumDurationSeconds > 0f)
                {
                    _ballSmoother.BeginCorrection(
                        ballDisplayPosition,
                        ballTarget,
                        maximumDurationSeconds);
                }
                else
                {
                    _ballSmoother.BeginCorrection(
                        ballDisplayPosition,
                        ballTarget);
                }
            }
            else
            {
                _ballSmoother.Snap();
            }
            RememberPresentedBallAttachment(ballSample);
        }

        private bool ReplacePresentationHistoryAfterRollback(
            int lastExecutedFrame)
        {
            if (TryBuildPresentationCursorViewWorld(
                out ViewWorldState viewWorld))
            {
                _presentationInterpolator.ReplaceViewWorld(viewWorld);
                return true;
            }

            Debug.LogError(
                $"[RouteC][PresentationFault] frame={lastExecutedFrame} " +
                $"missing=[{_confirmedPresentationCursor.ActiveFromFrame}," +
                $"{_confirmedPresentationCursor.ActiveToFrame}] " +
                "after rollback; confirmed presentation is held.");
            _confirmedPresentationCursor.EnterPresentationFault(
                _confirmedPresentationCursor.ActiveFromFrame,
                _confirmedPresentationCursor.ActiveToFrame);
            return false;
        }

        private bool TryBuildRealtimeViewWorld(out ViewWorldState viewWorld)
        {
            if (_frameSyncCoordinator == null)
            {
                viewWorld = default;
                return false;
            }

            return ViewWorldBuilder.TryBuild(
                _frameSyncCoordinator,
                GetPresentationLocalPlayerIndex(),
                out viewWorld);
        }

        private bool TryBuildPresentationCursorViewWorld(
            out ViewWorldState viewWorld)
        {
            if (_frameSyncCoordinator == null ||
                _confirmedPresentationCursor == null)
            {
                viewWorld = default;
                return false;
            }

            return ViewWorldBuilder.TryBuild(
                _frameSyncCoordinator,
                GetPresentationLocalPlayerIndex(),
                _confirmedPresentationCursor.ActiveToFrame,
                out viewWorld);
        }

        private bool AdvanceRealtimePresentation(
            float deltaTimeSeconds,
            float frameDurationSeconds)
        {
            if (_presentationInterpolator == null ||
                _frameSyncCoordinator == null ||
                _confirmedPresentationCursor == null)
            {
                return false;
            }

            ConfirmedPlaybackAdvance advance =
                _confirmedPresentationCursor.Advance(
                    _frameSyncCoordinator.ConfirmedFrame,
                    deltaTimeSeconds,
                    frameDurationSeconds);
            _lastConfirmedPlaybackAdvance = advance;
            long observedTimestamp =
                System.Diagnostics.Stopwatch.GetTimestamp();
            if (advance.IsFaulted)
            {
                _runtimeNetworkDiagnostics.RecordConfirmedPlayback(
                    observedTimestamp,
                    advance);
                return false;
            }

            int presentationFrame = advance.HasPresentationFrame
                ? advance.ActiveToFrame
                : -1;
            Vector3 ballDisplayPosition = default;
            bool capturedOverflowDisplay = advance.IsOverflowRebase &&
                TryCapturePresentationDisplay(out ballDisplayPosition);
            if (!ViewWorldBuilder.TryBuild(
                    _frameSyncCoordinator,
                    GetPresentationLocalPlayerIndex(),
                    presentationFrame,
                    out ViewWorldState viewWorld))
            {
                if (advance.HasPresentationFrame)
                {
                    _confirmedPresentationCursor.EnterPresentationFault(
                        advance.ActiveFromFrame,
                        advance.ActiveToFrame);
                    _lastConfirmedPlaybackAdvance =
                        _confirmedPresentationCursor.Advance(
                            _frameSyncCoordinator.ConfirmedFrame,
                            0f,
                            frameDurationSeconds);
                    Debug.LogError(
                        "[RouteC][PresentationFault] confirmed range=" +
                        $"[{advance.ActiveFromFrame}," +
                        $"{advance.ActiveToFrame}] is unavailable; " +
                        "confirmed presentation is held.");
                }
                _runtimeNetworkDiagnostics.RecordConfirmedPlayback(
                    observedTimestamp,
                    _lastConfirmedPlaybackAdvance);
                return false;
            }

            if (_presentationInterpolator.CanPushLogicFrame(
                    viewWorld.PredictedFrame))
            {
                _presentationInterpolator.PushViewWorld(viewWorld);
            }
            else
            {
                _presentationInterpolator.ReplaceViewWorld(viewWorld);
            }

            if (capturedOverflowDisplay)
            {
                BeginRollbackPresentationCorrection(
                    ballDisplayPosition,
                    _confirmedPlaybackSettings
                        .OverflowCorrectionMaximumSeconds);
            }

            _runtimeNetworkDiagnostics.RecordBallSourceSwitch(
                observedTimestamp,
                viewWorld.PresentationFrame,
                viewWorld.Ball.Source,
                viewWorld.Ball.ToHolderPlayerIndex);
            _runtimeNetworkDiagnostics.RecordConfirmedPlayback(
                observedTimestamp,
                advance);

            return true;
        }

        private bool TryCapturePresentationDisplay(
            out Vector3 ballDisplayPosition)
        {
            if (_players == null ||
                _ballObject == null ||
                _rollbackPlayerDisplayPositions == null ||
                _players.Length != _rollbackPlayerDisplayPositions.Length)
            {
                ballDisplayPosition = default;
                return false;
            }

            for (int i = 0; i < _players.Length; i++)
            {
                if (_players[i] == null)
                {
                    ballDisplayPosition = default;
                    return false;
                }

                _rollbackPlayerDisplayPositions[i] =
                    _players[i].transform.position;
            }

            ballDisplayPosition = _ballObject.transform.position;
            return true;
        }

        private float GetConfirmedPresentationAlpha()
        {
            return _confirmedPresentationCursor != null
                ? _confirmedPresentationCursor.InterpolationAlpha
                : _frameEngine.RenderInterpolationAlpha;
        }

        private void RememberPresentedBallAttachment(
            PresentationBallSample sample)
        {
            _lastPresentedBallAttachmentIndex =
                sample.AttachedPlayerIndex;
            _hasPresentedBallAttachment = true;
        }

        private void LogNormalWorldHash(FrameSnapshot snapshot, int canonicalFrame)
        {
            if (_worldHashLogIntervalFrames <= 0 ||
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
                $"phase=confirmed source=normal " +
                $"hash=0x{hash:X16}");
        }

        private void LogRollbackWorldHash(
            FrameSnapshot snapshot,
            int errorFrame,
            int restoredFrame,
            int replayedFrameCount)
        {
            ulong hash = WorldHash.Compute(snapshot, snapshot.frameID);
            Debug.Log(
                $"[RouteC][WorldHash] schema={WorldHash.SchemaVersion} " +
                $"algo={WorldHash.AlgorithmName} " +
                $"localPlayer={GetLocalPlayerLogIndex()} " +
                $"frame={snapshot.frameID} " +
                $"phase=confirmed source=rollback " +
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

        private bool DrainRemoteInputsToLedger()
        {
            int drainCount = 0;
            long firstReceivedTimestamp = 0;
            long lastReceivedTimestamp = 0;
            while (_networkClient.TryGetRemoteInput(
                out NetworkPacketArrival packetArrival))
            {
                uint raw = packetArrival.Raw;
                int remoteFrame = packetArrival.RemoteFrameID;
                _runtimeNetworkDiagnostics.RecordReceive(packetArrival);
                if (drainCount == 0)
                    firstReceivedTimestamp = packetArrival.ReceivedTimestamp;
                lastReceivedTimestamp = packetArrival.ReceivedTimestamp;
                drainCount++;

                if (remoteFrame < 0)
                {
                    _paused = true;
                    Debug.LogError($"[RouteC][LedgerFault] invalid remote frame={remoteFrame}");
                    return false;
                }

                if (!CanonicalFrame.TryFromLocal(
                    _networkClient.RemotePlayerIndex,
                    remoteFrame,
                    NetworkFrameTimeline.RemoteFrameOffset,
                    out int canonicalFrame))
                {
                    _paused = true;
                    Debug.LogError($"[RouteC][LedgerFault] invalid remote frame={remoteFrame}");
                    return false;
                }

                _runtimeNetworkDiagnostics.RecordActualArrival(
                    canonicalFrame,
                    packetArrival.ReceivedTimestamp);
                if (!RecordLedgerActual(
                        canonicalFrame,
                        _networkClient.RemotePlayerIndex,
                        FrameInput.FromRaw(raw)))
                {
                    return false;
                }
            }

            if (drainCount > 0)
            {
                _runtimeNetworkDiagnostics.RecordDrain(
                    System.Diagnostics.Stopwatch.GetTimestamp(),
                    drainCount,
                    firstReceivedTimestamp,
                    lastReceivedTimestamp);
            }

            return true;
        }

        private bool IsP2EDiagnosticsEnabled()
        {
            if (_enableP2EDiagnostics)
                return true;

            string[] arguments = System.Environment.GetCommandLineArgs();
            return System.Array.IndexOf(arguments, "-p2eDiagnostics") >= 0;
        }

        private bool RecordLedgerActual(int frame, int playerIndex, FrameInput input)
        {
            FrameInputLedger.ActualArrival arrival =
                _frameSyncCoordinator.RecordActual(frame, playerIndex, input);
            if (arrival.Disposition == FrameInputLedger.ActualDisposition.HistoryUnavailable ||
                arrival.Disposition == FrameInputLedger.ActualDisposition.CapacityExceeded ||
                arrival.Disposition == FrameInputLedger.ActualDisposition.ConflictingDuplicate)
            {
                _paused = true;
                Debug.LogError($"[RouteC][LedgerFault] frame={frame} player={playerIndex} raw=0x{input._raw:X8} floor={_frameSyncCoordinator.FirstRetainedFrame} disposition={arrival.Disposition}");
                return false;
            }

            return true;
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

        private ResumeReadiness BuildResumeReadiness()
        {
            if (_frameSyncCoordinator == null)
            {
                throw new System.InvalidOperationException(
                    "Frame-sync coordinator is not initialized.");
            }

            int lastContiguousRemoteFrameID = _networkClient == null
                ? -1
                : _networkClient.LatestRemoteFrameID;
            return new ResumeReadiness(
                lastContiguousRemoteFrameID,
                _frameSyncCoordinator.EarliestRecoverableCanonicalFrame,
                _latestLocallySubmittedFrameID);
        }

        private bool TryGetCanonicalFrame(int localFrame, out int canonicalFrame)
        {
            return CanonicalFrame.TryFromLocal(
                GetLocalPlayerLogIndex(),
                localFrame,
                NetworkFrameTimeline.RemoteFrameOffset,
                out canonicalFrame);
        }

        private bool TryGetLocalFrame(int canonicalFrame, out int localFrame)
        {
            return CanonicalFrame.TryToLocal(
                GetLocalPlayerLogIndex(),
                canonicalFrame,
                NetworkFrameTimeline.RemoteFrameOffset,
                out localFrame);
        }

        private void StageConfirmedLedgerFrameForHighlight()
        {
            if (_frameSyncCoordinator == null || _highlightRemoteFrameGate == null ||
                _frameSyncCoordinator.ConfirmedThroughFrame < _frameSyncCoordinator.StartFrame)
            {
                return;
            }

            if (!TryGetLocalFrame(
                    _frameSyncCoordinator.ConfirmedThroughFrame,
                    out int confirmedLocalFrame))
            {
                _paused = true;
                Debug.LogError(
                    "[RouteC][LedgerFault] cannot map confirmed ledger frame to local frame.");
                return;
            }

            _highlightRemoteFrameGate.StageResolvedThrough(confirmedLocalFrame);
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

            bool correctionCompleted = true;
            if (ShouldUseNetworkInputPath())
            {
                if (PresentationTargetResolver.ShouldProcessRollback(
                        _paused,
                        false) &&
                    _frameSyncCoordinator.TryGetEarliestMismatch(
                        out FrameInputLedger.InputMismatch mismatch))
                {
                    correctionCompleted = DoRollback(mismatch.Frame);
                }

                if (correctionCompleted)
                {
                    StageConfirmedLedgerFrameForHighlight();
                    _highlightRemoteFrameGate.CommitCorrection();
                }
            }

            bool stableConsumersCompleted = ProcessStableHighlightFrames();
            if (correctionCompleted && stableConsumersCompleted)
                TryPruneLedgerHistory();
            if (_matchPhase == MatchPhase.PostGameReplay)
            {
                ApplyPostGameReplayPresentation();
                UpdateControlOverlay();
                return;
            }

            if (!_paused)
            {
                bool canSubmitPresentation = AdvanceRealtimePresentation(
                    Time.deltaTime,
                    _frameEngine.FrameIntervalMs / 1000f);
                if (canSubmitPresentation)
                    SyncPresentationFromLogic(Time.deltaTime);
            }
            else
            {
                SyncPresentationFromLogic(0f);
            }
            UpdateControlOverlay();
        }

        private bool ProcessStableHighlightFrames()
        {
            if (_highlightStableCursor == null ||
                _highlightRecorder == null ||
                _frameSyncCoordinator == null ||
                _frameEngine == null)
            {
                return false;
            }

            bool isConnected = ShouldUseNetworkInputPath();
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
                if (!TryGetCanonicalFrame(frameID, out int canonicalFrame))
                {
                    _paused = true;
                    Debug.LogError(
                        $"[RouteC][LedgerFault] invalid stable local frame={frameID}");
                    return false;
                }

                if (!_frameSyncCoordinator.TryGetFrameSnapshot(
                    WorldTrack.Confirmed,
                    canonicalFrame,
                    out FrameSnapshot snapshot))
                {
                    if (TryRebaseExpiredHighlightHistory(frameID))
                        continue;

                    Debug.LogError(
                        $"[RouteC][Highlight] stable snapshot missing " +
                        $"localFrame={frameID} canonicalFrame={canonicalFrame}");
                    return false;
                }

                if (!TryGetStableActualInputs(canonicalFrame, out FrameInput[] inputs))
                {
                    if (TryRebaseExpiredHighlightHistory(frameID))
                        continue;

                    return false;
                }

                _highlightRecorder.ProcessStableFrame(
                    frameID,
                    WithFrameID(snapshot, frameID),
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
                    return false;
                }
            }

            return true;
        }

        private void TryPruneLedgerHistory()
        {
            if (_paused || _frameSyncCoordinator == null ||
                _highlightStableCursor == null ||
                _frameSyncCoordinator.HasIntegrityFault)
            {
                return;
            }

            int firstFrameToKeep = NextFrameOrMax(
                _frameSyncCoordinator.ConfirmedThroughFrame);
            if (_frameSyncCoordinator.TryGetInFlightReplayPlan(
                    out FrameInputLedger.ReplayInputPlan inFlightPlan))
            {
                firstFrameToKeep = System.Math.Min(
                    firstFrameToKeep,
                    inFlightPlan.FromFrame);
            }

            if (_frameSyncCoordinator.TryGetEarliestMismatch(
                    out FrameInputLedger.InputMismatch mismatch))
            {
                int mismatchFloor = GetMismatchRecoveryFloor(mismatch.Frame);
                firstFrameToKeep = System.Math.Min(firstFrameToKeep, mismatchFloor);
            }

            if (!TryGetCanonicalFrame(
                    NextFrameOrMax(_highlightStableCursor.LastProcessedFrame),
                    out int highlightFirstNeededCanonicalFrame))
            {
                _paused = true;
                Debug.LogError(
                    "[RouteC][LedgerFault] cannot map highlight cursor to canonical frame.");
                return;
            }

            firstFrameToKeep = System.Math.Min(
                firstFrameToKeep,
                highlightFirstNeededCanonicalFrame);
            if (firstFrameToKeep <= _frameSyncCoordinator.FirstRetainedFrame)
            {
                return;
            }

            FrameInputLedger.PruneResult result =
                _frameSyncCoordinator.TryPruneBefore(firstFrameToKeep);
            if (result != FrameInputLedger.PruneResult.Success &&
                result != FrameInputLedger.PruneResult.NoOp)
            {
                Debug.LogWarning(
                    $"[RouteC][LedgerPrune] floor={firstFrameToKeep} " +
                    $"retained={_frameSyncCoordinator.FirstRetainedFrame} result={result}");
            }
        }

        private int GetMismatchRecoveryFloor(int mismatchFrame)
        {
            if (mismatchFrame <= _frameSyncCoordinator.StartFrame)
            {
                return _frameSyncCoordinator.StartFrame;
            }

            return _frameSyncCoordinator.TryGetFrameSnapshot(
                WorldTrack.Confirmed,
                mismatchFrame - 1,
                out _)
                ? mismatchFrame
                : _frameSyncCoordinator.StartFrame;
        }

        private static int NextFrameOrMax(int frame)
        {
            return frame == int.MaxValue ? int.MaxValue : frame + 1;
        }

        private static FrameSnapshot WithFrameID(FrameSnapshot snapshot, int frameID)
        {
            snapshot.frameID = frameID;
            return snapshot;
        }

        private bool TryRebaseExpiredHighlightHistory(int missingFrameID)
        {
            int firstRetainedLocalFrame = System.Math.Max(
                0,
                _frameEngine.Buffer.MaxWrittenFrame - FrameBuffer.CAPACITY + 1);
            if (_frameSyncCoordinator != null)
            {
                if (!TryGetLocalFrame(
                        _frameSyncCoordinator.FirstRetainedFrame,
                        out int ledgerFirstRetainedLocalFrame))
                {
                    _paused = true;
                    Debug.LogError(
                        "[RouteC][LedgerFault] cannot map ledger retention floor to local frame.");
                    return false;
                }

                firstRetainedLocalFrame = System.Math.Max(
                    firstRetainedLocalFrame,
                    ledgerFirstRetainedLocalFrame);
            }

            if (firstRetainedLocalFrame <= missingFrameID)
                return false;

            _highlightStableCursor.RebaseAt(firstRetainedLocalFrame);
            _highlightRecorder.RebaseAt(firstRetainedLocalFrame);
            Debug.LogError(
                $"[RouteC][Highlight] stable history expired at " +
                $"localFrame={missingFrameID}; resumed at localFrame={firstRetainedLocalFrame}");
            return true;
        }

        private bool TryGetStableActualInputs(
            int canonicalFrame,
            out FrameInput[] inputs)
        {
            inputs = null;
            if (_frameSyncCoordinator == null ||
                !_frameSyncCoordinator.TryGetActualFrame(
                    canonicalFrame,
                    out FrameInputLedger.ResolvedFrame actual))
            {
                inputs = null;
                return false;
            }

            inputs = new[] { actual.GetPlayer(0).Value, actual.GetPlayer(1).Value };
            return true;
        }

        private void EnterPostGameReplay()
        {
            if (_matchPhase == MatchPhase.PostGameReplay)
                return;

            int recordedTerminalFrame =
                _highlightRecorder?.PostGameTerminalFrame ?? -1;
            if (!TryGetCanonicalFrame(
                    recordedTerminalFrame,
                    out int canonicalTerminalFrame))
            {
                Debug.LogError(
                    $"[RouteC][Highlight] invalid terminal frame " +
                    $"frame={recordedTerminalFrame}");
                return;
            }

            if (!PostGameTransitionSystem.TryRestoreTerminalWorld(
                _highlightRecorder,
                _frameSyncCoordinator,
                canonicalTerminalFrame,
                out int restoredCanonicalFrame) ||
                !TryGetLocalFrame(
                    restoredCanonicalFrame,
                    out int terminalFrame))
            {
                Debug.LogError(
                    $"[RouteC][Highlight] terminal snapshot missing " +
                    $"frame={recordedTerminalFrame}; postgame transition cancelled");
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

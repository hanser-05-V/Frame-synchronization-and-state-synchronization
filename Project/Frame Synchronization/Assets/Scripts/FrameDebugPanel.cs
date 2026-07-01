using UnityEngine;
using UnityEngine.UI;

namespace FrameSyncDemo
{
    /// <summary>
    /// Game View 悬浮面板 — 可折叠抽屉式
    /// 自动查找子组件，无需手动拖拽引用
    /// 默认折叠成一行，点击展开显示完整数据
    /// </summary>
    public class FrameDebugPanel : MonoBehaviour
    {
        // 自动查找的引用
        private GameObject _collapsedBar;
        private Text _collapsedFrameText;
        private Image _statusLight;
        private Text _collapsedStatusText;

        private GameObject _expandedPanel;
        private Text _expandedFrameText;
        private Text _p1InputText;
        private Text _p2InputText;
        private Text _p1PosText;
        private Text _p2PosText;
        private Text _bufferText;
        private Text _infoText;
        private Text _recordingText;

        private bool _isExpanded = false;
        private FrameDebugger _debugger;
        private const int UPDATE_INTERVAL = 5;
        private int _updateCounter = 0;

        private void Start()
        {
            // 自动查找子组件（按名称匹配）
            _collapsedBar = transform.Find("Panel_CollapsedBar")?.gameObject;
            if (_collapsedBar != null)
            {
                _collapsedFrameText = _collapsedBar.transform.Find("Text_CollapsedFrame")?.GetComponent<Text>();
                _statusLight = _collapsedBar.transform.Find("Image_StatusLight")?.GetComponent<Image>();
                _collapsedStatusText = _collapsedBar.transform.Find("Text_CollapsedStatus")?.GetComponent<Text>();
                // 添加点击事件
                var btn = _collapsedBar.GetComponent<Button>();
                if (btn == null) btn = _collapsedBar.AddComponent<Button>();
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(TogglePanel);
            }

            _expandedPanel = transform.Find("Panel_Expanded")?.gameObject;
            if (_expandedPanel != null)
            {
                _expandedFrameText = _expandedPanel.transform.Find("Text_ExpandedFrame")?.GetComponent<Text>();
                _p1InputText = _expandedPanel.transform.Find("Text_P1Input")?.GetComponent<Text>();
                _p2InputText = _expandedPanel.transform.Find("Text_P2Input")?.GetComponent<Text>();
                _p1PosText = _expandedPanel.transform.Find("Text_P1Pos")?.GetComponent<Text>();
                _p2PosText = _expandedPanel.transform.Find("Text_P2Pos")?.GetComponent<Text>();
                _bufferText = _expandedPanel.transform.Find("Text_Buffer")?.GetComponent<Text>();
                _infoText = _expandedPanel.transform.Find("Text_Info")?.GetComponent<Text>();
                _recordingText = _expandedPanel.transform.Find("Text_Recording")?.GetComponent<Text>();
                _expandedPanel.SetActive(false);
            }

            _debugger = FrameDebugger.Instance;
        }

        private void Update()
        {
            if (_debugger == null) return;

            _updateCounter++;
            if (_updateCounter % UPDATE_INTERVAL != 0) return;

            // 折叠条
            if (_collapsedFrameText != null)
                _collapsedFrameText.text = $"帧#{_debugger.currentFrameID}";
            if (_statusLight != null)
                _statusLight.color = _debugger.isRunning ? Color.green : Color.yellow;

            string status = _debugger.isRunning ? "●运行" : "⏸暂停";
            if (_debugger.isRecording) status += " 🔴录制";
            if (_debugger.isPlayingBack) status += " ▶回放";
            if (_collapsedStatusText != null)
                _collapsedStatusText.text = status;

            if (!_isExpanded) return;

            // 展开面板
            if (_expandedFrameText != null)
                _expandedFrameText.text = $"帧#{_debugger.currentFrameID} ({_debugger.targetFPS}FPS)";
            if (_infoText != null)
                _infoText.text = $"运行:{_debugger.elapsedTime:F1}s 追:{_debugger.catchupCount}";
            if (_recordingText != null)
            {
                if (_debugger.isRecording)
                    _recordingText.text = $"🔴录制中(帧0→{_debugger.recordedFrameCount})";
                else if (_debugger.isPlayingBack)
                    _recordingText.text = $"▶回放({_debugger.playbackFrame}/{_debugger.recordedFrameCount})";
                else
                    _recordingText.text = "待机";
            }

            if (_debugger.playerData != null && _debugger.playerData.Length > 0)
            {
                if (_debugger.playerData.Length > 0 && _p1InputText != null)
                {
                    var p1 = _debugger.playerData[0];
                    _p1InputText.text = $"P1: dir:{p1.input.moveDir} btn:{p1.input.buttons} raw:0x{p1.input._raw:X8}";
                    if (_p1PosText != null)
                        _p1PosText.text = $"({p1.position.x:F2},{p1.position.z:F2})";
                }
                if (_debugger.playerData.Length > 1 && _p2InputText != null)
                {
                    var p2 = _debugger.playerData[1];
                    _p2InputText.text = $"P2: dir:{p2.input.moveDir} btn:{p2.input.buttons} raw:0x{p2.input._raw:X8}";
                    if (_p2PosText != null)
                        _p2PosText.text = $"({p2.position.x:F2},{p2.position.z:F2})";
                }
            }

            var snap = _debugger.bufferSnapshot;
            if (snap.slotStates != null && _bufferText != null)
            {
                int used = System.Math.Max(0, snap.maxWritten - snap.lastRead);
                _bufferText.text = $"缓冲:{used}/{snap.capacity}帧";
            }
        }

        public void TogglePanel()
        {
            _isExpanded = !_isExpanded;
            if (_expandedPanel != null) _expandedPanel.SetActive(_isExpanded);
        }
    }
}

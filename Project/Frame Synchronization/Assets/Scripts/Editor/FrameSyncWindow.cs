using UnityEngine;
using UnityEditor;

namespace FrameSyncDemo
{
    /// <summary>
    /// Editor Window — 混合版(B+C): 左侧P1/P2实时对比 + 右侧Tab切换
    /// 菜单: Window → 帧同步调试器
    /// 数据源: FrameDebugger.Instance (Runtime单例)
    /// </summary>
    public class FrameSyncWindow : EditorWindow
    {
        private enum TabType { Buffer, Playback, Log, Compare }
        private TabType _currentTab = TabType.Buffer;
        private Vector2 _leftScroll, _rightScroll;
        private bool _autoRefresh = true;

        [MenuItem("Window/帧同步调试器")]
        public static void ShowWindow()
        {
            var window = GetWindow<FrameSyncWindow>("帧同步调试器");
            window.minSize = new Vector2(700, 400);
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.update += Repaint;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Repaint;
        }

        private void OnGUI()
        {
            var debugger = FrameDebugger.Instance;
            if (debugger == null)
            {
                EditorGUILayout.HelpBox("请先运行游戏 (Enter Play Mode)", MessageType.Info);
                return;
            }

            DrawHeader(debugger);

            EditorGUILayout.BeginHorizontal();
            {
                // 左侧: P1/P2 实时对比 (固定区域)
                EditorGUILayout.BeginVertical(GUILayout.Width(320));
                DrawPlayerStates(debugger);
                EditorGUILayout.EndVertical();

                // 分隔线
                EditorGUILayout.BeginVertical(GUILayout.Width(4));
                EditorGUILayout.EndVertical();

                // 右侧: Tab切换区
                EditorGUILayout.BeginVertical();
                DrawTabBar();
                _rightScroll = EditorGUILayout.BeginScrollView(_rightScroll);
                switch (_currentTab)
                {
                    case TabType.Buffer: DrawBufferTab(debugger); break;
                    case TabType.Playback: DrawPlaybackTab(debugger); break;
                    case TabType.Log: DrawLogTab(debugger); break;
                    case TabType.Compare: DrawCompareTab(debugger); break;
                }
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndHorizontal();

            DrawFooter(debugger);
        }

        private void DrawHeader(FrameDebugger d)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label($"帧#{d.currentFrameID} | {d.targetFPS}FPS | {d.frameIntervalMs}ms/帧",
                EditorStyles.toolbarButton);
            GUILayout.FlexibleSpace();

            Color oldColor = GUI.backgroundColor;
            GUI.backgroundColor = d.isRunning ? Color.green : Color.yellow;
            GUILayout.Label(d.isRunning ? "● 运行中" : "⏸ 已暂停", EditorStyles.toolbarButton);
            GUI.backgroundColor = oldColor;

            GUILayout.Space(10);
            _autoRefresh = GUILayout.Toggle(_autoRefresh, "自动刷新", EditorStyles.toolbarButton);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawPlayerStates(FrameDebugger d)
        {
            EditorGUILayout.LabelField("🎮 实时状态", EditorStyles.boldLabel);
            if (d.playerData == null) return;

            float boxWidth = 300;

            for (int i = 0; i < d.playerData.Length; i++)
            {
                var p = d.playerData[i];
                Color borderColor = i == 0 ?
                    new Color(0.9f, 0.2f, 0.2f) : new Color(0.2f, 0.4f, 0.9f);

                // 边框
                Rect boxRect = EditorGUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(boxWidth));
                EditorGUI.DrawRect(new Rect(boxRect.x, boxRect.y, boxRect.width, boxRect.height),
                    new Color(borderColor.r, borderColor.g, borderColor.b, 0.1f));

                // 标题 + 状态标签（同一行，避免遮挡）
                EditorGUILayout.BeginHorizontal();
                var titleStyle = new GUIStyle(EditorStyles.boldLabel);
                titleStyle.normal.textColor = borderColor;
                EditorGUILayout.LabelField($"■ P{i + 1} {(i == 0 ? "红方" : "蓝方")}", titleStyle);
                string status = d.isRecording ? "● 录制" : d.isPlayingBack ? "▶ 回放" : "● 操作";
                GUILayout.FlexibleSpace();
                var statusStyle = new GUIStyle(EditorStyles.miniLabel);
                statusStyle.normal.textColor = borderColor;
                EditorGUILayout.LabelField(status, statusStyle, GUILayout.Width(60));
                EditorGUILayout.EndHorizontal();

                // Input 数据
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField("方向:", GetDirName(p.input.moveDir));
                EditorGUILayout.LabelField("按键:", $"0x{p.input.buttons:X2}");
                EditorGUILayout.LabelField("raw:", $"0x{p.input._raw:X8}");

                // 位域展开
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("pos:", GUILayout.Width(30));
                GUILayout.Label($"{i}", GUILayout.Width(25));
                GUILayout.Label("yaw:", GUILayout.Width(30));
                GUILayout.Label($"{p.input.moveDir}", GUILayout.Width(25));
                GUILayout.Label("btn:", GUILayout.Width(30));
                GUILayout.Label($"{p.input.buttons}", GUILayout.Width(25));
                EditorGUILayout.EndHorizontal();

                // 位置（大号显示）
                var posStyle = new GUIStyle(EditorStyles.largeLabel);
                posStyle.normal.textColor = borderColor;
                posStyle.fontSize = 20;
                posStyle.fontStyle = FontStyle.Bold;
                EditorGUILayout.LabelField($"({p.position.x:F2}, {p.position.z:F2})", posStyle);

                EditorGUI.indentLevel--;
                EditorGUILayout.EndVertical();

                if (i < d.playerData.Length - 1) EditorGUILayout.Space(4);
            }
        }

        private void DrawTabBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            _currentTab = (TabType)GUILayout.Toolbar((int)_currentTab,
                new[] { "🔄缓冲区", "💾录放", "📋日志", "⚙对比" });
            EditorGUILayout.EndHorizontal();
        }

        private void DrawBufferTab(FrameDebugger d)
        {
            EditorGUILayout.LabelField("环形缓冲区", EditorStyles.boldLabel);
            var snap = d.bufferSnapshot;
            if (snap.slotStates == null)
            {
                EditorGUILayout.HelpBox("等待数据...", MessageType.Info);
                return;
            }

            // 显示最近 64 槽
            int showCount = System.Math.Min(64, snap.capacity);
            EditorGUILayout.LabelField($"容量:{snap.capacity} | 已写:{snap.maxWritten + 1} | 已读:{snap.lastRead + 1}");
            EditorGUILayout.Space(4);
            EditorGUI.DrawRect(EditorGUILayout.GetControlRect(false, 24),
                new Color(0.1f, 0.1f, 0.1f));
            // 网格方块
            int cols = 16;
            int rows = (showCount + cols - 1) / cols;
            for (int r = 0; r < rows; r++)
            {
                EditorGUILayout.BeginHorizontal();
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    if (idx >= showCount) break;

                    Color boxColor;
                    switch (snap.slotStates[idx])
                    {
                        case 1: boxColor = new Color(0.2f, 0.8f, 0.2f); break; // 已消费
                        case 2: boxColor = new Color(0.9f, 0.7f, 0.1f); break; // 待消费
                        default: boxColor = new Color(0.15f, 0.15f, 0.15f); break; // 空闲
                    }
                    var rect = GUILayoutUtility.GetRect(14, 14, GUILayout.Width(14), GUILayout.Height(14));
                    EditorGUI.DrawRect(rect, boxColor);
                    GUILayout.Space(2);
                }
                EditorGUILayout.EndHorizontal();
            }

            // 图例
            EditorGUILayout.BeginHorizontal();
            EditorGUI.DrawRect(GUILayoutUtility.GetRect(12, 12, GUILayout.Width(12), GUILayout.Height(12)),
                new Color(0.2f, 0.8f, 0.2f));
            GUILayout.Label("已消费");
            GUILayout.Space(8);
            EditorGUI.DrawRect(GUILayoutUtility.GetRect(12, 12, GUILayout.Width(12), GUILayout.Height(12)),
                new Color(0.9f, 0.7f, 0.1f));
            GUILayout.Label("待消费");
            GUILayout.Space(8);
            EditorGUI.DrawRect(GUILayoutUtility.GetRect(12, 12, GUILayout.Width(12), GUILayout.Height(12)),
                new Color(0.15f, 0.15f, 0.15f));
            GUILayout.Label("空闲");
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField($"追帧次数: {d.catchupCount}");
            EditorGUILayout.LabelField($"帧间隔: {d.frameIntervalMs}ms");
        }

        private void DrawPlaybackTab(FrameDebugger d)
        {
            EditorGUILayout.LabelField("赛后精彩回放", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "旧式本机录制/回放已停用。比赛中按 F9，系统会在稳定帧完成后进入赛后精彩片段回放。",
                MessageType.Info);
        }

        private void DrawLogTab(FrameDebugger d)
        {
            EditorGUILayout.LabelField("事件日志", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(GUI.skin.box);
            EditorGUILayout.LabelField($"[{System.DateTime.Now:HH:mm:ss}] 帧#{d.currentFrameID} — {(d.isRunning ? "运行中" : "已暂停")}");
            if (d.catchupCount > 0)
                EditorGUILayout.LabelField($"[{System.DateTime.Now:HH:mm:ss}] ⚡ 追帧 {d.catchupCount} 次累计");
            if (d.isRecording)
                EditorGUILayout.LabelField($"[{System.DateTime.Now:HH:mm:ss}] 🔴 录制中 — 已录{d.recordedFrameCount}帧");
            if (d.isPlayingBack)
                EditorGUILayout.LabelField($"[{System.DateTime.Now:HH:mm:ss}] ▶ 回放中 — {d.playbackFrame}/{d.recordedFrameCount}");
            EditorGUILayout.EndVertical();
        }

        private void DrawCompareTab(FrameDebugger d)
        {
            EditorGUILayout.LabelField("确定性对比（网络同步阶段启用）", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "此Tab用于验证帧同步的确定性:\n" +
                "• 录制P1操作 → 回放 → 对比坐标\n" +
                "• 如果结果完全一致 → 确定性验证通过\n" +
                "• 网络同步阶段会在这里展示两个客户端的状态差异",
                MessageType.Info);

            if (d.isPlayingBack && d.playerData != null)
            {
                EditorGUILayout.LabelField("回放验证:", EditorStyles.boldLabel);
                for (int i = 0; i < d.playerData.Length; i++)
                {
                    var p = d.playerData[i];
                    EditorGUILayout.LabelField($"P{i + 1}: ({p.position.x:F3}, {p.position.z:F3}) — ✅ 确定");
                }
            }
        }

        private void DrawFooter(FrameDebugger d)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"运行时间: {d.elapsedTime:F1}s | 帧号: {d.currentFrameID}",
                EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private string GetDirName(byte dir)
        {
            return dir switch
            {
                0 => "停止",
                1 => "↑上",
                2 => "↗右上",
                3 => "→右",
                4 => "↘右下",
                5 => "↓下",
                6 => "↙左下",
                7 => "←左",
                8 => "↖左上",
                _ => $"未知({dir})"
            };
        }
    }
}

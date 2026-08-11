using System.Text;
using UnityEngine;

namespace FrameSyncDemo
{
    public sealed class RuntimeControlOverlay : MonoBehaviour
    {
        public MatchPhase Phase { get; set; }
        public bool WaitingForHighlightTail { get; set; }
        public bool IsPreparingShot { get; set; }
        public BallEntity.EState BallState { get; set; }
        public int HolderPlayerIndex { get; set; } = -1;
        public int ClipIndex { get; set; }
        public int ClipCount { get; set; }
        public HighlightClip CurrentClip { get; set; }
        public bool ReplayPlaying { get; set; }

        private GUIStyle _boxStyle;
        private GUIStyle _labelStyle;

        public static string BuildText(
            MatchPhase phase,
            bool waitingForHighlightTail,
            bool isPreparingShot,
            BallEntity.EState ballState,
            int holderPlayerIndex,
            int clipIndex,
            int clipCount,
            HighlightClip currentClip,
            bool replayPlaying)
        {
            var text = new StringBuilder();
            if (phase == MatchPhase.Playing)
            {
                text.AppendLine($"\u7bee\u7403\uff1a{ballState}");
                text.AppendLine(holderPlayerIndex >= 0
                    ? $"\u6301\u7403\u8005\uff1aP{holderPlayerIndex + 1}"
                    : "\u6301\u7403\u8005\uff1a\u65e0\uff08\u81ea\u7531\u7403\uff09");
                if (isPreparingShot)
                    text.AppendLine("\u72b6\u6001\uff1a\u51c6\u5907\u6295\u7bee\uff0c\u677e\u5f00\u51fa\u624b");
                text.AppendLine("操作说明（比赛中）");
                text.AppendLine("WASD：移动本机球员");
                text.AppendLine("E：靠近自由球时拾取");
                text.AppendLine("Space（松开）：持球时投篮");
                text.AppendLine("F9：结束比赛并进入精彩回放");
                text.Append("Esc：退出");
                if (waitingForHighlightTail)
                    text.Append("\n状态：正在收尾精彩片段");
                return text.ToString();
            }

            text.AppendLine("精彩回放");
            if (currentClip == null || clipCount <= 0)
            {
                text.AppendLine("本场暂无精彩片段");
            }
            else
            {
                text.AppendLine(
                    $"片段：{clipIndex + 1}/{clipCount}  " +
                    $"原始帧：{currentClip.StartFrame}-{currentClip.EndFrame}");
                text.AppendLine($"状态：{(replayPlaying ? "播放中" : "已暂停")}");
            }

            text.AppendLine("P：播放/暂停");
            text.AppendLine("[ / ]：上一段/下一段");
            text.AppendLine("R：重播当前片段");
            text.Append("Esc：退出");
            return text.ToString();
        }

        public static bool ShouldShowShotPreparation(
            MatchPhase phase,
            bool paused,
            bool spaceHeld,
            bool localHasBall)
        {
            return phase == MatchPhase.Playing &&
                !paused &&
                spaceHeld &&
                localHasBall;
        }

        private void OnGUI()
        {
            EnsureStyles();
            string text = BuildText(
                Phase,
                WaitingForHighlightTail,
                IsPreparingShot,
                BallState,
                HolderPlayerIndex,
                ClipIndex,
                ClipCount,
                CurrentClip,
                ReplayPlaying);
            int lineCount = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                    lineCount++;
            }

            float height = 26f + lineCount * 22f;
            var panelRect = new Rect(16f, 16f, 360f, height);
            Color previousColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.72f);
            GUI.Box(panelRect, GUIContent.none, _boxStyle);
            GUI.color = previousColor;
            GUI.Label(
                new Rect(30f, 26f, 332f, height - 16f),
                text,
                _labelStyle);
        }

        private void EnsureStyles()
        {
            if (_boxStyle != null)
                return;

            _boxStyle = new GUIStyle(GUI.skin.box);
            _boxStyle.normal.background = Texture2D.whiteTexture;
            _boxStyle.normal.textColor = Color.white;
            _boxStyle.padding = new RectOffset(12, 12, 10, 10);

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                normal = { textColor = Color.white },
                wordWrap = true
            };
        }
    }
}

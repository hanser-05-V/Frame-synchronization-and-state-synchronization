using System.Collections.Generic;
using UnityEngine;

namespace FrameSyncDemo
{
    /// <summary>
    /// 帧调试数据层 — 统一数据源，供 EditorWindow 和 GameDebugPanel 两个视图读取
    /// Runtime 单例，每帧由 FrameEngine 更新，不依赖 Editor
    /// </summary>
    public class FrameDebugger : MonoBehaviour
    {
        // ----- 当前帧数据 -----
        public int currentFrameID;
        public float elapsedTime;
        public int catchupCount;
        public bool isRunning;
        public int targetFPS;
        public int frameIntervalMs;

        // ----- 玩家状态 -----
        public int playerCount;
        public PlayerFrameData[] playerData; // 每玩家一帧数据

        // ----- 缓冲区 -----
        public BufferSnapshot bufferSnapshot;

        // ----- 录制回放 -----
        public bool isRecording = false;
        public bool isPlayingBack = false;
        public List<uint> recordedInputs = new List<uint>();       // 所有玩家的Input序列
        public int playbackFrame = 0;
        public int recordedFrameCount = 0;
        public int recordedPlayerCount = 0;
        public Vector3[] recordStartPositions;  // 录制开始时的方块位置
     

        // ----- 事件（GUI线程安全）-----
        public System.Action OnDataUpdated;

        public static FrameDebugger Instance { get; private set; }

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        /// <summary>每帧由 FrameEngine 调用</summary>
        public void UpdateFrame(int frameID, FrameInput[] inputs, BufferSnapshot snapshot,
            int catchup, float time, bool running)
        {
            currentFrameID = frameID;
            elapsedTime = time;
            catchupCount = catchup;
            isRunning = running;
            bufferSnapshot = snapshot;

            // 更新玩家数据
            int count = inputs?.Length ?? 0;
            if (playerData == null || playerData.Length != count)
            {
                playerData = new PlayerFrameData[count];
                for (int i = 0; i < count; i++)
                    playerData[i] = new PlayerFrameData();
            }

            for (int i = 0; i < count; i++)
            {
                playerData[i].input = inputs[i];
                playerData[i].playerIndex = i;
            }

            // 录制
            if (isRecording && !isPlayingBack)
            {
                recordedPlayerCount = count;
                recordedFrameCount++;
                for (int i = 0; i < count; i++)
                {
                    recordedInputs.Add(inputs[i]._raw);
                }
            }

            // 回放
            if (isPlayingBack)
            {
                playbackFrame++;
                if (playbackFrame >= recordedFrameCount)
                {
                    StopPlayback();
                }
            }

            OnDataUpdated?.Invoke();
        }

        /// <summary>更新玩家渲染数据（GameController 调）</summary>
        public void UpdatePlayerRender(int index, Vector3 position, Color color)
        {
            if (playerData == null || index >= playerData.Length) return;
            playerData[index].position = position;
            playerData[index].color = color;
        }

        // ----- 录制控制 -----
    public void StartRecording()
    {
        isRecording = true;
        isPlayingBack = false;
        recordedInputs.Clear();
        recordedFrameCount = 0;
        playbackFrame = 0;
        Debug.Log(
            "[FrameDebugger] Legacy recording no longer resets the live frame timeline.");
    }

        public void StopRecording()
        {
            isRecording = false;
            Debug.Log($"[FrameDebugger] 停止录制 — 共{recordedFrameCount}帧, {recordedInputs.Count}个Input");
        }

        public List<uint> GetRecordedFrame(uint expectedPlayerCount)
        {
            var list = new List<uint>();
            int frameCount = recordedInputs.Count / (int)expectedPlayerCount;
            for (int f = 0; f < frameCount; f++)
            {
                int idx = f * (int)expectedPlayerCount;
                // 把所有玩家的Input合并成一个标记
                uint sum = 0;
                for (int p = 0; p < expectedPlayerCount && (idx + p) < recordedInputs.Count; p++)
                    sum ^= recordedInputs[idx + p];
                list.Add(sum);
            }
            return list;
        }

        public void StartPlayback()
        {
            if (recordedInputs.Count == 0)
            {
                Debug.LogWarning("[FrameDebugger] 无录制数据");
                return;
            }
            isPlayingBack = true;
            isRecording = false;
            playbackFrame = 0;
            Debug.LogWarning(
                "[FrameDebugger] Legacy playback is detached from the live world. " +
                "Use F9 postgame highlights instead.");
        }

        public void StopPlayback()
        {
            isPlayingBack = false;
            Debug.Log($"[FrameDebugger] 回放结束");
        }

        public void ClearRecording()
        {
            isRecording = false;
            isPlayingBack = false;
            recordedInputs.Clear();
            recordedFrameCount = 0;
            playbackFrame = 0;
            Debug.Log("[FrameDebugger] 录制已清空");
        }
    }

    /// <summary>单个玩家的帧数据</summary>
    [System.Serializable]
    public class PlayerFrameData
    {
        public int playerIndex;
        public FrameInput input;
        public Vector3 position;
        public Color color = Color.white;
    }
}

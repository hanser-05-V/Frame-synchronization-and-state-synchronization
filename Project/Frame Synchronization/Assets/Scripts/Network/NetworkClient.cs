using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace FrameSyncDemo
{
    public class NetworkClient : MonoBehaviour
    {
        [Header("连接配置")]
        [SerializeField] private string _serverIP = "127.0.0.1";
        [SerializeField] private int _serverPort = 8888;

        [Header("运行时状态")]
        [SerializeField] private bool _isConnected = false;
        [SerializeField] private int _localPlayerIndex = -1;

        private TcpClient _tcp;
        private NetworkStream _stream;
        private Thread _recvThread;
        private volatile bool _running = false;

        /// <summary>FIFO 远程输入队列（值+帧号配对）</summary>
        private ConcurrentQueue<RemotePacket> _remoteInputs = new ConcurrentQueue<RemotePacket>();

        /// <summary>帧号索引字典 — 按 remoteFrameID 直接取值（帧对齐方案核心）</summary>
        private Dictionary<int, uint> _remoteInputDict = new Dictionary<int, uint>();

        private struct RemotePacket
        {
            public uint raw;
            public int remoteFrameID;
        }

        public bool IsConnected => _isConnected;
        public int LocalPlayerIndex => _localPlayerIndex;
        public int RemotePlayerIndex => _localPlayerIndex == 0 ? 1 : 0;

        public void Connect(string ip, int port)
        {
            _serverIP = ip;
            _serverPort = port;

            try
            {
                _tcp = new TcpClient();
                _tcp.Connect(_serverIP, _serverPort);
                _stream = _tcp.GetStream();

                int playerIdx = _stream.ReadByte();
                if (playerIdx < 0) { Debug.LogError("[NetworkClient] 服务器断开"); return; }
                _localPlayerIndex = playerIdx;

                _isConnected = true;
                _running = true;

                _remoteInputDict = new Dictionary<int, uint>();

                _recvThread = new Thread(RecvLoop);
                _recvThread.IsBackground = true;
                _recvThread.Start();

                Debug.Log("[NetworkClient] 已连接 我是 Player" + _localPlayerIndex + " (对方=Player" + RemotePlayerIndex + ")");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[NetworkClient] 连接失败(服务器未启动?): " + e.Message);
                _isConnected = false;
            }
        }

        public void SendInput(uint raw) { SendInput(raw, -1); }

        public void SendInput(uint raw, int localFrameID)
        {
            if (!_isConnected || _stream == null) return;
            try
            {
                byte[] data = new byte[8];
                System.BitConverter.GetBytes(raw).CopyTo(data, 0);
                System.BitConverter.GetBytes(localFrameID).CopyTo(data, 4);
                _stream.Write(data, 0, 8);
            }
            catch (System.Exception e)
            {
                Debug.LogError("[NetworkClient] 发送失败: " + e.Message);
                _isConnected = false;
            }
        }

        /// <summary>取远程输入（FIFO），附带远程帧号</summary>
        public bool TryGetRemoteInput(out uint raw, out int remoteFrameID)
        {
            if (_remoteInputs.TryDequeue(out var pkt))
            {
                raw = pkt.raw;
                remoteFrameID = pkt.remoteFrameID;
                return true;
            }
            raw = 0;
            remoteFrameID = -1;
            return false;
        }

        /// <summary>仅取 raw（无帧号，兼容旧调用）</summary>
        public bool TryGetRemoteInput(out uint raw)
        {
            if (_remoteInputs.TryDequeue(out var pkt))
            {
                raw = pkt.raw;
                return true;
            }
            raw = 0;
            return false;
        }

        /// <summary>把 FIFO 中的积压包全部出队写入 dict，供帧对齐查询</summary>
        public void DrainQueueToDict()
        {
            while (_remoteInputs.TryDequeue(out var pkt))
            {
                if (pkt.remoteFrameID < 0) continue;
                _remoteInputDict[pkt.remoteFrameID] = pkt.raw;
            }
        }

        /// <summary>按 remoteFrameID 从 dict 取值。命中返回 true，不命中返回 false（取完不移除）</summary>
        public bool TryGetRemoteInputAt(int remoteFrameID, out uint raw)
        {
            return _remoteInputDict.TryGetValue(remoteFrameID, out raw);
        }

        /// <summary>清理 dict 中小于 minKeepFrameID 的键，防止无限增长（主线程 Update 末尾调用）</summary>
        public void CleanupRemoteInputs(int minKeepFrameID)
        {
            if (_remoteInputDict.Count == 0) return;
            var toRemove = new List<int>();
            foreach (var kv in _remoteInputDict)
                if (kv.Key < minKeepFrameID)
                    toRemove.Add(kv.Key);
            foreach (var k in toRemove)
                _remoteInputDict.Remove(k);
        }

        /// <summary>获取 dict 中最小的 remoteFrameID（用于首包锚定偏移计算），dict 为空返回 -1</summary>
        public int GetMinRemoteFrameID()
        {
            if (_remoteInputDict.Count == 0) return -1;
            int min = int.MaxValue;
            foreach (var key in _remoteInputDict.Keys)
                if (key < min) min = key;
            return min;
        }

        private void RecvLoop()
        {
            byte[] buffer = new byte[8];
            while (_running && _isConnected)
            {
                try
                {
                    int bytesRead = _stream.Read(buffer, 0, 8);
                    if (bytesRead <= 0) break;

                    uint raw = System.BitConverter.ToUInt32(buffer, 0);
                    int remoteFrameID = System.BitConverter.ToInt32(buffer, 4);

                    _remoteInputs.Enqueue(new RemotePacket { raw = raw, remoteFrameID = remoteFrameID });
                }
                catch (System.Exception)
                {
                    break;
                }
            }
            Debug.Log("[NetworkClient] 接收线程退出");
        }

        private void OnDestroy()
        {
            _running = false;
            _recvThread?.Join(1000);
            _stream?.Close();
            _tcp?.Close();
        }

        private void OnApplicationQuit() { _running = false; }
    }
}

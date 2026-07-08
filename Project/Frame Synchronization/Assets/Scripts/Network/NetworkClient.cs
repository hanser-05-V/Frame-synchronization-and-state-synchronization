using System.Collections.Concurrent;
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
        private ConcurrentQueue<uint> _remoteInputs = new ConcurrentQueue<uint>();

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

        public void SendInput(uint raw)
        {
            if (!_isConnected || _stream == null) return;
            try
            {
                byte[] data = System.BitConverter.GetBytes(raw);
                _stream.Write(data, 0, 4);
            }
            catch (System.Exception e)
            {
                Debug.LogError("[NetworkClient] 发送失败: " + e.Message);
                _isConnected = false;
            }
        }

        public bool TryGetRemoteInput(out uint raw)
        {
            return _remoteInputs.TryDequeue(out raw);
        }

        private void RecvLoop()
        {
            byte[] buffer = new byte[4];
            while (_running && _isConnected)
            {
                try
                {
                    int bytesRead = _stream.Read(buffer, 0, 4);
                    if (bytesRead <= 0) break;

                    uint raw = System.BitConverter.ToUInt32(buffer, 0);
                    _remoteInputs.Enqueue(raw);
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

        private void OnApplicationQuit()
        {
            _running = false;
        }
    }
}

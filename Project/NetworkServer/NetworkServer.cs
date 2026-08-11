using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace FrameSyncServer
{
    class Program
    {
        static List<TcpClient> _clients = new List<TcpClient>();
        static object _lock = new object();
        static int[] _confirmedFrames = new int[2];

        static void Main(string[] args)
        {
            Console.WriteLine("==================================");
            Console.WriteLine(" 帧同步转发服务器 (8字节协议)");
            Console.WriteLine("==================================");
            Console.WriteLine(" 选择模式：");
            Console.WriteLine("   0 = 正常模式 (无延迟)");
            Console.WriteLine("   1 = 延迟模式 (100ms)");
            Console.WriteLine("   2 = 严重延迟 (200ms)");
            Console.Write(">>> ");

            string choice = Console.ReadLine();
            int delayMs = 0;
            if (choice == "1") delayMs = 100;
            else if (choice == "2") delayMs = 200;

            int port = 8888;

            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            Console.WriteLine("");
            Console.WriteLine("[Server] 监听端口 " + port + " (延迟=" + delayMs + "ms)");
            Console.WriteLine("");

            for (int i = 0; i < 2; i++)
            {
                Console.WriteLine("[Server] 等待客户端 " + i + "...");
                var client = listener.AcceptTcpClient();
                ConfigureLowLatency(client);
                lock (_lock) _clients.Add(client);
                Console.WriteLine("[Server] 客户端 " + i + " 已连接，等待共同放行");
            }

            for (int i = 0; i < 2; i++)
            {
                var client = _clients[i];
                client.GetStream().WriteByte((byte)i);
                client.GetStream().Flush();
            }

            for (int i = 0; i < 2; i++)
            {
                var client = _clients[i];
                int idx = i;
                new Thread(() => RecvLoop(idx, client, delayMs)).Start();
            }

            Console.WriteLine("[Server] 2 客户端共同放行，从逻辑帧 0 开始转发");
            Console.WriteLine("按 Ctrl+C 退出");
            while (true) { Thread.Sleep(1000); }
        }

        private static void ConfigureLowLatency(TcpClient client)
        {
            if (client == null) throw new ArgumentNullException("client");
            client.NoDelay = true;
        }

        struct Packet
        {
            public uint raw;
            public int frameID;
            public int senderIdx;
        }

        static void RecvLoop(int clientIdx, TcpClient client, int delay)
        {
            byte[] buffer = new byte[8];
            var sendQueue = new Queue<Packet>();
            object queueLock = new object();
            bool running = true;

            // 发件线程：每100ms把队列里积压的包全部转发
            var sendThread = new Thread(() =>
            {
                while (running)
                {
                    Thread.Sleep(delay > 0 ? delay : 5);

                    List<Packet> batch = new List<Packet>();
                    lock (queueLock)
                    {
                        while (sendQueue.Count > 0)
                            batch.Add(sendQueue.Dequeue());
                    }

                    // 把积压的包一次性全发出去
                    foreach (var pkt in batch)
                        Broadcast(pkt.raw, pkt.senderIdx, pkt.frameID);
                }
            });
            sendThread.IsBackground = true;
            sendThread.Start();

            try
            {
                var stream = client.GetStream();
                while (true)
                {
                    if (!TryReadExactly(stream, buffer)) break;
                    uint raw = BitConverter.ToUInt32(buffer, 0);
                    int frameID = BitConverter.ToInt32(buffer, 4);
                    _confirmedFrames[clientIdx] = frameID;
                    lock (queueLock) { sendQueue.Enqueue(new Packet { raw = raw, frameID = frameID, senderIdx = clientIdx }); }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("[Client" + clientIdx + "] 断开: " + e.Message);
            }
            running = false;
        }

        static bool TryReadExactly(NetworkStream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int bytesRead = stream.Read(buffer, offset, buffer.Length - offset);
                if (bytesRead <= 0) return false;
                offset += bytesRead;
            }

            return true;
        }

        static void Broadcast(uint raw, int senderIdx, int frameID)
        {
            byte[] data = new byte[8];
            BitConverter.GetBytes(raw).CopyTo(data, 0);
            BitConverter.GetBytes(frameID).CopyTo(data, 4);
            lock (_lock)
            {
                foreach (var c in _clients)
                {
                    if (c == _clients[senderIdx]) continue;
                    try { c.GetStream().Write(data, 0, 8); }
                    catch { }
                }
            }
        }
    }
}

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

        static void Main(string[] args)
        {
            int port = args.Length > 0 ? int.Parse(args[0]) : 8888;

            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            Console.WriteLine("[Server] 监听端口 " + port + "...");

            // 等待 2 个客户端
            for (int i = 0; i < 2; i++)
            {
                Console.WriteLine("[Server] 等待客户端 " + i + "...");
                var client = listener.AcceptTcpClient();
                lock (_lock) _clients.Add(client);

                // 告诉客户端它的 playerIndex
                client.GetStream().WriteByte((byte)i);
                client.GetStream().Flush();

                Console.WriteLine("[Server] 客户端 " + i + " 已连接 — 分配 PlayerIndex=" + i);

                // 每个客户端起一条独立接收线程
                int idx = i;
                new Thread(() => RecvLoop(idx, client)).Start();
            }

            Console.WriteLine("[Server] 2 个客户端就绪，开始转发 Input");
            while (true) { Thread.Sleep(1000); }  // 主线程保持存活
        }

        static void RecvLoop(int clientIdx, TcpClient client)
        {
            byte[] buffer = new byte[4];
            uint lastRaw = 0;
            bool firstPacket = true;
            try
            {
                var stream = client.GetStream();
                while (true)
                {
                    int bytesRead = stream.Read(buffer, 0, 4);
                    if (bytesRead <= 0) break;

                    uint raw = BitConverter.ToUInt32(buffer, 0);

                    // 只在 Input 变化时打印（减少刷屏）
                    if (firstPacket || raw != lastRaw)
                    {
                        Console.WriteLine("[Server] 客户端" + clientIdx + " 发送 0x" + raw.ToString("X8"));
                        firstPacket = false;
                    }
                    lastRaw = raw;

                    // 广播给其他客户端（排除发送者）
                    Broadcast(raw, client);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("[Server] 客户端" + clientIdx + " 断开: " + e.Message);
            }
        }

        static void Broadcast(uint raw, TcpClient sender)
        {
            byte[] data = BitConverter.GetBytes(raw);
            lock (_lock)
            {
                foreach (var c in _clients)
                {
                    if (c == sender) continue;  // 不发给发送者
                    try { c.GetStream().Write(data, 0, 4); }
                    catch { /* 客户端已断开 */ }
                }
            }
        }
    }
}

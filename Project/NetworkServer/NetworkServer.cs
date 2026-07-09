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
            Console.WriteLine("==================================");
            Console.WriteLine(" 帧同步转发服务器");
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
                lock (_lock) _clients.Add(client);
                client.GetStream().WriteByte((byte)i);
                client.GetStream().Flush();
                Console.WriteLine("[Server] 客户端 " + i + " 已连接");
                int idx = i;
                new Thread(() => RecvLoop(idx, client, delayMs)).Start();
            }

            Console.WriteLine("[Server] 2 客户端就绪，开始转发");
            Console.WriteLine("按 Ctrl+C 退出");
            while (true) { Thread.Sleep(1000); }
        }

        static void RecvLoop(int clientIdx, TcpClient client, int delay)
        {
            byte[] buffer = new byte[4];
            var sendQueue = new Queue<uint>();
            object queueLock = new object();
            bool running = true;

            // 发件线程：每100ms把队列里积压的包全部转发
            var sendThread = new Thread(() =>
            {
                while (running)
                {
                    Thread.Sleep(delay > 0 ? delay : 5);

                    List<uint> batch = new List<uint>();
                    lock (queueLock)
                    {
                        while (sendQueue.Count > 0)
                            batch.Add(sendQueue.Dequeue());
                    }

                    // 把积压的包一次性全发出去——客户端收到后会while追帧，瞬移
                    foreach (var raw in batch)
                        Broadcast(raw, client);
                }
            });
            sendThread.IsBackground = true;
            sendThread.Start();

            try
            {
                var stream = client.GetStream();
                while (true)
                {
                    int bytesRead = stream.Read(buffer, 0, 4);
                    if (bytesRead <= 0) break;
                    uint raw = BitConverter.ToUInt32(buffer, 0);
                    lock (queueLock) { sendQueue.Enqueue(raw); }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("[Client" + clientIdx + "] 断开: " + e.Message);
            }
            running = false;
        }

        static void Broadcast(uint raw, TcpClient sender)
        {
            byte[] data = BitConverter.GetBytes(raw);
            lock (_lock)
            {
                foreach (var c in _clients)
                {
                    if (c == sender) continue;
                    try { c.GetStream().Write(data, 0, 4); }
                    catch { }
                }
            }
        }
    }
}

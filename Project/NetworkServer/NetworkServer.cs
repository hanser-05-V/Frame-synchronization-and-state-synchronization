using System;
using System.IO;
using System.Text;

namespace FrameSyncServer
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            ServerOptions options;
            try
            {
                options = ReadOptions(args);
            }
            catch (ArgumentException exception)
            {
                Console.Error.WriteLine("[Server] Invalid options: " + exception.Message);
                return 1;
            }

            if (options.Transport == FrameSyncDemo.NetworkTransportKind.KcpUdp)
            {
                using (var server = new KcpUdpRelayServer(
                    options.KcpIntervalMs))
                {
                    server.Run();
                    bool stopped = options.KcpStopFilePath == null
                        ? server.WaitForStop(-1)
                        : WaitForKcpStopFile(
                            server,
                            options.KcpStopFilePath);
                    if (options.KcpDiagnosticsOutputPath != null)
                    {
                        WriteKcpDiagnostics(
                            options.KcpDiagnosticsOutputPath,
                            server.Diagnostics.ToMeasurementJson());
                    }
                    if (server.Diagnostics.WorkerFault)
                    {
                        Console.Error.WriteLine(
                            "[Server] KCP UDP worker terminated unexpectedly.");
                        return 4;
                    }
                    if (!stopped)
                    {
                        Console.Error.WriteLine(
                            "[Server] KCP UDP worker did not stop cleanly.");
                        return 5;
                    }
                }
                return 0;
            }

            if (options.Transport == FrameSyncDemo.NetworkTransportKind.RawUdp)
            {
                using (var server = new RawUdpRelayServer(options.RawWindowSize))
                {
                    server.Run();
                    server.WaitForStop(-1);
                    if (server.Diagnostics.WorkerFault)
                    {
                        Console.Error.WriteLine(
                            "[Server] Raw UDP worker terminated unexpectedly.");
                        return 3;
                    }
                }
                return 0;
            }

            using (var server = new TcpRelayServer(options.LabOptions))
            {
                server.Run();
            }

            return 0;
        }

        private static bool WaitForKcpStopFile(
            KcpUdpRelayServer server,
            string stopFilePath)
        {
            while (!File.Exists(stopFilePath))
            {
                if (server.WaitForStop(10))
                {
                    return false;
                }
            }
            server.RequestStop();
            return server.WaitForStop(2000);
        }

        private static void WriteKcpDiagnostics(
            string outputPath,
            string json)
        {
            string fullPath = Path.GetFullPath(outputPath);
            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(fullPath, json, new UTF8Encoding(false));
        }

        private static ServerOptions ReadOptions(string[] args)
        {
            ServerOptions options = ServerOptions.Parse(args);
            if (!options.UseLegacyInteractive)
            {
                return options;
            }

            Console.WriteLine("==================================");
            Console.WriteLine(" Frame Sync Relay Server (8-byte protocol)");
            Console.WriteLine("==================================");
            Console.WriteLine(" Select mode:");
            Console.WriteLine("   0 = normal (0ms)");
            Console.WriteLine("   1 = fixed delay (100ms)");
            Console.WriteLine("   2 = fixed delay (200ms)");
            Console.Write(">>> ");
            return ServerOptions.FromInteractiveChoice(Console.ReadLine());
        }
    }
}

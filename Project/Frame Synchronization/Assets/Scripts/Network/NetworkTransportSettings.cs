using System;
using System.Net.Sockets;

namespace FrameSyncDemo
{
    public static class NetworkTransportSettings
    {
        public static void ConfigureLowLatency(TcpClient client)
        {
            if (client == null)
                throw new ArgumentNullException(nameof(client));

            client.NoDelay = true;
        }
    }
}

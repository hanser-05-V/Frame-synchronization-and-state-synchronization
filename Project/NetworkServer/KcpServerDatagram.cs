using System;
using System.Net;

namespace FrameSyncServer
{
    public sealed class KcpServerDatagram
    {
        private readonly byte[] _datagram;

        public KcpServerDatagram(IPEndPoint endpoint, byte[] datagram)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }
            if (datagram == null)
            {
                throw new ArgumentNullException(nameof(datagram));
            }

            Endpoint = new IPEndPoint(endpoint.Address, endpoint.Port);
            _datagram = (byte[])datagram.Clone();
        }

        public IPEndPoint Endpoint { get; }
        public byte[] Datagram => (byte[])_datagram.Clone();
    }
}

using System;
using System.IO;

namespace FrameSyncDemo
{
    /// <summary>
    /// TCP 是字节流；固定长度协议必须循环读取到完整包或明确遇到 EOF。
    /// </summary>
    public static class NetworkStreamReader
    {
        public static bool TryReadExactly(Stream stream, byte[] buffer, int count)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (count < 0 || count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            int offset = 0;
            while (offset < count)
            {
                int bytesRead = stream.Read(buffer, offset, count - offset);
                if (bytesRead <= 0)
                    return false;

                offset += bytesRead;
            }

            return true;
        }
    }
}

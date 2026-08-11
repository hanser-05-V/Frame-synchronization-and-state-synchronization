using System;
using System.IO;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class NetworkStreamReaderTests
    {
        [Test]
        public void TryReadExactly_PacketArrivesInThreeAndFiveBytes_ReturnsWholePacket()
        {
            byte[] expected = { 1, 2, 3, 4, 5, 6, 7, 8 };
            using (var stream = new ChunkedReadStream(expected, 3, 5))
            {
                var buffer = new byte[8];

                bool succeeded = NetworkStreamReader.TryReadExactly(
                    stream,
                    buffer,
                    buffer.Length);

                Assert.IsTrue(succeeded);
                CollectionAssert.AreEqual(expected, buffer);
            }
        }

        [Test]
        public void TryReadExactly_PacketArrivesInOneThreeAndFourBytes_ReturnsWholePacket()
        {
            AssertFragmentedPacketIsReadExactly(1, 3, 4);
        }

        [Test]
        public void TryReadExactly_PacketArrivesInSevenAndOneBytes_ReturnsWholePacket()
        {
            AssertFragmentedPacketIsReadExactly(7, 1);
        }

        [Test]
        public void TryReadExactly_StreamEndsMidPacket_ReturnsFalse()
        {
            using (var stream = new ChunkedReadStream(new byte[] { 1, 2, 3 }, 3))
            {
                var buffer = new byte[8];

                bool succeeded = NetworkStreamReader.TryReadExactly(
                    stream,
                    buffer,
                    buffer.Length);

                Assert.IsFalse(succeeded);
            }
        }

        private static void AssertFragmentedPacketIsReadExactly(params int[] chunks)
        {
            byte[] expected = { 1, 2, 3, 4, 5, 6, 7, 8 };
            using (var stream = new ChunkedReadStream(expected, chunks))
            {
                var buffer = new byte[8];

                Assert.IsTrue(NetworkStreamReader.TryReadExactly(
                    stream,
                    buffer,
                    buffer.Length));
                CollectionAssert.AreEqual(expected, buffer);
            }
        }

        private sealed class ChunkedReadStream : Stream
        {
            private readonly byte[] _data;
            private readonly int[] _chunks;
            private int _position;
            private int _chunkIndex;

            public ChunkedReadStream(byte[] data, params int[] chunks)
            {
                _data = data;
                _chunks = chunks;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position >= _data.Length)
                    return 0;

                int chunk = _chunkIndex < _chunks.Length
                    ? _chunks[_chunkIndex++]
                    : count;
                int length = Math.Min(Math.Min(chunk, count), _data.Length - _position);
                Array.Copy(_data, _position, buffer, offset, length);
                _position += length;
                return length;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _data.Length;
            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();
            public override void SetLength(long value) =>
                throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();
        }
    }
}

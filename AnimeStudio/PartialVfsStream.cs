using System;
using System.Collections.Generic;
using System.IO;

namespace AnimeStudio
{
    internal sealed class UnavailableVfsDataException(int blockIndex)
        : IOException($"Endfield VFS block {blockIndex} is unavailable because it could not be decrypted.")
    {
        public int BlockIndex { get; } = blockIndex;
    }

    internal sealed class PartialVfsStream(Stream stream, IReadOnlyList<PartialVfsStream.Range> unavailable) : Stream
    {
        internal readonly record struct Range(long Start, long End, int BlockIndex);

        public override bool CanRead => stream.CanRead;
        public override bool CanSeek => stream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => stream.Length;

        public override long Position
        {
            get => stream.Position;
            set => stream.Position = value;
        }

        internal void RequireAvailable(long offset, long length)
        {
            if (length == 0)
            {
                return;
            }

            var end = checked(offset + length);
            foreach (var range in unavailable)
            {
                if (range.Start >= end)
                {
                    break;
                }
                if (range.End > offset)
                {
                    throw new UnavailableVfsDataException(range.BlockIndex);
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, Math.Max(0, Length - Position));
            RequireAvailable(Position, count);
            return stream.Read(buffer[..count]);
        }

        public override int ReadByte()
        {
            RequireAvailable(Position, Position < Length ? 1 : 0);
            return stream.ReadByte();
        }

        public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);
        public override void Flush() => stream.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                stream.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

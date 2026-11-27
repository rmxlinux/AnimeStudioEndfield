#pragma warning disable CA2022 // these reads are expected to throw, the result is intentionally unused
using System;
using System.Collections.Generic;
using System.IO;

namespace AnimeStudio.Tests
{
    internal static class PartialStreamTests
    {
        public static void Register(TestRunner runner)
        {
            runner.Add("partial stream: reads outside unavailable ranges succeed", () =>
            {
                using var stream = Create(out var inner);
                var buffer = new byte[10];

                stream.Position = 0;
                Assert.Equal(10, stream.Read(buffer, 0, 10), "read length before the range");
                Assert.BytesEqual(inner.AsSpan(0, 10), buffer, "content before the range");

                stream.Position = 48;
                Assert.Equal(10, stream.Read(buffer, 0, 10), "read length after the range");
                Assert.BytesEqual(inner.AsSpan(48, 10), buffer, "content after the range");
            });

            runner.Add("partial stream: a read touching an unavailable range throws a descriptive error", () =>
            {
                using var stream = Create(out _);

                stream.Position = 31;
                var error = Assert.Throws<IOException>(() => stream.Read(new byte[2], 0, 2),
                    "a read crossing into the range must fail");
                Assert.Contains(error.Message, "block 3", "error should name the block");

                stream.Position = 32;
                Assert.Throws<IOException>(() => stream.Read(new byte[1], 0, 1),
                    "a read starting exactly at the range must fail");

                stream.Position = 39;
                Assert.Throws<IOException>(() => stream.Read(new byte[9], 0, 9),
                    "a read ending exactly at the range end must fail");
            });

            runner.Add("partial stream: zero length reads and reads ending at the range start succeed", () =>
            {
                using var stream = Create(out _);

                stream.Position = 32;
                Assert.Equal(0, stream.Read(Array.Empty<byte>(), 0, 0), "zero length read at the range start");
                Assert.Equal(0, stream.Read(Span<byte>.Empty), "zero length span read at the range start");

                stream.Position = 24;
                Assert.Equal(8, stream.Read(new byte[8], 0, 8), "read ending exactly at the range start");
            });

            runner.Add("partial stream: ReadByte honours the same boundaries", () =>
            {
                using var stream = Create(out var inner);

                stream.Position = 31;
                Assert.Equal(inner[31], stream.ReadByte(), "byte before the range");

                stream.Position = 32;
                Assert.Throws<IOException>(() => stream.ReadByte(), "byte at the range start must fail");

                stream.Position = 47;
                Assert.Throws<IOException>(() => stream.ReadByte(), "last byte of the range must fail");

                stream.Position = 48;
                Assert.Equal(inner[48], stream.ReadByte(), "first byte after the range");
            });

            runner.Add("partial stream: seeking past an unavailable range restores reads", () =>
            {
                using var stream = Create(out var inner);

                stream.Position = 36;
                Assert.Throws<IOException>(() => stream.Read(new byte[4], 0, 4), "read inside the range");

                var position = stream.Seek(50, SeekOrigin.Begin);
                Assert.Equal(50L, position, "seek result");

                var buffer = new byte[4];
                Assert.Equal(4, stream.Read(buffer, 0, 4), "read after seeking");
                Assert.BytesEqual(inner.AsSpan(50, 4), buffer, "content after seeking");
            });

            runner.Add("partial stream: multiple ranges are all enforced", () =>
            {
                var inner = new MemoryStream(Sequence(128));
                using var stream = new PartialVfsStream(inner, new[]
                {
                    new PartialVfsStream.Range(16, 24, 1),
                    new PartialVfsStream.Range(64, 80, 2),
                });

                Assert.Equal(8, stream.Read(new byte[8], 0, 8), "read before the first range");
                stream.Position = 20;
                Assert.Throws<IOException>(() => stream.Read(new byte[4], 0, 4), "read inside the first range");
                stream.Position = 40;
                Assert.Equal(8, stream.Read(new byte[8], 0, 8), "read between the ranges");
                stream.Position = 70;
                Assert.Throws<IOException>(() => stream.Read(new byte[4], 0, 4), "read inside the second range");
                stream.Position = 80;
                Assert.Equal(8, stream.Read(new byte[8], 0, 8), "read after the last range");
            });

            runner.Add("partial stream: length, truncation and unsupported members behave", () =>
            {
                using var stream = Create(out var inner);

                Assert.Equal((long)inner.Length, stream.Length, "length is forwarded");
                Assert.True(stream.CanRead, "can read");
                Assert.True(stream.CanSeek, "can seek");
                Assert.False(stream.CanWrite, "cannot write");

                stream.Position = 96;
                Assert.Equal(4, stream.Read(new byte[16], 0, 16), "read is clamped to the remaining length");

                Assert.Throws<NotSupportedException>(() => stream.SetLength(10), "SetLength must be unsupported");
                Assert.Throws<NotSupportedException>(() => stream.Write(new byte[1], 0, 1), "Write must be unsupported");
            });

            runner.Add("partial stream: object probes only reject ranges that overlap unavailable data", () =>
            {
                using var stream = Create(out _);

                stream.RequireAvailable(0, 32);
                stream.RequireAvailable(48, 16);
                stream.RequireAvailable(32, 0);

                var crossingStart = Assert.Throws<IOException>(() => stream.RequireAvailable(31, 2),
                    "a range crossing into the unavailable block must be rejected");
                Assert.Contains(crossingStart.Message, "block 3", "the error should name the unavailable block");

                Assert.Throws<IOException>(() => stream.RequireAvailable(40, 1),
                    "a range inside the unavailable block must be rejected");
                Assert.Throws<IOException>(() => stream.RequireAvailable(24, 24),
                    "a range ending inside the unavailable block must be rejected");
            });

            runner.Add("partial stream: disposing disposes the inner stream", () =>
            {
                var inner = new MemoryStream(Sequence(16));
                var stream = new PartialVfsStream(inner, new[] { new PartialVfsStream.Range(4, 8, 0) });
                stream.Dispose();

                Assert.Throws<ObjectDisposedException>(() => inner.ReadByte(), "inner stream should be disposed");
            });
        }

        private static PartialVfsStream Create(out byte[] content)
        {
            content = Sequence(100);
            var range = new PartialVfsStream.Range(32, 48, 3);
            return new PartialVfsStream(new MemoryStream(content), new[] { range });
        }

        private static byte[] Sequence(int length)
        {
            var data = new byte[length];
            for (var i = 0; i < length; i++)
            {
                data[i] = (byte)i;
            }

            return data;
        }
    }
}

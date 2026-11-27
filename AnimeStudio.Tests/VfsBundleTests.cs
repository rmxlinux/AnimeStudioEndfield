using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnimeStudio.Tests
{
    internal static class VfsBundleTests
    {
        private static readonly byte[] KeyOne = Enumerable.Range(0x10, 16).Select(value => (byte)value).ToArray();
        private static readonly byte[] KeyTwo = Enumerable.Range(0x40, 16).Select(value => (byte)(value * 3)).ToArray();
        private const uint KeyIdOne = 0x11111111;
        private const uint KeyIdTwo = 0x22222222;
        private const uint MissingKeyId = 0xDEADBEEF;

        public static void Register(TestRunner runner)
        {
            runner.Add("vfs: encrypted blocks decrypt at their real, unaligned output offsets", () =>
            {
                var plain0 = Pattern(100, 1);
                var plain1 = Pattern(37, 2);
                var plain2 = Pattern(200, 3);
                var plain3 = Pattern(64, 4);

                var bundle = SyntheticVfs.Build(
                    new[]
                    {
                        new SyntheticBlock { Payload = plain0 },
                        new SyntheticBlock { Payload = plain1, Encrypted = true, KeyId = KeyIdOne, Key = KeyOne },
                        new SyntheticBlock { Payload = plain2, Encrypted = true, KeyId = KeyIdTwo, Key = KeyTwo },
                        new SyntheticBlock { Payload = plain3 },
                    },
                    new[]
                    {
                        new SyntheticNode { Path = "a.bin", Offset = 0, Size = 100 },
                        new SyntheticNode { Path = "b.bin", Offset = 100, Size = 37 },
                        new SyntheticNode { Path = "c.bin", Offset = 137, Size = 200 },
                        new SyntheticNode { Path = "d.bin", Offset = 337, Size = 64 },
                        new SyntheticNode { Path = "span.bin", Offset = 90, Size = 260 },
                    });

                using var loaded = Load(bundle, Keys((KeyIdOne, KeyOne), (KeyIdTwo, KeyTwo)));
                var files = loaded.File.fileList;

                Assert.Equal(5, files.Count, "file count");
                Assert.False(files.Any(file => file.HasUnavailableData), "no file should be flagged when every key is present");

                Assert.BytesEqual(plain0, ReadAll(files[0]), "a.bin content");
                Assert.BytesEqual(plain1, ReadAll(files[1]), "b.bin content (block starts at offset 100)");
                Assert.BytesEqual(plain2, ReadAll(files[2]), "c.bin content (block starts at offset 137)");
                Assert.BytesEqual(plain3, ReadAll(files[3]), "d.bin content");

                var expected = plain0.Concat(plain1).Concat(plain2).Concat(plain3).ToArray();
                Assert.BytesEqual(expected.AsSpan(90, 260), ReadAll(files[4]), "span.bin content");
            });

            runner.Add("vfs: a missing key only hides the affected block", () =>
            {
                var plain0 = Pattern(100, 11);
                var plain1 = Pattern(137, 12);
                var plain2 = Pattern(64, 13);

                var bundle = SyntheticVfs.Build(
                    new[]
                    {
                        new SyntheticBlock { Payload = plain0 },
                        new SyntheticBlock { Payload = plain1, Encrypted = true, KeyId = KeyIdOne, Key = KeyOne },
                        new SyntheticBlock { Payload = plain2, Encrypted = true, KeyId = MissingKeyId, Key = KeyTwo },
                        new SyntheticBlock { Payload = plain0, Encrypted = true, KeyId = KeyIdTwo, Key = KeyTwo },
                    },
                    new[]
                    {
                        new SyntheticNode { Path = "before.bin", Offset = 0, Size = 100 },
                        new SyntheticNode { Path = "good.bin", Offset = 100, Size = 137 },
                        new SyntheticNode { Path = "missing.bin", Offset = 237, Size = 64 },
                        new SyntheticNode { Path = "after.bin", Offset = 301, Size = 100 },
                        new SyntheticNode { Path = "overlap.bin", Offset = 230, Size = 80 },
                    });

                using var loaded = Load(bundle, Keys((KeyIdOne, KeyOne), (KeyIdTwo, KeyTwo)));
                var files = loaded.File.fileList;

                Assert.Equal(5, files.Count, "file count");
                Assert.False(files[0].HasUnavailableData, "before.bin should be readable");
                Assert.False(files[1].HasUnavailableData, "good.bin should be readable");
                Assert.True(files[2].HasUnavailableData, "missing.bin should be flagged");
                Assert.False(files[3].HasUnavailableData, "after.bin should stay readable");
                Assert.True(files[4].HasUnavailableData, "overlap.bin should be flagged");

                Assert.BytesEqual(plain0, ReadAll(files[0]), "before.bin content");
                Assert.BytesEqual(plain1, ReadAll(files[1]), "good.bin content");
                Assert.BytesEqual(plain0, ReadAll(files[3]), "after.bin content (offset stays intact after a failed block)");

                var error = Assert.Throws<IOException>(() => ReadAll(files[2]), "reading a missing block must fail");
                Assert.Contains(error.Message, "block 2", "the error should identify the failed block");
                Assert.Contains(error.Message, "could not be decrypted", "the error should explain why");

                Assert.BytesEqual(plain1.AsSpan(130, 7), ReadAllPrefix(files[4], 7),
                    "the readable prefix of a partially hidden file");
                Assert.Throws<IOException>(() => ReadAllPrefix(files[4], 8),
                    "crossing into the hidden range must fail");
            });

            runner.Add("vfs: a corrupt encrypted block is quarantined instead of aborting the bundle", () =>
            {
                var plain0 = Pattern(16, 21);
                var garbage = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }; // impossible LZ4Inv token stream

                var bundle = SyntheticVfs.Build(
                    new[]
                    {
                        new SyntheticBlock { Payload = plain0 },
                        new SyntheticBlock
                        {
                            Payload = garbage,
                            Encrypted = true,
                            KeyId = KeyIdOne,
                            Key = KeyOne,
                            CompressionType = 5,
                            UncompressedSizeOverride = 64,
                        },
                    },
                    new[]
                    {
                        new SyntheticNode { Path = "ok.bin", Offset = 0, Size = 16 },
                        new SyntheticNode { Path = "broken.bin", Offset = 16, Size = 64 },
                    });

                using var loaded = Load(bundle, Keys((KeyIdOne, KeyOne)));
                var files = loaded.File.fileList;

                Assert.False(files[0].HasUnavailableData, "ok.bin should be readable");
                Assert.True(files[1].HasUnavailableData, "a block that fails to decompress must be quarantined");
                Assert.BytesEqual(plain0, ReadAll(files[0]), "ok.bin content");
                Assert.Throws<IOException>(() => ReadAll(files[1]), "reading the quarantined block must fail");
            });

            runner.Add("vfs: a truncated block payload is rejected", () =>
            {
                var bundle = SyntheticVfs.Build(
                    new[]
                    {
                        new SyntheticBlock { Payload = Pattern(32, 31), Encrypted = true, KeyId = KeyIdOne, Key = KeyOne },
                    },
                    new[]
                    {
                        new SyntheticNode { Path = "a.bin", Offset = 0, Size = 32 },
                    });

                var truncated = bundle.AsSpan(0, bundle.Length - 4).ToArray();
                Assert.Throws<EndOfStreamException>(() => Load(truncated, Keys((KeyIdOne, KeyOne))),
                    "a block shorter than its table entry must be rejected");
            });
        }

        private static byte[] Pattern(int length, int seed)
        {
            var data = new byte[length];
            for (var i = 0; i < length; i++)
            {
                data[i] = (byte)((i * 17 + seed * 29) & 0xFF);
            }

            return data;
        }

        private static Dictionary<uint, byte[]> Keys(params (uint Id, byte[] Key)[] entries)
        {
            return entries.ToDictionary(entry => entry.Id, entry => entry.Key);
        }

        private static byte[] ReadAll(StreamFile file)
        {
            file.stream.Position = 0;
            using var buffer = new MemoryStream();
            file.stream.CopyTo(buffer);
            return buffer.ToArray();
        }

        private static byte[] ReadAllPrefix(StreamFile file, int count)
        {
            var buffer = new byte[count];
            file.stream.Position = 0;
            var read = 0;
            while (read < count)
            {
                var chunk = file.stream.Read(buffer, read, count - read);
                if (chunk == 0)
                {
                    break;
                }

                read += chunk;
            }

            return buffer.AsSpan(0, read).ToArray();
        }

        private static LoadedBundle Load(byte[] bundle, Dictionary<uint, byte[]> keys)
        {
            EndfieldBundleKeyStore.ReplaceKeys(keys);

            var directory = Path.Combine(Path.GetTempPath(), "anime-studio-vfs-tests");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".vfs");
            File.WriteAllBytes(path, bundle);

            var reader = new FileReader(path);
            try
            {
                var file = new VFSFile(reader, path, GameType.ArknightsEndfield);
                return new LoadedBundle(file, reader, path);
            }
            catch
            {
                reader.Dispose();
                TryDelete(path);
                throw;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Leftover temp files are harmless.
            }
        }

        private sealed class LoadedBundle : IDisposable
        {
            public LoadedBundle(VFSFile file, FileReader reader, string path)
            {
                File = file;
                Reader = reader;
                Path = path;
            }

            public VFSFile File { get; }
            private FileReader Reader { get; }
            private string Path { get; }

            public void Dispose()
            {
                foreach (var entry in File.fileList)
                {
                    entry.stream?.Dispose();
                }

                Reader.Dispose();
                TryDelete(Path);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AnimeStudio.Tests
{
    internal static class StreamTests
    {
        private static readonly byte[] PatternKey =
        {
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
            0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
        };

        public static void Register(TestRunner runner)
        {
            runner.Add("stream: matches the captured UnityPlayer fixtures", () =>
            {
                foreach (var (name, offset) in new[] { ("block-zero", 0u), ("block-nonzero-offset", 37u) })
                {
                    var ciphertext = File.ReadAllBytes(DataPath(name + ".input"));
                    var expected = File.ReadAllBytes(DataPath(name + ".expected"));
                    var actual = (byte[])ciphertext.Clone();

                    EndfieldBundleCrypto.DecryptBlockPayload(actual, PatternKey, offset);

                    Assert.BytesEqual(expected, actual, name);
                }
            });

            runner.Add("stream: matches the independent reference vectors", () =>
            {
                using var document = JsonDocument.Parse(File.ReadAllText(DataPath("stream-vectors.json")));
                var cases = document.RootElement.GetProperty("cases");
                Assert.True(cases.GetArrayLength() > 0, "vector file must contain cases");

                foreach (var item in cases.EnumerateArray())
                {
                    var name = item.GetProperty("name").GetString();
                    var key = Convert.FromHexString(item.GetProperty("key").GetString()!);
                    var offset = item.GetProperty("offset").GetUInt32();
                    var ciphertext = Convert.FromHexString(item.GetProperty("ciphertext").GetString()!);
                    var plaintext = Convert.FromHexString(item.GetProperty("plaintext").GetString()!);
                    var actual = (byte[])ciphertext.Clone();

                    EndfieldBundleCrypto.DecryptBlockPayload(actual, key, offset);

                    Assert.BytesEqual(plaintext, actual, "vector " + name);
                }
            });

            runner.Add("stream: the same ciphertext decrypts differently at different offsets", () =>
            {
                var ciphertext = new byte[64];
                for (var i = 0; i < ciphertext.Length; i++)
                {
                    ciphertext[i] = (byte)(i * 11 + 5);
                }

                var atZero = Decrypt(ciphertext, PatternKey, 0);
                var atOne = Decrypt(ciphertext, PatternKey, 1);
                var atSixteen = Decrypt(ciphertext, PatternKey, 16);

                Assert.False(atZero.AsSpan().SequenceEqual(atOne), "offset 0 and 1 must produce different keystreams");
                Assert.False(atZero.AsSpan(16).SequenceEqual(atSixteen), "a 16 byte aligned window must not restart the counter");
            });

            runner.Add("stream: a window at absolute offset equals the tail of a full decrypt", () =>
            {
                var random = new Random(20261127);
                var lengths = new[] { 1, 15, 16, 17, 31, 32, 33, 255, 256, 257, 1000 };
                var offsets = new uint[] { 0, 1, 7, 15, 16, 17, 31, 32, 37, 100, 255, 256, 4096, 65535 };

                foreach (var length in lengths)
                {
                    var full = new byte[length];
                    random.NextBytes(full);
                    var fullPlain = Decrypt(full, PatternKey, 0);

                    foreach (var offset in offsets)
                    {
                        var absolute = (int)offset;
                        if (absolute >= full.Length)
                        {
                            continue;
                        }

                        var window = full.AsSpan(absolute).ToArray();
                        var windowPlain = Decrypt(window, PatternKey, offset);
                        Assert.BytesEqual(fullPlain.AsSpan(absolute), windowPlain,
                            $"window length={window.Length} offset={offset}");
                    }
                }
            });

            runner.Add("stream: decryption is its own inverse", () =>
            {
                var random = new Random(555);
                for (var round = 0; round < 32; round++)
                {
                    var data = new byte[random.Next(1, 700)];
                    var key = new byte[16];
                    random.NextBytes(data);
                    random.NextBytes(key);
                    var offset = (uint)random.Next(0, 100000);

                    var plain = Decrypt(data, key, offset);
                    var restored = Decrypt(plain, key, offset);

                    Assert.BytesEqual(data, restored, $"round {round}");
                }
            });

            runner.Add("stream: rejects keys that are not 16 bytes", () =>
            {
                foreach (var size in new[] { 0, 15, 17, 24, 32 })
                {
                    var data = new byte[32];
                    Assert.Throws<ArgumentException>(
                        () => EndfieldBundleCrypto.DecryptBlockPayload(data, new byte[size], 0),
                        $"key size {size} should be rejected");
                }
            });

            runner.Add("stream: empty payload is a no-op", () =>
            {
                EndfieldBundleCrypto.DecryptBlockPayload(Span<byte>.Empty, PatternKey, 12345);
            });

            runner.Add("stream: very large offsets stay deterministic", () =>
            {
                var data = new byte[96];
                new Random(17).NextBytes(data);
                var first = Decrypt(data, PatternKey, uint.MaxValue - 31);
                var second = Decrypt(data, PatternKey, uint.MaxValue - 31);
                Assert.BytesEqual(first, second, "same offset must produce the same stream");
                Assert.False(first.AsSpan().SequenceEqual(data), "stream must actually be applied");
            });
        }

        private static byte[] Decrypt(byte[] ciphertext, byte[] key, uint offset)
        {
            var copy = (byte[])ciphertext.Clone();
            EndfieldBundleCrypto.DecryptBlockPayload(copy, key, offset);
            return copy;
        }

        private static string DataPath(string name) => Path.Combine(AppContext.BaseDirectory, "Data", name);
    }
}

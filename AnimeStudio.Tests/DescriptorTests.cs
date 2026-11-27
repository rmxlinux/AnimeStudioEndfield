using System;
using System.Security.Cryptography;
using System.Text;

namespace AnimeStudio.Tests
{
    internal static class DescriptorTests
    {
        // AES-256 key the launcher response values are wrapped with.
        private const string WrappingKeyBase64 = "J5UrEzDfdeCNM/Kdt+2SjBjygRZWi0AhvuVePAoQYjg=";

        public static void Register(TestRunner runner)
        {
            runner.Add("descriptor: unwraps a 32 hex character descriptor XORed with 0xA5", () =>
            {
                var key = new byte[16];
                for (var i = 0; i < key.Length; i++)
                {
                    key[i] = (byte)i;
                }

                var value = Wrap(Describe(key), new byte[16]);
                Assert.True(EndfieldBundleCrypto.TryDecodeResourceKeyValue(value, out var decoded, out var error),
                    "descriptor value should decode, got error: " + error);
                Assert.BytesEqual(key, decoded, "decoded key");
            });

            runner.Add("descriptor: accepts lowercase hex and surrounding whitespace", () =>
            {
                var key = new byte[16];
                new Random(1234).NextBytes(key);
                var descriptor = Describe(key).ToLowerInvariant();
                var value = Wrap("  " + descriptor + "\n", new byte[16]);

                Assert.True(EndfieldBundleCrypto.TryDecodeResourceKeyValue(value, out var decoded, out var error),
                    "descriptor value should decode, got error: " + error);
                Assert.BytesEqual(key, decoded, "decoded key");
            });

            runner.Add("descriptor: random descriptors round-trip", () =>
            {
                var random = new Random(9876);
                for (var round = 0; round < 64; round++)
                {
                    var key = new byte[16];
                    var iv = new byte[16];
                    random.NextBytes(key);
                    random.NextBytes(iv);

                    var value = Wrap(Describe(key), iv);
                    Assert.True(EndfieldBundleCrypto.TryDecodeResourceKeyValue(value, out var decoded, out var error),
                        $"round {round} should decode, got error: {error}");
                    Assert.BytesEqual(key, decoded, $"round {round} decoded key");
                }
            });

            runner.Add("descriptor: rejects invalid inputs instead of throwing", () =>
            {
                var key = new byte[16];
                new Random(42).NextBytes(key);

                var invalid = new (string Name, string Value)[]
                {
                    ("empty", string.Empty),
                    ("not base64", "not base64 !!!"),
                    ("payload shorter than one iv", Convert.ToBase64String(new byte[16])),
                    ("ciphertext not block aligned", Convert.ToBase64String(new byte[32])),
                    ("random bytes", Convert.ToBase64String(CreateRandom(64))),
                };

                foreach (var (name, value) in invalid)
                {
                    Assert.False(EndfieldBundleCrypto.TryDecodeResourceKeyValue(value, out var decoded, out var error),
                        name + " should be rejected");
                    Assert.True(decoded is null, name + " should not produce a key");
                    Assert.True(!string.IsNullOrEmpty(error), name + " should report an error");
                }
            });

            runner.Add("descriptor: rejects a decrypted descriptor that is not 32 hex characters", () =>
            {
                foreach (var descriptor in new[] { new string('A', 30), new string('A', 34), new string('Z', 32) })
                {
                    var value = Wrap(descriptor, new byte[16]);
                    Assert.False(EndfieldBundleCrypto.TryDecodeResourceKeyValue(value, out _, out var error),
                        $"descriptor '{descriptor}' should be rejected");
                    Assert.True(!string.IsNullOrEmpty(error), "rejection should carry an error message");
                }
            });

            runner.Add("descriptor: corrupted ciphertext fails cleanly", () =>
            {
                var key = new byte[16];
                new Random(7).NextBytes(key);
                var payload = Convert.FromBase64String(Wrap(Describe(key), new byte[16]));
                payload[^1] ^= 0x5A;

                Assert.False(EndfieldBundleCrypto.TryDecodeResourceKeyValue(Convert.ToBase64String(payload), out var decoded, out var error),
                    "corrupted ciphertext should be rejected");
                Assert.True(decoded is null, "corrupted ciphertext should not produce a key");
                Assert.True(!string.IsNullOrEmpty(error), "corrupted ciphertext should report an error");
            });
        }

        private static string Describe(byte[] key)
        {
            var descriptor = new byte[key.Length];
            for (var i = 0; i < key.Length; i++)
            {
                descriptor[i] = (byte)(key[i] ^ 0xA5);
            }

            return Convert.ToHexString(descriptor);
        }

        private static string Wrap(string descriptor, byte[] iv)
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = Convert.FromBase64String(WrappingKeyBase64);
            aes.IV = iv;

            using var encryptor = aes.CreateEncryptor();
            var plaintext = Encoding.UTF8.GetBytes(descriptor);
            var ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

            var payload = new byte[iv.Length + ciphertext.Length];
            iv.CopyTo(payload, 0);
            ciphertext.CopyTo(payload, iv.Length);
            return Convert.ToBase64String(payload);
        }

        private static byte[] CreateRandom(int length)
        {
            var data = new byte[length];
            new Random(length).NextBytes(data);
            return data;
        }
    }
}

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AnimeStudio
{
    public static class EndfieldBundleKeyStore
    {
        private static readonly ConcurrentDictionary<uint, byte[]> Keys = new();

        public static bool TryGetKey(uint id, out byte[] key)
        {
            if (Keys.TryGetValue(id, out var cached))
            {
                key = cached;
                return true;
            }

            key = null;
            return false;
        }

        public static void ReplaceKeys(IEnumerable<KeyValuePair<uint, byte[]>> keys)
        {
            Keys.Clear();
            AddOrUpdateKeys(keys);
        }

        public static void AddOrUpdateKeys(IEnumerable<KeyValuePair<uint, byte[]>> keys)
        {
            foreach (var entry in keys)
            {
                if (entry.Value is { Length: 16 })
                {
                    Keys[entry.Key] = entry.Value.ToArray();
                }
            }
        }

        public static Dictionary<uint, byte[]> Snapshot()
        {
            var snapshot = new Dictionary<uint, byte[]>();
            foreach (var entry in Keys)
            {
                snapshot[entry.Key] = entry.Value.ToArray();
            }

            return snapshot;
        }

    }

    public static class EndfieldBundleCrypto
    {
        private const string LatestResourcesValueText = "J5UrEzDfdeCNM/Kdt+2SjBjygRZWi0AhvuVePAoQYjg=";

        private static readonly uint[] CounterSeed =
        {
            0xA95E12C7 ^ 0x6AE642EB,
            0x8833DB04 ^ 0xA5C31E90,
            0x49770EF2 ^ 0x27B4D6F8,
            0x931CBA65 ^ 0xA5C31E90,
        };

        public static bool TryDecodeResourceKeyValue(string encryptedValue, out byte[] key, out string error)
        {
            key = null;
            error = null;

            try
            {
                var payload = Convert.FromBase64String(encryptedValue);
                if (payload.Length < 32 || (payload.Length - 16) % 16 != 0)
                {
                    error = "ciphertext length is invalid";
                    return false;
                }

                var aesKey = Convert.FromBase64String(LatestResourcesValueText);
                var iv = payload.AsSpan(0, 16).ToArray();
                var ciphertext = payload.AsSpan(16).ToArray();

                using var aes = Aes.Create();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = aesKey;
                aes.IV = iv;

                using var decryptor = aes.CreateDecryptor();
                var plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
                var descriptor = Encoding.UTF8.GetString(plaintext).Trim();
                if (descriptor.Length != 32)
                {
                    error = "decrypted descriptor must contain 32 hexadecimal characters";
                    return false;
                }

                var descriptorBytes = Convert.FromHexString(descriptor);
                for (var i = 0; i < descriptorBytes.Length; i++)
                {
                    descriptorBytes[i] ^= 0xA5;
                }

                key = descriptorBytes;
                return true;
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
            {
                error = ex.Message;
                return false;
            }
        }

        public static void DecryptBlockPayload(Span<byte> data, ReadOnlySpan<byte> key, long offset = 0)
        {
            if (key.Length != 16)
            {
                throw new ArgumentException("Endfield bundle keys must be 16 bytes long.", nameof(key));
            }

            var roundKeys = ExpandTwoRounds(key);
            Span<byte> counterBlock = stackalloc byte[16];
            Span<byte> keystream = stackalloc byte[16];

            var dataOffset = 0;
            var absoluteOffset = (ulong)offset;
            while (dataOffset < data.Length)
            {
                var counter = checked((uint)(absoluteOffset >> 4));
                var positionInBlock = (int)(absoluteOffset & 0x0F);
                BinaryPrimitives.WriteUInt32LittleEndian(counterBlock, CounterSeed[0]);
                BinaryPrimitives.WriteUInt32LittleEndian(counterBlock[4..], CounterSeed[1]);
                BinaryPrimitives.WriteUInt32LittleEndian(counterBlock[8..], CounterSeed[2]);
                BinaryPrimitives.WriteUInt32LittleEndian(counterBlock[12..], CounterSeed[3] ^ counter);
                EncryptTwoRounds(counterBlock, roundKeys, keystream);

                var count = Math.Min(16 - positionInBlock, data.Length - dataOffset);
                for (var i = 0; i < count; i++)
                {
                    data[dataOffset + i] ^= keystream[positionInBlock + i];
                }

                dataOffset += count;
                absoluteOffset += (uint)count;
            }
        }

        private static byte[] ExpandTwoRounds(ReadOnlySpan<byte> key)
        {
            var roundKeys = new byte[48];
            key.CopyTo(roundKeys);
            byte roundConstant = 1;
            Span<byte> temporary = stackalloc byte[4];

            for (var word = 4; word < 12; word++)
            {
                var sourceOffset = (word - 1) * 4;
                temporary[0] = roundKeys[sourceOffset];
                temporary[1] = roundKeys[sourceOffset + 1];
                temporary[2] = roundKeys[sourceOffset + 2];
                temporary[3] = roundKeys[sourceOffset + 3];

                if (word % 4 == 0)
                {
                    var first = temporary[0];
                    temporary[0] = CryptoHelper.VFSAESSBox[temporary[1]];
                    temporary[1] = CryptoHelper.VFSAESSBox[temporary[2]];
                    temporary[2] = CryptoHelper.VFSAESSBox[temporary[3]];
                    temporary[3] = CryptoHelper.VFSAESSBox[first];
                    temporary[0] ^= roundConstant;
                    roundConstant = XTime(roundConstant);
                }

                var destinationOffset = word * 4;
                var previousOffset = (word - 4) * 4;
                for (var i = 0; i < 4; i++)
                {
                    roundKeys[destinationOffset + i] = (byte)(roundKeys[previousOffset + i] ^ temporary[i]);
                }
            }

            return roundKeys;
        }

        private static void EncryptTwoRounds(ReadOnlySpan<byte> input, ReadOnlySpan<byte> roundKeys, Span<byte> output)
        {
            Span<byte> state = stackalloc byte[16];
            Span<byte> shifted = stackalloc byte[16];

            for (var i = 0; i < 16; i++)
            {
                state[i] = (byte)(input[i] ^ roundKeys[i]);
            }

            for (var round = 1; round <= 2; round++)
            {
                for (var column = 0; column < 4; column++)
                {
                    for (var row = 0; row < 4; row++)
                    {
                        shifted[column * 4 + row] = CryptoHelper.VFSAESSBox[state[((column + row) & 3) * 4 + row]];
                    }
                }

                if (round == 1)
                {
                    for (var column = 0; column < 4; column++)
                    {
                        var index = column * 4;
                        var a = shifted[index];
                        var b = shifted[index + 1];
                        var c = shifted[index + 2];
                        var d = shifted[index + 3];
                        var total = (byte)(a ^ b ^ c ^ d);
                        state[index] = (byte)(a ^ total ^ XTime((byte)(a ^ b)));
                        state[index + 1] = (byte)(b ^ total ^ XTime((byte)(b ^ c)));
                        state[index + 2] = (byte)(c ^ total ^ XTime((byte)(c ^ d)));
                        state[index + 3] = (byte)(d ^ total ^ XTime((byte)(d ^ a)));
                    }
                }
                else
                {
                    shifted.CopyTo(state);
                }

                var roundKeyOffset = round * 16;
                for (var i = 0; i < 16; i++)
                {
                    state[i] ^= roundKeys[roundKeyOffset + i];
                }
            }

            state.CopyTo(output);
        }

        private static byte XTime(byte value)
        {
            return (byte)(((value & 0x80) != 0) ? (value << 1) ^ 0x1B : value << 1);
        }
    }
}

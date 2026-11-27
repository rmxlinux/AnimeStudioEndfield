using System;
using System.Buffers.Binary;

namespace AnimeStudio.Tests
{
    /// <summary>
    /// Independent transcription of the encrypted Endfield VFS block stream.
    ///
    /// The production implementation (<c>EndfieldBundleCrypto</c>) works on a
    /// column-major byte layout and derives the keystream in a single pass.
    /// This reference is deliberately written the other way round: it keeps a
    /// FIPS-197 style <c>[row, column]</c> state matrix, uses its own key
    /// schedule and counter schedule, and only shares the public S-box table
    /// and the published seed words with production. A layout or transcription
    /// mistake on either side therefore shows up as a mismatch instead of
    /// being hidden by copy/paste.
    /// </summary>
    internal static class EndfieldStreamReference
    {
        // The published seed is stored obfuscated as four XOR pairs; keep the
        // pairs here so the reference derives the same words independently.
        private static readonly uint[] SeedWords =
        {
            0xA95E12C7 ^ 0x6AE642EB,
            0x8833DB04 ^ 0xA5C31E90,
            0x49770EF2 ^ 0x27B4D6F8,
            0x931CBA65 ^ 0xA5C31E90,
        };

        public static byte[] Decrypt(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> key, uint offset)
        {
            if (key.Length != 16)
            {
                throw new ArgumentException("Endfield bundle keys must be 16 bytes long.", nameof(key));
            }

            var words = ExpandKey(key);
            var result = ciphertext.ToArray();
            ulong absolute = offset;
            var index = 0;

            while (index < result.Length)
            {
                var counter = (uint)(absolute >> 4);
                var position = (int)(absolute & 0xF);
                var keystream = EncryptBlock(BuildCounterBlock(counter), words);
                var take = Math.Min(16 - position, result.Length - index);

                for (var i = 0; i < take; i++)
                {
                    result[index + i] ^= keystream[position + i];
                }

                index += take;
                absolute += (uint)take;
            }

            return result;
        }

        private static byte[] BuildCounterBlock(uint counter)
        {
            var block = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0), SeedWords[0]);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), SeedWords[1]);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8), SeedWords[2]);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12), SeedWords[3] ^ counter);
            return block;
        }

        // FIPS-197 key expansion restricted to the two rounds used here.
        private static byte[][] ExpandKey(ReadOnlySpan<byte> key)
        {
            var words = new byte[12][];
            for (var i = 0; i < 4; i++)
            {
                words[i] = key.Slice(i * 4, 4).ToArray();
            }

            byte roundConstant = 1;
            for (var word = 4; word < words.Length; word++)
            {
                var temp = (byte[])words[word - 1].Clone();
                if (word % 4 == 0)
                {
                    temp = new[] { SBox(temp[1]), SBox(temp[2]), SBox(temp[3]), SBox(temp[0]) };
                    temp[0] ^= roundConstant;
                    roundConstant = MultiplyByTwo(roundConstant);
                }

                words[word] = new byte[4];
                for (var i = 0; i < 4; i++)
                {
                    words[word][i] = (byte)(words[word - 4][i] ^ temp[i]);
                }
            }

            return words;
        }

        private static byte[] EncryptBlock(ReadOnlySpan<byte> input, byte[][] words)
        {
            // state[row, column]; input byte k lives at state[k % 4, k / 4].
            var state = new byte[4, 4];
            for (var column = 0; column < 4; column++)
            {
                for (var row = 0; row < 4; row++)
                {
                    state[row, column] = (byte)(input[column * 4 + row] ^ words[column][row]);
                }
            }

            // Round 1: SubBytes, ShiftRows, MixColumns, AddRoundKey.
            SubBytes(state);
            ShiftRows(state);
            MixColumns(state);
            AddRoundKey(state, words, 1);

            // Round 2 is the final round: no MixColumns.
            SubBytes(state);
            ShiftRows(state);
            AddRoundKey(state, words, 2);

            var output = new byte[16];
            for (var column = 0; column < 4; column++)
            {
                for (var row = 0; row < 4; row++)
                {
                    output[column * 4 + row] = state[row, column];
                }
            }

            return output;
        }

        private static void SubBytes(byte[,] state)
        {
            for (var row = 0; row < 4; row++)
            {
                for (var column = 0; column < 4; column++)
                {
                    state[row, column] = SBox(state[row, column]);
                }
            }
        }

        private static void ShiftRows(byte[,] state)
        {
            for (var row = 1; row < 4; row++)
            {
                var original = new byte[4];
                for (var column = 0; column < 4; column++)
                {
                    original[column] = state[row, column];
                }

                for (var column = 0; column < 4; column++)
                {
                    state[row, column] = original[(column + row) % 4];
                }
            }
        }

        private static void MixColumns(byte[,] state)
        {
            for (var column = 0; column < 4; column++)
            {
                var a0 = state[0, column];
                var a1 = state[1, column];
                var a2 = state[2, column];
                var a3 = state[3, column];

                state[0, column] = (byte)(MultiplyByTwo(a0) ^ (MultiplyByTwo(a1) ^ a1) ^ a2 ^ a3);
                state[1, column] = (byte)(a0 ^ MultiplyByTwo(a1) ^ (MultiplyByTwo(a2) ^ a2) ^ a3);
                state[2, column] = (byte)(a0 ^ a1 ^ MultiplyByTwo(a2) ^ (MultiplyByTwo(a3) ^ a3));
                state[3, column] = (byte)((MultiplyByTwo(a0) ^ a0) ^ a1 ^ a2 ^ MultiplyByTwo(a3));
            }
        }

        private static void AddRoundKey(byte[,] state, byte[][] words, int round)
        {
            for (var column = 0; column < 4; column++)
            {
                for (var row = 0; row < 4; row++)
                {
                    state[row, column] ^= words[round * 4 + column][row];
                }
            }
        }

        private static byte MultiplyByTwo(byte value) =>
            (byte)((value & 0x80) != 0 ? (value << 1) ^ 0x1B : value << 1);

        private static byte SBox(byte value) => CryptoHelper.VFSAESSBox[value];
    }
}

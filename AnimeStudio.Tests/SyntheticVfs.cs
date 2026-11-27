using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace AnimeStudio.Tests
{
    internal sealed class SyntheticBlock
    {
        public byte[] Payload = Array.Empty<byte>();
        public bool Encrypted;
        public uint KeyId;
        public byte[] Key = Array.Empty<byte>();
        public int CompressionType;
        public long UncompressedSizeOverride = -1;
        public long OutputOffset;

        public long UncompressedSize => UncompressedSizeOverride >= 0 ? UncompressedSizeOverride : Payload.Length;
    }

    internal sealed class SyntheticNode
    {
        public string Path = string.Empty;
        public long Offset;
        public long Size;
    }

    /// <summary>
    /// Writes a minimal, structurally valid Endfield VFS container so that the
    /// real <c>VFSFile</c> parser reads it. Every field is produced by applying
    /// the inverse of the scramble that <c>VFSUtils</c> applies when reading, so
    /// a drift in either direction breaks these tests.
    /// </summary>
    internal static class SyntheticVfs
    {
        private const int HeaderOffset = 48;

        public static byte[] Build(IReadOnlyList<SyntheticBlock> blocks, IReadOnlyList<SyntheticNode> nodes)
        {
            var outputOffset = 0L;
            foreach (var block in blocks)
            {
                block.OutputOffset = outputOffset;
                outputOffset += block.UncompressedSize;
            }

            var blocksInfo = WriteBlocksInfo(blocks);
            var directoryInfo = WriteDirectoryInfo(nodes);
            var blocksInfoBytes = new byte[blocksInfo.Count + directoryInfo.Count];
            blocksInfo.CopyTo(blocksInfoBytes, 0);
            directoryInfo.CopyTo(blocksInfoBytes, blocksInfo.Count);

            var data = new MemoryStream();
            foreach (var block in blocks)
            {
                var payload = block.Payload;
                if (block.Encrypted)
                {
                    payload = EndfieldStreamReference.Decrypt(block.Payload, block.Key, (uint)block.OutputOffset);
                }

                data.Write(payload);
            }

            var totalSize = (ulong)(HeaderOffset + blocksInfoBytes.Length + data.Length);
            var header = WriteHeader((uint)blocksInfoBytes.Length, totalSize, (ArchiveFlags)0, 7);

            using var bundle = new MemoryStream();
            bundle.Write(header);
            bundle.Write(blocksInfoBytes);
            bundle.Write(data.ToArray());
            return bundle.ToArray();
        }

        private static byte[] WriteHeader(uint blocksInfoSize, ulong size, ArchiveFlags flags, uint encFlags)
        {
            var header = new List<byte>(HeaderOffset);
            WriteUInt32BE(header, 0);
            WriteUInt32BE(header, DeriveHeaderB(0));

            const uint flags2 = 0;
            var compressed = ScrambleInvert32(blocksInfoSize, 0xF74324EE);
            var total = ScrambleInvert64(size);
            var storedFlags = (uint)flags ^ flags2 ^ 0xA7F49310;

            WriteUInt16BE(header, (ushort)(compressed & 0xFFFF));
            WriteUInt32BE(header, flags2);
            WriteUInt32BE(header, encFlags ^ flags2);
            WriteUInt32BE(header, (uint)(total & 0xFFFFFFFF));
            WriteUInt32BE(header, storedFlags);
            WriteUInt16BE(header, (ushort)((compressed >> 16) ^ (ushort)compressed ^ 0xA121));
            WriteUInt32BE(header, 0);
            WriteUInt16BE(header, (ushort)(compressed & 0xFFFF));
            WriteUInt32BE(header, (uint)(total >> 32));
            WriteUInt16BE(header, (ushort)((compressed >> 16) ^ (ushort)compressed ^ 0xA121));
            header.Add(0);

            while (header.Count < HeaderOffset)
            {
                header.Add(0);
            }

            return header.ToArray();
        }

        // Mirrors VFSUtils.IsValidHeader: b must equal the value derived from a.
        private static uint DeriveHeaderB(uint a)
        {
            var mixed = a ^ 0x4A92F0CD;
            var c1 = (uint)(4 * mixed) & 0xFFFF0000;
            var c2 = BitOperations.RotateRight(mixed, 14);
            return c1 ^ c2 ^ 0xD8B1E637;
        }

        // value = RotateRight(concat, 18) ^ key, so concat = RotateLeft(value ^ key, 18).
        private static uint ScrambleInvert32(uint value, uint key) =>
            BitOperations.RotateLeft(value ^ key, 18);

        private static ulong ScrambleInvert64(ulong value)
        {
            var concat = BitOperations.RotateLeft(value ^ 0xA4F1A11747816520UL, 18);
            var low = (uint)(concat & 0xFFFFFFFF);
            var high = (uint)(concat >> 32);
            return ((ulong)(high ^ low ^ 0xDAD76848) << 32) | low;
        }

        private static List<byte> WriteBlocksInfo(IReadOnlyList<SyntheticBlock> blocks)
        {
            var bytes = new List<byte>();
            WriteBlocksCount(bytes, blocks.Count);

            foreach (var block in blocks)
            {
                var flags = (ushort)((block.Encrypted ? 0x80 : 0) | (block.CompressionType & 0x3F));
                var compressedSize = (uint)block.Payload.Length;
                var uncompressedSize = (uint)block.UncompressedSize;

                // uncompressedSize: concat(a ^ c ^ 0xA121, c)
                var sizeBits = BitOperations.RotateLeft(uncompressedSize ^ 0xF74324EE, 18);
                var c = (ushort)(sizeBits & 0xFFFF);
                var a = (ushort)((sizeBits >> 16) ^ c ^ 0xA121);

                // compressedSize: concat(b ^ d ^ 0xA121, d)
                var compressedBits = BitOperations.RotateLeft(compressedSize ^ 0xF74324EE, 18);
                var d = (ushort)(compressedBits & 0xFFFF);
                var b = (ushort)((compressedBits >> 16) ^ d ^ 0xA121);

                // flags = c ^ RotateLeft((a1 ^ a0) << 8 | a0, 14) ^ 0x523F
                var flagBase = (ushort)(flags ^ c ^ 0x523F);
                var baseFlags = (ushort)((flagBase >> 14) | (flagBase << 2));
                var a0 = (byte)(baseFlags & 0xFF);
                var a1 = (byte)((baseFlags >> 8) ^ a0);
                var storedFlags = (ushort)(((a1 << 8) | a0) ^ 0x9CD6);

                WriteUInt16BE(bytes, a);
                WriteUInt16BE(bytes, b);
                WriteUInt16BE(bytes, c);
                WriteUInt16BE(bytes, storedFlags);
                WriteUInt16BE(bytes, d);

                if (block.Encrypted)
                {
                    var keyBits = BitOperations.RotateLeft(block.KeyId ^ 0xF74324EE, 18);
                    var keyLow = (ushort)(keyBits & 0xFFFF);
                    var keyHigh = (ushort)((keyBits >> 16) ^ keyLow ^ 0xA121);
                    WriteUInt16BE(bytes, keyHigh);
                    WriteUInt16BE(bytes, keyLow);
                }
            }

            return bytes;
        }

        private static List<byte> WriteDirectoryInfo(IReadOnlyList<SyntheticNode> nodes)
        {
            var bytes = new List<byte>();
            WriteNodesCount(bytes, nodes.Count);

            foreach (var node in nodes)
            {
                // size: concat(b ^ e ^ 0xDAD76848, e)
                var sizeBits = BitOperations.RotateRight((ulong)node.Size ^ 0xA4F1A11747816520UL, 14);
                var e = (uint)(sizeBits & 0xFFFFFFFF);
                var b = (uint)((sizeBits >> 32) ^ e ^ 0xDAD76848);

                // flags: RotateRight(concat((a1 ^ a0), a0), 18) ^ 0xF13927C4 ^ b
                var baseFlags = BitOperations.RotateLeft(0u ^ 0xF13927C4 ^ b, 18);
                var a0 = (ushort)(baseFlags & 0xFFFF);
                var a1 = (ushort)((baseFlags >> 16) ^ a0);
                var a = ((uint)a1 << 16) | a0;

                // offset: concat(d ^ c ^ 0xDAD76848, c)
                var offsetBits = BitOperations.RotateRight((ulong)node.Offset ^ 0xA4F1A11747816520UL, 14);
                var cValue = (uint)(offsetBits & 0xFFFFFFFF);
                var d = (uint)((offsetBits >> 32) ^ cValue ^ 0xDAD76848);

                WriteUInt32BE(bytes, a ^ 0x8E06A9F8);
                WriteUInt32BE(bytes, b);
                WriteUInt32BE(bytes, cValue);
                WriteUInt32BE(bytes, d);

                var name = Encoding.ASCII.GetBytes(node.Path);
                for (var i = 0; i < name.Length; i++)
                {
                    bytes.Add((byte)(name[i] ^ ((i ^ 0x97) & 0xFF)));
                }

                bytes.Add(0);
                WriteUInt32BE(bytes, e);
            }

            return bytes;
        }

        private static void WriteBlocksCount(List<byte> bytes, int count)
        {
            var mixed = BitOperations.RotateLeft((uint)count ^ 0x91CE0A4F, 18);
            var low = (ushort)(mixed & 0xFFFF);
            var high = (ushort)((mixed >> 16) ^ low);
            WriteUInt32LE(bytes, BinaryPrimitives.ReverseEndianness(((uint)high << 16) | low) ^ 0x8A7BF723);
        }

        private static void WriteNodesCount(List<byte> bytes, int count)
        {
            var mixed = BitOperations.RotateLeft((uint)count ^ 0xE4C1D9F2, 18);
            var low = (ushort)(mixed & 0xFFFF);
            var high = (ushort)((mixed >> 16) ^ low);
            WriteUInt32LE(bytes, BinaryPrimitives.ReverseEndianness(((uint)high << 16) | low) ^ 0x5DE50A6B);
        }

        private static void WriteUInt16BE(List<byte> bytes, ushort value)
        {
            bytes.Add((byte)(value >> 8));
            bytes.Add((byte)value);
        }

        private static void WriteUInt32BE(List<byte> bytes, uint value)
        {
            bytes.Add((byte)(value >> 24));
            bytes.Add((byte)(value >> 16));
            bytes.Add((byte)(value >> 8));
            bytes.Add((byte)value);
        }

        private static void WriteUInt32LE(List<byte> bytes, uint value)
        {
            bytes.Add((byte)value);
            bytes.Add((byte)(value >> 8));
            bytes.Add((byte)(value >> 16));
            bytes.Add((byte)(value >> 24));
        }
    }
}

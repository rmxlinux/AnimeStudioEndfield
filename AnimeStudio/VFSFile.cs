using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnimeStudio
{
    public class VFSFile
    {
        private List<BundleFile.StorageBlock> m_BlocksInfo;
        private List<BundleFile.Node> m_DirectoryInfo;

        public BundleFile.Header m_Header;
        public List<StreamFile> fileList;
        public long Offset;

        public VFSFile(FileReader reader, string path, GameType game)
        {
            Offset = reader.Position;
            reader.Endian = EndianType.BigEndian;


            if (!VFSUtils.IsValidHeader(reader, game))
            {
                throw new Exception("Not a VFS file / VFS version mismatch");
            }

            // read header
            reader.ReadBytes(8);
            m_Header = VFSUtils.ReadHeader(reader, game);
            Logger.Verbose($"Header : {m_Header.ToString()}");

            // go to blocks info
            uint blockInfosOffset;

            if ((m_Header.flags & ArchiveFlags.BlocksInfoAtTheEnd) != 0)
                blockInfosOffset = (uint)(m_Header.size) - m_Header.compressedBlocksInfoSize;
            else
            {
                if (m_Header.encFlags >= 7)
                    blockInfosOffset = 48;
                else
                    blockInfosOffset = 40;
            }

            reader.Position = Offset + blockInfosOffset;
            ReadBlocksInfoAndDirectory(reader, game);

            // go to data
            uint dataOffset;

            if (m_Header.encFlags >= 7)
                dataOffset = 48;
            else
                dataOffset = 40;
            if (((m_Header.flags) & ArchiveFlags.BlocksInfoAtTheEnd) == 0)
            {
                var temp = m_Header.compressedBlocksInfoSize;
                if (((m_Header.flags) & ArchiveFlags.BlockInfoNeedPaddingAtStart) != 0)
                    temp = (temp + 15) & 0xFFFFFFF0;
                dataOffset += temp;
            }

            reader.Position = Offset + dataOffset;

            //
            using var blocksStream = CreateBlocksStream(path);
            var unavailable = ReadBlocks(reader, blocksStream, game);
            ReadFiles(blocksStream, path, unavailable);
        }

        private void ReadBlocksInfoAndDirectory(FileReader reader, GameType game)
        {
            byte[] blocksInfoBytes = reader.ReadBytes((int)m_Header.compressedBlocksInfoSize);

            MemoryStream blocksInfoUncompressedStream = new MemoryStream();
            if (((int)m_Header.flags & 0x3F) != 0)
            {
                // compressed + encrypted
                VFSUtils.DecryptBlock(blocksInfoBytes, game);

                var uncompressedSize = m_Header.uncompressedBlocksInfoSize;
                var blocksInfoBytesSpan = blocksInfoBytes.AsSpan(0, blocksInfoBytes.Length);
                var uncompressedBytes = ArrayPool<byte>.Shared.Rent((int)uncompressedSize);

                try
                {
                    var uncompressedBytesSpan = uncompressedBytes.AsSpan(0, (int)uncompressedSize);
                    // normal LZ4
                    var numWrite = LZ4.Instance.Decompress(blocksInfoBytesSpan, uncompressedBytesSpan);

                    if (numWrite != uncompressedSize)
                    {
                        throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
                    }
                    blocksInfoUncompressedStream = new MemoryStream(uncompressedBytesSpan.ToArray());
                } catch (Exception e)
                {
                    throw new IOException($"Lz4 decompression error {e.Message}");
                } finally
                {
                    ArrayPool<byte>.Shared.Return(uncompressedBytes);
                }
            } else
            {
                blocksInfoUncompressedStream = new MemoryStream(blocksInfoBytes);
            }

            // read
            using (var blocksInfoReader = new EndianBinaryReader(blocksInfoUncompressedStream))
            {
                reader.Endian = EndianType.BigEndian;
                m_BlocksInfo = VFSUtils.ReadBlocksInfos(blocksInfoReader, game);
                m_DirectoryInfo = VFSUtils.ReadDirectoryInfos(blocksInfoReader, game);
            }
        }

        private Stream CreateBlocksStream(string path)
        {
            Stream blocksStream;
            var uncompressedSizeSum = m_BlocksInfo.Sum(x => (long)x.uncompressedSize);
            Logger.Verbose($"Total size of decompressed blocks: 0x{uncompressedSizeSum:X8}");
            if (uncompressedSizeSum >= int.MaxValue)
                blocksStream = new FileStream(path + ".temp", FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            else
                blocksStream = new MemoryStream((int)uncompressedSizeSum);
            return blocksStream;
        }

        private List<PartialVfsStream.Range> ReadBlocks(FileReader reader, Stream blocksStream, GameType game)
        {
            var unavailable = new List<PartialVfsStream.Range>();
            for (int i = 0; i < m_BlocksInfo.Count; i++)
            {
                var blockInfo = m_BlocksInfo[i];
                var compressionType = (int)(blockInfo.flags & StorageBlockFlags.CompressionTypeMask);
                Logger.Verbose($"Block compression type {compressionType}");

                if (game == GameType.ArknightsEndfield && (blockInfo.flags & StorageBlockFlags.EndfieldEncrypted) != 0)
                {
                    if (blockInfo.compressedSize > reader.Remaining)
                    {
                        throw new EndOfStreamException($"Truncated Endfield VFS block {i}.");
                    }

                    reader.Position += blockInfo.compressedSize;
                    var start = blocksStream.Position;
                    var end = start + blockInfo.uncompressedSize;
                    unavailable.Add(new PartialVfsStream.Range(start, end, i));
                    // Keep file offsets intact. PartialVfsStream guards reads into these ranges.
                    blocksStream.SetLength(end);
                    blocksStream.Position = end;
                    continue;
                }

                switch (compressionType)
                {
                    case 0:
                        var size = checked((int)blockInfo.uncompressedSize);
                        var raw = reader.ReadBytes(size);
                        if (raw.Length != size)
                        {
                            throw new EndOfStreamException($"Truncated VFS block {i}.");
                        }
                        blocksStream.Write(raw);
                        break;
                    case 5:
                        var compressedSize = checked((int)blockInfo.compressedSize);
                        var uncompressedSize = checked((int)blockInfo.uncompressedSize);

                        var compressedBytes = ArrayPool<byte>.Shared.Rent(compressedSize);
                        var uncompressedBytes = ArrayPool<byte>.Shared.Rent(uncompressedSize);

                        var compressedBytesSpan = compressedBytes.AsSpan(0, compressedSize);
                        var uncompressedBytesSpan = uncompressedBytes.AsSpan(0, uncompressedSize);

                        try
                        {
                            reader.BaseStream.ReadExactly(compressedBytesSpan);

                            VFSUtils.DecryptBlock(compressedBytesSpan, game);

                            // LZ4Inv this time
                            var numWrite = LZ4Inv.Instance.Decompress(compressedBytesSpan, uncompressedBytesSpan);
                            if (numWrite != uncompressedSize)
                            {
                                throw new InvalidDataException($"VFS block {i} decompressed to {numWrite} bytes, expected {uncompressedSize}.");
                            }
                            blocksStream.Write(uncompressedBytesSpan);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(compressedBytes, true);
                            ArrayPool<byte>.Shared.Return(uncompressedBytes, true);
                        }

                        break;
                    default:
                        throw new Exception($"Unsupported block compression type {compressionType}");
                }
            }
            if (unavailable.Count != 0)
            {
                Logger.Warning($"{reader.FileName} has {unavailable.Count} encrypted Endfield VFS blocks with unavailable keys. Assets that use those blocks cannot be read.");
            }
            return unavailable;
        }

        private void ReadFiles(Stream blocksStream, string path, List<PartialVfsStream.Range> unavailable)
        {
            Logger.Verbose($"Writing files from blocks stream...");

            fileList = new List<StreamFile>();
            try
            {
                foreach (var node in m_DirectoryInfo)
                {
                    if (node.offset < 0 || node.size < 0 || node.offset > blocksStream.Length ||
                        node.size > blocksStream.Length - node.offset)
                    {
                        throw new InvalidDataException($"VFS entry {node.path} extends beyond its data.");
                    }

                    var end = node.offset + node.size;
                    var missing = unavailable.Where(range => range.Start < end && range.End > node.offset)
                        .Select(range => new PartialVfsStream.Range(Math.Max(0, range.Start - node.offset),
                            Math.Min(node.size, range.End - node.offset), range.BlockIndex)).ToArray();
                    var file = new StreamFile { path = node.path, fileName = Path.GetFileName(node.path) };
                    fileList.Add(file);
                    if (node.size < int.MaxValue)
                    {
                        file.stream = new MemoryStream((int)node.size);
                    }
                    else if (missing.Length != 0)
                    {
                        var tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
                        file.stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite,
                            FileShare.None, 4096, FileOptions.DeleteOnClose);
                    }
                    else
                    {
                        var extractPath = path + "_unpacked" + Path.DirectorySeparatorChar;
                        Directory.CreateDirectory(extractPath);
                        file.stream = new FileStream(extractPath + file.fileName, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
                    }
                    blocksStream.Position = node.offset;
                    blocksStream.CopyTo(file.stream, node.size);
                    file.stream.Position = 0;
                    if (missing.Length != 0)
                    {
                        file.stream = new PartialVfsStream(file.stream, missing);
                    }
                }
            }
            catch
            {
                foreach (var file in fileList)
                {
                    file.stream?.Dispose();
                }
                throw;
            }
        }
    }
}

using System.IO.Compression;
using System.Text;

namespace DotNet.Bundler.Archive;

internal enum ZipEntryKind
{
    File,
    Directory,
    Symlink
}

/// <summary>A single zip archive entry. <see cref="Content"/> is used for files.</summary>
internal sealed class ZipEntry
{
    /// <summary>Archive path in POSIX form ('/'-separated, relative, no leading './').</summary>
    internal string Name = "";
    internal ZipEntryKind Kind;
    internal int Mode;
    internal byte[] Content = [];
    /// <summary>
    /// 文件条目的可选流式载荷；设置后优先于 <see cref="Content"/>。
    /// 工厂必须返回报告 <see cref="Stream.Length"/> 的流：写出端逐条打开、
    /// 按声明长度经 Deflate 分块写入并释放，随后回填本地头的 CRC 与尺寸字段——
    /// 因此要求输出流可寻址。流式条目恒用 Deflate，stored 回退只适用缓冲的
    /// <see cref="Content"/>。尺寸顶到经典 zip 上限时自动升级 Zip64。
    /// </summary>
    internal Func<Stream>? OpenContent;
    internal string LinkTarget = "";
}

/// <summary>
/// Writes ZIP archives (local file headers + central directory + EOCD) with
/// Deflate compression. Entries carry Unix mode bits in the external
/// attributes field (version made by = Unix), so exec bits and symlinks
/// survive Info-ZIP-style extraction. Deterministic: a fixed DOS date-time
/// (1980-01-01 00:00) and caller-controlled entry order; names are UTF-8
/// (general purpose flag 11). Fields that exceed the classic 32-bit/16-bit
/// limits are upgraded to Zip64 the way <see cref="ZipArchive"/> does:
/// sentinel values in the small field plus the real value in a Zip64
/// extra field / EOCD64 record — archives under the limits stay
/// byte-identical to the classic layout.
/// </summary>
internal static class ZipWriter
{
    private const int DosEpochDate = (1 << 5) | 1; // 1980-01-01
    private const ushort Zip64Version = 45;
    private const ushort Zip64ExtraId = 0x0001;
    private const uint Zip64EocdSignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50;

    // deflate 的最坏膨胀率远低于 0.1%：声明长度距哨兵不足 2MiB 安全带的流式
    // 条目预写 Zip64 本地头——压缩后真超限时 extra 已就位；未超限时哨兵+extra
    // 存的也是真值，结构依然合法。
    private const long Zip64SafetyMargin = 2L * 1024 * 1024;

    internal static void Write(Stream output, IEnumerable<ZipEntry> entries)
    {
        var central = new List<(string Name, ZipEntry Entry, uint Crc, long Compressed, long Size, long LocalOffset, bool Stored, bool LocalZip64)>();
        var copyBuffer = new byte[81920];
        foreach (var entry in entries)
        {
            var name = entry.Name;
            if (entry.Kind == ZipEntryKind.Directory && !name.EndsWith("/", StringComparison.Ordinal))
            {
                name += "/";
            }
            var nameBytes = Encoding.UTF8.GetBytes(name);
            if (nameBytes.Length > ushort.MaxValue)
            {
                throw new ArgumentException($"Zip entry name exceeds the limit: '{name}'.");
            }
            if (entry.Kind == ZipEntryKind.File && entry.OpenContent is { } openContent)
            {
                WriteStreamedFile(output, copyBuffer, central, entry, name, nameBytes, openContent);
                continue;
            }
            var data = entry.Kind switch
            {
                ZipEntryKind.File => entry.Content,
                ZipEntryKind.Symlink => Encoding.UTF8.GetBytes(entry.LinkTarget),
                _ => []
            };
            byte[] compressed;
            bool stored = data.Length == 0;
            if (stored)
            {
                compressed = [];
            }
            else
            {
                using var deflateBuffer = new MemoryStream();
                using (var stream = new DeflateStream(deflateBuffer, CompressionLevel.Optimal, leaveOpen: true))
                {
                    stream.Write(data, 0, data.Length);
                }
                compressed = deflateBuffer.ToArray();
                stored = compressed.Length >= data.Length;
                if (stored)
                {
                    compressed = data;
                }
            }
            var crc = Crc32(data);
            var localOffset = output.Position;
            WriteLocalHeader(output, nameBytes, entry, stored, crc, data.Length, compressed.Length);
            output.Write(compressed, 0, compressed.Length);
            central.Add((name, entry, crc, compressed.Length, data.Length, localOffset, stored, false));
        }

        var centralOffset = output.Position;
        foreach (var (name, entry, crc, compressedLength, size, localOffset, stored, localZip64) in central)
        {
            WriteCentralHeader(output, Encoding.UTF8.GetBytes(name), entry, crc, compressedLength, size, localOffset, stored, localZip64);
        }
        var centralSize = output.Position - centralOffset;

        // 条目数顶 0xFFFF 或中央目录字段顶 0xFFFFFFFF 时升级为 Zip64 EOCD；
        // 经典 EOCD 里受影响字段写哨兵值，真值在 Zip64 记录中（与 ZipArchive 同）。
        var needsZip64Eocd = central.Count >= ushort.MaxValue
            || centralOffset >= uint.MaxValue
            || centralSize >= uint.MaxValue;
        if (needsZip64Eocd)
        {
            var eocd64Offset = output.Position;
            WriteZip64EndOfCentralDirectory(output, central.Count, centralSize, centralOffset);
            WriteZip64EndOfCentralDirectoryLocator(output, eocd64Offset);
        }
        WriteEndOfCentralDirectory(output, central.Count, centralSize, centralOffset);
    }

    private static void WriteLocalHeader(
        Stream output, byte[] nameBytes, ZipEntry entry, bool stored, uint crc,
        long size, long compressedSize, byte[]? zip64Extra = null)
    {
        var zip64 = zip64Extra != null;
        using var buffer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        buffer.Write(0x04034b50u);            // local file header signature
        buffer.Write((ushort)(zip64 ? Zip64Version : 20)); // version needed to extract
        buffer.Write((ushort)0x0800);         // flags: UTF-8 names
        buffer.Write((ushort)(stored ? 0 : 8)); // method: stored / deflate
        buffer.Write((ushort)0);              // mod time (00:00)
        buffer.Write((ushort)DosEpochDate);   // mod date (1980-01-01)
        buffer.Write(crc);
        buffer.Write(zip64 ? uint.MaxValue : (uint)compressedSize);
        buffer.Write(zip64 ? uint.MaxValue : (uint)size);
        buffer.Write((ushort)nameBytes.Length);
        buffer.Write((ushort)(zip64Extra?.Length ?? 0));
        buffer.Write(nameBytes);
        if (zip64Extra != null)
        {
            buffer.Write(zip64Extra);
        }
    }

    // 本地头要求 CRC 与两个尺寸先于载荷写入；流式条目先写占位头、量出真值后回填——
    // 输出字节与预先已知长度的写法完全同构。Zip64 形态下 32 位字段保持哨兵，
    // 只回填 CRC 与 extra 内的压缩长槽位。
    private static void WriteStreamedFile(
        Stream output, byte[] copyBuffer,
        List<(string Name, ZipEntry Entry, uint Crc, long Compressed, long Size, long LocalOffset, bool Stored, bool LocalZip64)> central,
        ZipEntry entry, string name, byte[] nameBytes, Func<Stream> openContent)
    {
        using var content = openContent();
        var contentLength = content.Length;
        if (!output.CanSeek)
        {
            throw new InvalidOperationException(
                "Streamed zip entries require a seekable output stream so the local header can be patched.");
        }
        var zip64 = contentLength >= (long)uint.MaxValue - Zip64SafetyMargin;
        var localOffset = output.Position;
        WriteLocalHeader(
            output, nameBytes, entry, stored: false, crc: 0,
            size: zip64 ? contentLength : 0,
            compressedSize: 0,
            zip64Extra: zip64 ? Zip64LocalExtra(contentLength, 0) : null);
        var dataStart = output.Position;
        var crc = new Crc32Computer();
        var remaining = contentLength;
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            while (remaining > 0)
            {
                var read = content.Read(copyBuffer, 0, (int)Math.Min(copyBuffer.Length, remaining));
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        $"Zip entry '{entry.Name}' stream ended after {contentLength - remaining} of {contentLength} bytes.");
                }
                crc.Update(copyBuffer, 0, read);
                deflate.Write(copyBuffer, 0, read);
                remaining -= read;
            }
        }
        var compressedLength = output.Position - dataStart;
        PatchLocalHeader(output, localOffset, nameBytes.Length, crc.Value, compressedLength, contentLength, zip64);
        central.Add((name, entry, crc.Value, compressedLength, contentLength, localOffset, false, zip64));
    }

    // 回到刚写的本地头 CRC 字段处，回填实测字段。
    // 经典形态：crc+压缩长+原始长三个 u32；Zip64 形态：crc + extra 内压缩长 u64
    // （extra 里的原始长占位时已写真值，不必再补）。
    private static void PatchLocalHeader(
        Stream output, long localOffset, int nameBytesLength,
        uint crc, long compressedSize, long size, bool zip64)
    {
        var end = output.Position;
        using var patch = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        if (zip64)
        {
            output.Position = localOffset + 14;
            patch.Write(crc);
            output.Position = localOffset + 30 + nameBytesLength + 12;
            patch.Write(compressedSize);
        }
        else
        {
            output.Position = localOffset + 14;
            patch.Write(crc);
            patch.Write((uint)compressedSize);
            patch.Write((uint)size);
        }
        output.Position = end;
    }

    // Zip64 本地 extra 字段：header id 0x0001 + 数据长度 16 + u64 原始长 + u64 压缩长。
    private static byte[] Zip64LocalExtra(long size, long compressedSize)
    {
        var extra = new byte[20];
        using var buffer = new BinaryWriter(new MemoryStream(extra));
        buffer.Write(Zip64ExtraId);
        buffer.Write((ushort)16);
        buffer.Write(size);
        buffer.Write(compressedSize);
        return extra;
    }

    private static void WriteCentralHeader(
        Stream output, byte[] nameBytes, ZipEntry entry, uint crc, long compressedSize, long size, long localOffset,
        bool stored, bool localHeaderZip64)
    {
        // Unix mode (S_IF* | perms) in the high word of external attributes;
        // low word stays zero so DOS-attribute tools see a plain file.
        var unixMode = entry.Kind switch
        {
            ZipEntryKind.Directory => 16877 /* 040755 */,
            ZipEntryKind.Symlink => 41471 /* 0120777 */,
            _ => 32768 /* 0100000 S_IFREG */ | (entry.Mode & 0xFFF)
        };
        // 中央目录的 Zip64 extra 只装溢出的字段，顺序固定：原始长/压缩长/本地偏移。
        // 本地头已按 Zip64 写的条目（流式预升级）在此强制同构——哨兵+extra，
        // 与本地头逐字段一致，按版本判格式的读端不会误判。
        var sizeOverflow = localHeaderZip64 || size >= uint.MaxValue;
        var compressedOverflow = localHeaderZip64 || compressedSize >= uint.MaxValue;
        var offsetOverflow = localOffset >= uint.MaxValue;
        var zip64 = sizeOverflow || compressedOverflow || offsetOverflow;
        byte[]? extra = null;
        if (zip64)
        {
            var extraCount = (sizeOverflow ? 1 : 0) + (compressedOverflow ? 1 : 0) + (offsetOverflow ? 1 : 0);
            using var extraStream = new MemoryStream(4 + extraCount * 8);
            using (var extraWriter = new BinaryWriter(extraStream))
            {
                extraWriter.Write(Zip64ExtraId);
                extraWriter.Write((ushort)(extraCount * 8));
                if (sizeOverflow)
                {
                    extraWriter.Write(size);
                }
                if (compressedOverflow)
                {
                    extraWriter.Write(compressedSize);
                }
                if (offsetOverflow)
                {
                    extraWriter.Write(localOffset);
                }
            }
            extra = extraStream.ToArray();
        }
        using var buffer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        buffer.Write(0x02014b50u);            // central file header signature
        buffer.Write((ushort)((3 << 8) | (zip64 ? Zip64Version : 20))); // version made by: Unix
        buffer.Write((ushort)(zip64 ? Zip64Version : 20)); // version needed to extract
        buffer.Write((ushort)0x0800);         // flags: UTF-8 names
        buffer.Write((ushort)(stored ? 0 : 8)); // method mirrors the local header
        buffer.Write((ushort)0);
        buffer.Write((ushort)DosEpochDate);
        buffer.Write(crc);
        buffer.Write(compressedOverflow ? uint.MaxValue : (uint)compressedSize);
        buffer.Write(sizeOverflow ? uint.MaxValue : (uint)size);
        buffer.Write((ushort)nameBytes.Length);
        buffer.Write((ushort)(extra?.Length ?? 0)); // extra
        buffer.Write((ushort)0);              // comment
        buffer.Write((ushort)0);              // disk number
        buffer.Write((ushort)0);              // internal attrs
        buffer.Write((uint)(unixMode << 16)); // external attrs: unix mode
        buffer.Write(offsetOverflow ? uint.MaxValue : (uint)localOffset);
        buffer.Write(nameBytes);
        if (extra != null)
        {
            buffer.Write(extra);
        }
    }

    private static void WriteZip64EndOfCentralDirectory(
        Stream output, int count, long centralSize, long centralOffset)
    {
        using var buffer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        buffer.Write(Zip64EocdSignature);
        buffer.Write((ulong)44);            // 本字段之后的记录长度
        buffer.Write((ushort)Zip64Version); // version made by
        buffer.Write((ushort)Zip64Version); // version needed to extract
        buffer.Write(0u);                   // 本盘号
        buffer.Write(0u);                   // 中央目录起始盘号
        buffer.Write((ulong)count);         // 本盘条目数
        buffer.Write((ulong)count);         // 条目总数
        buffer.Write((ulong)centralSize);
        buffer.Write((ulong)centralOffset);
    }

    private static void WriteZip64EndOfCentralDirectoryLocator(Stream output, long eocd64Offset)
    {
        using var buffer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        buffer.Write(Zip64LocatorSignature);
        buffer.Write(0u);                   // EOCD64 所在盘号
        buffer.Write((ulong)eocd64Offset);
        buffer.Write(1u);                   // 总盘数
    }

    private static void WriteEndOfCentralDirectory(
        Stream output, int count, long centralSize, long centralOffset)
    {
        using var buffer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        buffer.Write(0x06054b50u);
        buffer.Write((ushort)0);
        buffer.Write((ushort)0);
        buffer.Write((ushort)Math.Min(count, ushort.MaxValue));
        buffer.Write((ushort)Math.Min(count, ushort.MaxValue));
        buffer.Write((uint)Math.Min(centralSize, uint.MaxValue));
        buffer.Write((uint)Math.Min(centralOffset, uint.MaxValue));
        buffer.Write((ushort)0);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = new Crc32Computer();
        crc.Update(data, 0, data.Length);
        return crc.Value;
    }

    // 增量式表驱动 CRC-32（IEEE 802.3），缓冲与流式路径共用，
    // 结果与经典逐位循环完全一致。
    private sealed class Crc32Computer
    {
        private static readonly uint[] Table = BuildTable();
        private uint _crc = 0xFFFFFFFFu;

        internal void Update(byte[] buffer, int offset, int count)
        {
            var crc = _crc;
            var end = offset + count;
            for (var i = offset; i < end; i++)
            {
                crc = (crc >> 8) ^ Table[(crc ^ buffer[i]) & 0xFF];
            }
            _crc = crc;
        }

        internal uint Value => ~_crc;

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (var i = 0u; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
                }
                table[i] = c;
            }
            return table;
        }
    }
}

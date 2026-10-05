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
    /// Optional streamed payload for file entries; when set it takes precedence
    /// over <see cref="Content"/>. The factory must return a stream reporting
    /// <see cref="Stream.Length"/>; the writer opens it per entry, streams
    /// exactly that many bytes through Deflate and disposes it, patching the
    /// local header with the measured CRC and sizes afterwards — a seekable
    /// output stream is required. Streamed entries always use Deflate; the
    /// stored fallback only applies to buffered <see cref="Content"/>.
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
/// (general purpose flag 11).
/// </summary>
internal static class ZipWriter
{
    private const int DosEpochDate = (1 << 5) | 1; // 1980-01-01

    internal static void Write(Stream output, IEnumerable<ZipEntry> entries)
    {
        var central = new List<(string Name, ZipEntry Entry, uint Crc, long Compressed, long Size, long LocalOffset, bool Stored)>();
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
            central.Add((name, entry, crc, compressed.Length, data.Length, localOffset, stored));
        }

        var centralOffset = output.Position;
        foreach (var (name, entry, crc, compressedLength, size, localOffset, stored) in central)
        {
            WriteCentralHeader(output, Encoding.UTF8.GetBytes(name), entry, crc, compressedLength, size, localOffset, stored);
        }
        var centralSize = output.Position - centralOffset;
        if (central.Count > ushort.MaxValue || centralOffset > uint.MaxValue || centralSize > uint.MaxValue)
        {
            throw new InvalidOperationException(
                "Zip64 archives are not supported by this writer; the payload exceeds the classic zip limits.");
        }
        WriteEndOfCentralDirectory(output, central.Count, centralSize, centralOffset);
    }

    private static void WriteLocalHeader(
        Stream output, byte[] nameBytes, ZipEntry entry, bool stored, uint crc, int size, int compressedSize)
    {
        using var buffer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        buffer.Write(0x04034b50u);            // local file header signature
        buffer.Write((ushort)20);             // version needed to extract
        buffer.Write((ushort)0x0800);         // flags: UTF-8 names
        buffer.Write((ushort)(stored ? 0 : 8)); // method: stored / deflate
        buffer.Write((ushort)0);              // mod time (00:00)
        buffer.Write((ushort)DosEpochDate);   // mod date (1980-01-01)
        buffer.Write(crc);
        buffer.Write((uint)compressedSize);
        buffer.Write((uint)size);
        buffer.Write((ushort)nameBytes.Length);
        buffer.Write((ushort)0);              // extra field length
        buffer.Write(nameBytes);
    }

    // The local header needs CRC and both sizes before the payload; streamed
    // entries write a placeholder first and patch it once the measured values
    // are known — output bytes stay identical to an upfront-known header.
    private static void WriteStreamedFile(
        Stream output, byte[] copyBuffer,
        List<(string Name, ZipEntry Entry, uint Crc, long Compressed, long Size, long LocalOffset, bool Stored)> central,
        ZipEntry entry, string name, byte[] nameBytes, Func<Stream> openContent)
    {
        using var content = openContent();
        var contentLength = content.Length;
        if (contentLength > uint.MaxValue)
        {
            throw new InvalidOperationException(
                $"Zip entry '{entry.Name}' exceeds the classic zip per-entry limit; Zip64 archives are not supported by this writer.");
        }
        if (!output.CanSeek)
        {
            throw new InvalidOperationException(
                "Streamed zip entries require a seekable output stream so the local header can be patched.");
        }
        var localOffset = output.Position;
        WriteLocalHeader(output, nameBytes, entry, stored: false, crc: 0, size: 0, compressedSize: 0);
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
        if (compressedLength > uint.MaxValue)
        {
            throw new InvalidOperationException(
                $"Zip entry '{entry.Name}' compressed payload exceeds the classic zip limit; Zip64 archives are not supported by this writer.");
        }
        PatchLocalHeader(output, localOffset, crc.Value, compressedLength, contentLength);
        central.Add((name, entry, crc.Value, compressedLength, contentLength, localOffset, false));
    }

    // Rewinds to the CRC field of the just-written local header and fills in
    // the three measured fields (crc / compressed size / uncompressed size).
    private static void PatchLocalHeader(Stream output, long localOffset, uint crc, long compressedSize, long size)
    {
        var end = output.Position;
        output.Position = localOffset + 14;
        using (var patch = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
        {
            patch.Write(crc);
            patch.Write((uint)compressedSize);
            patch.Write((uint)size);
        }
        output.Position = end;
    }

    private static void WriteCentralHeader(
        Stream output, byte[] nameBytes, ZipEntry entry, uint crc, long compressedSize, long size, long localOffset,
        bool stored)
    {
        // Unix mode (S_IF* | perms) in the high word of external attributes;
        // low word stays zero so DOS-attribute tools see a plain file.
        var unixMode = entry.Kind switch
        {
            ZipEntryKind.Directory => 16877 /* 040755 */,
            ZipEntryKind.Symlink => 41471 /* 0120777 */,
            _ => 32768 /* 0100000 S_IFREG */ | (entry.Mode & 0xFFF)
        };
        using var buffer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        buffer.Write(0x02014b50u);            // central file header signature
        buffer.Write((ushort)((3 << 8) | 20)); // version made by: Unix, 2.0
        buffer.Write((ushort)20);             // version needed to extract
        buffer.Write((ushort)0x0800);         // flags: UTF-8 names
        buffer.Write((ushort)(stored ? 0 : 8)); // method mirrors the local header
        buffer.Write((ushort)0);
        buffer.Write((ushort)DosEpochDate);
        buffer.Write(crc);
        buffer.Write((uint)compressedSize);
        buffer.Write((uint)size);
        buffer.Write((ushort)nameBytes.Length);
        buffer.Write((ushort)0);              // extra
        buffer.Write((ushort)0);              // comment
        buffer.Write((ushort)0);              // disk number
        buffer.Write((ushort)0);              // internal attrs
        buffer.Write((uint)(unixMode << 16)); // external attrs: unix mode
        buffer.Write((uint)localOffset);
        buffer.Write(nameBytes);
    }

    private static void WriteEndOfCentralDirectory(
        Stream output, int count, long centralSize, long centralOffset)
    {
        using var buffer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        buffer.Write(0x06054b50u);
        buffer.Write((ushort)0);
        buffer.Write((ushort)0);
        buffer.Write((ushort)count);
        buffer.Write((ushort)count);
        buffer.Write((uint)centralSize);
        buffer.Write((uint)centralOffset);
        buffer.Write((ushort)0);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = new Crc32Computer();
        crc.Update(data, 0, data.Length);
        return crc.Value;
    }

    // Incremental table-driven CRC-32 (IEEE 802.3) shared by the buffered and
    // streamed paths — identical results to the classic bit loop.
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

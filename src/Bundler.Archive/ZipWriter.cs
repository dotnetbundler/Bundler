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
        var central = new List<(string Name, ZipEntry Entry, uint Crc, int Compressed, long LocalOffset, bool Stored)>();
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
            central.Add((name, entry, crc, compressed.Length, localOffset, stored));
        }

        var centralOffset = output.Position;
        foreach (var (name, entry, crc, compressedLength, localOffset, stored) in central)
        {
            WriteCentralHeader(output, Encoding.UTF8.GetBytes(name), entry, crc, compressedLength, localOffset, stored);
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

    private static void WriteCentralHeader(
        Stream output, byte[] nameBytes, ZipEntry entry, uint crc, long compressedSize, long localOffset,
        bool stored)
    {
        var size = entry.Kind switch
        {
            ZipEntryKind.File => entry.Content.Length,
            ZipEntryKind.Symlink => Encoding.UTF8.GetByteCount(entry.LinkTarget),
            _ => 0
        };
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
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return ~crc;
    }
}

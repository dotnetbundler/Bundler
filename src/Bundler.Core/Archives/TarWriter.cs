using System.Text;

namespace DotNet.Bundler.Core;

internal enum TarEntryKind
{
    File,
    Directory,
    Symlink,

    /// <summary>pax extended header; <see cref="TarEntry.Content"/> holds the records block.</summary>
    PaxHeader
}

/// <summary>A single ustar archive entry. <see cref="Content"/> is used for files only.</summary>
internal sealed class TarEntry
{
    /// <summary>Archive path in POSIX form ('/'-separated, e.g. "./usr/lib/app").</summary>
    internal string Name = "";
    internal TarEntryKind Kind;
    internal int Mode;
    internal byte[] Content = [];
    /// <summary>
    /// Optional streamed payload for file entries; when set it takes precedence
    /// over <see cref="Content"/>. The factory must return a stream reporting
    /// <see cref="Stream.Length"/>; the writer opens it per entry, writes
    /// exactly that many bytes and disposes it.
    /// </summary>
    internal Func<Stream>? OpenContent;
    internal string LinkTarget = "";

    /// <summary>
    /// Optional pax extended-header records written as an 'x'-type entry
    /// immediately before this entry (e.g. apk's APK-TOOLS.checksum.SHA1).
    /// </summary>
    internal IReadOnlyList<KeyValuePair<string, string>>? PaxRecords;
}

/// <summary>
/// Writes POSIX ustar archives. Deterministic: uid/gid 0 ("root"), a fixed
/// mtime (<see cref="EntryMtime"/>), caller-controlled entry order. Long names
/// use the ustar prefix field; entries not expressible in ustar are rejected.
/// </summary>
internal static class TarWriter
{
    // Fixed mtime keeps archives byte-identical across builds; 1980-01-01 UTC
    // instead of the Unix epoch because linters (e.g. lintian's
    // package-contains-ancient-file) flag dates at/below the mid-70s.
    internal const long EntryMtime = 315532800L;

    internal static void Write(Stream output, IEnumerable<TarEntry> entries) =>
        Write(output, entries, omitEndOfArchive: false);

    /// <summary>
    /// Writes the entries; <paramref name="omitEndOfArchive"/> drops the final
    /// pair of zero blocks for tar fragments that continue inside a larger
    /// container (apk signature/control gzip streams never carry the end marker).
    /// </summary>
    internal static void Write(Stream output, IEnumerable<TarEntry> entries, bool omitEndOfArchive)
    {
        var header = new byte[512];
        var zeroBlock = new byte[512];
        var copyBuffer = new byte[81920];
        foreach (var entry in entries)
        {
            if (entry.PaxRecords is { Count: > 0 } paxRecords)
            {
                WriteEntry(output, header, zeroBlock, copyBuffer, PaxEntry(entry.Name, paxRecords));
            }
            WriteEntry(output, header, zeroBlock, copyBuffer, entry);
        }
        if (!omitEndOfArchive)
        {
            output.Write(zeroBlock, 0, zeroBlock.Length);
            output.Write(zeroBlock, 0, zeroBlock.Length);
        }
    }

    private static void WriteEntry(Stream output, byte[] header, byte[] zeroBlock, byte[] copyBuffer, TarEntry entry)
    {
        Stream? contentStream = null;
        try
        {
            var contentLength = 0L;
            if (entry.Kind is TarEntryKind.File or TarEntryKind.PaxHeader)
            {
                if (entry.OpenContent is { } openContent)
                {
                    contentStream = openContent();
                    contentLength = contentStream.Length;
                }
                else
                {
                    contentLength = entry.Content.Length;
                }
            }

            Array.Clear(header, 0, header.Length);
            var name = entry.Name;
            if (entry.Kind == TarEntryKind.Directory && !name.EndsWith("/", StringComparison.Ordinal))
            {
                name += "/";
            }
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var prefix = "";
            var shortName = name;
            if (nameBytes.Length > 100)
            {
                (prefix, shortName) = SplitLongName(name);
            }
            WriteString(header, 0, 100, shortName);
            WriteOctal(header, 100, 8, entry.Mode);
            WriteOctal(header, 108, 8, 0);               // uid
            WriteOctal(header, 116, 8, 0);               // gid
            WriteOctal(header, 124, 12, contentLength);
            WriteOctal(header, 136, 12, EntryMtime);     // mtime
            for (var i = 148; i < 156; i++)
            {
                header[i] = (byte)' ';
            }
            header[156] = entry.Kind switch
            {
                TarEntryKind.Directory => (byte)'5',
                TarEntryKind.Symlink => (byte)'2',
                TarEntryKind.PaxHeader => (byte)'x',
                _ => (byte)'0',
            };
            if (entry.Kind == TarEntryKind.Symlink)
            {
                WriteString(header, 157, 100, entry.LinkTarget);
            }
            WriteString(header, 257, 6, "ustar");
            header[263] = (byte)'0';
            header[264] = (byte)'0';
            WriteString(header, 265, 32, "root");
            WriteString(header, 297, 32, "root");
            WriteOctal(header, 329, 8, 0);               // devmajor
            WriteOctal(header, 337, 8, 0);               // devminor
            if (prefix.Length > 0)
            {
                WriteString(header, 345, 155, prefix);
            }
            var checksum = header.Sum(b => (int)b);
            WriteChecksum(header, checksum);
            output.Write(header, 0, header.Length);
            if (contentLength > 0)
            {
                if (contentStream != null)
                {
                    var remaining = contentLength;
                    while (remaining > 0)
                    {
                        var read = contentStream.Read(
                            copyBuffer, 0, (int)Math.Min(copyBuffer.Length, remaining));
                        if (read == 0)
                        {
                            throw new EndOfStreamException(
                                $"Tar entry '{entry.Name}' stream ended after {contentLength - remaining} of {contentLength} bytes.");
                        }
                        output.Write(copyBuffer, 0, read);
                        remaining -= read;
                    }
                }
                else
                {
                    output.Write(entry.Content, 0, entry.Content.Length);
                }
                var remainder = contentLength % 512;
                if (remainder != 0)
                {
                    output.Write(zeroBlock, 0, (int)(512 - remainder));
                }
            }
        }
        finally
        {
            contentStream?.Dispose();
        }
    }

    // pax extended headers carry per-entry metadata that ustar cannot express;
    // apk uses them for per-file APK-TOOLS.checksum.SHA1 digests.
    private static TarEntry PaxEntry(string name, IReadOnlyList<KeyValuePair<string, string>> records) =>
        new()
        {
            Name = name,
            Kind = TarEntryKind.PaxHeader,
            Mode = 420, // 0644
            Content = BuildPaxData(records)
        };

    // Each record is "<len> <key>=<value>\n" where <len> counts itself.
    private static byte[] BuildPaxData(IReadOnlyList<KeyValuePair<string, string>> records)
    {
        using var buffer = new MemoryStream();
        foreach (var record in records)
        {
            var tail = Encoding.UTF8.GetBytes(record.Key + "=" + record.Value + "\n");
            // len = digits(len) + 1 (space) + tail bytes; solve for the digit count.
            var length = 0;
            for (var d = 1; ; d++)
            {
                var candidate = tail.Length + 1 + d;
                if (candidate.ToString().Length == d)
                {
                    length = candidate;
                    break;
                }
            }
            var prefix = Encoding.ASCII.GetBytes(length + " ");
            buffer.Write(prefix, 0, prefix.Length);
            buffer.Write(tail, 0, tail.Length);
        }
        return buffer.ToArray();
    }

    // Splits a path into ustar prefix/name so each fits its field.
    private static (string Prefix, string Name) SplitLongName(string name)
    {
        var index = name.Length;
        while (index > 0 && (index = name.LastIndexOf('/', index - 1)) > 0)
        {
            var prefix = name.Substring(0, index);
            var leaf = name.Substring(index + 1);
            if (Encoding.UTF8.GetByteCount(prefix) <= 155 &&
                Encoding.UTF8.GetByteCount(leaf) <= 100 && leaf.Length > 0)
            {
                return (prefix, leaf);
            }
        }
        throw new ArgumentException($"Tar entry path exceeds the ustar limit: '{name}'.");
    }

    private static void WriteString(byte[] buffer, int offset, int width, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > width)
        {
            throw new ArgumentException($"Tar header field overflows: '{value}'.");
        }
        Array.Copy(bytes, 0, buffer, offset, bytes.Length);
    }

    private static void WriteOctal(byte[] buffer, int offset, int width, long value)
    {
        var text = Convert.ToString(value, 8).PadLeft(width - 1, '0');
        var bytes = Encoding.ASCII.GetBytes(text);
        if (bytes.Length > width - 1)
        {
            throw new ArgumentException($"Tar numeric field overflows: {value}.");
        }
        Array.Copy(bytes, 0, buffer, offset, bytes.Length);
        buffer[offset + width - 1] = 0;
    }

    private static void WriteChecksum(byte[] header, int checksum)
    {
        var bytes = Encoding.ASCII.GetBytes(Convert.ToString(checksum, 8).PadLeft(6, '0'));
        Array.Copy(bytes, 0, header, 148, 6);
        header[154] = 0;
        header[155] = (byte)' ';
    }
}

using System.Text;

namespace DotNet.Bundler.Deb;

internal enum TarEntryKind
{
    File,
    Directory,
    Symlink
}

/// <summary>A single ustar archive entry. <see cref="Content"/> is used for files only.</summary>
internal sealed class TarEntry
{
    /// <summary>Archive path in POSIX form ('/'-separated, e.g. "./usr/lib/app").</summary>
    internal string Name = "";
    internal TarEntryKind Kind;
    internal int Mode;
    internal byte[] Content = [];
    internal string LinkTarget = "";
}

/// <summary>
/// Writes POSIX ustar archives. Deterministic: uid/gid 0 ("root"), mtime 0,
/// caller-controlled entry order. Long names use the ustar prefix field;
/// entries not expressible in ustar are rejected.
/// </summary>
internal static class TarWriter
{
    internal static void Write(Stream output, IEnumerable<TarEntry> entries)
    {
        var header = new byte[512];
        var zeroBlock = new byte[512];
        foreach (var entry in entries)
        {
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
            WriteOctal(header, 124, 12, entry.Kind == TarEntryKind.File ? entry.Content.Length : 0);
            WriteOctal(header, 136, 12, 0);              // mtime
            for (var i = 148; i < 156; i++)
            {
                header[i] = (byte)' ';
            }
            header[156] = entry.Kind switch
            {
                TarEntryKind.Directory => (byte)'5',
                TarEntryKind.Symlink => (byte)'2',
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
            if (entry.Kind == TarEntryKind.File && entry.Content.Length > 0)
            {
                output.Write(entry.Content, 0, entry.Content.Length);
                var remainder = entry.Content.Length % 512;
                if (remainder != 0)
                {
                    output.Write(zeroBlock, 0, 512 - remainder);
                }
            }
        }
        output.Write(zeroBlock, 0, zeroBlock.Length);
        output.Write(zeroBlock, 0, zeroBlock.Length);
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

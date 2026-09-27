using System.IO.Compression;
using System.Text;

namespace DotNet.Bundler.Deb;

/// <summary>A tar entry read back from an archive, used by the test suite.</summary>
internal sealed class TarReadEntry
{
    internal string Name = "";
    internal TarEntryKind Kind;
    internal int Mode;
    internal byte[] Content = [];
    internal string LinkTarget = "";
}

/// <summary>
/// Minimal reader for the ar/tar structures the writer produces; lets tests assert
/// package structure without host tools (ar/tar/dpkg-deb may be absent).
/// </summary>
internal static class DebPackageReader
{
    internal static List<ArMember> ReadAr(string path)
    {
        var members = new List<ArMember>();
        using var stream = File.OpenRead(path);
        var magic = new byte[8];
        ReadFully(stream, magic, 0, 8);
        if (Encoding.ASCII.GetString(magic) != "!<arch>\n")
        {
            throw new InvalidDataException("Not an ar archive: " + path);
        }
        var header = new byte[60];
        while (stream.Read(header, 0, 1) == 1)
        {
            ReadFully(stream, header, 1, 59);
            var name = Encoding.ASCII.GetString(header, 0, 16).TrimEnd();
            if (name.EndsWith("/", StringComparison.Ordinal))
            {
                name = name.Substring(0, name.Length - 1);
            }
            var size = int.Parse(
                Encoding.ASCII.GetString(header, 48, 10).Trim(),
                System.Globalization.CultureInfo.InvariantCulture);
            var content = new byte[size];
            ReadFully(stream, content, 0, size);
            if ((size & 1) == 1)
            {
                stream.ReadByte();
            }
            members.Add(new ArMember(name, content));
        }
        return members;
    }

    internal static List<TarReadEntry> ReadTar(byte[] tarBytes)
    {
        var entries = new List<TarReadEntry>();
        var offset = 0;
        while (offset + 512 <= tarBytes.Length)
        {
            var header = tarBytes.Skip(offset).Take(512).ToArray();
            if (header.All(b => b == 0))
            {
                break;
            }
            var name = ReadString(header, 0, 100);
            var prefix = ReadString(header, 345, 155);
            if (prefix.Length > 0)
            {
                name = prefix + "/" + name;
            }
            var size = (int)ReadOctal(header, 124, 12);
            var entry = new TarReadEntry
            {
                Name = name.TrimEnd('/'),
                Kind = header[156] switch
                {
                    (byte)'5' => TarEntryKind.Directory,
                    (byte)'2' => TarEntryKind.Symlink,
                    _ => TarEntryKind.File
                },
                Mode = (int)ReadOctal(header, 100, 8),
                LinkTarget = ReadString(header, 157, 100)
            };
            offset += 512;
            if (entry.Kind == TarEntryKind.File)
            {
                entry.Content = tarBytes.Skip(offset).Take(size).ToArray();
                offset += ((size + 511) / 512) * 512;
            }
            entries.Add(entry);
        }
        return entries;
    }

    internal static byte[] Ungzip(byte[] gzipBytes)
    {
        using var input = new MemoryStream(gzipBytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static string ReadString(byte[] buffer, int offset, int width)
    {
        var length = 0;
        while (length < width && buffer[offset + length] != 0)
        {
            length++;
        }
        return Encoding.UTF8.GetString(buffer, offset, length);
    }

    private static long ReadOctal(byte[] buffer, int offset, int width)
    {
        var text = Encoding.ASCII.GetString(buffer, offset, width).Trim('\0', ' ');
        return text.Length == 0 ? 0 : Convert.ToInt64(text, 8);
    }

    private static void ReadFully(Stream stream, byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            var read = stream.Read(buffer, offset, count);
            if (read == 0)
            {
                throw new EndOfStreamException("Truncated ar archive.");
            }
            offset += read;
            count -= read;
        }
    }
}

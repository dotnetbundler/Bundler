using System.IO.Compression;
using System.Text;

namespace DotNet.Bundler.AlpineApk;

/// <summary>
/// Read-back helper used by tests and verification tooling: splits an apk into
/// its concatenated gzip members and parses each member's tar entries,
/// including pax extended headers.
/// </summary>
internal static class ApkPackageReader
{
    internal sealed class Entry
    {
        internal string Name = "";
        internal char TypeFlag;
        internal int Mode;
        internal byte[] Content = [];
        internal string LinkTarget = "";
        internal Dictionary<string, string> Pax = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Decompresses every concatenated gzip member in order and returns the raw
    /// (still tar-framed) content of each. Gzip members carry no length field
    /// and the framework inflater transparently continues across member
    /// boundaries, so members are located by their "\x1f\x8b\x08" magic and
    /// confirmed by decompressing each candidate through end of file; member i's
    /// tar is the prefix of that decompression longer than the next member's.
    /// </summary>
    internal static List<int> GzipMemberOffsets(byte[] apk)
    {
        var starts = new List<int>();
        for (var offset = 0; offset + 3 <= apk.Length; offset++)
        {
            if (apk[offset] != 0x1f || apk[offset + 1] != 0x8b || apk[offset + 2] != 0x08)
            {
                continue;
            }
            if (TryDecompressFrom(apk, offset) is not null)
            {
                starts.Add(offset);
            }
        }
        return starts;
    }

    internal static List<byte[]> SplitGzipStreams(byte[] apk)
    {
        var starts = GzipMemberOffsets(apk);
        var segments = new List<byte[]>();
        for (var i = 0; i < starts.Count; i++)
        {
            var decompressed = TryDecompressFrom(apk, starts[i])!;
            var length = i + 1 < starts.Count
                ? decompressed.Length - TryDecompressFrom(apk, starts[i + 1])!.Length
                : decompressed.Length;
            segments.Add(decompressed.Take(length).ToArray());
        }
        return segments;
    }

    private static byte[]? TryDecompressFrom(byte[] apk, int offset)
    {
        try
        {
            using var input = new MemoryStream(apk, offset, apk.Length - offset);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    internal static List<Entry> ReadTar(byte[] tar)
    {
        var entries = new List<Entry>();
        var offset = 0;
        var pendingPax = new Dictionary<string, string>(StringComparer.Ordinal);
        while (offset + 512 <= tar.Length)
        {
            if (IsZeroBlock(tar, offset))
            {
                break; // end-of-archive zero block
            }
            var name = ReadString(tar, offset, 100);
            var prefix = ReadString(tar, offset + 345, 155);
            if (prefix.Length > 0)
            {
                name = prefix + "/" + name;
            }
            var mode = (int)ReadOctal(tar, offset + 100, 8);
            var size = (int)ReadOctal(tar, offset + 124, 12);
            var typeFlag = (char)tar[offset + 156];
            var linkTarget = ReadString(tar, offset + 157, 100);
            offset += 512;
            var content = new byte[size];
            Array.Copy(tar, offset, content, 0, size);
            offset += (size + 511) / 512 * 512;

            if (typeFlag == 'x')
            {
                pendingPax = ParsePax(content);
                continue;
            }
            entries.Add(new Entry
            {
                Name = name,
                TypeFlag = typeFlag,
                Mode = mode,
                Content = content,
                LinkTarget = linkTarget,
                Pax = pendingPax
            });
            pendingPax = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        return entries;
    }

    private static Dictionary<string, string> ParsePax(byte[] content)
    {
        var records = new Dictionary<string, string>(StringComparer.Ordinal);
        var text = Encoding.UTF8.GetString(content);
        var position = 0;
        while (position < text.Length)
        {
            var space = text.IndexOf(' ', position);
            if (space < 0 || !int.TryParse(text.Substring(position, space - position), out var length))
            {
                break;
            }
            var record = text.Substring(space + 1, length - (space - position) - 1).TrimEnd('\n');
            var equals = record.IndexOf('=');
            if (equals > 0)
            {
                records[record.Substring(0, equals)] = record.Substring(equals + 1);
            }
            position += length;
        }
        return records;
    }

    private static bool IsZeroBlock(byte[] buffer, int offset)
    {
        for (var i = offset; i < offset + 512; i++)
        {
            if (buffer[i] != 0)
            {
                return false;
            }
        }
        return true;
    }

    private static string ReadString(byte[] buffer, int offset, int width)
    {
        var end = offset;
        while (end < offset + width && buffer[end] != 0)
        {
            end++;
        }
        return Encoding.UTF8.GetString(buffer, offset, end - offset);
    }

    private static long ReadOctal(byte[] buffer, int offset, int width)
    {
        var value = 0L;
        for (var i = offset; i < offset + width; i++)
        {
            var c = buffer[i];
            if (c >= (byte)'0' && c <= (byte)'7')
            {
                value = value * 8 + (c - (byte)'0');
            }
        }
        return value;
    }

}

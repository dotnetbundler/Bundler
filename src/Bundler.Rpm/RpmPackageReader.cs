using System.Text;

namespace DotNet.Bundler.Rpm;

/// <summary>
/// Parses a .rpm written by <see cref="RpmPackageWriter"/> back into tag values
/// and payload entries. Used by tests to prove the writer produces a structure
/// rpm itself reads.
/// </summary>
internal sealed class RpmPackageReader
{
    public sealed class Header
    {
        public Dictionary<int, object> Tags { get; } = [];
        public int Tag(int tag) => Tags.TryGetValue(tag, out var v) && v is int[] i && i.Length > 0 ? i[0] : 0;
        public int[] Ints(int tag) => Tags.TryGetValue(tag, out var v) && v is int[] i ? i : [];
        public string Text(int tag) => Tags.TryGetValue(tag, out var v) && v is string[] s && s.Length > 0 ? s[0] : "";
        public string[] Strings(int tag) => Tags.TryGetValue(tag, out var v) && v is string[] s ? s : [];
    }

    public sealed class PayloadEntry
    {
        public string Path = "";
        public int Mode;
        public byte[] Data = [];
    }

    public string FileName = "";
    public Header Signature { get; } = new();
    public Header Main { get; } = new();
    public List<PayloadEntry> Payload { get; } = [];

    public static RpmPackageReader Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var reader = new RpmPackageReader();
        if (bytes.Length < 96 || bytes[0] != 0xED || bytes[1] != 0xAB ||
            bytes[2] != 0xEE || bytes[3] != 0xDB)
        {
            throw new InvalidDataException($"'{path}' is not an rpm (bad lead magic).");
        }
        reader.FileName = Encoding.ASCII.GetString(bytes, 10, 66).TrimEnd('\0');

        var offset = 96;
        reader.Signature.Tags.Merge(ParseHeader(bytes, ref offset, 62));
        offset = (offset + 7) & ~7;
        reader.Main.Tags.Merge(ParseHeader(bytes, ref offset, 63));

        var compressed = new byte[bytes.Length - offset];
        Buffer.BlockCopy(bytes, offset, compressed, 0, compressed.Length);
        reader.Payload.AddRange(ParseCpio(Inflate(compressed)));
        return reader;
    }

    private static Dictionary<int, object> ParseHeader(byte[] bytes, ref int offset, int regionTag)
    {
        if (bytes[offset] != 0x8e || bytes[offset + 1] != 0xad ||
            bytes[offset + 2] != 0xe8 || bytes[offset + 3] != 0x01)
        {
            throw new InvalidDataException($"Bad header magic at offset {offset}.");
        }
        var indexCount = Read32(bytes, offset + 8);
        var storeSize = Read32(bytes, offset + 12);
        var indexBase = offset + 16;
        var storeBase = indexBase + indexCount * 16;

        var tags = new Dictionary<int, object>();
        for (var i = 0; i < indexCount; i++)
        {
            var entry = indexBase + i * 16;
            var tag = Read32(bytes, entry);
            var type = Read32(bytes, entry + 4);
            var dataOffset = Read32(bytes, entry + 8);
            var count = Read32(bytes, entry + 12);
            var value = ReadValue(bytes, storeBase + dataOffset, type, count);
            if (tag == regionTag)
            {
                continue; // immutable-region trailer
            }
            tags[tag] = value;
        }
        offset = storeBase + storeSize;
        return tags;
    }

    private static object ReadValue(byte[] bytes, int offset, int type, int count)
    {
        return type switch
        {
            3 => ReadInt16s(bytes, offset, count),
            4 => ReadInt32s(bytes, offset, count),
            5 => new int[count],
            6 => new[] { ReadString(bytes, offset) },
            7 => ReadBin(bytes, offset, count),
            8 or 9 => ReadStrings(bytes, offset, count),
            _ => ReadBin(bytes, offset, count)
        };
    }

    private static int[] ReadInt16s(byte[] bytes, int offset, int count)
    {
        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = (bytes[offset + i * 2] << 8) | bytes[offset + i * 2 + 1];
        }
        return result;
    }

    private static int[] ReadInt32s(byte[] bytes, int offset, int count)
    {
        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = Read32(bytes, offset + i * 4);
        }
        return result;
    }

    private static string[] ReadStrings(byte[] bytes, int offset, int count)
    {
        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = ReadString(bytes, offset);
            offset += Encoding.UTF8.GetByteCount(result[i]) + 1;
        }
        return result;
    }

    private static string ReadString(byte[] bytes, int offset)
    {
        var end = Array.IndexOf(bytes, (byte)0, offset);
        return Encoding.UTF8.GetString(bytes, offset, end - offset);
    }

    private static byte[] ReadBin(byte[] bytes, int offset, int count)
    {
        var result = new byte[count];
        Buffer.BlockCopy(bytes, offset, result, 0, count);
        return result;
    }

    private static List<PayloadEntry> ParseCpio(byte[] data)
    {
        var entries = new List<PayloadEntry>();
        var offset = 0;
        while (offset + 110 <= data.Length)
        {
            var magic = Encoding.ASCII.GetString(data, offset, 6);
            if (magic != "070701") break;
            int Field(int index) => Convert.ToInt32(Encoding.ASCII.GetString(data, offset + 6 + index * 8, 8), 16);
            var mode = Field(1);
            var fileSize = Field(6);
            var nameSize = Field(11);
            var nameStart = offset + 110;
            var name = Encoding.UTF8.GetString(data, nameStart, nameSize - 1);
            // cpio members carry the "./" payload prefix; normalize back to
            // the absolute filesystem path the header entries use.
            if (name.StartsWith("./", StringComparison.Ordinal))
            {
                name = name.Substring(1);
            }
            offset = nameStart + nameSize;
            offset = (offset + 3) & ~3;
            if (name == "TRAILER!!!") break;
            var payload = new byte[fileSize];
            Buffer.BlockCopy(data, offset, payload, 0, fileSize);
            offset += fileSize;
            offset = (offset + 3) & ~3;
            entries.Add(new PayloadEntry { Path = name, Mode = mode, Data = payload });
        }
        return entries;
    }

    private static byte[] Inflate(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var gzip = new System.IO.Compression.GZipStream(input,
            System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static int Read32(byte[] bytes, int offset) =>
        (bytes[offset] << 24) | (bytes[offset + 1] << 16) |
        (bytes[offset + 2] << 8) | bytes[offset + 3];
}

internal static class RpmReaderExtensions
{
    internal static void Merge(this Dictionary<int, object> target, Dictionary<int, object> source)
    {
        foreach (var pair in source) target[pair.Key] = pair.Value;
    }
}

using System.Text;

namespace DotNet.Bundler.Rpm;

/// <summary>
/// Serializes an RPM header (signature or main): intro (magic + counts),
/// a sorted index of 16-byte tag entries, and a data store holding the values.
/// The region tag (62 signature / 63 main) is an ordinary sorted index entry
/// whose value is a 16-byte trailer index entry appended at the end of the
/// store, pointing back at the index with a negative offset.
/// </summary>
internal static class RpmHeaderWriter
{
    internal const int TypeChar = 1;
    internal const int TypeInt8 = 2;
    internal const int TypeInt16 = 3;
    internal const int TypeInt32 = 4;
    internal const int TypeInt64 = 5;
    internal const int TypeString = 6;      // NUL-terminated
    internal const int TypeBin = 7;
    internal const int TypeStringArray = 8; // consecutive NUL-terminated strings
    internal const int TypeI18n = 9;        // like StringArray, one entry per locale

    internal sealed class Entry
    {
        internal int Tag;
        internal int Type;
        internal byte[] Data = [];
        internal int Count;                 // element count (strings → items, ints → items)
    }

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    internal static Entry Str(int tag, string value, int type = TypeString) => new()
    {
        Tag = tag,
        Type = type,
        Data = Utf8.GetBytes(value + "\0"),
        Count = 1
    };

    internal static Entry Int32s(int tag, params int[] values) => new()
    {
        Tag = tag,
        Type = TypeInt32,
        Data = Pack32(values),
        Count = values.Length
    };

    internal static Entry Int16s(int tag, params int[] values) => new()
    {
        Tag = tag,
        Type = TypeInt16,
        Data = Pack16(values),
        Count = values.Length
    };

    internal static Entry Strings(int tag, IReadOnlyList<string> values, int type = TypeStringArray)
    {
        var data = new List<byte>();
        foreach (var value in values)
        {
            data.AddRange(Utf8.GetBytes(value));
            data.Add(0);
        }
        return new Entry { Tag = tag, Type = type, Data = data.ToArray(), Count = values.Count };
    }

    internal static Entry Bin(int tag, byte[] bytes) => new()
    {
        Tag = tag,
        Type = TypeBin,
        Data = bytes,
        Count = bytes.Length
    };

    // regionTag is 62 for the signature header, 63 for the main header.
    internal static byte[] Write(IReadOnlyList<Entry> entries, int regionTag)
    {
        var indexCount = entries.Count + 1;

        // Trailer index entry: {regionTag, BIN, -indexBytes, 16}, appended last
        // in the store; the region tag's own index entry points at it.
        var trailer = new byte[16];
        Write32(trailer, 0, regionTag);
        Write32(trailer, 4, TypeBin);
        Write32(trailer, 8, -indexCount * 16);
        Write32(trailer, 12, 16);

        var region = Bin(regionTag, trailer);
        var sorted = entries.Concat([region]).OrderBy(e => e.Tag).ToList();

        var store = new MemoryStream();
        var offsets = new int[sorted.Count];
        // Non-region values first (in tag order); the trailer rides at the end.
        var ordered = sorted.Where(e => e != region)
            .Concat([region]).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            var alignment = entry.Type switch
            {
                TypeInt16 => 2,
                TypeInt32 => 4,
                TypeInt64 => 8,
                _ => 1
            };
            while (store.Position % alignment != 0)
            {
                store.WriteByte(0);
            }
            var index = sorted.IndexOf(entry);
            offsets[index] = (int)store.Position;
            store.Write(entry.Data, 0, entry.Data.Length);
        }
        var storeBytes = store.ToArray();

        var output = new MemoryStream(16 + indexCount * 16 + storeBytes.Length);
        output.Write(new byte[] { 0x8e, 0xad, 0xe8, 0x01, 0, 0, 0, 0 }, 0, 8);
        Write32To(output, indexCount);
        Write32To(output, storeBytes.Length);
        for (var i = 0; i < sorted.Count; i++)
        {
            Write32To(output, sorted[i].Tag);
            Write32To(output, sorted[i].Type);
            Write32To(output, offsets[i]);
            Write32To(output, sorted[i].Count);
        }
        output.Write(storeBytes, 0, storeBytes.Length);
        return output.ToArray();
    }

    private static byte[] Pack32(IReadOnlyList<int> values)
    {
        var bytes = new byte[values.Count * 4];
        for (var i = 0; i < values.Count; i++)
        {
            Write32(bytes, i * 4, values[i]);
        }
        return bytes;
    }

    private static byte[] Pack16(IReadOnlyList<int> values)
    {
        var bytes = new byte[values.Count * 2];
        for (var i = 0; i < values.Count; i++)
        {
            Write16(bytes, i * 2, values[i]);
        }
        return bytes;
    }

    internal static void Write32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void Write16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
    }

    private static void Write32To(Stream stream, int value)
    {
        var bytes = new byte[4];
        Write32(bytes, 0, value);
        stream.Write(bytes, 0, 4);
    }
}

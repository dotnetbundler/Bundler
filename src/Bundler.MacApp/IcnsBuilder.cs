using System.Text;

namespace DotNet.Bundler.MacApp;

internal static class IcnsBuilder
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] IcnsMagic = Encoding.ASCII.GetBytes("icns");

    // PNG-in-icns slot types: pixel edge -> four-letter OSType.
    private static readonly IReadOnlyDictionary<int, string> TypeByPixelSize =
        new Dictionary<int, string>
        {
            [32] = "ic11",
            [64] = "ic12",
            [128] = "ic07",
            [256] = "ic08",
            [512] = "ic09",
            [1024] = "ic10"
        };

    internal static IReadOnlyList<int> SupportedPixelSizes { get; } =
        TypeByPixelSize.Keys.OrderBy(size => size).ToArray();

    internal static byte[] BuildFromPngs(IReadOnlyList<byte[]> pngs)
    {
        if (pngs.Count == 0)
        {
            throw new ArgumentException("At least one PNG icon source is required.");
        }
        var chunks = new List<(string Type, byte[] Data)>();
        var usedSizes = new HashSet<int>();
        foreach (var png in pngs)
        {
            var (width, height) = ReadPngSize(png);
            if (width != height)
            {
                throw new InvalidDataException("An .icns icon source must be a square PNG.");
            }
            if (!TypeByPixelSize.TryGetValue(width, out var type))
            {
                throw new InvalidDataException(
                    $"Unsupported .icns source size {width}px; supported sizes: " +
                    string.Join(", ", SupportedPixelSizes) + ".");
            }
            if (!usedSizes.Add(width))
            {
                throw new InvalidDataException($"Duplicate .icns source size {width}px.");
            }
            chunks.Add((type, png));
        }

        var totalLength = 8 + chunks.Sum(chunk => 8 + chunk.Data.Length);
        using var output = new MemoryStream();
        output.Write(IcnsMagic, 0, IcnsMagic.Length);
        WriteBigEndian(output, totalLength);
        foreach (var (type, data) in chunks)
        {
            var typeBytes = Encoding.ASCII.GetBytes(type);
            output.Write(typeBytes, 0, typeBytes.Length);
            WriteBigEndian(output, 8 + data.Length);
            output.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    internal static (int Width, int Height) ReadPngSize(byte[] png)
    {
        if (png.Length < 24 ||
            !png.Take(PngSignature.Length).SequenceEqual(PngSignature) ||
            !(png[12] == 'I' && png[13] == 'H' && png[14] == 'D' && png[15] == 'R'))
        {
            throw new InvalidDataException("The icon source is not a PNG file.");
        }
        var width = ReadBigEndian(png, 16);
        var height = ReadBigEndian(png, 20);
        return (width, height);
    }

    private static void WriteBigEndian(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static int ReadBigEndian(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
}

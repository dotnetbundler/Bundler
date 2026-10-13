using System.IO.Compression;

namespace DotNet.Bundler.Core;

/// <summary>Deterministic gzip compression (.NET emits a zero mtime header).</summary>
internal static class Gzip
{
    internal static byte[] Compress(byte[] content)
    {
        using var buffer = new MemoryStream();
        CompressTo(content, buffer);
        return buffer.ToArray();
    }

    internal static void CompressTo(byte[] content, Stream output)
    {
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(content, 0, content.Length);
        }
    }
}

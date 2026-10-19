using System.IO.Compression;

namespace DotNet.Bundler.Core;

/// <summary>Deterministic gzip compression (.NET emits a zero mtime header).</summary>
internal static class Gzip
{
    /// <summary>
    /// 确定性写侧压缩参数的唯一出处：各写入器 tar/cpio→gzip→文件都经此包裹，
    /// 压缩级别与 leaveOpen 不再三处复写。
    /// </summary>
    internal static GZipStream WrapWrite(Stream destination) =>
        new(destination, CompressionLevel.Optimal, leaveOpen: true);

    internal static byte[] Compress(byte[] content)
    {
        using var buffer = new MemoryStream();
        using (var gzip = WrapWrite(buffer))
        {
            gzip.Write(content, 0, content.Length);
        }
        return buffer.ToArray();
    }
}

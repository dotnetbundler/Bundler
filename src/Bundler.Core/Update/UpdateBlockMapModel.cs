using System.Runtime.Serialization;
using System.Security.Cryptography;

#if BUNDLER_UPDATER_LINK
namespace DotNet.Bundler.Updater.Protocol;
#else
namespace DotNet.Bundler.Core.Update;
#endif

/// <summary>
/// `.blockmap` 差分块表模型（electron-updater 同型精简版）：定宽块 + sha256 序列表，
/// 客户端按哈希匹配（不依赖位置）复用旧件边界对齐的未变块，缺失块走 Range 拉取。
/// 打包侧写、应用侧读的开放协议——本文件无打包链依赖，可被应用内更新库直接链接编译。
/// </summary>
[DataContract]
public sealed class UpdateBlockMap
{
    public const int DefaultBlockSize = 64 * 1024;
    public const string FileSuffix = ".blockmap";

    [DataMember(Name = "version")] public int Version = 1;
    [DataMember(Name = "blockSize")] public int BlockSize = DefaultBlockSize;
    [DataMember(Name = "fileSize")] public long FileSize;
    [DataMember(Name = "hashes")] public List<string> Hashes = [];

    /// <summary>对数据流按 blockSize 切块逐块 sha256，产出块表（流被读完，不重定位）。</summary>
    public static UpdateBlockMap Compute(Stream stream, int blockSize = DefaultBlockSize)
    {
        var map = new UpdateBlockMap { BlockSize = blockSize };
        var buffer = new byte[blockSize];
        using var sha = SHA256.Create();
        while (true)
        {
            var read = ReadFull(stream, buffer);
            if (read == 0)
            {
                break;
            }
            map.Hashes.Add(Convert.ToBase64String(sha.ComputeHash(buffer, 0, read)));
            map.FileSize += read;
        }
        return map;
    }

    public static UpdateBlockMap ComputeFile(string path, int blockSize = DefaultBlockSize)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Compute(stream, blockSize);
    }

    private static int ReadFull(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }
}

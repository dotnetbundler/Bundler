using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Bundler.TestShared;

/// <summary>
/// 测试侧最小 ustar 读器——只解普通文件/目录名与内容（含 GNU 长名 'L' 与 pax 'x'），
/// 供 Api 层在不用 internal 读器的前提下断言包内成员。
/// </summary>
public static class MiniTar
{
    public sealed record Entry(string Name, byte[] Data, int Mode, char Type);

    public static IReadOnlyList<Entry> Read(byte[] blob)
    {
        var entries = new List<Entry>();
        var offset = 0;
        string? pendingName = null;
        while (offset + 512 <= blob.Length)
        {
            var header = blob.AsSpan(offset, 512);
            if (header[..512].IndexOfAnyExcept((byte)0) < 0)
            {
                break; // 全零块 = 归档尾
            }
            var name = Encoding.UTF8.GetString(header[..100]).TrimEnd('\0');
            var mode = Convert.ToInt32(Encoding.ASCII.GetString(header[100..108]).Trim('\0', ' '), 8);
            var size = (int)Convert.ToInt64(Encoding.ASCII.GetString(header[124..136]).Trim('\0', ' '), 8);
            var type = (char)header[156];
            var data = blob.AsSpan(offset + 512, size).ToArray();
            offset += 512 + ((size + 511) / 512) * 512;
            if (type is 'L' or 'x')
            {
                // GNU 长名 / pax 扩展——本读器只取 path 键。
                pendingName = type == 'L'
                    ? Encoding.UTF8.GetString(data).TrimEnd('\0')
                    : ParsePaxPath(data) ?? name;
                continue;
            }
            entries.Add(new Entry(pendingName ?? name, data, mode, type));
            pendingName = null;
        }
        return entries;
    }

    public static IReadOnlyList<Entry> ReadGzip(string path)
    {
        using var stream = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Read(memory.ToArray());
    }

    private static string? ParsePaxPath(byte[] data)
    {
        // pax 记录："<len> <key>=<value>\n"——只取 path。
        var text = Encoding.UTF8.GetString(data);
        foreach (Match match in Regex.Matches(text, @"\d+ path=([^\n]+)"))
        {
            return match.Groups[1].Value;
        }
        return null;
    }
}
